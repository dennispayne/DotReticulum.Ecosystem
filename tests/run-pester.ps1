#Requires -Version 7.0
[CmdletBinding()]
param(
    [string[]] $Path = @((Join-Path $PSScriptRoot 'Pester')),
    [string[]] $Tag = @()
)

$ErrorActionPreference = 'Stop'
if (!(Get-Module Pester | Where-Object Version -GE ([version]'5.7.1'))) {
    Import-Module Pester -MinimumVersion 5.7.1 -ErrorAction Stop
}
$configuration = New-PesterConfiguration
$configuration.Run.Path = $Path
$configuration.Run.PassThru = $true
$configuration.Output.Verbosity = 'Detailed'
if ($Tag.Count) {
    $configuration.Filter.Tag = $Tag
}
$saved = @{}
foreach ($name in 'GITHUB_ENV', 'GITHUB_OUTPUT', 'GITHUB_STEP_SUMMARY', 'GITHUB_PATH', 'GITHUB_STATE', 'NBGV_SetCloudBuildVersionVars', 'NO_COLOR') {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    [Environment]::SetEnvironmentVariable($name, $null, 'Process')
}
try {
    $env:NBGV_SetCloudBuildVersionVars = 'false'
    $env:NO_COLOR = '1'
    $result = Invoke-Pester -Configuration $configuration
}
finally {
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
}
if ($null -eq $result -or $result.FailedCount -gt 0 -or $result.Result -eq 'Failed' -or
    ($result.PassedCount + $result.FailedCount + $result.SkippedCount) -eq 0) {
    exit 1
}
