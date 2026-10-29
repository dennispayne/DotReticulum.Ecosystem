#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('linux-x64', 'win-x64', 'osx-arm64')] [string] $Rid,
    [string] $Directory = 'artifacts\native',
    [string] $ArtifactsPath,
    [string] $Commit = $env:GITHUB_SHA
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'NativeBuild.psm1') -Force
if (!$Commit) { $Commit = & git rev-parse HEAD; if ($LASTEXITCODE -ne 0) { throw 'Cannot determine source commit.' } }
if (Test-Path -LiteralPath $Directory) { throw "Use a fresh native output directory: $Directory" }
$null = New-Item -ItemType Directory -Path $Directory
$Directory = (Resolve-Path -LiteralPath $Directory).Path
$root = Split-Path $PSScriptRoot -Parent
foreach ($target in Get-CiNativeTargets $Rid) {
    $output = Join-Path $Directory $target.Name
    $project = Join-Path $root "src\$($target.Project)\$($target.Project).csproj"
    $arguments = @('publish', $project, '-c', 'Release', '-r', $Rid, '--self-contained', 'true',
        '-p:ILLinkTreatWarningsAsErrors=true', '-p:IlcTreatWarningsAsErrors=true', '-o', $output)
    if ($ArtifactsPath) { $arguments += @('--artifacts-path', $ArtifactsPath) }
    if ($IsWindows) {
        $installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
        $env:PATH = "$installer;$env:PATH"
        $installation = & (Join-Path $installer 'vswhere.exe') -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
        if (!$installation) { throw 'MSVC NativeAOT tools are unavailable.' }
        $setup = Join-Path $installation 'VC\Auxiliary\Build\vcvars64.bat'
        $quoted = ($arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
        & $env:ComSpec /c "call `"$setup`" >nul && dotnet $quoted"
    } else {
        & dotnet @arguments
    }
    if ($LASTEXITCODE -ne 0) { throw "NativeAOT publication failed for $($target.Name)." }
    $exe = $target.Name + $(if ($IsWindows) { '.exe' } else { '' })
    & (Join-Path $output $exe) --help
    if ($LASTEXITCODE -ne 0) { throw "Native help failed for $($target.Name)." }
}
New-CiNativeManifest -Directory $Directory -Rid $Rid -Commit $Commit
$null = @(Get-VerifiedCiNativeBuild -Directory $Directory -Rid $Rid -Commit $Commit)
Write-Host "Verified native build manifest: $(Join-Path $Directory 'build-manifest.json')"
