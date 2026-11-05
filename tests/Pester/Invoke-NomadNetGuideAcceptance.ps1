#Requires -Version 7.0
[CmdletBinding()]
param(
    [switch] $Build,
    [switch] $Freeze,
    [switch] $Child,
    [string] $Candidate = 'artifacts\tests\nomad-guide\candidate.json',
    [string] $OutputDirectory = ('artifacts\tests\nomad-guide\replay-' + [guid]::NewGuid().ToString('N')),
    [string] $Binary = 'artifacts\tests\nomad-guide\native\nomadnet.exe',
    [string] $ArtifactsPath = 'artifacts\builds\nomad-guide'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $root
$provenancePath = Join-Path (Split-Path $Candidate -Parent) build-provenance.json
if ($Freeze -and (Test-Path $Candidate)) { throw 'Refusing to overwrite an existing frozen candidate.' }
function Get-C102ProductionInputs {
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
function Get-C102AcceptanceInputs {
    $paths = @(Get-ChildItem tests\NomadNet.Tests -Recurse -File |
        Where-Object { $_.Extension -in '.cs','.csproj','.json' -and $_.FullName -notmatch '\\(bin|obj)\\' })
    $paths += @(Get-Item tests\Interop\GenerateNomadNetGuideVectors.py,
        tests\Interop\GenerateNomadNetClientRuntimeVectors.py,tests\Interop\reference_environment.py,
        tests\Pester\NomadNetGuide.Tests.ps1,tests\Pester\Invoke-NomadNetGuideAcceptance.ps1,
        tools\PSScriptAnalyzerSettings.psd1)
    @($paths | Sort-Object FullName -Unique)
}
function ConvertTo-C102Fingerprints($Files) {
    @($Files | Sort-Object FullName -Unique | ForEach-Object {
        @{Path=[IO.Path]::GetRelativePath($root,$_.FullName);Sha256=(Get-FileHash $_.FullName).Hash.ToLowerInvariant()}
    })
}
function Assert-C102Hashes($Files) {
    foreach ($file in $Files) {
        if ((Get-FileHash $file.Path).Hash.ToLowerInvariant() -cne $file.Sha256) { throw "Frozen input changed: $($file.Path)" }
    }
}
if ($Build) {
    $null = New-Item (Split-Path $provenancePath -Parent) -ItemType Directory -Force
    $head = (& git rev-parse HEAD)
    $sources = @(ConvertTo-C102Fingerprints (Get-C102ProductionInputs))
    $acceptance = @(ConvertTo-C102Fingerprints (Get-C102AcceptanceInputs))
    & dotnet build tests\NomadNet.Tests\NomadNet.Tests.csproj -c Release -warnaserror --nologo -v:q `
        --artifacts-path $ArtifactsPath 2>&1 | Tee-Object (Join-Path (Split-Path $provenancePath -Parent) build.log)
    if ($LASTEXITCODE -ne 0) { throw 'Strict Nomad managed build failed.' }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $install = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $install) { throw 'MSVC NativeAOT tools are required for Windows guide acceptance.' }
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
    Assert-C102Hashes @($sources + $acceptance)
    if ((& git rev-parse HEAD) -cne $head) { throw 'Build HEAD changed; do not relabel the binary.' }
    @{Head=$head;Stack=(& git -C external\DotReticulum rev-parse HEAD);Sdk=(& dotnet --version);
        Compiler=($compiler[0] -split '/')[-1];Sources=$sources;AcceptanceSources=$acceptance;Binary=@{Path=$Binary;Sha256=(Get-FileHash $Binary).Hash.ToLowerInvariant()};
        ManagedCommand="dotnet build tests\NomadNet.Tests\NomadNet.Tests.csproj -c Release -warnaserror --artifacts-path $ArtifactsPath";
        NativeCommand="dotnet publish src\NomadNet\NomadNet.csproj -c Release -r win-x64 --artifacts-path $ArtifactsPath -o $nativeDirectory -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -warnaserror";
        Boundary='Dirty guide implementation is frozen by exact hashes; root HEAD is versioning provenance, not a clean-tree claim.'} |
        ConvertTo-Json -Depth 12 | Set-Content $provenancePath -Encoding utf8NoBOM
    if (-not $Freeze) { return }
}
if ($Freeze) {
    $previous = Get-Content artifacts\tests\nomad-editor-state-import\candidate.json -Raw | ConvertFrom-Json
    $provenance = Get-Content $provenancePath -Raw | ConvertFrom-Json
    if (-not $provenance.PSObject.Properties['AcceptanceSources']) { throw 'Rebuild with pre-build acceptance fingerprints before freezing.' }
    if ((& git rev-parse HEAD) -cne $provenance.Head) { throw 'Build HEAD changed; rebuild rather than relabel.' }
    $sources = @(ConvertTo-C102Fingerprints (Get-C102ProductionInputs))
    if (Compare-Object @($provenance.Sources.Path) @($sources.Path)) { throw 'Production input set changed since build.' }
    $acceptance = @(ConvertTo-C102Fingerprints (Get-C102AcceptanceInputs))
    if (Compare-Object @($provenance.AcceptanceSources.Path) @($acceptance.Path)) { throw 'Acceptance input set changed since build.' }
    Assert-C102Hashes @($provenance.Sources + $provenance.AcceptanceSources)
    Assert-C102Hashes @($provenance.Binary)
    $assemblies = @(Get-ChildItem (Join-Path $ArtifactsPath bin) -Recurse -Filter NomadNet.Tests.dll -File)
    if ($assemblies.Count -ne 1) { throw 'Expected one isolated Release Nomad test assembly.' }
    @{
        Claim='C102';Head=$provenance.Head;Stack=$provenance.Stack;Sdk=$provenance.Sdk;Compiler=$provenance.Compiler
        Reference=$previous.Reference
        Sources=$sources;AcceptanceSources=$provenance.AcceptanceSources
        BuildProvenance=$provenancePath;BuildProvenanceSha256=(Get-FileHash $provenancePath).Hash.ToLowerInvariant()
        ArtifactsPath=$ArtifactsPath
        ManagedArtifacts=@(ConvertTo-C102Fingerprints @(Get-ChildItem $assemblies[0].DirectoryName -File |
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
function Assert-C102Frozen {
    if ((& git rev-parse HEAD) -cne $frozen.Head) { throw 'Root versioning commit changed.' }
    if ((& dotnet --version) -cne $frozen.Sdk) { throw 'Selected SDK changed.' }
    if (Compare-Object @($frozen.Sources.Path) @((ConvertTo-C102Fingerprints (Get-C102ProductionInputs)).Path)) {
        throw 'Compile-input file set changed.'
    }
    if (Compare-Object @($frozen.AcceptanceSources.Path) @((ConvertTo-C102Fingerprints (Get-C102AcceptanceInputs)).Path)) {
        throw 'Acceptance-input file set changed.'
    }
    Assert-C102Hashes @(@($frozen.Sources) + @($frozen.AcceptanceSources) + @($frozen.ManagedArtifacts) + @($frozen.Binary))
    if ((Get-FileHash $frozen.BuildProvenance).Hash.ToLowerInvariant() -cne $frozen.BuildProvenanceSha256) { throw 'Build provenance changed.' }
    if ((& git -C external\DotReticulum rev-parse HEAD) -cne $frozen.Stack) { throw 'Stack pin changed.' }
    if (& git -C external\DotReticulum status --porcelain) { throw 'Stack has uncommitted inputs.' }
    foreach ($pair in @(@($frozen.Reference.Primary,$frozen.Reference.PrimaryCommit),
        @($frozen.Reference.Rns,$frozen.Reference.RnsCommit),@($frozen.Reference.Lxmf,$frozen.Reference.LxmfCommit))) {
        if ((& git -C $pair[0] rev-parse HEAD) -cne $pair[1]) { throw "Reference pin changed: $($pair[0])" }
        if (& git -C $pair[0] status --porcelain) { throw "Reference source changed: $($pair[0])" }
    }
}
function Write-C102Snapshot([string] $Path) {
    @{Head=(& git rev-parse HEAD);Stack=(& git -C external\DotReticulum rev-parse HEAD);
        CandidateSha256=(Get-FileHash $candidatePath).Hash.ToLowerInvariant()
        Sources=@(ConvertTo-C102Fingerprints (Get-C102ProductionInputs))
        AcceptanceSources=@(ConvertTo-C102Fingerprints (Get-C102AcceptanceInputs))
        NativeSha256=(Get-FileHash $frozen.Binary.Path).Hash.ToLowerInvariant()
        ManagedArtifacts=@($frozen.ManagedArtifacts | ForEach-Object {
            @{Path=$_.Path;Sha256=(Get-FileHash $_.Path).Hash.ToLowerInvariant()}
        })
    } | ConvertTo-Json -Depth 12 | Set-Content $Path -Encoding utf8NoBOM
}
Assert-C102Frozen
Write-C102Snapshot (Join-Path $output before.json)
$env:DOTRETICULUM_NOMADNET_BINARY = [IO.Path]::GetFullPath($frozen.Binary.Path)
$env:DOTRETICULUM_NOMADNET_GUIDE = '1'
$env:DOTRETICULUM_NOMADNET_GUIDE_CANDIDATE = $candidatePath
$env:DOTRETICULUM_NOMADNET_GUIDE_EVIDENCE = Join-Path $output native
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
    & $env:DOTRETICULUM_PYTHON tests\Interop\GenerateNomadNetGuideVectors.py `
        --output (Join-Path $output vectors.json) --observations (Join-Path $output imports.json)
    if ($LASTEXITCODE -ne 0) { throw 'Actual-primary guide observations failed.' }
    if ((Get-FileHash (Join-Path $output vectors.json)).Hash -cne (Get-FileHash tests\NomadNet.Tests\NomadNetGuideVectors.json).Hash) {
        throw 'Actual-primary vectors changed.'
    }
    $commands.Add(@{Command='GenerateNomadNetGuideVectors.py (verified pinned actual imports)';Result='byte-identical factual vectors'})
    & dotnet test tests\NomadNet.Tests\NomadNet.Tests.csproj -c Release --no-build --no-restore -warnaserror --artifacts-path $frozen.ArtifactsPath `
        --filter 'FullyQualifiedName~NomadNetGuideTests|FullyQualifiedName~NomadNetGuideNativeTests' `
        --logger 'trx;LogFileName=focused.trx' --results-directory $output -v quiet 2>&1 |
        Tee-Object (Join-Path $output focused.log)
    if ($LASTEXITCODE -ne 0) { throw 'Focused guide/native ConPTY suite failed.' }
    [xml] $focused = Get-Content (Join-Path $output focused.trx) -Raw
    if ([int]$focused.TestRun.ResultSummary.Counters.passed -ne 13 -or
        @($focused.TestRun.Results.UnitTestResult | Where-Object outcome -NE Passed).Count -ne 0) { throw 'Focused guide counters must be13/0/0.' }
    $commands.Add(@{Command='dotnet test (frozen --no-build --no-restore -warnaserror; NomadNetGuideTests|NomadNetGuideNativeTests)';Result='13 passed;0 failed;0 skipped'})
    & dotnet test tests\NomadNet.Tests\NomadNet.Tests.csproj -c Release --no-build --no-restore -warnaserror --artifacts-path $frozen.ArtifactsPath `
        --logger 'trx;LogFileName=managed.trx' --results-directory $output -v quiet 2>&1 |
        Tee-Object (Join-Path $output managed.log)
    if ($LASTEXITCODE -ne 0) { throw 'Full Nomad suite failed.' }
    [xml] $trx = Get-Content (Join-Path $output managed.trx) -Raw
    $counts = $trx.TestRun.ResultSummary.Counters
    $skipResults = @($trx.TestRun.Results.UnitTestResult | Where-Object outcome -EQ NotExecuted)
    $skipped = $skipResults.Count
    $expectedSkips = @(
        'NomadNetAttachmentExportTests.RejectsDestinationAndNameLinksAndRecovers'
        'NomadNetAttachmentExportTests.DeniedDirectoryAndSpecialFilesFailWithoutBlockingAndRecover'
        'NomadNetHostedStatisticsNativeTests.T960ActualPrimaryRequestsNativeHostCountAttemptsResetPreservesMediaAndRestartRetainsCounters'
        'NomadNetEditorScrollingNativeTests.ActualConptyMalformedCommandsClampAndPendingCloseRecovery'
        'NomadNetEditorScrollingNativeTests.ActualConptyLargeUnicodeTabSourceTailAndFullSavedText'
        'NomadNetEditorScrollingNativeTests.ActualConptyIndependentScrollRefreshToggleSaveAndRestart'
        'NomadNetEditorPresentationNativeTests.T622ActualConptyPendingCloseCancellationAndFailedControlContinueHealthyService'
        'NomadNetEditorPresentationNativeTests.T621ActualConptyFilteredTreeVisibilitySelectionAndIdentitySurviveRestart'
        'NomadNetEditorPresentationNativeTests.T620ActualConptySourceSyntaxGutterSplitPanesAndDebouncedLatestPreview'
        'NomadNetMicronGrammarNativeTests.T881ActualConptyActivatesPagePreviewConversationRrcAndImageProperties'
        'NomadNetMicronGrammarNativeTests.T870ActualConptyCachedGrammarActionsMalformedRecoveryAndRestart'
        'NomadNetMicronGrammarNativeTests.T879ActualAsciiDecoderAppliesImagePropertiesAndRecoversAfterMalformedBytes'
        'NomadNetTerminalControlNativeTests.T556ActualConptyIntroThemeEveryColorModeAndGlyphSetRender'
        'NomadNetTerminalControlNativeTests.T557ActualConptyMouseClicksSelectVisibleMenuWhileDisabledAndMalformedInputCannotActivate'
        'NomadNetTerminalControlNativeTests.T558ActualConptyConfiguredAnimationAndCancellationRestoreInputThenIdentityRestart'
        'NomadNetNodeProfileTests.NativePeerRemovalPersistsAndDoesNotInventPeersOrEditStaticConfiguration'
        'NomadNetImageTests.AllPrimaryFormatsResizeWithoutUpscalingAndReferenceImagesDecodeForPreview'
        'NomadNetImageTests.HostedConversionsAreAuthorizedNamedCachedRestartedAndBounded'
        'NomadNetFilePickerTests.FifoDevicesAndSymbolicLinksAreRejectedWithoutBlockingOrReading'
        'NomadNetEditorStorageTests.LinuxSymlinksExecutablePreviewAndUnixModes'
        'NomadNetFunctionalTests.ExecutablePagesRequireOptInPassFormEnvironmentAndCancelChildren'
        'NomadNetFunctionalTests.HostedPathsCannotEscapeThroughSymlinks'
        'NomadNetExternalEditorNativeTests.ActualConptyNativeAndSelectedRnsEditsReturnAndRestartWithIdentity'
        'NomadNetExternalEditorNativeTests.ActualConptyInvalidConflictCancellationAndDescendantsRecover'
        'NomadNetPrinterTests.LinuxExitedCommandCannotAbandonItsDescendant'
        'NomadNetProfileImportTests.SymlinkSourceOrAncestorAndSpecialFileAreRejected'
        'NomadNetConversationTests.AttachmentCleanupRejectsLinkedStorageWithoutDeletingItsTarget'
        'NomadNetConversationImageStorageTests.RealConverterPreviewReusesPersistentConversionAndRejectsCorruptCacheThenRecovers'
        'NomadNetConversationImageStorageTests.AttachmentAndConversionCacheDirectoryLinksAreRejected'
        'NomadNetConversationImageStorageTests.UntrustedEncodedInputCannotOpenNetworkProtocols'
        'NomadNetRemoteMediaCacheTests.LinkedStorageIsRejectedBeforeCreatingAnythingOutsideTheProfile'
        'NomadNetEditorStateImportNativeTests.T741ActualConptyImportsPrimarySelectionPreferencesEditsSavesAndRestarts'
        'NomadNetEditorStateImportNativeTests.T742ActualConptyCurrentDirtyConfigurationOwnershipAndFailedControlRecoverWithoutImportDrafts'
    )
    $unexpectedOutcomes = @($trx.TestRun.Results.UnitTestResult | Where-Object outcome -NotIn Passed,NotExecuted)
    if ([int]$counts.failed -ne 0 -or [int]$counts.passed -ne 493 -or $unexpectedOutcomes.Count -ne 0 -or
        $skipped -ne $expectedSkips.Count -or
        (Compare-Object $expectedSkips @($skipResults | ForEach-Object { $_.testName -replace '^NomadNet\.Tests\.', '' })) -or
        @($skipResults | Where-Object { [string]::IsNullOrWhiteSpace($_.Output.ErrorInfo.Message) }).Count -ne 0) {
        throw 'Full-suite outcomes differ from the explicit Windows platform/separately gated acceptance inventory.'
    }
    @($skipResults | ForEach-Object { @{Test=$_.testName;Reason=[string]$_.Output.ErrorInfo.Message} }) |
        ConvertTo-Json -Depth 4 | Set-Content (Join-Path $output explicit-skips.json) -Encoding utf8NoBOM
    $commands.Add(@{Command="dotnet test tests\NomadNet.Tests\NomadNet.Tests.csproj -c Release --no-build --no-restore -warnaserror --artifacts-path $($frozen.ArtifactsPath)";
        Result="$($counts.passed) passed;$skipped explicit skips;0 failures; includes two C102 actual ConPTY flows"})
    $analysis = @(foreach ($path in 'tests\Pester\NomadNetGuide.Tests.ps1','tests\Pester\Invoke-NomadNetGuideAcceptance.ps1') {
        Invoke-ScriptAnalyzer -Path $path -Settings tools\PSScriptAnalyzerSettings.psd1
    })
    $analysis | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $output analysis.json)
    if ($analysis.Count) { throw 'Configured PowerShell analysis failed.' }
    $commands.Add(@{Command='Invoke-ScriptAnalyzer (guide Pester and frozen replay operator) -Settings tools\PSScriptAnalyzerSettings.psd1';Result='zero diagnostics'})
    $env:DOTRETICULUM_NOMADNET_GUIDE_EVIDENCE = Join-Path $output pester
    $config = New-PesterConfiguration
    $config.Run.Path = 'tests\Pester\NomadNetGuide.Tests.ps1'
    $config.Run.PassThru = $true
    $config.Output.Verbosity = 'Detailed'
    $config.TestResult.Enabled = $true
    $config.TestResult.OutputPath = Join-Path $output pester.xml
    $result = Invoke-Pester -Configuration $config
    if ($result.PassedCount -ne 4 -or $result.FailedCount -ne 0 -or $result.SkippedCount -ne 0) { throw 'Guide Pester counters must be4/0/0.' }
    $commands.Add(@{Command='Invoke-Pester tests\Pester\NomadNetGuide.Tests.ps1';Result='4 passed;0 failed;0 skipped'})
    @{Claim='C102';Head=$frozen.Head;Binary=$frozen.Binary;Commands=$commands;
        Boundary='Windows headless actual primary guide reader with disclosed singleton/screen/alarm and unresolved-demo-image seams; original native ConPTY and ordinary guide/local-authoring/hosting/restart. No new wire, fullscreen primary, hardware or Linux acceptance.'} |
        ConvertTo-Json -Depth 12 | Set-Content (Join-Path $output result.json) -Encoding utf8NoBOM
} finally {
    Assert-C102Frozen
    Write-C102Snapshot (Join-Path $output after.json)
    $before = Get-Content (Join-Path $output before.json) -Raw | ConvertFrom-Json
    $after = Get-Content (Join-Path $output after.json) -Raw | ConvertFrom-Json
    foreach ($property in 'Head','Stack','CandidateSha256','NativeSha256') {
        if ($before.$property -cne $after.$property) { throw "Snapshot changed: $property" }
    }
    foreach ($property in 'Sources','AcceptanceSources','ManagedArtifacts') {
        if (Compare-Object @($before.$property | ForEach-Object { "$($_.Path):$($_.Sha256)" }) `
            @($after.$property | ForEach-Object { "$($_.Path):$($_.Sha256)" })) { throw "Snapshot changed: $property" }
    }
    @{Before=(Get-FileHash (Join-Path $output before.json)).Hash.ToLowerInvariant();
        After=(Get-FileHash (Join-Path $output after.json)).Hash.ToLowerInvariant();
        InputHashesUnchanged=$true} | ConvertTo-Json | Set-Content (Join-Path $output snapshot-hashes.json)
}
