BeforeAll {
    $script:C102Root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $script:C102Evidence = if ($env:DOTRETICULUM_NOMADNET_GUIDE_EVIDENCE) {
        $env:DOTRETICULUM_NOMADNET_GUIDE_EVIDENCE
    } else { Join-Path $script:C102Root ('artifacts\tests\nomad-guide\pester-' + [guid]::NewGuid().ToString('N')) }
    $null = New-Item $script:C102Evidence -ItemType Directory -Force
    function Assert-C102Frozen {
        if ($env:DOTRETICULUM_NOMADNET_GUIDE -ne '1') { return }
        if (-not $env:DOTRETICULUM_NOMADNET_GUIDE_CANDIDATE) { throw 'Select the frozen C102 candidate.' }
        $candidate = Get-Content $env:DOTRETICULUM_NOMADNET_GUIDE_CANDIDATE -Raw | ConvertFrom-Json
        (Get-FileHash $env:DOTRETICULUM_NOMADNET_BINARY).Hash.ToLowerInvariant() | Should -BeExactly $candidate.Binary.Sha256
        foreach ($source in @($candidate.Sources) + @($candidate.AcceptanceSources)) {
            (Get-FileHash (Join-Path $script:C102Root $source.Path)).Hash.ToLowerInvariant() | Should -BeExactly $source.Sha256
        }
        (& git -C (Join-Path $script:C102Root 'external\DotReticulum') rev-parse HEAD) | Should -BeExactly $candidate.Stack
    }
    function Invoke-C102([string] $Label, [string[]] $Arguments, [string[]] $Commands = @()) {
        $info = [Diagnostics.ProcessStartInfo]::new($env:DOTRETICULUM_NOMADNET_BINARY)
        $info.UseShellExecute = $false
        $info.RedirectStandardInput = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $info.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
        $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
        foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $info
        $started = $false
        try {
            $started = $process.Start()
            if (-not $started) { throw 'Native guide process did not start.' }
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            foreach ($command in $Commands) { $process.StandardInput.WriteLine($command) }
            $process.StandardInput.Close()
            $null = $process.WaitForExitAsync().WaitAsync([TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult()
            $text = $stdout.GetAwaiter().GetResult()
            $errors = $stderr.GetAwaiter().GetResult()
            $text.Length | Should -BeLessThan 2000000
            [IO.File]::WriteAllText((Join-Path $script:C102Evidence "$Label.stdout"), $text)
            [IO.File]::WriteAllText((Join-Path $script:C102Evidence "$Label.stderr"), $errors)
            $process.ExitCode | Should -Be 0 -Because $errors
            return $text
        } finally {
            if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
            $process.Dispose()
        }
    }
    function New-C102Profile([string] $Name, [bool] $Hosting = $false) {
        $root = Join-Path $script:C102Evidence $Name
        $rns = Join-Path $root rns
        $profile = Join-Path $root profile
        $null = New-Item $rns -ItemType Directory -Force
        [IO.File]::WriteAllText((Join-Path $rns config), "[reticulum]`nshare_instance=no`nenable_transport=no`n[logging]`nloglevel=0`n[interfaces]`n")
        $null = Invoke-C102 "$Name-init" @('--init','--config',$profile)
        $path = Join-Path $profile settings.json
        $settings = Get-Content $path -Raw | ConvertFrom-Json
        $settings.clientAnnounceAtStart = $false
        $settings.periodicLxmfSync = $false
        $settings.introTime = 0
        $settings.hideGuide = $true
        $settings.enableNode = $Hosting
        $settings.nodeAnnounceAtStart = $false
        $settings | ConvertTo-Json -Depth 10 | Set-Content $path -Encoding utf8NoBOM
        return @{Root=$root;Profile=$profile;Rns=$rns;Arguments=@('--config',$profile,'--rnsconfig',$rns)}
    }
}

Describe 'Original complete local guide workflows C102' -Tag 'Guide' {
    BeforeEach { Assert-C102Frozen }
    AfterEach { Assert-C102Frozen }
    It 'T822 actual pinned primary guide topics selection anchors and link delegates regenerate byte-identical observations' {
        if ($env:DOTRETICULUM_NOMADNET_GUIDE -ne '1') {
            Set-ItResult -Skipped -Because 'Select C102 with pinned actual-primary imports.'
            return
        }
        $output = Join-Path $script:C102Evidence regenerated.json
        & $env:DOTRETICULUM_PYTHON (Join-Path $script:C102Root 'tests\Interop\GenerateNomadNetGuideVectors.py') `
            --output $output --observations (Join-Path $script:C102Evidence actual-imports.json)
        $LASTEXITCODE | Should -Be 0
        (Get-FileHash $output).Hash | Should -BeExactly (Get-FileHash (Join-Path $script:C102Root 'tests\NomadNet.Tests\NomadNetGuideVectors.json')).Hash
    }
    It 'T823 native ordinary console exposes every original topic plus links selection search viewport and history' {
        if ($env:DOTRETICULUM_NOMADNET_GUIDE -ne '1') {
            Set-ItResult -Skipped -Because 'Select the frozen C102 native binary.'
            return
        }
        $work = New-C102Profile reader
        $output = Invoke-C102 reader $work.Arguments @('guide topics','guide','guide links','guide open 1',
            'guide hosting#access','guide view 3','guide messages','guide back','guide forward',
            'guide display','guide next','guide next','guide view 1','guide scroll 10000','guide scroll -10000',
            'guide find RESTART','guide select 1','guide find UNIQUE-SYNTHETIC-NOT-IN-GUIDE','quit')
        $output | Should -Not -Match 'Error:'
        $output | Should -Not -Match '\[5:Guide\]'
        $output | Should -Match 'Local application guide'
        $output | Should -Match 'Authoring local documents'
        $output | Should -Match 'Access lists'
        $output | Should -Match 'Guide hosting; lines \d+-\d+/\d+'
        $output | Should -Match 'Anchor exercise'
        $output | Should -Match 'local guide matches'
        $output | Should -Match 'No local guide matches'
        $topics = @('start','authoring','messages','hosting','configuration','micron','network',
            'first-run','concepts','interfaces','network-configuration','propagation','browser','keyboard',
            'display','licenses','troubleshooting','safety')
        $all = Invoke-C102 all-topics $work.Arguments @(@($topics | ForEach-Object { "guide $_" }) + 'quit')
        $all | Should -Not -Match 'Error:'
        $all | Should -Match 'Addresses and trust'
        $all | Should -Match 'Connect Reticulum interfaces'
        $all | Should -Match 'Deferred delivery and retrieval'
        $all | Should -Match 'Provenance and licenses'
        $all | Should -Match 'Recover without weakening safety'
    }
    It 'T824 native original guide authoring instructions create preview save restrict and register a page across restart' {
        if ($env:DOTRETICULUM_NOMADNET_GUIDE -ne '1') {
            Set-ItResult -Skipped -Because 'Select C102 native guide and local hosting acceptance.'
            return
        }
        $work = New-C102Profile authoring $true
        $output = Invoke-C102 authoring $work.Arguments @('guide authoring','edit mkdir pages',
            'edit new pages guide-example.mu','edit text ">Original guide example\nsynthetic persisted text\n"',
            'edit preview','edit save','guide hosting#access',
            ('edit permissions ["' + ('a' * 32) + '"]'),'edit close discard','node refresh','node paths','quit')
        $output | Should -Not -Match 'Error:'
        $output | Should -Match 'Original guide example'
        $output | Should -Match '/page/guide-example.mu'
        $path = Join-Path $work.Profile 'storage\pages\guide-example.mu'
        [IO.File]::ReadAllText($path) | Should -BeExactly ">Original guide example`nsynthetic persisted text`n"
        [IO.File]::ReadAllText("$path.allowed") | Should -BeExactly (('a' * 32) + "`n")
        $hash = (Get-FileHash $path).Hash
        $identity = (Get-FileHash (Join-Path $work.Rns 'storage\nomadnet_identity')).Hash
        $restart = Invoke-C102 authoring-restart $work.Arguments @('guide hosting#access',
            'edit open pages guide-example.mu','edit show','node paths','quit')
        $restart | Should -Not -Match 'Error:'
        $restart | Should -Match 'synthetic persisted text'
        $restart | Should -Match '/page/guide-example.mu'
        (Get-FileHash $path).Hash | Should -BeExactly $hash
        (Get-FileHash (Join-Path $work.Rns 'storage\nomadnet_identity')).Hash | Should -BeExactly $identity
    }
    It 'T825 native malformed guide commands fail locally recover and leave private editor configuration and browser state separate' {
        if ($env:DOTRETICULUM_NOMADNET_GUIDE -ne '1') {
            Set-ItResult -Skipped -Because 'Select C102 frozen native failure/restart acceptance.'
            return
        }
        $work = New-C102Profile failures
        $settingsHash = (Get-FileHash (Join-Path $work.Profile settings.json)).Hash
        $output = Invoke-C102 failures $work.Arguments @('edit mkdir pages','edit new pages untouched.mu',
            'edit text "PRIVATE SYNTHETIC UNSAVED BUFFER"','guide hosting#access','guide hosting#missing',
            'guide missing','guide open 0','guide open nope','guide topics surplus','guide back surplus',
            'guide view 101','guide scroll 10001',('guide ' + ('x' * 257)),('guide find ' + ('x' * 129)),
            'guide find PRIVATE SYNTHETIC UNSAVED BUFFER','guide hosting#access','guide links',
            'guide open 1','edit show','edit close discard','quit')
        ([regex]::Matches($output,'Error:')).Count | Should -Be 10
        $output | Should -Match 'Unknown guide anchor'
        $output | Should -Match 'No local guide matches'
        $output | Should -Match 'PRIVATE SYNTHETIC UNSAVED BUFFER'
        Test-Path (Join-Path $work.Profile 'storage\pages\untouched.mu') | Should -BeFalse
        (Get-FileHash (Join-Path $work.Profile settings.json)).Hash | Should -BeExactly $settingsHash
        $restart = Invoke-C102 failures-restart $work.Arguments @('guide back','guide forward','guide view 1','guide','quit')
        $restart | Should -Match 'No previous guide topic'
        $restart | Should -Match 'No next guide topic'
        $restart | Should -Match 'Guide start; lines 1-1/'
        $restart | Should -Not -Match 'PRIVATE SYNTHETIC'
        @(Get-ChildItem $work.Profile -File -Filter '*guide*').Count | Should -Be 0
    }
}
