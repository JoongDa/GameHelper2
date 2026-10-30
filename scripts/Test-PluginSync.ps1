#requires -Version 5.1
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:Utf8 = New-Object Text.UTF8Encoding($false)
$script:Updater = Join-Path $PSScriptRoot 'Sync-Plugins.ps1'
$script:TestRoot = Join-Path (Split-Path -Parent $PSScriptRoot) ('.plugin-sync/tests/' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($script:TestRoot) | Out-Null
$script:Passed = 0

function Save-Text([string]$Path, [string]$Text) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Text, $script:Utf8)
}
function Git([string]$Repo, [string[]]$GitArgs) {
    $result = & git.exe -c core.hooksPath=NUL -C $Repo @GitArgs 2>$null
    if ($LASTEXITCODE -ne 0) { throw "Fixture git failed: $($GitArgs -join ' ')" }
    return ($result -join "`n").Trim()
}
function Commit([string]$Repo) {
    Git $Repo @('add', '-A') | Out-Null
    Git $Repo @('commit', '-qm', 'Fixture') | Out-Null
    return Git $Repo @('rev-parse', 'HEAD')
}
function Init-Repo([string]$Path) {
    [IO.Directory]::CreateDirectory($Path) | Out-Null
    Git $Path @('init', '-q', '-b', 'main') | Out-Null
    Git $Path @('config', 'user.name', 'Test') | Out-Null
    Git $Path @('config', 'user.email', 'test@localhost') | Out-Null
    Git $Path @('config', 'core.autocrlf', 'false') | Out-Null
}
function Assert($Condition, [string]$Message) {
    if (-not $Condition) { throw "ASSERTION FAILED: $Message" }
}
function Read([string]$Path) { return [IO.File]::ReadAllText($Path) }
function New-Fixture([string]$Name) {
    $dir = Join-Path $script:TestRoot ($Name + ' space')
    $core = Join-Path $dir 'core'
    $plugin = Join-Path $dir 'plugin'
    $local = Join-Path $dir ('local ' + [char]0x6D4B + [char]0x8BD5)
    Init-Repo $core
    Save-Text (Join-Path $core '.gitignore') ".plugin-sync/`n"
    Save-Text (Join-Path $core 'GameOverlay.sln') "Microsoft Visual Studio Solution File, Format Version 12.00`n# Visual Studio Version 17`nGlobal`nEndGlobal`n"
    $lines = @(1..30 | ForEach-Object { "line $_" }) -join "`n"
    Save-Text (Join-Path $core 'shared.txt') ($lines + "`n")
    Save-Text (Join-Path $core 'Plugins/Bundled/plugin.txt') "bundled base`n"
    $coreBase = Commit $core
    Init-Repo $plugin
    Save-Text (Join-Path $plugin 'Demo.csproj') '<Project />'
    Save-Text (Join-Path $plugin 'plugin.txt') ($lines + "`n")
    Save-Text (Join-Path $plugin 'config/settings.txt') 'user settings baseline'
    Save-Text (Join-Path $plugin 'obsolete.txt') 'delete me'
    Save-Text (Join-Path $plugin 'rename-old.txt') 'rename me'
    [IO.File]::WriteAllBytes((Join-Path $plugin 'asset.bin'), [byte[]]@(0, 1, 2, 0))
    $pluginBase = Commit $plugin
    [IO.Directory]::CreateDirectory($local) | Out-Null
    Git $local @('-c', 'core.autocrlf=false', 'clone', '-q', '--local', '--no-hardlinks', '--', $core, '.') | Out-Null
    Git $local @('config', 'user.name', 'Test') | Out-Null
    Git $local @('config', 'user.email', 'test@localhost') | Out-Null
    Git $local @('config', 'core.autocrlf', 'false') | Out-Null
    $localPlugin = Join-Path $local 'Plugins/Demo'
    foreach ($file in Get-ChildItem -LiteralPath $plugin -Recurse -File -Force | Where-Object { $_.FullName -notmatch '[\\/]\.git[\\/]' }) {
        $relative = $file.FullName.Substring($plugin.Length).TrimStart('\', '/')
        $target = Join-Path $localPlugin $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        [IO.File]::Copy($file.FullName, $target)
    }
    Save-Text (Join-Path $local 'Plugins/CraftingHelper/own.txt') 'owned local plugin'
    $manifest = [ordered]@{
        version = 1
        core = [ordered]@{ name = 'Core'; repository = $core; branch = 'main'; commit = $null }
        plugins = @([ordered]@{ name = 'Demo'; path = 'Plugins/Demo'; repository = $plugin; branch = 'main'; commit = $pluginBase })
    }
    Save-Text (Join-Path $local 'plugin-sync.json') ($manifest | ConvertTo-Json -Depth 10)
    $localHead = Commit $local
    return [pscustomobject]@{ Root = $local; Core = $core; Plugin = $plugin; LocalPlugin = $localPlugin; PluginBase = $pluginBase; CoreBase = $coreBase; Head = $localHead }
}
function Run-Updater($Fixture, [string[]]$Options = @(), [int]$Expected = 0) {
    $output = & powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $script:Updater -RepositoryRoot $Fixture.Root @Options
    $code = $LASTEXITCODE
    if ($code -ne $Expected) { throw "Expected exit $Expected, got $code.`n$($output -join "`n")" }
    return $output -join "`n"
}
function Latest-Report($Fixture) {
    $file = Get-ChildItem -LiteralPath (Join-Path $Fixture.Root '.plugin-sync/runs') -Directory |
        Sort-Object CreationTimeUtc -Descending | Select-Object -First 1
    return Get-Content -LiteralPath (Join-Path $file.FullName 'report.json') -Raw -Encoding UTF8 | ConvertFrom-Json
}
function Test-Case([string]$Name, [scriptblock]$Body) {
    Write-Host "TEST $Name"
    & $Body
    $script:Passed++
    Write-Host "PASS $Name"
}

Test-Case 'Merge, binary/delete/rename, config preservation, staged edits, idempotency and restore' {
    $f = New-Fixture 'merge'
    Save-Text (Join-Path $f.Root 'shared.txt') ((Read (Join-Path $f.Root 'shared.txt')).Replace('line 1' + "`n", 'local core' + "`n"))
    Git $f.Root @('add', 'shared.txt') | Out-Null
    $indexBefore = (Get-FileHash -LiteralPath (Join-Path $f.Root '.git/index')).Hash
    Save-Text (Join-Path $f.LocalPlugin 'plugin.txt') ((Read (Join-Path $f.LocalPlugin 'plugin.txt')).Replace('line 1' + "`n", 'local plugin' + "`n"))
    Save-Text (Join-Path $f.LocalPlugin 'config/settings.txt') 'my settings'
    Save-Text (Join-Path $f.LocalPlugin 'config/local.json') '{"local":true}'
    $beforeCore = Read (Join-Path $f.Root 'shared.txt')
    $beforePlugin = Read (Join-Path $f.LocalPlugin 'plugin.txt')
    $beforeManifest = Read (Join-Path $f.Root 'plugin-sync.json')
    Save-Text (Join-Path $f.Core 'shared.txt') ((Read (Join-Path $f.Core 'shared.txt')).Replace('line 25', 'upstream core'))
    Save-Text (Join-Path $f.Core 'Plugins/Bundled/plugin.txt') 'bundled update'
    Save-Text (Join-Path $f.Core 'Plugins/Demo/plugin.txt') 'core must not replace standalone plugin'
    Save-Text (Join-Path $f.Core 'Plugins/CraftingHelper/own.txt') 'core must not replace owned plugin'
    Commit $f.Core | Out-Null
    Save-Text (Join-Path $f.Plugin 'plugin.txt') ((Read (Join-Path $f.Plugin 'plugin.txt')).Replace('line 25', 'upstream plugin'))
    Save-Text (Join-Path $f.Plugin 'config/settings.txt') 'new upstream defaults'
    [IO.File]::WriteAllBytes((Join-Path $f.Plugin 'asset.bin'), [byte[]]@(0, 9, 8, 0))
    [IO.File]::Delete((Join-Path $f.Plugin 'obsolete.txt'))
    [IO.File]::Move((Join-Path $f.Plugin 'rename-old.txt'), (Join-Path $f.Plugin 'rename-new.txt'))
    $unicodeName = 'extra-' + [char]0x4E2D + [char]0x6587 + '.txt'
    Save-Text (Join-Path $f.Plugin $unicodeName) 'unicode path'
    Commit $f.Plugin | Out-Null
    Run-Updater $f | Out-Null
    Assert ((Read (Join-Path $f.Root 'shared.txt')) -match 'local core' -and (Read (Join-Path $f.Root 'shared.txt')) -match 'upstream core') 'both core edits must survive'
    Assert ((Read (Join-Path $f.LocalPlugin 'plugin.txt')) -match 'local plugin' -and (Read (Join-Path $f.LocalPlugin 'plugin.txt')) -match 'upstream plugin') 'both plugin edits must survive'
    Assert ((Read (Join-Path $f.LocalPlugin 'config/settings.txt')) -eq 'my settings') 'config must stay local'
    Assert ([IO.File]::Exists((Join-Path $f.LocalPlugin 'config/local.json'))) 'untracked config must survive'
    Assert (([IO.File]::ReadAllBytes((Join-Path $f.LocalPlugin 'asset.bin')))[1] -eq 9) 'binary must update'
    Assert (-not [IO.File]::Exists((Join-Path $f.LocalPlugin 'obsolete.txt'))) 'upstream deletion must apply'
    Assert (-not [IO.File]::Exists((Join-Path $f.LocalPlugin 'rename-old.txt')) -and [IO.File]::Exists((Join-Path $f.LocalPlugin 'rename-new.txt'))) 'rename must apply'
    Assert ([IO.File]::Exists((Join-Path $f.LocalPlugin $unicodeName))) 'unicode path must apply'
    Assert ((Read (Join-Path $f.Root 'Plugins/CraftingHelper/own.txt')) -eq 'owned local plugin') 'owned plugin must survive'
    Assert ((Git $f.Root @('rev-parse', 'HEAD')) -eq $f.Head) 'HEAD must not move'
    Assert ((Get-FileHash -LiteralPath (Join-Path $f.Root '.git/index')).Hash -eq $indexBefore) 'user index must not change'
    $pointerBefore = Read (Join-Path $f.Root '.plugin-sync/latest-backup.json')
    Run-Updater $f | Out-Null
    Assert ((Latest-Report $f).status -eq 'current') 'second update must be idempotent'
    Assert ((Read (Join-Path $f.Root '.plugin-sync/latest-backup.json')) -eq $pointerBefore) 'no-op must retain backup'
    Run-Updater $f @('-RestoreLatest') | Out-Null
    Assert ((Read (Join-Path $f.Root 'shared.txt')) -eq $beforeCore) 'restore must retain original staged edits'
    Assert ((Read (Join-Path $f.LocalPlugin 'plugin.txt')) -eq $beforePlugin) 'restore must retain original local edits'
    Assert ((Read (Join-Path $f.Root 'plugin-sync.json')) -eq $beforeManifest) 'restore must revert baseline'
    Assert ([IO.File]::Exists((Join-Path $f.LocalPlugin 'obsolete.txt'))) 'restore deleted file'
    Assert (-not [IO.File]::Exists((Join-Path $f.LocalPlugin $unicodeName))) 'restore must remove added file'
    Run-Updater $f | Out-Null
    Save-Text (Join-Path $f.LocalPlugin 'plugin.txt') 'edited after update'
    Run-Updater $f @('-RestoreLatest') 1 | Out-Null
    Assert ((Read (Join-Path $f.LocalPlugin 'plugin.txt')) -eq 'edited after update') 'restore must refuse newer edits'
}

Test-Case 'Text conflict keeps local file and does not advance plugin baseline' {
    $f = New-Fixture 'conflict'
    Save-Text (Join-Path $f.LocalPlugin 'plugin.txt') 'local conflict'
    Save-Text (Join-Path $f.Plugin 'plugin.txt') 'upstream conflict'
    Commit $f.Plugin | Out-Null
    Run-Updater $f @() 2 | Out-Null
    Assert ((Read (Join-Path $f.LocalPlugin 'plugin.txt')) -eq 'local conflict') 'conflict must not overwrite local file'
    $config = Get-Content (Join-Path $f.Root 'plugin-sync.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert ($config.plugins[0].commit -eq $f.PluginBase) 'conflict must not advance baseline'
    Assert ((Latest-Report $f).sources[1].status -eq 'failed') 'conflict must be reported'
}

Test-Case 'Binary conflict keeps local binary' {
    $f = New-Fixture 'binary-conflict'
    [IO.File]::WriteAllBytes((Join-Path $f.LocalPlugin 'asset.bin'), [byte[]]@(0, 5, 0))
    [IO.File]::WriteAllBytes((Join-Path $f.Plugin 'asset.bin'), [byte[]]@(0, 8, 0))
    Commit $f.Plugin | Out-Null
    Run-Updater $f @() 2 | Out-Null
    Assert (([IO.File]::ReadAllBytes((Join-Path $f.LocalPlugin 'asset.bin')))[1] -eq 5) 'binary conflict must stay local'
}

Test-Case 'Core conflict aborts the entire update' {
    $f = New-Fixture 'core-conflict'
    Save-Text (Join-Path $f.Root 'shared.txt') 'local core conflict'
    Save-Text (Join-Path $f.Core 'shared.txt') 'remote core conflict'
    Commit $f.Core | Out-Null
    $manifestBefore = Read (Join-Path $f.Root 'plugin-sync.json')
    Run-Updater $f @() 1 | Out-Null
    Assert ((Read (Join-Path $f.Root 'shared.txt')) -eq 'local core conflict') 'core must stay local'
    Assert ((Read (Join-Path $f.Root 'plugin-sync.json')) -eq $manifestBefore) 'core failure must not advance any baseline'
}

Test-Case 'Build failure leaves source and baselines untouched' {
    $f = New-Fixture 'build-failure'
    Save-Text (Join-Path $f.Core 'shared.txt') 'build must not apply this'
    Save-Text (Join-Path $f.Core 'GameOverlay.sln') 'invalid solution'
    Commit $f.Core | Out-Null
    $sourceBefore = Read (Join-Path $f.Root 'shared.txt')
    $manifestBefore = Read (Join-Path $f.Root 'plugin-sync.json')
    Run-Updater $f @() 1 | Out-Null
    Assert ((Read (Join-Path $f.Root 'shared.txt')) -eq $sourceBefore) 'failed build must not apply source'
    Assert ((Read (Join-Path $f.Root 'plugin-sync.json')) -eq $manifestBefore) 'failed build must not advance baseline'
}

Test-Case 'Check-only changes neither source nor baselines' {
    $f = New-Fixture 'check'
    Save-Text (Join-Path $f.Plugin 'plugin.txt') 'available update'
    Commit $f.Plugin | Out-Null
    $sourceBefore = Read (Join-Path $f.LocalPlugin 'plugin.txt')
    $manifestBefore = Read (Join-Path $f.Root 'plugin-sync.json')
    Run-Updater $f @('-CheckOnly') | Out-Null
    Assert ((Read (Join-Path $f.LocalPlugin 'plugin.txt')) -eq $sourceBefore) 'check must not apply source'
    Assert ((Read (Join-Path $f.Root 'plugin-sync.json')) -eq $manifestBefore) 'check must not advance baseline'
    Assert ((Latest-Report $f).sources[1].status -eq 'ready') 'check must find update'
}

Test-Case 'Missing project is monitored but not silently marked synced' {
    $f = New-Fixture 'missing'
    [IO.File]::Delete((Join-Path $f.LocalPlugin 'Demo.csproj'))
    Save-Text (Join-Path $f.Plugin 'plugin.txt') 'new version'
    Commit $f.Plugin | Out-Null
    Run-Updater $f @() 2 | Out-Null
    $report = Latest-Report $f
    Assert ($report.sources[1].status -eq 'skipped' -and $report.sources[1].latest) 'missing source must still be monitored'
    $config = Get-Content (Join-Path $f.Root 'plugin-sync.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert ($config.plugins[0].commit -eq $f.PluginBase) 'missing project must not advance baseline'
}

Test-Case 'Unavailable source is reported without overwriting local files' {
    $f = New-Fixture 'unavailable'
    $config = Get-Content (Join-Path $f.Root 'plugin-sync.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $config.plugins[0].repository = Join-Path $script:TestRoot 'nonexistent-repository'
    Save-Text (Join-Path $f.Root 'plugin-sync.json') ($config | ConvertTo-Json -Depth 10)
    $before = Read (Join-Path $f.LocalPlugin 'plugin.txt')
    Run-Updater $f @() 2 | Out-Null
    Assert ((Read (Join-Path $f.LocalPlugin 'plugin.txt')) -eq $before) 'unavailable source must not overwrite'
    Assert ((Latest-Report $f).sources[1].status -eq 'failed') 'unavailable source must be reported'
}

Test-Case 'Rewritten upstream history is not silently merged' {
    $f = New-Fixture 'rewritten'
    Git $f.Plugin @('checkout', '-q', '--orphan', 'replacement') | Out-Null
    Save-Text (Join-Path $f.Plugin 'plugin.txt') 'unrelated replacement'
    Commit $f.Plugin | Out-Null
    Git $f.Plugin @('branch', '-f', 'main', 'HEAD') | Out-Null
    Run-Updater $f @() 2 | Out-Null
    Assert ((Latest-Report $f).sources[1].message -match 'rewritten') 'rewritten history must be reported'
}

Test-Case 'Path traversal is rejected before source application' {
    $f = New-Fixture 'traversal'
    $config = Get-Content (Join-Path $f.Root 'plugin-sync.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $config.plugins[0].path = '../escape'
    Save-Text (Join-Path $f.Root 'plugin-sync.json') ($config | ConvertTo-Json -Depth 10)
    Run-Updater $f @() 1 | Out-Null
    Assert (-not [IO.File]::Exists((Join-Path (Split-Path -Parent $f.Root) 'escape'))) 'path must not escape root'
}

Test-Case 'Already-staged submodule-to-source conversion is preserved' {
    $f = New-Fixture 'vendor-conversion'
    Git $f.Root @('rm', '-r', '-q', '--cached', '--', 'Plugins/Demo') | Out-Null
    Git $f.Root @('update-index', '--add', '--cacheinfo', ('160000,' + $f.PluginBase + ',Plugins/Demo')) | Out-Null
    Git $f.Root @('commit', '-qm', 'Fixture gitlink') | Out-Null
    Git $f.Root @('rm', '-q', '--cached', '--', 'Plugins/Demo') | Out-Null
    Git $f.Root @('add', '--', 'Plugins/Demo') | Out-Null
    $indexBefore = (Get-FileHash -LiteralPath (Join-Path $f.Root '.git/index')).Hash
    $headBefore = Git $f.Root @('rev-parse', 'HEAD')
    Save-Text (Join-Path $f.Plugin 'plugin.txt') 'updated ordinary source'
    Commit $f.Plugin | Out-Null
    Run-Updater $f | Out-Null
    Assert ((Read (Join-Path $f.LocalPlugin 'plugin.txt')) -eq 'updated ordinary source') 'ordinary source must update'
    Assert ((Git $f.Root @('rev-parse', 'HEAD')) -eq $headBefore) 'conversion must not move HEAD'
    Assert ((Get-FileHash -LiteralPath (Join-Path $f.Root '.git/index')).Hash -eq $indexBefore) 'staged conversion must remain unchanged'
}

Write-Host "All $script:Passed integration tests passed. Fixtures: $script:TestRoot"
