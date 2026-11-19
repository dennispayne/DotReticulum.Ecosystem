#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version = '1.0.0',
    [string] $OutputDirectory
)

function Add-NuGetNotices {
    param(
        [Parameter(Mandatory)] [string] $ProjectPath,
        [Parameter(Mandatory)] [string] $PublishDirectory
    )

    $assetsPath = Join-Path (Split-Path $ProjectPath -Parent) 'obj\project.assets.json'
    if (!(Test-Path -LiteralPath $assetsPath)) { throw "NuGet assets were not produced: $assetsPath" }
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    $packageLibraries = @($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' } | Sort-Object Name)
    $nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget\packages' }
    $noticePath = Join-Path $PublishDirectory 'licenses\NuGet\THIRD-PARTY-NOTICES.txt'
    $null = New-Item -ItemType Directory -Path (Split-Path $noticePath -Parent) -Force
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add("NuGet dependency notices for $([IO.Path]::GetFileNameWithoutExtension($ProjectPath)).")
    $lines.Add('Each package remains under its own license; this application does not relicense these dependencies.')

    foreach ($library in $packageLibraries) {
        $id, $version = $library.Name -split '/', 2
        $packageDirectory = Join-Path $nugetRoot (Join-Path $id.ToLowerInvariant() $version)
        if (!(Test-Path -LiteralPath $packageDirectory)) { throw "NuGet package cache is missing: $id $version" }
        $nuspec = Get-ChildItem -LiteralPath $packageDirectory -File -Filter '*.nuspec' | Select-Object -First 1
        $license = 'Not declared in the NuGet package metadata'
        $projectUrl = 'Not declared'
        if ($nuspec) {
            [xml]$metadata = Get-Content -LiteralPath $nuspec.FullName -Raw
            $licenseNode = $metadata.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='license']")
            if ($licenseNode) {
                $license = $licenseNode.InnerText
                if ($licenseNode.Attributes['type']) { $license = "$($licenseNode.Attributes['type'].Value): $license" }
            }
            $projectNode = $metadata.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='projectUrl']")
            if ($projectNode -and $projectNode.InnerText) { $projectUrl = $projectNode.InnerText }
        }

        $safeId = $id -replace '[^A-Za-z0-9._-]', '_'
        $licenseDirectory = Join-Path (Join-Path (Split-Path $noticePath -Parent) $safeId) $version
        $includedFiles = [Collections.Generic.List[string]]::new()
        $candidates = Get-ChildItem -LiteralPath $packageDirectory -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^(LICENSE|COPYING|NOTICE)(\..+)?$' }
        foreach ($candidate in $candidates) {
            $relative = [IO.Path]::GetRelativePath($packageDirectory, $candidate.FullName)
            $destination = Join-Path $licenseDirectory $relative
            $null = New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force
            Copy-Item -LiteralPath $candidate.FullName -Destination $destination -Force
            $includedFiles.Add($relative)
        }

        $lines.Add('')
        $lines.Add("Package: $id $version")
        $lines.Add("License: $license")
        $lines.Add("Project: $projectUrl")
        $lines.Add("Included license files: $(if ($includedFiles.Count) { $includedFiles -join ', ' } else { 'No separate license file was supplied in the package cache.' })")
    }

    Set-Content -LiteralPath $noticePath -Value $lines -Encoding utf8
}

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (!$IsWindows) { throw 'The Windows MSI can only be built on Windows.' }

$repository = Split-Path $PSScriptRoot -Parent
$buildRoot = if ($OutputDirectory) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    Join-Path $repository "artifacts\builds\ecosystem-msi-$Version"
}
if (Test-Path -LiteralPath $buildRoot) {
    throw "Refusing to overwrite existing MSI build output: $buildRoot"
}

$stageRoot = Join-Path $buildRoot 'stage'
$nativeCache = Join-Path $buildRoot 'native-cache'
$packageExtraction = Join-Path $nativeCache 'msys2-extract'
$msiOutput = Join-Path $buildRoot 'package'
$null = New-Item -ItemType Directory -Path $stageRoot, $nativeCache, $packageExtraction, $msiOutput -Force

$installerTools = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
$env:PATH = "$installerTools;$env:PATH"
$vswhere = Join-Path $installerTools 'vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw "Required MSVC discovery tool not found: $vswhere" }
$visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$visualStudio) { throw 'MSVC NativeAOT tools are unavailable.' }

$applications = @(
    @{ Name = 'telemetry'; Project = 'src\Telemetry\Telemetry.csproj'; Documentation = 'docs\telemetry.md' },
    @{ Name = 'lxmd'; Project = 'src\lxmd\lxmd.csproj'; Documentation = 'docs\lxmf.md' },
    @{ Name = 'rnphone'; Project = 'src\rnphone\rnphone.csproj'; Documentation = 'docs\lxst.md' },
    @{ Name = 'nomadnet'; Project = 'src\NomadNet\NomadNet.csproj'; Documentation = 'docs\nomadnet.md' },
    @{ Name = 'sideband'; Project = 'src\Sideband.Desktop\Sideband.Desktop.csproj'; Documentation = 'docs\sideband.md' }
)

foreach ($application in $applications) {
    $publishDirectory = Join-Path $stageRoot $application.Name
    $arguments = @(
        'publish', $application.Project,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-p:PublishAot=true',
        '-p:ILLinkTreatWarningsAsErrors=true',
        '-p:IlcTreatWarningsAsErrors=true',
        '-m:1',
        '-o', $publishDirectory
    )
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "NativeAOT publish failed for $($application.Name): $LASTEXITCODE" }
    Copy-Item -LiteralPath (Join-Path $repository 'LICENSE') -Destination $publishDirectory
    $documentationDirectory = Join-Path $publishDirectory 'documentation'
    $null = New-Item -ItemType Directory -Path $documentationDirectory -Force
    Copy-Item -LiteralPath (Join-Path $repository $application.Documentation) -Destination $documentationDirectory
    Add-NuGetNotices -ProjectPath (Join-Path $repository $application.Project) -PublishDirectory $publishDirectory
    Get-ChildItem -LiteralPath $publishDirectory -Recurse -File |
        Where-Object { $_.Extension -in @('.pdb', '.dbg') } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName }
}

$msys2Packages = @(
    @{
        Name = 'mingw-w64-ucrt-x86_64-codec2-1.2.0-2-any.pkg.tar.zst'
        Sha256 = '11a85ff886cc34f85cb8c9f2ceaeb7809a9d601ebec068305998a1962bbfec41'
    },
    @{
        Name = 'mingw-w64-ucrt-x86_64-libgcc-16.2.0-6-any.pkg.tar.zst'
        Sha256 = '80b2497f019977dd9528b1abc8621b1328a0115ab1f724047ccbb56bf323f11d'
    },
    @{
        Name = 'mingw-w64-ucrt-x86_64-libogg-1.3.6-1-any.pkg.tar.zst'
        Sha256 = '110a34239e8f122da15e041b2601e2f3c6d4c909d738bdf978700601e0f64449'
    },
    @{
        Name = 'mingw-w64-ucrt-x86_64-libwinpthread-14.0.0.r569.g34eeafb7d-1-any.pkg.tar.zst'
        Sha256 = 'b4ebe2826e848c5e790e06ea7b37d3e0594e005f42382b0506886f17df8127da'
    },
    @{
        Name = 'mingw-w64-ucrt-x86_64-opus-1.6.1-2-any.pkg.tar.zst'
        Sha256 = 'bb114bcf26225f50f874497ebfd6b8224fff2a20bda3f8ee45cd07ce5f02657b'
    },
    @{
        Name = 'mingw-w64-ucrt-x86_64-opusfile-0.12-4-any.pkg.tar.zst'
        Sha256 = '8a9fa96bcba9ae6caf77b9834e7c3a4072078761aeb7aa8121967e3f65faf656'
    }
)

$verifiedRuntime = Join-Path $repository 'artifacts\builds\lxst-msys2'
$packageBaseUri = 'https://repo.msys2.org/mingw/ucrt64/'
foreach ($package in $msys2Packages) {
    $archive = Join-Path $nativeCache $package.Name
    $existingArchive = Join-Path $verifiedRuntime $package.Name
    if (!(Test-Path -LiteralPath $archive) -and (Test-Path -LiteralPath $existingArchive)) {
        Copy-Item -LiteralPath $existingArchive -Destination $archive
    }
    if (!(Test-Path -LiteralPath $archive)) {
        Invoke-WebRequest -Uri ($packageBaseUri + $package.Name) -OutFile $archive
    }
    $actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $package.Sha256) { throw "MSYS2 package hash mismatch: $($package.Name)" }
    & tar.exe -xf $archive -C $packageExtraction
    if ($LASTEXITCODE -ne 0) { throw "Could not extract MSYS2 package: $($package.Name)" }
}

$codec2Source = Join-Path $nativeCache 'codec2-1.2.0.tar.gz'
if (!(Test-Path -LiteralPath $codec2Source)) {
    Invoke-WebRequest -Uri 'https://github.com/drowe67/codec2/archive/1.2.0/codec2-1.2.0.tar.gz' -OutFile $codec2Source
}
if ((Get-FileHash -LiteralPath $codec2Source -Algorithm SHA256).Hash.ToLowerInvariant() -ne
    'cbccae52b2c2ecc5d2757e407da567eb681241ff8dadce39d779a7219dbcf449') {
    throw 'Codec2 1.2.0 source archive hash mismatch.'
}
$codec2Patch = Join-Path $nativeCache 'codec2-msys2-0001-GetDependencies_point_to_correct_file.patch'
if (!(Test-Path -LiteralPath $codec2Patch)) {
    Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/msys2/MINGW-packages/89d94dc0d9d97957caf21c626ce54cec6aa71c02/mingw-w64-codec2/0001-GetDependencies_point_to_correct_file.patch' -OutFile $codec2Patch
}
if ((Get-FileHash -LiteralPath $codec2Patch -Algorithm SHA256).Hash.ToLowerInvariant() -ne
    '2379f3dfc592c27111cfab1e52f6d4fd73341f39819eac119a5fed367ec87e50') {
    throw 'Codec2 MSYS2 source patch hash mismatch.'
}
$codec2Recipe = Join-Path $nativeCache 'mingw-w64-codec2-PKGBUILD'
if (!(Test-Path -LiteralPath $codec2Recipe)) {
    Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/msys2/MINGW-packages/89d94dc0d9d97957caf21c626ce54cec6aa71c02/mingw-w64-codec2/PKGBUILD' -OutFile $codec2Recipe
}
if ((Get-FileHash -LiteralPath $codec2Recipe -Algorithm SHA256).Hash.ToLowerInvariant() -ne
    '1c687cab0df756bf9e40a537a04fec430bd7a6bc5a5ee8b8968bbc062307d3a6') {
    throw 'Codec2 MSYS2 package recipe hash mismatch.'
}

$rnphoneDirectory = Join-Path $stageRoot 'rnphone'
$nativeBin = Join-Path $packageExtraction 'ucrt64\bin'
$requiredRuntimeFiles = @(
    'libcodec2.dll',
    'libgcc_s_seh-1.dll',
    'libogg-0.dll',
    'libopus-0.dll',
    'libopusfile-0.dll',
    'libwinpthread-1.dll'
)
foreach ($file in $requiredRuntimeFiles) {
    $source = Join-Path $nativeBin $file
    if (!(Test-Path -LiteralPath $source)) { throw "Required rnphone runtime file is missing: $source" }
    Copy-Item -LiteralPath $source -Destination $rnphoneDirectory
}

$nativeLicenses = Join-Path $rnphoneDirectory 'licenses\native'
$null = New-Item -ItemType Directory -Path $nativeLicenses -Force
$licenseFiles = @(
    @{ Source = 'share\licenses\codec2\COPYING'; Destination = 'codec2-COPYING.txt' },
    @{ Source = 'share\licenses\libgcc\COPYING.RUNTIME'; Destination = 'libgcc-COPYING.RUNTIME.txt' },
    @{ Source = 'share\licenses\libgcc\COPYING3'; Destination = 'libgcc-COPYING3.txt' },
    @{ Source = 'share\licenses\libogg\COPYING'; Destination = 'libogg-COPYING.txt' },
    @{ Source = 'share\licenses\libwinpthread\COPYING'; Destination = 'libwinpthread-COPYING.txt' },
    @{ Source = 'share\licenses\opus\COPYING'; Destination = 'opus-COPYING.txt' },
    @{ Source = 'share\licenses\opusfile\LICENSE'; Destination = 'opusfile-LICENSE.txt' }
)
foreach ($license in $licenseFiles) {
    $source = Join-Path $packageExtraction "ucrt64\$($license.Source)"
    if (!(Test-Path -LiteralPath $source)) { throw "Native dependency license is missing: $source" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $nativeLicenses $license.Destination)
}
Copy-Item -LiteralPath $codec2Source -Destination (Join-Path $nativeLicenses 'codec2-1.2.0-source.tar.gz')
Copy-Item -LiteralPath $codec2Patch -Destination (Join-Path $nativeLicenses 'codec2-msys2-source.patch')
Copy-Item -LiteralPath $codec2Recipe -Destination (Join-Path $nativeLicenses 'mingw-w64-codec2-PKGBUILD')
@'
Native codec payload: unmodified MSYS2 UCRT64 packages, verified by the package archive SHA-256 values recorded in docs/lxst.md.

libcodec2.dll is Codec2 1.2.0, LGPL-2.1-or-later. The corresponding upstream source archive, MSYS2 build patch and package recipe used for this binary are included beside this notice. The MSYS2 packaging recipe is pinned at https://github.com/msys2/MINGW-packages/tree/89d94dc0d9d97957caf21c626ce54cec6aa71c02/mingw-w64-codec2.

libopus-0.dll is Opus 1.6.1; libogg-0.dll is libogg 1.3.6; libopusfile-0.dll is opusfile 0.12. Their BSD notices are included. libgcc_s_seh-1.dll and libwinpthread-1.dll are the unmodified MSYS2 runtime files; their GPL runtime exception and MIT/BSD license texts are included.

The application loads local Ogg files only. libopusurl, libopusenc, OpenSSL and unrelated MSYS2 tools are not included.
'@ | Set-Content -LiteralPath (Join-Path $nativeLicenses 'THIRD-PARTY-NOTICES.txt') -Encoding utf8

$licenseText = Get-Content -LiteralPath (Join-Path $repository 'LICENSE') -Raw
$licenseText = $licenseText -replace "`r`n|`r|`n", "`n"
$rtfText = [Text.StringBuilder]::new()
foreach ($character in $licenseText.ToCharArray()) {
    $code = [int]$character
    if ($character -eq '\') { [void]$rtfText.Append('\\') }
    elseif ($character -eq '{') { [void]$rtfText.Append('\{') }
    elseif ($character -eq '}') { [void]$rtfText.Append('\}') }
    elseif ($character -eq "`n") { [void]$rtfText.Append('\par ') }
    elseif ($code -gt 127) {
        $signedCode = if ($code -gt 32767) { $code - 65536 } else { $code }
        [void]$rtfText.Append("\u$signedCode?")
    } else { [void]$rtfText.Append($character) }
}
$rtf = "{\rtf1\ansi\deff0{\fonttbl{\f0\fswiss Arial;}}\viewkind4\uc1\f0\fs18 " +
    "DotReticulum.Ecosystem is experimental, unaudited and not for production or security-critical use.\par\par " +
    $rtfText.ToString() + '}'
Set-Content -LiteralPath (Join-Path $stageRoot 'LICENSE.rtf') -Value $rtf -Encoding ascii -NoNewline

$wixProject = Join-Path $repository 'packaging\windows\DotReticulum.Ecosystem.wixproj'
$wixArguments = @(
    'build', $wixProject,
    '-c', 'Release',
    '-m:1',
    "-p:InstallerVersion=$Version",
    "-p:StageRoot=$stageRoot",
    '-o', $msiOutput
)
& dotnet @wixArguments
if ($LASTEXITCODE -ne 0) { throw "WiX MSI build failed: $LASTEXITCODE" }

$msi = Get-ChildItem -LiteralPath $msiOutput -File -Filter '*.msi' | Select-Object -First 1
if (!$msi) { throw "WiX did not produce an MSI in $msiOutput" }
$hash = (Get-FileHash -LiteralPath $msi.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $($msi.Name)" | Set-Content -LiteralPath "$($msi.FullName).sha256" -Encoding ascii
Write-Output "MSI: $($msi.FullName)"
Write-Output "SHA256: $hash"
