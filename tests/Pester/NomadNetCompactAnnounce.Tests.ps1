Describe 'NomadNet bounded compact announce stream' -Tag 'CompactAnnounce' {
    BeforeAll {
        $enabled = $env:DOTRETICULUM_NOMADNET_COMPACT_ANNOUNCE -eq '1'
        if ($enabled) {
            $repository = if ($env:DOTRETICULUM_TEST_REPOSITORY) { $env:DOTRETICULUM_TEST_REPOSITORY }
                else { Split-Path (Split-Path $PSScriptRoot -Parent) -Parent }
            foreach ($name in 'DOTRETICULUM_NOMADNET_BINARY', 'DOTRETICULUM_PYTHON',
                'DOTRETICULUM_UPSTREAM', 'DOTRETICULUM_LXMF_UPSTREAM', 'DOTRETICULUM_NOMADNET_SOURCE') {
                if (-not [Environment]::GetEnvironmentVariable($name)) { throw "$name is required." }
            }
            $work = Join-Path (Get-Location) "artifacts\tests\nomad-compact\acceptance-$([Guid]::NewGuid().ToString('N'))"
            $null = New-Item $work -ItemType Directory -Force
            $processes = [Collections.Generic.List[Diagnostics.Process]]::new()
            $pending = @{}
            $logs = @{}
            $errors = @{}
            function Start-CompactProcess([string] $name, [string] $binary, [string[]] $arguments) {
                $info = [Diagnostics.ProcessStartInfo]::new($binary)
                $info.WorkingDirectory = $work
                $info.UseShellExecute = $false
                $info.RedirectStandardInput = $true
                $info.RedirectStandardOutput = $true
                $info.RedirectStandardError = $true
                $info.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
                $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
                foreach ($argument in $arguments) { $info.ArgumentList.Add($argument) }
                $info.Environment['PYTHONPATH'] = @($env:DOTRETICULUM_NOMADNET_SOURCE,
                    $env:DOTRETICULUM_UPSTREAM, $env:DOTRETICULUM_LXMF_UPSTREAM,
                    (Join-Path $repository 'tests\Interop')) -join [IO.Path]::PathSeparator
                $info.Environment['PYTHON_CPU_COUNT'] = '2'
                foreach ($key in 'TMP', 'TEMP', 'TMPDIR') { $info.Environment[$key] = $work }
                $p = [Diagnostics.Process]::new()
                $p.StartInfo = $info
                $null = $p.Start()
                $processes.Add($p)
                $logs[$p.Id] = @{ Name=$name; Text=[Text.StringBuilder]::new() }
                $errors[$p.Id] = $p.StandardError.ReadToEndAsync()
                return $p
            }
            function Send-Compact($p, [string] $command) {
                $p.StandardInput.WriteLine($command)
                $p.StandardInput.Flush()
            }
            function Read-Compact($p, [string] $pattern, [int] $seconds = 45) {
                $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
                while ([DateTime]::UtcNow -lt $deadline) {
                    if (-not $pending.ContainsKey($p.Id)) { $pending[$p.Id] = $p.StandardOutput.ReadLineAsync() }
                    try { $line = $pending[$p.Id].WaitAsync([TimeSpan]::FromMilliseconds(200)).GetAwaiter().GetResult() }
                    catch [TimeoutException] { continue }
                    $pending.Remove($p.Id)
                    if ($null -eq $line) { throw "Closed stdout before $pattern; evidence: $work" }
                    $null = $logs[$p.Id].Text.AppendLine($line)
                    if ($line -match $pattern) { return $line }
                }
                throw "Timed out before $pattern; evidence: $work"
            }
            function Stop-Compact($p) {
                Send-Compact $p 'quit'
                $p.WaitForExit(30000) | Should -BeTrue
                $p.ExitCode | Should -Be 0
                if ($pending.ContainsKey($p.Id)) {
                    $null = $logs[$p.Id].Text.AppendLine($pending[$p.Id].GetAwaiter().GetResult())
                    $pending.Remove($p.Id)
                }
                $null = $logs[$p.Id].Text.Append($p.StandardOutput.ReadToEnd())
            }
            function New-CompactRns([string] $name, [int] $port = 0, [bool] $client = $false) {
                $path = Join-Path $work $name
                $null = New-Item $path -ItemType Directory -Force
                $config = "[reticulum]`nshare_instance=no`nenable_transport=no`n[logging]`nloglevel=0`n[interfaces]`n"
                if ($port) {
                    $config += if ($client) { "[[Loopback]]`ntype=TCPClientInterface`nenabled=yes`ntarget_host=127.0.0.1`ntarget_port=$port`nannounce_cap=100`nbitrate=10000000`ningress_control=no`n" }
                        else { "[[Loopback]]`ntype=TCPServerInterface`nenabled=yes`nlisten_ip=127.0.0.1`nlisten_port=$port`nannounce_cap=100`nbitrate=10000000`ningress_control=no`n" }
                }
                $config | Set-Content (Join-Path $path 'config') -Encoding utf8NoBOM
                return $path
            }
            function Start-CompactNative([string] $name, [string] $profile, [string] $rns) {
                $p = Start-CompactProcess $name $env:DOTRETICULUM_NOMADNET_BINARY @('--config', $profile, '--rnsconfig', $rns)
                Send-Compact $p 'announces'
                $null = Read-Compact $p 'No nodes announces| N '
                return $p
            }
            function Wait-CompactHistory([string] $profile, [string] $hash, [int] $count) {
                $deadline = [DateTime]::UtcNow.AddSeconds(40)
                while ([DateTime]::UtcNow -lt $deadline) {
                    $path = Join-Path $profile 'storage\announce-stream.json'
                    if (Test-Path $path) {
                        $entries = @(Get-Content $path -Raw | ConvertFrom-Json)
                        if (@($entries | Where-Object destinationHash -EQ $hash).Count -eq $count) { return }
                    }
                    Start-Sleep -Milliseconds 100
                }
                throw "History count did not reach $count for $hash; evidence: $work"
            }
        }
    }

    It 'T238 regenerates independent primary constructor history and widget vectors' {
        if (-not $enabled) { Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_COMPACT_ANNOUNCE=1 with selected native binary and pinned primary environment.'; return }
        $fixture = Join-Path $work 'reference.json'
        $p = Start-CompactProcess 'vectors' $env:DOTRETICULUM_PYTHON @(
            (Join-Path $repository 'tests\Interop\GenerateNomadNetCompactAnnounceVectors.py'),
            '--output', $fixture, '--work', (Join-Path $work 'primary-vectors'))
        $output = $p.StandardOutput.ReadToEndAsync()
        $p.WaitForExit(120000) | Should -BeTrue
        $p.ExitCode | Should -Be 0 -Because $errors[$p.Id].GetAwaiter().GetResult()
        $null = $logs[$p.Id].Text.Append($output.GetAwaiter().GetResult())
        (Get-FileHash $fixture).Hash | Should -Be (Get-FileHash (
            Join-Path $repository 'tests\NomadNet.Tests\NomadNetCompactAnnounceVectors.json')).Hash
    }

    It 'T236 persists configured modes imports primary history and presents terminal views across restart' {
        if (-not $enabled) { Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_COMPACT_ANNOUNCE=1 with selected native binary and pinned primary environment.'; return }
        $profile = Join-Path $work 'native-presentation'
        $rns = New-CompactRns 'presentation-rns'
        $p = Start-CompactNative 'presentation' $profile $rns
        Send-Compact $p ('announces import ' + (Join-Path $work 'primary-vectors\full\profile'))
        Read-Compact $p 'Announce import complete: 6 entries' | Should -Match '6'
        Send-Compact $p 'announces peers name updated'
        Read-Compact $p 'Peers \(1\)' | Should -Match 'Nodes \(0\).*Peers \(1\)'
        $null = Read-Compact $p ' P Updated trust=2'
        Send-Compact $p 'announces pn dest'
        Read-Compact $p ' S 03030303030303030303030303030303 trust=2' | Should -Match ' S '
        Send-Compact $p 'announces mode full'
        $null = Read-Compact $p 'mode saved: full'
        Send-Compact $p 'announces mode bogus'
        $null = Read-Compact $p 'Error: Usage: announces mode'
        $malformed = Join-Path $work 'malformed-primary\storage'
        $null = New-Item $malformed -ItemType Directory -Force
        [IO.File]::WriteAllBytes((Join-Path $malformed 'directory'), [byte[]]@(0xc1))
        Send-Compact $p ('announces import ' + (Split-Path $malformed -Parent))
        $null = Read-Compact $p 'Error: Malformed primary announce stream'
        Send-Compact $p ('announces import ' + (Join-Path $work 'primary-vectors\full\profile'))
        $null = Read-Compact $p 'Announce import complete: 0 entries'
        Stop-Compact $p
        (Get-Content (Join-Path $profile 'settings.json') -Raw | ConvertFrom-Json).compactAnnounceStream | Should -BeFalse
        $p = Start-CompactNative 'presentation-restart' $profile $rns
        Send-Compact $p 'announces peers dest no-such-search'
        $null = Read-Compact $p 'Announce stream \(full\): Nodes \(0\); Peers \(0\)'
        $null = Read-Compact $p 'No peers announces'
        Send-Compact $p 'announces mode compact'
        $null = Read-Compact $p 'mode saved: compact'
        Stop-Compact $p
        (Get-Content (Join-Path $profile 'settings.json') -Raw | ConvertFrom-Json).compactAnnounceStream | Should -BeTrue
    }

    It 'T237 captures actual primary application announcements in compact and full modes with healthy restart' {
        if (-not $enabled) { Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_COMPACT_ANNOUNCE=1 with selected native binary and pinned primary environment.'; return }
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $port = $listener.LocalEndpoint.Port
        $listener.Stop()
        $profile = Join-Path $work 'native-traffic'
        $nativeRns = New-CompactRns 'native-rns' $port
        $pythonRns = New-CompactRns 'python-rns' $port $true
        $native = Start-CompactNative 'traffic-native' $profile $nativeRns
        $pythonProfile = Join-Path $work 'python-app'
        $null = New-Item $pythonProfile -ItemType Directory
        @'
[client]
user_interface=none
announce_at_start=no
periodic_lxmf_sync=no
compact_announce_stream=yes
[node]
enable_node=yes
disable_propagation=yes
announce_at_start=no
[logging]
loglevel=0
destination=console
'@ | Set-Content (Join-Path $pythonProfile 'config') -Encoding utf8NoBOM
        $python = Start-CompactProcess 'traffic-primary' $env:DOTRETICULUM_PYTHON @(
            '-u', (Join-Path $repository 'tests\Interop\nomadnet_compact_announce_node.py'), '--config', $pythonProfile,
            '--rnsconfig', $pythonRns)
        $readyLine = Read-Compact $python '@@.*"event": "ready"'
        $ready = $readyLine.Substring($readyLine.IndexOf('@@') + 2) | ConvertFrom-Json
        $ready.revision | Should -Be 'f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2'
        Send-Compact $python 'wait-network'
        $null = Read-Compact $python '"event": "network-ready"'
        $logs[$native.Id].Text.ToString() | Should -Match 'LXMF delivery address: ([a-f0-9]{32})'
        $nativeDelivery = [regex]::Match($logs[$native.Id].Text.ToString(),
            'LXMF delivery address: ([a-f0-9]{32})').Groups[1].Value
        Send-Compact $native 'announce'
        $null = Read-Compact $native 'Destinations announced'
        Start-Sleep -Seconds 3
        Send-Compact $native 'announce'
        $null = Read-Compact $native 'Destinations announced'
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            Send-Compact $python 'stream'
            $line = Read-Compact $python '"event": "stream"'
            $stream = $line.Substring($line.IndexOf('@@') + 2) | ConvertFrom-Json
            if (@($stream.rows | Where-Object hash -EQ $nativeDelivery).Count -eq 1) { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        @($stream.rows | Where-Object hash -EQ $nativeDelivery).Count | Should -Be 1
        foreach ($number in 1..2) {
            Send-Compact $python 'announce'
            $null = Read-Compact $python '"event": "announced"'
            Wait-CompactHistory $profile $ready.delivery 1
            Wait-CompactHistory $profile $ready.node 1
            Start-Sleep -Seconds 3
        }
        Send-Compact $native 'announces mode full'
        $null = Read-Compact $native 'mode saved: full'
        foreach ($count in 2..3) {
            Send-Compact $python 'announce'
            $null = Read-Compact $python '"event": "announced"'
            Wait-CompactHistory $profile $ready.delivery $count
            Wait-CompactHistory $profile $ready.node $count
            Start-Sleep -Seconds 3
        }
        Send-Compact $native 'announces peers name'
        $null = Read-Compact $native 'Peers \(3\)'
        Stop-Compact $native
        $native = Start-CompactNative 'traffic-restart' $profile $nativeRns
        Send-Compact $native 'announces mode compact'
        $null = Read-Compact $native 'mode saved: compact'
        Start-Sleep -Seconds 6
        Send-Compact $python 'announce'
        $null = Read-Compact $python '"event": "announced"'
        Wait-CompactHistory $profile $ready.delivery 1
        Wait-CompactHistory $profile $ready.node 1
        Stop-Compact $native
        Stop-Compact $python
        @{ Primary=$ready.revision; Delivery=$ready.delivery; Node=$ready.node;
            NativeDelivery=$nativeDelivery; ReversePrimaryCompactCount=1;
            Topology='actual primary application <-> direct TCP loopback <-> native; compact/full/restart';
            Native=$env:DOTRETICULUM_NOMADNET_BINARY } | ConvertTo-Json |
            Set-Content (Join-Path $work 'traffic-receipt.json') -Encoding utf8NoBOM
    }

    AfterAll {
        if ($enabled) {
            foreach ($p in $processes) {
                if (-not $p.HasExited) { $p.Kill($true); $p.WaitForExit() }
                if ($pending.ContainsKey($p.Id)) {
                    $null = $logs[$p.Id].Text.AppendLine($pending[$p.Id].GetAwaiter().GetResult())
                }
                $null = $logs[$p.Id].Text.Append($p.StandardOutput.ReadToEnd())
                $logs[$p.Id].Text.ToString() | Set-Content (Join-Path $work ($logs[$p.Id].Name + '.stdout')) -Encoding utf8NoBOM
                $errors[$p.Id].GetAwaiter().GetResult() | Set-Content (Join-Path $work ($logs[$p.Id].Name + '.stderr')) -Encoding utf8NoBOM
                $p.Dispose()
            }
            Write-Host "Compact announce evidence: $work"
        }
    }
}
