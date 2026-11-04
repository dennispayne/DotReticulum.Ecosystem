Describe 'NomadNet explicit OSC52 clipboard workflows' {
    It 'T367 regenerates exact executed-primary clipboard browser URL and authoring observations' {
        if ($env:DOTRETICULUM_NOMADNET_WORKFLOWS -ne '1') {
            Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_WORKFLOWS=1 with the pinned actual-primary environment.'
            return
        }
        foreach ($name in 'DOTRETICULUM_PYTHON', 'DOTRETICULUM_UPSTREAM', 'DOTRETICULUM_LXMF_UPSTREAM', 'DOTRETICULUM_NOMADNET_SOURCE') {
            if (-not [Environment]::GetEnvironmentVariable($name)) { throw "$name is required." }
        }
        $repository = if ($env:DOTRETICULUM_TEST_REPOSITORY) { $env:DOTRETICULUM_TEST_REPOSITORY }
            else { Split-Path (Split-Path $PSScriptRoot -Parent) -Parent }
        $env:PYTHONPATH = @($env:DOTRETICULUM_UPSTREAM, $env:DOTRETICULUM_LXMF_UPSTREAM,
            $env:DOTRETICULUM_NOMADNET_SOURCE) -join [IO.Path]::PathSeparator
        $fixture = Join-Path $TestDrive reference.json
        & $env:DOTRETICULUM_PYTHON (Join-Path $repository 'tests\Interop\GenerateNomadNetWorkflowVectors.py') `
            --output $fixture --observations (Join-Path $TestDrive imports.json)
        $LASTEXITCODE | Should -Be 0
        (Get-FileHash $fixture).Hash | Should -Be (Get-FileHash (
            Join-Path $repository 'tests\NomadNet.Tests\NomadNetWorkflowVectors.json')).Hash
    }

    It 'T368 native opt-in copies content only as one framed sequence persists policy and disables it after restart' {
        if ($env:DOTRETICULUM_NOMADNET_WORKFLOWS -ne '1') {
            Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_WORKFLOWS=1 with the selected NativeAOT executable.'
            return
        }
        if (-not $env:DOTRETICULUM_NOMADNET_BINARY) { throw 'DOTRETICULUM_NOMADNET_BINARY is required.' }
        $root = Join-Path $TestDrive 'native clipboard'
        $profileDirectory = Join-Path $root profile
        $rns = Join-Path $root rns
        $null = New-Item $rns -ItemType Directory -Force
        "[reticulum]`nshare_instance=no`nenable_transport=no`n[logging]`nloglevel=0`n[interfaces]`n" |
            Set-Content (Join-Path $rns config) -Encoding utf8NoBOM
        function Invoke-ClipboardNative([string[]] $arguments, [string[]] $commands) {
            $info = [Diagnostics.ProcessStartInfo]::new($env:DOTRETICULUM_NOMADNET_BINARY)
            $info.UseShellExecute = $false
            $info.WorkingDirectory = $root
            $info.RedirectStandardInput = $true
            $info.RedirectStandardOutput = $true
            $info.RedirectStandardError = $true
            $info.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
            $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
            foreach ($argument in $arguments) { $info.ArgumentList.Add($argument) }
            $process = [Diagnostics.Process]::new()
            $process.StartInfo = $info
            $started = $false
            try {
                $started = $process.Start()
                if (-not $started) { throw 'Clipboard process did not start.' }
                $output = $process.StandardOutput.ReadToEndAsync()
                $errors = $process.StandardError.ReadToEndAsync()
                foreach ($command in $commands) { $process.StandardInput.WriteLine($command) }
                $process.StandardInput.Close()
                $process.WaitForExitAsync().WaitAsync([TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult()
                $process.ExitCode | Should -Be 0 -Because $errors.GetAwaiter().GetResult()
                return $output.GetAwaiter().GetResult()
            }
            finally {
                if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
                $process.Dispose()
            }
        }
        $null = Invoke-ClipboardNative @('--init', '--config', $profileDirectory) @()
        $settingsPath = Join-Path $profileDirectory settings.json
        $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
        $settings.clipboardCopy | Should -BeFalse
        $settings.clientAnnounceAtStart = $false
        $settings.periodicLxmfSync = $false
        $settings | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding utf8NoBOM
        $id = 'a' * 64
        $content = "Synthetic $([char]0x03bb)`none`e]52;c;injected`a"
        $statePath = Join-Path $profileDirectory state.json
        $state = Get-Content $statePath -Raw | ConvertFrom-Json
        $state.messages = @(@{ id=$id;peerHash=('b' * 32);direction='inbound';timestamp='2025-01-01T00:00:00+00:00';
            title='Never copy this synthetic title';content=$content;status='received';importedAttachmentNames='Never copy an attachment' })
        $state | ConvertTo-Json -Depth 10 | Set-Content $statePath -Encoding utf8NoBOM
        $before = (Get-FileHash $statePath).Hash
        $arguments = @('--config', $profileDirectory, '--rnsconfig', $rns)
        $disabled = Invoke-ClipboardNative $arguments @("copy message $id", 'copy url', 'quit')
        $disabled | Should -Not -Match '\x1b\]52;'
        $enabled = Invoke-ClipboardNative $arguments @('clipboard on', "copy message $id", 'utility identity', 'quit')
        $frames = [regex]::Matches($enabled, '\x1b\]52;c;([A-Za-z0-9+/=]*)\x07')
        $frames.Count | Should -Be 1
        [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($frames[0].Groups[1].Value)) | Should -BeExactly $content
        $enabled | Should -Not -Match 'Never copy|injected'
        (Get-Content $settingsPath -Raw | ConvertFrom-Json).clipboardCopy | Should -BeTrue
        $restarted = Invoke-ClipboardNative $arguments @("copy message $id", 'clipboard off', "copy message $id", 'utility identity', 'quit')
        [regex]::Matches($restarted, '\x1b\]52;c;([A-Za-z0-9+/=]*)\x07').Count | Should -Be 1
        [regex]::Match($restarted, 'Speed test: ([0-9a-f]{32})').Groups[1].Value |
            Should -BeExactly ([regex]::Match($enabled, 'Speed test: ([0-9a-f]{32})').Groups[1].Value)
        [regex]::Match($enabled, 'Speed test: ([0-9a-f]{32})').Success | Should -BeTrue
        (Get-Content $settingsPath -Raw | ConvertFrom-Json).clipboardCopy | Should -BeFalse
        (Get-FileHash $statePath).Hash | Should -Be $before
    }
}
