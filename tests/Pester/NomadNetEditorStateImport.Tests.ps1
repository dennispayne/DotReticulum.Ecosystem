BeforeAll {
    $script:C100Root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    Import-Module (Join-Path $script:C100Root 'tests\StackProvenance.psm1') -Force
    $script:C100Evidence = if ($env:DOTRETICULUM_NOMADNET_STATE_EVIDENCE) {
        $env:DOTRETICULUM_NOMADNET_STATE_EVIDENCE
    } else { Join-Path $script:C100Root ('artifacts\tests\nomad-editor-state-import\pester-' + [guid]::NewGuid().ToString('N')) }
    $null = New-Item $script:C100Evidence -ItemType Directory -Force
    function Assert-C100Frozen {
        if ($env:DOTRETICULUM_NOMADNET_EDITOR_STATE_IMPORT -ne '1') { return }
        if (-not $env:DOTRETICULUM_NOMADNET_STATE_CANDIDATE) { throw 'Select the frozen C100 candidate.' }
        $candidate = Get-Content $env:DOTRETICULUM_NOMADNET_STATE_CANDIDATE -Raw | ConvertFrom-Json
        (Get-FileHash $env:DOTRETICULUM_NOMADNET_BINARY).Hash.ToLowerInvariant() | Should -BeExactly $candidate.Binary.Sha256
        foreach ($source in @($candidate.Sources) + @($candidate.AcceptanceSources)) {
            (Get-FileHash (Join-Path $script:C100Root $source.Path)).Hash.ToLowerInvariant() | Should -BeExactly $source.Sha256
        }
        Get-DotReticulumStackId -Root $script:C100Root | Should -BeExactly $candidate.Stack -Because 'the frozen candidate must match the restored DotReticulum package identity'
    }
    function Invoke-C100([string] $Label, [string[]] $Arguments, [string[]] $Commands = @(), [int] $Expected = 0) {
        $info = [Diagnostics.ProcessStartInfo]::new($env:DOTRETICULUM_NOMADNET_BINARY)
        $info.UseShellExecute = $false
        $info.RedirectStandardInput = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $info.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
        foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $info
        $started = $false
        try {
            $started = $process.Start()
            if (-not $started) { throw 'Native child did not start.' }
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            foreach ($command in $Commands) { $process.StandardInput.WriteLine($command) }
            $process.StandardInput.Close()
            $null = $process.WaitForExitAsync().WaitAsync([TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult()
            $text = $stdout.GetAwaiter().GetResult()
            $errors = $stderr.GetAwaiter().GetResult()
            $text.Length | Should -BeLessThan 2000000
            [IO.File]::WriteAllText((Join-Path $script:C100Evidence "$Label.stdout"), $text)
            [IO.File]::WriteAllText((Join-Path $script:C100Evidence "$Label.stderr"), $errors)
            $process.ExitCode | Should -Be $Expected -Because $errors
            return $text
        } finally {
            if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
            $process.Dispose()
        }
    }
    function New-C100Profile([string] $Name) {
        $root = Join-Path $script:C100Evidence $Name
        $source = Join-Path $root primary
        $profile = Join-Path $root native
        $rns = Join-Path $root rns
        $null = New-Item $rns -ItemType Directory -Force
        Copy-Item $env:DOTRETICULUM_NOMADNET_STATE_PRIMARY $source -Recurse
        $state = Join-Path $source editor_state.json
        $text = [IO.File]::ReadAllText($state).Replace(
            $env:DOTRETICULUM_NOMADNET_STATE_PRIMARY.Replace('\','/'), $source.Replace('\','/'))
        [IO.File]::WriteAllText($state, $text)
        [IO.File]::WriteAllText((Join-Path $rns config), "[reticulum]`nshare_instance=no`nenable_transport=no`n[logging]`nloglevel=0`n[interfaces]`n")
        return @{Root=$root;Source=$source;Profile=$profile;Rns=$rns;
            Import=@('--import-profile',$source,'--config',$profile,'--rnsconfig',$rns);
            Arguments=@('--config',$profile,'--rnsconfig',$rns)}
    }
    function Get-C100Snapshot([string] $Path) {
        @(Get-ChildItem $Path -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
            [IO.Path]::GetRelativePath($Path,$_.FullName) + ':' + (Get-FileHash $_.FullName).Hash
        }) -join "`n"
    }
}

Describe 'Bounded primary editor state import C100' -Tag 'EditorStateImport' {
    BeforeEach { Assert-C100Frozen }
    AfterEach { Assert-C100Frozen }
    It 'T743 actual primary editor constructors and persistence regenerate byte-identical factual vectors' {
        if ($env:DOTRETICULUM_NOMADNET_EDITOR_STATE_IMPORT -ne '1') {
            Set-ItResult -Skipped -Because 'Select C100 with pinned actual-primary imports.'
            return
        }
        $output = Join-Path $script:C100Evidence regenerated.json
        & $env:DOTRETICULUM_PYTHON (Join-Path $script:C100Root 'tests\Interop\GenerateNomadNetEditorStateImportVectors.py') `
            --work (Join-Path $script:C100Evidence primary-execution) --output $output `
            --observations (Join-Path $script:C100Evidence actual-imports.json)
        $LASTEXITCODE | Should -Be 0
        (Get-FileHash $output).Hash | Should -BeExactly (Get-FileHash (Join-Path $script:C100Root 'tests\NomadNet.Tests\NomadNetEditorStateImportVectors.json')).Hash
    }
    It 'T744 native profile import activates selected file panes tree preferences and ordinary saved edits across restart' {
        if ($env:DOTRETICULUM_NOMADNET_EDITOR_STATE_IMPORT -ne '1') {
            Set-ItResult -Skipped -Because 'Select frozen Windows native C100 acceptance.'
            return
        }
        $work = New-C100Profile selected
        [IO.File]::WriteAllText((Join-Path $work.Source 'storage\pages\nested\notice.mu.allowed'), ('a' * 32) + "`n")
        $before = Get-C100Snapshot $work.Source
        Invoke-C100 selected-import $work.Import | Should -Match 'Imported profile'
        $before | Should -BeExactly (Get-C100Snapshot $work.Source)
        $identity = (Get-FileHash (Join-Path $work.Profile identity)).Hash
        $first = Invoke-C100 selected-first $work.Arguments @('edit status','edit view 100 8','edit tree',
            'edit text ">Native C100 saved\nretained"','edit save','quit')
        $first | Should -Match 'pages/nested/notice.mu'
        $first | Should -Match 'filter.mu=True'
        $first | Should -Match '\[-\] pages/nested'
        $first | Should -Not -Match 'preview=38%|Error:'
        $second = Invoke-C100 selected-restart $work.Arguments @('edit status','edit show','edit view 100 8','quit')
        $second | Should -Match 'Native C100 saved'
        $second | Should -Match 'filter.mu=True'
        (Get-FileHash (Join-Path $work.Profile identity)).Hash | Should -BeExactly $identity
        (Get-FileHash (Join-Path $work.Source 'storage\pages\nested\notice.mu.allowed')).Hash |
            Should -BeExactly (Get-FileHash (Join-Path $work.Profile 'storage\pages\nested\notice.mu.allowed')).Hash
        Get-Content (Join-Path $work.Profile editor_state.json) -Raw | Should -Not -Match 'scroll|Native C100 saved'
        $before | Should -BeExactly (Get-C100Snapshot $work.Source)
    }
    It 'T745 native malformed oversized duplicate and unsafe primary state fail without a partial target then recover' {
        if ($env:DOTRETICULUM_NOMADNET_EDITOR_STATE_IMPORT -ne '1') {
            Set-ItResult -Skipped -Because 'Select frozen native C100 importer.'
            return
        }
        $cases = @('{','{"filter_mu":true,"filter_mu":false}','{"filter_mu":"false"}',
            '{"expanded":[1]}', (' ' * 32769), '{"last_file":"@/storage/pages/.hidden"}')
        for ($index=0; $index -lt $cases.Count; $index++) {
            $work = New-C100Profile "malformed-$index"
            [IO.File]::WriteAllText((Join-Path $work.Source editor_state.json),
                $cases[$index].Replace('@/', $work.Source.Replace('\','/') + '/'))
            $before = Get-C100Snapshot $work.Source
            $null = Invoke-C100 "malformed-$index" $work.Import @() 1
            Test-Path $work.Profile | Should -BeFalse
            @(Get-ChildItem $work.Root -Directory -Filter 'native.profile-import-*').Count | Should -Be 0
            $before | Should -BeExactly (Get-C100Snapshot $work.Source)
        }
        $recovery = New-C100Profile recovery
        $null = Invoke-C100 recovery-import $recovery.Import
        Invoke-C100 recovery-start $recovery.Arguments @('edit status','utility identity','quit') |
            Should -Match 'pages/nested/notice.mu'
    }
    It 'T746 native absent state missing selection and unknown fields have explicit restart dispositions' {
        if ($env:DOTRETICULUM_NOMADNET_EDITOR_STATE_IMPORT -ne '1') {
            Set-ItResult -Skipped -Because 'Select frozen native C100 importer.'
            return
        }
        $absent = New-C100Profile absent
        Remove-Item (Join-Path $absent.Source editor_state.json)
        $null = Invoke-C100 absent-import $absent.Import
        (Get-Content (Join-Path $absent.Profile profile-import.json) -Raw) | Should -Match 'state absent'
        Invoke-C100 absent-start $absent.Arguments @('edit view 80 6','edit status','quit') | Should -Match 'Editor: closed/'
        $missing = New-C100Profile missing
        Remove-Item (Join-Path $missing.Source 'storage\pages\nested\notice.mu')
        $statePath = Join-Path $missing.Source editor_state.json
        $state = Get-Content $statePath -Raw | ConvertFrom-Json
        $state | Add-Member invented_scroll 400
        $state | ConvertTo-Json -Depth 10 | Set-Content $statePath -Encoding utf8NoBOM
        $null = Invoke-C100 missing-import $missing.Import
        Get-Content (Join-Path $missing.Profile profile-import.json) -Raw | Should -Match 'selected file is absent'
        Get-Content (Join-Path $missing.Profile profile-import.json) -Raw | Should -Match 'invented_scroll'
        Invoke-C100 missing-start $missing.Arguments @('edit status','edit view 80 6','quit') | Should -Match 'Editor: closed/'
        Invoke-C100 missing-restart $missing.Arguments @('edit status','quit') | Should -Match 'Editor: closed/'
    }
    It 'T747 native target ownership and source reparse refusal never damage unrelated native state' {
        if ($env:DOTRETICULUM_NOMADNET_EDITOR_STATE_IMPORT -ne '1' -or -not $IsWindows) {
            Set-ItResult -Skipped -Because 'Selected Windows native junction/ownership acceptance; Linux blocked by WSL EIO.'
            return
        }
        $work = New-C100Profile ownership
        $null = New-Item $work.Profile -ItemType Directory
        [IO.File]::WriteAllText((Join-Path $work.Profile editor_state.json), 'NATIVE OWNED STATE')
        [IO.File]::WriteAllText((Join-Path $work.Profile sentinel), 'UNRELATED')
        $before = Get-C100Snapshot $work.Profile
        $null = Invoke-C100 existing-owner $work.Import @() 1
        $before | Should -BeExactly (Get-C100Snapshot $work.Profile)
        $linked = New-C100Profile linked
        $outside = Join-Path $linked.Root outside
        $null = New-Item $outside -ItemType Directory
        [IO.File]::WriteAllText((Join-Path $outside sentinel), 'OUTSIDE')
        $junction = Join-Path $linked.Source 'storage\pages\linked'
        $null = New-Item $junction -ItemType Junction -Target $outside
        try {
            $null = Invoke-C100 linked-import $linked.Import @() 1
            Test-Path $linked.Profile | Should -BeFalse
            [IO.File]::ReadAllText((Join-Path $outside sentinel)) | Should -BeExactly 'OUTSIDE'
        } finally { Remove-Item $junction -Force }
    }
}
