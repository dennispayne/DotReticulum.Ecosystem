#Requires -Version 7.0
[CmdletBinding()]
param(
    [switch] $Build,
    [switch] $Freeze,
    [switch] $Child,
    [string] $Candidate = 'artifacts\tests\nomad-host-statistics\candidate.json',
    [string] $OutputDirectory = ('artifacts\tests\nomad-host-statistics\replay-' + [guid]::NewGuid().ToString('N')),
    [string] $Binary = 'artifacts\tests\nomad-host-statistics\native\nomadnet.exe',
    [string] $ArtifactsPath = 'artifacts\builds\nomad-host-statistics'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $root
$provenancePath = Join-Path (Split-Path $Candidate -Parent) build-provenance.json
function Get-C107ProductionInputs {
    $directories = @('src\NomadNet','src\LXMF','external\DotReticulum\src')
    if (Test-Path src\Shared) { $directories += 'src\Shared' }
    $paths = @(Get-ChildItem $directories -Recurse -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
    foreach ($directory in '.','external\DotReticulum') {
        $paths += @(Get-ChildItem $directory -File -Force |
            Where-Object { $_.Name -match '^Directory\.|^global.json$|^version.json$|^NuGet.Config$|^.editorconfig$' })
    }
    @($paths | Sort-Object FullName -Unique)
}
function Get-C107AcceptanceInputs {
    $paths = @(Get-ChildItem tests\NomadNet.Tests -Recurse -File |
        Where-Object { $_.Extension -in '.cs','.csproj','.json','.py' -and $_.FullName -notmatch '\\(bin|obj)\\' })
    $paths += @(Get-Item tests\Interop\GenerateNomadNetClientRuntimeVectors.py,
        tests\Interop\nomadnet_live_node.py,tests\Interop\reference_environment.py,
        tests\Pester\NomadNetHostedStatistics.Tests.ps1,tests\Pester\Invoke-NomadNetHostedStatisticsAcceptance.ps1)
    @($paths | Sort-Object FullName -Unique)
}
function ConvertTo-C107Fingerprints($Files) {
    @($Files | Sort-Object FullName -Unique | ForEach-Object {
        @{Path=[IO.Path]::GetRelativePath($root,$_.FullName);Sha256=(Get-FileHash $_.FullName).Hash.ToLowerInvariant()}
    })
}
function Assert-C107Hashes($Files) {
    foreach ($file in $Files) {
        if ((Get-FileHash $file.Path).Hash.ToLowerInvariant() -cne $file.Sha256) { throw "Frozen input changed: $($file.Path)" }
    }
}
if ($Freeze -and (Test-Path $Candidate)) { throw 'Refusing to overwrite an existing frozen statistics candidate.' }
if ($Build) {
    $null = New-Item (Split-Path $provenancePath -Parent) -ItemType Directory -Force
    $head = (& git rev-parse HEAD)
    $sources = @(ConvertTo-C107Fingerprints (Get-C107ProductionInputs))
    $acceptanceSources = @(ConvertTo-C107Fingerprints (Get-C107AcceptanceInputs))
    & dotnet build tests\NomadNet.Tests\NomadNet.Tests.csproj -c Release -warnaserror --nologo -v:q `
        --artifacts-path $ArtifactsPath 2>&1 | Tee-Object (Join-Path (Split-Path $provenancePath -Parent) build.log)
    if ($LASTEXITCODE -ne 0) { throw 'Strict Nomad managed build failed.' }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $install = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $install) { throw 'MSVC NativeAOT tools are required for Windows statistics acceptance.' }
    $setup = Join-Path $install 'VC\Auxiliary\Build\vcvars64.bat'
    $nativeDirectory = Split-Path ([IO.Path]::GetFullPath($Binary)) -Parent
    $env:PATH = (Split-Path $vswhere -Parent) + ';' + $env:PATH
    & $env:ComSpec /c "call `"$setup`" >nul && dotnet publish src\NomadNet\NomadNet.csproj -c Release -r win-x64 --artifacts-path `"$ArtifactsPath`" -o `"$nativeDirectory`" -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -warnaserror --nologo -v:q" 2>&1 |
        Tee-Object (Join-Path (Split-Path $provenancePath -Parent) publish.log)
    if ($LASTEXITCODE -ne 0) { throw 'Strict Windows NativeAOT publish failed.' }
    $assets = @(Get-ChildItem (Join-Path $ArtifactsPath 'obj\NomadNet') -Recurse -Filter project.assets.json -File)
    if ($assets.Count -ne 1) { throw 'Expected one selected native assets file.' }
    $libraries = (Get-Content $assets[0].FullName -Raw | ConvertFrom-Json).libraries
    $compiler = @($libraries.PSObject.Properties.Name | Where-Object { $_ -match '^Microsoft\.DotNet\.ILCompiler/' })
    if ($compiler.Count -ne 1) { throw 'Expected one explicit NativeAOT compiler package.' }
    Assert-C107Hashes $sources
    Assert-C107Hashes $acceptanceSources
    if ((& git rev-parse HEAD) -cne $head) { throw 'Build HEAD changed; do not relabel the binary.' }
    @{Head=$head;Stack=(& git -C external\DotReticulum rev-parse HEAD);Sdk=(& dotnet --version);
        Compiler=($compiler[0] -split '/')[-1];Sources=$sources;AcceptanceSources=$acceptanceSources;Binary=@{Path=$Binary;Sha256=(Get-FileHash $Binary).Hash.ToLowerInvariant()};
        ManagedCommand="dotnet build tests\NomadNet.Tests\NomadNet.Tests.csproj -c Release -warnaserror --artifacts-path $ArtifactsPath";
        NativeCommand="dotnet publish src\NomadNet\NomadNet.csproj -c Release -r win-x64 --artifacts-path $ArtifactsPath -o $nativeDirectory -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -warnaserror";
        Boundary='Dirty integrated inputs frozen by hashes; root HEAD is version provenance, not a clean-tree claim.'} |
        ConvertTo-Json -Depth 12 | Set-Content $provenancePath -Encoding utf8NoBOM
    if (-not $Freeze) { return }
}
if ($Freeze) {
    $previous = Get-Content artifacts\tests\nomad-guide\candidate.json -Raw | ConvertFrom-Json
    $provenance = Get-Content $provenancePath -Raw | ConvertFrom-Json
    if ((& git rev-parse HEAD) -cne $provenance.Head) { throw 'Build HEAD changed; rebuild rather than relabel.' }
    $sources = @(ConvertTo-C107Fingerprints (Get-C107ProductionInputs))
    if (Compare-Object @($provenance.Sources.Path) @($sources.Path)) { throw 'Production input set changed since build.' }
    Assert-C107Hashes $provenance.Sources
    if (-not $provenance.AcceptanceSources) { throw 'Build provenance lacks pre-build acceptance input hashes; rebuild.' }
    if (Compare-Object @($provenance.AcceptanceSources.Path) @((ConvertTo-C107Fingerprints (Get-C107AcceptanceInputs)).Path)) {
        throw 'Acceptance input set changed since build.'
    }
    Assert-C107Hashes $provenance.AcceptanceSources
    Assert-C107Hashes @($provenance.Binary)
    $assemblies = @(Get-ChildItem (Join-Path $ArtifactsPath bin) -Recurse -Filter NomadNet.Tests.dll -File)
    if ($assemblies.Count -ne 1) { throw 'Expected one isolated Release Nomad test assembly.' }
    @{
        Claim='C107';Head=$provenance.Head;Stack=$provenance.Stack;Sdk=$provenance.Sdk;Compiler=$provenance.Compiler
        Reference=$previous.Reference
        Sources=$sources;AcceptanceSources=$provenance.AcceptanceSources
        BuildProvenance=$provenancePath;BuildProvenanceSha256=(Get-FileHash $provenancePath).Hash.ToLowerInvariant()
        ArtifactsPath=$ArtifactsPath
        ManagedArtifacts=@(ConvertTo-C107Fingerprints @(Get-ChildItem $assemblies[0].DirectoryName -File |
            Where-Object Extension -In '.dll','.json','.pdb','.exe'))
        Binary=$provenance.Binary
    } | ConvertTo-Json -Depth 12 | Set-Content $Candidate -Encoding utf8NoBOM
    return
}
$candidatePath = (Resolve-Path $Candidate).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path $output) { throw 'Replay requires a fresh evidence path.' }
$null = New-Item $output -ItemType Directory -Force
if (-not $Child) {
    & pwsh -NoProfile -File $PSCommandPath -Candidate $candidatePath -OutputDirectory (Join-Path $output child) -Child 2>&1 |
        Tee-Object (Join-Path $output fresh-child.log)
    if ($LASTEXITCODE -ne 0) { throw "Fresh child failed: $LASTEXITCODE" }
    return
}
$frozen = Get-Content $candidatePath -Raw | ConvertFrom-Json
Assert-C107Hashes $frozen.Sources
Assert-C107Hashes $frozen.AcceptanceSources
Assert-C107Hashes $frozen.ManagedArtifacts
Assert-C107Hashes @($frozen.Binary)
if (Compare-Object @($frozen.Sources.Path) @((ConvertTo-C107Fingerprints (Get-C107ProductionInputs)).Path)) {
    throw 'Frozen production input set changed.'
}
if ((Get-FileHash $frozen.BuildProvenance).Hash.ToLowerInvariant() -cne $frozen.BuildProvenanceSha256) {
    throw 'Frozen build provenance changed.'
}
$env:DOTRETICULUM_LIVE = '1'
$env:DOTRETICULUM_NOMADNET_HOST_STATISTICS = '1'
$env:DOTRETICULUM_NOMADNET_HOST_STATISTICS_ROOT = $root
$env:DOTRETICULUM_NOMADNET_BINARY = [IO.Path]::GetFullPath($frozen.Binary.Path)
$env:DOTRETICULUM_NOMADNET_HOST_STATISTICS_EVIDENCE = $output
$env:DOTRETICULUM_PYTHON = $frozen.Reference.Python
$env:DOTRETICULUM_UPSTREAM = $frozen.Reference.Rns
$env:DOTRETICULUM_LXMF_UPSTREAM = $frozen.Reference.Lxmf
$env:DOTRETICULUM_NOMADNET_SOURCE = $frozen.Reference.Primary
$env:PYTHONPATH = "$($frozen.Reference.Primary);$($frozen.Reference.Rns);$($frozen.Reference.Lxmf)"
& $env:DOTRETICULUM_PYTHON tests\NomadNet.Tests\GenerateHostedStatisticsVectors.py `
    --output (Join-Path $output vectors.json) --observations (Join-Path $output reference-imports.json) `
    --work (Join-Path $output reference-work) 2>&1 | Tee-Object (Join-Path $output reference.log)
if ($LASTEXITCODE -ne 0) { throw 'Actual primary statistics reference execution failed.' }
$expected = Get-Content tests\NomadNet.Tests\NomadNetHostedStatisticsVectors.json -Raw | ConvertFrom-Json
$actual = Get-Content (Join-Path $output vectors.json) -Raw | ConvertFrom-Json
if (($expected | ConvertTo-Json -Depth 20 -Compress) -cne ($actual | ConvertTo-Json -Depth 20 -Compress)) {
    throw 'Fresh primary statistics vectors differ.'
}
$assembly = @($frozen.ManagedArtifacts | Where-Object Path -Like '*NomadNet.Tests.dll')
if ($assembly.Count -ne 1) { throw 'Expected one frozen test assembly.' }
& dotnet vstest $assembly[0].Path '/TestCaseFilter:FullyQualifiedName~NomadNetHostedStatistics|TestId=T990|TestId=T991' `
    "/ResultsDirectory:$output" '/Logger:trx;LogFileName=statistics.trx' 2>&1 |
    Tee-Object (Join-Path $output tests.log)
if ($LASTEXITCODE -ne 0) { throw 'Frozen statistics/native live tests failed.' }
[xml]$trx = Get-Content (Join-Path $output statistics.trx) -Raw
$counters = $trx.TestRun.ResultSummary.Counters
if ([int]$counters.failed -ne 0 -or [int]$counters.notExecuted -ne 0 -or [int]$counters.passed -lt 23) {
    throw 'Statistics acceptance requires no failures or skipped selected tests.'
}
$env:DOTRETICULUM_NOMADNET_HOST_STATISTICS_CANDIDATE = $candidatePath
$env:DOTRETICULUM_NOMADNET_HOST_STATISTICS_REPLAY = $output
$configuration = New-PesterConfiguration
$configuration.Run.Path = Join-Path $PSScriptRoot NomadNetHostedStatistics.Tests.ps1
$configuration.Run.PassThru = $true
$configuration.Output.Verbosity = 'Detailed'
$configuration.TestResult.Enabled = $true
$configuration.TestResult.OutputPath = Join-Path $output pester.xml
$result = Invoke-Pester -Configuration $configuration
if ($result.FailedCount -ne 0 -or $result.SkippedCount -ne 0 -or $result.PassedCount -lt 3) {
    throw 'Statistics Pester acceptance failed or skipped.'
}
Assert-C107Hashes $frozen.Sources
Assert-C107Hashes $frozen.AcceptanceSources
Assert-C107Hashes $frozen.ManagedArtifacts
Assert-C107Hashes @($frozen.Binary)
@{Claim='C107';Candidate=$candidatePath;CandidateSha256=(Get-FileHash $candidatePath).Hash.ToLowerInvariant()
    Binary=$frozen.Binary;Passed=[int]$counters.passed;Failed=[int]$counters.failed;Skipped=[int]$counters.notExecuted
    PesterPassed=$result.PassedCount;Reference=$frozen.Reference;Platform='Windows win-x64'
    Selector='FullyQualifiedName~NomadNetHostedStatistics|TestId=T990|TestId=T991'
    Boundary='Direct actual-primary app/RNS client to native hosted node over TCP loopback. No Urwid Browser/hop/loss/RF/Linux/macOS claim.'
} | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $output receipt.json) -Encoding utf8NoBOM
