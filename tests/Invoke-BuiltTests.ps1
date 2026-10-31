#Requires -Version 7.3
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Directory,
    [Parameter(Mandatory)] [ValidateSet('linux-x64', 'win-x64', 'osx-arm64')] [string] $Rid,
    [Parameter(Mandatory)] [ValidatePattern('^[a-f0-9]{40}$')] [string] $Commit,
    [Parameter(Mandatory)] [string] $Filter,
    [Parameter(Mandatory)] [string] $ResultsDirectory,
    [switch] $Coverage
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ManagedBuild.psm1') -Force
$root = Split-Path $PSScriptRoot -Parent
$build = Test-EcosystemManagedBuild -Root $root -Directory $Directory -Rid $Rid -Commit $Commit
if (Test-Path -LiteralPath $ResultsDirectory) { throw 'Test results directory must be fresh.' }
$null = New-Item -ItemType Directory -Path $ResultsDirectory
$ResultsDirectory = (Resolve-Path -LiteralPath $ResultsDirectory).Path
& tar -xf $build.Archive -C $root --exclude=collector
if ($LASTEXITCODE -ne 0) { throw 'Managed artifact extraction failed.' }
& tar -xf $build.Archive -C $Directory collector
if ($LASTEXITCODE -ne 0) { throw 'Coverage collector extraction failed.' }
$collector = (Resolve-Path -LiteralPath (Join-Path $Directory 'collector')).Path
$failed = $false
$discovered = 0
$saved = @{}
foreach ($variable in 'GITHUB_ENV', 'GITHUB_OUTPUT', 'GITHUB_STEP_SUMMARY', 'GITHUB_PATH', 'GITHUB_STATE') {
    $saved[$variable] = [Environment]::GetEnvironmentVariable($variable)
    [Environment]::SetEnvironmentVariable($variable, $null)
}
try {
    foreach ($assembly in $build.Assemblies) {
        $name = [IO.Path]::GetFileNameWithoutExtension($assembly)
        $reportsBefore = @(Get-ChildItem -LiteralPath $ResultsDirectory -Recurse -Filter coverage.opencover.xml).Count
        $arguments = @('vstest', (Join-Path $root $assembly), "/TestCaseFilter:$Filter",
            "/ResultsDirectory:$ResultsDirectory", "/Logger:trx;LogFileName=$name.trx", "/TestAdapterPath:$collector")
        if ($Coverage) {
            $arguments += '/Collect:XPlat Code Coverage', "/Settings:$(Join-Path $PSScriptRoot 'coverage.runsettings')"
        }
        & dotnet @arguments -- xUnit.ParallelizeTestCollections=false
        if ($LASTEXITCODE -ne 0) { $failed = $true }
        $trx = Join-Path $ResultsDirectory "$name.trx"
        if (!(Test-Path -LiteralPath $trx)) { throw "Missing execution report for $name." }
        $report = [xml](Get-Content -Raw -LiteralPath $trx)
        $discovered += [int]$report.TestRun.ResultSummary.Counters.total
        if ($Coverage -and [int]$report.TestRun.ResultSummary.Counters.executed -gt 0 -and
            @(Get-ChildItem -LiteralPath $ResultsDirectory -Recurse -Filter coverage.opencover.xml).Count -le $reportsBefore) {
            throw "Missing requested coverage for $name."
        }
    }
}
finally {
    foreach ($variable in $saved.Keys) { [Environment]::SetEnvironmentVariable($variable, $saved[$variable]) }
}
if (!$discovered) { throw "No tests matched the requested filter: $Filter" }
if ($failed) { exit 1 }
