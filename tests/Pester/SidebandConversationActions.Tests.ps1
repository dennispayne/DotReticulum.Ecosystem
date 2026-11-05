Describe 'Sideband selected local conversation actions' {
    It 'T532 regenerates actual primary clear/delete SQL and policy observations' -Tag 'Reference' {
        if (-not $env:DOTRETICULUM_PYTHON -or -not $env:DOTRETICULUM_SIDEBAND_UPSTREAM) {
            Set-ItResult -Skipped -Because 'Select the isolated pinned reference Python, RNS path and clean primary Sideband checkout.'
            return
        }
        $repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $output = Join-Path $TestDrive 'conversation-policy.json'
        $previous = $env:PYTHONPATH
        try {
            $env:PYTHONPATH = (Join-Path $repository 'tests\Interop') + [IO.Path]::PathSeparator + $previous
            & $env:DOTRETICULUM_PYTHON (Join-Path $repository 'tests\Sideband.Tests\GenerateConversationPolicyVectors.py') --source $env:DOTRETICULUM_SIDEBAND_UPSTREAM --output $output
            $LASTEXITCODE | Should -Be 0
            (Get-FileHash $output).Hash | Should -Be (Get-FileHash (
                Join-Path $repository 'tests\Sideband.Tests\Fixtures\sideband-conversation-policy.json')).Hash
        }
        finally { $env:PYTHONPATH = $previous }
    }

    It 'T533 native rendered confirmation, draft protection, local erasure and restart preserve unrelated state' -Tag 'Native', 'GUI' {
        if ($env:DOTRETICULUM_SIDEBAND_CONVERSATION_ACTIONS -ne '1') {
            Set-ItResult -Skipped -Because 'Select an exact native binary/hash, fresh synthetic output and desktop; no network, OS clipboard or physical audio is used.'
            return
        }
        $binary = $env:DOTRETICULUM_SIDEBAND_BINARY
        $root = $env:DOTRETICULUM_SIDEBAND_CONVERSATION_ACTIONS_RESULTS
        $hash = $env:DOTRETICULUM_SIDEBAND_CONVERSATION_ACTIONS_SHA256
        if (-not $binary -or -not $root -or -not $hash) { throw 'Select binary, CONVERSATION_ACTIONS_RESULTS/SHA256.' }
        (Get-FileHash $binary).Hash.ToLowerInvariant() | Should -Be $hash
        if (Test-Path $root) { throw "Select a fresh synthetic output: $root" }
        $null = New-Item -ItemType Directory -Path $root
        $config = Join-Path $root 'rns'
        $null = New-Item -ItemType Directory -Path $config
        "[reticulum]`n share_instance = no`n[interfaces]`n" | Set-Content (Join-Path $config 'config') -Encoding utf8NoBOM
        $data = Join-Path $root 'data'
        $output = & $binary --init --data-dir $data --rnsconfig $config 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 0 -Because $output
        $path = Join-Path $data 'state.json'
        $state = Get-Content $path -Raw | ConvertFrom-Json
        $own = $state.ownDeliveryHash
        $selected = '00112233445566778899aabbccddeeff'
        $other = 'ffeeddccbbaa99887766554433221100'
        $state.contacts = @($selected, $other | ForEach-Object {
            @{DeliveryHash=$_;DisplayName="Synthetic $_";Trusted=$true;Unread=$false;ConversationKnown=$true;NameIsUserDefined=$true}
        })
        $state.messages = @($selected, $other | ForEach-Object {
            @{Id=[guid]::NewGuid().ToString();PeerDeliveryHash=$_;Incoming=$true;Title='Synthetic'
                Content="History $_";Timestamp=[DateTimeOffset]::UtcNow.ToString('O');DeliveryState='Received';DeliveryMethod='Direct'}
        })
        $state | ConvertTo-Json -Depth 30 | Set-Content $path -Encoding utf8NoBOM
        $process = [Diagnostics.Process]::new()
        $started = $false
        try {
            $process.StartInfo.FileName = $binary
            $process.StartInfo.UseShellExecute = $false
            $process.StartInfo.RedirectStandardOutput = $true
            $process.StartInfo.RedirectStandardError = $true
            foreach ($argument in '--smoke-ui', '--smoke-conversation-actions', '--data-dir', $data) {
                $process.StartInfo.ArgumentList.Add($argument)
            }
            $process.Start() | Should -BeTrue
            $started = $true
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(30000)) {
                $process.Kill($true); $process.WaitForExit()
                throw 'Synthetic conversation-actions window exceeded its deadline.'
            }
            $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
            $output | Set-Content (Join-Path $root 'native-window.log') -Encoding utf8NoBOM
            $process.ExitCode | Should -Be 0 -Because $output
            $output | Should -Match 'SMOKE_UI_RENDERED_INITIAL=1'
            $output | Should -Match 'SMOKE_UI_CONVERSATION_ACTIONS=confirmed-clear-delete'
        }
        finally {
            if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
            $process.Dispose()
        }
        $output = & $binary --check --data-dir $data 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 0 -Because $output
        $saved = Get-Content $path -Raw | ConvertFrom-Json
        $saved.ownDeliveryHash | Should -Be $own
        @($saved.contacts).Count | Should -Be 1
        $saved.contacts[0].DeliveryHash | Should -Be $other
        $saved.contacts[0].Trusted | Should -BeTrue
        @($saved.messages).Count | Should -Be 1
        $saved.messages[0].PeerDeliveryHash | Should -Be $other
        $output = & $binary --smoke-conversation-actions --data-dir $data 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 1 -Because $output
        $output | Should -Match 'requires --smoke-ui'
        (Get-FileHash $binary).Hash.ToLowerInvariant() | Should -Be $hash
    }
}
