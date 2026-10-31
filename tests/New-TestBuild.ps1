#Requires -Version 7.3
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Directory,
    [Parameter(Mandatory)] [ValidateSet('linux-x64', 'win-x64', 'osx-arm64')] [string] $Rid,
    [Parameter(Mandatory)] [ValidatePattern('^[a-f0-9]{40}$')] [string] $Commit
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ManagedBuild.psm1') -Force
$root = Split-Path $PSScriptRoot -Parent
if (Test-Path -LiteralPath $Directory) { throw 'Managed artifact directory must be fresh.' }
$null = New-Item -ItemType Directory -Path $Directory
$directoryPath = (Resolve-Path -LiteralPath $Directory).Path
$solution = [xml](Get-Content -Raw -LiteralPath (Join-Path $root 'DotReticulum.Ecosystem.slnx'))
$outputs = @(
    foreach ($project in $solution.SelectNodes('//Project')) {
        $item = Get-Item -LiteralPath (Join-Path $root $project.GetAttribute('Path'))
        $output = Join-Path $item.DirectoryName 'bin\Release\net10.0'
        $inventory = Join-Path $item.DirectoryName "obj\Release\net10.0\$($item.Name).FileListAbsolute.txt"
        if (!(Test-Path -LiteralPath $inventory)) { throw "Missing build inventory: $inventory" }
        foreach ($entry in Get-Content -LiteralPath $inventory) {
            if ($entry.StartsWith($output + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
                (Test-Path -LiteralPath $entry -PathType Leaf)) {
                [IO.Path]::GetRelativePath($root, $entry).Replace('\', '/')
            }
        }
    }
)
$assemblies = @(Get-EcosystemTestAssemblies $root)
$assets = Get-Content -Raw -LiteralPath (Join-Path $root 'tests\LXMF.Tests\obj\project.assets.json') | ConvertFrom-Json
$collector = @($assets.libraries.PSObject.Properties.Name | Where-Object { $_ -like 'coverlet.collector/*' })
if ($collector.Count -ne 1) { throw 'Expected one restored coverage collector version.' }
$collectorDirectory = @($assets.packageFolders.PSObject.Properties.Name | ForEach-Object {
    $candidate = Join-Path $_ "$($collector[0])/build/net10.0"
    if (Test-Path -LiteralPath (Join-Path $candidate 'coverlet.collector.dll')) { $candidate }
}) | Select-Object -First 1
if (!$collectorDirectory) { throw 'Restored coverage collector is missing.' }
Copy-Item -LiteralPath $collectorDirectory -Destination (Join-Path $directoryPath 'collector') -Recurse
$list = Join-Path $directoryPath 'managed-files.txt'
$outputs | Sort-Object -Unique | Set-Content -LiteralPath $list -Encoding utf8NoBOM
Push-Location $root
try {
    & tar -cf (Join-Path $directoryPath 'managed.tar') -T $list
    if ($LASTEXITCODE -ne 0) { throw 'Managed archive creation failed.' }
    & tar -rf (Join-Path $directoryPath 'managed.tar') -C $directoryPath collector
    if ($LASTEXITCODE -ne 0) { throw 'Coverage collector archive creation failed.' }
}
finally { Pop-Location }
[ordered]@{
    schemaVersion = 1; commit = $Commit; rid = $Rid; assemblies = $assemblies
    sha256 = (Get-FileHash -LiteralPath (Join-Path $directoryPath 'managed.tar') -Algorithm SHA256).Hash
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $directoryPath 'managed-manifest.json') -Encoding utf8
$null = Test-EcosystemManagedBuild -Root $root -Directory $directoryPath -Rid $Rid -Commit $Commit
