[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Python,
    [Parameter(Mandatory)][string]$ReticulumSource,
    [Parameter(Mandatory)][string]$SidebandSource,
    [Parameter(Mandatory)][string]$LxstSource,
    [string]$DotNet = 'dotnet',
    [string]$ArtifactsDirectory = 'artifacts\builds\sbt',
    [string]$NativeRuntimeDirectory,
    [string]$Filter = 'FullyQualifiedName~SidebandTelemetry|FullyQualifiedName~ActualPrimaryTelemetry'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$executionRoot = (Get-Location).ProviderPath
$relativeArtifacts = $ArtifactsDirectory.Replace('\', [IO.Path]::DirectorySeparatorChar)
$artifacts = [IO.Path]::GetFullPath((Join-Path $root $relativeArtifacts))
if (-not $artifacts.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Acceptance artifacts must remain beneath the repository.'
}
$runtime = Join-Path $artifacts 'runtime'
if ($NativeRuntimeDirectory) {
    if ($IsWindows) { throw 'NativeRuntimeDirectory is for Linux filesystem validation.' }
    $nativeRuntime = [IO.Path]::GetFullPath((Join-Path $executionRoot $NativeRuntimeDirectory))
    if (-not $nativeRuntime.StartsWith($executionRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal) -or
        $nativeRuntime.StartsWith('/mnt/', [StringComparison]::Ordinal)) {
        throw 'NativeRuntimeDirectory must be beneath the current native Linux project directory, not a mounted Windows filesystem.'
    }
    # Keep SDK/diagnostic Unix socket names beneath Linux's sockaddr_un path limit.
    $runtime = Join-Path $executionRoot 'artifacts/builds/t'
}
New-Item -ItemType Directory -Force $runtime | Out-Null
$settings = @{
    DOTRETICULUM_LIVE = '1'
    DOTSIDEBAND_LIVE = '1'
    DOTRETICULUM_PYTHON = [IO.Path]::GetFullPath($Python)
    DOTRETICULUM_UPSTREAM = [IO.Path]::GetFullPath($ReticulumSource)
    DOTRETICULUM_SIDEBAND_UPSTREAM = [IO.Path]::GetFullPath($SidebandSource)
    DOTRETICULUM_LXST_UPSTREAM = [IO.Path]::GetFullPath($LxstSource)
    TMPDIR = $runtime
    TEMP = $runtime
    TMP = $runtime
}
$previous = @{}
foreach ($key in $settings.Keys) {
    $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
    [Environment]::SetEnvironmentVariable($key, $settings[$key], 'Process')
}
# The reference adapter checks real imports/revisions. All locations and clocks use synthetic test profiles.
Push-Location $root
try {
    $project = Join-Path $root ('tests\Sideband.Tests\Sideband.Tests.csproj'.Replace('\', [IO.Path]::DirectorySeparatorChar))
    if ($NativeRuntimeDirectory) {
        & $DotNet build $project -c Release --artifacts-path $artifacts -m:1 -nr:false `
            -p:UseSharedCompilation=false -p:TreatWarningsAsErrors=true -v quiet
        if ($LASTEXITCODE -ne 0) { throw "Telemetry acceptance build failed with exit code $LASTEXITCODE." }
        $binaryDirectory = Join-Path $nativeRuntime 'bin'
        New-Item -ItemType Directory -Force $binaryDirectory | Out-Null
        $compiled = Join-Path $artifacts 'bin/Sideband.Tests/release'
        Copy-Item (Join-Path $compiled '*') $binaryDirectory -Recurse -Force
        Push-Location $nativeRuntime
        try {
            & $DotNet vstest (Join-Path $binaryDirectory 'Sideband.Tests.dll') "--TestCaseFilter:$Filter" `
                '--Logger:trx;LogFileName=telemetry-acceptance.trx' "--ResultsDirectory:$(Join-Path $nativeRuntime 'results')"
        }
        finally { Pop-Location }
    }
    else {
        & $DotNet test $project -c Release --artifacts-path $artifacts --filter $Filter -m:1 -nr:false `
            -p:TreatWarningsAsErrors=true --logger 'trx;LogFileName=telemetry-acceptance.trx' `
            --results-directory (Join-Path $artifacts 'results') -v quiet
    }
    if ($LASTEXITCODE -ne 0) { throw "Telemetry acceptance failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
    foreach ($key in $settings.Keys) {
        [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process')
    }
}
