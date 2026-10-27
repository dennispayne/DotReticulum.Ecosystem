#Requires -Version 7.0
<#
.SYNOPSIS
Creates the annotated release tag telemetry-v<version> for the current commit without pushing it.

.DESCRIPTION
The version comes from Nerdbank.GitVersioning for DotTelemetry, so the tag matches the version
embedded in the binary. Pushing the tag starts the Release workflow; this script never pushes
unless -Push is given.
#>
[CmdletBinding()]
param(
    [string] $Project = 'src/DotTelemetry/DotTelemetry.csproj',
    [switch] $Push
)

$ErrorActionPreference = 'Stop'

if (git status --porcelain) { throw 'The working tree has uncommitted changes.' }
$branch = git rev-parse --abbrev-ref HEAD
if ($branch -ne 'main') { throw "Releases are tagged from main, not '$branch'." }

$version = (dotnet msbuild $Project -getProperty:NuGetPackageVersion -p:Configuration=Release -t:GetBuildVersion | Select-Object -Last 1).Trim()
if ($LASTEXITCODE -ne 0 -or $version -notmatch '^\d+\.\d+\.\d+$') { throw "Unexpected version '$version'." }

$tag = "telemetry-v$version"
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
