#Requires -Version 7.0
[CmdletBinding()]
param(
    [switch] $Build,
    [switch] $Freeze,
    [switch] $Child,
    [string] $Candidate = 'artifacts\tests\sideband-message-telemetry\candidate.json',
    [string] $OutputDirectory = ('artifacts\tests\sideband-message-telemetry\replay-' + [guid]::NewGuid().ToString('N')),
    [string] $ArtifactsPath = 'artifacts\builds\sideband-message-telemetry',
    [string] $Binary = 'artifacts\tests\sideband-message-telemetry\native\sideband.exe'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $root
$provenancePath = Join-Path (Split-Path $Candidate -Parent) build-provenance.json
function Get-ProductionInputs {
    $directories = @('src\Sideband','src\Sideband.Desktop','src\LXMF','src\LXST','external\DotReticulum\src')
    if (Test-Path src\Shared) { $directories += 'src\Shared' }
    $files = @(Get-ChildItem $directories -Recurse -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
    foreach ($directory in '.','external\DotReticulum') {
        $files += @(Get-ChildItem $directory -File -Force |
            Where-Object { $_.Name -match '^Directory\.|^global\.json$|^version\.json$|^NuGet\.Config$|^\.editorconfig$' })
    }
    @($files | Sort-Object FullName -Unique)
}
function Get-AcceptanceInputs {
    $files = @(Get-ChildItem tests\Sideband.Tests,tests\LXMF.Tests -Recurse -File |
        Where-Object { $_.Extension -in '.cs','.csproj','.json','.py' -and $_.FullName -notmatch '\\(bin|obj|__pycache__)\\' })
    $files += @(Get-ChildItem tests\Interop -File |
        Where-Object { $_.Extension -in '.py','.cs' -and $_.Name -notmatch 'nomad' })
    $files += @(Get-Item tests\Pester\SidebandMessageTelemetry.Tests.ps1,tests\Pester\SidebandMessageCopy.Tests.ps1,
        tests\Pester\Invoke-SidebandMessageTelemetryAcceptance.ps1,tools\PSScriptAnalyzerSettings.psd1,.gitattributes)
    @($files | Sort-Object FullName -Unique)
}
function Get-Fingerprints($Files) {
    @($Files | Sort-Object FullName -Unique | ForEach-Object {
        @{Path=$_.FullName;Sha256=(Get-FileHash $_.FullName).Hash.ToLowerInvariant()}
    })
}
function Assert-Hashes($Files) {
    foreach ($file in $Files) {
        if ((Get-FileHash $file.Path).Hash.ToLowerInvariant() -cne $file.Sha256) { throw "Frozen input changed: $($file.Path)" }
    }
}
if ($Build) {
    $null = New-Item (Split-Path $provenancePath -Parent) -ItemType Directory -Force
    $head = (& git rev-parse HEAD)
    $sources = @(Get-Fingerprints (Get-ProductionInputs))
    $acceptance = @(Get-Fingerprints (Get-AcceptanceInputs))
    & dotnet build tests\Sideband.Tests\Sideband.Tests.csproj -c Release -warnaserror --nologo -v:q `
        --artifacts-path $ArtifactsPath 2>&1 | Tee-Object (Join-Path (Split-Path $provenancePath -Parent) build.log)
    if ($LASTEXITCODE -ne 0) { throw 'Strict managed build failed.' }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $install = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $install) { throw 'Windows MSVC tools are required for native acceptance.' }
    $setup = Join-Path $install 'VC\Auxiliary\Build\vcvars64.bat'
    $nativeDirectory = Split-Path ([IO.Path]::GetFullPath($Binary)) -Parent
    $env:PATH = (Split-Path $vswhere -Parent) + ';' + $env:PATH
    & $env:ComSpec /c "call `"$setup`" >nul && dotnet publish src\Sideband.Desktop\Sideband.Desktop.csproj -c Release -r win-x64 --artifacts-path `"$ArtifactsPath`" -o `"$nativeDirectory`" -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -warnaserror --nologo -v:q" 2>&1 |
        Tee-Object (Join-Path (Split-Path $provenancePath -Parent) publish.log)
    if ($LASTEXITCODE -ne 0) { throw 'Strict NativeAOT publish failed.' }
    $assets = @(Get-ChildItem (Join-Path $ArtifactsPath 'obj\Sideband.Desktop') -Recurse -Filter project.assets.json -File)
    if ($assets.Count -ne 1) { throw 'Expected one selected native assets file.' }
    $libraries = (Get-Content $assets[0].FullName -Raw | ConvertFrom-Json).libraries
    $compiler = @($libraries.PSObject.Properties.Name | Where-Object { $_ -match '^Microsoft\.DotNet\.ILCompiler/' })
    if ($compiler.Count -ne 1) { throw 'Expected one explicit NativeAOT compiler package.' }
    Assert-Hashes @($sources + $acceptance)
    if ((& git rev-parse HEAD) -cne $head) { throw 'Versioning HEAD changed during build.' }
    @{Head=$head;Sdk=(& dotnet --version);Stack=(& git -C external\DotReticulum rev-parse HEAD);
        Compiler=($compiler[0] -split '/')[-1];Sources=$sources;Acceptance=$acceptance;
        ArtifactsPath=$ArtifactsPath;Binary=@{Path=[IO.Path]::GetFullPath($Binary);Sha256=(Get-FileHash $Binary).Hash.ToLowerInvariant()};
        Boundary='Dirty implementation is identified by exact source hashes; HEAD is versioning provenance, not a clean-tree claim.'} |
        ConvertTo-Json -Depth 10 | Set-Content $provenancePath -Encoding utf8NoBOM
    if (-not $Freeze) { return }
}
if ($Freeze) {
    if (Test-Path $Candidate) { throw 'Do not replace an existing frozen candidate.' }
    $provenance = Get-Content $provenancePath -Raw | ConvertFrom-Json
    Assert-Hashes @($provenance.Sources + $provenance.Acceptance + $provenance.Binary)
    if ((& git rev-parse HEAD) -cne $provenance.Head) { throw 'Versioning HEAD changed; rebuild rather than relabel.' }
    $reference = @{}
    foreach ($key in 'DOTRETICULUM_UPSTREAM','DOTRETICULUM_PYTHON','DOTRETICULUM_LXST_UPSTREAM','DOTRETICULUM_SIDEBAND_UPSTREAM') {
        $value = [Environment]::GetEnvironmentVariable($key)
        if (-not $value -or -not (Test-Path $value)) { throw "Select an existing $key before freezing." }
        $reference[$key] = [IO.Path]::GetFullPath($value)
    }
    $referenceFiles = @(Get-Item $reference.DOTRETICULUM_PYTHON)
    foreach ($key in 'DOTRETICULUM_UPSTREAM','DOTRETICULUM_LXST_UPSTREAM','DOTRETICULUM_SIDEBAND_UPSTREAM') {
        $referenceFiles += @(foreach ($path in @(& git -c core.quotepath=false -C $reference[$key] ls-files)) {
            Get-Item -LiteralPath (Join-Path $reference[$key] $path)
        })
    }
    $assembly = @(Get-ChildItem (Join-Path $ArtifactsPath bin) -Recurse -Filter Sideband.Tests.dll -File)
    if ($assembly.Count -ne 1) { throw 'Expected one isolated selected Sideband test assembly.' }
    @{Provenance=$provenance;Reference=$reference;ReferenceFiles=@(Get-Fingerprints $referenceFiles);
        ManagedArtifacts=@(Get-Fingerprints @(Get-ChildItem $assembly[0].DirectoryName -File |
            Where-Object Extension -In '.dll','.json','.pdb','.exe'));
        ProvenanceFile=@{Path=[IO.Path]::GetFullPath($provenancePath);Sha256=(Get-FileHash $provenancePath).Hash.ToLowerInvariant()}} |
        ConvertTo-Json -Depth 12 | Set-Content $Candidate -Encoding utf8NoBOM
    return
}
$candidatePath = (Resolve-Path $Candidate).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path $output) { throw 'Select fresh replay output.' }
$null = New-Item $output -ItemType Directory -Force
if (-not $Child) {
    & pwsh -NoProfile -File $PSCommandPath -Candidate $candidatePath -OutputDirectory (Join-Path $output child) -Child 2>&1 |
        Tee-Object (Join-Path $output child.log)
    if ($LASTEXITCODE -ne 0) { throw 'Fresh-child selected-message telemetry acceptance failed.' }
    return
}
$frozen = Get-Content $candidatePath -Raw | ConvertFrom-Json
$candidateHash = (Get-FileHash $candidatePath).Hash.ToLowerInvariant()
$provenance = $frozen.Provenance
function Assert-Frozen {
    if ((Get-FileHash $candidatePath).Hash.ToLowerInvariant() -cne $candidateHash) { throw 'Candidate manifest changed.' }
    if ((& git rev-parse HEAD) -cne $provenance.Head -or (& dotnet --version) -cne $provenance.Sdk) { throw 'Versioning HEAD/SDK changed.' }
    if ((& git -C external\DotReticulum rev-parse HEAD) -cne $provenance.Stack -or (& git -C external\DotReticulum status --porcelain)) {
        throw 'Stack pin/source changed.'
    }
    if (Compare-Object @($provenance.Sources.Path) @((Get-Fingerprints (Get-ProductionInputs)).Path)) { throw 'Production inventory changed.' }
    if (Compare-Object @($provenance.Acceptance.Path) @((Get-Fingerprints (Get-AcceptanceInputs)).Path)) { throw 'Acceptance inventory changed.' }
    Assert-Hashes @($provenance.Sources + $provenance.Acceptance + $provenance.Binary +
        $frozen.ReferenceFiles + $frozen.ManagedArtifacts + $frozen.ProvenanceFile)
}
Assert-Frozen
Copy-Item -LiteralPath $candidatePath -Destination (Join-Path $output frozen-inputs.json)
try {
    foreach ($entry in Get-ChildItem Env: | Where-Object { $_.Name -match '^(DOTRETICULUM_|DOTLXST_|PYTHONPATH$|GITHUB_)' }) {
        [Environment]::SetEnvironmentVariable($entry.Name, $null, 'Process')
    }
    foreach ($pair in $frozen.Reference.PSObject.Properties) { [Environment]::SetEnvironmentVariable($pair.Name, $pair.Value, 'Process') }
    $env:PYTHONPATH = $env:DOTRETICULUM_UPSTREAM + [IO.Path]::PathSeparator + $env:DOTRETICULUM_LXST_UPSTREAM
    $env:PYTHONIOENCODING = 'utf-8'
    $env:TELEMETER_GEOID_PATH = Join-Path $env:DOTRETICULUM_SIDEBAND_UPSTREAM 'sbapp\assets\geoids'
    $env:DOTRETICULUM_LIVE = '1'
    $env:DOTRETICULUM_SIDEBAND_LIVE = '1'
    $env:DOTRETICULUM_SIDEBAND_BINARY = $provenance.Binary.Path
    $env:DOTRETICULUM_SIDEBAND_MESSAGE_TELEMETRY = '1'
    $env:DOTRETICULUM_SIDEBAND_MESSAGE_TELEMETRY_SHA256 = $provenance.Binary.Sha256
    $env:DOTRETICULUM_SIDEBAND_MESSAGE_TELEMETRY_RESULTS = Join-Path $output native
    $env:DOTRETICULUM_SIDEBAND_MESSAGE_COPY = '1'
    $env:DOTRETICULUM_SIDEBAND_MESSAGE_COPY_SHA256 = $provenance.Binary.Sha256
    $env:DOTRETICULUM_SIDEBAND_MESSAGE_COPY_RESULTS = Join-Path $output raw-copy
    & $env:DOTRETICULUM_PYTHON tests\Interop\reference_environment.py --require-lxmf --require-lxst 2>&1 |
        Tee-Object (Join-Path $output reference.log)
    if ($LASTEXITCODE -ne 0) { throw 'Actual imported reference verification failed.' }
    & $provenance.Binary.Path --help > (Join-Path $output native-help.txt)
    if ($LASTEXITCODE -ne 0) { throw 'Frozen native help failed.' }
    & dotnet test tests\Sideband.Tests\Sideband.Tests.csproj -c Release --no-build --no-restore --artifacts-path $provenance.ArtifactsPath `
        --filter 'FullyQualifiedName~MessageTelemetry|FullyQualifiedName~SelectedTelemetryCopies|FullyQualifiedName~TelemetryAndRawCopyShare' `
        --nologo -v:q --logger 'trx;LogFileName=selected.trx' --results-directory $output
    if ($LASTEXITCODE -ne 0) { throw 'Selected reference/storage/UI/native-primary cases failed.' }
    [xml] $trx = Get-Content (Join-Path $output selected.trx) -Raw
    $counts = $trx.TestRun.ResultSummary.Counters
    if ($counts.total -ne '84' -or $counts.passed -ne '84' -or $counts.notExecuted -ne '0') { throw 'Expected84 executed selected cases without skips.' }
    Import-Module Pester -MinimumVersion 5.7.1
    $configuration = New-PesterConfiguration
    $configuration.Run.Path = @('tests\Pester\SidebandMessageTelemetry.Tests.ps1','tests\Pester\SidebandMessageCopy.Tests.ps1')
    $configuration.Run.PassThru = $true
    $configuration.TestResult.Enabled = $true
    $configuration.TestResult.OutputPath = Join-Path $output native-reference.xml
    $configuration.Output.Verbosity = 'Detailed'
    $pester = Invoke-Pester -Configuration $configuration
    if ($pester.FailedCount -ne 0 -or $pester.PassedCount -ne 4 -or $pester.SkippedCount -ne 0) { throw 'Expected4 executed native/reference Pester cases.' }
    Assert-Frozen
    @{CandidateSha256=$candidateHash;NativeSha256=$provenance.Binary.Sha256;Head=$provenance.Head;Stack=$provenance.Stack;Sdk=$provenance.Sdk;Compiler=$provenance.Compiler;
        SelectedPassed=84;PesterPassed=4;NativeWindows=6;ProductionInputs=$provenance.Sources.Count;
        AcceptanceInputs=$provenance.Acceptance.Count;ReferenceInputs=$frozen.ReferenceFiles.Count;ManagedArtifacts=$frozen.ManagedArtifacts.Count;
        Boundary='Actual-primary direct normal messages both ways, newer reports, clean/abrupt native restart, malformed stream isolation and native actual-profile import. Six offline Windows private-clipboard windows. No OS clipboard, physical sensors, primary GUI, new hop/loss or current Linux replay.'} |
        ConvertTo-Json | Set-Content (Join-Path $output receipt.json) -Encoding utf8NoBOM
}
finally { Assert-Frozen }
