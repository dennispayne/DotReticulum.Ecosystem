#Requires -Version 7.0
[CmdletBinding()]
param(
    [string[]] $Path = @((Join-Path $PSScriptRoot 'Pester')),
    [string[]] $Tag = @()
)

$ErrorActionPreference = 'Stop'
Import-Module Pester -MinimumVersion 5.7.1 -ErrorAction Stop
$configuration = New-PesterConfiguration
$configuration.Run.Path = $Path
$configuration.Run.PassThru = $true
$configuration.Output.Verbosity = 'Detailed'
if ($Tag.Count) {
    $configuration.Filter.Tag = $Tag
}
$result = Invoke-Pester -Configuration $configuration
if ($null -eq $result -or $result.FailedCount -gt 0 -or $result.Result -eq 'Failed' -or
    ($result.PassedCount + $result.FailedCount + $result.SkippedCount) -eq 0) {
    exit 1
}
