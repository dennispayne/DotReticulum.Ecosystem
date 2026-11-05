BeforeAll {
    $script:C104Root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $script:C104Evidence = $env:DOTRETICULUM_NOMADNET_MICRON_EVIDENCE
    function Invoke-C104Process([string[]] $Arguments, [string[]] $Commands = @()) {
        $info = [Diagnostics.ProcessStartInfo]::new($env:DOTRETICULUM_NOMADNET_BINARY)
        $info.UseShellExecute = $false
        $info.RedirectStandardInput = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $info
        try {
            if (-not $process.Start()) { throw 'Native Micron process did not start.' }
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            foreach ($command in $Commands) { $process.StandardInput.WriteLine($command) }
            $process.StandardInput.Close()
            $null = $process.WaitForExitAsync().WaitAsync([TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult()
            $text = $stdout.GetAwaiter().GetResult()
            $errors = $stderr.GetAwaiter().GetResult()
            $process.ExitCode | Should -Be 0 -Because $errors
            $text.Length | Should -BeLessThan 2000000
            $label = [guid]::NewGuid().ToString('N')
            [IO.File]::WriteAllText((Join-Path $script:C104Evidence "$label.stdout"), $text)
            [IO.File]::WriteAllText((Join-Path $script:C104Evidence "$label.stderr"), $errors)
            return $text
        } finally {
            if ($process.Id -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
            $process.Dispose()
        }
    }
}

Describe 'Pinned bounded Micron grammar C104' -Tag 'MicronGrammar' {
    It 'T872 actual pinned parser independently regenerates its factual CSharp fixture' {
        if ($env:DOTRETICULUM_NOMADNET_MICRON -ne '1') {
            Set-ItResult -Skipped -Because 'Select C104 frozen native and isolated pinned reference environment.'
            return
        }
        $json = Join-Path $script:C104Evidence regenerated.json
        $csharp = Join-Path $script:C104Evidence regenerated.cs
        & $env:DOTRETICULUM_PYTHON (Join-Path $script:C104Root 'tests\Interop\GenerateNomadNetMicronGrammarVectors.py') `
            --output $json --csharp $csharp --observations (Join-Path $script:C104Evidence actual-imports.json)
        $LASTEXITCODE | Should -Be 0
        (Get-FileHash $csharp).Hash | Should -BeExactly (Get-FileHash (Join-Path $script:C104Root 'tests\NomadNet.Tests\NomadNetMicronGrammarVectors.cs')).Hash
        $vectors = Get-Content $json -Raw | ConvertFrom-Json
        $vectors.revision | Should -BeExactly 'f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2'
        @($vectors.cases).Count | Should -Be 76
        $failures = @($vectors.cases | Where-Object error)
        $failures.Count | Should -Be 1
        $failures[0].name | Should -BeExactly 'fold-empty-formatting-heading'
        $failures[0].error | Should -BeExactly 'TypeError'
        $parserBytes = $vectors.sourceBytes.'nomadnet/ui/textui/MicronParser.py'
        $parserBytes.gitBlobSha256 | Should -BeExactly '995a8fd8883ec57cd173d117b6758447e508ca2ddf96d8521260cc70e18a8e4e'
        $parserBytes.filesystemSha256 | Should -BeExactly '9932db8038b6380742e2586250dc77b2509b72d7ed8a6fdfd954f03602cf4e03'
        $parserBytes.equalAfterCrLfNormalization | Should -BeTrue
        @($vectors.images).Count | Should -Be 9
    }
    It 'T873 frozen Windows NativeAOT help and version run successfully' {
        if ($env:DOTRETICULUM_NOMADNET_MICRON -ne '1') {
            Set-ItResult -Skipped -Because 'Select the C104 Windows NativeAOT binary.'
            return
        }
        (Invoke-C104Process @('--help')) | Should -Match 'nomadnet'
        (Invoke-C104Process @('--version')) | Should -Match '1\.4\.4'
    }
    It 'T874 native malformed table fails explicitly then original authoring preview and restart recover' {
        if ($env:DOTRETICULUM_NOMADNET_MICRON -ne '1') {
            Set-ItResult -Skipped -Because 'Select C104 native grammar authoring and recovery acceptance.'
            return
        }
        $work = Join-Path $script:C104Evidence ('authoring-' + [guid]::NewGuid().ToString('N'))
        $profile = Join-Path $work profile
        $rns = Join-Path $work rns
        $null = New-Item $rns -ItemType Directory -Force
        [IO.File]::WriteAllText((Join-Path $rns config), "[reticulum]`nshare_instance=no`nenable_transport=no`n[logging]`nloglevel=0`n[interfaces]`n")
        $null = Invoke-C104Process @('--init','--config',$profile,'--rnsconfig',$rns)
        $path = Join-Path $profile settings.json
        $settings = Get-Content $path -Raw | ConvertFrom-Json
        $settings.clientAnnounceAtStart = $false
        $settings.periodicLxmfSync = $false
        $settings.introTime = 0
        $settings | ConvertTo-Json -Depth 10 | Set-Content $path -Encoding utf8NoBOM
        $arguments = @('--config',$profile,'--rnsconfig',$rns)
        $output = Invoke-C104Process $arguments @('edit mkdir pages','edit new pages grammar.mu',
            'edit text "`t513\n| A |\n|--|\n`t"','edit preview',
            'edit text ">Grammar\none`[]two\ntrailing`\n`t\n| A | B |\n|---|---|\n| one | two |\n`t"',
            'edit preview','edit save','quit')
        $output | Should -Match 'Micron table width'
        $output | Should -Match 'onetwo'
        $output | Should -Match 'trailing'
        $saved = Join-Path $profile 'storage\pages\grammar.mu'
        $hash = (Get-FileHash $saved).Hash
        $identity = (Get-FileHash (Join-Path $rns 'storage\nomadnet_identity')).Hash
        $restart = Invoke-C104Process $arguments @('edit open pages grammar.mu','edit preview','quit')
        $restart | Should -Not -Match 'Error:'
        $restart | Should -Match 'onetwo'
        (Get-FileHash $saved).Hash | Should -BeExactly $hash
        (Get-FileHash (Join-Path $rns 'storage\nomadnet_identity')).Hash | Should -BeExactly $identity
    }
    It 'T883 native pipe suppresses styles across seeded page preview conversation and RRC owners' {
        if ($env:DOTRETICULUM_NOMADNET_MICRON -ne '1') {
            Set-ItResult -Skipped -Because 'Requires the C104 actual-native owner seed and frozen candidate.'
            return
        }
        $seed = @(Get-ChildItem $script:C104Evidence -Directory -Filter 'styled-*' |
            Sort-Object Name | Select-Object -Last 1)
        $seed.Count | Should -Be 1 -Because 'T881 seeds synthetic source through actual public cache/message APIs.'
        $profile = Join-Path $seed[0].FullName profile
        $rns = Join-Path $seed[0].FullName rns
        $path = Join-Path $profile settings.json
        $settings = Get-Content $path -Raw | ConvertFrom-Json
        $settings.colorMode = '24bit'
        $settings | ConvertTo-Json -Depth 10 | Set-Content $path -Encoding utf8NoBOM
        $destination = '0123456789abcdef0123456789abcdef'
        $output = Invoke-C104Process @('--config',$profile,'--rnsconfig',$rns) @(
            "browse $destination /page/styled.mu","messages $destination","rrc open $destination",
            'rrc view room','edit open pages styled.mu','edit preview','quit')
        $output | Should -Not -Match ([char]27)
        $output | Should -Not -Match 'Error:'
        foreach ($label in 'BOLD-C104','FG-C104','BG-C104','BOLD-MESSAGE-C104','R-UNDER','BOLD-PREVIEW-C104') {
            $output | Should -Match ([regex]::Escape($label))
        }
    }
}
