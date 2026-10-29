#Requires -Version 7.0
[CmdletBinding()]
param(
    [switch] $Pester,
    [switch] $Gui,
    [switch] $Coverage,
    [switch] $Live,
    [switch] $LxstLive,
    [switch] $NomadNetLive,
    [switch] $RequireComplete,
    [string] $OutputDirectory,
    [string] $ArtifactsPath
)
$ErrorActionPreference = 'Stop'
$arguments = @{}
foreach ($key in $PSBoundParameters.Keys) { $arguments[$key] = $PSBoundParameters[$key] }
& (Join-Path $PSScriptRoot 'run-compatibility.ps1') @arguments
exit $LASTEXITCODE
