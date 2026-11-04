#Requires -Version 7.0
BeforeAll {
    function Start-SidebandVoiceProcess {
        param([string] $Binary, [string[]] $Arguments)
        $start = [Diagnostics.ProcessStartInfo]::new($Binary)
        $start.UseShellExecute = $false
        $start.RedirectStandardInput = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::Start($start)
        [pscustomobject]@{
            Process = $process
            Lines = [Collections.Generic.List[string]]::new()
            Read = $process.StandardOutput.ReadLineAsync()
            Error = $process.StandardError.ReadToEndAsync()
        }
    }

    function Send-SidebandVoiceLine {
        param($Peer, [string] $Line)
        $Peer.Process.StandardInput.WriteLine($Line)
        $Peer.Process.StandardInput.Flush()
    }

    function Wait-SidebandVoiceLine {
        param($Peer, [string] $Pattern, [int] $Occurrence = 1, [int] $TimeoutSeconds = 30)
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
            while ($Peer.Read -and $Peer.Read.IsCompleted) {
                $line = $Peer.Read.GetAwaiter().GetResult()
                if ($null -eq $line) { $Peer.Read = $null; break }
                if ($Peer.Lines.Count -ge 2000) { throw 'Unexpected excessive Sideband output.' }
                $Peer.Lines.Add($line)
                $Peer.Read = $Peer.Process.StandardOutput.ReadLineAsync()
            }

            $matching = @($Peer.Lines | Where-Object { $_ -match $Pattern })
            if ($matching.Count -ge $Occurrence) { return $matching[$Occurrence - 1] }
            if ($Peer.Process.HasExited -and !$Peer.Read) { break }
            Start-Sleep -Milliseconds 20
        }
        $errorText = if ($Peer.Error.IsCompleted) { $Peer.Error.GetAwaiter().GetResult() } else { '(process still running)' }
        throw "Sideband did not report $Pattern occurrence $Occurrence.`n$($Peer.Lines -join "`n")`n$errorText"
    }

    function Stop-SidebandVoiceProcess {
        param($Peer)
        if (!$Peer) { return }
        try {
            if (!$Peer.Process.HasExited) {
                Send-SidebandVoiceLine $Peer 'quit'
                $Peer.Process.StandardInput.Close()
                if (!$Peer.Process.WaitForExit(10000)) {
                    $Peer.Process.Kill($true)
                    throw 'Sideband did not shut down gracefully within ten seconds.'
                }
            }
            $Peer.Process.WaitForExit()
            $Peer.Process.ExitCode | Should -Be 0
        }
        finally { $Peer.Process.Dispose() }
    }
}

Describe 'sideband frozen native voice endurance' -Tag 'Native', 'VoiceSoak' {
    It 'holds one bidirectional call for an actual hour, then recovers from peer loss and reloads identity/history' {
        if ($env:DOTRETICULUM_SIDEBAND_SOAK -ne '1') {
            Set-ItResult -Skipped -Because 'Set DOTRETICULUM_SIDEBAND_SOAK=1 and select VoiceSoak for the explicit one-hour frozen-binary acceptance.'
            return
        }
        $binary = $env:DOTRETICULUM_SIDEBAND_BINARY
        $expectedHash = $env:DOTRETICULUM_SIDEBAND_SOAK_SHA256
        if (!$binary -or !(Test-Path -LiteralPath $binary -PathType Leaf) -or $expectedHash -notmatch '^[0-9a-fA-F]{64}$') {
            throw 'Supply the frozen executable and its expected DOTRETICULUM_SIDEBAND_SOAK_SHA256.'
        }
        $hash = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant()
        $hash | Should -Be $expectedHash.ToLowerInvariant()
        $root = Join-Path (Join-Path (Get-Location) 'artifacts\tests') ('sideband-soak-' + [Guid]::NewGuid().ToString('N'))
        $evidencePath = $env:DOTRETICULUM_SIDEBAND_SOAK_EVIDENCE
        if (!$evidencePath) { throw 'Set DOTRETICULUM_SIDEBAND_SOAK_EVIDENCE to a retained JSON evidence path.' }
        New-Item -ItemType Directory -Force (Split-Path -Parent $evidencePath) | Out-Null
        $evidence = [ordered]@{
            Binary = $binary; Sha256 = $hash; Platform = [Runtime.InteropServices.RuntimeInformation]::OSDescription
            Audio = 'Explicit synthetic capture/discard output; no Python or physical audio'
            RequiredHoldSeconds = 3600; SampleIntervalSeconds = 30
            MaximumWorkingSetBytes = 512MB; MaximumGrowthAfterWarmupBytes = 128MB
            StartedUtc = [DateTimeOffset]::UtcNow.ToString('O'); Stage = 'setup'; Passed = $false
            Samples = [Collections.Generic.List[object]]::new()
            Restart = [Collections.Generic.List[object]]::new()
        }
        $server = $null
        $client = $null
        function Save-SoakEvidence {
            $evidence | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $evidencePath
        }
        function Read-SoakStatus($Peer) {
            if ($Peer.Process.HasExited) { throw "Owned peer $($Peer.Process.Id) exited unexpectedly." }
            $occurrence = @($Peer.Lines | Where-Object { $_ -match '^STATUS ' }).Count + 1
            Send-SidebandVoiceLine $Peer 'voice status'
            $line = Wait-SidebandVoiceLine $Peer '^STATUS ' $occurrence
            $match = [regex]::Match($line, '^STATUS voice=(\w+) .*profile=(\d*) mode=(\d*) rx=(\d+) tx=(\d+)')
            if (!$match.Success) { throw "Unrecognised native status: $line" }
            $Peer.Process.Refresh()
            [pscustomobject]@{
                Pid = $Peer.Process.Id; State = $match.Groups[1].Value; Profile = $match.Groups[2].Value
                Rx = [long]$match.Groups[4].Value; Tx = [long]$match.Groups[5].Value
                WorkingSetBytes = $Peer.Process.WorkingSet64; PeakWorkingSetBytes = $Peer.Process.PeakWorkingSet64
            }
        }
        function Wait-SoakTraffic($First, $Second, [int]$CodecProfile = 64) {
            $watch = [Diagnostics.Stopwatch]::StartNew()
            do {
                $firstStatus = Read-SoakStatus $First
                $secondStatus = Read-SoakStatus $Second
                if ($firstStatus.State -eq 'InCall' -and $secondStatus.State -eq 'InCall' -and
                    $firstStatus.Profile -eq "$CodecProfile" -and $secondStatus.Profile -eq "$CodecProfile" -and
                    $firstStatus.Rx -ge 5 -and $firstStatus.Tx -ge 5 -and
                    $secondStatus.Rx -ge 5 -and $secondStatus.Tx -ge 5) {
                    return @($firstStatus, $secondStatus)
                }
                Start-Sleep -Milliseconds 100
            } while ($watch.Elapsed.TotalSeconds -lt 30)
            throw 'The frozen pair did not establish actual bidirectional codec progress.'
        }
        try {
            $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
            try { $listener.Start(); $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
            finally { $listener.Stop() }
            New-Item -ItemType Directory -Force -Path (Join-Path $root 'server-rns'), (Join-Path $root 'client-rns') | Out-Null
            $common = "[reticulum]`n  enable_transport = no`n  share_instance = no`n`n[logging]`n  loglevel = 0`n`n[interfaces]`n"
            Set-Content -LiteralPath (Join-Path $root 'server-rns\config') -Value ($common +
                "  [[Calls]]`n    type = TCPServerInterface`n    enabled = yes`n    listen_ip = 127.0.0.1`n    listen_port = $port`n")
            Set-Content -LiteralPath (Join-Path $root 'client-rns\config') -Value ($common +
                "  [[Calls]]`n    type = TCPClientInterface`n    enabled = yes`n    target_host = 127.0.0.1`n    target_port = $port`n")
            $settings = Join-Path $root 'voice.json'
            '{"voice_enabled":true}' | Set-Content -LiteralPath $settings
            foreach ($name in 'server', 'client') {
                & $binary --config (Join-Path $root $name) --import-settings $settings 2>&1 | Out-Null
                $LASTEXITCODE | Should -Be 0
            }
            $serverArguments = @('--daemon', '--interactive', '--verbose', '--synthetic-audio', '--config', (Join-Path $root 'server'),
                '--rnsconfig', (Join-Path $root 'server-rns'))
            $clientArguments = @('--daemon', '--interactive', '--verbose', '--synthetic-audio', '--config', (Join-Path $root 'client'),
                '--rnsconfig', (Join-Path $root 'client-rns'))
            $server = Start-SidebandVoiceProcess $binary $serverArguments
            $serverReady = Wait-SidebandVoiceLine $server '^READY identity=([0-9a-f]{32})'
            $client = Start-SidebandVoiceProcess $binary $clientArguments
            $clientReady = Wait-SidebandVoiceLine $client '^READY identity=([0-9a-f]{32})'
            $serverIdentity = [regex]::Match($serverReady, 'identity=([0-9a-f]{32})').Groups[1].Value
            $clientIdentity = [regex]::Match($clientReady, 'identity=([0-9a-f]{32})').Groups[1].Value
            $evidence.ServerIdentity = $serverIdentity
            $evidence.ClientIdentity = $clientIdentity
            Send-SidebandVoiceLine $server 'voice announce'
            Send-SidebandVoiceLine $client 'voice announce'
            Send-SidebandVoiceLine $client "voice dial $serverIdentity"
            Wait-SidebandVoiceLine $server '^VOICE state=Ringing' | Out-Null
            Send-SidebandVoiceLine $server 'voice answer'
            $previous = Wait-SoakTraffic $server $client
            $availableBefore = @(
                @($server.Lines | Where-Object { $_ -match '^VOICE state=Available' }).Count
                @($client.Lines | Where-Object { $_ -match '^VOICE state=Available' }).Count
            )
            $evidence.Stage = 'holding'
            $evidence.HoldStartedUtc = [DateTimeOffset]::UtcNow.ToString('O')
            $evidence.Samples.Add([ordered]@{ ElapsedSeconds = 0; Utc = $evidence.HoldStartedUtc; Peers = $previous })
            $watch = [Diagnostics.Stopwatch]::StartNew()
            $warm = @{}
            $codecProfile = 64
            Save-SoakEvidence
            do {
                Start-Sleep -Seconds 30
                $current = @(Read-SoakStatus $server; Read-SoakStatus $client)
                for ($index = 0; $index -lt 2; $index++) {
                    $now = $current[$index]
                    $now.State | Should -Be 'InCall'
                    $now.Profile | Should -Be "$codecProfile"
                    $now.Rx | Should -BeGreaterThan $previous[$index].Rx
                    $now.Tx | Should -BeGreaterThan $previous[$index].Tx
                    $now.WorkingSetBytes | Should -BeLessOrEqual 512MB
                    if ($watch.Elapsed.TotalSeconds -ge 120 -and !$warm.ContainsKey($now.Pid)) {
                        $warm[$now.Pid] = $now.WorkingSetBytes
                    }
                    if ($warm.ContainsKey($now.Pid)) {
                        ($now.WorkingSetBytes - $warm[$now.Pid]) | Should -BeLessOrEqual 128MB
                    }
                    $peer = @($server, $client)[$index]
                    @($peer.Lines | Where-Object { $_ -match '^VOICE state=Available' }).Count |
                        Should -Be $availableBefore[$index] -Because 'The hour must contain no observed call teardown.'
                }
                $evidence.Samples.Add([ordered]@{
                        ElapsedSeconds = $watch.Elapsed.TotalSeconds; Utc = [DateTimeOffset]::UtcNow.ToString('O'); Peers = $current
                    })
                Save-SoakEvidence
                Write-Host ("SOAK {0} elapsed={1:F1}s profile={2} server_rx={3} client_rx={4}" -f
                    $evidence.Platform, $watch.Elapsed.TotalSeconds, $codecProfile, $current[0].Rx, $current[1].Rx)
                $previous = $current
                if ($codecProfile -eq 64 -and $watch.Elapsed.TotalSeconds -ge 1800) {
                    Send-SidebandVoiceLine $client 'voice profile 48'
                    Wait-SidebandVoiceLine $client '^OK voice profile' | Out-Null
                    $codecProfile = 48
                    $evidence.Codec2RequestedAtSeconds = $watch.Elapsed.TotalSeconds
                }
            } while ($watch.Elapsed.TotalSeconds -lt 3600)
            $evidence.ActualHoldSeconds = $watch.Elapsed.TotalSeconds
            $evidence.HoldEndedUtc = [DateTimeOffset]::UtcNow.ToString('O')
            $evidence.Stage = 'peer-failure'
            $evidence.KilledOwnedPid = $client.Process.Id
            $client.Process.Kill($true)
            $client.Process.WaitForExit()
            $client.Process.Dispose()
            $client = $null
            $recovery = [Diagnostics.Stopwatch]::StartNew()
            Wait-SidebandVoiceLine $server '^VOICE state=Available' -Occurrence ($availableBefore[0] + 1) -TimeoutSeconds 120 | Out-Null
            $evidence.PeerLossDetectedSeconds = $recovery.Elapsed.TotalSeconds
            $client = Start-SidebandVoiceProcess $binary $clientArguments
            Wait-SidebandVoiceLine $client '^READY identity=' | Should -Match "identity=$clientIdentity"
            Send-SidebandVoiceLine $client 'voice announce'
            Send-SidebandVoiceLine $server 'voice announce'
            Send-SidebandVoiceLine $server "voice dial $clientIdentity"
            Wait-SidebandVoiceLine $client '^VOICE state=Ringing' | Out-Null
            Send-SidebandVoiceLine $client 'voice answer'
            $evidence.RecoveredTraffic = Wait-SoakTraffic $server $client
            $clientAvailableBeforeHangup = @($client.Lines | Where-Object { $_ -match '^VOICE state=Available' }).Count
            Send-SidebandVoiceLine $server 'voice hangup'
            Wait-SidebandVoiceLine $server '^OK voice hangup' | Out-Null
            Wait-SidebandVoiceLine $server '^VOICE state=Available' -Occurrence ($availableBefore[0] + 2) | Out-Null
            Wait-SidebandVoiceLine $client '^VOICE state=Available' -Occurrence ($clientAvailableBeforeHangup + 1) | Out-Null
            Stop-SidebandVoiceProcess $client
            $client = $null
            Stop-SidebandVoiceProcess $server
            $server = $null
            $evidence.Stage = 'graceful-restart'
            foreach ($name in 'server', 'client') {
                $state = Get-Content -LiteralPath (Join-Path $root "$name\state.json") -Raw | ConvertFrom-Json
                @($state.Calls | Where-Object Event -In @('incoming-success', 'outgoing-success')).Count | Should -BeGreaterOrEqual 2
                @($state.Calls | Where-Object Event -EQ 'ongoing-ended').Count | Should -BeGreaterOrEqual 1
                $arguments = if ($name -eq 'server') { $serverArguments } else { $clientArguments }
                $identity = if ($name -eq 'server') { $serverIdentity } else { $clientIdentity }
                $peer = Start-SidebandVoiceProcess $binary $arguments
                try {
                    Wait-SidebandVoiceLine $peer '^READY identity=' | Should -Match "identity=$identity"
                    Send-SidebandVoiceLine $peer 'voice log'
                    Wait-SidebandVoiceLine $peer '^CALL .* ongoing-ended ' | Out-Null
                    $evidence.Restart.Add([ordered]@{
                            Peer = $name; Identity = $identity; Events = @($state.Calls | ForEach-Object Event)
                            HistoryReloaded = $true; Pid = $peer.Process.Id
                        })
                }
                finally { Stop-SidebandVoiceProcess $peer }
            }
            (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant() | Should -Be $hash
            $evidence.Stage = 'complete'
            $evidence.Passed = $true
        }
        catch {
            $evidence.Failure = $_.ToString()
            throw
        }
        finally {
            try { Stop-SidebandVoiceProcess $client }
            finally {
                try { Stop-SidebandVoiceProcess $server }
                finally {
                    $evidence.FinishedUtc = [DateTimeOffset]::UtcNow.ToString('O')
                    Save-SoakEvidence
                    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
                }
            }
        }
    }
}
Describe 'sideband native daemon cancellation acceptance' -Tag 'Native' {
    It 'cancels on SIGTERM with stdin blocked or closed (interactive=<Interactive>)' -TestCases @(
        @{ Interactive = $true }, @{ Interactive = $false }
    ) {
        param($Interactive)
        if (!$IsLinux) {
            Set-ItResult -Skipped -Because 'POSIX SIGTERM acceptance requires Linux; Windows console quit is exercised separately.'
            return
        }
        $binary = $env:DOTRETICULUM_SIDEBAND_BINARY
        if (!$binary -or !(Test-Path -LiteralPath $binary -PathType Leaf)) {
            throw 'Set DOTRETICULUM_SIDEBAND_BINARY to the published sideband executable.'
        }
        $root = Join-Path (Join-Path (Get-Location) 'artifacts\tests') ('sideband-daemon-signal-' + [Guid]::NewGuid().ToString('N'))
        $peer = $null
        try {
            $config = Join-Path $root 'rns'
            New-Item -ItemType Directory -Force $config | Out-Null
            "[reticulum]`n  enable_transport = no`n  share_instance = no`n`n[logging]`n  loglevel = 0`n`n[interfaces]`n" |
                Set-Content -LiteralPath (Join-Path $config 'config')
            $arguments = @('--daemon', '--synthetic-audio', '--config', (Join-Path $root 'data'), '--rnsconfig', $config)
            if ($Interactive) { $arguments += '--interactive' }
            $peer = Start-SidebandVoiceProcess $binary $arguments
            Wait-SidebandVoiceLine $peer '^READY identity=' | Out-Null
            if (!$Interactive) { $peer.Process.StandardInput.Close() }
            Start-Sleep -Milliseconds 300
            $peer.Process.HasExited | Should -BeFalse
            & /bin/kill -TERM $peer.Process.Id
            $LASTEXITCODE | Should -Be 0
            $peer.Process.WaitForExit(3000) | Should -BeTrue -Because 'SIGTERM must cancel blocked input and release application ownership.'
            $peer.Process.ExitCode | Should -Be 0
        }
        finally {
            if ($peer) {
                if (!$peer.Process.HasExited) { $peer.Process.Kill($true); $peer.Process.WaitForExit() }
                $peer.Process.Dispose()
            }
            if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
        }
    }
}

Describe 'sideband native voice application acceptance' -Tag 'Native' {
    BeforeAll {
        $binary = $env:DOTRETICULUM_SIDEBAND_BINARY
        if (!$binary -or !(Test-Path -LiteralPath $binary -PathType Leaf)) {
            throw 'Set DOTRETICULUM_SIDEBAND_BINARY to the published sideband executable.'
        }
    }

    It 'rejects conflicting daemon and offline options before creating state' {
        $target = Join-Path (Join-Path (Get-Location) 'artifacts\tests') ('sideband-invalid-mode-' + [Guid]::NewGuid().ToString('N'))
        try {
            foreach ($options in @(@('--daemon', '--init'), @('--interactive'), @('--synthetic-audio'))) {
                $message = & $binary @options --config $target 2>&1 | Out-String
                $LASTEXITCODE | Should -Be 1
                $message | Should -Match 'cannot be combined|require --daemon'
                Test-Path -LiteralPath $target | Should -BeFalse
            }
        }
        finally {
            if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
        }
    }

    It 'exchanges actual Opus and Codec2 frames with synthetic devices and recovers after restart (reverse=<Reverse>)' -TestCases @(
        @{ Reverse = $false }, @{ Reverse = $true }
    ) {
        param($Reverse)
        $root = Join-Path (Join-Path (Get-Location) 'artifacts\tests') ('sideband-native-voice-' + [Guid]::NewGuid().ToString('N'))
        $server = $null
        $client = $null
        try {
            $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
            $listener.Start()
            $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
            $listener.Stop()
            New-Item -ItemType Directory -Force -Path (Join-Path $root 'server-rns'), (Join-Path $root 'client-rns') | Out-Null
            $common = "[reticulum]`n  enable_transport = no`n  share_instance = no`n`n[logging]`n  loglevel = 0`n`n[interfaces]`n"
            Set-Content -LiteralPath (Join-Path $root 'server-rns\config') -Value ($common +
                "  [[Calls]]`n    type = TCPServerInterface`n    enabled = yes`n    listen_ip = 127.0.0.1`n    listen_port = $port`n")
            Set-Content -LiteralPath (Join-Path $root 'client-rns\config') -Value ($common +
                "  [[Calls]]`n    type = TCPClientInterface`n    enabled = yes`n    target_host = 127.0.0.1`n    target_port = $port`n")
            $settings = Join-Path $root 'voice.json'
            '{"voice_enabled":true}' | Set-Content -LiteralPath $settings
            foreach ($name in 'server', 'client') {
                & $binary --config (Join-Path $root $name) --import-settings $settings 2>&1 | Out-Null
                $LASTEXITCODE | Should -Be 0
            }
            $serverArguments = @('--daemon', '--interactive', '--verbose', '--synthetic-audio', '--config', (Join-Path $root 'server'),
                '--rnsconfig', (Join-Path $root 'server-rns'))
            $clientArguments = @('--daemon', '--interactive', '--verbose', '--synthetic-audio', '--config', (Join-Path $root 'client'),
                '--rnsconfig', (Join-Path $root 'client-rns'))
            $server = Start-SidebandVoiceProcess $binary $serverArguments
            $serverReady = Wait-SidebandVoiceLine $server '^READY identity=([0-9a-f]{32})'
            $client = Start-SidebandVoiceProcess $binary $clientArguments
            $clientReady = Wait-SidebandVoiceLine $client '^READY identity=([0-9a-f]{32})'
            $serverIdentity = [regex]::Match($serverReady, 'identity=([0-9a-f]{32})').Groups[1].Value
            $clientIdentity = [regex]::Match($clientReady, 'identity=([0-9a-f]{32})').Groups[1].Value
            $caller = if ($Reverse) { $server } else { $client }
            $callee = if ($Reverse) { $client } else { $server }
            $target = if ($Reverse) { $clientIdentity } else { $serverIdentity }
            Send-SidebandVoiceLine $server 'voice announce'
            Send-SidebandVoiceLine $client 'voice announce'
            Send-SidebandVoiceLine $caller "voice dial $target"
            Wait-SidebandVoiceLine $callee '^VOICE state=Ringing' | Out-Null
            Send-SidebandVoiceLine $callee 'voice answer'
            foreach ($peer in $server, $client) {
                Wait-SidebandVoiceLine $peer '^VOICE state=InCall' | Out-Null
            }
            $counters = @{}
            foreach ($profile in 64, 48) {
                if ($profile -eq 48) {
                    Send-SidebandVoiceLine $caller 'voice profile 48'
                    Wait-SidebandVoiceLine $caller '^OK voice profile' | Out-Null
                }
                foreach ($peer in $server, $client) {
                    $watch = [Diagnostics.Stopwatch]::StartNew()
                    do {
                        $occurrence = @($peer.Lines | Where-Object { $_ -match '^STATUS ' }).Count + 1
                        Send-SidebandVoiceLine $peer 'voice status'
                        $status = Wait-SidebandVoiceLine $peer '^STATUS ' $occurrence
                        $observed = [regex]::Match($status, "profile=$profile .*rx=(\d+) tx=(\d+)")
                        $before = $counters[$peer.Process.Id]
                        $minimumRx = if ($before) { $before[0] + 3 } else { 5 }
                        $minimumTx = if ($before) { $before[1] + 3 } else { 5 }
                        if ($observed.Success -and [long]$observed.Groups[1].Value -ge $minimumRx -and [long]$observed.Groups[2].Value -ge $minimumTx) {
                            $counters[$peer.Process.Id] = @([long]$observed.Groups[1].Value, [long]$observed.Groups[2].Value)
                            break
                        }
                        if ($watch.Elapsed.TotalSeconds -gt 20) { throw "No actual bidirectional codec traffic: $status" }
                        Start-Sleep -Milliseconds 100
                    } while ($true)
                }
            }
            Send-SidebandVoiceLine $caller 'voice mute mic'
            Wait-SidebandVoiceLine $caller '^OK voice microphone' | Out-Null
            $occurrence = @($caller.Lines | Where-Object { $_ -match '^STATUS ' }).Count + 1
            Send-SidebandVoiceLine $caller 'voice status'
            Wait-SidebandVoiceLine $caller '^STATUS ' $occurrence | Should -Match 'mic_muted=True'
            Send-SidebandVoiceLine $caller 'voice unmute mic'
            Send-SidebandVoiceLine $caller 'voice hangup'
            Wait-SidebandVoiceLine $caller '^OK voice hangup' | Out-Null
            Stop-SidebandVoiceProcess $client
            $client = $null
            Stop-SidebandVoiceProcess $server
            $server = $null
            foreach ($name in 'server', 'client') {
                $state = Get-Content -LiteralPath (Join-Path $root "$name\state.json") -Raw | ConvertFrom-Json
                @($state.Calls | Where-Object Event -EQ 'ongoing-ended').Count | Should -Be 1
            }
            $server = Start-SidebandVoiceProcess $binary $serverArguments
            Wait-SidebandVoiceLine $server '^READY identity=' | Should -Match "identity=$serverIdentity"
            Send-SidebandVoiceLine $server 'voice log'
            Wait-SidebandVoiceLine $server '^CALL .* ongoing-ended ' | Out-Null
        }
        finally {
            Stop-SidebandVoiceProcess $client
            Stop-SidebandVoiceProcess $server
            if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
        }
    }
}
