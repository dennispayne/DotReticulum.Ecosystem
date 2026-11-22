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

    function Read-PaperPeerEvent($Process, [string] $Event) {
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(40)
        while ([DateTimeOffset]::UtcNow -lt $deadline) {
            $remaining = $deadline - [DateTimeOffset]::UtcNow
            if ($remaining -le [TimeSpan]::Zero) { break }
            $line = $Process.StandardOutput.ReadLineAsync().WaitAsync($remaining).GetAwaiter().GetResult()
            if ($null -eq $line) { throw "Primary paper peer ended before $Event." }
            if ($line.StartsWith('@@')) {
                $observation = $line.Substring(2) | ConvertFrom-Json
                if ($observation.event -eq $Event) { return $observation }
            }
        }
        throw "Primary paper peer did not report $Event before the deadline."
    }

    function Send-PaperPeerCommand($Process, [string] $Command, [string] $Event) {
        $Process.StandardInput.WriteLine($Command)
        $Process.StandardInput.Flush()
        Read-PaperPeerEvent $Process $Event
    }
}

Describe 'sideband primary historical paper migration' -Tag 'Native', 'PaperProfileImport' {
    It 'imports the actual primary pair and preserves native offline exports consumed by the actual primary recipient across restart' {
        if ([string]::IsNullOrWhiteSpace($env:DOTRETICULUM_SIDEBAND_PAPER_PROFILE)) {
            Set-ItResult -Skipped -Because 'Set DOTRETICULUM_SIDEBAND_PAPER_PROFILE to GenerateSidebandProfile.py --paper-profile output.'
            return
        }
        foreach ($name in 'DOTRETICULUM_SIDEBAND_BINARY', 'DOTRETICULUM_PYTHON',
            'DOTRETICULUM_UPSTREAM', 'DOTRETICULUM_SIDEBAND_UPSTREAM', 'DOTRETICULUM_QR_PYTHON') {
            if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) { throw "$name is required." }
        }
        $binary = $env:DOTRETICULUM_SIDEBAND_BINARY
        $source = $env:DOTRETICULUM_SIDEBAND_PAPER_PROFILE
        $metadata = Get-Content (Join-Path $source 'observations.json') -Raw | ConvertFrom-Json
        $row = $metadata.paper.rows[0]
        $root = Join-Path (Join-Path (Get-Location) 'artifacts\tests') "sideband-paper-import-$PID"
        $native = Join-Path $root 'native'
        $recipient = Join-Path $root 'recipient'
        New-Item -ItemType Directory -Force (Join-Path $recipient 'app_storage') | Out-Null
        Copy-Item (Join-Path $source 'paper-recipient\identity') (Join-Path $recipient 'app_storage\primary_identity')
        $process = $null
        try {
            $output = & $binary --import-profile (Join-Path $source 'app_storage') --data-dir $native 2>&1 | Out-String
            $LASTEXITCODE | Should -Be 0 -Because $output
            $output | Should -Match 'not authenticated'
            $statePath = Join-Path $native 'state.json'
            $state = Get-Content $statePath -Raw | ConvertFrom-Json
            $paper = @($state.messages | Where-Object DeliveryMethod -EQ 'Paper')
            $paper.Count | Should -Be 1
            $paper[0].PaperUri | Should -BeExactly $row.uri
            $paper[0].ImportedPaperEnvelope | Should -BeTrue
            $paper[0].Incoming | Should -BeFalse
            $initialState = (Get-FileHash $statePath).Hash
            for ($restart = 0; $restart -lt 2; $restart++) {
                $uriPath = Join-Path $root "restored-$restart.txt"
                $pngPath = Join-Path $root "restored-$restart.png"
                $output = & $binary --export-paper $paper[0].Id --paper-output $uriPath --data-dir $native 2>&1 | Out-String
                $LASTEXITCODE | Should -Be 0 -Because $output
                $output | Should -Match 'not authenticated'
                [IO.File]::ReadAllText($uriPath) | Should -BeExactly $row.uri
                & $binary --export-paper $paper[0].Id --paper-output $pngPath --paper-qr --data-dir $native | Out-Null
                $LASTEXITCODE | Should -Be 0
                $decoded = & $env:DOTRETICULUM_QR_PYTHON (Join-Path $PSScriptRoot '..\Interop\decode_paper_qr.py') $pngPath
                $LASTEXITCODE | Should -Be 0
                $decoded | Should -BeExactly $row.uri
                $process = [Diagnostics.Process]::new()
                $process.StartInfo.FileName = $env:DOTRETICULUM_PYTHON
                $process.StartInfo.UseShellExecute = $false
                $process.StartInfo.RedirectStandardInput = $true
                $process.StartInfo.RedirectStandardOutput = $true
                $process.StartInfo.RedirectStandardError = $true
                foreach ($argument in @((Join-Path $PSScriptRoot '..\Interop\sideband_paper_node.py'),
                    '--source', $env:DOTRETICULUM_SIDEBAND_UPSTREAM, '--config', (Join-Path $root "peer-$restart"),
                    '--profile', $recipient)) { $process.StartInfo.ArgumentList.Add($argument) }
                $process.Start() | Should -BeTrue
                $errors = $process.StandardError.ReadToEndAsync()
                (Read-PaperPeerEvent $process 'ready').dest | Should -BeExactly $row.destination
                Send-PaperPeerCommand $process "remember $($row.source) $($row.sourcePublicKey)" 'remembered' | Out-Null
                $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([IO.File]::ReadAllText($uriPath)))
                $ingested = Send-PaperPeerCommand $process "ingest $encoded" 'ingest'
                if ($restart -eq 0) { $ingested.response | Should -Match 'added to your conversation list' }
                else { $ingested.response | Should -Match 'already been processed' }
                $snapshot = Send-PaperPeerCommand $process "snapshot $($row.source)" 'snapshot'
                $snapshot.messages.Count | Should -Be 1
                $snapshot.messages[0].content | Should -BeExactly $row.content
                $snapshot.messages[0].signature_valid | Should -BeTrue
                $process.StandardInput.WriteLine('quit')
                $process.StandardInput.Flush()
                $process.WaitForExit(30000) | Should -BeTrue
                $process.ExitCode | Should -Be 0 -Because $errors.GetAwaiter().GetResult()
                $process.Dispose()
                $process = $null
                (Get-FileHash $statePath).Hash | Should -Be $initialState
            }
        }
        finally {
            if ($null -ne $process) {
                if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
                if ($null -ne $errors) {
                    $diagnostic = $errors.GetAwaiter().GetResult()
                    if (-not [string]::IsNullOrWhiteSpace($diagnostic)) { Write-Host "Primary paper peer stderr: $diagnostic" }
                }
                $process.Dispose()
            }
            if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force }
        }
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
        & $binary --export-paper $paper.MessageId --paper-output $uriPath --data-dir $paper.Profile | Out-Null
        $LASTEXITCODE | Should -Be 0
        [IO.File]::ReadAllText($uriPath) | Should -BeExactly $paper.Uri
        & $binary --export-paper $paper.MessageId --paper-output $pngPath --paper-qr --data-dir $paper.Profile | Out-Null
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
        & $binary --export-paper $paper.MessageId --paper-output $png --paper-qr --data-dir $paper.Profile | Out-Null
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

Describe 'sideband imported historical paper rendering' -Tag 'GUI', 'PaperProfileImport' {
    It 'renders the imported saved envelope offline through two native desktop lifetimes' {
        if ([string]::IsNullOrWhiteSpace($env:DOTRETICULUM_SIDEBAND_PAPER_PROFILE)) {
            Set-ItResult -Skipped -Because 'Set DOTRETICULUM_SIDEBAND_PAPER_PROFILE to the actual generated paper profile.'
            return
        }
        $binary = $env:DOTRETICULUM_SIDEBAND_BINARY
        $root = Join-Path (Join-Path (Get-Location) 'artifacts\tests') "sideband-paper-import-gui-$PID"
        $native = Join-Path $root 'native'
        $process = $null
        try {
            & $binary --import-profile $env:DOTRETICULUM_SIDEBAND_PAPER_PROFILE --data-dir $native 2>&1 | Out-Null
            $LASTEXITCODE | Should -Be 0
            $initial = Get-Content (Join-Path $native 'state.json') -Raw | ConvertFrom-Json
            $before = ConvertTo-Json -InputObject $initial.messages -Depth 20 -Compress
            for ($restart = 0; $restart -lt 2; $restart++) {
                $process = [Diagnostics.Process]::new()
                $process.StartInfo.FileName = $binary
                $process.StartInfo.UseShellExecute = $false
                $process.StartInfo.RedirectStandardOutput = $true
                $process.StartInfo.RedirectStandardError = $true
                foreach ($argument in @('--smoke-ui', '--smoke-paper', '--data-dir', $native)) {
                    $process.StartInfo.ArgumentList.Add($argument)
                }
                $process.Start() | Should -BeTrue
                $stdout = $process.StandardOutput.ReadToEndAsync()
                $stderr = $process.StandardError.ReadToEndAsync()
                $process.WaitForExit(30000) | Should -BeTrue
                $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
                $process.ExitCode | Should -Be 0 -Because $output
                $output | Should -Match 'SMOKE_UI_RENDERED_PAPER=uri-and-qr'
                $output | Should -Match 'SMOKE_UI_RENDERED_PAPER_PIXELS=[1-9][0-9]*'
                $process.Dispose()
                $process = $null
                $restored = Get-Content (Join-Path $native 'state.json') -Raw | ConvertFrom-Json
                (ConvertTo-Json -InputObject $restored.messages -Depth 20 -Compress) | Should -BeExactly $before
                $restored.ownDeliveryHash | Should -BeExactly $initial.ownDeliveryHash
            }
        }
        finally {
            if ($null -ne $process) {
                if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
                $process.Dispose()
            }
            if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force }
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
