#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Directory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$IsWindows) { throw 'Windows codec prerequisites require Windows.' }
if (Test-Path -LiteralPath $Directory) { throw "Use a fresh codec directory: $Directory" }
$null = New-Item -ItemType Directory -Path $Directory
$Directory = (Resolve-Path -LiteralPath $Directory).Path
$packages = @(
    @{ Name = 'mingw-w64-ucrt-x86_64-codec2-1.2.0-2-any.pkg.tar.zst'; Hash = '11a85ff886cc34f85cb8c9f2ceaeb7809a9d601ebec068305998a1962bbfec41' },
    @{ Name = 'mingw-w64-ucrt-x86_64-libgcc-16.2.0-6-any.pkg.tar.zst'; Hash = '80b2497f019977dd9528b1abc8621b1328a0115ab1f724047ccbb56bf323f11d' },
    @{ Name = 'mingw-w64-ucrt-x86_64-libwinpthread-14.0.0.r569.g34eeafb7d-1-any.pkg.tar.zst'; Hash = 'b4ebe2826e848c5e790e06ea7b37d3e0594e005f42382b0506886f17df8127da' },
    @{ Name = 'mingw-w64-ucrt-x86_64-opus-1.6.1-2-any.pkg.tar.zst'; Hash = 'bb114bcf26225f50f874497ebfd6b8224fff2a20bda3f8ee45cd07ce5f02657b' }
)
foreach ($package in $packages) {
    $archive = Join-Path $Directory $package.Name
    Invoke-WebRequest -Uri "https://repo.msys2.org/mingw/ucrt64/$($package.Name)" -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $package.Hash) {
        throw "MSYS2 package hash mismatch: $($package.Name)"
    }
    & tar.exe -xf $archive -C $Directory
    if ($LASTEXITCODE -ne 0) { throw "Could not extract MSYS2 package: $($package.Name)" }
}
$bin = Join-Path $Directory 'ucrt64\bin'
foreach ($file in 'libcodec2.dll', 'libopus-0.dll', 'libgcc_s_seh-1.dll', 'libwinpthread-1.dll') {
    if (!(Test-Path -LiteralPath (Join-Path $bin $file) -PathType Leaf)) { throw "Missing native codec dependency: $file" }
}
Write-Output $bin
