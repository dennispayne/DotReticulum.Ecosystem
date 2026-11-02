#Requires -Version 7.3
[CmdletBinding()]
param(
    [string] $Directory = (Join-Path $PSScriptRoot '..\artifacts\debug')
)
$ErrorActionPreference = 'Stop'
foreach ($application in 'sideband', 'nomadnet', 'telemetry', 'lxmd', 'rnphone') {
    $root = Join-Path $Directory $application
    $rns = Join-Path $root 'rns'
    $null = New-Item -ItemType Directory -Force -Path $rns, (Join-Path $root 'data')
    # Reset transport settings each launch, preserving application identities and state.
    @"
[reticulum]
  enable_transport = No
  share_instance = No
  instance_name = ecosystem_debug_$application

[logging]
  loglevel = 4

[interfaces]
"@ | Set-Content -LiteralPath (Join-Path $rns 'config') -Encoding utf8
}
