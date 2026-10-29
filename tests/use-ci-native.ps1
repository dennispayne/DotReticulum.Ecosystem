#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Directory,
    [Parameter(Mandatory)] [ValidateSet('linux-x64', 'win-x64', 'osx-arm64')] [string] $Rid,
    [string] $Commit = $env:GITHUB_SHA
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'NativeBuild.psm1') -Force
if (!$Commit) { throw 'Supply the expected source commit.' }
$targets = @(Get-VerifiedCiNativeBuild -Directory $Directory -Rid $Rid -Commit $Commit)
foreach ($target in $targets) {
    if (!$IsWindows) {
        & chmod +x $target.Binary
        if ($LASTEXITCODE -ne 0) { throw "Cannot restore executable permission: $($target.Binary)" }
    }
    [Environment]::SetEnvironmentVariable($target.Variable, $target.Binary, 'Process')
    if ($env:GITHUB_ENV) { "$($target.Variable)=$($target.Binary)" | Add-Content -LiteralPath $env:GITHUB_ENV }
    Write-Host "Using verified $($target.Name) from $Commit ($Rid)."
}
