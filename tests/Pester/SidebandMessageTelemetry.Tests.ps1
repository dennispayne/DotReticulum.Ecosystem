Describe 'Bounded selected-message telemetry capture and copy' {
    It 'T839 independently regenerates actual primary selected-message telemetry and link rounding' -Tag 'Reference' {
        if (-not $env:DOTRETICULUM_PYTHON -or -not $env:DOTRETICULUM_SIDEBAND_UPSTREAM) {
            Set-ItResult -Skipped -Because 'Select pinned reference Python, RNS and the actual Sideband checkout.'
            return
        }
        $repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $output = Join-Path $TestDrive 'sideband-message-telemetry.json'
        & $env:DOTRETICULUM_PYTHON (Join-Path $repository 'tests\Sideband.Tests\GenerateMessageTelemetryVectors.py') `
            --source $env:DOTRETICULUM_SIDEBAND_UPSTREAM --output $output
        $LASTEXITCODE | Should -Be 0
        (Get-FileHash $output).Hash | Should -Be (Get-FileHash (
            Join-Path $repository 'tests\Sideband.Tests\Fixtures\sideband-message-telemetry.json')).Hash
    }

    It 'T840 exact native private-provider windows copy different saved snapshots across restart without replacing legacy history' -Tag 'Native', 'GUI' {
        if ($env:DOTRETICULUM_SIDEBAND_MESSAGE_TELEMETRY -ne '1') {
            Set-ItResult -Skipped -Because 'Select exact Windows native binary/hash, fresh synthetic output and desktop.'
            return
        }
        if (-not $IsWindows) { throw 'This gate verifies Windows desktop windows, not other platforms.' }
        $binary = $env:DOTRETICULUM_SIDEBAND_BINARY
        $hash = $env:DOTRETICULUM_SIDEBAND_MESSAGE_TELEMETRY_SHA256
        $root = Join-Path $env:DOTRETICULUM_SIDEBAND_MESSAGE_TELEMETRY_RESULTS 'windows'
        if (-not $binary -or -not $hash -or -not $env:DOTRETICULUM_SIDEBAND_MESSAGE_TELEMETRY_RESULTS) {
            throw 'Select explicit binary/hash and synthetic results.'
        }
        if (Test-Path $root) { throw 'Use fresh native results.' }
        $null = New-Item $root -ItemType Directory -Force
        (Get-FileHash $binary).Hash.ToLowerInvariant() | Should -Be $hash
        $repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $fixture = Get-Content (Join-Path $repository 'tests\Sideband.Tests\Fixtures\sideband-message-telemetry.json') -Raw | ConvertFrom-Json
        $cases = @($fixture.cases | Where-Object name -EQ 'battery') + @($fixture.cases | Where-Object name -EQ 'information')
        $profile = Join-Path $root profile
        $config = Join-Path $root rns
        $null = New-Item $config -ItemType Directory
        "[reticulum]`n share_instance = no`n[interfaces]`n" | Set-Content (Join-Path $config config) -Encoding utf8NoBOM
        $output = & $binary --init --data-dir $profile --rnsconfig $config 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 0 -Because $output
        $path = Join-Path $profile state.json
        $state = Get-Content $path -Raw | ConvertFrom-Json
        $own = $state.ownDeliveryHash
        $peer = '00112233445566778899aabbccddeeff'
        $state.contacts = @(@{DeliveryHash=$peer;DisplayName='Synthetic telemetry peer';Trusted=$true
            Unread=$false;ConversationKnown=$true;NameIsUserDefined=$true})
        $legacy = [guid]::NewGuid().ToString()
        $state.messages = @(
            foreach ($case in $cases) {
                @{Id=[guid]::NewGuid().ToString();PeerDeliveryHash=$peer;Incoming=$true;Title='Synthetic'
                    Content=$case.name;Timestamp=[DateTimeOffset]::UtcNow.ToString('O');DeliveryState='Received'
                    DeliveryMethod='Direct';TelemetryHex=$case.wire_hex}
            }
            @{Id=$legacy;PeerDeliveryHash=$peer;Incoming=$false;Title='Legacy';Content='No telemetry'
                Timestamp=[DateTimeOffset]::UtcNow.ToString('O');DeliveryState='Failed';DeliveryMethod='Direct'}
        )
        $state | ConvertTo-Json -Depth 30 | Set-Content $path -Encoding utf8NoBOM
        foreach ($case in $cases) {
            $process = [Diagnostics.Process]::new()
            $started = $false
            try {
                $process.StartInfo.FileName = $binary
                $process.StartInfo.UseShellExecute = $false
                $process.StartInfo.RedirectStandardOutput = $true
                $process.StartInfo.RedirectStandardError = $true
                foreach ($argument in '--smoke-ui','--smoke-message-actions','--data-dir',$profile) {
                    $process.StartInfo.ArgumentList.Add($argument)
                }
                $process.Start() | Should -BeTrue
                $started = $true
                $stdout = $process.StandardOutput.ReadToEndAsync()
                $stderr = $process.StandardError.ReadToEndAsync()
                if (-not $process.WaitForExit(30000)) {
                    $process.Kill($true); $process.WaitForExit()
                    throw 'Synthetic selected-message telemetry window exceeded its deadline.'
                }
                $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
                $output | Set-Content (Join-Path $root "$($case.name).log") -Encoding utf8NoBOM
                $process.ExitCode | Should -Be 0 -Because $output
                $expected = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData(
                    [Text.Encoding]::UTF8.GetBytes([string]$case.copied[0])))
                $output | Should -Match "SMOKE_UI_MESSAGE_TELEMETRY_SHA256=$expected"
                $output | Should -Match 'SMOKE_UI_MESSAGE_COPY=private-port-exact-raw'
                $output | Should -Match 'SMOKE_UI_MESSAGE_ACTIONS=confirmed-local-delete'
                $saved = Get-Content $path -Raw | ConvertFrom-Json
                $saved.ownDeliveryHash | Should -Be $own
                $saved.contacts[0].Trusted | Should -BeTrue
            }
            finally {
                if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
                $process.Dispose()
            }
        }
        @($saved.messages).Count | Should -Be 1
        $saved.messages[0].Id | Should -Be $legacy
        $saved.messages[0].Content | Should -Be 'No telemetry'
        $saved.messages[0].TelemetryHex | Should -BeNullOrEmpty
        (Get-FileHash $binary).Hash.ToLowerInvariant() | Should -Be $hash
    }
}
