BeforeAll {
    $script:C97Root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    Import-Module (Join-Path $script:C97Root 'tests\StackProvenance.psm1') -Force
    $script:C97Evidence = $env:DOTRETICULUM_NOMADNET_EDITOR_EVIDENCE
    if (-not $script:C97Evidence) {
        $script:C97Evidence = Join-Path $script:C97Root ('artifacts\tests\nomad-editor-presentation\pester-' + [guid]::NewGuid().ToString('N'))
    }
    $null = New-Item $script:C97Evidence -ItemType Directory -Force
    function Assert-C97Frozen {
        if ($env:DOTRETICULUM_NOMADNET_EDITOR_PRESENTATION -ne '1') { return }
        if (-not $env:DOTRETICULUM_NOMADNET_EDITOR_CANDIDATE) { throw 'Select the frozen C97 candidate.' }
        $candidate = Get-Content $env:DOTRETICULUM_NOMADNET_EDITOR_CANDIDATE -Raw | ConvertFrom-Json
        (Get-FileHash $env:DOTRETICULUM_NOMADNET_BINARY).Hash.ToLowerInvariant() | Should -BeExactly $candidate.Binary.Sha256
        foreach ($source in @($candidate.Sources) + @($candidate.AcceptanceSources)) {
            (Get-FileHash (Join-Path $script:C97Root $source.Path)).Hash.ToLowerInvariant() | Should -BeExactly $source.Sha256
        }
        Get-DotReticulumStackId -Root $script:C97Root | Should -BeExactly $candidate.Stack -Because 'the frozen candidate must match the restored DotReticulum package identity'
    }
    function Invoke-C97([string] $Label, [string[]] $Arguments, [string[]] $Commands = @(), [int] $Expected = 0) {
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
            [IO.File]::WriteAllText((Join-Path $script:C97Evidence "$Label.stdout"), $text)
            [IO.File]::WriteAllText((Join-Path $script:C97Evidence "$Label.stderr"), $errors)
            $process.ExitCode | Should -Be $Expected -Because $errors
            return $text
        } finally {
            if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
            $process.Dispose()
        }
    }
    function New-C97Profile([string] $Name) {
        $root = Join-Path $script:C97Evidence $Name
        $profile = Join-Path $root profile
        $rns = Join-Path $root rns
        $null = New-Item $rns -ItemType Directory -Force
        [IO.File]::WriteAllText((Join-Path $rns config), "[reticulum]`nshare_instance=no`nenable_transport=no`n[logging]`nloglevel=0`n[interfaces]`n")
        $null = Invoke-C97 "$Name-init" @('--init','--config',$profile)
        $settings = Join-Path $profile settings.json
        $value = Get-Content $settings -Raw | ConvertFrom-Json
        $value.clientAnnounceAtStart = $false
        $value.periodicLxmfSync = $false
        $value.introTime = 0
        $value.logLevel = 0
        $value.nodePageRefreshIntervalMinutes = 0
        $value.nodeFileRefreshIntervalMinutes = 0
        $value | ConvertTo-Json -Depth 10 | Set-Content $settings -Encoding utf8NoBOM
        return @{Root=$root;Profile=$profile;Rns=$rns;Arguments=@('--config',$profile,'--rnsconfig',$rns)}
    }
}

Describe 'Bounded original editor presentation C97' -Tag 'EditorPresentation' {
    BeforeEach { Assert-C97Frozen }
    AfterEach { Assert-C97Frozen }
    It 'T623 actual primary editor presentation regenerates byte-identical factual vectors' {
        if ($env:DOTRETICULUM_NOMADNET_EDITOR_PRESENTATION -ne '1') {
            Set-ItResult -Skipped -Because 'Select C97 with the pinned actual primary interpreter and source.'
            return
        }
        $output = Join-Path $script:C97Evidence regenerated.json
        & $env:DOTRETICULUM_PYTHON (Join-Path $script:C97Root 'tests\Interop\GenerateNomadNetEditorPresentationVectors.py') `
            --work (Join-Path $script:C97Evidence primary) --output $output --observations (Join-Path $script:C97Evidence imports.json)
        $LASTEXITCODE | Should -Be 0
        (Get-FileHash $output).Hash | Should -BeExactly (Get-FileHash (Join-Path $script:C97Root 'tests\NomadNet.Tests\NomadNetEditorPresentationVectors.json')).Hash
    }
    It 'T624 native command controls persist panes filtered expanded tree and reopen saved source after restart' {
        if ($env:DOTRETICULUM_NOMADNET_EDITOR_PRESENTATION -ne '1') {
            Set-ItResult -Skipped -Because 'Select the frozen native C97 candidate.'
            return
        }
        $work = New-C97Profile persisted
        $text = Invoke-C97 first $work.Arguments @('edit mkdir pages nested','edit new pages nested\saved.mu',
            'edit text ">Native saved heading\nbody"','edit save','edit expand pages nested','edit filter on',
            'edit pane preview','edit view 100 8','quit')
        $text | Should -Match 'files=22%.*editor=40%'
        $text | Should -Not -Match 'preview=38%|Error:'
        $text | Should -Match 'LXMF delivery address: ([0-9a-f]{32})'
        $identity = [regex]::Match($text, 'LXMF delivery address: ([0-9a-f]{32})').Groups[1].Value
        $second = Invoke-C97 restart $work.Arguments @('edit view 100 8','edit status','edit pane preview','edit preview','quit')
        $second | Should -Match 'filter.mu=True'
        $second | Should -Match 'nested\\saved.mu'
        $second | Should -Match 'Native saved heading'
        $second | Should -Not -Match 'Error:'
        $second | Should -Match ('LXMF delivery address: ' + $identity)
        $state = Get-Content (Join-Path $work.Profile editor_state.json) -Raw | ConvertFrom-Json
        $state.expanded | Should -Contain 'pages/nested'
        $state.visiblePanes | Should -Contain preview
    }
    It 'T625 native dirty EOF never persists drafts and malformed control errors leave healthy service' {
        if ($env:DOTRETICULUM_NOMADNET_EDITOR_PRESENTATION -ne '1') {
            Set-ItResult -Skipped -Because 'Select the frozen native C97 candidate.'
            return
        }
        $work = New-C97Profile errors
        $dirty = Invoke-C97 dirty $work.Arguments @('edit new pages draft.mu','edit text "PRIVATE SYNTHETIC DRAFT"') 1
        $dirty | Should -Not -Match 'Document saved'
        Test-Path (Join-Path $work.Profile 'storage\pages\draft.mu') | Should -BeFalse
        Get-Content (Join-Path $work.Profile editor_state.json) -Raw | Should -Not -Match 'PRIVATE SYNTHETIC DRAFT'
        $recovered = Invoke-C97 recovery $work.Arguments @('edit pane bogus','edit filter bad','edit expand pages ..',
            'edit view 80 6','utility identity','quit')
        $recovered | Should -Match 'Choose files, editor or preview'
        $recovered | Should -Match 'Use edit filter on'
        $recovered | Should -Match 'Editor panes:'
        $recovered | Should -Match 'Speed test:'
    }
}
