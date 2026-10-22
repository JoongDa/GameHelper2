# Runs dependency-free matcher regressions, the original baseline and current lifecycle checks.
[CmdletBinding()]
param(
    [string]$BaselineRef = 'HEAD',
    [ValidateRange(1, 4096)][int]$Slots = 144
)

$ErrorActionPreference = 'Stop'
$pluginRoot = Split-Path $PSScriptRoot -Parent
$repositoryRoot = (Resolve-Path (Join-Path $pluginRoot '../..')).Path
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('StashUtilityP0-' + [Guid]::NewGuid().ToString('N'))

function Read-BaselineSource([string]$relativePath) {
    $source = & git -C $repositoryRoot show "${BaselineRef}:$relativePath"
    if ($LASTEXITCODE -ne 0) { throw "Cannot read baseline source: $relativePath" }
    return $source -join "`n"
}

function Read-Method([string]$source, [string]$signature) {
    $start = $source.IndexOf($signature, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Baseline method was not found: $signature" }
    $open = $source.IndexOf('{', $start)
    $depth = 1
    $end = $open + 1
    while ($end -lt $source.Length -and $depth -gt 0) {
        if ($source[$end] -eq '{') { $depth++ }
        elseif ($source[$end] -eq '}') { $depth-- }
        $end++
    }
    if ($depth -ne 0) { throw "Unbalanced baseline method: $signature" }
    return $source.Substring($start, $end - $start)
}

try {
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    $baselinePerformance = Read-BaselineSource 'Plugins/StashUtility/StashUtilityCore.EquipmentPerformance.cs'
    $baselineCore = Read-BaselineSource 'Plugins/StashUtility/StashUtilityCore.cs'
    # A deterministic clock makes same-batch expiry reproducible without sleeping.
    $baselinePerformance = $baselinePerformance.Replace('Environment.TickCount64', 'BaselineClock.TickCount64')
    [IO.File]::WriteAllText((Join-Path $temporaryRoot 'BaselinePerformance.cs'), $baselinePerformance)
    $methods = @(
        'private IntPtr GetItemAddressFromElement(IntPtr elementAddr)',
        'private bool IsValidItemEntity(IntPtr address)',
        'private Item ReadFreshItem(IntPtr itemAddress)',
        'private bool TryReadEquipmentMods(Item item, out List<(string name, (float v0, float v1) vals)> mods)'
    ) | ForEach-Object { Read-Method $baselineCore $_ }
    $methodSource = @'
namespace StashUtility {
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using GameOffsets.Natives;
    using GameOffsets.Objects.Components;
    public sealed partial class StashUtilityCore {
'@ + "`n" + ($methods -join "`n") + "`n    }`n}"
    $methodSource = $methodSource.Replace('Environment.TickCount64', 'BaselineClock.TickCount64')
    [IO.File]::WriteAllText((Join-Path $temporaryRoot 'BaselineMethods.cs'), $methodSource)

    $sources = @{
        'EquipmentMatcher.cs' = (Join-Path $pluginRoot 'EquipmentMatcher.cs')
        'EquipmentGroup.cs' = (Join-Path $pluginRoot 'Models/EquipmentGroup.cs')
        'EquipmentMod.cs' = (Join-Path $pluginRoot 'Models/EquipmentMod.cs')
        'StdVector.cs' = (Join-Path $repositoryRoot 'GameOffsets/Natives/StdVector.cs')
        'StdWString.cs' = (Join-Path $repositoryRoot 'GameOffsets/Natives/StdWString.cs')
        'ComponentHeader.cs' = (Join-Path $repositoryRoot 'GameOffsets/Objects/Components/ComponentHeader.cs')
        'ModsAndObjectMagicProperties.cs' = (Join-Path $repositoryRoot 'GameOffsets/Objects/Components/ModsAndObjectMagicProperties.cs')
        'MatcherRegression.cs' = (Join-Path $PSScriptRoot 'MatcherRegression.cs.txt')
        'BaselineHarness.cs' = (Join-Path $PSScriptRoot 'BaselineHarness.cs.txt')
    }
    foreach ($entry in $sources.GetEnumerator()) {
        Copy-Item -LiteralPath $entry.Value -Destination (Join-Path $temporaryRoot $entry.Key)
    }
    @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <EnableNETAnalyzers>false</EnableNETAnalyzers>
  </PropertyGroup>
</Project>
'@ | Set-Content -LiteralPath (Join-Path $temporaryRoot 'EquipmentP0.csproj')
    '<configuration><packageSources><clear /></packageSources></configuration>' |
        Set-Content -LiteralPath (Join-Path $temporaryRoot 'NuGet.config')

    Write-Host "Matcher: current working sources; baseline: $BaselineRef; fake visible slots: $Slots."
    & dotnet run --project (Join-Path $temporaryRoot 'EquipmentP0.csproj') --configuration Release -- $Slots
    if ($LASTEXITCODE -ne 0) { throw "Equipment P0 verification failed (exit $LASTEXITCODE)." }

    $currentRoot = Join-Path $temporaryRoot 'Current'
    New-Item -ItemType Directory -Path $currentRoot | Out-Null
    foreach ($entry in $sources.GetEnumerator()) {
        if ($entry.Key -eq 'BaselineHarness.cs') { continue }
        Copy-Item -LiteralPath $entry.Value -Destination (Join-Path $currentRoot $entry.Key)
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'CurrentHarness.cs.txt') -Destination (Join-Path $currentRoot 'CurrentHarness.cs')
    foreach ($relativePath in @('GameOffsets/Objects/States/InGameState/EntityOffsets.cs',
        'GameOffsets/Objects/UiElement/UiElementBaseOffset.cs', 'GameOffsets/Natives/StdTuple2D.cs', 'GameOffsets/Natives/StdBucket.cs')) {
        Copy-Item -LiteralPath (Join-Path $repositoryRoot $relativePath) -Destination $currentRoot
    }
    $currentPerformance = [IO.File]::ReadAllText((Join-Path $pluginRoot 'StashUtilityCore.EquipmentPerformance.cs'))
    [IO.File]::WriteAllText((Join-Path $currentRoot 'CurrentPerformance.cs'),
        $currentPerformance.Replace('Environment.TickCount64', 'BaselineClock.TickCount64'))
    $currentCore = [IO.File]::ReadAllText((Join-Path $pluginRoot 'StashUtilityCore.cs'))
    $currentMethods = @(
        'private IntPtr GetItemAddressFromElement(IntPtr elementAddr)',
        'private Item ReadFreshItem(IntPtr itemAddress)',
        'private bool TryReadEquipmentMods(Item item, List<(string name, (float v0, float v1) vals)> mods)'
    ) | ForEach-Object { Read-Method $currentCore $_ }
    $currentMethodSource = $methodSource.Substring(0, $methodSource.IndexOf('private IntPtr GetItemAddressFromElement', [StringComparison]::Ordinal)) +
        ($currentMethods -join "`n") + "`n    }`n}"
    [IO.File]::WriteAllText((Join-Path $currentRoot 'CurrentMethods.cs'),
        $currentMethodSource.Replace('Environment.TickCount64', 'BaselineClock.TickCount64'))
    Copy-Item -LiteralPath (Join-Path $temporaryRoot 'EquipmentP0.csproj') -Destination $currentRoot
    Copy-Item -LiteralPath (Join-Path $temporaryRoot 'NuGet.config') -Destination $currentRoot

    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ReaderBindingRegression.cs.txt') -Destination (Join-Path $currentRoot 'ReaderBindingRegression.cs')
    $readerPartial = [IO.File]::ReadAllText((Join-Path $pluginRoot 'StashUtilityCore.MemoryReaders.cs'))
    [IO.File]::WriteAllText((Join-Path $currentRoot 'ReaderDelegates.cs'),
        $readerPartial.Replace('namespace StashUtility', 'namespace BindingRegression').Replace('StashUtilityCore', 'ReaderProbe'))
    $readerMethods = @('private T ReadMemory<T>(IntPtr address)',
        'private bool TryReadMemory<T>(IntPtr address, out T result)',
        'private T[] ReadStdVector<T>(StdVector nativeContainer)') |
        ForEach-Object { Read-Method $currentCore $_ }
    $readerMethodSource = "namespace BindingRegression { using System; using System.Linq; using System.Reflection; using GameOffsets.Natives; public sealed partial class ReaderProbe {`n" +
        ($readerMethods -join "`n") + "`n} }"
    [IO.File]::WriteAllText((Join-Path $currentRoot 'ReaderMethods.cs'), $readerMethodSource)

    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PersistenceRegression.cs.txt') -Destination (Join-Path $currentRoot 'PersistenceRegression.cs')
    $persistencePartial = [IO.File]::ReadAllText((Join-Path $pluginRoot 'StashUtilityCore.SettingsPersistence.cs'))
    [IO.File]::WriteAllText((Join-Path $currentRoot 'Persistence.cs'),
        $persistencePartial.Replace('namespace StashUtility', 'namespace PersistenceRegression').Replace('StashUtilityCore', 'PersistenceProbe').Replace('Environment.TickCount64', 'PersistenceClock.TickCount64'))
    $jsonAssembly = Join-Path $repositoryRoot 'GameHelper/bin/Release/net10.0-windows/win-x64/Newtonsoft.Json.dll'
    if (-not (Test-Path -LiteralPath $jsonAssembly)) { throw 'Build the full solution in Release first; persistence tests reuse its existing Newtonsoft.Json assembly.' }
    $projectPath = Join-Path $currentRoot 'EquipmentP0.csproj'
    $project = [IO.File]::ReadAllText($projectPath)
    $reference = '<ItemGroup><Reference Include="Newtonsoft.Json"><HintPath>' + [Security.SecurityElement]::Escape($jsonAssembly) + '</HintPath></Reference></ItemGroup>'
    [IO.File]::WriteAllText($projectPath, $project.Replace('</Project>', $reference + '</Project>'))
    & dotnet run --project (Join-Path $currentRoot 'EquipmentP0.csproj') --configuration Release -- $Slots
    if ($LASTEXITCODE -ne 0) { throw "Current equipment P0 verification failed (exit $LASTEXITCODE)." }
}
finally {
    # Only remove the uniquely created child of the verified temporary directory.
    $resolvedTemporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
    if ($resolvedTemporaryRoot.StartsWith($resolvedTemporaryParent, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedTemporaryRoot).StartsWith('StashUtilityP0-', [StringComparison]::Ordinal)) {
        if (Test-Path -LiteralPath $resolvedTemporaryRoot) { Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force }
    }
}
