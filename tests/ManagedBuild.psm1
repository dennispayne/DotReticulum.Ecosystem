Set-StrictMode -Version Latest

function Get-EcosystemTestAssemblies {
    param([string] $Root)
    $solution = [xml](Get-Content -Raw -LiteralPath (Join-Path $Root 'DotReticulum.Ecosystem.slnx'))
    foreach ($project in $solution.SelectNodes('//Project')) {
        $path = $project.GetAttribute('Path').Replace('/', [IO.Path]::DirectorySeparatorChar)
        if ($path -notlike 'tests\*' -and $path -notlike 'tests/*') { continue }
        $name = [IO.Path]::GetFileNameWithoutExtension($path)
        Join-Path (Split-Path $path -Parent) "bin\Release\net10.0\$name.dll"
    }
}

function Test-EcosystemManagedBuild {
    param([string] $Root, [string] $Directory, [string] $Rid, [string] $Commit)
    $manifest = Get-Content -Raw -LiteralPath (Join-Path $Directory 'managed-manifest.json') | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.rid -cne $Rid -or $manifest.commit -cne $Commit) {
        throw 'Managed artifact schema, RID or commit mismatch.'
    }
    $archive = (Resolve-Path -LiteralPath (Join-Path $Directory 'managed.tar')).Path
    if ($manifest.sha256 -cnotmatch '^[0-9A-F]{64}$' -or
        (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -cne $manifest.sha256) {
        throw 'Managed artifact digest mismatch.'
    }
    $expected = @(Get-EcosystemTestAssemblies $Root | ForEach-Object { $_.Replace('\', '/') } | Sort-Object)
    $actual = @($manifest.assemblies | ForEach-Object { $_.Replace('\', '/') } | Sort-Object)
    if (!$expected.Count -or ($expected -join "`n") -cne ($actual -join "`n")) {
        throw 'Managed artifact assembly inventory mismatch.'
    }
    $solution = [xml](Get-Content -Raw -LiteralPath (Join-Path $Root 'DotReticulum.Ecosystem.slnx'))
    $prefixes = @('collector/')
    $prefixes += @($solution.SelectNodes('//Project') | ForEach-Object {
        (Split-Path $_.GetAttribute('Path') -Parent).Replace('\', '/') + '/bin/Release/net10.0/'
    })
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $stream = [IO.File]::OpenRead($archive)
    $reader = [System.Formats.Tar.TarReader]::new($stream)
    try {
        while ($null -ne ($entry = $reader.GetNextEntry())) {
            $path = $entry.Name
            if ($entry.EntryType -notin [System.Formats.Tar.TarEntryType]::RegularFile,
                [System.Formats.Tar.TarEntryType]::V7RegularFile, [System.Formats.Tar.TarEntryType]::Directory -or
                $path -match '(^|/)\.\.?(/|$)|\\|:|^/' -or
                !@($prefixes | Where-Object { $path.StartsWith($_, [StringComparison]::Ordinal) }).Count -or
                !$seen.Add($path)) {
                throw "Unexpected managed archive entry: $path"
            }
        }
    }
    finally { $reader.Dispose(); $stream.Dispose() }
    foreach ($assembly in $expected) {
        if (!$seen.Contains($assembly)) { throw "Managed archive is missing $assembly." }
    }
    if (!$seen.Contains('collector/coverlet.collector.dll')) { throw 'Managed archive is missing its coverage collector.' }
    [pscustomobject]@{ Archive = $archive; Assemblies = $expected }
}

Export-ModuleMember -Function Get-EcosystemTestAssemblies, Test-EcosystemManagedBuild
