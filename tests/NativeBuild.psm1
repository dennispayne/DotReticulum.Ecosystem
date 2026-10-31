Set-StrictMode -Version Latest

function Get-CiNativeTargets {
    param([ValidateSet('linux-x64', 'win-x64', 'osx-arm64')] [string] $Rid)
    @(
        @{ Name = 'telemetry'; Project = 'Telemetry'; Variable = 'DOTRETICULUM_TELEMETRY_BINARY' }
        @{ Name = 'lxmd'; Project = 'lxmd'; Variable = 'DOTRETICULUM_LXMD_BINARY' }
        if ($Rid -ne 'osx-arm64') {
            @{ Name = 'rnphone'; Project = 'rnphone'; Variable = 'DOTLXST_RNPHONE_BINARY' }
            @{ Name = 'nomadnet'; Project = 'NomadNet'; Variable = 'DOTRETICULUM_NOMADNET_BINARY' }
            @{ Name = 'sideband'; Project = 'Sideband.Desktop'; Variable = 'DOTRETICULUM_SIDEBAND_BINARY' }
        }
    )
}

function New-CiNativeManifest {
    param([string] $Directory, [string] $Rid, [string] $Commit)
    $root = (Resolve-Path -LiteralPath $Directory).Path
    $files = @(
        foreach ($target in Get-CiNativeTargets $Rid) {
            foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root $target.Name) -Recurse -File -Force) {
                if ($file.Extension -in @('.pdb', '.dbg') -or $file.FullName -match '\.dSYM[\\/]') { continue }
                [ordered]@{
                    path = [IO.Path]::GetRelativePath($root, $file.FullName)
                    sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
                }
            }
        }
    )
    [ordered]@{ schemaVersion = 1; rid = $Rid; commit = $Commit; files = $files } |
        ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $root 'build-manifest.json') -Encoding utf8
}

function Get-VerifiedCiNativeBuild {
    param([string] $Directory, [string] $Rid, [string] $Commit)
    $root = (Resolve-Path -LiteralPath $Directory).Path
    $manifest = Get-Content -Raw -LiteralPath (Join-Path $root 'build-manifest.json') | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.rid -cne $Rid -or $manifest.commit -cne $Commit) {
        throw 'Native artifact schema, RID or commit does not match the requested build.'
    }
    $seen = @{}
    foreach ($file in $manifest.files) {
        if ([IO.Path]::IsPathRooted($file.path)) { throw 'Native artifact paths must be relative.' }
        $path = [IO.Path]::GetFullPath((Join-Path $root $file.path))
        $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
        if (!$path.StartsWith($root + [IO.Path]::DirectorySeparatorChar, $comparison) -or $seen.ContainsKey($path)) {
            throw 'Native artifact contains an escaping or duplicate path.'
        }
        $seen[$path] = $true
        if (!(Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $file.sha256) {
            throw "Native artifact file is missing or changed: $($file.path)"
        }
    }
    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File -Force) {
        if ($file.FullName -eq (Join-Path $root 'build-manifest.json') -or
            $file.Extension -in '.pdb', '.dbg' -or $file.FullName -match '\.dSYM[\\/]') { continue }
        if (!$seen.ContainsKey($file.FullName)) { throw "Unverified native runtime file: $($file.FullName)" }
    }
    foreach ($target in Get-CiNativeTargets $Rid) {
        $name = $target.Name + $(if ($Rid -eq 'win-x64') { '.exe' } else { '' })
        $binary = Join-Path (Join-Path $root $target.Name) $name
        if (!$seen.ContainsKey($binary)) { throw "Native artifact has no verified executable for $($target.Name)." }
        [pscustomobject]@{ Name = $target.Name; Variable = $target.Variable; Binary = $binary }
    }
}

Export-ModuleMember -Function Get-CiNativeTargets, New-CiNativeManifest, Get-VerifiedCiNativeBuild
