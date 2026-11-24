#Requires -Version 7.0
[CmdletBinding()]
param(
    [switch] $Freeze,
    [switch] $Child,
    [string] $Candidate = 'artifacts\tests\nomad-editor-state-import\candidate.json',
    [string] $OutputDirectory = ('artifacts\tests\nomad-editor-state-import\replay-' + [guid]::NewGuid().ToString('N')),
    [string] $Binary = 'artifacts\tests\nomad-editor-state-import\native\nomadnet.exe',
    [string] $ArtifactsPath = 'artifacts\builds\nomad-editor-state-import'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $root
Import-Module (Join-Path $root 'tests\StackProvenance.psm1') -Force
function Get-C100ProductionInputs {
    $paths = @(Get-ChildItem src\NomadNet,src\LXMF -Recurse -File |
        Where-Object { $_.Extension -in '.cs','.csproj','.props','.targets','.json','.txt' -and $_.FullName -notmatch '\\(bin|obj)\\' })
    foreach ($directory in '.') {
        $paths += @(Get-ChildItem $directory -File -Force |
            Where-Object { $_.Name -match '^Directory\.|^global.json$|^version.json$|^NuGet.Config$|^.editorconfig$' })
    }
    @($paths | Sort-Object FullName -Unique)
}
function Get-C100AcceptanceInputs {
    $paths = @(Get-ChildItem tests\NomadNet.Tests -Recurse -File |
        Where-Object { $_.Extension -in '.cs','.csproj','.json' -and $_.FullName -notmatch '\\(bin|obj)\\' })
    $paths += @(Get-Item tests\Interop\GenerateNomadNetEditorStateImportVectors.py,
        tests\Interop\GenerateNomadNetClientRuntimeVectors.py,tests\Interop\reference_environment.py,
        tests\Pester\NomadNetEditorStateImport.Tests.ps1,tests\Pester\Invoke-NomadNetEditorStateImportAcceptance.ps1,tests\StackProvenance.psm1,
        docs\nomadnet.md,tools\PSScriptAnalyzerSettings.psd1)
    @($paths | Sort-Object FullName -Unique)
}
function ConvertTo-C100Fingerprints($Files) {
    @($Files | Sort-Object FullName -Unique | ForEach-Object {
        @{Path=[IO.Path]::GetRelativePath($root,$_.FullName);Sha256=(Get-FileHash $_.FullName).Hash.ToLowerInvariant()}
    })
}
if ($Freeze) {
    $previous = Get-Content artifacts\tests\nomad-editor-scrolling\candidate.json -Raw | ConvertFrom-Json
    $provenance = Get-Content artifacts\tests\nomad-editor-state-import\build-provenance.json -Raw | ConvertFrom-Json
    if ((& git rev-parse HEAD) -cne $provenance.Head) { throw 'Build HEAD changed; rebuild rather than relabel the old native artifact.' }
    if ((Get-DotReticulumStackId -Root $root) -cne $provenance.Stack) {
        throw 'Build provenance Stack does not match the restored stack package identity (record Get-DotReticulumStackId at build); rebuild rather than relabel.'
    }
    $sources = @(ConvertTo-C100Fingerprints (Get-C100ProductionInputs))
    if (Compare-Object @($provenance.Sources.Path) @($sources.Path)) { throw 'Production input set changed since build.' }
    foreach ($source in $provenance.Sources) {
        if ((Get-FileHash $source.Path).Hash.ToLowerInvariant() -cne $source.Sha256) { throw "Production input changed since build: $($source.Path)" }
    }
    $assemblies = @(Get-ChildItem (Join-Path $ArtifactsPath bin) -Recurse -Filter NomadNet.Tests.dll -File)
    if ($assemblies.Count -ne 1) { throw 'Expected one isolated Release Nomad test assembly.' }
    @{
        Claim='C100';Head=$provenance.Head;Stack=(Get-DotReticulumStackId -Root $root)
        Sdk=(& dotnet --version);Compiler='10.0.14';Reference=$previous.Reference
        Sources=$sources;AcceptanceSources=@(ConvertTo-C100Fingerprints (Get-C100AcceptanceInputs))
        BuildProvenance='artifacts\tests\nomad-editor-state-import\build-provenance.json'
        BuildProvenanceSha256=(Get-FileHash artifacts\tests\nomad-editor-state-import\build-provenance.json).Hash.ToLowerInvariant()
        ArtifactsPath=$ArtifactsPath
        ManagedArtifacts=@(ConvertTo-C100Fingerprints @(Get-ChildItem $assemblies[0].DirectoryName -File |
            Where-Object Extension -In '.dll','.json','.pdb','.exe'))
        Binary=@{Path=$Binary;Sha256=(Get-FileHash $Binary).Hash.ToLowerInvariant()}
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
function Assert-C100Frozen {
    if ((& git rev-parse HEAD) -cne $frozen.Head) { throw 'Root versioning commit changed.' }
    if ((& dotnet --version) -cne $frozen.Sdk) { throw 'Selected SDK changed.' }
    if (Compare-Object @($frozen.Sources.Path) @((ConvertTo-C100Fingerprints (Get-C100ProductionInputs)).Path)) {
        throw 'Compile-input file set changed.'
    }
    if (Compare-Object @($frozen.AcceptanceSources.Path) @((ConvertTo-C100Fingerprints (Get-C100AcceptanceInputs)).Path)) {
        throw 'Acceptance-input file set changed.'
    }
    foreach ($source in @($frozen.Sources) + @($frozen.AcceptanceSources) + @($frozen.ManagedArtifacts)) {
        if ((Get-FileHash $source.Path).Hash.ToLowerInvariant() -cne $source.Sha256) { throw "Frozen input changed: $($source.Path)" }
    }
    if ((Get-FileHash $frozen.Binary.Path).Hash.ToLowerInvariant() -cne $frozen.Binary.Sha256) { throw 'Native hash changed.' }
    if ((Get-FileHash $frozen.BuildProvenance).Hash.ToLowerInvariant() -cne $frozen.BuildProvenanceSha256) { throw 'Build provenance changed.' }
    if ((Get-DotReticulumStackId -Root $root) -cne $frozen.Stack) { throw 'Stack package identity changed.' }
    foreach ($pair in @(@($frozen.Reference.Primary,$frozen.Reference.PrimaryCommit),
        @($frozen.Reference.Rns,$frozen.Reference.RnsCommit),@($frozen.Reference.Lxmf,$frozen.Reference.LxmfCommit))) {
        if ((& git -C $pair[0] rev-parse HEAD) -cne $pair[1]) { throw "Reference pin changed: $($pair[0])" }
        if (& git -C $pair[0] status --porcelain) { throw "Reference source changed: $($pair[0])" }
    }
}
Assert-C100Frozen
Copy-Item $candidatePath (Join-Path $output before.json)
$env:DOTRETICULUM_NOMADNET_BINARY = [IO.Path]::GetFullPath($frozen.Binary.Path)
$env:DOTRETICULUM_NOMADNET_EDITOR_STATE_IMPORT = '1'
$env:DOTRETICULUM_NOMADNET_STATE_CANDIDATE = $candidatePath
$env:DOTRETICULUM_NOMADNET_STATE_EVIDENCE = Join-Path $output native
$env:DOTRETICULUM_PYTHON = $frozen.Reference.Python
$env:DOTRETICULUM_UPSTREAM = $frozen.Reference.Rns
$env:DOTRETICULUM_LXMF_UPSTREAM = $frozen.Reference.Lxmf
$env:DOTRETICULUM_NOMADNET_SOURCE = $frozen.Reference.Primary
$env:PYTHONPATH = "$($frozen.Reference.Primary);$($frozen.Reference.Rns);$($frozen.Reference.Lxmf)"
$env:DOTRETICULUM_LIVE = '0'
$env:DOTRETICULUM_NOMADNET_LIVE = '0'
$env:TEMP = $output
$env:TMP = $output
$env:TMPDIR = $output
$commands = [Collections.Generic.List[object]]::new()
try {
    Import-Module Pester -MinimumVersion 5.7.1
    & $env:DOTRETICULUM_NOMADNET_BINARY --help | Set-Content (Join-Path $output native-help.txt)
    if ($LASTEXITCODE -ne 0) { throw 'Native help failed.' }
    & $env:DOTRETICULUM_NOMADNET_BINARY --version | Set-Content (Join-Path $output native-version.txt)
    if ($LASTEXITCODE -ne 0) { throw 'Native version failed.' }
    $commands.Add(@{Command="$($frozen.Binary.Path) --help; --version";Result='passed'})
    & $env:DOTRETICULUM_PYTHON tests\Interop\GenerateNomadNetEditorStateImportVectors.py `
        --work (Join-Path $output primary) --output (Join-Path $output vectors.json) `
        --observations (Join-Path $output imports.json)
    if ($LASTEXITCODE -ne 0) { throw 'Actual-primary editor observations failed.' }
    if ((Get-FileHash (Join-Path $output vectors.json)).Hash -cne (Get-FileHash tests\NomadNet.Tests\NomadNetEditorStateImportVectors.json).Hash) {
        throw 'Actual-primary vectors changed.'
    }
    $env:DOTRETICULUM_NOMADNET_STATE_PRIMARY = Join-Path $output 'primary\saved'
    $commands.Add(@{Command='GenerateNomadNetEditorStateImportVectors.py (verified pinned actual imports)';Result='byte-identical factual vectors'})
    & dotnet test tests\NomadNet.Tests\NomadNet.Tests.csproj -c Release --no-build --no-restore -warnaserror --artifacts-path $frozen.ArtifactsPath `
        --logger 'trx;LogFileName=managed.trx' --results-directory $output -v quiet 2>&1 |
        Tee-Object (Join-Path $output managed.log)
    if ($LASTEXITCODE -ne 0) { throw 'Managed/full Nomad/native ConPTY suite failed.' }
    [xml] $trx = Get-Content (Join-Path $output managed.trx) -Raw
    $counts = $trx.TestRun.ResultSummary.Counters
    $skipped = @($trx.TestRun.Results.UnitTestResult | Where-Object outcome -EQ NotExecuted).Count
    if ([int]$counts.failed -ne 0 -or [int]$counts.passed -lt 440 -or $skipped -gt 26) { throw 'Unexpected full-suite counters.' }
    $commands.Add(@{Command="dotnet test tests\NomadNet.Tests\NomadNet.Tests.csproj -c Release --no-build --no-restore -warnaserror --artifacts-path $($frozen.ArtifactsPath)";
        Result="$($counts.passed) passed;$skipped explicit skips;0 failures; includes two C100 actual ConPTY flows"})
    $analysis = @(foreach ($path in 'tests\Pester\NomadNetEditorStateImport.Tests.ps1',
        'tests\Pester\Invoke-NomadNetEditorStateImportAcceptance.ps1') {
        Invoke-ScriptAnalyzer -Path $path -Settings tools\PSScriptAnalyzerSettings.psd1
    })
    $analysis | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $output analysis.json)
    if ($analysis.Count) { throw 'Configured PowerShell analysis failed.' }
    $commands.Add(@{Command='Invoke-ScriptAnalyzer (new Pester and frozen replay operator) -Settings tools\PSScriptAnalyzerSettings.psd1';Result='zero diagnostics'})
    $env:DOTRETICULUM_NOMADNET_STATE_EVIDENCE = Join-Path $output pester
    $config = New-PesterConfiguration
    $config.Run.Path = 'tests\Pester\NomadNetEditorStateImport.Tests.ps1'
    $config.Run.PassThru = $true
    $config.Output.Verbosity = 'Detailed'
    $config.TestResult.Enabled = $true
    $config.TestResult.OutputPath = Join-Path $output pester.xml
    $result = Invoke-Pester -Configuration $config
    if ($result.PassedCount -ne 5 -or $result.FailedCount -ne 0 -or $result.SkippedCount -ne 0) { throw 'New Pester counters failed.' }
    $commands.Add(@{Command='Invoke-Pester tests\Pester\NomadNetEditorStateImport.Tests.ps1';Result='5 passed;0 failed;0 skipped'})
    @{Claim='C100';Head=$frozen.Head;Binary=$frozen.Binary;Commands=$commands;
        Boundary='Windows headless actual primary editor constructors/persistence plus native profile-import and original ConPTY editor. Not full primary interactive GUI or hardware. Linux explicitly blocked by WSL EIO.'} |
        ConvertTo-Json -Depth 12 | Set-Content (Join-Path $output result.json) -Encoding utf8NoBOM
} finally {
    Assert-C100Frozen
    Copy-Item $candidatePath (Join-Path $output after.json)
    if ((Get-FileHash (Join-Path $output before.json)).Hash -cne (Get-FileHash (Join-Path $output after.json)).Hash) { throw 'Candidate changed during replay.' }
}
