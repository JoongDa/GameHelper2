param([Parameter(Mandatory = $true)][string]$RequestPath)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$requestFolder = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($RequestPath))
$logPath = Join-Path $requestFolder 'apply.log'
$installLock = $null
$restartHost = $false
$journalPath = $null
$applyStarted = $false

function Assert-Name([string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Name) -or $Name -in @('.', '..') -or
        $Name.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
        $Name.EndsWith('.') -or $Name.EndsWith(' ')) {
        throw 'Invalid plugin or repository name.'
    }
}

function Assert-Child([string]$Path, [string]$Root) {
    $fullRoot = [System.IO.Path]::GetFullPath($Root).TrimEnd([char[]]'\/')
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($fullRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Plugin operation points outside its working directory.'
    }
    $cursor = $fullPath
    while ($cursor -and $cursor.Length -ge $fullRoot.Length) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Plugin operation paths cannot contain filesystem links.'
            }
        }
        $cursor = [System.IO.Path]::GetDirectoryName($cursor)
    }
}

function Assert-Tree([string]$Path) {
    foreach ($entry in Get-ChildItem -LiteralPath $Path -Recurse -Force) {
        if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Plugin folders cannot contain filesystem links.'
        }
    }
}

function Write-Json([string]$Path, $Value) {
    $temporary = $Path + '.tmp'
    [System.IO.File]::WriteAllText($temporary, (ConvertTo-Json -InputObject $Value -Depth 30), [System.Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $Path) {
        $previous = $Path + '.previous'
        [System.IO.File]::Replace($temporary, $Path, $previous, $true)
        [System.IO.File]::Delete($previous)
    } else {
        [System.IO.File]::Move($temporary, $Path)
    }
}

function Read-Installed([string]$Path) {
    $installed = @{}
    if (Test-Path -LiteralPath $Path) {
        $saved = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($property in $saved.PSObject.Properties) {
            $installed[$property.Name] = $property.Value
        }
    }
    return $installed
}

function Restore-Journal($Journal, [string]$PluginsRoot, [string]$ManagerRoot, [string]$InstalledPath) {
    $entries = @($Journal.Entries)
    [array]::Reverse($entries)
    foreach ($entry in $entries) {
        if ($entry.TargetPath) {
            Assert-Child $entry.TargetPath $PluginsRoot
            Assert-Child $entry.BackupPath (Join-Path $ManagerRoot 'Backups')
            if (Test-Path -LiteralPath $entry.BackupPath) {
                if (Test-Path -LiteralPath $entry.TargetPath) {
                    Assert-Child $entry.OutputDirectory (Join-Path $ManagerRoot 'Staging')
                    if (Test-Path -LiteralPath $entry.OutputDirectory) {
                        throw 'Cannot restore the previous plugin because its staging directory is occupied.'
                    }
                    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($entry.OutputDirectory)) | Out-Null
                    Move-Item -LiteralPath $entry.TargetPath -Destination $entry.OutputDirectory
                }
                Move-Item -LiteralPath $entry.BackupPath -Destination $entry.TargetPath
            } elseif (-not $entry.HadOriginal -and -not $entry.Delete -and
                (Test-Path -LiteralPath $entry.TargetPath) -and -not (Test-Path -LiteralPath $entry.OutputDirectory)) {
                Assert-Child $entry.OutputDirectory (Join-Path $ManagerRoot 'Staging')
                Move-Item -LiteralPath $entry.TargetPath -Destination $entry.OutputDirectory
            }
        }
        if ($entry.SourceBackup -and (Test-Path -LiteralPath $entry.SourceBackup)) {
            Assert-Child $entry.SourcePath (Join-Path $ManagerRoot 'Source')
            Assert-Child $entry.SourceBackup (Join-Path $ManagerRoot 'Backups')
            if (Test-Path -LiteralPath $entry.SourcePath) {
                throw 'Cannot restore the repository because its directory is occupied.'
            }
            Move-Item -LiteralPath $entry.SourceBackup -Destination $entry.SourcePath
        }
    }
    Write-Json $InstalledPath $Journal.PreviousInstalled
}

try {
    $request = Get-Content -LiteralPath $RequestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $hostRoot = [System.IO.Path]::GetFullPath($request.HostDirectory)
    $managerRoot = [System.IO.Path]::GetFullPath($request.ManagerDirectory)
    $pluginsRoot = Join-Path $hostRoot 'Plugins'
    $stateRoot = Join-Path $managerRoot 'config'
    Assert-Child $managerRoot $pluginsRoot
    Assert-Child $RequestPath (Join-Path $stateRoot 'requests')
    if (-not [string]::Equals($managerRoot.TrimEnd([char[]]'\/'), (Join-Path $pluginsRoot 'PluginUpdater'), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Unexpected plugin manager directory.'
    }
    $launcher = Join-Path $hostRoot 'Launcher.exe'
    if (-not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
        throw 'Launcher.exe is missing.'
    }
    $installedPath = Join-Path $stateRoot 'installed.json'
    $pendingPath = Join-Path $stateRoot 'pending.json'
    $journalPath = Join-Path $stateRoot 'apply-journal.json'
    $installLock = [System.IO.File]::Open((Join-Path $stateRoot 'apply.lock'), [System.IO.FileMode]::OpenOrCreate,
        [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    if (-not (Test-Path -LiteralPath $pendingPath) -or
        (Get-Content -LiteralPath $pendingPath -Raw -Encoding UTF8) -cne $request.PendingState) {
        throw 'Pending plugin changes changed before installation. Retry the restart.'
    }
    $parent = Get-Process -Id $request.ParentId -ErrorAction SilentlyContinue
    if ($parent -and $parent.StartTime.ToUniversalTime().Ticks.ToString() -ne $request.ParentStartTimeUtcTicks) {
        throw 'The original GameHelper process has changed.'
    }
    [System.IO.File]::WriteAllText((Join-Path $requestFolder 'ready'), 'ready')
    if ($parent -and -not $parent.WaitForExit(60000)) {
        throw 'GameHelper did not exit within 60 seconds.'
    }
    $restartHost = $true
    $applyStarted = $true

    if (Test-Path -LiteralPath $journalPath) {
        $oldJournal = Get-Content -LiteralPath $journalPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($oldJournal.Status -eq 'Applying') {
            Restore-Journal $oldJournal $pluginsRoot $managerRoot $installedPath
            $oldJournal.Status = 'Recovered'
            Write-Json $journalPath $oldJournal
        }
    }

    $installed = Read-Installed $installedPath
    $journal = [pscustomobject]@{
        Status = 'Applying'
        PreviousInstalled = $installed.Clone()
        Entries = @()
    }
    Write-Json $journalPath $journal
    $batchName = Split-Path -Path $requestFolder -Leaf
    $backupRoot = Join-Path (Join-Path $managerRoot 'Backups') $batchName
    Assert-Child $backupRoot (Join-Path $managerRoot 'Backups')
    [System.IO.Directory]::CreateDirectory($backupRoot) | Out-Null

    foreach ($change in @($request.Changes)) {
        Assert-Name $change.RepositoryName
        $target = ''
        $backup = ''
        $hadOriginal = $false
        if ($change.PluginName) {
            Assert-Name $change.PluginName
            if ($change.PluginName -ieq 'PluginUpdater') {
                throw 'The plugin manager cannot replace or remove itself.'
            }
            $target = Join-Path $pluginsRoot $change.PluginName
            $backup = Join-Path (Join-Path $backupRoot 'compiled') $change.PluginName
            Assert-Child $target $pluginsRoot
            Assert-Child $backup (Join-Path $managerRoot 'Backups')
            $hadOriginal = Test-Path -LiteralPath $target
            if ($hadOriginal) { Assert-Tree $target }
        }
        $sourcePath = Join-Path (Join-Path $managerRoot 'Source') $change.RepositoryName
        $sourceBackup = Join-Path (Join-Path $backupRoot 'source') $change.RepositoryName
        Assert-Child $sourcePath (Join-Path $managerRoot 'Source')
        Assert-Child $sourceBackup (Join-Path $managerRoot 'Backups')
        # A previous helper can finish the swap and commit its registry just before
        # being interrupted while clearing pending.json. Replaying that batch is safe.
        if (-not $change.Delete -and $installed.ContainsKey($change.RepositoryName) -and
            $installed[$change.RepositoryName].Commit -eq $change.Commit -and
            $installed[$change.RepositoryName].PluginName -eq $change.PluginName -and
            -not (Test-Path -LiteralPath $change.OutputDirectory) -and
            (Test-Path -LiteralPath (Join-Path $target ($change.PluginName + '.dll')) -PathType Leaf)) {
            continue
        }
        if ($change.Delete -and -not $installed.ContainsKey($change.RepositoryName) -and
            -not (Test-Path -LiteralPath $sourcePath) -and -not $hadOriginal) {
            continue
        }
        $entry = [pscustomobject]@{
            TargetPath = $target
            BackupPath = $backup
            OutputDirectory = $change.OutputDirectory
            HadOriginal = $hadOriginal
            Delete = $change.Delete
            SourcePath = $sourcePath
            SourceBackup = ''
        }
        if ($change.Delete) {
            if (Test-Path -LiteralPath $sourcePath) {
                Assert-Tree $sourcePath
                $entry.SourceBackup = $sourceBackup
            }
        } else {
            Assert-Child $change.OutputDirectory (Join-Path $managerRoot 'Staging')
            Assert-Tree $change.OutputDirectory
            if (-not (Test-Path -LiteralPath (Join-Path $change.OutputDirectory ($change.PluginName + '.dll')) -PathType Leaf)) {
                throw 'The staged plugin assembly is missing.'
            }
            foreach ($config in @('config', 'configs')) {
                $sourceConfig = Join-Path $target $config
                $stagedConfig = Join-Path $change.OutputDirectory $config
                Assert-Child $stagedConfig (Join-Path $managerRoot 'Staging')
                if (Test-Path -LiteralPath $stagedConfig) {
                    Remove-Item -LiteralPath $stagedConfig -Recurse -Force
                }
                if (Test-Path -LiteralPath $sourceConfig) {
                    Copy-Item -LiteralPath $sourceConfig -Destination $stagedConfig -Recurse -Force
                }
            }
        }
        $journal.Entries += $entry
        Write-Json $journalPath $journal
        if ($hadOriginal) {
            [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($backup)) | Out-Null
            Move-Item -LiteralPath $target -Destination $backup
        }
        if ($change.Delete) {
            if ($entry.SourceBackup) {
                [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($sourceBackup)) | Out-Null
                Move-Item -LiteralPath $sourcePath -Destination $sourceBackup
            }
            $installed.Remove($change.RepositoryName)
        } else {
            Move-Item -LiteralPath $change.OutputDirectory -Destination $target
            $installed[$change.RepositoryName] = [pscustomobject]@{
                PluginName = $change.PluginName
                Commit = $change.Commit
            }
        }
    }

    Write-Json $installedPath $installed
    $journal.Status = 'Committed'
    Write-Json $journalPath $journal
    if (Test-Path -LiteralPath $pendingPath) {
        Remove-Item -LiteralPath $pendingPath -Force
    }
    Write-Json (Join-Path $stateRoot 'apply-result.json') ([pscustomobject]@{ Success = $true; Error = ''; Time = [DateTime]::UtcNow })
} catch {
    $failure = $_.Exception.ToString()
    if ($applyStarted -and $journalPath -and (Test-Path -LiteralPath $journalPath)) {
        try {
            $failedJournal = Get-Content -LiteralPath $journalPath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($failedJournal.Status -eq 'Applying') {
                Restore-Journal $failedJournal $pluginsRoot $managerRoot $installedPath
                $failedJournal.Status = 'RolledBack'
                Write-Json $journalPath $failedJournal
            }
        } catch {
            $failure += [Environment]::NewLine + 'Recovery failed: ' + $_.Exception.ToString()
            $restartHost = $false
        }
    }
    [System.IO.File]::WriteAllText($logPath, $failure)
    if ($null -ne (Get-Variable -Name stateRoot -ErrorAction SilentlyContinue)) {
        Write-Json (Join-Path $stateRoot 'apply-result.json') ([pscustomobject]@{ Success = $false; Error = $failure; Time = [DateTime]::UtcNow })
    }
} finally {
    if ($installLock) { $installLock.Dispose() }
}

if ($restartHost) {
    try {
        Start-Process -FilePath $launcher -WorkingDirectory $hostRoot -WindowStyle Hidden
    } catch {
        [System.IO.File]::AppendAllText($logPath, [Environment]::NewLine + $_.Exception.ToString())
    }
}
