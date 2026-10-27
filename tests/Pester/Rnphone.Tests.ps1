#Requires -Version 7.0
BeforeAll {
    $script:phoneRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $script:phoneBinary = if ($env:DOTLXST_RNPHONE_BINARY) { $env:DOTLXST_RNPHONE_BINARY } else { $env:DOTRETICULUM_RNPHONE_BINARY }
    $script:phoneArtifacts = Join-Path (Get-Location).Path 'artifacts'

    function Start-PhoneProcess {
        param([string] $File, [string[]] $Arguments, [hashtable] $Environment = @{})
        $start = [Diagnostics.ProcessStartInfo]::new($File)
        $start.UseShellExecute = $false
        $start.RedirectStandardInput = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
        foreach ($entry in $Environment.GetEnumerator()) { $start.Environment[$entry.Key] = $entry.Value }
        $process = [Diagnostics.Process]::Start($start)
        [pscustomobject]@{
            Process = $process
            Output = [Collections.Generic.List[string]]::new()
            Error = [Collections.Generic.List[string]]::new()
            Read = $process.StandardOutput.ReadLineAsync()
            ErrorRead = $process.StandardError.ReadLineAsync()
        }
    }

    function Read-PhoneProcess {
        param($Running)
        if ($Running.Read -and $Running.Read.IsCompleted) {
            $line = $Running.Read.GetAwaiter().GetResult()
            if ($null -eq $line) { $Running.Read = $null }
            else {
                $Running.Output.Add($line)
                $Running.Read = $Running.Process.StandardOutput.ReadLineAsync()
            }
        }
        if ($Running.ErrorRead -and $Running.ErrorRead.IsCompleted) {
            $line = $Running.ErrorRead.GetAwaiter().GetResult()
            if ($null -eq $line) { $Running.ErrorRead = $null }
            else {
                $Running.Error.Add($line)
                $Running.ErrorRead = $Running.Process.StandardError.ReadLineAsync()
            }
        }
    }

    function Wait-PhoneLine {
        param($Running, [string] $Pattern, [int] $Occurrence = 1, [int] $Seconds = 30)
        $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
        while ([DateTime]::UtcNow -lt $deadline) {
            Read-PhoneProcess $Running
            $matching = @($Running.Output | Where-Object { $_ -match $Pattern })
            if ($matching.Count -ge $Occurrence) { return $matching[$Occurrence - 1] }
            if ($Running.Process.HasExited -and !$Running.Read) { break }
            Start-Sleep -Milliseconds 20
        }
        throw "rnphone acceptance timed out for $Pattern occurrence $Occurrence.`nstdout:`n$($Running.Output -join "`n")`nstderr:`n$($Running.Error -join "`n")"
    }

    function Send-PhoneLine {
        param($Running, [string] $Line)
        $Running.Process.StandardInput.WriteLine($Line)
        $Running.Process.StandardInput.Flush()
    }

    function Stop-PhoneProcess {
        param($Running, [switch] $Graceful)
        if (!$Running) { return }
        if (!$Running.Process.HasExited -and $Graceful) {
            Send-PhoneLine $Running 'quit'
            $Running.Process.StandardInput.Close()
            $Running.Process.WaitForExit(10000) | Out-Null
        }
        if (!$Running.Process.HasExited) { $Running.Process.Kill($true) }
        $Running.Process.WaitForExit(10000) | Out-Null
        $Running.Process.Dispose()
    }

    function Invoke-PhoneCommand {
        param([string] $File, [string[]] $Arguments, [hashtable] $Environment = @{})
        $start = [Diagnostics.ProcessStartInfo]::new($File)
        $start.UseShellExecute = $false
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
        foreach ($entry in $Environment.GetEnumerator()) { $start.Environment[$entry.Key] = $entry.Value }
        $process = [Diagnostics.Process]::Start($start)
        try {
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            if (!$process.WaitForExit(15000)) { $process.Kill($true); throw 'rnphone acceptance command timed out.' }
            [pscustomobject]@{ Code = $process.ExitCode; Output = $stdout.GetAwaiter().GetResult(); Error = $stderr.GetAwaiter().GetResult() }
        } finally { $process.Dispose() }
    }

    function Get-PhonePort {
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $listener.Start()
        try { ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
        finally { $listener.Stop() }
    }
}

Describe 'Published rnphone CLI' -Tag Integration, Native -Skip:(!$env:DOTLXST_RNPHONE_BINARY -and !$env:DOTRETICULUM_RNPHONE_BINARY) {
    It 'validates options and prints upstream-named CLI entry points without opening audio' {
        $help = Invoke-PhoneCommand $script:phoneBinary @('--help')
        $help.Code | Should -Be 0
        $help.Output | Should -Match '--list-devices'
        $help.Output | Should -Match 'not implemented'
        (Invoke-PhoneCommand $script:phoneBinary @('--version')).Output | Should -Match 'rnphone 0\.4\.5'
        (Invoke-PhoneCommand $script:phoneBinary @('--systemd')).Output | Should -Match 'ExecStart=.*rnphone --service'
        (Invoke-PhoneCommand $script:phoneBinary @('--invalid-option')).Code | Should -Be 2
    }

    It 'rejects unsupported hardware configuration rather than pretending it was applied' {
        $directory = Join-Path $script:phoneArtifacts ('rnphone-bad-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force $directory | Out-Null
        try {
            "[hardware]`nkeypad = gpio_4x4`n" | Set-Content (Join-Path $directory 'config')
            $result = Invoke-PhoneCommand $script:phoneBinary @('--config', $directory)
            $result.Code | Should -Be 1
            $result.Error | Should -Match 'not implemented'
            Test-Path (Join-Path $directory 'identity') | Should -BeFalse
        } finally { Remove-Item -LiteralPath $directory -Recurse -Force }
    }
}

Describe 'Native rnphone against actual Python Telephone' -Tag Integration, Native -Skip:(
    !$IsLinux -or (!$env:DOTLXST_RNPHONE_BINARY -and !$env:DOTRETICULUM_RNPHONE_BINARY) -or
    $env:DOTLXST_LIVE -ne '1' -or $env:DOTLXST_VIRTUAL_AUDIO -ne '1') {
    It 'originates and answers real Opus calls, runs both audio paths, and retains identity across restart' {
        $root = Join-Path $script:phoneArtifacts ('rnphone-pester-' + [Guid]::NewGuid().ToString('N'))
        $phoneConfig = Join-Path $root 'phone'
        $rnsConfig = Join-Path $root 'rns'
        $pythonConfig = Join-Path $root 'python'
        $pulseConfig = Join-Path $root 'pulse.pa'
        New-Item -ItemType Directory -Force $phoneConfig, $rnsConfig, $pythonConfig | Out-Null
        [IO.File]::SetUnixFileMode($root, [IO.UnixFileMode]448)
        $pulse = $null
        $python = $null
        $phone = $null
        try {
            $pulsePort = Get-PhonePort
            @"
load-module module-native-protocol-tcp listen=127.0.0.1 port=$pulsePort auth-anonymous=yes
load-module module-null-sink sink_name=lxst_output rate=48000 channels=2
load-module module-null-source source_name=lxst_input rate=48000 channels=2
set-default-sink lxst_output
set-default-source lxst_input
"@ | Set-Content $pulseConfig
            $pulseAddress = "tcp:127.0.0.1:$pulsePort"
            $pulse = Start-PhoneProcess 'pulseaudio' @('-n', "--file=$pulseConfig", '--daemonize=no',
                '--use-pid-file=no', '--exit-idle-time=-1', '--disable-shm=yes', '--log-target=stderr') @{
                    PULSE_RUNTIME_PATH = $root; PULSE_STATE_PATH = $root; XDG_RUNTIME_DIR = $root; PULSE_SERVER = ''
                    DBUS_SESSION_BUS_ADDRESS = 'unix:abstract=dotlxst-no-bus-' + [Guid]::NewGuid().ToString('N')
                }

            $deadline = [DateTime]::UtcNow.AddSeconds(10)
            do {
                Start-Sleep -Milliseconds 100
                $probe = Invoke-PhoneCommand 'pactl' @('-s', $pulseAddress, 'info')
            } while ($probe.Code -ne 0 -and [DateTime]::UtcNow -lt $deadline)
            $probe.Code | Should -Be 0
            $port = Get-PhonePort
            $harness = Join-Path (Join-Path (Join-Path $script:phoneRoot 'tests') 'Interop') 'live_lxst_node.py'
            $referenceRoots = @($env:DOTRETICULUM_LXST_UPSTREAM, $env:DOTRETICULUM_UPSTREAM)
            if ($env:DOTRETICULUM_LXMF_UPSTREAM) { $referenceRoots += $env:DOTRETICULUM_LXMF_UPSTREAM }
            $python = Start-PhoneProcess $env:DOTRETICULUM_PYTHON @($harness, '--config', $pythonConfig,
                '--tcp-server-port', "$port", '--telephone') @{
                    PYTHONPATH = $referenceRoots -join [IO.Path]::PathSeparator
                    PYTHONIOENCODING = 'utf-8'
                    PULSE_SERVER = $pulseAddress
                    DOTLXST_VIRTUAL_PULSE = '1'
                }
            $ready = (Wait-PhoneLine $python '^@@.*"event": "ready"').Substring(2) | ConvertFrom-Json
            @'
[telephone]
speaker = lxst_output
microphone = lxst_input
allowed_callers = all
[phonebook]
[hardware]
'@ | Set-Content (Join-Path $phoneConfig 'config')
            @"
[reticulum]
share_instance = no
enable_transport = no
[logging]
loglevel = 4
[interfaces]
[[Python]]
type = TCPClientInterface
enabled = yes
target_host = 127.0.0.1
target_port = $port
"@ | Set-Content (Join-Path $rnsConfig 'config')
            $phoneArguments = @('--config', $phoneConfig, '--rnsconfig', $rnsConfig)
            $phone = Start-PhoneProcess $script:phoneBinary $phoneArguments @{ PULSE_SERVER = $pulseAddress }
            Wait-PhoneLine $phone 'Reticulum Telephone ready' | Out-Null
            $identityLine = Wait-PhoneLine $phone '^Identity hash:'
            $destinationLine = Wait-PhoneLine $phone '^Destination hash:'
            $destination = [regex]::Match($destinationLine, '[a-f0-9]{32}').Value
            Send-PhoneLine $python 'announce'
            Wait-PhoneLine $python '^@@.*"event": "announced"' | Out-Null

            Send-PhoneLine $phone $ready.identity_hash
            Wait-PhoneLine $phone '^State: Established' | Out-Null
            Wait-PhoneLine $python '^@@.*"event": "established"' | Out-Null
            Wait-PhoneLine $phone '^Audio receive ready' | Out-Null
            Wait-PhoneLine $phone '^Audio transmit ready' | Out-Null
            Send-PhoneLine $phone ''
            Wait-PhoneLine $phone '^State: Available' | Out-Null
            Wait-PhoneLine $python '^@@.*"event": "ended"' | Out-Null

            Send-PhoneLine $phone 'announce'
            Send-PhoneLine $python "call $destination"
            Wait-PhoneLine $phone '^State: Ringing' -Occurrence 2 | Out-Null
            Send-PhoneLine $phone ''
            Wait-PhoneLine $phone '^State: Established' -Occurrence 2 | Out-Null
            Wait-PhoneLine $python '^@@.*"event": "established"' -Occurrence 2 | Out-Null
            Send-PhoneLine $python 'hangup'
            Wait-PhoneLine $phone '^State: Available' -Occurrence 2 | Out-Null
            Send-PhoneLine $phone 'quit'
            $phone.Process.WaitForExit(10000) | Should -BeTrue
            $phone.Process.ExitCode | Should -Be 0
            Stop-PhoneProcess $phone
            $phone = $null

            $identityPath = Join-Path $phoneConfig 'identity'
            Test-Path $identityPath | Should -BeTrue
            [IO.File]::GetUnixFileMode($identityPath) | Should -Be ([IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)
            $phone = Start-PhoneProcess $script:phoneBinary $phoneArguments @{ PULSE_SERVER = $pulseAddress }
            Wait-PhoneLine $phone 'Reticulum Telephone ready' | Out-Null
            (Wait-PhoneLine $phone '^Identity hash:') | Should -Be $identityLine
            (Wait-PhoneLine $phone '^Destination hash:') | Should -Be $destinationLine
            Send-PhoneLine $phone 'quit'
            $phone.Process.WaitForExit(10000) | Should -BeTrue
            $phone.Process.ExitCode | Should -Be 0
        } finally {
            Stop-PhoneProcess $phone
            Stop-PhoneProcess $python -Graceful
            Stop-PhoneProcess $pulse
            if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force }
        }
    }
}

Describe 'Two native rnphone processes with isolated virtual audio' -Tag Integration, Native, NativeLocal, NativeStandalone -Skip:(
    !$IsLinux -or (!$env:DOTLXST_RNPHONE_BINARY -and !$env:DOTRETICULUM_RNPHONE_BINARY) -or $env:DOTLXST_VIRTUAL_AUDIO -ne '1') {
    It 'calls in both directions, captures and plays audio, switches profile, and shuts down without Python' {
        $root = Join-Path $script:phoneArtifacts ('rnphone-local-' + [Guid]::NewGuid().ToString('N'))
        $firstConfig = Join-Path $root 'first'
        $secondConfig = Join-Path $root 'second'
        $serverConfig = Join-Path $root 'server'
        $clientConfig = Join-Path $root 'client'
        New-Item -ItemType Directory -Force $firstConfig, $secondConfig, $serverConfig, $clientConfig | Out-Null
        [IO.File]::SetUnixFileMode($root, [IO.UnixFileMode]448)
        $pulse = $null
        $first = $null
        $second = $null
        try {
            $pulsePort = Get-PhonePort
            $pulseConfig = Join-Path $root 'pulse.pa'
            @"
load-module module-native-protocol-tcp listen=127.0.0.1 port=$pulsePort auth-anonymous=yes
load-module module-null-sink sink_name=lxst_output rate=48000 channels=2
load-module module-null-source source_name=lxst_input rate=48000 channels=2
set-default-sink lxst_output
set-default-source lxst_input
"@ | Set-Content $pulseConfig
            $pulseAddress = "tcp:127.0.0.1:$pulsePort"
            $pulse = Start-PhoneProcess 'pulseaudio' @('-n', "--file=$pulseConfig", '--daemonize=no',
                '--use-pid-file=no', '--exit-idle-time=-1', '--disable-shm=yes', '--log-target=stderr') @{
                    PULSE_RUNTIME_PATH = $root; PULSE_STATE_PATH = $root; XDG_RUNTIME_DIR = $root; PULSE_SERVER = ''
                    DBUS_SESSION_BUS_ADDRESS = 'unix:abstract=dotlxst-no-bus-' + [Guid]::NewGuid().ToString('N')
                }
            $deadline = [DateTime]::UtcNow.AddSeconds(10)
            do {
                Start-Sleep -Milliseconds 100
                $probe = Invoke-PhoneCommand 'pactl' @('-s', $pulseAddress, 'info')
            } while ($probe.Code -ne 0 -and [DateTime]::UtcNow -lt $deadline)
            $probe.Code | Should -Be 0
            foreach ($directory in @($firstConfig, $secondConfig)) {
                "[telephone]`nspeaker = lxst_output`nmicrophone = lxst_input`nallowed_callers = all`n" |
                    Set-Content (Join-Path $directory 'config')
            }
            $port = Get-PhonePort
            @"
[reticulum]
share_instance = no
[logging]
loglevel = 4
[interfaces]
[[Local server]]
type = TCPServerInterface
enabled = yes
listen_ip = 127.0.0.1
listen_port = $port
"@ | Set-Content (Join-Path $serverConfig 'config')
            @"
[reticulum]
share_instance = no
[logging]
loglevel = 4
[interfaces]
[[Local client]]
type = TCPClientInterface
enabled = yes
target_host = 127.0.0.1
target_port = $port
"@ | Set-Content (Join-Path $clientConfig 'config')
            $first = Start-PhoneProcess $script:phoneBinary @('--config', $firstConfig, '--rnsconfig', $serverConfig) @{ PULSE_SERVER = $pulseAddress }
            Wait-PhoneLine $first 'Reticulum Telephone ready' | Out-Null
            $firstIdentity = [regex]::Match((Wait-PhoneLine $first '^Identity hash:'), '[a-f0-9]{32}').Value
            $second = Start-PhoneProcess $script:phoneBinary @('--config', $secondConfig, '--rnsconfig', $clientConfig) @{ PULSE_SERVER = $pulseAddress }
            Wait-PhoneLine $second 'Reticulum Telephone ready' | Out-Null
            $secondIdentity = [regex]::Match((Wait-PhoneLine $second '^Identity hash:'), '[a-f0-9]{32}').Value
            Send-PhoneLine $first 'announce'
            Send-PhoneLine $second $firstIdentity
            Wait-PhoneLine $first '^State: Ringing' | Out-Null
            Send-PhoneLine $first ''
            foreach ($phone in @($first, $second)) {
                Wait-PhoneLine $phone '^State: Established' | Out-Null
                Wait-PhoneLine $phone '^Audio transmit ready' | Out-Null
                Wait-PhoneLine $phone '^Audio receive ready' | Out-Null
            }
            Send-PhoneLine $first 'profile'
            Wait-PhoneLine $first '^Profile: HighQuality' | Out-Null
            foreach ($phone in @($first, $second)) { Wait-PhoneLine $phone '^Audio transmit ready' -Occurrence 2 | Out-Null }
            Send-PhoneLine $second ''
            foreach ($phone in @($first, $second)) { Wait-PhoneLine $phone '^State: Available' | Out-Null }
            Send-PhoneLine $second 'announce'
            Send-PhoneLine $first $secondIdentity
            Wait-PhoneLine $second '^State: Ringing' -Occurrence 2 | Out-Null
            Send-PhoneLine $second ''
            foreach ($phone in @($first, $second)) { Wait-PhoneLine $phone '^State: Established' -Occurrence 2 | Out-Null }
            Send-PhoneLine $first ''
            foreach ($phone in @($first, $second)) {
                Wait-PhoneLine $phone '^State: Available' -Occurrence 2 | Out-Null
                Send-PhoneLine $phone 'quit'
                $phone.Process.WaitForExit(10000) | Should -BeTrue
                $phone.Process.ExitCode | Should -Be 0
            }
        } catch {
            $failure = $_
            foreach ($peer in @($first, $second, $pulse)) {
                if ($peer) {
                    for ($i = 0; $i -lt 100; $i++) { Read-PhoneProcess $peer }
                    Write-Host ("Native peer stdout:`n" + ($peer.Output -join "`n") + "`nstderr:`n" + ($peer.Error -join "`n"))
                }
            }
            throw $failure
        } finally {
            Stop-PhoneProcess $second
            Stop-PhoneProcess $first
            Stop-PhoneProcess $pulse
            if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force }
        }
    }
}
