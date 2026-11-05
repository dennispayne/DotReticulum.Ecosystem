#Requires -Version 7.0
[CmdletBinding()]
param(
    [switch] $Build,
    [switch] $Freeze,
    [switch] $Child,
    [string] $Candidate = 'artifacts\tests\nomad-micron-contracts\final-candidate.json',
    [string] $OutputDirectory = ('artifacts\tests\nomad-micron-contracts\replay-' + [guid]::NewGuid().ToString('N')),
    [string] $ArtifactsPath = 'artifacts\builds\nomad-micron-contracts',
    [string] $Binary = 'artifacts\tests\nomad-micron-contracts\final-native\nomadnet.exe'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $root
$evidenceRoot = Join-Path $root 'artifacts\tests\nomad-micron-contracts'
$null = New-Item $evidenceRoot -ItemType Directory -Force
$provenancePath = [IO.Path]::ChangeExtension([IO.Path]::GetFullPath($Candidate), 'build-provenance.json')
if (($Build -or $Freeze) -and (Test-Path $Candidate)) {
    throw 'Candidate already exists. Preserve it and select new Candidate/Binary/ArtifactsPath paths for intentional changed-input acceptance.'
}
function Get-C104ProductionInputs {
    $files = @(Get-ChildItem src\NomadNet,src\LXMF,external\DotReticulum\src -Recurse -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
    if (Test-Path src\Shared) {
        $files += @(Get-ChildItem src\Shared -Recurse -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
    }
    foreach ($directory in '.','external\DotReticulum') {
        $files += @(Get-ChildItem $directory -File -Force |
            Where-Object { $_.Name -match '^Directory\.|^global.json$|^version.json$|^NuGet.Config$|^.editorconfig$' })
    }
    @($files | Sort-Object FullName -Unique)
}
function Get-C104AcceptanceInputs {
    $files = @(Get-ChildItem tests\NomadNet.Tests -Recurse -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Extension -in '.cs','.csproj','.json','.py' })
    $files += @(Get-Item tests\Interop\GenerateNomadNetMicronGrammarVectors.py,
        tests\Interop\GenerateNomadNetEditorStateImportVectors.py,
        tests\Interop\GenerateNomadNetClientRuntimeVectors.py,tests\Interop\reference_environment.py,
        tests\Pester\NomadNetMicronGrammar.Tests.ps1,tests\Pester\Invoke-NomadNetMicronGrammarAcceptance.ps1,
        tools\PSScriptAnalyzerSettings.psd1)
    @($files | Sort-Object FullName -Unique)
}
function ConvertTo-C104Hashes($Files) {
    @($Files | Sort-Object FullName -Unique | ForEach-Object {
        @{Path=[IO.Path]::GetRelativePath($root,$_.FullName);Sha256=(Get-FileHash $_.FullName).Hash.ToLowerInvariant()}
    })
}
function Assert-C104Hashes($Files) {
    foreach ($file in $Files) {
        if ((Get-FileHash $file.Path).Hash.ToLowerInvariant() -cne $file.Sha256) { throw "Frozen input changed: $($file.Path)" }
    }
}
function Get-C104ReferenceInputs($Reference) {
    $files = @()
    foreach ($directory in $Reference.Primary,$Reference.Rns,$Reference.Lxmf) {
        $files += @(Get-ChildItem $directory -File -Recurse |
            Where-Object { $_.Extension -eq '.py' -and $_.FullName -notmatch '\\(\.git|__pycache__)\\' })
    }
    $venv = Split-Path (Split-Path $Reference.Python -Parent) -Parent
    $files += @(Get-ChildItem (Join-Path $venv 'Lib\site-packages') -Recurse -File |
        Where-Object { $_.Extension -in '.py','.pyd','.dll' -or $_.Name -eq 'METADATA' })
    $files += Get-Item $Reference.Python,(Join-Path $venv pyvenv.cfg)
    @($files | Sort-Object FullName -Unique)
}
if ($Build) {
    $buildLogs = Join-Path $evidenceRoot ('build-' + [guid]::NewGuid().ToString('N'))
    $null = New-Item $buildLogs -ItemType Directory -Force
    $null = New-Item (Join-Path $buildLogs build.log),(Join-Path $buildLogs publish.log) -ItemType File
    $env:TEMP = Join-Path $evidenceRoot build-work
    $env:TMP = $env:TEMP
    $null = New-Item $env:TEMP -ItemType Directory -Force
    $head = (& git rev-parse HEAD)
    $sources = @(ConvertTo-C104Hashes (Get-C104ProductionInputs))
    $acceptance = @(ConvertTo-C104Hashes (Get-C104AcceptanceInputs))
    & dotnet build tests\NomadNet.Tests\NomadNet.Tests.csproj -c Release -warnaserror --nologo -v:q `
        --artifacts-path $ArtifactsPath 2>&1 | Tee-Object (Join-Path $buildLogs build.log)
    if ($LASTEXITCODE -ne 0) { throw 'Strict Nomad managed build failed.' }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $install = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $install) { throw 'Windows MSVC NativeAOT tools are required.' }
    $setup = Join-Path $install 'VC\Auxiliary\Build\vcvars64.bat'
    $nativeDirectory = Split-Path ([IO.Path]::GetFullPath($Binary)) -Parent
    $env:PATH = (Split-Path $vswhere -Parent) + ';' + $env:PATH
    & $env:ComSpec /c "call `"$setup`" >nul && dotnet publish src\NomadNet\NomadNet.csproj -c Release -r win-x64 --artifacts-path `"$ArtifactsPath`" -o `"$nativeDirectory`" -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -warnaserror --nologo -v:q" 2>&1 |
        Tee-Object (Join-Path $buildLogs publish.log)
    if ($LASTEXITCODE -ne 0) { throw 'Strict Windows NativeAOT publish failed.' }
    Assert-C104Hashes $sources
    Assert-C104Hashes $acceptance
    if (Compare-Object @($acceptance.Path) @((ConvertTo-C104Hashes (Get-C104AcceptanceInputs)).Path)) {
        throw 'Acceptance input set changed during build.'
    }
    if ((& git rev-parse HEAD) -cne $head) { throw 'Build HEAD changed; rebuild rather than relabel.' }
    $sdk = & dotnet --version
    $dotnet = (Get-Command dotnet).Source
    $sdkDirectory = Join-Path (Split-Path $dotnet -Parent) "sdk\$sdk"
    $imageConverter = [IO.Path]::GetFullPath('artifacts\builds\nomadnet-reference-tools\ffmpeg.exe')
    if (-not (Test-Path $imageConverter)) { throw 'Select the provisioned actual image converter before building acceptance.' }
    $toolFiles = @(Get-Item $dotnet,(Join-Path $sdkDirectory 'MSBuild.dll'),(Join-Path $sdkDirectory 'Roslyn\bincore\csc.dll'),$setup,$vswhere,$imageConverter)
    $tools = Get-ChildItem (Join-Path $install 'VC\Tools\MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1
    $toolFiles += Get-Item (Join-Path $tools.FullName 'bin\Hostx64\x64\cl.exe'),(Join-Path $tools.FullName 'bin\Hostx64\x64\link.exe')
    $assetsPath = Join-Path $ArtifactsPath 'obj\NomadNet\project.assets.json'
    $assets = Get-Content $assetsPath -Raw | ConvertFrom-Json
    $compilerPackage = @($assets.libraries.PSObject.Properties | Where-Object Name -Like 'runtime.win-x64.microsoft.dotnet.ilcompiler/*')
    if ($compilerPackage.Count -ne 1) { throw 'Expected one actual resolved NativeAOT compiler package.' }
    $compilerVersion = $compilerPackage[0].Name.Split('/')[1]
    $compilerFiles = @($assets.packageFolders.PSObject.Properties.Name | ForEach-Object {
        Join-Path $_ ($compilerPackage[0].Value.path.Replace('/', '\') + '\tools\ilc.exe')
    } | Where-Object { Test-Path $_ })
    if ($compilerFiles.Count -ne 1) { throw 'Expected one actual resolved NativeAOT compiler executable.' }
    $toolFiles += Get-Item $compilerFiles[0],$assetsPath
    @{Head=$head;Stack=(& git -C external\DotReticulum rev-parse HEAD);Sdk=$sdk;Compiler=$compilerVersion;ImageConverter=$imageConverter;
        Toolchain=@(ConvertTo-C104Hashes $toolFiles);Sources=$sources;AcceptanceSources=$acceptance;
        Logs=@(ConvertTo-C104Hashes @(Get-Item (Join-Path $buildLogs build.log),(Join-Path $buildLogs publish.log)));
        Binary=@{Path=$Binary;Sha256=(Get-FileHash $Binary).Hash.ToLowerInvariant()};
        ManagedCommand="dotnet build tests\NomadNet.Tests\NomadNet.Tests.csproj -c Release -warnaserror --artifacts-path $ArtifactsPath";
        NativeCommand="dotnet publish src\NomadNet\NomadNet.csproj -c Release -r win-x64 --artifacts-path $ArtifactsPath -o $nativeDirectory -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -warnaserror";
        Boundary='Dirty shared working tree; exact production hashes, not HEAD alone, identify this candidate.'} |
        ConvertTo-Json -Depth 12 | Set-Content $provenancePath -Encoding utf8NoBOM
    if (-not $Freeze) { return }
}
if ($Freeze) {
    $previous = Get-Content artifacts\tests\nomad-guide\candidate.json -Raw | ConvertFrom-Json
    $provenance = Get-Content $provenancePath -Raw | ConvertFrom-Json
    if ((& git rev-parse HEAD) -cne $provenance.Head) { throw 'Build HEAD changed.' }
    if (Compare-Object @($provenance.Sources.Path) @((ConvertTo-C104Hashes (Get-C104ProductionInputs)).Path)) {
        throw 'Production input set changed since build.'
    }
    if (Compare-Object @($provenance.AcceptanceSources.Path) @((ConvertTo-C104Hashes (Get-C104AcceptanceInputs)).Path)) {
        throw 'Acceptance input set changed since build.'
    }
    Assert-C104Hashes @(@($provenance.Sources) + @($provenance.AcceptanceSources) + @($provenance.Binary) + @($provenance.Toolchain))
    $assemblies = @(Get-ChildItem (Join-Path $ArtifactsPath bin) -Filter NomadNet.Tests.dll -Recurse -File)
    if ($assemblies.Count -ne 1) { throw 'Expected one isolated Release Nomad test assembly.' }
    @{Claim='C104';Head=$provenance.Head;Stack=$provenance.Stack;Sdk=$provenance.Sdk;Compiler=$provenance.Compiler;ImageConverter=$provenance.ImageConverter;
        Sources=$provenance.Sources;AcceptanceSources=$provenance.AcceptanceSources;
        Toolchain=$provenance.Toolchain;Reference=$previous.Reference;
        ReferenceInputs=@(ConvertTo-C104Hashes (Get-C104ReferenceInputs $previous.Reference));
        ManagedArtifacts=@(ConvertTo-C104Hashes @(Get-ChildItem $assemblies[0].DirectoryName -File -Recurse |
            Where-Object Extension -In '.dll','.json','.pdb','.exe'));
        ArtifactsPath=$ArtifactsPath;Binary=$provenance.Binary;BuildProvenance=$provenancePath;
        BuildProvenanceSha256=(Get-FileHash $provenancePath).Hash.ToLowerInvariant()} |
        ConvertTo-Json -Depth 12 | Set-Content $Candidate -Encoding utf8NoBOM
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
function Assert-C104Frozen {
    if ((& git rev-parse HEAD) -cne $frozen.Head) { throw 'Root versioning commit changed.' }
    if ((& dotnet --version) -cne $frozen.Sdk) { throw 'SDK selection changed.' }
    if (Compare-Object @($frozen.Sources.Path) @((ConvertTo-C104Hashes (Get-C104ProductionInputs)).Path)) {
        throw 'Production input set changed.'
    }
    if (Compare-Object @($frozen.AcceptanceSources.Path) @((ConvertTo-C104Hashes (Get-C104AcceptanceInputs)).Path)) {
        throw 'Acceptance input set changed.'
    }
    if (Compare-Object @($frozen.ReferenceInputs.Path) @((ConvertTo-C104Hashes (Get-C104ReferenceInputs $frozen.Reference)).Path)) {
        throw 'Reference input set changed.'
    }
    Assert-C104Hashes @(@($frozen.Sources) + @($frozen.AcceptanceSources) + @($frozen.ReferenceInputs) +
        @($frozen.Toolchain) + @($frozen.ManagedArtifacts) + @($frozen.Binary))
    if ((Get-FileHash $frozen.BuildProvenance).Hash.ToLowerInvariant() -cne $frozen.BuildProvenanceSha256) { throw 'Build provenance changed.' }
    if ((& git -C external\DotReticulum rev-parse HEAD) -cne $frozen.Stack) { throw 'Stack pin changed.' }
    if (& git -C external\DotReticulum status --porcelain) { throw 'Stack source changed.' }
    foreach ($pair in @(@($frozen.Reference.Primary,$frozen.Reference.PrimaryCommit),
        @($frozen.Reference.Rns,$frozen.Reference.RnsCommit),@($frozen.Reference.Lxmf,$frozen.Reference.LxmfCommit))) {
        if ((& git -C $pair[0] rev-parse HEAD) -cne $pair[1]) { throw "Reference pin changed: $($pair[0])" }
        if (& git -C $pair[0] status --porcelain) { throw "Reference source changed: $($pair[0])" }
    }
}
Assert-C104Frozen
$env:DOTRETICULUM_NOMADNET_BINARY = [IO.Path]::GetFullPath($frozen.Binary.Path)
$env:DOTRETICULUM_NOMADNET_IMAGE_CONVERTER = $frozen.ImageConverter
$env:DOTRETICULUM_NOMADNET_MICRON = '1'
$env:DOTRETICULUM_NOMADNET_EDITOR_PRESENTATION = '1'
$env:DOTRETICULUM_NOMADNET_EDITOR_SCROLLING = '1'
$env:DOTRETICULUM_NOMADNET_EDITOR_STATE_IMPORT = '1'
$env:DOTRETICULUM_NOMADNET_PEER_CONTROLS = '1'
$env:DOTRETICULUM_NOMADNET_MICRON_EVIDENCE = Join-Path $output native
$null = New-Item $env:DOTRETICULUM_NOMADNET_MICRON_EVIDENCE -ItemType Directory -Force
$env:DOTRETICULUM_NOMADNET_EDITOR_EVIDENCE = Join-Path $output editor-presentation
$env:DOTRETICULUM_NOMADNET_SCROLLING_EVIDENCE = Join-Path $output editor-scrolling
$env:DOTRETICULUM_NOMADNET_STATE_EVIDENCE = Join-Path $output editor-state
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
$success = $false
$observations = [Collections.Generic.List[object]]::new()
try {
    Import-Module Pester -MinimumVersion 5.7.1
    & $env:DOTRETICULUM_PYTHON tests\Interop\GenerateNomadNetEditorStateImportVectors.py `
        --work (Join-Path $output editor-primary) --output (Join-Path $output editor-vectors.json) `
        --observations (Join-Path $output editor-imports.json) 2>&1 | Tee-Object (Join-Path $output editor-reference.log)
    if ($LASTEXITCODE -ne 0) { throw 'Actual-primary editor-state regression seed failed.' }
    if ((Get-FileHash (Join-Path $output editor-vectors.json)).Hash -cne
        (Get-FileHash tests\NomadNet.Tests\NomadNetEditorStateImportVectors.json).Hash) {
        throw 'Actual-primary editor-state regression fixture changed.'
    }
    $env:DOTRETICULUM_NOMADNET_STATE_PRIMARY = Join-Path $output 'editor-primary\saved'
    $observations.Add(@{Suite='C100 primary regression seed';Command='GenerateNomadNetEditorStateImportVectors.py';
        Result='Byte-identical actual-primary fixture; new synthetic saved profile'})
    foreach ($suite in @(
        @{Name='focused';Filter='FullyQualifiedName~NomadNetMicronGrammarTests|FullyQualifiedName~NomadNetMicronGrammarNativeTests'},
        @{Name='full';Filter='' })) {
        $arguments = @('test','tests\NomadNet.Tests\NomadNet.Tests.csproj','-c','Release','--no-build','--no-restore','-warnaserror',
            '--artifacts-path',$frozen.ArtifactsPath,'--logger',"trx;LogFileName=$($suite.Name).trx",'--results-directory',$output,'-v','quiet')
        if ($suite.Filter) { $arguments += @('--filter',$suite.Filter) }
        & dotnet @arguments 2>&1 | Tee-Object (Join-Path $output "$($suite.Name).log")
        if ($LASTEXITCODE -ne 0) { throw "Strict $($suite.Name) Nomad suite failed." }
        [xml] $trx = Get-Content (Join-Path $output "$($suite.Name).trx") -Raw
        $counts = $trx.TestRun.ResultSummary.Counters
        $cases = @($trx.TestRun.Results.UnitTestResult)
        $passed = @($cases | Where-Object outcome -EQ Passed).Count
        $failed = @($cases | Where-Object outcome -EQ Failed).Count
        $skipped = @($cases | Where-Object outcome -EQ NotExecuted).Count
        $observations.Add(@{Suite=$suite.Name;Command=('dotnet ' + ($arguments -join ' '));
            Passed=$passed;Failed=$failed;Skipped=$skipped;
            Skips=@($cases | Where-Object outcome -EQ NotExecuted | ForEach-Object {
                @{Name=$_.testName;Reason=$_.Output.ErrorInfo.Message}
            })})
        if ([int]$counts.failed -ne 0 -or $failed -ne 0 -or
            $cases.Count -ne $passed + $failed + $skipped) { throw 'Managed failures or unclassified outcomes.' }
        if ($suite.Name -eq 'focused' -and ($passed -ne 15 -or $skipped -ne 0)) { throw 'Focused requires15/0/0.' }
        if ($suite.Name -eq 'full' -and ($passed -lt 465 -or $skipped -gt 30)) { throw 'Unexpected full Nomad counters.' }
        if ($suite.Name -eq 'full') {
            foreach ($regression in @(@{Name='NomadNetEditorPresentationNativeTests';Count=3},
                @{Name='NomadNetEditorScrollingNativeTests';Count=3},@{Name='NomadNetEditorStateImportNativeTests';Count=2})) {
                $cases = @($trx.TestRun.Results.UnitTestResult | Where-Object { $_.testName -like "*$($regression.Name).*" })
                if ($cases.Count -ne $regression.Count -or @($cases | Where-Object outcome -ne Passed).Count) {
                    throw "Required accepted native editor regression failed/skipped: $($regression.Name)"
                }
            }
            $peerControl = @($trx.TestRun.Results.UnitTestResult | Where-Object {
                $_.testName -eq 'NomadNet.Tests.NomadNetNodeProfileTests.NativePeerRemovalPersistsAndDoesNotInventPeersOrEditStaticConfiguration'
            })
            if ($peerControl.Count -ne 1 -or $peerControl[0].outcome -ne 'Passed') {
                throw 'Required T1052 native peer-control regression failed/skipped.'
            }
        }
    }
    $configuration = New-PesterConfiguration
    $configuration.Run.Path = Join-Path $PSScriptRoot NomadNetMicronGrammar.Tests.ps1
    $configuration.Run.PassThru = $true
    $configuration.TestResult.Enabled = $true
    $configuration.TestResult.OutputPath = Join-Path $output pester.xml
    $configuration.Output.Verbosity = 'Detailed'
    $result = Invoke-Pester -Configuration $configuration
    $observations.Add(@{Suite='Pester';Command='Invoke-Pester tests\Pester\NomadNetMicronGrammar.Tests.ps1';
        Passed=$result.PassedCount;Failed=$result.FailedCount;Skipped=$result.SkippedCount})
    if ($result.FailedCount -ne 0 -or $result.SkippedCount -ne 0 -or $result.PassedCount -ne 4) { throw 'Pester requires4/0/0.' }
    $success = $true
} finally {
    Assert-C104Frozen
    @{Claim='C104';Accepted=$success;CandidateSha256=(Get-FileHash $candidatePath).Hash.ToLowerInvariant();
        BinarySha256=(Get-FileHash $frozen.Binary.Path).Hash.ToLowerInvariant();
        Results=@($observations);
        Boundary='Actual Windows ConPTY cached-page grammar/actions and native authoring/restart; headless primary parser vectors. No network, primary fullscreen, physical terminal, or current Linux replay claim.'} |
        ConvertTo-Json -Depth 6 | Set-Content (Join-Path $output receipt.json) -Encoding utf8NoBOM
}
