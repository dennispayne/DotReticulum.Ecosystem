#Requires -Version 7.0
<#
.SYNOPSIS
Identifies the DotReticulum stack packages consumed by this repository.

.DESCRIPTION
The stack is consumed as NuGet.org packages whose version is the root Directory.Build.props
DotReticulumVersion property. Its provenance identity is that version, the DotReticulum source
commit recorded in the package nuspec repository element, the NuGet SHA-512 of each restored
.nupkg (verified against the actual file) and a SHA-256 over the extracted package files.
Frozen acceptance candidates record Get-DotReticulumStackId as their Stack value, so a change of
package version or restored package content invalidates them.
#>
Set-StrictMode -Version Latest

$script:StackPackageIds = @(
    'DotReticulum.Applications', 'DotReticulum.Core', 'DotReticulum.Crypto',
    'DotReticulum.Interfaces', 'DotReticulum.Transport')

# Read from DotReticulum 99275e6b5724033fcb3cd3f78befea0ae60c786b:tests/Interop/reference_environment.py
# (the commit recorded in the 1.5.7.158 package nuspecs). Re-verify before changing either value.
$script:StackReference = [pscustomobject]@{
    Version = '1.5.7.158'
    Commit = '99275e6b5724033fcb3cd3f78befea0ae60c786b'
    RnsVersion = '1.5.7'
    RnsSha = 'a29bfb51c468945c96a1ee22cae0114e993e2391'
}

function Get-DefaultRoot {
    Split-Path $PSScriptRoot -Parent
}

<#
.SYNOPSIS
Returns the recorded DotReticulum package release and the RNS reference pin it was verified against.
#>
function Get-DotReticulumStackReference {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param()
    $script:StackReference
}

<#
.SYNOPSIS
Reads DotReticulumVersion from the repository root Directory.Build.props.
#>
function Get-DotReticulumStackVersion {
    [CmdletBinding()]
    [OutputType([string])]
    param([string] $Root = (Get-DefaultRoot))
    $props = Join-Path $Root 'Directory.Build.props'
    if (-not (Test-Path -LiteralPath $props -PathType Leaf)) { throw "Directory.Build.props not found: $props" }
    [xml] $xml = Get-Content -LiteralPath $props -Raw
    $values = @($xml.SelectNodes('/Project/PropertyGroup/DotReticulumVersion') | ForEach-Object { $_.InnerText.Trim() } |
        Where-Object { $_ })
    if ($values.Count -ne 1) { throw "Expected exactly one DotReticulumVersion in $props; found $($values.Count)." }
    if ($values[0] -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "DotReticulumVersion '$($values[0])' is not a four-part version." }
    $values[0]
}

<#
.SYNOPSIS
Resolves the NuGet global packages folder used by restore.
#>
function Get-NuGetPackagesRoot {
    [CmdletBinding()]
    [OutputType([string])]
    param()
    if ($env:NUGET_PACKAGES) { return [IO.Path]::GetFullPath($env:NUGET_PACKAGES) }
    Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages'
}

<#
.SYNOPSIS
Returns the restored DotReticulum stack package identity.

.DESCRIPTION
Returns Version, Commit, Packages (package id to NuGet base64 SHA-512 of the .nupkg),
Extracted (package id to SHA-256 over the extracted files) and Id (a canonical string of all of
them for exact comparison). Fails when a package is not restored, its .nupkg does not match its
recorded SHA-512, its nuspec does not name the expected id/version, or package commits disagree.

.EXAMPLE
(Get-DotReticulumStackIdentity).Id
#>
function Get-DotReticulumStackIdentity {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [string] $Root = (Get-DefaultRoot),
        [string] $PackagesRoot = (Get-NuGetPackagesRoot)
    )
    $version = Get-DotReticulumStackVersion -Root $Root
    $packages = [ordered]@{}
    $extracted = [ordered]@{}
    $commits = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($id in $script:StackPackageIds) {
        $lower = $id.ToLowerInvariant()
        $directory = Join-Path $PackagesRoot "$lower\$version"
        $nupkg = Join-Path $directory "$lower.$version.nupkg"
        $sha512File = "$nupkg.sha512"
        $nuspec = Join-Path $directory "$lower.nuspec"
        foreach ($required in $nupkg, $sha512File, $nuspec) {
            if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
                throw "DotReticulum package $id $version is not restored ($required missing). Run 'dotnet restore DotReticulum.Ecosystem.slnx'."
            }
        }
        $recorded = (Get-Content -LiteralPath $sha512File -Raw).Trim()
        $actual = [Convert]::ToBase64String([Security.Cryptography.SHA512]::HashData([IO.File]::ReadAllBytes($nupkg)))
        if ($actual -cne $recorded) { throw "Restored $id $version .nupkg does not match its recorded SHA-512." }
        [xml] $spec = Get-Content -LiteralPath $nuspec -Raw
        $namespace = [Xml.XmlNamespaceManager]::new($spec.NameTable)
        $namespace.AddNamespace('n', $spec.DocumentElement.NamespaceURI)
        $metadata = $spec.SelectSingleNode('/n:package/n:metadata', $namespace)
        if ($null -eq $metadata -or $metadata.SelectSingleNode('n:id', $namespace).InnerText -cne $id -or
            $metadata.SelectSingleNode('n:version', $namespace).InnerText -cne $version) {
            throw "Restored $id nuspec does not identify $id $version."
        }
        $repository = $metadata.SelectSingleNode('n:repository', $namespace)
        $commit = if ($repository) { $repository.GetAttribute('commit') } else { '' }
        if ($commit -notmatch '^[0-9a-f]{40}$') { throw "Restored $id $version nuspec has no repository commit." }
        $null = $commits.Add($commit)
        $packages[$id] = $recorded
        $lines = @(Get-ChildItem -LiteralPath $directory -Recurse -File -Force |
            Where-Object Name -NE '.nupkg.metadata' |
            ForEach-Object {
                $relative = [IO.Path]::GetRelativePath($directory, $_.FullName).Replace('\', '/')
                "$relative $((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())"
            } | Sort-Object -CaseSensitive)
        $extracted[$id] = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::UTF8.GetBytes(($lines -join "`n") + "`n"))).ToLowerInvariant()
    }
    if ($commits.Count -ne 1) { throw "DotReticulum $version packages record different source commits: $(@($commits) -join ', ')." }
    $commitValue = @($commits)[0]
    $parts = @("DotReticulum $version+$commitValue") + @($packages.Keys | ForEach-Object {
        "$_ sha512=$($packages[$_]) files-sha256=$($extracted[$_])"
    })
    [pscustomobject]@{
        Version = $version
        Commit = $commitValue
        Packages = $packages
        Extracted = $extracted
        Id = $parts -join '; '
    }
}

<#
.SYNOPSIS
Returns the canonical stack identity string recorded as a candidate's Stack value.
#>
function Get-DotReticulumStackId {
    [CmdletBinding()]
    [OutputType([string])]
    param([string] $Root = (Get-DefaultRoot))
    (Get-DotReticulumStackIdentity -Root $Root).Id
}

Export-ModuleMember -Function Get-DotReticulumStackReference, Get-DotReticulumStackVersion, Get-NuGetPackagesRoot,
    Get-DotReticulumStackIdentity, Get-DotReticulumStackId
