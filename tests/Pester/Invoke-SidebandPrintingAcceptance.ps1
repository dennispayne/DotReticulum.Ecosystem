#Requires -Version 7.0
[CmdletBinding()]
param(
    [switch] $Build,
    [switch] $Freeze,
    [switch] $Child,
    [string] $Candidate = 'artifacts\tests\sideband-printing\candidate.json',
    [string] $OutputDirectory = ('artifacts\tests\sideband-printing\replay-' + [guid]::NewGuid().ToString('N')),
    [string] $ArtifactsPath = 'artifacts\builds\sideband-printing',
    [string] $Binary = 'artifacts\tests\sideband-printing\native\sideband.exe'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $root
$candidateDirectory = Split-Path ([IO.Path]::GetFullPath($Candidate)) -Parent
$buildManifest = Join-Path $candidateDirectory build-provenance.json
function Get-ScopedSources {
    $files = @(Get-ChildItem src\Sideband,src\Sideband.Desktop,src\LXMF,src\LXST,src\Shared,
        src\NomadNet,external\DotReticulum\src -Recurse -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
    foreach ($directory in '.', 'external\DotReticulum') {
        $files += @(Get-ChildItem $directory -File -Force |
            Where-Object { $_.Name -match '^Directory\.|^global\.json$|^version\.json$|^NuGet\.Config$|^\.editorconfig$' })
    }
    @($files | Sort-Object FullName -Unique)
}
function Get-TestInputs {
    $files = @(Get-ChildItem tests\Sideband.Tests,tests\NomadNet.Tests -Recurse -File |
        Where-Object { $_.Extension -in '.cs','.csproj','.json','.py' -and $_.FullName -notmatch '\\(bin|obj|__pycache__)\\' })
    $files += @(Get-Item tests\Pester\SidebandPrinting.Tests.ps1,tests\Pester\NomadNetPrinterStub.ps1,
        tests\Pester\Invoke-SidebandPrintingAcceptance.ps1,tests\Interop\reference_environment.py,
        tests\Interop\decode_paper_qr.py,tools\PSScriptAnalyzerSettings.psd1,.gitattributes)
    @($files | Sort-Object FullName -Unique)
}
function Get-Hashes($Files) {
    @($Files | Sort-Object FullName -Unique | ForEach-Object {
        @{ Path = $_.FullName; Sha256 = (Get-FileHash $_.FullName).Hash.ToLowerInvariant() }
    })
}
function Assert-Hashes($Files) {
    foreach ($file in $Files) {
        if ((Get-FileHash $file.Path).Hash.ToLowerInvariant() -cne $file.Sha256) {
            throw "Frozen input changed: $($file.Path)"
        }
    }
}
function Assert-SourceSet($Files) {
    Assert-Hashes $Files
    $current = @(Get-ScopedSources | ForEach-Object FullName)
    if (($current -join "`n") -cne ((@($Files | ForEach-Object Path) | Sort-Object) -join "`n")) {
        throw 'Compiled production input file set changed.'
    }
}
function Get-ReferenceInputs {
    if (-not $env:DOTRETICULUM_PYTHON -or -not $env:DOTRETICULUM_UPSTREAM) {
        throw 'Actual pinned DOTRETICULUM_PYTHON and DOTRETICULUM_UPSTREAM are required.'
    }
    $venv = Split-Path (Split-Path $env:DOTRETICULUM_PYTHON -Parent) -Parent
    $site = Join-Path $venv 'Lib\site-packages'
    $files = @(Get-Item $env:DOTRETICULUM_PYTHON)
    $files += @(Get-Item artifacts\builds\fleet-sideband-reference\sbapp\ui\messages.py,
        artifacts\builds\fleet-sideband-reference\sbapp\sideband\core.py,
        artifacts\builds\fleet-sideband-reference\sbapp\main.py,
        artifacts\builds\fleet-sideband-reference\sbapp\ui\layouts.py)
    $files += @(Get-ChildItem (Join-Path $env:DOTRETICULUM_UPSTREAM RNS),
        (Join-Path $site LXMF),(Join-Path $site qrcode),(Join-Path $site PIL) -Recurse -File |
        Where-Object { $_.Extension -in '.py','.pyd','.dll' })
    $files += @(Get-ChildItem $site -File -Filter 'zxingcpp*.pyd')
    $files += @(Get-ChildItem $site -Directory | Where-Object Name -Match '^(qrcode|pillow|zxing_cpp)-.*\.dist-info$' |
        ForEach-Object { Get-Item (Join-Path $_.FullName METADATA) })
    if (@($files | Where-Object Name -Like 'zxingcpp*.pyd').Count -ne 1) {
        throw 'Selected isolated independent QR decoding oracle is missing.'
    }
    Get-Hashes $files
}
function Invoke-PrimaryObservation([string] $ObservationPath, [string] $Workspace) {
    & $env:DOTRETICULUM_PYTHON tests\Sideband.Tests\GeneratePrintingVectors.py `
        --source artifacts\builds\fleet-sideband-reference --output $ObservationPath `
        --software-directory $Workspace --sink tests\Pester\NomadNetPrinterStub.ps1 --pwsh (Get-Command pwsh).Source
    if ($LASTEXITCODE -ne 0) { throw 'Actual primary printing callback/reference verification failed.' }
    $observation = Get-Content $ObservationPath -Raw | ConvertFrom-Json
    $decoded = & $env:DOTRETICULUM_PYTHON tests\Interop\decode_paper_qr.py `
        $observation.software_observation.receipt.copy
    if ($LASTEXITCODE -ne 0 -or $decoded -cne $observation.software_observation.uri) {
        throw 'Independent decoder did not reproduce the actual primary printed URI.'
    }
}
if ($Build) {
    if (-not $IsWindows) { throw 'This candidate is Windows-only; current Linux execution is unavailable.' }
    $null = New-Item $candidateDirectory -ItemType Directory -Force
    $sources = Get-Hashes (Get-ScopedSources)
    $tests = Get-Hashes (Get-TestInputs)
    foreach ($project in 'Sideband','NomadNet') {
        & dotnet build "tests\$project.Tests\$project.Tests.csproj" -c Release -warnaserror `
            --artifacts-path $ArtifactsPath --nologo -v:q 2>&1 |
            Tee-Object (Join-Path $candidateDirectory "$project-build.log")
        if ($LASTEXITCODE -ne 0) { throw "Strict $project managed build failed." }
    }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $install = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $install) { throw 'Windows MSVC tools are required for native acceptance.' }
    $setup = Join-Path $install 'VC\Auxiliary\Build\vcvars64.bat'
    $env:PATH = (Split-Path $vswhere -Parent) + ';' + $env:PATH
    $nativeDirectory = Split-Path ([IO.Path]::GetFullPath($Binary)) -Parent
    & $env:ComSpec /c "call `"$setup`" >nul && dotnet publish src\Sideband.Desktop\Sideband.Desktop.csproj -c Release -r win-x64 --artifacts-path `"$ArtifactsPath`" -o `"$nativeDirectory`" -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -warnaserror --nologo -v:q" 2>&1 |
        Tee-Object (Join-Path $candidateDirectory publish.log)
    if ($LASTEXITCODE -ne 0) { throw 'Strict Windows NativeAOT publish failed.' }
    Assert-SourceSet $sources
    Assert-Hashes $tests
    @{
        Sources = $sources; Tests = $tests; Head = (& git rev-parse HEAD); Stack = (& git -C external\DotReticulum rev-parse HEAD)
        Dotnet = (& dotnet --info | Out-String); Binary = [IO.Path]::GetFullPath($Binary)
        SidebandTests = [IO.Path]::GetFullPath((Join-Path $ArtifactsPath 'bin\Sideband.Tests\release\Sideband.Tests.dll'))
        NomadNetTests = [IO.Path]::GetFullPath((Join-Path $ArtifactsPath 'bin\NomadNet.Tests\release\NomadNet.Tests.dll'))
        Built = [DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json -Depth 20 | Set-Content $buildManifest -Encoding utf8NoBOM
}
if ($Freeze) {
    $manifest = Get-Content $buildManifest -Raw | ConvertFrom-Json
    Assert-SourceSet $manifest.Sources
    Assert-Hashes $manifest.Tests
    $references = Get-ReferenceInputs
    $env:PYTHONPATH = $env:DOTRETICULUM_UPSTREAM
    $primaryDirectory = Join-Path $candidateDirectory ('primary-' + [guid]::NewGuid().ToString('N'))
    $primaryOutput = Join-Path $candidateDirectory primary-software-frozen.json
    Invoke-PrimaryObservation $primaryOutput $primaryDirectory
    Assert-Hashes $references
    $executables = @(Get-ChildItem (Split-Path $manifest.Binary -Parent),
        (Split-Path $manifest.SidebandTests -Parent),(Split-Path $manifest.NomadNetTests -Parent) -Recurse -File)
    $manifest | Add-Member Binaries (Get-Hashes $executables) -Force
    $manifest | Add-Member References $references -Force
    $oracle = @(Get-Item $primaryOutput) + @(Get-ChildItem $primaryDirectory -Recurse -File)
    $manifest | Add-Member Oracle (Get-Hashes $oracle) -Force
    $manifest | Add-Member Reference @{
        DOTRETICULUM_PYTHON = $env:DOTRETICULUM_PYTHON; DOTRETICULUM_UPSTREAM = $env:DOTRETICULUM_UPSTREAM
    } -Force
    $manifest | Add-Member Scope 'C108 Windows software printing only; no physical-printer, primary GUI, Linux execution or live delivery claim.' -Force
    $manifest | ConvertTo-Json -Depth 30 | Set-Content $Candidate -Encoding utf8NoBOM
}
if ($Build -or $Freeze) { return }
if (-not $Child) {
    & pwsh -NoProfile -File $PSCommandPath -Child -Candidate $Candidate -OutputDirectory $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Fresh-child frozen printing replay failed.' }
    return
}
$frozen = Get-Content $Candidate -Raw | ConvertFrom-Json
Assert-SourceSet $frozen.Sources
Assert-Hashes $frozen.Tests
Assert-Hashes $frozen.Binaries
Assert-Hashes $frozen.References
Assert-Hashes $frozen.Oracle
$null = New-Item $OutputDirectory -ItemType Directory -ErrorAction Stop
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
foreach ($key in $frozen.Reference.PSObject.Properties.Name) {
    [Environment]::SetEnvironmentVariable($key, $frozen.Reference.$key)
}
$env:PYTHONPATH = $env:DOTRETICULUM_UPSTREAM
$env:DOTRETICULUM_NOMADNET_PEER_CONTROLS = '0'
Invoke-PrimaryObservation (Join-Path $outputPath primary-replay.json) (Join-Path $outputPath primary-software)
$results = @()
foreach ($suite in @(
    @{ Name='sideband-printing'; Dll=$frozen.SidebandTests; Filter='FullyQualifiedName~SidebandPrinting|TestId=T1006|TestId=T1007|TestId=T1015' },
    @{ Name='shared-printing'; Dll=$frozen.NomadNetTests; Filter='FullyQualifiedName~Printing|FullyQualifiedName~Printer' },
    @{ Name='sideband-full'; Dll=$frozen.SidebandTests; Filter='FullyQualifiedName~Sideband.Tests' },
    @{ Name='nomadnet-full'; Dll=$frozen.NomadNetTests; Filter='FullyQualifiedName~NomadNet.Tests' }
)) {
    & dotnet test $suite.Dll --filter $suite.Filter --logger "trx;LogFileName=$($suite.Name).trx" `
        --results-directory $outputPath --logger 'console;verbosity=normal' 2>&1 |
        Tee-Object (Join-Path $outputPath "$($suite.Name).log")
    if ($LASTEXITCODE -ne 0) { throw "Frozen managed suite failed: $($suite.Name)" }
    [xml] $trx = Get-Content (Join-Path $outputPath "$($suite.Name).trx") -Raw
    $counters = $trx.TestRun.ResultSummary.Counters
    $outcomes = @($trx.TestRun.Results.UnitTestResult)
    $skips = @($outcomes | Where-Object outcome -EQ NotExecuted)
    if ($outcomes.Count -eq 0 -or $outcomes.Count -ne [int]$counters.total -or
        @($outcomes | Where-Object outcome -NotIn Passed,NotExecuted).Count -ne 0) {
        throw "Frozen suite has missing or unsuccessful outcomes: $($suite.Name)"
    }
    if ($suite.Name -eq 'sideband-printing' -and ([int]$counters.passed -ne 16 -or $skips.Count -ne 0)) {
        throw 'Selected printing acceptance must pass all16 cases without skips.'
    }
    @($skips | ForEach-Object { @{Test=$_.testName;Reason=[string]$_.Output.ErrorInfo.Message} }) |
        ConvertTo-Json -Depth 4 | Set-Content (Join-Path $outputPath "$($suite.Name)-explicit-skips.json") -Encoding utf8NoBOM
    $results += @{
        Suite=$suite.Name; Result='passed'; Total=[int]$counters.total
        Passed=[int]$counters.passed; Failed=[int]$counters.failed; NotExecuted=$skips.Count
    }
}
$env:DOTRETICULUM_PRINTING_BINARY = $frozen.Binary
$env:DOTRETICULUM_PRINTING_WORKSPACE = Join-Path $outputPath native-software
$configuration = New-PesterConfiguration
$configuration.Run.Path = Join-Path $PSScriptRoot SidebandPrinting.Tests.ps1
$configuration.Run.PassThru = $true
$configuration.Output.Verbosity = 'Detailed'
$configuration.TestResult.Enabled = $true
$configuration.TestResult.OutputPath = Join-Path $outputPath pester.xml
$pester = Invoke-Pester -Configuration $configuration
if ($pester.FailedCount -ne 0 -or $pester.SkippedCount -ne 0 -or $pester.PassedCount -ne 5) {
    throw "Native private-process Pester gate failed: passed=$($pester.PassedCount), failed=$($pester.FailedCount), skipped=$($pester.SkippedCount)"
}
Assert-SourceSet $frozen.Sources
Assert-Hashes $frozen.Tests
Assert-Hashes $frozen.Binaries
Assert-Hashes $frozen.References
Assert-Hashes $frozen.Oracle
@{
    Claim='C108'; Candidate=[IO.Path]::GetFullPath($Candidate); CandidateSha256=(Get-FileHash $Candidate).Hash.ToLowerInvariant()
    ChildPid=$PID; Time=[DateTimeOffset]::UtcNow.ToString('O'); Managed=$results
    Pester=@{ Passed=$pester.PassedCount; Failed=$pester.FailedCount; Skipped=$pester.SkippedCount }
    Scope=$frozen.Scope; Status='producer replay passed; parent independent acceptance still required'
    NomadNativePeerControls='Explicitly not enabled: this printing candidate contains no NomadNet native binary. T1052 must skip with its declared reason; separate C104/parent native acceptance owns that capability.'
} | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $outputPath handoff.json) -Encoding utf8NoBOM
