#Requires -Version 7.0
$script:liveEnabled = $env:DOTRETICULUM_LIVE -eq '1'
BeforeAll {
    $script:repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $script:binary = $env:DOTRETICULUM_LXMD_BINARY
    $script:python = $env:DOTRETICULUM_PYTHON
    $script:upstream = $env:DOTRETICULUM_UPSTREAM

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
        param($Running, [string] $Name, [int] $TimeoutSeconds = 60, [string] $Destination, [object[]] $Other = @())
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        while ([DateTime]::UtcNow -lt $deadline) {
            Read-ProcessOutput $Running
            foreach ($additional in $Other) { Read-ProcessOutput $additional }
            foreach ($line in $Running.Output) {
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

        $unsupported = Invoke-Lxmd @('--status')
        $unsupported.ExitCode | Should -Be 1
        $unsupported.Error | Should -Match 'remote propagation management operations are not implemented'
        $unsupported.Error | Should -Match 'no network request was made'
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
                $harness, '--config', $pythonConfig, '--tcp-server-port', "$port"
            ) -Environment @{
                DOTRETICULUM_UPSTREAM = $script:upstream
                PYTHONPATH = $script:upstream
                PYTHONIOENCODING = 'utf-8'
            }
            $ready = Wait-Event $python 'ready' 60
            $pythonDestination = $ready.dest

            @'
[lxmf]
display_name = lxmd acceptance
announce_at_start = yes
delivery_transfer_max_accepted_size = 1000

[propagation]
enable_node = no

[logging]
loglevel = 6
'@ | Set-Content -LiteralPath (Join-Path $daemonConfig 'config') -Encoding utf8
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

            $daemonArguments = @('--config', $daemonConfig, '--rnsconfig', $rnsConfig)
            $daemon = Start-CapturedProcess -FileName $script:binary -Arguments $daemonArguments
            $daemonReady = Wait-OutputMatch $daemon '^lxmd: delivery destination ' 30
            $deliveryDestination = $daemonReady.Split(' ')[-1]
            $announce = Wait-Event $python 'announce' 45
            $announce.dest | Should -Be $deliveryDestination
            $announce.dest | Should -Not -BeNullOrEmpty
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

            $validator = Join-Path (Join-Path (Join-Path $script:repository 'tests') 'Interop') 'verify_lxmd_message.py'
            $verify = Start-CapturedProcess -FileName $script:python -Arguments @($validator, $messagePath, $title, '128') -Environment @{
                DOTRETICULUM_UPSTREAM = $script:upstream
                PYTHONPATH = $script:upstream
                PYTHONIOENCODING = 'utf-8'
            }
            $verify.Process.WaitForExit(15000) | Should -BeTrue
            $verify.Process.ExitCode | Should -Be 0 -Because ($verify.Error -join "`n")
            Stop-CapturedProcess $verify

            Stop-CapturedProcess $daemon
            $daemon = Start-CapturedProcess -FileName $script:binary -Arguments $daemonArguments
            $restarted = Wait-OutputMatch $daemon '^lxmd: delivery destination ' 30
            $restarted.Split(' ')[-1] | Should -Be $deliveryDestination
            $identityFingerprintAfterRestart = (Get-FileHash -LiteralPath (Join-Path $daemonConfig 'identity') -Algorithm SHA256).Hash
            $identityFingerprintAfterRestart | Should -Be $identityFingerprint
            Test-Path -LiteralPath $messagePath | Should -BeTrue

            $verifyAgain = Start-CapturedProcess -FileName $script:python -Arguments @($validator, $messagePath, $title, '128') -Environment @{
                DOTRETICULUM_UPSTREAM = $script:upstream
                PYTHONPATH = $script:upstream
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
}
