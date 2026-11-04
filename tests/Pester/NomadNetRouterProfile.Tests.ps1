Describe 'NomadNet bounded primary client router migration' -Tag 'RouterProfile' {
    BeforeAll {
        $enabled = $env:DOTRETICULUM_NOMADNET_ROUTER_PROFILE -eq '1'
        if ($enabled) {
            $repository = if ($env:DOTRETICULUM_TEST_REPOSITORY) { $env:DOTRETICULUM_TEST_REPOSITORY }
                else { Split-Path (Split-Path $PSScriptRoot -Parent) -Parent }
            foreach ($name in 'DOTRETICULUM_NOMADNET_BINARY', 'DOTRETICULUM_PYTHON',
                'DOTRETICULUM_UPSTREAM', 'DOTRETICULUM_LXMF_UPSTREAM', 'DOTRETICULUM_NOMADNET_SOURCE') {
                if (-not [Environment]::GetEnvironmentVariable($name)) { throw "$name is required." }
            }
            $work = Join-Path (Get-Location) "artifacts\tests\nomad-router\acceptance-$([Guid]::NewGuid().ToString('N'))"
            $null = New-Item $work -ItemType Directory -Force
            $processes = [Collections.Generic.List[Diagnostics.Process]]::new()
            $pending = @{}
            $logs = @{}
            $errors = @{}
            function Start-RouterProcess([string] $name, [string] $binary, [string[]] $arguments) {
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
            function Send-Router($p, [string] $command) {
                $p.StandardInput.WriteLine($command)
                $p.StandardInput.Flush()
            }
            function Read-Router($p, [string] $pattern, [int] $seconds = 60) {
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
            function Read-RouterEvent($p, [string] $event) {
                $line = Read-Router $p ('^@@.*"event": "' + [regex]::Escape($event) + '"')
                return $line.Substring(2) | ConvertFrom-Json
            }
            function Finish-Router($p, [int] $expected = 0) {
                $p.WaitForExit(90000) | Should -BeTrue
                $p.ExitCode | Should -Be $expected -Because "process diagnostics retained in $work"
                if ($pending.ContainsKey($p.Id)) {
                    $null = $logs[$p.Id].Text.AppendLine($pending[$p.Id].GetAwaiter().GetResult())
                    $pending.Remove($p.Id)
                }
                $null = $logs[$p.Id].Text.Append($p.StandardOutput.ReadToEnd())
            }
            function New-RouterSeed([string] $name) {
                $profile = Join-Path $work "$name-primary"
                $fixture = Join-Path $work "$name-reference.json"
                $p = Start-RouterProcess "$name-generator" $env:DOTRETICULUM_PYTHON @(
                    (Join-Path $repository 'tests\Interop\GenerateNomadNetRouterProfile.py'), '--profile', $profile,
                    '--rnsconfig', (Join-Path $work "$name-generator-rns"), '--output', $fixture)
                $line = Read-Router $p '^\{"profile":'
                $metadata = $line | ConvertFrom-Json
                Finish-Router $p
                (Get-FileHash $fixture).Hash | Should -Be (Get-FileHash (
                    Join-Path $repository 'tests\NomadNet.Tests\NomadNetRouterProfileVectors.json')).Hash
                return @{ Profile=$profile; Fixture=$fixture; Delivery=$metadata.delivery }
            }
            function New-RouterRns([string] $name, [int] $port = 0, [bool] $client = $false) {
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
            function Import-Router([string] $name, [string] $source, [string] $target, [string] $rns, [int] $expected = 0) {
                $p = Start-RouterProcess $name $env:DOTRETICULUM_NOMADNET_BINARY @(
                    '--import-profile', $source, '--config', $target, '--rnsconfig', $rns)
                Finish-Router $p $expected
            }
            function Snapshot-Router([string] $root) {
                return @(Get-ChildItem $root -File -Recurse | Sort-Object FullName | ForEach-Object {
                    [IO.Path]::GetRelativePath($root, $_.FullName) + ':' + (Get-FileHash $_.FullName).Hash
                }) -join "`n"
            }
        }
    }

    It 'T247 independently regenerates public primary constructor and router persistence fixtures' {
        if (-not $enabled) { Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_ROUTER_PROFILE=1 with selected NativeAOT binary and pinned primary Python environment.'; return }
        $seed = New-RouterSeed 'vectors'
        $reference = Get-Content $seed.Fixture -Raw | ConvertFrom-Json
        $reference.clientFiles.Count | Should -Be 5
        $reference.nodeStatsWrittenByClient | Should -BeTrue
        $reference.propagationNode | Should -BeFalse
    }

    It 'T248 atomically imports selected router stores and cross loads primary native primary across restart' {
        if (-not $enabled) { Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_ROUTER_PROFILE=1 with selected NativeAOT binary and pinned primary Python environment.'; return }
        $seed = New-RouterSeed 'cross-load'
        $before = Snapshot-Router $seed.Profile
        $target = Join-Path $work 'native-cross-load'
        $rns = New-RouterRns 'native-cross-load-rns'
        Import-Router 'cross-import' $seed.Profile $target $rns
        (Snapshot-Router $seed.Profile) | Should -BeExactly $before
        $identity = (Get-FileHash (Join-Path $target 'identity')).Hash
        $identity | Should -Be (Get-FileHash (Join-Path $seed.Profile 'storage\identity')).Hash
        $report = Get-Content (Join-Path $target 'profile-import.json') -Raw | ConvertFrom-Json
        @($report.notImportedFiles) | Should -Contain 'storage/lxmf/node_stats'
        @($report.notImportedFiles | Where-Object { $_ -match '(available_tickets|outbound_stamp_costs|local_deliveries|locally_processed|\.ratchets$)' }).Count | Should -Be 0
        for ($restart = 0; $restart -lt 2; $restart++) {
            $p = Start-RouterProcess "cross-native-$restart" $env:DOTRETICULUM_NOMADNET_BINARY @('--config', $target)
            Read-Router $p 'LXMF delivery address:' | Should -Match $seed.Delivery
            Send-Router $p 'quit'
            Finish-Router $p
        }
        $cross = Join-Path $work 'primary-cross-load'
        Copy-Item $seed.Profile $cross -Recurse
        Remove-Item (Join-Path $cross 'storage\lxmf') -Recurse
        Copy-Item (Join-Path $target 'storage\lxmf') (Join-Path $cross 'storage\lxmf') -Recurse
        Copy-Item (Join-Path $target 'identity') (Join-Path $cross 'storage\identity') -Force
        $observation = Join-Path $work 'cross-load-observation.json'
        $primary = Start-RouterProcess 'cross-primary' $env:DOTRETICULUM_PYTHON @(
            (Join-Path $repository 'tests\Interop\GenerateNomadNetRouterProfile.py'), '--inspect',
            '--profile', $cross, '--rnsconfig', (Join-Path $work 'cross-primary-rns'), '--output', $observation)
        Finish-Router $primary
        (Get-FileHash $observation).Hash | Should -Be (Get-FileHash $seed.Fixture).Hash
        (Get-FileHash (Join-Path $target 'identity')).Hash | Should -Be $identity
        Import-Router 'no-overwrite' $seed.Profile $target $rns 1
        $bad = Join-Path $work 'malformed-primary'
        Copy-Item $seed.Profile $bad -Recurse
        [IO.File]::WriteAllBytes((Join-Path $bad 'storage\lxmf\available_tickets'), [byte[]]@(0xc1))
        $badTarget = Join-Path $work 'refused-native'
        Import-Router 'malformed-refusal' $bad $badTarget $rns 1
        Test-Path $badTarget | Should -BeFalse
        @(Get-ChildItem $work -Directory -Filter 'refused-native.profile-import-*').Count | Should -Be 0
        (Snapshot-Router $seed.Profile) | Should -BeExactly $before
    }

    It 'T249 exchanges actual primary app messages with migrated identity after clean and abrupt native restart' {
        if (-not $enabled) { Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_ROUTER_PROFILE=1 with selected NativeAOT binary and pinned primary Python environment.'; return }
        $seed = New-RouterSeed 'live'
        $before = Snapshot-Router $seed.Profile
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $port = $listener.LocalEndpoint.Port
        $listener.Stop()
        $rns = New-RouterRns 'live-native-rns' $port $true
        $target = Join-Path $work 'live-native'
        Import-Router 'live-import' $seed.Profile $target $rns
        $identity = (Get-FileHash (Join-Path $target 'identity')).Hash
        $initial = Start-RouterProcess 'live-initial' $env:DOTRETICULUM_NOMADNET_BINARY @('--config', $target)
        Read-Router $initial 'LXMF delivery address:' | Should -Match $seed.Delivery
        Send-Router $initial 'quit'
        Finish-Router $initial
        $peerProfile = Join-Path $work 'live-primary-peer'
        $null = New-Item $peerProfile -ItemType Directory
        "[client]`nenable_client=yes`nannounce_at_start=no`nperiodic_lxmf_sync=no`n[node]`nenable_node=yes`ndisable_propagation=yes`nannounce_at_start=no`n[logging]`nloglevel=0`n" |
            Set-Content (Join-Path $peerProfile 'config') -Encoding utf8NoBOM
        $peerRns = New-RouterRns 'live-primary-rns' $port
        $peer = Start-RouterProcess 'actual-primary-peer' $env:DOTRETICULUM_PYTHON @('-u',
            (Join-Path $repository 'tests\Interop\nomadnet_live_node.py'), '--config', $peerProfile, '--rnsconfig', $peerRns)
        $ready = Read-RouterEvent $peer 'ready'
        $ready.revision | Should -BeExactly 'f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2'
        Send-Router $peer 'observe-client-announces'
        $null = Read-RouterEvent $peer 'observing-client-announces'
        for ($restart = 0; $restart -lt 2; $restart++) {
            $native = Start-RouterProcess "live-restart-$restart" $env:DOTRETICULUM_NOMADNET_BINARY @('--config', $target)
            Read-Router $native 'LXMF delivery address:' | Should -Match $seed.Delivery
            Send-Router $peer 'wait-network'
            $null = Read-RouterEvent $peer 'network-ready'
            Send-Router $native 'announce'
            $null = Read-Router $native 'Announced'
            Send-Router $peer "client-announces $($seed.Delivery) $($restart + 1)"
            $observed = Read-RouterEvent $peer 'client-announces'
            $observed.delivery | Should -Be $seed.Delivery
            Send-Router $peer 'announce'
            $null = Read-RouterEvent $peer 'announced'
            Send-Router $native "conversation active $($ready.delivery)"
            $null = Read-Router $native 'Conversation command complete.'
            $outbound = "migrated-router-to-primary-$restart"
            $inbound = "primary-to-migrated-router-$restart"
            Send-Router $native "send $($ready.delivery) $outbound"
            $received = Read-RouterEvent $peer 'received'
            $received.content | Should -BeExactly $outbound
            Test-Path (Join-Path $peerProfile "storage\conversations\$($seed.Delivery)\$($received.message_hash)") | Should -BeTrue
            Send-Router $peer "send $($seed.Delivery) $inbound"
            $queued = Read-RouterEvent $peer 'queued'
            (Read-RouterEvent $peer 'delivered').message_hash | Should -Be $queued.message_hash
            $null = Read-Router $native "Conversation received:.*$inbound"
            if ($restart -eq 0) {
                $native.Kill($true)
                $native.WaitForExit()
                Finish-Router $native $native.ExitCode
                # Let the unchanged primary retire its old accepted TCP child before testing
                # a new connection; online on that old child is not a new-connection barrier.
                Start-Sleep -Seconds 2
            }
            else { Send-Router $native 'quit'; Finish-Router $native }
            $state = Get-Content (Join-Path $target 'state.json') -Raw | ConvertFrom-Json
            @($state.messages | Where-Object content -EQ $inbound).Count | Should -Be 1
            (Get-FileHash (Join-Path $target 'identity')).Hash | Should -Be $identity
        }
        Send-Router $peer 'quit'
        Finish-Router $peer
        (Snapshot-Router $seed.Profile) | Should -BeExactly $before
        @{ BinarySha256=(Get-FileHash $env:DOTRETICULUM_NOMADNET_BINARY).Hash.ToLowerInvariant();
            FixtureSha256=(Get-FileHash $seed.Fixture).Hash.ToLowerInvariant();
            NativeRestarts='clean initial shutdown then two live starts separated by abrupt kill';
            Exchanges=2; Topology='Direct TCP loopback leaves, actual unmodified primary NomadNetworkApp with observing adapter';
            Exclusions='No transport-hop/loss/propagation/hardware/complete newer-dependency parity claim' } |
            ConvertTo-Json | Set-Content (Join-Path $work 'traffic-receipt.json') -Encoding utf8NoBOM
    }

    AfterAll {
        if ($enabled) {
            foreach ($p in $processes) {
                if (-not $p.HasExited) { $p.Kill($true); $p.WaitForExit() }
                if ($pending.ContainsKey($p.Id)) {
                    $null = $logs[$p.Id].Text.AppendLine($pending[$p.Id].GetAwaiter().GetResult())
                    $pending.Remove($p.Id)
                }
                $null = $logs[$p.Id].Text.Append($p.StandardOutput.ReadToEnd())
                [IO.File]::WriteAllText((Join-Path $work "$($logs[$p.Id].Name).stdout"), $logs[$p.Id].Text.ToString())
                [IO.File]::WriteAllText((Join-Path $work "$($logs[$p.Id].Name).stderr"), $errors[$p.Id].GetAwaiter().GetResult())
                $p.Dispose()
            }
        }
    }
}
