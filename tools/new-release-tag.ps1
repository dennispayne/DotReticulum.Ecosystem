#Requires -Version 7.0
<#
.SYNOPSIS
Creates an annotated executable release tag for the current commit without pushing it.

.DESCRIPTION
The version comes from Nerdbank.GitVersioning for the selected project, so the tag matches the version
embedded in the binary. Pushing the tag starts the Release workflow; this script never pushes
unless -Push is given.
#>
[CmdletBinding()]
param(
    [ValidateSet('telemetry', 'lxmd', 'rnphone')]
    [string] $Application = 'telemetry',
    [string] $Project,
    [switch] $Push
)

$ErrorActionPreference = 'Stop'

if (!$Project) {
    $Project = if ($Application -eq 'telemetry') {
        'src/Telemetry/Telemetry.csproj'
    } elseif ($Application -eq 'lxmd') {
        'src/lxmd/lxmd.csproj'
    } else {
        'src/rnphone/rnphone.csproj'
    }
}

if (git status --porcelain) { throw 'The working tree has uncommitted changes.' }
$branch = git rev-parse --abbrev-ref HEAD
if ($branch -ne 'main') { throw "Releases are tagged from main, not '$branch'." }

$property = if ($Application -eq 'telemetry') { 'NuGetPackageVersion' } else { 'AssemblyFileVersion' }
$intermediate = [IO.Directory]::CreateTempSubdirectory('DotReticulum.ReleaseTag-')
try {
    $result = @(dotnet msbuild $Project -restore "-getProperty:$property" -p:Configuration=Release `
        -p:UseArtifactsOutput=true "-p:ArtifactsPath=$($intermediate.FullName)" -t:GetBuildVersion 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Could not determine the release version: $($result -join "`n")" }
    $version = ([string]($result | Select-Object -Last 1)).Trim()
} finally {
    $intermediate.Delete($true)
}
$pattern = if ($Application -eq 'telemetry') { '^\d+\.\d+\.\d+$' } else { '^\d+\.\d+\.\d+\.\d+$' }
if ($version -notmatch $pattern) { throw "Unexpected version '$version'." }

$tag = "$Application-v$version"
if (git tag --list $tag) { throw "Tag $tag already exists." }
git tag -a $tag -m "Release $tag"
if ($LASTEXITCODE -ne 0) { throw "Could not create $tag." }
Write-Host "Created $tag at $(git rev-parse --short HEAD)."

if ($Push) {
    git push origin $tag
    if ($LASTEXITCODE -ne 0) { throw "Could not push $tag." }
} else {
    Write-Host "Push with: git push origin $tag   (this starts the Release workflow)"
}
