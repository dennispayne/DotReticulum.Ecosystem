Describe 'NomadNet bounded primary propagation node migration' -Tag 'NodeProfile', 'Native' {
    BeforeAll {
        $enabled = $env:DOTRETICULUM_NOMADNET_NODE_PROFILE -eq '1'
        if ($enabled) {
            $repository = if ($env:DOTRETICULUM_TEST_REPOSITORY) { $env:DOTRETICULUM_TEST_REPOSITORY }
                else { Split-Path (Split-Path $PSScriptRoot -Parent) -Parent }
            foreach ($name in 'DOTRETICULUM_NOMADNET_BINARY', 'DOTRETICULUM_PYTHON',
                'DOTRETICULUM_UPSTREAM', 'DOTRETICULUM_LXMF_UPSTREAM', 'DOTRETICULUM_NOMADNET_SOURCE') {
                if (-not [Environment]::GetEnvironmentVariable($name)) { throw "$name is required." }
            }
            $work = Join-Path (Get-Location) "artifacts\tests\nomad-node-migration\acceptance-$([Guid]::NewGuid().ToString('N'))"
            $null = New-Item $work -ItemType Directory -Force
            if ($IsWindows) {
                $current = [Security.Principal.WindowsIdentity]::GetCurrent()
                try { $sid = $current.User } finally { $current.Dispose() }
                $acl = Get-Acl $work
                $acl.SetAccessRuleProtection($true, $false)
                $acl.SetOwner($sid)
                $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,
                    [Security.AccessControl.FileSystemRights]::FullControl,
                    [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit,
                    [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow))
                Set-Acl -LiteralPath $work -AclObject $acl
            }
            else {
                [IO.File]::SetUnixFileMode($work, [IO.UnixFileMode]::UserRead -bor
                    [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute)
            }
            $processes = [Collections.Generic.List[Diagnostics.Process]]::new()
            $pending = @{}
            $logs = @{}
            $errors = @{}
            function Start-NodeProcess([string] $name, [string] $binary, [string[]] $arguments) {
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
                $process = [Diagnostics.Process]::new()
                $process.StartInfo = $info
                $null = $process.Start()
                $processes.Add($process)
                $logs[$process.Id] = @{Name=$name;Text=[Text.StringBuilder]::new()}
                $errors[$process.Id] = $process.StandardError.ReadToEndAsync()
                return $process
            }
            function Send-Node($process, [string] $command) {
                $process.StandardInput.WriteLine($command)
                $process.StandardInput.Flush()
            }
            function Read-Node($process, [string] $pattern, [int] $seconds = 60) {
                $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
                while ([DateTime]::UtcNow -lt $deadline) {
                    if (-not $pending.ContainsKey($process.Id)) { $pending[$process.Id] = $process.StandardOutput.ReadLineAsync() }
                    try { $line = $pending[$process.Id].WaitAsync([TimeSpan]::FromMilliseconds(200)).GetAwaiter().GetResult() }
                    catch [TimeoutException] { continue }
                    $pending.Remove($process.Id)
                    if ($null -eq $line) { throw "Closed stdout before $pattern; diagnostics: $work / $($logs[$process.Id].Name)" }
                    $null = $logs[$process.Id].Text.AppendLine($line)
                    if ($line -match '@@.*"event": "(error|failed)"|(^|nomadnet> )Error:') { throw $line }
                    if ($line -match $pattern) { return $line }
                }
                throw "Timed out before $pattern; diagnostics: $work / $($logs[$process.Id].Name)"
            }
            function Read-NodeEvent($process, [string] $event, [int] $seconds = 60) {
                $line = Read-Node $process ('@@.*"event": "' + [regex]::Escape($event) + '"') $seconds
                return $line.Substring($line.IndexOf('@@', [StringComparison]::Ordinal) + 2) | ConvertFrom-Json
            }
            function Finish-Node($process, [int] $expected = 0) {
                if ($pending.ContainsKey($process.Id)) {
                    $null = $logs[$process.Id].Text.AppendLine($pending[$process.Id].GetAwaiter().GetResult())
                    $pending.Remove($process.Id)
                }
                $remaining = $process.StandardOutput.ReadToEndAsync()
                $process.WaitForExit(90000) | Should -BeTrue
                $process.ExitCode | Should -Be $expected -Because "diagnostics remain in $work"
                $null = $logs[$process.Id].Text.Append($remaining.GetAwaiter().GetResult())
            }
            function Stop-Node($process) {
                Send-Node $process 'quit'
                Finish-Node $process
            }
            function New-NodeSeed([string] $name, [bool] $quota = $false) {
                $profile = Join-Path $work "$name-primary"
                $rns = Join-Path $work "$name-rns"
                $output = Join-Path $work "$name.json"
                $fixturePath = Join-Path $repository $(if ($quota) { 'tests\NomadNet.Tests\NomadNetNodeQuotaVectors.json' } else {
                    'tests\NomadNet.Tests\NomadNetNodeProfileVectors.json' })
                $fixture = Get-Content $fixturePath -Raw | ConvertFrom-Json
                $process = Start-NodeProcess "$name-generator" $env:DOTRETICULUM_PYTHON @(
                    (Join-Path $repository 'tests\Interop\GenerateNomadNetNodeProfile.py'),
                    '--profile', $profile, '--rnsconfig', $rns, '--stamp', $fixture.stampHex, '--output', $output,
                    '--payload-repetitions', $(if ($quota) { '20' } else { '2' }))
                $null = Read-Node $process '^\{"profile":'
                Finish-Node $process
                (Get-FileHash $output).Hash | Should -Be (Get-FileHash $fixturePath).Hash
                return @{Profile=$profile;Rns=$rns;Fixture=$output}
            }
            function Import-Node([string] $name, [string] $source, [string] $target, [string] $rns, [int] $expected = 0) {
                $process = Start-NodeProcess $name $env:DOTRETICULUM_NOMADNET_BINARY @(
                    '--import-profile', $source, '--config', $target, '--rnsconfig', $rns)
                Finish-Node $process $expected
            }
            function Start-NativeNode([string] $name, [string] $profile, [string] $rns) {
                $process = Start-NodeProcess $name $env:DOTRETICULUM_NOMADNET_BINARY @('--config', $profile, '--rnsconfig', $rns)
                $delivery = [regex]::Match((Read-Node $process 'LXMF delivery address:'), '[0-9a-f]{32}').Value
                return @{Process=$process;Delivery=$delivery}
            }
            function Snapshot-Node([string] $root) {
                return @(Get-ChildItem $root -File -Recurse | Sort-Object FullName | ForEach-Object {
                    [IO.Path]::GetRelativePath($root, $_.FullName) + ':' + (Get-FileHash $_.FullName).Hash
                }) -join "`n"
            }
            function Copy-NodeState([string] $native, [string] $primary) {
                $from = Join-Path $native 'storage\lxmf'
                $to = Join-Path $primary 'storage\lxmf'
                foreach ($name in 'peers','node_stats') { Copy-Item (Join-Path $from $name) (Join-Path $to $name) -Force }
                Remove-Item (Join-Path $to 'messagestore') -Recurse -Force
                Copy-Item (Join-Path $from 'messagestore') (Join-Path $to 'messagestore') -Recurse
            }
            function New-NodeRns([string] $path, [int] $port = 0, [bool] $server = $false) {
                $null = New-Item $path -ItemType Directory -Force
                $text = "[reticulum]`nshare_instance=No`nenable_transport=$($server.ToString())`n[logging]`nloglevel=0`n[interfaces]`n"
                if ($port) {
                    $text += if ($server) { "[[Loopback]]`ntype=TCPServerInterface`nenabled=Yes`nlisten_ip=127.0.0.1`nlisten_port=$port`n" }
                        else { "[[Loopback]]`ntype=TCPClientInterface`nenabled=Yes`ntarget_host=127.0.0.1`ntarget_port=$port`n" }
                    $text += "bitrate=10000000`nannounce_cap=100`ningress_control=No`n"
                }
                $text | Set-Content (Join-Path $path 'config') -Encoding utf8NoBOM
            }
            function New-PrimaryNode([string] $profile) {
                $null = New-Item $profile -ItemType Directory -Force
                @('[client]','enable_client=Yes','user_interface=text','announce_at_start=No',
                    'periodic_lxmf_sync=No','max_accepted_size=4000','[node]','enable_node=Yes',
                    'disable_propagation=No','announce_at_start=No','max_transfer_size=4000',
                    'max_sync_size=8000','message_storage_limit=16','propagation_cost=13','max_peers=20',
                    '[textui]','colormode=256',
                    '[logging]','loglevel=7','destination=console') |
                    Set-Content (Join-Path $profile 'config') -Encoding utf8NoBOM
            }
            function Start-PrimaryNode([string] $name, [string] $profile, [string] $rns, [bool] $clientOnly = $false) {
                $arguments = @('-u',
                    (Join-Path $repository 'tests\Interop\nomadnet_node_profile_peer.py'), '--config', $profile, '--rnsconfig', $rns)
                if ($clientOnly) { $arguments += '--client-only' }
                $process = Start-NodeProcess $name $env:DOTRETICULUM_PYTHON $arguments
                return @{Process=$process;Ready=(Read-NodeEvent $process 'ready')}
            }
            function New-ImportedNativeClient([string] $name, [string] $target, [string] $rns) {
                $sourceProfile = Join-Path $work "$name-primary-client"
                $sourceRns = Join-Path $work "$name-primary-client-rns"
                New-PrimaryNode $sourceProfile
                $configurationPath = Join-Path $sourceProfile 'config'
                [IO.File]::WriteAllText($configurationPath,
                    ([IO.File]::ReadAllText($configurationPath)).Replace('enable_node=Yes', 'enable_node=No').
                        Replace('disable_propagation=No', 'disable_propagation=Yes'))
                New-NodeRns $sourceRns
                $client = Start-PrimaryNode "$name-client-initializer" $sourceProfile $sourceRns $true
                $client.Ready.propagation | Should -BeNullOrEmpty
                Stop-Node $client.Process
                $before = Snapshot-Node $sourceProfile
                $networkBefore = Snapshot-Node $rns
                Import-Node "$name-client-import" $sourceProfile $target $rns
                (Snapshot-Node $sourceProfile) | Should -Be $before
                (Snapshot-Node $rns) | Should -Be $networkBefore
                $settingsPath = Join-Path $target 'settings.json'
                $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json -AsHashtable
                $settings.displayName = 'Synthetic imported node migration client'
                $settings.periodicLxmfSync = $false
                $settings.deliveryTransferLimitBytes = 4000000
                $settings.logLevel = 6
                $settings | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding utf8NoBOM
                return $client.Ready.delivery
            }
            function Wait-NodeAttachment([string] $profile, [string] $digest) {
                $deadline = [DateTime]::UtcNow.AddSeconds(180)
                do {
                    foreach ($file in Get-ChildItem (Join-Path $profile 'attachments') -Filter '*.bin' -File -Recurse -ErrorAction SilentlyContinue) {
                        if ($file.Length -eq 2200000 -and (Get-FileHash $file.FullName).Hash -eq $digest) { return }
                    }
                    Start-Sleep -Milliseconds 100
                } while ([DateTime]::UtcNow -lt $deadline)
                throw "Missing SHA-checked native attachment; diagnostics: $work"
            }
        }
    }

    It 'T269 independently regenerates public primary propagation store peer and counter vectors' {
        if (-not $enabled) { Set-ItResult -Skipped -Because 'Enable node-profile mode with selected NativeAOT binary and pinned primary Python references.'; return }
        $seed = New-NodeSeed 'node-vectors'
        $fixture = Get-Content $seed.Fixture -Raw | ConvertFrom-Json
        $fixture.messages | Should -Be 1
        $fixture.peerUnhandled | Should -Be 1
        $fixture.stampValue | Should -BeGreaterOrEqual 13
        $quota = New-NodeSeed 'node-quota-vectors' $true
        $large = Get-Content $quota.Fixture -Raw | ConvertFrom-Json
        ($large.storeFiles[0].hex.Length / 2) | Should -BeGreaterThan 5000
    }

    It 'T270 atomically imports actual primary node stores retains unresolved peers across native restarts and cross loads a primary node again' {
        if (-not $enabled) { Set-ItResult -Skipped -Because 'Enable node-profile mode with selected NativeAOT binary and pinned primary Python references.'; return }
        $help = Start-NodeProcess 'help' $env:DOTRETICULUM_NOMADNET_BINARY @('--help')
        Finish-Node $help
        $logs[$help.Id].Text.ToString() | Should -Match '\-\-import-profile'
        $seed = New-NodeSeed 'node-cross'
        $before = Snapshot-Node $seed.Profile
        $target = Join-Path $work 'native-cross'
        $rns = Join-Path $work 'native-cross-rns'
        New-NodeRns $rns
        $rnsBefore = Snapshot-Node $rns
        Import-Node 'node-import' $seed.Profile $target $rns
        (Snapshot-Node $seed.Profile) | Should -Be $before
        (Snapshot-Node $rns) | Should -Be $rnsBefore
        $identity = (Get-FileHash (Join-Path $target 'identity')).Hash
        $report = Get-Content (Join-Path $target 'profile-import.json') -Raw | ConvertFrom-Json
        @($report.warnings | Where-Object { $_ -match '1 unresolved persisted peers' }).Count | Should -Be 1
        for ($restart = 0; $restart -lt 2; $restart++) {
            $native = Start-NativeNode "node-native-$restart" $target $rns
            $null = Read-Node $native.Process 'Propagation node address:'
            Send-Node $native.Process 'status'
            $null = Read-Node $native.Process 'Propagation stored messages: 1$'
            Send-Node $native.Process 'node-peers'
            $null = Read-Node $native.Process 'Propagation peers: 0\.'
            Stop-Node $native.Process
        }
        if (-not $IsWindows) {
            $nodeDirectory = Join-Path $target 'storage\lxmf'
            $nodeFiles = @((Join-Path $nodeDirectory 'peers'), (Join-Path $nodeDirectory 'node_stats')) +
                @(Get-ChildItem (Join-Path $nodeDirectory 'messagestore') -File | ForEach-Object FullName)
            foreach ($path in $nodeFiles) {
                [IO.File]::GetUnixFileMode($path) | Should -Be ([IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)
            }
        }
        (Get-FileHash (Join-Path $target 'identity')).Hash | Should -Be $identity
        Import-Node 'node-existing-refusal' $seed.Profile $target $rns 1
        (Get-FileHash (Join-Path $target 'identity')).Hash | Should -Be $identity
        $reverse = Join-Path $work 'primary-cross-reverse'
        Copy-Item $seed.Profile $reverse -Recurse
        Copy-NodeState $target $reverse
        $output = Join-Path $work 'primary-cross-reverse.json'
        $inspect = Start-NodeProcess 'primary-node-cross-reload' $env:DOTRETICULUM_PYTHON @(
            (Join-Path $repository 'tests\Interop\GenerateNomadNetNodeProfile.py'), '--inspect',
            '--profile', $reverse, '--rnsconfig', $seed.Rns, '--output', $output)
        $null = Read-Node $inspect '^\{"profile":'
        Finish-Node $inspect
        $loaded = Get-Content $output -Raw | ConvertFrom-Json
        $loaded.propagationNode | Should -BeTrue
        $loaded.peers | Should -Be 1
        $loaded.messages | Should -Be 1
        $loaded.peerUnhandled | Should -Be 1
        $loaded.statsHex | Should -Be (Get-Content $seed.Fixture -Raw | ConvertFrom-Json).statsHex
        (Snapshot-Node $seed.Profile) | Should -Be $before
        $bad = Join-Path $work 'primary-malformed-node'
        Copy-Item $seed.Profile $bad -Recurse
        [IO.File]::WriteAllBytes((Join-Path $bad 'storage\lxmf\peers'), [byte[]]@(0xc1))
        $badBefore = Snapshot-Node $bad
        $badTarget = Join-Path $work 'native-malformed-node'
        Import-Node 'node-malformed-refusal' $bad $badTarget $rns 1
        (Test-Path $badTarget) | Should -BeFalse
        (Snapshot-Node $bad) | Should -Be $badBefore
        $quota = New-NodeSeed 'node-quota-refusal' $true
        $configurationPath = Join-Path $quota.Profile 'config'
        [IO.File]::WriteAllText($configurationPath,
            ([IO.File]::ReadAllText($configurationPath)).Replace('[node]', "[node]`nmessage_storage_limit=0.005"))
        $quotaBefore = Snapshot-Node $quota.Profile
        $quotaTarget = Join-Path $work 'native-quota-refusal'
        Import-Node 'node-quota-refusal' $quota.Profile $quotaTarget $rns 1
        (Test-Path $quotaTarget) | Should -BeFalse
        (Snapshot-Node $quota.Profile) | Should -Be $quotaBefore
    }

    It 'T271 migrates a live primary node recovers peer identity after clean and abrupt restart cross loads primary state and transfers real multi megabyte messages both ways' {
        if (-not $enabled) { Set-ItResult -Skipped -Because 'Enable node-profile mode with selected NativeAOT binary and pinned primary Python references.'; return }
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $port = $listener.LocalEndpoint.Port
        $listener.Stop()
        $source = Join-Path $work 'live-primary-a'
        $sourceRns = Join-Path $work 'live-primary-a-rns'
        $peerProfile = Join-Path $work 'live-primary-b'
        $peerRns = Join-Path $work 'live-primary-b-rns'
        $originProfile = Join-Path $work 'live-origin'
        $originRns = Join-Path $work 'live-origin-rns'
        $receiverProfile = Join-Path $work 'live-receiver'
        $receiverRns = Join-Path $work 'live-receiver-rns'
        New-PrimaryNode $source
        New-PrimaryNode $peerProfile
        New-NodeRns $sourceRns $port $true
        foreach ($path in @($peerRns,$originRns,$receiverRns)) { New-NodeRns $path $port }
        $expectedOrigin = New-ImportedNativeClient 'origin' $originProfile $originRns
        $expectedReceiver = New-ImportedNativeClient 'receiver' $receiverProfile $receiverRns
        $a = Start-PrimaryNode 'live-source-a' $source $sourceRns
        $b = Start-PrimaryNode 'live-peer-b' $peerProfile $peerRns
        $origin = Start-NativeNode 'live-origin-before' $originProfile $originRns
        $receiver = Start-NativeNode 'live-receiver-before' $receiverProfile $receiverRns
        $origin.Delivery | Should -Be $expectedOrigin
        $receiver.Delivery | Should -Be $expectedReceiver
        $originDelivery = $origin.Delivery
        $receiverDelivery = $receiver.Delivery
        foreach ($node in @($a,$b)) { Send-Node $node.Process 'announce'; $null = Read-NodeEvent $node.Process 'announced' }
        foreach ($node in @($origin,$receiver)) { Send-Node $node.Process 'announce'; $null = Read-Node $node.Process 'Destinations announced\.' }
        $deadline = [DateTime]::UtcNow.AddSeconds(80)
        do {
            Send-Node $a.Process 'stats'
            $stats = Read-NodeEvent $a.Process 'stats'
            $savedPeer = @($stats.peers | Where-Object hash -EQ $b.Ready.propagation)
            if ($savedPeer.Count -eq 1) { break }
            Start-Sleep -Milliseconds 200
        } while ([DateTime]::UtcNow -lt $deadline)
        $savedPeer.Count | Should -Be 1
        Stop-Node $b.Process
        $bytes = [byte[]]::new(2200000)
        [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
        $payload = Join-Path $work 'public-synthetic-node-payload.bin'
        [IO.File]::WriteAllBytes($payload, $bytes)
        $digest = (Get-FileHash $payload).Hash
        Send-Node $origin.Process "propagation $($a.Ready.propagation)"
        $null = Read-Node $origin.Process 'Propagation selection saved\.'
        Send-Node $origin.Process "contact delivery $($receiver.Delivery) propagated"
        $null = Read-Node $origin.Process 'Contact policy saved\.'
        Send-Node $origin.Process "attach $($receiver.Delivery) $payload"
        $null = Read-Node $origin.Process 'Message queued:' 180
        $deadline = [DateTime]::UtcNow.AddSeconds(180)
        do {
            Send-Node $a.Process 'stats'
            $stats = Read-NodeEvent $a.Process 'stats'
            if ($stats.messages -eq 1) { break }
            Start-Sleep -Milliseconds 200
        } while ([DateTime]::UtcNow -lt $deadline)
        $stats.messages | Should -Be 1
        @($stats.peers | Where-Object hash -EQ $b.Ready.propagation)[0].unhandled | Should -Be 1
        foreach ($node in @($origin,$receiver,$a)) { Stop-Node $node.Process }
        $before = Snapshot-Node $source
        $target = Join-Path $work 'live-native-a'
        $targetRns = Join-Path $work 'live-native-a-rns'
        New-NodeRns $targetRns $port $true
        $rnsBefore = Snapshot-Node $targetRns
        Import-Node 'live-node-import' $source $target $targetRns
        (Snapshot-Node $source) | Should -Be $before
        (Snapshot-Node $targetRns) | Should -Be $rnsBefore
        $identity = (Get-FileHash (Join-Path $target 'identity')).Hash
        $peerFile = Join-Path $target 'storage\lxmf\peers'
        $peerHash = (Get-FileHash $peerFile).Hash
        for ($restart = 0; $restart -lt 3; $restart++) {
            $native = Start-NativeNode "live-native-offline-$restart" $target $targetRns
            [regex]::Match((Read-Node $native.Process 'Propagation node address:'), '[0-9a-f]{32}').Value |
                Should -Be $a.Ready.propagation
            Send-Node $native.Process 'node-peers'
            $null = Read-Node $native.Process 'Propagation peers: 0\.'
            Send-Node $native.Process 'status'
            $null = Read-Node $native.Process 'Propagation stored messages: 1$'
            if ($restart -eq 0) { Stop-Node $native.Process }
            elseif ($restart -eq 1) { $native.Process.Kill($true); $native.Process.WaitForExit() }
            (Get-FileHash $peerFile).Hash | Should -Be $peerHash
        }
        $b = Start-PrimaryNode 'live-peer-b-restarted' $peerProfile $peerRns
        Send-Node $b.Process 'announce'
        $null = Read-NodeEvent $b.Process 'announced'
        $null = Read-Node $native.Process ('Activated preserved propagation peer after matching identity discovery: ' + $b.Ready.propagation) 90
        Send-Node $native.Process 'node-peers'
        $null = Read-Node $native.Process ("Peer " + $b.Ready.propagation + '.*unhandled=1; outgoing=0')
        Send-Node $native.Process ("node-peer-sync " + $b.Ready.propagation)
        $null = Read-Node $native.Process 'Propagation peer sync complete: 1 messages\.' 180
        $deadline = [DateTime]::UtcNow.AddSeconds(90)
        do {
            Send-Node $b.Process 'stats'
            $received = Read-NodeEvent $b.Process 'stats'
            if ($received.messages -eq 1) { break }
            Start-Sleep -Milliseconds 200
        } while ([DateTime]::UtcNow -lt $deadline)
        $received.messages | Should -Be 1
        Stop-Node $native.Process
        $reverse = Join-Path $work 'live-primary-a-reverse'
        Copy-Item $source $reverse -Recurse
        Copy-NodeState $target $reverse
        Start-Sleep -Seconds 2
        $primaryAgain = Start-PrimaryNode 'live-primary-a-reverse' $reverse $sourceRns
        $primaryAgain.Ready.propagation | Should -Be $a.Ready.propagation
        Send-Node $primaryAgain.Process 'stats'
        $crossLoaded = Read-NodeEvent $primaryAgain.Process 'stats'
        $crossLoaded.messages | Should -Be 1
        $crossPeer = @($crossLoaded.peers | Where-Object hash -EQ $b.Ready.propagation)[0]
        $crossPeer.outgoing | Should -Be 1
        $crossPeer.handled | Should -Be 1
        Stop-Node $primaryAgain.Process
        Start-Sleep -Seconds 2
        $native = Start-NativeNode 'live-native-final' $target $targetRns
        $null = Read-Node $native.Process 'Propagation node address:'
        $receiverAfterHandoverRns = Join-Path $work 'live-receiver-after-handover-rns'
        New-NodeRns $receiverAfterHandoverRns $port
        $receiver = Start-NativeNode 'live-receiver-restarted' $receiverProfile $receiverAfterHandoverRns
        $receiver.Delivery | Should -Be $receiverDelivery
        foreach ($node in @($native,$receiver)) { Send-Node $node.Process 'announce'; $null = Read-Node $node.Process 'Destinations announced\.' }
        Send-Node $b.Process 'announce'
        $null = Read-NodeEvent $b.Process 'announced'
        $null = Read-Node $receiver.Process ('RNS: Destination ' + $b.Ready.propagation + ' is now 2 hops away') 90
        Send-Node $receiver.Process "propagation $($b.Ready.propagation)"
        $null = Read-Node $receiver.Process 'Propagation selection saved\.'
        Send-Node $receiver.Process 'sync'
        $null = Read-Node $receiver.Process 'Propagation sync: 1 messages \(Complete\)' 180
        Wait-NodeAttachment $receiverProfile $digest
        $originAfterHandoverRns = Join-Path $work 'live-origin-after-handover-rns'
        New-NodeRns $originAfterHandoverRns $port
        $origin = Start-NativeNode 'live-origin-restarted' $originProfile $originAfterHandoverRns
        $origin.Delivery | Should -Be $originDelivery
        Send-Node $origin.Process 'announce'
        $null = Read-Node $origin.Process 'Destinations announced\.'
        Send-Node $native.Process 'announce'
        $null = Read-Node $native.Process 'Destinations announced\.'
        Send-Node $b.Process 'announce'
        $null = Read-NodeEvent $b.Process 'announced'
        $null = Read-Node $origin.Process ('RNS: Destination ' + $b.Ready.propagation + ' is now 2 hops away') 90
        Send-Node $origin.Process "propagation $($b.Ready.propagation)"
        $null = Read-Node $origin.Process 'Propagation selection saved\.'
        Send-Node $origin.Process "contact delivery $($native.Delivery) propagated"
        $null = Read-Node $origin.Process 'Contact policy saved\.'
        Send-Node $origin.Process "attach $($native.Delivery) $payload"
        $null = Read-Node $origin.Process 'Message queued:' 180
        $deadline = [DateTime]::UtcNow.AddSeconds(180)
        do {
            Send-Node $b.Process 'stats'
            $stats = Read-NodeEvent $b.Process 'stats'
            if ($stats.messages -eq 1) { break }
            Start-Sleep -Milliseconds 200
        } while ([DateTime]::UtcNow -lt $deadline)
        $stats.messages | Should -Be 1
        Send-Node $b.Process ("peer-sync " + $a.Ready.propagation)
        $null = Read-NodeEvent $b.Process 'peer-sync-requested'
        $deadline = [DateTime]::UtcNow.AddSeconds(180)
        do {
            Send-Node $b.Process 'stats'
            $stats = Read-NodeEvent $b.Process 'stats'
            $peer = @($stats.peers | Where-Object hash -EQ $a.Ready.propagation)[0]
            if ($peer.key_ready) { break }
            Start-Sleep -Milliseconds 500
        } while ([DateTime]::UtcNow -lt $deadline)
        $peer.key_ready | Should -BeTrue
        Send-Node $b.Process ("peer-sync " + $a.Ready.propagation)
        $null = Read-NodeEvent $b.Process 'peer-sync-requested'
        Wait-NodeAttachment $target $digest
        foreach ($node in @($origin,$receiver,$native,$b)) { Stop-Node $node.Process }
        (Get-FileHash (Join-Path $target 'identity')).Hash | Should -Be $identity
        (Snapshot-Node $source) | Should -Be $before
        @{primaryCommit=$a.Ready.revision;nativeBinary=$env:DOTRETICULUM_NOMADNET_BINARY;
            nativeSha256=(Get-FileHash $env:DOTRETICULUM_NOMADNET_BINARY).Hash;
            payloadBytes=2200000;payloadSha256=$digest;node=$a.Ready.propagation;
            peer=$b.Ready.propagation;primaryReverseOutgoing=$crossPeer.outgoing;
            primaryReverseHandled=$crossPeer.handled;sourceUnchanged=$true;freshSelectedRnsUnchangedDuringImport=$true;
            nodePolicy=@{transferBytes=4000000;syncBytes=8000000;storageBytes=16000000;stampCost=13;maxPeers=20;peeringCost=18};
            externalClientsUseFreshRnsAfterTransportIdentityHandover=$true;
            originalNodePeerRnsProfileRetained=$true;cachedClientTransportRouteRecoveryNotClaimed=$true;
            topology='actual primary A node profile -> native A on fresh RNS cache; actual primary B recovers by signed discovery; native A -> primary B peer sync and native-client retrieval, then primary B -> native A local delivery; A also transports B/client traffic over direct loopback TCP; no new impairment or separate third transport node';
            cleanAndAbruptUnresolvedRestarts=$true;primaryNodeReverseCrossLoad=$true} |
            ConvertTo-Json | Set-Content (Join-Path $work 'live-node-acceptance.json') -Encoding utf8NoBOM
    }

    AfterAll {
        if ($enabled) {
            foreach ($process in $processes) {
                if (-not $process.HasExited) {
                    try { Send-Node $process 'quit' } catch { }
                    if (-not $process.WaitForExit(5000)) { $process.Kill($true); $process.WaitForExit() }
                }
                if ($pending.ContainsKey($process.Id)) {
                    try { $null = $logs[$process.Id].Text.AppendLine($pending[$process.Id].GetAwaiter().GetResult()) } catch { }
                }
                try { $null = $logs[$process.Id].Text.Append($process.StandardOutput.ReadToEnd()) } catch { }
                $logs[$process.Id].Text.ToString() | Set-Content (Join-Path $work ($logs[$process.Id].Name + '.stdout')) -Encoding utf8NoBOM
                $errors[$process.Id].GetAwaiter().GetResult() | Set-Content (Join-Path $work ($logs[$process.Id].Name + '.stderr')) -Encoding utf8NoBOM
                $process.Dispose()
            }
        }
    }
}
