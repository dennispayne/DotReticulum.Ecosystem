Describe 'Sideband bounded rich-message formatting' {
    It 'T577 independently regenerates executed primary sender selection and trusted message markup' -Tag 'Reference' {
        if (-not $env:DOTRETICULUM_PYTHON -or -not $env:DOTRETICULUM_SIDEBAND_UPSTREAM) {
            Set-ItResult -Skipped -Because 'Select isolated reference Python with declared Mistune/Kivy, pinned RNS/LXMF and the exact primary Sideband checkout.'
            return
        }
        $repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $output = Join-Path $TestDrive 'message-markup.json'
        $previous = $env:PYTHONPATH
        try {
            $env:PYTHONPATH = (Join-Path $repository 'tests\Interop') + [IO.Path]::PathSeparator + $previous
            & $env:DOTRETICULUM_PYTHON (Join-Path $repository 'tests\Sideband.Tests\GenerateMessageMarkupVectors.py') --source $env:DOTRETICULUM_SIDEBAND_UPSTREAM --output $output
            $LASTEXITCODE | Should -Be 0
            (Get-FileHash $output).Hash | Should -Be (Get-FileHash (
                Join-Path $repository 'tests\Sideband.Tests\Fixtures\sideband-message-markup.json')).Hash
        }
        finally { $env:PYTHONPATH = $previous }
    }

    It 'T578 selected native windows render both formats and trust policy across restart while preserving raw history' -Tag 'Native', 'GUI' {
        if ($env:DOTRETICULUM_SIDEBAND_MESSAGE_MARKUP -ne '1') {
            Set-ItResult -Skipped -Because 'Select exact native binary/hash, fresh synthetic output and desktop; no OS clipboard, network or audio devices are accessed.'
            return
        }
        $binary = $env:DOTRETICULUM_SIDEBAND_BINARY
        $root = $env:DOTRETICULUM_SIDEBAND_MESSAGE_MARKUP_RESULTS
        $hash = $env:DOTRETICULUM_SIDEBAND_MESSAGE_MARKUP_SHA256
        if (-not $binary -or -not $root -or -not $hash) { throw 'Select binary, MESSAGE_MARKUP_RESULTS/SHA256.' }
        (Get-FileHash $binary).Hash.ToLowerInvariant() | Should -Be $hash
        $root = Join-Path $root 'windows'
        if (Test-Path $root) { throw "Select a fresh synthetic output: $root" }
        $null = New-Item -ItemType Directory -Path $root -Force
        foreach ($mode in 'markdown', 'bbcode', 'untrusted', 'trusted') {
            $data = Join-Path $root $mode
            $config = Join-Path $root "rns-$mode"
            $null = New-Item -ItemType Directory -Path $config -Force
            "[reticulum]`n share_instance = no`n[interfaces]`n" |
                Set-Content (Join-Path $config 'config') -Encoding utf8NoBOM
            $output = & $binary --init --data-dir $data --rnsconfig $config 2>&1 | Out-String
            $LASTEXITCODE | Should -Be 0 -Because $output
            $settings = Join-Path $root "$mode.json"
            @{compose_in_markdown=$true;trusted_markup_only=($mode -in 'untrusted','trusted')} |
                ConvertTo-Json | Set-Content $settings -Encoding utf8NoBOM
            $output = & $binary --import-settings $settings --data-dir $data 2>&1 | Out-String
            $LASTEXITCODE | Should -Be 0 -Because $output
            $path = Join-Path $data 'state.json'
            $state = Get-Content $path -Raw | ConvertFrom-Json
            $own = $state.ownDeliveryHash
            $peer = '00112233445566778899aabbccddeeff'
            $content = if ($mode -eq 'bbcode') { '[b]bold[/b] and [i]italic[/i]' } else { '**bold** and *italic*' }
            $renderer = if ($mode -eq 'bbcode') { 3 } else { 2 }
            $state.contacts = @(@{DeliveryHash=$peer;DisplayName='Synthetic formatting peer';Trusted=($mode -eq 'trusted')
                Unread=$false;ConversationKnown=$true;NameIsUserDefined=$true})
            $state.messages = @(1..3 | ForEach-Object { @{
                Id=[guid]::NewGuid().ToString();PeerDeliveryHash=$peer;Incoming=$true;Title='Synthetic'
                Content=$content;Renderer=$renderer;Timestamp=[DateTimeOffset]::UtcNow.AddSeconds($_).ToString('O')
                DeliveryState='Received';DeliveryMethod='Direct'
            } })
            $state | ConvertTo-Json -Depth 30 | Set-Content $path -Encoding utf8NoBOM
            $displayed = if ($mode -eq 'untrusted') { $content } else { 'bold and italic' }
            $textHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
                [Text.Encoding]::UTF8.GetBytes($displayed))).ToLowerInvariant()
            $count = if ($mode -eq 'untrusted') { 0 } else { 1 }
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
                        throw 'Synthetic formatting window exceeded its deadline.'
                    }
                    $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
                    $output | Set-Content (Join-Path $root "$mode-$restart.log") -Encoding utf8NoBOM
                    $process.ExitCode | Should -Be 0 -Because $output
                    $output | Should -Match 'SMOKE_UI_RENDERED_INITIAL=1'
                    $output | Should -Match "SMOKE_UI_MESSAGE_FORMATTING=bold:$count;italic:$count;text-sha256:$textHash"
                    $output | Should -Match 'SMOKE_UI_MESSAGE_COPY=private-port-exact-raw'
                    $state = Get-Content $path -Raw | ConvertFrom-Json
                    $state.ownDeliveryHash | Should -Be $own
                    @($state.messages).Count | Should -Be (3 - $restart)
                    $state.contacts[0].Trusted | Should -Be ($mode -eq 'trusted')
                    @($state.messages | Where-Object { $_.Renderer -ne $renderer -or $_.Content -cne $content }).Count | Should -Be 0
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
            $saved.compose_in_markdown | Should -BeTrue
            $saved.trusted_markup_only | Should -Be ($mode -in 'untrusted','trusted')
        }
        (Get-FileHash $binary).Hash.ToLowerInvariant() | Should -Be $hash
    }
}
