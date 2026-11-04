#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Binary,
    [Parameter(Mandatory)] [string] $EvidenceDirectory,
    [string] $Repository = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent),
    [Parameter(Mandatory)] [string] $NativeObjectDirectory,
    [Parameter(Mandatory)] [string] $CompiledSourceDirectory,
    [Parameter(Mandatory)] [string] $ManagedTestDirectory,
    [string] $ManagedSourceRoot,
    [string] $NativeSourceRoot
)
$ErrorActionPreference = 'Stop'
$Binary = (Resolve-Path $Binary).Path
$Repository = (Resolve-Path $Repository).Path
if (-not $NativeSourceRoot) { $NativeSourceRoot = $Repository }
$NativeSourceRoot = [IO.Path]::GetFullPath($NativeSourceRoot)
$EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path $EvidenceDirectory) { throw 'Evidence directory must be absent; no acceptance receipts are overwritten.' }
$null = New-Item $EvidenceDirectory -ItemType Directory
foreach ($key in 'DOTRETICULUM_PYTHON', 'DOTRETICULUM_UPSTREAM', 'DOTRETICULUM_LXMF_UPSTREAM', 'DOTRETICULUM_NOMADNET_SOURCE') {
    if (-not [Environment]::GetEnvironmentVariable($key)) { throw "$key is required." }
}
function Reference-PrintingSnapshot {
    $sources = @{
        RNS = $env:DOTRETICULUM_UPSTREAM
        LXMF = $env:DOTRETICULUM_LXMF_UPSTREAM
        NomadNet = $env:DOTRETICULUM_NOMADNET_SOURCE
    }
    return @($sources.GetEnumerator() | Sort-Object Name | ForEach-Object {
        $root = $_.Value
        $files = switch ($_.Name) {
            'RNS' { @('RNS\__init__.py') }
            'LXMF' { @('LXMF\LXMessage.py', 'LXMF\LXMRouter.py') }
            'NomadNet' { @('nomadnet\NomadNetworkApp.py', 'nomadnet\Conversation.py', 'nomadnet\Directory.py') }
        }
        @{
            name=$_.Name; path=$root; head=(& git -C $root rev-parse HEAD); status=@(& git -C $root status --porcelain)
            files=@($files | ForEach-Object { @{ path=$_; sha256=(Get-FileHash (Join-Path $root $_)).Hash } })
        }
    })
}
$before = Reference-PrintingSnapshot
if (@($before | Where-Object { $_.status.Count }).Count) { throw 'Reference sources must be clean.' }
$binaryHash = (Get-FileHash $Binary).Hash
$sourceRoot = Join-Path $EvidenceDirectory 'source'
$CompiledSourceDirectory = (Resolve-Path $CompiledSourceDirectory).Path
$sourceHashes = @(Get-ChildItem $CompiledSourceDirectory -File -Recurse | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($CompiledSourceDirectory, $_.FullName)
        $copy = Join-Path $sourceRoot $relative
        $null = New-Item (Split-Path $copy -Parent) -ItemType Directory -Force
        Copy-Item $_.FullName $copy
        @{ path=$relative; sha256=(Get-FileHash $copy).Hash }
})
$harness = @('tests\NomadNet.Tests\NomadNetPrintingTests.cs', 'tests\NomadNet.Tests\NomadNetPrintingVectors.json',
    'tests\Interop\GenerateNomadNetPrintingVectors.py', 'tests\Interop\nomadnet_printing_peer.py',
    'tests\Pester\NomadNetPrinting.Tests.ps1', 'tests\Pester\NomadNetPrinterStub.ps1',
    'tests\Pester\Invoke-NomadNetPrintingAcceptance.ps1', 'tests\Interop\GenerateNomadNetClientRuntimeVectors.py',
    'tests\Interop\reference_environment.py', 'tests\Interop\nomadnet_live_node.py', 'tests\run-pester.ps1',
    'tests\Pester\Replay-NomadNetPrintingAcceptance.ps1')
foreach ($relative in $harness) {
    $copy = Join-Path $sourceRoot $relative
    $null = New-Item (Split-Path $copy -Parent) -ItemType Directory -Force
    Copy-Item (Join-Path $Repository $relative) $copy
}
foreach ($relative in 'global.json', 'Directory.Build.props', 'Directory.Build.targets', 'DotReticulum.Ecosystem.slnx',
    'external\DotReticulum\global.json', 'external\DotReticulum\Directory.Build.props', 'external\DotReticulum\Directory.Build.targets') {
    if (Test-Path (Join-Path $Repository $relative)) {
        $copy = Join-Path $sourceRoot $relative
        $null = New-Item (Split-Path $copy -Parent) -ItemType Directory -Force
        Copy-Item (Join-Path $Repository $relative) $copy
    }
}
$binaryRoot = Join-Path $EvidenceDirectory 'binary'
$null = New-Item $binaryRoot -ItemType Directory
Copy-Item $Binary $binaryRoot
Get-ChildItem (Split-Path $Binary -Parent) -File | Where-Object { $_.Name -match '\.(pdb|dbg)$' } |
    Copy-Item -Destination $EvidenceDirectory
$objects = (Resolve-Path $NativeObjectDirectory).Path
$rid = if ($IsWindows) { 'win-x64' } else { 'linux-x64' }
$compilation = Join-Path $objects "bin\NomadNet\release_$rid"
if (-not (Test-Path $compilation)) { throw 'Expected isolated native compilation directory is absent.' }
$nativeInput = Join-Path $EvidenceDirectory 'native-input'
$null = New-Item $nativeInput -ItemType Directory
Get-ChildItem $compilation -File | Copy-Item -Destination $nativeInput
$ManagedTestDirectory = (Resolve-Path $ManagedTestDirectory).Path
if (-not $ManagedSourceRoot) { $ManagedSourceRoot = $Repository }
$ManagedSourceRoot = [IO.Path]::GetFullPath($ManagedSourceRoot)
$managedOutput = Join-Path $EvidenceDirectory 'managed'
$null = New-Item $managedOutput -ItemType Directory
Get-ChildItem $ManagedTestDirectory | Copy-Item -Destination $managedOutput -Recurse
$checks = [Collections.Generic.List[object]]::new()
$generated = [Collections.Generic.List[object]]::new()
foreach ($build in @(@{name='native';path=$compilation;source=$NativeSourceRoot},
    @{name='managed';path=$managedOutput;source=$ManagedSourceRoot})) {
foreach ($pdb in Get-ChildItem $build.path -File -Filter '*.pdb') {
    $stream = [IO.File]::OpenRead($pdb.FullName)
    $provider = [Reflection.Metadata.MetadataReaderProvider]::FromPortablePdbStream($stream)
    try {
        $reader = $provider.GetMetadataReader()
        foreach ($handle in $reader.Documents) {
            $document = $reader.GetDocument($handle)
            $path = $reader.GetString($document.Name)
            $expected = [Convert]::ToHexStringLower($reader.GetBlobBytes($document.Hash))
            if ($reader.GetGuid($document.HashAlgorithm) -ne [guid]'8829d00f-11b8-4213-878b-770e8597ac16') {
                throw "Unsupported PDB source checksum algorithm: $path"
            }
            if ($path.StartsWith($build.source, [StringComparison]::OrdinalIgnoreCase)) {
                $relative = [IO.Path]::GetRelativePath($build.source, $path)
                $captured = Join-Path $sourceRoot $relative
                if (Test-Path $captured -PathType Leaf) {
                    $actual = (Get-FileHash $captured).Hash.ToLowerInvariant()
                    if ($expected -ne $actual) { throw "Compiled PDB source mismatch: $relative" }
                    $checks.Add(@{ compilation=$build.name; assembly=$pdb.BaseName; path=$relative; sha256=$actual })
                    continue
                }
            }
            $target = Join-Path $EvidenceDirectory "generated\pdb\$($build.name)\$($pdb.BaseName)\$([IO.Path]::GetFileName($path))"
            $null = New-Item (Split-Path $target -Parent) -ItemType Directory -Force
            if (Test-Path $path -PathType Leaf) { Copy-Item $path $target }
            else {
                $found = $false
                foreach ($custom in $reader.GetCustomDebugInformation($handle)) {
                    $information = $reader.GetCustomDebugInformation($custom)
                    if ($reader.GetGuid($information.Kind) -ne [guid]'0e8a571b-6926-466e-b4ad-8ab04611f5fe') { continue }
                    $bytes = $reader.GetBlobBytes($information.Value)
                    $size = [BitConverter]::ToInt32($bytes, 0)
                    if ($size -eq 0) { [IO.File]::WriteAllBytes($target, $bytes[4..($bytes.Length-1)]) }
                    else {
                        $input = [IO.MemoryStream]::new($bytes, 4, $bytes.Length-4)
                        $decompress = [IO.Compression.DeflateStream]::new($input, [IO.Compression.CompressionMode]::Decompress)
                        $output = [IO.MemoryStream]::new()
                        try {
                            $decompress.CopyTo($output)
                            if ($output.Length -ne $size) { throw 'Embedded source length differs.' }
                            [IO.File]::WriteAllBytes($target, $output.ToArray())
                        }
                        finally { $output.Dispose(); $decompress.Dispose(); $input.Dispose() }
                    }
                    $found = $true
                }
                if (-not $found) { throw "Unverifiable compiled source document: $path" }
            }
            $actual = (Get-FileHash $target).Hash.ToLowerInvariant()
            if ($actual -ne $expected) { throw "Generated compiled source mismatch: $path" }
            $generated.Add(@{ compilation=$build.name; assembly=$pdb.BaseName; sourcePath=$path; path=[IO.Path]::GetRelativePath($EvidenceDirectory, $target); sha256=$actual })
        }
    }
    finally { $provider.Dispose(); $stream.Dispose() }
}
}
foreach ($assembly in 'nomadnet', 'LXMF', 'DotReticulum.Applications', 'DotReticulum.Core',
    'DotReticulum.Crypto', 'DotReticulum.Interfaces', 'DotReticulum.Transport') {
    if (-not @($checks | Where-Object assembly -EQ $assembly).Count) { throw "Missing compiled source verification for $assembly" }
}
if (-not @($checks | Where-Object { $_.compilation -eq 'managed' -and $_.assembly -eq 'NomadNet.Tests' }).Count) {
    throw 'Missing compiled printing test-source verification.'
}
$testAssembly = [Reflection.Assembly]::LoadFile((Join-Path $managedOutput 'NomadNet.Tests.dll'))
$fixture = $testAssembly.GetManifestResourceStream('NomadNet.Tests.NomadNetPrintingVectors.json')
if (-not $fixture) { throw 'Printing fixture was not embedded in the compiled test assembly.' }
try { $embeddedFixtureHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($fixture)) }
finally { $fixture.Dispose() }
if ($embeddedFixtureHash -ne (Get-FileHash (Join-Path $sourceRoot 'tests\NomadNet.Tests\NomadNetPrintingVectors.json')).Hash) {
    throw 'Compiled printing fixture differs from frozen source.'
}
foreach ($filter in '*.ilc.rsp', '*.rd.xml', '*.AssemblyInfo.cs', '*.GlobalUsings.g.cs', '*JsonContext*.cs', 'project.assets.json') {
    Get-ChildItem $objects -File -Recurse -Filter $filter | ForEach-Object {
        $destination = Join-Path $EvidenceDirectory ('generated\' + [IO.Path]::GetRelativePath($objects, $_.FullName))
        $null = New-Item (Split-Path $destination -Parent) -ItemType Directory -Force
        Copy-Item $_.FullName $destination
    }
}
if (-not (Get-ChildItem $EvidenceDirectory -File -Recurse -Filter '*.ilc.rsp')) { throw 'NativeAOT compiler response file is absent.' }
$assets = Get-Content (Join-Path $objects 'obj\NomadNet\project.assets.json') -Raw | ConvertFrom-Json
$compiler = @($assets.libraries.psobject.Properties.Name | Where-Object { $_ -like '*Microsoft.DotNet.ILCompiler/*' })
$runtime = @($assets.project.frameworks.'net10.0'.downloadDependencies |
    Where-Object name -Like 'Microsoft.NETCore.App.Runtime.NativeAOT.*')
if ((& dotnet --version) -ne '10.0.403' -or -not $compiler.Count -or
    @($compiler | Where-Object { -not $_.EndsWith('/10.0.14') }).Count -or
    $runtime.Count -ne 1 -or $runtime[0].version -ne '[10.0.14, 10.0.14]') {
    throw 'Unexpected serviced compiler/runtime.'
}
& dotnet --info | Set-Content (Join-Path $EvidenceDirectory 'dotnet-info.txt')
& $Binary --version | Set-Content (Join-Path $EvidenceDirectory 'binary-version.txt')
& $Binary --help | Set-Content (Join-Path $EvidenceDirectory 'binary-help.txt')
if ($LASTEXITCODE) { throw 'Selected native binary failed --help.' }
if (-not (Select-String -Path (Join-Path $EvidenceDirectory 'binary-help.txt') -SimpleMatch -Pattern 'paper-print')) { throw 'Binary does not expose printing.' }
$env:DOTRETICULUM_NOMADNET_BINARY = $Binary
$env:DOTRETICULUM_TEST_REPOSITORY = $Repository
$env:DOTRETICULUM_NOMADNET_PRINTING = '1'
Import-Module Pester -MinimumVersion 5.7.1 -ErrorAction Stop
$configuration = New-PesterConfiguration
$configuration.TestDrive.Enabled = $false
$configuration.Run.Path = Join-Path $Repository 'tests\Pester\NomadNetPrinting.Tests.ps1'
$configuration.Run.PassThru = $true
$configuration.Output.Verbosity = 'Detailed'
$configuration.TestResult.Enabled = $true
$configuration.TestResult.OutputPath = Join-Path $EvidenceDirectory 'pester.xml'
$configuration.TestResult.OutputFormat = 'NUnitXml'
$result = Invoke-Pester -Configuration $configuration
$after = Reference-PrintingSnapshot
$receipt = @{
    binary=$Binary; sha256=$binaryHash; binaryUnchanged=($binaryHash -eq (Get-FileHash $Binary).Hash)
    head=(& git -C $Repository rev-parse HEAD); dirty=@(& git -C $Repository status --porcelain)
    passed=$result.PassedCount; failed=$result.FailedCount; skipped=$result.SkippedCount
    referenceBefore=$before; referenceAfter=$after; source=$sourceHashes; compiledSource=$checks.ToArray()
    generatedSource=$generated.ToArray(); compiler=$compiler; runtime=$runtime; sdk=(& dotnet --version)
    nativeObjectDirectory=$objects; compiledSourceDirectory=$CompiledSourceDirectory; repository=$Repository
    managedTestDirectory=$ManagedTestDirectory; embeddedFixtureSha256=$embeddedFixtureHash
    managedSourceRoot=$ManagedSourceRoot; nativeSourceRoot=$NativeSourceRoot
    python=$env:DOTRETICULUM_PYTHON; powerShell=$env:DOTRETICULUM_PWSH
    harness=@($harness | ForEach-Object { @{path=$_;sha256=(Get-FileHash (Join-Path $sourceRoot $_)).Hash} })
    fixtureSha256=(Get-FileHash (Join-Path $Repository 'tests\NomadNet.Tests\NomadNetPrintingVectors.json')).Hash
    noRepublish=$true; physicalPrinter=$false
}
$receipt | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDirectory 'handoff.json') -Encoding utf8NoBOM
if (($before | ConvertTo-Json -Depth 8 -Compress) -ne ($after | ConvertTo-Json -Depth 8 -Compress)) {
    throw 'Referenced-source hashes changed during acceptance.'
}
if (-not $receipt.binaryUnchanged) { throw 'Selected binary changed during acceptance.' }
if ($result.FailedCount -or $result.PassedCount -ne 20 -or $result.SkippedCount) { throw 'Printing acceptance requires exactly 20 passing cases, no skips.' }
Write-Host "Frozen printing acceptance: $EvidenceDirectory"
