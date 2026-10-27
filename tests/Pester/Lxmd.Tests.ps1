#Requires -Version 7.0
$script:liveEnabled = $env:DOTRETICULUM_LIVE -eq '1'
BeforeAll {
    $script:repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $script:binary = $env:DOTRETICULUM_LXMD_BINARY
    $script:python = $env:DOTRETICULUM_PYTHON
    $script:upstream = $env:DOTRETICULUM_UPSTREAM
    $script:referenceLogLevel = if ($env:DOTRETICULUM_LXMF_LOGLEVEL) { $env:DOTRETICULUM_LXMF_LOGLEVEL } else { '2' }
    $script:referenceModulePath = $script:upstream
    if ($env:DOTRETICULUM_LXMF_UPSTREAM) {
        $script:referenceModulePath += [IO.Path]::PathSeparator + $env:DOTRETICULUM_LXMF_UPSTREAM
    }
    function Start-CapturedProcess {
        param(
            [Parameter(Mandatory)] [string] $FileName,
            [string[]] $Arguments = @(),
            [hashtable] $Environment = @{}
        )
        $start = [System.Diagnostics.ProcessStartInfo]::new($FileName)
        $start.UseShellExecute = $false
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.RedirectStandardInput = $true
        $start.StandardOutputEncoding = [Text.Encoding]::UTF8
        foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
        foreach ($entry in $Environment.GetEnumerator()) { $start.Environment[$entry.Key] = $entry.Value }
        $process = [System.Diagnostics.Process]::new()
        $process.StartInfo = $start
        if (!$process.Start()) { throw "Could not start $FileName." }
        $read = $process.StandardOutput.ReadLineAsync()
        $errorRead = $process.StandardError.ReadLineAsync()
        [pscustomobject]@{
            Process = $process
            Output = [Collections.Generic.List[string]]::new()
            Error = [Collections.Generic.List[string]]::new()
            ReadTask = $read
            ErrorReadTask = $errorRead
            EndOfOutput = $false
            EndOfError = $false
        }
    }

    function Read-ProcessOutput {
        param($Running)
        if ($Running.ReadTask -and $Running.ReadTask.IsCompleted) {
            $line = $Running.ReadTask.GetAwaiter().GetResult()
            if ($null -eq $line) {
                $Running.ReadTask = $null
                $Running.EndOfOutput = $true
            } else {
                $Running.Output.Add($line)
                $Running.ReadTask = $Running.Process.StandardOutput.ReadLineAsync()
            }
        }
        if ($Running.ErrorReadTask -and $Running.ErrorReadTask.IsCompleted) {
            $line = $Running.ErrorReadTask.GetAwaiter().GetResult()
            if ($null -eq $line) {
                $Running.ErrorReadTask = $null
                $Running.EndOfError = $true
            } else {
                $Running.Error.Add($line)
                $Running.ErrorReadTask = $Running.Process.StandardError.ReadLineAsync()
            }
        }
    }

    function Wait-Event {
        param($Running, [string] $Name, [int] $TimeoutSeconds = 60, [string] $Destination, [object[]] $Other = @(), [int] $StartIndex = 0)
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        while ([DateTime]::UtcNow -lt $deadline) {
            Read-ProcessOutput $Running
            foreach ($additional in $Other) { Read-ProcessOutput $additional }
            for ($index = $StartIndex; $index -lt $Running.Output.Count; $index++) {
                $line = $Running.Output[$index]
                if (!$line.StartsWith('@@', [StringComparison]::Ordinal)) { continue }
                $event = $line.Substring(2) | ConvertFrom-Json
                if ($event.event -eq $Name -and (!$Destination -or $event.dest -eq $Destination)) { return $event }
            }
            if ($Running.Process.HasExited -and $Running.EndOfOutput) { break }
            Start-Sleep -Milliseconds 50
        }
        $stdout = $Running.Output -join "`n"
        $stderr = $Running.Error -join "`n"
        $exit = if ($Running.Process.HasExited) { $Running.Process.ExitCode } else { 'still running' }
        $otherLog = foreach ($additional in $Other) {
            Read-ProcessOutput $additional
            $extraExit = if ($additional.Process.HasExited) { $additional.Process.ExitCode } else { 'still running' }
            "Additional process exit=$extraExit`nstdout:`n$($additional.Output -join "`n")`nstderr:`n$($additional.Error -join "`n")"
        }
        throw "Timed out waiting for '$Name'. Exit=$exit`nstdout:`n$stdout`nstderr:`n$stderr`n$($otherLog -join "`n")"
    }

    function Wait-OutputMatch {
        param($Running, [string] $Pattern, [int] $TimeoutSeconds = 30)
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        while ([DateTime]::UtcNow -lt $deadline) {
            Read-ProcessOutput $Running
            foreach ($line in $Running.Output) {
                if ($line -match $Pattern) { return $line }
            }
            if ($Running.Process.HasExited -and $Running.EndOfOutput) { break }
            Start-Sleep -Milliseconds 50
        }
        $stdout = $Running.Output -join "`n"
        $stderr = $Running.Error -join "`n"
        $exit = if ($Running.Process.HasExited) { $Running.Process.ExitCode } else { 'still running' }
        throw "Timed out waiting for '$Pattern'. Exit=$exit`nstdout:`n$stdout`nstderr:`n$stderr"
    }

    function Invoke-AdapterCommand {
        param($Running, [string] $Command, [string] $Event, [int] $TimeoutSeconds = 30, [object[]] $Other = @())
        $start = $Running.Output.Count
        $Running.Process.StandardInput.WriteLine($Command)
        $Running.Process.StandardInput.Flush()
        Wait-Event $Running $Event $TimeoutSeconds -Other $Other -StartIndex $start
    }

    function Wait-NodeCount {
        param($Running, [string] $Destination, [int] $Count, [object[]] $Other, [int] $TimeoutSeconds = 150, [int] $MinimumPeers = 0)
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        do {
            $status = Invoke-AdapterCommand $Running "status $Destination" 'status' 30 -Other $Other
            if ($status.messages -eq $Count -and $status.peers -ge $MinimumPeers) { return $status }
            Start-Sleep -Seconds 2
        } while ([DateTime]::UtcNow -lt $deadline)
        $local = Invoke-AdapterCommand $Running 'local-status' 'local-status' 15 -Other $Other
        throw "Node $Destination did not reach $Count stored messages and $MinimumPeers peers; latest status: $($status | ConvertTo-Json -Depth 8 -Compress); reference local status: $($local | ConvertTo-Json -Depth 8 -Compress).`n$($Running.Output -join "`n")`nstderr:`n$($Running.Error -join "`n")`n$($Other | ForEach-Object { $_.Output -join "`n" })"
    }

    function Stop-CapturedProcess {
        param($Running, [switch] $SendQuit)
        if (!$Running) { return }
        if (!$Running.Process.HasExited) {
            if ($SendQuit) {
                try { $Running.Process.StandardInput.WriteLine('quit'); $Running.Process.StandardInput.Close() } catch {}
                if (!$Running.Process.WaitForExit(10000)) { $Running.Process.Kill($true) }
            } else {
                $Running.Process.Kill($true)
                $Running.Process.WaitForExit(10000) | Out-Null
            }
        }
        if ($Running.Process.HasExited) {
            try {
                if ($Running.ReadTask) { $Running.ReadTask.Wait(1000) | Out-Null }
                if ($Running.ErrorReadTask) { $Running.ErrorReadTask.Wait(1000) | Out-Null }
                Read-ProcessOutput $Running
            } catch {}
        }
        $Running.Process.Dispose()
    }

    function Invoke-Lxmd {
        param([string[]] $Arguments)
        $start = [System.Diagnostics.ProcessStartInfo]::new($script:binary)
        $start.UseShellExecute = $false
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
        $process = [System.Diagnostics.Process]::Start($start)
        try {
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            if (!$process.WaitForExit(15000)) { $process.Kill($true); throw 'lxmd CLI command exceeded 15 seconds.' }
            [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $stdout.GetAwaiter().GetResult(); Error = $stderr.GetAwaiter().GetResult() }
        } finally { $process.Dispose() }
    }
}

Describe 'Published lxmd command line' -Tag Integration, Native -Skip:(!$env:DOTRETICULUM_LXMD_BINARY) {
    It 'prints help and the default configuration without starting Reticulum' {
        $help = Invoke-Lxmd @('--help')
        $help.ExitCode | Should -Be 0
        $help.Output | Should -Match 'Lightweight Extensible Messaging Daemon'

        $example = Invoke-Lxmd @('--exampleconfig')
        $example.ExitCode | Should -Be 0
        $example.Output | Should -Match '\[lxmf\]'
        $example.Output | Should -Match 'delivery_transfer_max_accepted_size'

        $missing = Invoke-Lxmd @('--status', '--config', (Join-Path $script:repository ('artifacts\absent-' + [Guid]::NewGuid().ToString('N'))))
        $missing.ExitCode | Should -Be 201
        $missing.Error | Should -Match 'configuration directory does not exist'
        (Invoke-Lxmd @('--timeout=NaN')).ExitCode | Should -Be 2
        (Invoke-Lxmd @('--timeout=1e100')).ExitCode | Should -Be 2
        (Invoke-Lxmd @('--help=unexpected')).ExitCode | Should -Be 2
    }

    It 'rejects malformed configuration and unresolved interpolation before creating an identity' {
        $root = Join-Path (Join-Path $script:repository 'artifacts') ('lxmd-invalid-' + [Guid]::NewGuid().ToString('N'))
        $null = New-Item -ItemType Directory -Force -Path $root
        try {
            "[lxmf]`nstamp_cost = NaN" | Set-Content -LiteralPath (Join-Path $root 'config') -Encoding utf8
            $invalid = Invoke-Lxmd @('--config', $root)
            $invalid.ExitCode | Should -Be 1
            Test-Path -LiteralPath (Join-Path $root 'identity') | Should -BeFalse

            "[lxmf]`ndisplay_name = %(undefined)s" | Set-Content -LiteralPath (Join-Path $root 'config') -Encoding utf8
            $unsupported = Invoke-Lxmd @('--config', $root)
            $unsupported.ExitCode | Should -Be 1
            $unsupported.Error | Should -Match 'interpolation option'
            Test-Path -LiteralPath (Join-Path $root 'identity') | Should -BeFalse
            foreach ($invalidConfig in @(
                "[lxmf]`ndisplay_name = %(loop)s`nloop = %(display_name)s",
                "[lxmf]`ndisplay_name = first, second",
                "[lxmf]`ndisplay_name = 'unterminated",
                "[lxmf]`n[lxmf]",
                "[[skipped-level]]"
            )) {
                $invalidConfig | Set-Content -LiteralPath (Join-Path $root 'config') -Encoding utf8
                (Invoke-Lxmd @("--config=$root")).ExitCode | Should -Be 1
                Test-Path -LiteralPath (Join-Path $root 'identity') | Should -BeFalse
            }
        } finally {
            Remove-Item -LiteralPath $root -Recurse -Force
        }
    }
}

Describe 'Native lxmd delivery and restart' -Tag Integration, Native -Skip:(!$env:DOTRETICULUM_LXMD_BINARY -or !$script:liveEnabled -or !$env:DOTRETICULUM_PYTHON -or !$env:DOTRETICULUM_UPSTREAM) {
    It 'receives and persists a Python LXMF message, then retains it across daemon restart' {
        $root = Join-Path (Join-Path $script:repository 'artifacts') ('lxmd-pester-' + [Guid]::NewGuid().ToString('N'))
        $pythonConfig = Join-Path $root 'python-rns'
        $daemonConfig = Join-Path $root 'lxmd'
        $rnsConfig = Join-Path $root 'daemon-rns'
        $null = New-Item -ItemType Directory -Force -Path $pythonConfig, $daemonConfig, $rnsConfig
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
        $listener.Stop()
        $python = $null
        $daemon = $null
        try {
            $harness = Join-Path (Join-Path (Join-Path $script:repository 'tests') 'Interop') 'live_lxmf_node.py'
            $python = Start-CapturedProcess -FileName $script:python -Arguments @(
                $harness, '--config', $pythonConfig, '--tcp-server-port', "$port", '--loglevel', $script:referenceLogLevel
            ) -Environment @{
                DOTRETICULUM_UPSTREAM = $script:upstream
                PYTHONPATH = $script:referenceModulePath
                PYTHONIOENCODING = 'utf-8'
            }
            $ready = Wait-Event $python 'ready' 60
            $pythonDestination = $ready.dest

            @'
base = "lxmd # acceptance"
prefix = daemon's identity # unquoted apostrophe does not quote the comment
[DEFAULT]
suffix = """multiline
with comma, apostrophe's and #hash"""
[lxmf] # delivery settings
display_name = """%(prefix)s :: %(base)s :: %(suffix)s :: 100%%"""
announce_at_start = yes
delivery_transfer_max_accepted_size = 1000
stamp_cost = %(cost)s
[[DEFAULT]]
cost = 12

[propagation]
enable_node = no

[logging]
loglevel = 6

[ignored_extension]
[[nested]]
unused = %(not_consumed_by_lxmd)s
'@ | Set-Content -LiteralPath (Join-Path $daemonConfig 'config') -Encoding utf8
            $referenceParser = Join-Path (Join-Path (Join-Path $script:repository 'tests') 'Interop') 'lxmd_config_reference.py'
            $configuration = Start-CapturedProcess $script:python @($referenceParser, (Join-Path $daemonConfig 'config')) @{
                DOTRETICULUM_UPSTREAM = $script:upstream; PYTHONPATH = $script:referenceModulePath; PYTHONIOENCODING = 'utf-8'
            }
            $referenceLine = Wait-OutputMatch $configuration '^\{' 15
            $configuration.Process.WaitForExit(15000) | Should -BeTrue
            $configuration.Process.ExitCode | Should -Be 0
            $referenceConfiguration = $referenceLine | ConvertFrom-Json
            Stop-CapturedProcess $configuration
            @"
[reticulum]
enable_transport = no
share_instance = no
instance_name = lxmd_acceptance

[logging]
loglevel = 4

[interfaces]
[[ToPython]]
type = TCPClientInterface
enabled = yes
target_host = 127.0.0.1
target_port = $port
"@ | Set-Content -LiteralPath (Join-Path $rnsConfig 'config') -Encoding utf8

            $validator = Join-Path (Join-Path (Join-Path $script:repository 'tests') 'Interop') 'verify_lxmd_message.py'
            $notification = Join-Path $root 'inbound-notification.json'
            $handlerCommand = '"' + $script:python + '" "' + $validator + '" --notify "' + $notification + '"'
            $daemonArguments = @('--config', $daemonConfig, '--rnsconfig', $rnsConfig, '--on-inbound', $handlerCommand, '--service')
            $daemonEnvironment = @{ PYTHONPATH = $script:referenceModulePath; PYTHONIOENCODING = 'utf-8' }
            $daemon = Start-CapturedProcess -FileName $script:binary -Arguments $daemonArguments -Environment $daemonEnvironment
            $daemonReady = Wait-OutputMatch $daemon '^lxmd: delivery destination ' 30
            $deliveryDestination = $daemonReady.Split(' ')[-1]
            $announce = Wait-Event $python 'announce' 45
            $announce.dest | Should -Be $deliveryDestination
            $announce.dest | Should -Not -BeNullOrEmpty
            $announce.display_name | Should -Be $referenceConfiguration.display_name
            $identityFingerprint = (Get-FileHash -LiteralPath (Join-Path $daemonConfig 'identity') -Algorithm SHA256).Hash

            $title = 'lxmd-persisted-message'
            $null = $python.Process.StandardInput.WriteLine("send $($announce.dest) direct 128 lxmd-persistence $title")
            $python.Process.StandardInput.Flush()
            $sent = Wait-Event $python 'sent_queued' 30
            $delivered = Wait-Event $python 'delivered' 90 -Other @($daemon)
            $delivered.hash | Should -Be $sent.hash
            $messagePath = Join-Path (Join-Path (Join-Path $daemonConfig 'storage') 'messages') $sent.hash
            $deadline = [DateTime]::UtcNow.AddSeconds(15)
            while (!(Test-Path -LiteralPath $messagePath) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 50 }
            if (!(Test-Path -LiteralPath $messagePath)) {
                Read-ProcessOutput $daemon
                Read-ProcessOutput $python
                throw "Python reported delivery proof for $($sent.hash), but lxmd did not persist the message.`nDaemon stdout:`n$($daemon.Output -join "`n")`nDaemon stderr:`n$($daemon.Error -join "`n")`nPython stdout:`n$($python.Output -join "`n")`nPython stderr:`n$($python.Error -join "`n")"
            }

            $deadline = [DateTime]::UtcNow.AddSeconds(15)
            while (!(Test-Path -LiteralPath $notification) -and [DateTime]::UtcNow -lt $deadline) {
                Read-ProcessOutput $daemon
                Start-Sleep -Milliseconds 50
            }
            Test-Path -LiteralPath $notification | Should -BeTrue
            $notified = Get-Content -LiteralPath $notification -Raw | ConvertFrom-Json
            $notified.hash | Should -Be $sent.hash
            $notified.title | Should -Be $title
            Get-Content -LiteralPath (Join-Path $daemonConfig 'logfile') -Raw | Should -Match 'written to'
            $verify = Start-CapturedProcess -FileName $script:python -Arguments @($validator, $messagePath, $title, '128') -Environment @{
                DOTRETICULUM_UPSTREAM = $script:upstream
                PYTHONPATH = $script:referenceModulePath
                PYTHONIOENCODING = 'utf-8'
            }
            $verify.Process.WaitForExit(15000) | Should -BeTrue
            $verify.Process.ExitCode | Should -Be 0 -Because ($verify.Error -join "`n")
            Stop-CapturedProcess $verify

            Stop-CapturedProcess $daemon
            $daemon = Start-CapturedProcess -FileName $script:binary -Arguments $daemonArguments -Environment $daemonEnvironment
            $restarted = Wait-OutputMatch $daemon '^lxmd: delivery destination ' 30
            $restarted.Split(' ')[-1] | Should -Be $deliveryDestination
            $identityFingerprintAfterRestart = (Get-FileHash -LiteralPath (Join-Path $daemonConfig 'identity') -Algorithm SHA256).Hash
            $identityFingerprintAfterRestart | Should -Be $identityFingerprint
            Test-Path -LiteralPath $messagePath | Should -BeTrue

            $verifyAgain = Start-CapturedProcess -FileName $script:python -Arguments @($validator, $messagePath, $title, '128') -Environment @{
                DOTRETICULUM_UPSTREAM = $script:upstream
                PYTHONPATH = $script:referenceModulePath
                PYTHONIOENCODING = 'utf-8'
            }
            $verifyAgain.Process.WaitForExit(15000) | Should -BeTrue
            $verifyAgain.Process.ExitCode | Should -Be 0 -Because ($verifyAgain.Error -join "`n")
            Stop-CapturedProcess $verifyAgain
        } finally {
            Stop-CapturedProcess $daemon
            Stop-CapturedProcess $python -SendQuit
            if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
        }

    }

    It 'persists a Python propagated message over restart and serves it back to its authenticated owner' {
            $root = Join-Path (Join-Path $script:repository 'artifacts') ('lxmd-propagation-' + [Guid]::NewGuid().ToString('N'))
            $daemonConfig = Join-Path $root 'lxmd'
            $rnsConfig = Join-Path $root 'daemon-rns'
            $pythonConfig = Join-Path $root 'python-rns'
            $null = New-Item -ItemType Directory -Force -Path $daemonConfig, $rnsConfig, $pythonConfig
            $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
            $listener.Start()
            $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
            $listener.Stop()
            $python = $null
            $daemon = $null
            try {
                $harness = Join-Path (Join-Path (Join-Path $script:repository 'tests') 'Interop') 'live_lxmf_node.py'
                $python = Start-CapturedProcess -FileName $script:python -Arguments @(
                    $harness, '--config', $pythonConfig, '--tcp-server-port', "$port", '--loglevel', $script:referenceLogLevel
                ) -Environment @{
                    DOTRETICULUM_UPSTREAM = $script:upstream
                    PYTHONPATH = $script:referenceModulePath
                    PYTHONIOENCODING = 'utf-8'
                }
                $ready = Wait-Event $python 'ready' 60
                @"
    recipient = $($ready.dest)
    [lxmf]
    display_name = lxmd propagation acceptance
    announce_at_start = yes

    [propagation]
    enable_node = yes
    autopeer = no
    announce_at_start = yes
    auth_required = yes
    propagation_stamp_cost_target = 13
    propagation_stamp_cost_flexibility = 0
    prioritise_destinations = "%(recipient)s",
    sequential_pn_stamp_validation = yes
    static_peers_bypass_sequential = yes
    max_inbound_syncs = 3

    [logging]
    loglevel = 6
"@ | Set-Content -LiteralPath (Join-Path $daemonConfig 'config') -Encoding utf8
                $ready.identity_hash | Set-Content -LiteralPath (Join-Path $daemonConfig 'allowed') -Encoding ascii
                @"
    [reticulum]
    enable_transport = no
    share_instance = no
    instance_name = lxmd_propagation_acceptance

    [interfaces]
    [[ToPython]]
    type = TCPClientInterface
    enabled = yes
    target_host = 127.0.0.1
    target_port = $port
"@ | Set-Content -LiteralPath (Join-Path $rnsConfig 'config') -Encoding utf8
                $daemonArguments = @('--config', $daemonConfig, '--rnsconfig', $rnsConfig)
                $daemon = Start-CapturedProcess -FileName $script:binary -Arguments $daemonArguments
                $pnReady = Wait-OutputMatch $daemon '^lxmd: propagation destination ' 30
                $pnDestination = $pnReady.Split(' ')[-1]
                $null = Wait-Event $python 'announce' 45 -Other @($daemon)
                $python.Process.StandardInput.WriteLine("use-pn $pnDestination")
                $python.Process.StandardInput.Flush()
                $null = Wait-Event $python 'pn-selected' 10
                $python.Process.StandardInput.WriteLine("send $($ready.dest) propagated 1024 lxmd-pn-restart propagation-roundtrip")
                $python.Process.StandardInput.Flush()
                $queued = Wait-Event $python 'sent_queued' 60 -Other @($daemon)
                $null = Wait-Event $python 'delivered' 120 -Other @($daemon)
                $store = Join-Path (Join-Path (Join-Path $daemonConfig 'storage') 'lxmf') 'messagestore'
                $deadline = [DateTime]::UtcNow.AddSeconds(15)
                while (@(Get-ChildItem -LiteralPath $store -File -ErrorAction SilentlyContinue).Count -ne 1 -and [DateTime]::UtcNow -lt $deadline) {
                    Read-ProcessOutput $daemon
                    Start-Sleep -Milliseconds 50
                }
                @(Get-ChildItem -LiteralPath $store -File).Count | Should -Be 1
                $fingerprint = (Get-FileHash -LiteralPath (Join-Path $daemonConfig 'identity') -Algorithm SHA256).Hash
                $python.Process.StandardInput.WriteLine('cancel-retrieval')
                $python.Process.StandardInput.Flush()
                $null = Wait-Event $python 'retrieval-cancelled' 10
                $restartLine = $python.Output.Count
                Stop-CapturedProcess $daemon
                $daemon = Start-CapturedProcess -FileName $script:binary -Arguments $daemonArguments
                $restarted = Wait-OutputMatch $daemon '^lxmd: propagation destination ' 30
                $restarted.Split(' ')[-1] | Should -Be $pnDestination
                (Get-FileHash -LiteralPath (Join-Path $daemonConfig 'identity') -Algorithm SHA256).Hash | Should -Be $fingerprint
                @(Get-ChildItem -LiteralPath $store -File).Count | Should -Be 1
                $null = Wait-Event $python 'pn-announce' 30 -Destination $pnDestination -Other @($daemon) -StartIndex $restartLine

                $python.Process.StandardInput.WriteLine('retrieve')
                $python.Process.StandardInput.Flush()
                $received = Wait-Event $python 'received' 90 -Other @($daemon)
                $received.hash | Should -Be $queued.hash
                $received.sha256 | Should -Be $queued.sha256
                $received.title | Should -Be 'propagation-roundtrip'
                $received.size | Should -Be 1024
                $received.signature_validated | Should -BeTrue
                $retrieved = Wait-Event $python 'retrieved' 30 -Other @($daemon)
                $retrieved.state | Should -Be 7
                $deadline = [DateTime]::UtcNow.AddSeconds(15)
                while (@(Get-ChildItem -LiteralPath $store -File).Count -ne 0 -and [DateTime]::UtcNow -lt $deadline) {
                    Read-ProcessOutput $daemon
                    Start-Sleep -Milliseconds 50
                }
                @(Get-ChildItem -LiteralPath $store -File).Count | Should -Be 0
            } finally {
                Stop-CapturedProcess $daemon
                Stop-CapturedProcess $python -SendQuit
                if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
        }
    }

    It 'queries Python node control from the native CLI and native node control from Python lxmd' {
        $root = Join-Path (Join-Path $script:repository 'artifacts') ('lxmd-control-' + [Guid]::NewGuid().ToString('N'))
        $pythonConfig = Join-Path $root 'python-rns'
        $daemonConfig = Join-Path $root 'lxmd'
        $rnsConfig = Join-Path $root 'daemon-rns'
        $clientConfig = Join-Path $root 'client-rns'
        $identity = Join-Path $root 'reference-identity'
        $null = New-Item -ItemType Directory -Force -Path $pythonConfig, $daemonConfig, $rnsConfig, $clientConfig
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
        $listener.Stop()
        $python = $null
        $daemon = $null
        try {
            @"
[reticulum]
share_instance = no
enable_transport = no
[logging]
loglevel = 2
[interfaces]
[[daemon control acceptance]]
type = TCPServerInterface
enabled = yes
listen_ip = 127.0.0.1
listen_port = $port
"@ | Set-Content -LiteralPath (Join-Path $pythonConfig 'config') -Encoding utf8
            foreach ($directory in @($rnsConfig, $clientConfig)) {
                @"
[reticulum]
share_instance = no
enable_transport = no
[logging]
loglevel = 2
[interfaces]
[[Python propagation control]]
type = TCPClientInterface
enabled = yes
target_host = 127.0.0.1
target_port = $port
"@ | Set-Content -LiteralPath (Join-Path $directory 'config') -Encoding utf8
            }
            $environment = @{
                PYTHONPATH = $script:referenceModulePath
                DOTRETICULUM_UPSTREAM = $script:upstream
                PYTHONUNBUFFERED = '1'
                PYTHONNOUSERSITE = '1'
            }
            $adapter = Join-Path (Join-Path (Join-Path $script:repository 'tests') 'Interop') 'lxmd_control_peer.py'
            $python = Start-CapturedProcess $script:python @($adapter, $pythonConfig, (Join-Path $root 'python-storage'), $identity) $environment
            $ready = Wait-Event $python 'ready' 30
            @"
[lxmf]
announce_at_start = yes
[propagation]
enable_node = yes
autopeer = no
announce_at_start = yes
control_allowed = $($ready.identity_hash)
[logging]
loglevel = 6
"@ | Set-Content -LiteralPath (Join-Path $daemonConfig 'config') -Encoding utf8
            $daemon = Start-CapturedProcess $script:binary @('--config', $daemonConfig, '--rnsconfig', $rnsConfig)
            $line = Wait-OutputMatch $daemon 'lxmd: propagation destination ([0-9a-f]{32})'
            $null = $line -match 'lxmd: propagation destination ([0-9a-f]{32})'
            $nativeDestination = $Matches[1]
            $null = Wait-Event $python 'pn-announce' 30 -Destination $nativeDestination -Other @($daemon)
            $python.Process.StandardInput.WriteLine("status $nativeDestination")
            $python.Process.StandardInput.Flush()
            $status = Wait-Event $python 'status' 30 -Other @($daemon)
            $status.dest | Should -Be $nativeDestination
            $status.messages | Should -Be 0
            $admission = Invoke-AdapterCommand $python "admission $nativeDestination" 'admission-recovered' 120 -Other @($daemon)
            $admission.saturated | Should -Be 246
            $admission.recovered | Should -BeTrue
            $admission.held_resources | Should -Be 3
            $rejected = Invoke-AdapterCommand $python "bad-stamp $nativeDestination" 'bad-stamp-rejected' 90 -Other @($daemon)
            $rejected.response | Should -Be $rejected.expected
            $elapsed = [Diagnostics.Stopwatch]::StartNew()
            do {
                $healthy = Invoke-AdapterCommand $python "status $nativeDestination" 'status' 30 -Other @($daemon)
                $healthy.messages | Should -Be 1
                Start-Sleep -Seconds 2
            } while ($elapsed.Elapsed.TotalSeconds -lt 181)
            $expired = Invoke-AdapterCommand $python 'retry-expired' 'throttle-expired' 90 -Other @($daemon)
            $expired.response | Should -BeTrue
            $elapsed.Elapsed.TotalSeconds | Should -BeGreaterOrEqual 180
            $null = Wait-NodeCount $python $nativeDestination 2 @($daemon)
            $healthy = Invoke-AdapterCommand $python "status $nativeDestination" 'status' 30 -Other @($daemon)
            $healthy.messages | Should -Be 2

            $nativeStatus = Invoke-Lxmd @('--status', '--peers', '--identity', $identity,
                "--remote=$($ready.dest)", '--rnsconfig', $clientConfig, '--timeout', '10')
            $nativeStatus.ExitCode | Should -Be 0 -Because ($nativeStatus.Output + $nativeStatus.Error)
            $nativeStatus.Output | Should -Match 'LXMF Propagation Node running on'
            $nativeStatus.Output | Should -Match 'Messagestore contains 0 messages'
            $nativeStatus.Output | Should -Match 'clients.client_propagation_messages_received ='
            $nativeStatus.Output | Should -Match 'messagestore.limit ='

            $unpeer = Invoke-Lxmd @('--break', ('00' * 16), '--identity', $identity,
                '--remote', $ready.dest, '--rnsconfig', $clientConfig, '--timeout', '10')
            $unpeer.ExitCode | Should -Be 206 -Because ($unpeer.Output + $unpeer.Error)
            $unpeer.Output | Should -Match 'requested peer was not found'
            if (!$IsWindows) {
                & /bin/kill -TERM $daemon.Process.Id
                $LASTEXITCODE | Should -Be 0
                $daemon.Process.WaitForExit(15000) | Should -BeTrue
                $daemon.Process.ExitCode | Should -Be 0
            }
        } finally {
            Stop-CapturedProcess $daemon
            Stop-CapturedProcess $python -SendQuit
            if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
        }
    }

    It 'automatically synchronizes two native daemons with Python and recovers after a failed peer and restart' {
        $root = Join-Path (Join-Path $script:repository 'artifacts') ('lxmd-peering-' + [Guid]::NewGuid().ToString('N'))
        $pythonConfig = Join-Path $root 'python-rns'
        $clientConfig = Join-Path $root 'control-rns'
        $identity = Join-Path $root 'reference-identity'
        $null = New-Item -ItemType Directory -Force -Path $pythonConfig, $clientConfig
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
        $listener.Stop()
        $python = $null
        $daemons = @($null, $null)
        $destinations = @('', '')
        try {
            @"
[reticulum]
share_instance = no
enable_transport = yes
[logging]
loglevel = 2
[interfaces]
[[daemon peering acceptance]]
type = TCPServerInterface
enabled = yes
listen_ip = 127.0.0.1
listen_port = $port
"@ | Set-Content -LiteralPath (Join-Path $pythonConfig 'config') -Encoding utf8
            $adapter = Join-Path (Join-Path (Join-Path $script:repository 'tests') 'Interop') 'lxmd_control_peer.py'
            $python = Start-CapturedProcess $script:python @($adapter, $pythonConfig, (Join-Path $root 'python-storage'), $identity, '--autopeer') @{
                PYTHONPATH = $script:referenceModulePath; PYTHONIOENCODING = 'utf-8'; PYTHONNOUSERSITE = '1'
            }
            $ready = Wait-Event $python 'ready' 30
            foreach ($index in 0..1) {
                $config = Join-Path $root "native-$index"
                $rns = Join-Path $root "rns-$index"
                $null = New-Item -ItemType Directory -Force -Path $config, $rns
                @"
[lxmf]
announce_at_start = yes
[propagation]
enable_node = yes
autopeer = yes
announce_at_start = yes
announce_interval = 1
propagation_stamp_cost_target = 13
propagation_stamp_cost_flexibility = 0
peering_cost = 18
control_allowed = "$($ready.identity_hash)",
[logging]
loglevel = 6
"@ | Set-Content -LiteralPath (Join-Path $config 'config') -Encoding utf8
                @"
[reticulum]
share_instance = no
enable_transport = no
[logging]
loglevel = 2
[interfaces]
[[Python propagation hub]]
type = TCPClientInterface
enabled = yes
target_host = 127.0.0.1
target_port = $port
"@ | Set-Content -LiteralPath (Join-Path $rns 'config') -Encoding utf8
                if ($index -eq 0) { Copy-Item -LiteralPath (Join-Path $rns 'config') -Destination (Join-Path $clientConfig 'config') }
                $daemons[$index] = Start-CapturedProcess $script:binary @('--config', $config, '--rnsconfig', $rns)
                $line = Wait-OutputMatch $daemons[$index] '^lxmd: propagation destination '
                $destinations[$index] = $line.Split(' ')[-1]
                $null = Wait-Event $python 'pn-announce' 30 -Destination $destinations[$index] -Other @($daemons[$index])
                if ($index -eq 0) {
                    # The upstream scheduler randomly selects one peer per interval.
                    # Establish the mixed-reference edge before introducing a second candidate.
                    $null = Invoke-AdapterCommand $python 'announce' 'announced' 15 -Other @($daemons[0])
                    $seeded = Invoke-AdapterCommand $python 'seed python-origin 1024' 'seeded' 60 -Other @($daemons[0])
                    $null = Wait-NodeCount $python $destinations[0] 1 @($daemons[0])
                }
            }
            $null = Invoke-AdapterCommand $python 'announce' 'announced' 15 -Other $daemons
            $firstB = Wait-NodeCount $python $destinations[1] 1 $daemons -MinimumPeers 2
            $firstA = Wait-NodeCount $python $destinations[0] 1 $daemons -MinimumPeers 2
            $firstA.peers | Should -BeGreaterOrEqual 2
            $firstB.peers | Should -BeGreaterOrEqual 2

            $null = Invoke-AdapterCommand $python "send $($destinations[0]) native-origin 1024" 'queued' 30 -Other $daemons
            $null = Wait-Event $python 'sent' 90 -Other $daemons
            $null = Wait-NodeCount $python $destinations[1] 2 $daemons
            $deadline = [DateTime]::UtcNow.AddSeconds(150)
            do {
                $local = Invoke-AdapterCommand $python 'local-status' 'local-status' 15 -Other $daemons
                if ($local.messages -eq 2) { break }
                Start-Sleep -Seconds 2
            } while ([DateTime]::UtcNow -lt $deadline)
            $local.messages | Should -Be 2
            $local.ids | Should -Contain $seeded.transient

            Stop-CapturedProcess $daemons[1]
            $daemons[1] = $null
            $sync = Invoke-Lxmd @('--sync', $destinations[1], '--remote', $destinations[0], '--identity', $identity,
                '--rnsconfig', $clientConfig, '--timeout', '10')
            $sync.ExitCode | Should -Be 0 -Because ($sync.Output + $sync.Error)
            $null = Invoke-AdapterCommand $python 'seed after-peer-failure 1024' 'seeded' 60 -Other @($daemons[0])
            $surviving = Wait-NodeCount $python $destinations[0] 3 @($daemons[0])
            $surviving.messages | Should -Be 3

            $configB = Join-Path $root 'native-1'
            $rnsB = Join-Path $root 'rns-1'
            $restartIndex = $python.Output.Count
            $daemons[1] = Start-CapturedProcess $script:binary @('--config', $configB, '--rnsconfig', $rnsB)
            $line = Wait-OutputMatch $daemons[1] '^lxmd: propagation destination '
            $line.Split(' ')[-1] | Should -Be $destinations[1]
            $null = Wait-Event $python 'pn-announce' 30 -Destination $destinations[1] -StartIndex $restartIndex -Other $daemons
            $null = Wait-NodeCount $python $destinations[1] 3 $daemons
            $unpeer = Invoke-Lxmd @('--break', $destinations[1], '--remote', $destinations[0], '--identity', $identity,
                '--rnsconfig', $clientConfig, '--timeout', '10')
            $unpeer.ExitCode | Should -Be 0 -Because ($unpeer.Output + $unpeer.Error)
            $after = Invoke-AdapterCommand $python "status $($destinations[0])" 'status' 30 -Other $daemons
            @($after.peer_details | Where-Object dest -EQ $destinations[1]).Count | Should -Be 0
        } finally {
            foreach ($daemon in $daemons) { Stop-CapturedProcess $daemon }
            Stop-CapturedProcess $python -SendQuit
            if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
        }
    }
}
