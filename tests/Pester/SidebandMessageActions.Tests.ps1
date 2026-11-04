Describe 'Sideband saved-message desktop actions' -Tag 'GUI', 'Native' {
    It 'T381 renders details and removes only confirmed local entries across native window restarts' {
        if ($env:DOTRETICULUM_SIDEBAND_MESSAGE_ACTIONS -ne '1') {
            Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_SIDEBAND_MESSAGE_ACTIONS=1 with an exact selected native binary and available desktop; this edits a synthetic test profile only.'
            return
        }
        $binary = $env:DOTRETICULUM_SIDEBAND_BINARY
        if (-not $binary -or -not $env:DOTRETICULUM_SIDEBAND_MESSAGE_ACTIONS_SHA256) {
            throw 'Select DOTRETICULUM_SIDEBAND_BINARY and DOTRETICULUM_SIDEBAND_MESSAGE_ACTIONS_SHA256.'
        }
        $hash = (Get-FileHash -LiteralPath $binary).Hash.ToLowerInvariant()
        $hash | Should -Be $env:DOTRETICULUM_SIDEBAND_MESSAGE_ACTIONS_SHA256
        $root = if ($env:DOTRETICULUM_SIDEBAND_MESSAGE_ACTIONS_RESULTS) {
            $env:DOTRETICULUM_SIDEBAND_MESSAGE_ACTIONS_RESULTS
        } else { Join-Path $TestDrive 'message-actions' }
        $data = Join-Path $root 'profile'
        $config = Join-Path $root 'rns'
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
            DeliveryHash = $peer; DisplayName = 'Synthetic peer'; Trusted = $true
            Unread = $false; ConversationKnown = $true; NameIsUserDefined = $true
        })
        $ids = @(1..3 | ForEach-Object { [guid]::NewGuid().ToString() })
        $blob = [Text.Encoding]::UTF8.GetBytes('Synthetic shared attachment')
        $blobHash = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($blob))
        $null = New-Item -ItemType Directory -Path (Join-Path $data 'attachments') -Force
        [IO.File]::WriteAllBytes((Join-Path $data "attachments\$blobHash"), $blob)
        $state.messages = @(0..2 | ForEach-Object {
            [ordered]@{
                Id = $ids[$_]; PeerDeliveryHash = $peer; Incoming = $true; Title = 'Synthetic saved title'
                Content = "Synthetic message $_"; Timestamp = [DateTimeOffset]::UtcNow.AddSeconds($_).ToString('O')
                DeliveryState = 'Received'; DeliveryMethod = 'Direct'
                Attachments = @(@{ Name = 'shared.txt'; ContentHash = $blobHash; Length = $blob.Length })
            }
        })
        $state | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $path -Encoding utf8NoBOM
        $unchangedFiles = @(Get-ChildItem -LiteralPath $data -File -Recurse |
            Where-Object FullName -NE $path | ForEach-Object {
                "$($_.FullName):$((Get-FileHash -LiteralPath $_.FullName).Hash)"
            } | Sort-Object) -join '|'
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
                    $process.Kill($true)
                    $process.WaitForExit()
                    $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
                    $output | Set-Content -LiteralPath (Join-Path $root "restart-$restart.log")
                    throw "Message-action native window did not finish: $output"
                }
                $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
                $output | Set-Content -LiteralPath (Join-Path $root "restart-$restart.log")
                $process.ExitCode | Should -Be 0 -Because $output
                $output | Should -Match 'SMOKE_UI_RENDERED_INITIAL=1'
                $output | Should -Match 'SMOKE_UI_MESSAGE_ACTIONS=confirmed-local-delete'
                $state = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
                $state.ownDeliveryHash | Should -Be $own
                @($state.contacts).Count | Should -Be 1
                $state.contacts[0].trusted | Should -BeTrue
                @($state.messages).Count | Should -Be (3 - $restart)
                (@($state.messages.id) -join '|') | Should -Be ($ids[$restart..2] -join '|')
            }
            finally {
                if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
                $process.Dispose()
            }
        }
        $afterFiles = @(Get-ChildItem -LiteralPath $data -File -Recurse |
            Where-Object FullName -NE $path | ForEach-Object {
                "$($_.FullName):$((Get-FileHash -LiteralPath $_.FullName).Hash)"
            } | Sort-Object) -join '|'
        $afterFiles | Should -Be $unchangedFiles
        $stateHash = (Get-FileHash -LiteralPath $path).Hash
        foreach ($arguments in @(
            @('--smoke-message-actions', '--data-dir', $data),
            @('--smoke-ui', '--smoke-message-actions', '--smoke-paper', '--data-dir', $data),
            @('--smoke-ui', '--smoke-message-actions', '--data-dir', $data, '--rnsconfig', $config)
        )) {
            $output = & $binary @arguments 2>&1 | Out-String
            $LASTEXITCODE | Should -Be 1 -Because $output
            $output | Should -Match 'requires --smoke-ui'
            (Get-FileHash -LiteralPath $path).Hash | Should -Be $stateHash
        }
        foreach ($invalid in 'empty', 'malformed') {
            $invalidData = Join-Path $root $invalid
            $null = New-Item -ItemType Directory -Path $invalidData -Force
            if ($invalid -eq 'malformed') {
                '{"SchemaVersion":' | Set-Content -LiteralPath (Join-Path $invalidData 'state.json') -Encoding utf8NoBOM
            }
            $process = [Diagnostics.Process]::new()
            $started = $false
            try {
                $process.StartInfo.FileName = $binary
                $process.StartInfo.UseShellExecute = $false
                $process.StartInfo.RedirectStandardOutput = $true
                $process.StartInfo.RedirectStandardError = $true
                foreach ($argument in '--smoke-ui', '--smoke-message-actions', '--data-dir', $invalidData) {
                    $process.StartInfo.ArgumentList.Add($argument)
                }
                $process.Start() | Should -BeTrue
                $started = $true
                $stdout = $process.StandardOutput.ReadToEndAsync()
                $stderr = $process.StandardError.ReadToEndAsync()
                if (-not $process.WaitForExit(30000)) { throw "Invalid $invalid test-profile window did not finish." }
                $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
                $output | Set-Content -LiteralPath (Join-Path $root "$invalid.log")
                $process.ExitCode | Should -Be 2 -Because $output
                $output | Should -Match 'Message actions smoke (failed|could not load its test profile)'
                $output | Should -Not -Match 'SMOKE_UI_MESSAGE_ACTIONS=confirmed-local-delete'
            }
            finally {
                if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
                $process.Dispose()
            }
        }
        (Get-FileHash -LiteralPath $binary).Hash.ToLowerInvariant() | Should -Be $hash
    }
}
