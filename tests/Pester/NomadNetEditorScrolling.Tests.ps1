BeforeAll {
    $script:C99Root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $script:C99Evidence = $env:DOTRETICULUM_NOMADNET_SCROLLING_EVIDENCE
    if (-not $script:C99Evidence) {
        $script:C99Evidence = Join-Path $script:C99Root ('artifacts\tests\nomad-editor-scrolling\pester-' + [guid]::NewGuid().ToString('N'))
    }
    $null = New-Item $script:C99Evidence -ItemType Directory -Force
    function Assert-C99Frozen {
        if ($env:DOTRETICULUM_NOMADNET_EDITOR_SCROLLING -ne '1') { return }
        if (-not $env:DOTRETICULUM_NOMADNET_SCROLLING_CANDIDATE) { throw 'Select the frozen C99 candidate.' }
        $candidate = Get-Content $env:DOTRETICULUM_NOMADNET_SCROLLING_CANDIDATE -Raw | ConvertFrom-Json
        (Get-FileHash $env:DOTRETICULUM_NOMADNET_BINARY).Hash.ToLowerInvariant() | Should -BeExactly $candidate.Binary.Sha256
        foreach ($source in @($candidate.Sources) + @($candidate.AcceptanceSources)) {
            (Get-FileHash (Join-Path $script:C99Root $source.Path)).Hash.ToLowerInvariant() | Should -BeExactly $source.Sha256
        }
    }
    function Invoke-C99([string] $Label, [string[]] $Arguments, [string[]] $Commands = @()) {
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
            [IO.File]::WriteAllText((Join-Path $script:C99Evidence "$Label.stdout"), $text)
            [IO.File]::WriteAllText((Join-Path $script:C99Evidence "$Label.stderr"), $errors)
            $process.ExitCode | Should -Be 0 -Because $errors
            return $text
        } finally {
            if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
            $process.Dispose()
        }
    }
    function New-C99Profile([string] $Name) {
        $root = Join-Path $script:C99Evidence $Name
        $profile = Join-Path $root profile
        $rns = Join-Path $root rns
        $null = New-Item $rns -ItemType Directory -Force
        [IO.File]::WriteAllText((Join-Path $rns config), "[reticulum]`nshare_instance=no`nenable_transport=no`n[logging]`nloglevel=0`n[interfaces]`n")
        $null = Invoke-C99 "$Name-init" @('--init','--config',$profile)
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

Describe 'Bounded original editor scrolling C99' -Tag 'EditorScrolling' {
    BeforeEach { Assert-C99Frozen }
    AfterEach { Assert-C99Frozen }
    It 'T713 actual primary scrolling independently regenerates factual vectors including preview replacement limitation' {
        if ($env:DOTRETICULUM_NOMADNET_EDITOR_SCROLLING -ne '1') {
            Set-ItResult -Skipped -Because 'Select C99 with the pinned actual primary interpreter and source.'
            return
        }
        $output = Join-Path $script:C99Evidence regenerated.json
        & $env:DOTRETICULUM_PYTHON (Join-Path $script:C99Root 'tests\Interop\GenerateNomadNetEditorScrollingVectors.py') `
            --work (Join-Path $script:C99Evidence primary) --output $output --observations (Join-Path $script:C99Evidence imports.json)
        $LASTEXITCODE | Should -Be 0
        (Get-FileHash $output).Hash | Should -BeExactly (Get-FileHash (Join-Path $script:C99Root 'tests\NomadNet.Tests\NomadNetEditorScrollingVectors.json')).Hash
        $result = Get-Content $output -Raw | ConvertFrom-Json
        $result.editor.before | Should -Be 17
        $result.editor.afterToggle | Should -Be 17
        $result.editor.afterRender | Should -Be 0
    }
    It 'T714 native scrolling retains session offsets and saved full source then resets positions on restart' {
        if ($env:DOTRETICULUM_NOMADNET_EDITOR_SCROLLING -ne '1') {
            Set-ItResult -Skipped -Because 'Select the frozen C99 native binary.'
            return
        }
        $work = New-C99Profile restart
        $pages = Join-Path $work.Profile 'storage\pages'
        $null = New-Item $pages -ItemType Directory -Force
        $text = (0..79 | ForEach-Object { 'LINE-{0:d3}' -f $_ }) -join "`n`n"
        [IO.File]::WriteAllText((Join-Path $pages long.mu), $text)
        $first = Invoke-C99 first $work.Arguments @('edit open pages long.mu','edit view 120 6',
            'edit scroll editor page-down','edit scroll preview 17','edit pane preview','edit pane preview',
            'edit view 120 6','edit save','quit')
        $first | Should -Match 'editor=5; preview=17; height=6'
        $first | Should -Not -Match 'Error:'
        [IO.File]::ReadAllText((Join-Path $pages long.mu)) | Should -BeExactly $text
        $second = Invoke-C99 second $work.Arguments @('edit view 120 6','edit scroll editor end',
            'edit scroll editor home','edit scroll preview end','quit')
        $second | Should -Match 'editor=0; preview=0; height=6'
        $second | Should -Match 'LINE-079'
        $second | Should -Not -Match 'Error:'
        Get-Content (Join-Path $work.Profile editor_state.json) -Raw | Should -Not -Match 'scroll|LINE-'
    }
    It 'T715 native malformed commands cannot move positions or disable healthy commands' {
        if ($env:DOTRETICULUM_NOMADNET_EDITOR_SCROLLING -ne '1') {
            Set-ItResult -Skipped -Because 'Select the frozen C99 native binary.'
            return
        }
        $work = New-C99Profile malformed
        $result = Invoke-C99 malformed $work.Arguments @('edit scroll','edit scroll editor 2147483648',
            'edit scroll files down','edit scroll editor 1 extra','edit view 80 6',
            'edit scroll editor -2147483648','utility identity','quit')
        $result | Should -Match 'Use edit scroll'
        $result | Should -Match 'signed Int32 row delta'
        $result | Should -Match 'Scroll editor, preview or a focused text pane'
        $result | Should -Match 'editor=0; preview=0; height=6'
        $result | Should -Match 'Speed test:'
    }
}
