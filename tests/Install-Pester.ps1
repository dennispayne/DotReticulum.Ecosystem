#Requires -Version 7.0

<#
.SYNOPSIS
Installs a specified Pester version for the current user.
.DESCRIPTION
Retries bounded installation attempts. The default version is 5.7.1.
.PARAMETER Version
Exact Pester version to install for the current user. Defaults to 5.7.1.
Returns without installing when that version is already available.
.PARAMETER Attempts
Number of Install-Module attempts before falling back to the direct package download.
Defaults to 4, with increasing delays between failed attempts.
.EXAMPLE
.\Install-Pester.ps1 -Version 5.7.1
.NOTES
Copied from the DotReticulum repository's tests/Install-Pester.ps1 (commit 7eb03e7) so CI no longer needs the stack submodule.
#>
[CmdletBinding()]
param([string] $Version = '5.7.1', [int] $Attempts = 4)

$ErrorActionPreference = 'Stop'
if (Get-Module -ListAvailable Pester | Where-Object { $_.Version -eq [version] $Version }) { return }
for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
    try {
        if (!(Get-PSRepository -Name PSGallery -ErrorAction SilentlyContinue)) { Register-PSRepository -Default }
        Install-Module Pester -RequiredVersion $Version -Scope CurrentUser -Force -SkipPublisherCheck -Repository PSGallery -ErrorAction Stop
        return
    }
    catch {
        Write-Warning "Pester install attempt $attempt failed: $($_.Exception.Message)"
        if ($attempt -lt $Attempts) { Start-Sleep -Seconds (10 * $attempt) }
    }
}

# PSGallery's module index is intermittently unavailable on hosted runners; the package endpoint is separate.
Write-Warning 'Falling back to downloading the Pester package directly.'
$userModules = Join-Path ($IsWindows ? [Environment]::GetFolderPath('MyDocuments') : (Join-Path $HOME '.local/share')) ($IsWindows ? 'PowerShell/Modules' : 'powershell/Modules')
$destination = Join-Path $userModules "Pester/$Version"
$package = Join-Path ([IO.Path]::GetTempPath()) "pester-$Version.zip"
Invoke-WebRequest -Uri "https://www.powershellgallery.com/api/v2/package/Pester/$Version" -OutFile $package -MaximumRetryCount 3 -RetryIntervalSec 5
if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
$null = New-Item -ItemType Directory -Force -Path $destination
Expand-Archive -LiteralPath $package -DestinationPath $destination -Force
Remove-Item -LiteralPath $package -Force
if (!(Get-Module -ListAvailable Pester | Where-Object { $_.Version -eq [version] $Version })) {
    throw "Pester $Version could not be installed."
}
