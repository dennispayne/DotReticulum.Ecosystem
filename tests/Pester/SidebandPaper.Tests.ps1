BeforeAll {
    function New-SidebandPaperProfile([string] $Directory, [string] $Binary) {
        New-Item -ItemType Directory -Force -Path $Directory | Out-Null
        $settings = Join-Path $Directory 'settings.json'
        '{"display_name":"Synthetic paper acceptance"}' | Set-Content -LiteralPath $settings
        $profile = Join-Path $Directory 'data'
        & $Binary --import-settings $settings --data-dir $profile 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not create isolated offline paper profile.' }
        $fixture = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\Sideband.Tests\Fixtures\sideband-paper.json') -Raw | ConvertFrom-Json
        $messageId = [guid]::NewGuid().ToString()
        $statePath = Join-Path $profile 'state.json'
        $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        $state.contacts = @(@{ DeliveryHash = $fixture.destination; DisplayName = 'Synthetic paper peer' })
        $state.messages = @(@{
            Id = $messageId; PeerDeliveryHash = $fixture.destination; Incoming = $false
            Title = ''; Content = $fixture.cases[0].content
            Timestamp = [DateTimeOffset]::UtcNow.ToString('O'); DeliveryState = 'Paper'
            DeliveryMethod = 'Paper'; PaperUri = $fixture.cases[0].uri
        })
        $state | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $statePath
        @{ Profile = $profile; MessageId = $messageId; Uri = $fixture.cases[0].uri }
    }
}

Describe 'sideband published offline paper export' -Tag 'Native' {
    BeforeAll {
        $paperNativeRoot = $null
        $binary = $env:DOTRETICULUM_SIDEBAND_BINARY
        if ([string]::IsNullOrWhiteSpace($binary) -or -not (Test-Path -LiteralPath $binary -PathType Leaf)) {
            throw 'Set DOTRETICULUM_SIDEBAND_BINARY to the complete published executable.'
        }
        $paperNativeRoot = Join-Path (Join-Path (Get-Location) 'artifacts\tests') "sideband-paper-native-$PID"
        $paper = New-SidebandPaperProfile $paperNativeRoot $binary
    }

    It 'exports the actual-reference saved URI and QR across process restarts without overwriting' {
        $uriPath = Join-Path $paperNativeRoot 'paper.txt'
        $pngPath = Join-Path $paperNativeRoot 'paper.png'
        & $binary --export-paper $paper.MessageId --paper-output $uriPath --data-dir $paper.Profile
        $LASTEXITCODE | Should -Be 0
        [IO.File]::ReadAllText($uriPath) | Should -BeExactly $paper.Uri
        & $binary --export-paper $paper.MessageId --paper-output $pngPath --paper-qr --data-dir $paper.Profile
        $LASTEXITCODE | Should -Be 0
        [Convert]::ToHexString([IO.File]::ReadAllBytes($pngPath)[0..7]) | Should -Be '89504E470D0A1A0A'
        foreach ($export in @(@($uriPath, $false), @($pngPath, $true))) {
            $original = (Get-FileHash -LiteralPath $export[0]).Hash
            $arguments = @('--export-paper', $paper.MessageId, '--paper-output', $export[0], '--data-dir', $paper.Profile)
            if ($export[1]) { $arguments += '--paper-qr' }
            & $binary @arguments 2>&1 | Out-Null
            $LASTEXITCODE | Should -Be 1
            (Get-FileHash -LiteralPath $export[0]).Hash | Should -Be $original
        }
        (Get-Content -LiteralPath (Join-Path $paper.Profile 'state.json') -Raw | ConvertFrom-Json).ownDeliveryHash | Should -BeNullOrEmpty
        Test-Path -LiteralPath (Join-Path $paper.Profile 'rns') | Should -BeFalse
    }

    It 'independently decodes the published PNG with Pillow and zxing-cpp rather than the managed encoder' {
        if ([string]::IsNullOrWhiteSpace($env:DOTRETICULUM_QR_PYTHON)) {
            Set-ItResult -Skipped -Because 'Set DOTRETICULUM_QR_PYTHON to an isolated Pillow/zxing-cpp 2.3.0 oracle.'
            return
        }
        $png = Join-Path $paperNativeRoot 'independent.png'
        & $binary --export-paper $paper.MessageId --paper-output $png --paper-qr --data-dir $paper.Profile
        $LASTEXITCODE | Should -Be 0
        $decoded = & $env:DOTRETICULUM_QR_PYTHON (Join-Path $PSScriptRoot '..\Interop\decode_paper_qr.py') $png
        $LASTEXITCODE | Should -Be 0
        $decoded | Should -BeExactly $paper.Uri
    }

    It 'rejects conflicting paper modes before creating a profile or opening Reticulum' {
        $absent = Join-Path $paperNativeRoot 'must-remain-absent'
        foreach ($arguments in @(
            @('--smoke-paper', '--data-dir', $absent),
            @('--paper-qr', '--data-dir', $absent),
            @('--export-paper', 'bad', '--data-dir', $absent),
            @('--export-paper', $paper.MessageId, '--paper-output', (Join-Path $paperNativeRoot 'bad.txt'), '--data-dir', $absent, '--rnsconfig', (Join-Path $paperNativeRoot 'rns'))
        )) {
            & $binary @arguments 2>&1 | Out-Null
            $LASTEXITCODE | Should -Be 1
            Test-Path -LiteralPath $absent | Should -BeFalse
        }
    }

    AfterAll {
        if ($paperNativeRoot -and (Test-Path -LiteralPath $paperNativeRoot)) {
            Remove-Item -LiteralPath $paperNativeRoot -Recurse -Force
        }
    }
}

Describe 'sideband published paper desktop rendering' -Tag 'GUI' {
    It 'renders the saved paper URI and original QR preview with no network or audio devices' {
        $binary = $env:DOTRETICULUM_SIDEBAND_BINARY
        if ([string]::IsNullOrWhiteSpace($binary) -or -not (Test-Path -LiteralPath $binary -PathType Leaf)) {
            throw 'Set DOTRETICULUM_SIDEBAND_BINARY to the complete published executable.'
        }
        $paperGuiRoot = Join-Path (Join-Path (Get-Location) 'artifacts\tests') "sideband-paper-gui-$PID"
        $process = [Diagnostics.Process]::new()
        $started = $false
        try {
            $paper = New-SidebandPaperProfile $paperGuiRoot $binary
            $process.StartInfo.FileName = $binary
            $process.StartInfo.UseShellExecute = $false
            $process.StartInfo.RedirectStandardOutput = $true
            $process.StartInfo.RedirectStandardError = $true
            foreach ($argument in @('--smoke-ui', '--smoke-paper', '--data-dir', $paper.Profile)) {
                $process.StartInfo.ArgumentList.Add($argument)
            }
            $process.Start() | Should -BeTrue
            $started = $true
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(30000)) {
                $process.Kill($true)
                $process.WaitForExit()
                throw "Paper rendering did not conclude: $($stdout.GetAwaiter().GetResult()) $($stderr.GetAwaiter().GetResult())"
            }
            $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
            $process.ExitCode | Should -Be 0 -Because $output
            $output | Should -Match 'SMOKE_UI_RENDERED_INITIAL=1'
            $output | Should -Match 'SMOKE_UI_RENDERED_PAPER=uri-and-qr'
            $output | Should -Match 'SMOKE_UI_RENDERED_PAPER_PIXELS=[1-9][0-9]*'
            $output | Should -Not -Match ([regex]::Escape($paper.Uri))
            (Get-Content -LiteralPath (Join-Path $paper.Profile 'state.json') -Raw | ConvertFrom-Json).ownDeliveryHash | Should -BeNullOrEmpty
            Test-Path -LiteralPath (Join-Path $paper.Profile 'rns') | Should -BeFalse
        }
        finally {
            if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
            $process.Dispose()
            if (Test-Path -LiteralPath $paperGuiRoot) { Remove-Item -LiteralPath $paperGuiRoot -Recurse -Force }
        }
    }
}
