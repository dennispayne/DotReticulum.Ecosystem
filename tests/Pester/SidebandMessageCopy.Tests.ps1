Describe 'Sideband exact raw selected-message copy' {
    It 'T474 native window copies raw empty control and bounded text into a private port without touching the OS clipboard' -Tag 'GUI', 'Native' {
        if ($env:DOTRETICULUM_SIDEBAND_MESSAGE_COPY -ne '1') {
            Set-ItResult -Skipped -Because 'Select an exact native binary/hash, persistent synthetic results and available desktop; only a private clipboard observation port is exercised.'
            return
        }
        $binary = $env:DOTRETICULUM_SIDEBAND_BINARY
        $root = $env:DOTRETICULUM_SIDEBAND_MESSAGE_COPY_RESULTS
        if (-not $binary -or -not $root -or -not $env:DOTRETICULUM_SIDEBAND_MESSAGE_COPY_SHA256) {
            throw 'Select DOTRETICULUM_SIDEBAND_BINARY and MESSAGE_COPY_SHA256/RESULTS.'
        }
        $hash = (Get-FileHash -LiteralPath $binary).Hash.ToLowerInvariant()
        $hash | Should -Be $env:DOTRETICULUM_SIDEBAND_MESSAGE_COPY_SHA256
        $cases = @(
            @{ Name = 'raw'; Text = "[b]raw[/b] **markdown**`n$([char]0xE6) $([char]0x3BB)" },
            @{ Name = 'empty'; Text = '' },
            @{ Name = 'controls'; Text = "raw$([char]0)`t`r`nend" },
            @{ Name = 'maximum'; Text = ('x' * 65536) }
        )
        foreach ($case in $cases) {
            $data = Join-Path $root $case.Name
            if (Test-Path -LiteralPath $data) { throw "Select fresh synthetic profile: $data" }
            $config = Join-Path $root "rns-$($case.Name)"
            $null = New-Item -ItemType Directory -Path $config -Force
            "[reticulum]`n share_instance = no`n[interfaces]`n" |
                Set-Content -LiteralPath (Join-Path $config 'config') -Encoding utf8NoBOM
            $output = & $binary --init --data-dir $data --rnsconfig $config 2>&1 | Out-String
            $LASTEXITCODE | Should -Be 0 -Because $output
            $path = Join-Path $data 'state.json'
            $state = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            $own = $state.ownDeliveryHash
            $peer = '00112233445566778899aabbccddeeff'
            $state.contacts = @([ordered]@{
                DeliveryHash = $peer; DisplayName = 'Synthetic copy peer'; Trusted = $true
                Unread = $false; ConversationKnown = $true; NameIsUserDefined = $true
            })
            $keep = [guid]::NewGuid().ToString()
            $state.messages = @(
                [ordered]@{
                    Id = [guid]::NewGuid().ToString(); PeerDeliveryHash = $peer; Incoming = $true
                    Title = 'Do not copy this title'; Content = $case.Text
                    Timestamp = [DateTimeOffset]::UtcNow.AddSeconds(-1).ToString('O'); DeliveryState = 'Received'; DeliveryMethod = 'Direct'
                },
                [ordered]@{
                    Id = $keep; PeerDeliveryHash = $peer; Incoming = $false
                    Title = 'Keep title'; Content = 'Keep unrelated content'
                    Timestamp = [DateTimeOffset]::UtcNow.ToString('O'); DeliveryState = 'Failed'; DeliveryMethod = 'Direct'
                }
            )
            $state | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $path -Encoding utf8NoBOM
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
                    $process.Kill($true)
                    $process.WaitForExit()
                    throw 'Synthetic native copy window exceeded its deadline.'
                }
                $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
                $output | Set-Content -LiteralPath (Join-Path $root "$($case.Name).log") -Encoding utf8NoBOM
                $process.ExitCode | Should -Be 0 -Because $output
                $output | Should -Match 'SMOKE_UI_MESSAGE_COPY=private-port-exact-raw'
                $output | Should -Match 'SMOKE_UI_MESSAGE_ACTIONS=confirmed-local-delete'
                $saved = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
                $saved.ownDeliveryHash | Should -Be $own
                @($saved.messages).Count | Should -Be 1
                $saved.messages[0].Id | Should -Be $keep
                $saved.messages[0].Content | Should -Be 'Keep unrelated content'
                $saved.contacts[0].Trusted | Should -BeTrue
            }
            finally {
                if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
                $process.Dispose()
            }
        }
        (Get-FileHash -LiteralPath $binary).Hash.ToLowerInvariant() | Should -Be $hash
    }

    It 'T475 independently regenerates actual primary raw text-copy observations and failures' -Tag 'Reference' {
        if (-not $env:DOTRETICULUM_PYTHON -or -not $env:DOTRETICULUM_SIDEBAND_UPSTREAM) {
            Set-ItResult -Skipped -Because 'Select the isolated pinned reference Python, RNS path and clean primary Sideband checkout.'
            return
        }
        $repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $output = Join-Path $TestDrive 'sideband-message-copy.json'
        $previous = $env:PYTHONPATH
        try {
            $env:PYTHONPATH = (Join-Path $repository 'tests\Interop') + [IO.Path]::PathSeparator + $previous
            & $env:DOTRETICULUM_PYTHON (Join-Path $repository 'tests\Sideband.Tests\GenerateMessageCopyVectors.py') --source $env:DOTRETICULUM_SIDEBAND_UPSTREAM --output $output
            $LASTEXITCODE | Should -Be 0
            (Get-FileHash -LiteralPath $output).Hash | Should -Be (Get-FileHash -LiteralPath (
                Join-Path $repository 'tests\Sideband.Tests\Fixtures\sideband-message-copy.json')).Hash
        }
        finally { $env:PYTHONPATH = $previous }
    }
}
