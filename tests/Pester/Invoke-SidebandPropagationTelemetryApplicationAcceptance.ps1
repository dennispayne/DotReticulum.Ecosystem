#Requires -Version 7.0
[CmdletBinding()]
param(
    [switch] $Build,
    [string] $Candidate = 'artifacts\tests\sideband-propagation-telemetry\application-candidate.json',
    [string] $ReferenceCandidate = 'artifacts\tests\sideband-message-telemetry\candidate.json',
    [string] $ArtifactsPath = 'artifacts\builds\sideband-propagation-application',
    [string] $OutputDirectory = ('artifacts\tests\sideband-propagation-telemetry\application-replay-' + [guid]::NewGuid().ToString('N'))
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $root
Import-Module (Join-Path $root 'tests\StackProvenance.psm1') -Force
function Fingerprints($Files) {
    @($Files | Sort-Object FullName -Unique | ForEach-Object {
        @{Path=$_.FullName;Sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}
    })
}
function ProductionInputs {
    $directories = @('src\Sideband\Sideband.Core','src\Sideband\Sideband.Desktop','src\LXMF','src\LXST')
    $files = @(Get-ChildItem $directories -File -Recurse | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
    foreach ($directory in '.') {
        $files += @(Get-ChildItem $directory -File -Force |
            Where-Object { $_.Name -match '^Directory\.|^global\.json$|^version\.json$|^NuGet\.Config$|^\.editorconfig$' })
    }
    @($files | Sort-Object FullName -Unique)
}
function AcceptanceInputs {
    $files = @(Get-ChildItem tests\Sideband.Tests -File -Recurse |
        Where-Object { $_.FullName -notmatch '\\(bin|obj|__pycache__)\\' -and $_.Extension -in '.cs','.csproj','.json','.py' })
    $files += @(Get-Item tests\Interop\PyLinkNode.cs,tests\Interop\live_lxmf_node.py,
        tests\Interop\reference_environment.py,tests\Interop\sideband_propagation_telemetry_node.py,tests\StackProvenance.psm1,$PSCommandPath)
    @($files | Sort-Object FullName -Unique)
}
function AssertHashes($Files) {
    foreach ($file in $Files) {
        if ((Get-FileHash -LiteralPath $file.Path).Hash.ToLowerInvariant() -cne $file.Sha256) {
            throw "Frozen input changed: $($file.Path)"
        }
    }
}
if ($Build) {
    if (Test-Path $Candidate) { throw 'Never replace a frozen candidate; select a new candidate path.' }
    $directory = Split-Path ([IO.Path]::GetFullPath($Candidate)) -Parent
    $null = New-Item $directory -ItemType Directory -Force
    $sources = @(Fingerprints (ProductionInputs))
    $head = (& git rev-parse HEAD)
    @{Head=$head;Sources=$sources} | ConvertTo-Json -Depth 8 |
        Set-Content (Join-Path $directory application-native-inputs-before.json) -Encoding utf8NoBOM
    if (-not $IsWindows) { throw 'This operator verifies Windows NativeAOT; Linux remains unavailable.' }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $install = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $install) { throw 'MSVC tools are required for application NativeAOT acceptance.' }
    $setup = Join-Path $install 'VC\Auxiliary\Build\vcvars64.bat'
    $native = Join-Path $directory application-native
    $env:PATH = (Split-Path $vswhere -Parent) + ';' + $env:PATH
    & $env:ComSpec /c "call `"$setup`" >nul && dotnet publish src\Sideband\Sideband.Desktop\Sideband.Desktop.csproj -c Release -r win-x64 --artifacts-path `"$ArtifactsPath`" -o `"$native`" -p:DebugType=none -p:DebugSymbols=false -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -warnaserror --nologo -v:q" 2>&1 |
        Tee-Object (Join-Path $directory application-publish.log)
    if ($LASTEXITCODE -ne 0) { throw 'Strict full application NativeAOT publish failed.' }
    AssertHashes $sources
    $acceptance = @(Fingerprints (AcceptanceInputs))
    @{Head=$head;Sources=$sources;Acceptance=$acceptance} | ConvertTo-Json -Depth 8 |
        Set-Content (Join-Path $directory application-inputs-before.json) -Encoding utf8NoBOM
    & dotnet build tests\Sideband.Tests\Sideband.Tests.csproj -c Release --artifacts-path $ArtifactsPath `
        -warnaserror --nologo -v:q 2>&1 | Tee-Object (Join-Path $directory application-build.log)
    if ($LASTEXITCODE -ne 0) { throw 'Strict application acceptance build failed.' }
    AssertHashes @($sources + $acceptance)
    if ((& git rev-parse HEAD) -cne $head) { throw 'Versioning HEAD changed during application build.' }
    $reference = (Get-Content $ReferenceCandidate -Raw | ConvertFrom-Json).Reference
    $references = @(Get-Item $reference.DOTRETICULUM_PYTHON)
    foreach ($key in 'DOTRETICULUM_UPSTREAM','DOTRETICULUM_LXST_UPSTREAM','DOTRETICULUM_SIDEBAND_UPSTREAM') {
        $path = $reference.$key
        $references += @(foreach ($file in @(& git -c core.quotepath=false -C $path ls-files)) {
            Get-Item -LiteralPath (Join-Path $path $file)
        })
    }
    $pythonRoot = Split-Path (Split-Path $reference.DOTRETICULUM_PYTHON -Parent) -Parent
    $references += @(Get-ChildItem (Join-Path $pythonRoot 'Lib\site-packages\LXMF') -Recurse -File |
        Where-Object { $_.FullName -notmatch '\\__pycache__\\' })
    $assembly = @(Get-ChildItem (Join-Path $ArtifactsPath bin) -Recurse -Filter Sideband.Tests.dll -File)
    if ($assembly.Count -ne 1) { throw 'Expected one selected managed test assembly.' }
    $assets = @(Get-ChildItem (Join-Path $ArtifactsPath 'obj\Sideband.Desktop') -Recurse -Filter project.assets.json -File)
    if ($assets.Count -ne 1) { throw 'Expected one native assets manifest.' }
    $libraries = (Get-Content $assets[0].FullName -Raw | ConvertFrom-Json).libraries
    $compiler = @($libraries.PSObject.Properties.Name | Where-Object { $_ -match '^Microsoft\.DotNet\.ILCompiler/' })
    if ($compiler.Count -ne 1) { throw 'Expected one identified NativeAOT compiler.' }
    @{Head=$head;Stack=(Get-DotReticulumStackId -Root $root);Sdk=(& dotnet --version);
        Compiler=$compiler[0];Sources=$sources;Acceptance=$acceptance;Reference=$reference;
        ReferenceFiles=@(Fingerprints $references);ArtifactsPath=[IO.Path]::GetFullPath($ArtifactsPath);
        ManagedArtifacts=@(Fingerprints @(Get-ChildItem $assembly[0].DirectoryName -File));
        Binary=@{Path=(Join-Path $native sideband.exe);Sha256=(Get-FileHash (Join-Path $native sideband.exe)).Hash.ToLowerInvariant()};
        Boundary='Dirty work is identified by exact hashes, including desktop UI inputs; HEAD is versioning provenance, not a clean-tree claim.'} |
        ConvertTo-Json -Depth 12 | Set-Content $Candidate -Encoding utf8NoBOM
    return
}
$candidatePath = (Resolve-Path $Candidate).Path
$candidateHash = (Get-FileHash $candidatePath).Hash.ToLowerInvariant()
$frozen = Get-Content $candidatePath -Raw | ConvertFrom-Json
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path $output) { throw 'Select fresh replay output; failed receipts must be preserved.' }
$null = New-Item $output -ItemType Directory
function AssertFrozen {
    if ((Get-FileHash $candidatePath).Hash.ToLowerInvariant() -cne $candidateHash) { throw 'Candidate manifest changed.' }
    if ((& git rev-parse HEAD) -cne $frozen.Head -or (& dotnet --version) -cne $frozen.Sdk) { throw 'HEAD/SDK changed.' }
    if ((Get-DotReticulumStackId -Root $root) -cne $frozen.Stack) {
        throw 'External stack changed.'
    }
    if (Compare-Object @($frozen.Sources.Path) @((ProductionInputs).FullName)) { throw 'Production inventory changed.' }
    if (Compare-Object @($frozen.Acceptance.Path) @((AcceptanceInputs).FullName)) { throw 'Acceptance inventory changed.' }
    AssertHashes @($frozen.Sources + $frozen.Acceptance + $frozen.ReferenceFiles + $frozen.ManagedArtifacts + $frozen.Binary)
}
try {
    AssertFrozen
    Copy-Item $candidatePath (Join-Path $output frozen-inputs.json)
    foreach ($entry in Get-ChildItem Env: | Where-Object { $_.Name -match '^(DOTRETICULUM_|DOTLXST_|DOTSIDEBAND_|PYTHONPATH$|GITHUB_)' }) {
        [Environment]::SetEnvironmentVariable($entry.Name, $null, 'Process')
    }
    foreach ($entry in $frozen.Reference.PSObject.Properties) {
        [Environment]::SetEnvironmentVariable($entry.Name, $entry.Value, 'Process')
    }
    $env:PYTHONPATH = $env:DOTRETICULUM_UPSTREAM + [IO.Path]::PathSeparator + $env:DOTRETICULUM_LXST_UPSTREAM
    $env:PYTHONIOENCODING = 'utf-8'
    $env:DOTRETICULUM_LIVE = '1'
    $env:DOTSIDEBAND_PROPAGATION_LIVE = '1'
    $env:DOTSIDEBAND_PROPAGATION_APPLICATION = '1'
    $env:DOTRETICULUM_SIDEBAND_BINARY = $frozen.Binary.Path
    $env:DOTSIDEBAND_PROPAGATION_APPLICATION_SHA256 = $frozen.Binary.Sha256
    $env:DOTSIDEBAND_PROPAGATION_EVIDENCE = $output
    & $env:DOTRETICULUM_PYTHON tests\Interop\reference_environment.py --require-lxmf --require-lxst 2>&1 |
        Tee-Object (Join-Path $output reference.log)
    if ($LASTEXITCODE -ne 0) { throw 'Actual imported reference verification failed.' }
    & $frozen.Binary.Path --help 2>&1 | Tee-Object (Join-Path $output native-help.log)
    if ($LASTEXITCODE -ne 0) { throw 'Published application help failed.' }
    & dotnet test tests\Sideband.Tests\Sideband.Tests.csproj -c Release --no-build --no-restore --artifacts-path $frozen.ArtifactsPath `
        --filter 'FullyQualifiedName~SidebandPropagationTelemetry|TestId=T920' --nologo -v:q `
        --logger 'trx;LogFileName=application.trx' --results-directory $output 2>&1 | Tee-Object (Join-Path $output tests.log)
    if ($LASTEXITCODE -ne 0) { throw 'Application/provider/reference/UI acceptance failed.' }
    [xml] $trx = Get-Content (Join-Path $output application.trx) -Raw
    $counts = $trx.TestRun.ResultSummary.Counters
    if ($counts.total -ne '30' -or $counts.passed -ne '30' -or $counts.notExecuted -ne '0') {
        throw 'Expected30 executed provider/application/parent UI cases with no skips.'
    }
    AssertFrozen
    @{Passed=$true;CandidateSha256=$candidateHash;NativeSha256=$frozen.Binary.Sha256;SelectedPassed=30;
        ProductionInputs=$frozen.Sources.Count;SharedInputs=@($frozen.Sources | Where-Object Path -Match '\\src\\Shared\\').Count;
        Boundary='Full Windows published Sideband runtime against actual primary lxmd, native/reference status both directions, desktop configuration UI, deterministic provider/lifecycle tests. No Linux/hardware/hop/loss or live five-minute stale-error retention claim.'} |
        ConvertTo-Json -Depth 5 | Set-Content (Join-Path $output result.json) -Encoding utf8NoBOM
}
catch {
    @{Passed=$false;Error=$_.Exception.Message;CandidateSha256=$candidateHash} |
        ConvertTo-Json | Set-Content (Join-Path $output failure.json) -Encoding utf8NoBOM
    throw
}
