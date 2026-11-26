#requires -Version 5.1
<#
Merge source updates in an isolated clone, validate the whole solution, then
apply only changed files. The user's HEAD, index, remotes and branches stay put.
All Git writes, including synthetic merge commits, occur in the isolated clone.
#>
[CmdletBinding()]
param(
    [switch]$CheckOnly,
    [switch]$RestoreLatest,
    [string]$RepositoryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:Utf8 = New-Object System.Text.UTF8Encoding($false)
$script:LogPath = $null
$script:HookDirectory = $null

function Write-Message([string]$Message) {
    Write-Host $Message
    if ($script:LogPath) { [IO.File]::AppendAllText($script:LogPath, $Message + [Environment]::NewLine, $script:Utf8) }
}

function Get-SafePath([string]$Root, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or
        $Relative -match '(^|[\\/])\.\.?([\\/]|$)|(^|[\\/])\.git([\\/]|$)|[:\x00-\x1f]') { throw "Invalid relative path: $Relative" }
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $path = [IO.Path]::GetFullPath((Join-Path $rootPath $Relative))
    if (-not $path.StartsWith($rootPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escapes its root: $Relative"
    }
    $cursor = $path
    while ($cursor -and $cursor.Length -gt $rootPath.Length) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing to follow a link: $cursor"
            }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    return $path
}

function Get-Fingerprint([string]$Path) {
    if ([IO.Directory]::Exists($Path)) { return 'directory' }
    if (-not [IO.File]::Exists($Path)) { return 'absent' }
    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $sha.Dispose() }
}

function Write-Json([string]$Path, $Value) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, (($Value | ConvertTo-Json -Depth 20) + [Environment]::NewLine), $script:Utf8)
}

function ConvertTo-NativeArgument([string]$Value) {
    # Windows CommandLineToArgvW quoting, including trailing backslashes.
    return '"' + [regex]::Replace([regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
}

function Invoke-Native {
    param([string]$Program, [string[]]$Arguments, [string]$WorkingDirectory,
          [hashtable]$Environment = @{}, [int]$TimeoutSeconds = 180, [switch]$AllowFailure)
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $Program
    $info.Arguments = (($Arguments | ForEach-Object { ConvertTo-NativeArgument $_ }) -join ' ')
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = $script:Utf8
    $info.StandardErrorEncoding = $script:Utf8
    foreach ($entry in $Environment.GetEnumerator()) {
        if ($null -eq $entry.Value) { $info.EnvironmentVariables.Remove($entry.Key) }
        else { $info.EnvironmentVariables[$entry.Key] = [string]$entry.Value }
    }
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $info
    try {
        $process.Start() | Out-Null
        $outTask = $process.StandardOutput.ReadToEndAsync()
        $errTask = $process.StandardError.ReadToEndAsync()
        $timer = [Diagnostics.Stopwatch]::StartNew()
        while (-not $process.WaitForExit(1000)) {
            if ($timer.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
                $process.Kill()
                throw "$Program timed out after $TimeoutSeconds seconds."
            }
            if ([int]$timer.Elapsed.TotalSeconds % 30 -eq 0) { Write-Message "  Still running: $Program ($([int]$timer.Elapsed.TotalSeconds)s)" }
        }
        $output = $outTask.GetAwaiter().GetResult()
        $errorText = $errTask.GetAwaiter().GetResult()
        if ($script:LogPath) { [IO.File]::AppendAllText($script:LogPath, $output + $errorText, $script:Utf8) }
        if ($process.ExitCode -ne 0 -and -not $AllowFailure) { throw "$Program failed ($($process.ExitCode)): $output$errorText" }
        return [pscustomobject]@{ Code = $process.ExitCode; Output = $output; Error = $errorText }
    } finally { $process.Dispose() }
}

function Invoke-Git {
    param([string]$Repo, [string[]]$Arguments, [hashtable]$Environment = @{}, [switch]$AllowFailure)
    $envValues = @{ GIT_TERMINAL_PROMPT = '0'; GCM_INTERACTIVE = 'Never'; GIT_OPTIONAL_LOCKS = '0' }
    # Do not let an inherited index/work-tree redirect a command into the user's checkout.
    foreach ($key in @('GIT_INDEX_FILE', 'GIT_DIR', 'GIT_WORK_TREE', 'GIT_COMMON_DIR', 'GIT_OBJECT_DIRECTORY', 'GIT_ALTERNATE_OBJECT_DIRECTORIES')) {
        $envValues[$key] = $null
    }
    foreach ($entry in $Environment.GetEnumerator()) { $envValues[$entry.Key] = $entry.Value }
    $options = @('-c', 'core.longpaths=true')
    if ($script:HookDirectory) { $options += @('-c', "core.hooksPath=$script:HookDirectory") }
    return Invoke-Native -Program 'git.exe' -Arguments ($options + @('-C', $Repo) + $Arguments) -WorkingDirectory $Repo -Environment $envValues -AllowFailure:$AllowFailure
}

function Get-GitText([string]$Repo, [string[]]$Arguments) {
    return (Invoke-Git $Repo $Arguments).Output.Trim()
}

function New-ScratchIndex([string]$RunDirectory) {
    return Join-Path $RunDirectory ('index-' + [guid]::NewGuid().ToString('N'))
}

function Get-FilteredTree([string]$Repo, [string]$Tree, [string[]]$Excluded, [string]$RunDirectory) {
    $indexEnv = @{ GIT_INDEX_FILE = (New-ScratchIndex $RunDirectory) }
    Invoke-Git $Repo @('read-tree', $Tree) $indexEnv | Out-Null
    if ($Excluded.Count -gt 0) {
        Invoke-Git $Repo (@('rm', '-r', '-f', '--cached', '--ignore-unmatch', '--') + $Excluded) $indexEnv | Out-Null
    }
    return (Invoke-Git $Repo @('write-tree') $indexEnv).Output.Trim()
}

function Merge-SourceTrees([string]$Repo, [string]$BaseTree, [string]$LocalTree, [string]$NewTree) {
    # Common synthetic parent makes the merge base explicit without relying on
    # unrelated vendor histories or heuristic patch application.
    $base = Get-GitText $Repo @('commit-tree', $BaseTree, '-m', 'Plugin sync: source baseline')
    $local = Get-GitText $Repo @('commit-tree', $LocalTree, '-p', $base, '-m', 'Plugin sync: local changes')
    $remote = Get-GitText $Repo @('commit-tree', $NewTree, '-p', $base, '-m', 'Plugin sync: upstream changes')
    $merge = Invoke-Git $Repo @('merge-tree', '--write-tree', '--name-only', $local, $remote) -AllowFailure
    if ($merge.Code -gt 1) { throw "Git could not merge source trees: $($merge.Error)" }
    if ($merge.Code -eq 1) { throw "Merge conflict; keeping the local version.`n$($merge.Output)" }
    return ($merge.Output -split '\r?\n')[0].Trim()
}

function Set-CandidateTree([string]$Repo, [string]$Tree) {
    $parent = Get-GitText $Repo @('rev-parse', 'HEAD')
    $commit = Get-GitText $Repo @('commit-tree', $Tree, '-p', $parent, '-m', 'Plugin sync: candidate update')
    Invoke-Git $Repo @('read-tree', '--reset', '-u', $Tree) | Out-Null
    Invoke-Git $Repo @('update-ref', 'HEAD', $commit) | Out-Null
}

function Set-PluginTree([string]$Repo, [string]$Path, [string]$Tree, [string]$RunDirectory) {
    $indexEnv = @{ GIT_INDEX_FILE = (New-ScratchIndex $RunDirectory) }
    Invoke-Git $Repo @('read-tree', 'HEAD') $indexEnv | Out-Null
    Invoke-Git $Repo @('rm', '-r', '-f', '--cached', '--ignore-unmatch', '--', $Path) $indexEnv | Out-Null
    Invoke-Git $Repo @('read-tree', "--prefix=$Path/", $Tree) $indexEnv | Out-Null
    $fullTree = (Invoke-Git $Repo @('write-tree') $indexEnv).Output.Trim()
    Set-CandidateTree $Repo $fullTree
}

function Copy-FileAtomically([string]$Source, [string]$Destination) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Destination)) | Out-Null
    $temporary = $Destination + '.plugin-sync-' + [guid]::NewGuid().ToString('N')
    try {
        [IO.File]::Copy($Source, $temporary, $false)
        if ([IO.File]::Exists($Destination)) { [IO.File]::Replace($temporary, $Destination, [NullString]::Value) }
        else { [IO.File]::Move($temporary, $Destination) }
    } finally {
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}

function Restore-Entries([string]$Root, [string]$BackupDirectory, $Entries) {
    foreach ($entry in $Entries) {
        $target = Get-SafePath $Root $entry.path
        if ($entry.before -ne 'absent') {
            Copy-FileAtomically (Get-SafePath (Join-Path $BackupDirectory 'files') $entry.path) $target
        } elseif ([IO.File]::Exists($target)) { [IO.File]::Delete($target) }
    }
}

function Invoke-Restore([string]$Root, [string]$StateDirectory) {
    $pointerPath = Join-Path $StateDirectory 'latest-backup.json'
    if (-not [IO.File]::Exists($pointerPath)) { throw 'No successful update backup was found.' }
    $pointer = Get-Content -LiteralPath $pointerPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $backup = Get-SafePath $StateDirectory $pointer.path
    $record = Get-Content -LiteralPath (Join-Path $backup 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($record.root -ne $Root) { throw 'This backup belongs to another checkout.' }
    foreach ($entry in $record.files) {
        if ((Get-Fingerprint (Get-SafePath $Root $entry.path)) -ne $entry.after) {
            throw "Changed since the update; refusing to overwrite: $($entry.path)"
        }
        if ($entry.before -ne 'absent' -and
            (Get-Fingerprint (Get-SafePath (Join-Path $backup 'files') $entry.path)) -ne $entry.before) {
            throw "Backup missing or damaged: $($entry.path)"
        }
    }
    Restore-Entries $Root $backup $record.files
    [IO.File]::Delete($pointerPath)
    Write-Message "Restored $(@($record.files).Count) files. Your Git HEAD and index were not changed."
}

function Invoke-Sync([string]$Root, [string]$StateDirectory) {
    $manifestPath = Get-SafePath $Root 'plugin-sync.json'
    $config = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($config.version -ne 1) { throw 'Unsupported plugin-sync.json version.' }
    $gitVersion = Get-GitText $Root @('--version')
    if ($gitVersion -notmatch '(\d+)\.(\d+)' -or [version]($Matches[1] + '.' + $Matches[2]) -lt [version]'2.38') {
        throw 'Git 2.38 or newer is required.'
    }
    if (-not $CheckOnly) { Invoke-Native 'dotnet.exe' @('--version') $Root | Out-Null }
    $head = Get-GitText $Root @('rev-parse', 'HEAD')
    $unmerged = Get-GitText $Root @('ls-files', '-u')
    if ($unmerged) { throw 'Resolve the existing Git conflicts before updating.' }
    foreach ($operation in @('MERGE_HEAD', 'CHERRY_PICK_HEAD', 'REVERT_HEAD', 'rebase-apply', 'rebase-merge')) {
        $operationPath = Get-GitText $Root @('rev-parse', '--git-path', $operation)
        if (-not [IO.Path]::IsPathRooted($operationPath)) { $operationPath = Join-Path $Root $operationPath }
        if (Test-Path -LiteralPath $operationPath) { throw 'Finish the existing Git merge/rebase operation before updating.' }
    }
    $runName = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $run = Get-SafePath $StateDirectory ('runs/' + $runName)
    [IO.Directory]::CreateDirectory($run) | Out-Null
    $script:LogPath = Join-Path $run 'sync.log'
    $script:HookDirectory = Join-Path $run 'empty-hooks'
    [IO.Directory]::CreateDirectory($script:HookDirectory) | Out-Null
    $candidate = Join-Path $run 'workspace'
    $report = [ordered]@{ started = [DateTime]::UtcNow.ToString('o'); checkOnly = [bool]$CheckOnly; status = 'preparing'; sources = @(); files = @() }
    $reportPath = Join-Path $run 'report.json'
    Write-Message "Preparing an isolated source snapshot. Report: $reportPath"
    try {
        Invoke-Native 'git.exe' @('-c', "core.hooksPath=$script:HookDirectory", 'clone', '--local', '--no-hardlinks', '--no-checkout', '--', $Root, $candidate) $run | Out-Null
        Invoke-Git $candidate @('config', 'user.name', 'Plugin Sync') | Out-Null
        Invoke-Git $candidate @('config', 'user.email', 'plugin-sync@localhost') | Out-Null
        foreach ($setting in @('core.autocrlf', 'core.eol')) {
            $value = Invoke-Git $Root @('config', '--get', $setting) -AllowFailure
            if ($value.Code -eq 0) { Invoke-Git $candidate @('config', $setting, $value.Output.Trim()) | Out-Null }
        }
        Invoke-Git $candidate @('checkout', '--detach', $head) | Out-Null
        $fingerprints = @{}
        $trackedLinks = @{}
        $trackedSubmodules = @{}
        foreach ($item in (Invoke-Git $Root @('ls-files', '--stage', '-z')).Output.Split([char]0)) {
            if ($item -match '^120000 [0-9a-f]+ \d\t(.+)$') { $trackedLinks[$Matches[1]] = $true }
            if ($item -match '^160000 [0-9a-f]+ \d\t(.+)$') { $trackedSubmodules[$Matches[1]] = $true }
        }
        foreach ($plugin in $config.plugins) {
            $localProject = Get-SafePath $Root ($plugin.path + '/' + $plugin.name + '.csproj')
            $candidateEntry = Get-GitText $candidate @('ls-files', '--stage', '--', $plugin.path)
            if (-not $trackedSubmodules.ContainsKey($plugin.path) -and [IO.File]::Exists($localProject) -and
                $candidateEntry -match '^160000 ') {
                # Respect a submodule-to-source conversion already staged by the user.
                Invoke-Git $candidate @('rm', '-f', '--cached', '--', $plugin.path) | Out-Null
            }
        }
        $sourceFiles = (Invoke-Git $Root @('ls-files', '--cached', '--others', '--exclude-standard', '--exclude=.plugin-sync/', '-z')).Output.Split([char]0)
        foreach ($relative in ($sourceFiles | Where-Object { $_ } | Sort-Object -Unique)) {
            if ($relative -match '^\.plugin-sync(/|$)') { continue }
            if ($trackedLinks.ContainsKey($relative)) { continue }
            $original = Get-SafePath $Root $relative
            $destination = Get-SafePath $candidate $relative
            $fingerprints[$relative] = Get-Fingerprint $original
            if ([IO.File]::Exists($original)) {
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
                [IO.File]::Copy($original, $destination, $true)
            } elseif (-not [IO.Directory]::Exists($original) -and [IO.File]::Exists($destination)) {
                [IO.File]::Delete($destination)
            }
        }
        Invoke-Git $candidate @('add', '-A', '--', '.') | Out-Null
        $snapshotTree = Get-GitText $candidate @('write-tree')
        $snapshot = Get-GitText $candidate @('commit-tree', $snapshotTree, '-p', $head, '-m', 'Plugin sync: working source snapshot')
        Invoke-Git $candidate @('update-ref', 'HEAD', $snapshot) | Out-Null
        $config = Get-Content -LiteralPath (Join-Path $candidate 'plugin-sync.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $sourceChanged = $false
        $partial = $false
        $protected = @('config', 'configs', 'bin', 'obj', '.gitmodules', '.github', ':(glob)**/bin/**', ':(glob)**/obj/**', ':(glob)**/config/**', ':(glob)**/configs/**')
        $coreExcluded = $protected + @('plugin-sync.json', 'PLUGIN_SOURCES.md', 'scripts/Sync-Plugins.ps1', 'scripts/Test-PluginSync.ps1', 'update-plugins.bat', 'restore-plugins.bat', 'pull-external-plugins.bat', 'Plugins/CraftingHelper')
        foreach ($plugin in $config.plugins) {
            Get-SafePath $Root $plugin.path | Out-Null
            if ($plugin.path -notmatch '^Plugins/[A-Za-z0-9_-]+$') { throw "Invalid plugin path: $($plugin.path)" }
            $coreExcluded += $plugin.path
        }
        $sources = @($config.core) + @($config.plugins)
        foreach ($source in $sources) {
            $isCore = $source -eq $config.core
            $candidateBeforeSource = Get-GitText $candidate @('rev-parse', 'HEAD')
            $row = [ordered]@{ name = $source.name; status = 'checking'; before = $source.commit; latest = $null; message = '' }
            Write-Message "Checking $($source.name)..."
            try {
                if ($source.branch -match '^-' -or $source.branch -match '[\s\x00-\x1f]' -or
                    $source.repository -match '^-' -or [string]::IsNullOrWhiteSpace($source.repository)) { throw 'Invalid source repository or branch.' }
                Invoke-Git $candidate @('fetch', '--no-tags', '--no-recurse-submodules', '--', $source.repository, ('refs/heads/' + $source.branch)) | Out-Null
                $latest = Get-GitText $candidate @('rev-parse', 'FETCH_HEAD')
                $row.latest = $latest
                $baseline = $source.commit
                if (-not $baseline -and $isCore) { $baseline = Get-GitText $candidate @('merge-base', $head, $latest) }
                if ($baseline -notmatch '^[0-9a-f]{40}$') { throw 'A verified source baseline is required; no changes applied.' }
                if ((Invoke-Git $candidate @('cat-file', '-e', ($baseline + '^{commit}')) -AllowFailure).Code -ne 0) {
                    Invoke-Git $candidate @('fetch', '--no-tags', '--', $source.repository, $baseline) | Out-Null
                }
                if ((Invoke-Git $candidate @('merge-base', '--is-ancestor', $baseline, $latest) -AllowFailure).Code -ne 0) {
                    throw 'Upstream history was rewritten or the baseline is unrelated. Keeping the local version.'
                }
                if (-not $isCore) {
                    $pluginDirectory = Get-SafePath $candidate $source.path
                    if (-not [IO.File]::Exists((Join-Path $pluginDirectory ($source.name + '.csproj')))) {
                        $row.status = 'skipped'; $row.message = 'No complete local plugin project. Upstream is still monitored; baseline unchanged.'
                        $partial = $true
                        Write-Message "  SKIPPED: $($row.message)"
                        $report.sources += [pscustomobject]$row
                        continue
                    }
                }
                if ($baseline -eq $latest) {
                    $source.commit = $latest
                    $row.status = 'current'; $row.message = 'No new upstream commits.'
                } else {
                    $excluded = $protected
                    if ($isCore) {
                        $excluded = $coreExcluded
                        $localTree = Get-GitText $candidate @('rev-parse', 'HEAD^{tree}')
                    } else {
                        $localTree = Get-GitText $candidate @('rev-parse', ('HEAD:' + $source.path))
                    }
                    $baseTree = Get-FilteredTree $candidate ($baseline + '^{tree}') $excluded $run
                    $newTree = Get-FilteredTree $candidate ($latest + '^{tree}') $excluded $run
                    $mergedTree = Merge-SourceTrees $candidate $baseTree $localTree $newTree
                    if ($mergedTree -ne $localTree) {
                        if ($isCore) { Set-CandidateTree $candidate $mergedTree }
                        else { Set-PluginTree $candidate $source.path $mergedTree $run }
                        $sourceChanged = $true
                    }
                    $source.commit = $latest
                    $row.status = 'ready'; $row.message = 'Merged into the isolated candidate.'
                }
                Write-Message "  $($row.status.ToUpper()): $($row.message)"
            } catch {
                if (-not $isCore) {
                    Invoke-Git $candidate @('read-tree', '--reset', '-u', ($candidateBeforeSource + '^{tree}')) | Out-Null
                    Invoke-Git $candidate @('update-ref', 'HEAD', $candidateBeforeSource) | Out-Null
                    $source.commit = $row.before
                }
                $row.status = 'failed'; $row.message = $_.Exception.Message
                $partial = $true
                Write-Message "  FAILED: $($row.message)"
                if ($isCore) { $report.sources += [pscustomobject]$row; throw 'Core update failed. Current sources were not changed.' }
            }
            $report.sources += [pscustomobject]$row
        }
        # The machine-readable baseline is separate from the short public credits.
        Write-Json (Join-Path $candidate 'plugin-sync.json') $config
        Invoke-Git $candidate @('add', '--', 'plugin-sync.json') | Out-Null
        $finalTree = Get-GitText $candidate @('write-tree')
        Set-CandidateTree $candidate $finalTree
        $changes = (Invoke-Git $candidate @('diff', '--name-only', '--no-renames', '-z', $snapshot, 'HEAD', '--')).Output.Split([char]0) | Where-Object { $_ }
        $report.files = @($changes)
        # New symlinks/submodules must never be copied as regular source files.
        foreach ($relative in $changes) {
            $entry = Get-GitText $candidate @('ls-tree', 'HEAD', '--', $relative)
            if ($entry -match '^(120000|160000) ') { throw "Unsupported updated link: $relative" }
        }
        if ($CheckOnly) {
            $report.status = 'checked'
            Write-Message "Check complete: $(@($changes).Count) candidate file changes. Nothing applied."
        } elseif (@($changes).Count -eq 0) {
            $report.status = 'current'
            Write-Message 'All available sources are current. Nothing applied.'
        } else {
            if ($sourceChanged) {
                Write-Message 'Building the complete solution in the isolated workspace...'
                $build = Invoke-Native 'dotnet.exe' @('build', 'GameOverlay.sln', '-c', 'Release', '--nologo') $candidate -TimeoutSeconds 1200 -AllowFailure
                if ($build.Code -ne 0) { throw "Solution build failed. Current sources were not changed. See $script:LogPath" }
            }
            # A snapshot may include existing local edits. Detect concurrent changes
            # before applying any file, and leave the original index untouched.
            if ((Get-GitText $Root @('rev-parse', 'HEAD')) -ne $head) { throw 'Your Git HEAD changed during the update. Nothing applied.' }
            $backup = Join-Path $run 'backup'
            $entries = @()
            foreach ($relative in $changes) {
                $target = Get-SafePath $Root $relative
                $before = 'absent'
                if ($fingerprints.ContainsKey($relative)) { $before = $fingerprints[$relative] }
                if ((Get-Fingerprint $target) -ne $before) { throw "File changed during the update: $relative. Nothing applied." }
                $after = Get-Fingerprint (Get-SafePath $candidate $relative)
                if ($before -eq 'directory' -or $after -eq 'directory') { throw "Unsupported directory replacement: $relative" }
                if ($before -ne 'absent') {
                    $saved = Get-SafePath (Join-Path $backup 'files') $relative
                    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($saved)) | Out-Null
                    [IO.File]::Copy($target, $saved, $false)
                }
                $entries += [pscustomobject]@{ path = $relative; before = $before; after = $after }
            }
            Write-Json (Join-Path $backup 'manifest.json') ([ordered]@{ root = $Root; files = $entries })
            $applied = New-Object 'System.Collections.Generic.List[object]'
            try {
                foreach ($entry in $entries) {
                    $target = Get-SafePath $Root $entry.path
                    if ((Get-Fingerprint $target) -ne $entry.before) { throw "File changed while applying: $($entry.path)" }
                    $applied.Add($entry)
                    if ($entry.after -eq 'absent') { [IO.File]::Delete($target) }
                    else { Copy-FileAtomically (Get-SafePath $candidate $entry.path) $target }
                }
            } catch {
                Restore-Entries $Root $backup $applied.ToArray()
                throw "Apply failed; restored the original files. $($_.Exception.Message)"
            }
            Write-Json (Join-Path $StateDirectory 'latest-backup.json') @{ path = ('runs/' + $runName + '/backup') }
            $report.status = 'applied'
            Write-Message "Applied $($entries.Count) files. Backup: $backup"
            Write-Message 'Source changes remain uncommitted. Rebuild your working solution to refresh runtime DLLs.'
        }
        if ($partial) { return 2 }
        return 0
    } catch {
        $report.status = 'failed'; $report['error'] = $_.Exception.Message
        throw
    } finally {
        $report['finished'] = [DateTime]::UtcNow.ToString('o')
        Write-Json $reportPath $report
        Write-Message "Report saved: $reportPath"
    }
}

$exitCode = 1
$lock = $null
try {
    if ($CheckOnly -and $RestoreLatest) { throw 'Choose either -CheckOnly or -RestoreLatest.' }
    if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = Split-Path -Parent $PSScriptRoot }
    $root = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/')
    $actualRoot = Get-GitText $root @('rev-parse', '--show-toplevel')
    if ([IO.Path]::GetFullPath($actualRoot).TrimEnd('\', '/') -ne $root) { throw 'RepositoryRoot must be the Git checkout root.' }
    $stateDirectory = Get-SafePath $root '.plugin-sync'
    [IO.Directory]::CreateDirectory($stateDirectory) | Out-Null
    try { $lock = [IO.File]::Open((Join-Path $stateDirectory 'sync.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
    catch { throw 'Another plugin update or restore is already running.' }
    if ($RestoreLatest) { Invoke-Restore $root $stateDirectory; $exitCode = 0 }
    else { $exitCode = Invoke-Sync $root $stateDirectory }
} catch {
    Write-Message ('FAILED: ' + $_.Exception.Message)
} finally {
    if ($null -ne $lock) { $lock.Dispose() }
}
exit $exitCode
