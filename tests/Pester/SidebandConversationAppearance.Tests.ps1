Describe 'Sideband bounded conversation appearance' {
    It 'T483 independently regenerates actual primary conversation badge policies and colors' -Tag 'Reference' {
        if (-not $env:DOTRETICULUM_PYTHON -or -not $env:DOTRETICULUM_SIDEBAND_UPSTREAM) {
            Set-ItResult -Skipped -Because 'Select the isolated pinned reference Python, RNS path and clean primary Sideband checkout.'
            return
        }
        $repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $output = Join-Path $TestDrive 'appearance.json'
        $previous = $env:PYTHONPATH
        try {
            $env:PYTHONPATH = (Join-Path $repository 'tests\Interop') + [IO.Path]::PathSeparator + $previous
            & $env:DOTRETICULUM_PYTHON (Join-Path $repository 'tests\Sideband.Tests\GenerateConversationAppearanceVectors.py') --source $env:DOTRETICULUM_SIDEBAND_UPSTREAM --output $output
            $LASTEXITCODE | Should -Be 0
            (Get-FileHash $output).Hash | Should -Be (Get-FileHash (
                Join-Path $repository 'tests\Sideband.Tests\Fixtures\sideband-conversation-appearance.json')).Hash
        }
        finally { $env:PYTHONPATH = $previous }
    }

    It 'T484 native settings activate custom trusted-only and disabled conversation badges across window restarts' -Tag 'GUI', 'Native' {
        if ($env:DOTRETICULUM_SIDEBAND_CONVERSATION_APPEARANCE -ne '1') {
            Set-ItResult -Skipped -Because 'Select an exact native binary/hash, fresh synthetic results and available desktop; no OS clipboard, user state or network is accessed.'
            return
        }
        $binary = $env:DOTRETICULUM_SIDEBAND_BINARY
        $root = $env:DOTRETICULUM_SIDEBAND_CONVERSATION_APPEARANCE_RESULTS
        $hash = $env:DOTRETICULUM_SIDEBAND_CONVERSATION_APPEARANCE_SHA256
        if (-not $binary -or -not $root -or -not $hash) { throw 'Select native binary, CONVERSATION_APPEARANCE_RESULTS/SHA256.' }
        (Get-FileHash $binary).Hash.ToLowerInvariant() | Should -Be $hash
        foreach ($mode in 'custom', 'trusted-only', 'disabled') {
            $data = Join-Path $root $mode
            if (Test-Path $data) { throw "Select fresh synthetic profile: $data" }
            $config = Join-Path $root "rns-$mode"
            $null = New-Item -ItemType Directory -Path $config -Force
            "[reticulum]`n share_instance = no`n[interfaces]`n" |
                Set-Content (Join-Path $config 'config') -Encoding utf8NoBOM
            $output = & $binary --init --data-dir $data --rnsconfig $config 2>&1 | Out-String
            $LASTEXITCODE | Should -Be 0 -Because $output
            $settings = Join-Path $root "$mode.json"
            @{display_style_in_contact_list=($mode -ne 'disabled');display_style_from_all=($mode -ne 'trusted-only')} |
                ConvertTo-Json | Set-Content $settings -Encoding utf8NoBOM
            $output = & $binary --import-settings $settings --data-dir $data 2>&1 | Out-String
            $LASTEXITCODE | Should -Be 0 -Because $output
            $path = Join-Path $data 'state.json'
            $state = Get-Content $path -Raw | ConvertFrom-Json
            $own = $state.ownDeliveryHash
            $peer = '00112233445566778899aabbccddeeff'
            $state.contacts = @(@{
                DeliveryHash=$peer;DisplayName='Synthetic styled peer';Trusted=$false
                Unread=$false;ConversationKnown=$true;NameIsUserDefined=$true
                Appearance=@{Icon='rocket';Foreground='336699';Background='ffcc66'}
            })
            $state.messages = @(1..3 | ForEach-Object { @{
                Id=[guid]::NewGuid().ToString();PeerDeliveryHash=$peer;Incoming=$true;Title='Synthetic'
                Content='Private-port smoke content';Timestamp=[DateTimeOffset]::UtcNow.AddSeconds($_).ToString('O')
                DeliveryState='Received';DeliveryMethod='Direct'
            } })
            $state | ConvertTo-Json -Depth 30 | Set-Content $path -Encoding utf8NoBOM
            foreach ($restart in 1, 2) {
                $process = [Diagnostics.Process]::new()
                $started = $false
                try {
                    $process.StartInfo.FileName = $binary
                    $process.StartInfo.UseShellExecute = $false
                    $process.StartInfo.RedirectStandardOutput = $true
                    $process.StartInfo.RedirectStandardError = $true
                    foreach ($argument in '--smoke-ui', '--smoke-message-actions', '--data-dir', $data) {
                        $process.StartInfo.ArgumentList.Add($argument)
                    }
                    $process.Start() | Should -BeTrue
                    $started = $true
                    $stdout = $process.StandardOutput.ReadToEndAsync()
                    $stderr = $process.StandardError.ReadToEndAsync()
                    if (-not $process.WaitForExit(30000)) {
                        $process.Kill($true); $process.WaitForExit()
                        throw 'Synthetic conversation badge window exceeded its deadline.'
                    }
                    $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
                    $output | Set-Content (Join-Path $root "$mode-$restart.log") -Encoding utf8NoBOM
                    $process.ExitCode | Should -Be 0 -Because $output
                    $expected = if ($mode -eq 'custom') { 'rocket/336699/ffcc66' } else { 'account-question/theme/theme' }
                    $output | Should -Match "SMOKE_UI_CONVERSATION_APPEARANCE=$expected"
                    $output | Should -Match 'SMOKE_UI_MESSAGE_COPY=private-port-exact-raw'
                    $state = Get-Content $path -Raw | ConvertFrom-Json
                    $state.ownDeliveryHash | Should -Be $own
                    @($state.messages).Count | Should -Be (3 - $restart)
                    $state.contacts[0].Trusted | Should -BeFalse
                    $state.contacts[0].Appearance.Icon | Should -Be 'rocket'
                }
                finally {
                    if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
                    $process.Dispose()
                }
            }
            $export = Join-Path $root "$mode-export.json"
            $output = & $binary --export-settings $export --data-dir $data 2>&1 | Out-String
            $LASTEXITCODE | Should -Be 0 -Because $output
            $saved = Get-Content $export -Raw | ConvertFrom-Json
            $saved.display_style_in_contact_list | Should -Be ($mode -ne 'disabled')
            $saved.display_style_from_all | Should -Be ($mode -ne 'trusted-only')
        }
        $process = [Diagnostics.Process]::new()
        $started = $false
        try {
            $process.StartInfo.FileName = $binary
            $process.StartInfo.UseShellExecute = $false
            $process.StartInfo.RedirectStandardInput = $true
            $process.StartInfo.RedirectStandardOutput = $true
            $process.StartInfo.RedirectStandardError = $true
            foreach ($argument in '--daemon', '--interactive', '--data-dir', (Join-Path $root 'custom'),
                '--rnsconfig', (Join-Path $root 'rns-custom')) {
                $process.StartInfo.ArgumentList.Add($argument)
            }
            $process.Start() | Should -BeTrue
            $started = $true
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            foreach ($command in 'config set general display_style_in_contact_list false',
                'config set telemetry display_style_from_all false', 'quit') {
                $process.StandardInput.WriteLine($command)
            }
            $process.StandardInput.Close()
            if (-not $process.WaitForExit(30000)) {
                $process.Kill($true); $process.WaitForExit()
                throw 'Native appearance console exceeded its deadline.'
            }
            $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
            $output | Set-Content (Join-Path $root 'console.log') -Encoding utf8NoBOM
            $process.ExitCode | Should -Be 0 -Because $output
            $state = Get-Content (Join-Path $root 'custom\state.json') -Raw | ConvertFrom-Json
            $state.importedSettings.display_style_in_contact_list | Should -BeFalse
            $state.importedSettings.display_style_from_all | Should -BeFalse
            @($state.messages).Count | Should -Be 1
            $state.contacts[0].Trusted | Should -BeFalse
        }
        finally {
            if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
            $process.Dispose()
        }
        (Get-FileHash $binary).Hash.ToLowerInvariant() | Should -Be $hash
    }
}
