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

    function Wait-PhoneSinkUsage {
        param([string] $Server, [string] $Sink, [bool] $Expected = $true)
        $sinks = Invoke-PhoneCommand 'pactl' @('-s', $Server, 'list', 'short', 'sinks')
        $index = [regex]::Match($sinks.Output, '(?m)^(\d+)\s+' + [regex]::Escape($Sink) + '\s+').Groups[1].Value
        $index | Should -Not -BeNullOrEmpty
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        do {
            $inputs = Invoke-PhoneCommand 'pactl' @('-s', $Server, 'list', 'short', 'sink-inputs')
            $used = $inputs.Output -match ('(?m)^\d+\s+' + $index + '\s+')
            if ($used -eq $Expected) { return }
            Start-Sleep -Milliseconds 50
        } while ([DateTime]::UtcNow -lt $deadline)
        throw "Pulse sink $Sink usage expected $Expected; actual sink inputs: $($inputs.Output)"
    }
}

Describe 'Two Windows native rnphone processes without audio hardware' -Tag Integration, NativeWindows -Skip:(
    !$IsWindows -or $env:DOTLXST_WINDOWS_AUDIO -ne '1' -or !$env:DOTLXST_RNPHONE_BINARY) {
    It 'exchanges real Opus profiles in both directions with synthetic silence and retains identity across restart' {
        $root = Join-Path $script:phoneArtifacts ('rnphone-win-headless-' + [Guid]::NewGuid().ToString('N'))
        $firstConfig = Join-Path $root 'first'
        $secondConfig = Join-Path $root 'second'
        $serverConfig = Join-Path $root 'server'
        $clientConfig = Join-Path $root 'client'
        New-Item -ItemType Directory -Force $firstConfig, $secondConfig, $serverConfig, $clientConfig | Out-Null
        $first = $null
        $second = $null
        try {
            $port = Get-PhonePort
            "[reticulum]`nshare_instance = no`n[logging]`nloglevel = 4`n[interfaces]`n[[Server]]`ntype = TCPServerInterface`nenabled = yes`nlisten_ip = 127.0.0.1`nlisten_port = $port`n" |
                Set-Content (Join-Path $serverConfig 'config')
            "[reticulum]`nshare_instance = no`n[logging]`nloglevel = 4`n[interfaces]`n[[Client]]`ntype = TCPClientInterface`nenabled = yes`ntarget_host = 127.0.0.1`ntarget_port = $port`n" |
                Set-Content (Join-Path $clientConfig 'config')
            $firstArgs = @('--headless-audio', '--config', $firstConfig, '--rnsconfig', $serverConfig)
            $secondArgs = @('--headless-audio', '--config', $secondConfig, '--rnsconfig', $clientConfig)
            $first = Start-PhoneProcess $script:phoneBinary $firstArgs
            $second = Start-PhoneProcess $script:phoneBinary $secondArgs
            foreach ($phone in @($first, $second)) {
                Wait-PhoneLine $phone 'Headless audio: synthetic silence' | Out-Null
                Wait-PhoneLine $phone 'Reticulum Telephone ready' | Out-Null
            }
            $firstIdentity = [regex]::Match((Wait-PhoneLine $first '^Identity hash:'), '[a-f0-9]{32}').Value
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
            foreach ($phone in @($first, $second)) { Wait-PhoneLine $phone '^State: Available' -Occurrence 2 | Out-Null }
            Send-PhoneLine $second 'quit'
            $second.Process.WaitForExit(10000) | Should -BeTrue
            $second.Process.ExitCode | Should -Be 0
            Stop-PhoneProcess $second
            $second = Start-PhoneProcess $script:phoneBinary $secondArgs
            Wait-PhoneLine $second 'Reticulum Telephone ready' | Out-Null
            (Wait-PhoneLine $second '^Identity hash:') | Should -Match $secondIdentity
            foreach ($phone in @($first, $second)) {
                Send-PhoneLine $phone 'quit'
                $phone.Process.WaitForExit(10000) | Should -BeTrue
                $phone.Process.ExitCode | Should -Be 0
            }
        } finally {
            Stop-PhoneProcess $second
            Stop-PhoneProcess $first
            Remove-Item -LiteralPath $root -Recurse -Force
        }
    }
}

Describe 'Published rnphone CLI' -Tag Integration, Native -Skip:(!$env:DOTLXST_RNPHONE_BINARY -and !$env:DOTRETICULUM_RNPHONE_BINARY) {
    It 'validates options and prints upstream-named CLI entry points without opening audio' {
        $help = Invoke-PhoneCommand $script:phoneBinary @('--help')
        $help.Code | Should -Be 0
        $help.Output | Should -Match '--list-devices'
        $help.Output | Should -Match 'require accessible kernel devices'
        $help.Output | Should -Match 'not WASAPI parity'
        (Invoke-PhoneCommand $script:phoneBinary @('--version')).Output | Should -Match 'rnphone 0\.4\.5'
        (Invoke-PhoneCommand $script:phoneBinary @('--systemd')).Output | Should -Match 'ExecStart=.*rnphone --service'
        (Invoke-PhoneCommand $script:phoneBinary @('--invalid-option')).Code | Should -Be 2
    }

    It 'rejects unsupported hardware configuration rather than pretending it was applied' {
        $directory = Join-Path $script:phoneArtifacts ('rnphone-bad-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force $directory | Out-Null
        try {
            "[hardware]`nkeypad = unknown_driver`n" | Set-Content (Join-Path $directory 'config')
            $result = Invoke-PhoneCommand $script:phoneBinary @('--config', $directory)
            $result.Code | Should -Be 1
            $result.Error | Should -Match 'Unknown keypad driver'
            Test-Path (Join-Path $directory 'identity') | Should -BeFalse
        } finally { Remove-Item -LiteralPath $directory -Recurse -Force }
    }

    It 'reports a configured Linux hardware provider failure before creating an identity' -Skip:(!$IsLinux) {
        $directory = Join-Path $script:phoneArtifacts ('rnphone-no-gpio-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force $directory | Out-Null
        $previous = $env:DOTLXST_GPIOCHIP
        $previousI2c = $env:DOTLXST_I2C_DEVICE
        try {
            $env:DOTLXST_GPIOCHIP = '/dev/dotlxst-device-does-not-exist'
            "[hardware]`nkeypad = gpio_4x4`nkeypad_hook_pin = 5`n" | Set-Content (Join-Path $directory 'config')
            $result = Invoke-PhoneCommand $script:phoneBinary @('--config', $directory)
            $result.Code | Should -Be 1
            $result.Error | Should -Match 'Opening hardware device.*errno'
            Test-Path (Join-Path $directory 'identity') | Should -BeFalse
            $env:DOTLXST_I2C_DEVICE = '/dev/dotlxst-device-does-not-exist'
            "[hardware]`ndisplay = i2c_lcd1602`n" | Set-Content (Join-Path $directory 'config')
            $result = Invoke-PhoneCommand $script:phoneBinary @('--config', $directory)
            $result.Code | Should -Be 1
            $result.Error | Should -Match 'Opening hardware device.*errno'
            Test-Path (Join-Path $directory 'identity') | Should -BeFalse
        } finally {
            $env:DOTLXST_GPIOCHIP = $previous
            $env:DOTLXST_I2C_DEVICE = $previousI2c
            Remove-Item -LiteralPath $directory -Recurse -Force
        }
    }
}

Describe 'Native rnphone configuration and phonebook persistence' -Tag Integration, Native, NativeUtility -Skip:(
    !$IsLinux -or (!$env:DOTLXST_RNPHONE_BINARY -and !$env:DOTRETICULUM_RNPHONE_BINARY)) {
    It 'runs service mode without reading commands and handles SIGTERM cleanly before restart' {
        $root = Join-Path $script:phoneArtifacts ('rnphone-service-' + [Guid]::NewGuid().ToString('N'))
        $phoneConfig = Join-Path $root 'phone'
        $rnsConfig = Join-Path $root 'rns'
        New-Item -ItemType Directory -Force $phoneConfig, $rnsConfig | Out-Null
        $phone = $null
        try {
            "[reticulum]`nshare_instance = no`n[logging]`nloglevel = 4`n[interfaces]`n" |
                Set-Content (Join-Path $rnsConfig 'config')
            $arguments = @('--service', '--config', $phoneConfig, '--rnsconfig', $rnsConfig)
            $phone = Start-PhoneProcess $script:phoneBinary $arguments
            Wait-PhoneLine $phone 'Reticulum Telephone ready' | Out-Null
            $identity = Wait-PhoneLine $phone '^Identity hash:'
            Send-PhoneLine $phone 'quit'
            $phone.Process.StandardInput.Close()
            $phone.Process.WaitForExit(200) | Should -BeFalse
            (Invoke-PhoneCommand '/usr/bin/kill' @('-TERM', "$($phone.Process.Id)")).Code | Should -Be 0
            $phone.Process.WaitForExit(10000) | Should -BeTrue
            $phone.Process.ExitCode | Should -Be 0
            Stop-PhoneProcess $phone
            $phone = Start-PhoneProcess $script:phoneBinary $arguments
            Wait-PhoneLine $phone 'Reticulum Telephone ready' | Out-Null
            (Wait-PhoneLine $phone '^Identity hash:') | Should -BeExactly $identity
            (Invoke-PhoneCommand '/usr/bin/kill' @('-TERM', "$($phone.Process.Id)")).Code | Should -Be 0
            $phone.Process.WaitForExit(10000) | Should -BeTrue
            $phone.Process.ExitCode | Should -Be 0
        } finally {
            Stop-PhoneProcess $phone
            if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force }
        }
    }

    It 'matches executed primary phonebook aliases, reloads atomically and persists identity/config across restart' {
        $root = Join-Path $script:phoneArtifacts ('rnphone-config-' + [Guid]::NewGuid().ToString('N'))
        $phoneConfig = Join-Path $root 'phone'
        $rnsConfig = Join-Path $root 'rns'
        New-Item -ItemType Directory -Force $phoneConfig, $rnsConfig | Out-Null
        $phone = $null
        try {
            $fixturePath = Join-Path $script:phoneRoot 'tests/DotLXST.Tests/Fixtures/rnphone-config-vectors.json'
            $fixture = Get-Content -LiteralPath $fixturePath -Raw | ConvertFrom-Json
            $path = Join-Path $phoneConfig 'config'
            $fixture.config | Set-Content -LiteralPath $path
            "[reticulum]`nshare_instance = no`n[logging]`nloglevel = 4`n[interfaces]`n" |
                Set-Content (Join-Path $rnsConfig 'config')
            $phone = Start-PhoneProcess $script:phoneBinary @('--config', $phoneConfig, '--rnsconfig', $rnsConfig)
            Wait-PhoneLine $phone 'Reticulum Telephone ready' | Out-Null
            $identity = Wait-PhoneLine $phone '^Identity hash:'
            $localHash = [regex]::Match($identity, '[a-f0-9]{32}').Value
            $configuration = $fixture.config.Replace('00000000000000000000000000000000', $localHash)
            $configuration | Set-Content -LiteralPath $path
            Send-PhoneLine $phone 'reload'
            Wait-PhoneLine $phone '^Configuration reloaded' | Out-Null
            Send-PhoneLine $phone 'phonebook'
            foreach ($entry in $fixture.phonebook.psobject.Properties) {
                $alias = $fixture.aliases.($entry.Value)
                Wait-PhoneLine $phone ("^$alias " + [regex]::Escape($entry.Name) + ': <' + $entry.Value + '>') | Out-Null
            }
            Send-PhoneLine $phone 'q'
            Wait-PhoneLine $phone '^Phonebook closed' | Out-Null
            ($phone.Output -join "`n") | Should -Not -Match '(?m)^\d+ Self:'
            $phone.Process.HasExited | Should -BeFalse
            $configuration.Replace('Alice =', 'Updated =') | Set-Content -LiteralPath $path
            Send-PhoneLine $phone 'reload'
            Wait-PhoneLine $phone '^Configuration reloaded' -Occurrence 2 | Out-Null
            Send-PhoneLine $phone 'p'
            Wait-PhoneLine $phone '^12 Updated:' | Out-Null
            Send-PhoneLine $phone 'b'
            Wait-PhoneLine $phone '^Phonebook closed' -Occurrence 2 | Out-Null
            "[telephone]`nunknown = invalid`n" | Set-Content -LiteralPath $path
            Send-PhoneLine $phone 'reload'
            $deadline = [DateTime]::UtcNow.AddSeconds(5)
            do { Read-PhoneProcess $phone; Start-Sleep -Milliseconds 20 } while (
                !($phone.Error -match 'Unsupported rnphone setting') -and [DateTime]::UtcNow -lt $deadline)
            ($phone.Error -join "`n") | Should -Match 'Unsupported rnphone setting'
            $phone.Process.HasExited | Should -BeFalse
            Send-PhoneLine $phone 'p'
            Wait-PhoneLine $phone '^12 Updated:' -Occurrence 2 | Out-Null
            Send-PhoneLine $phone 'b'
            Wait-PhoneLine $phone '^Phonebook closed' -Occurrence 3 | Out-Null
            $configuration.Replace('Alice =', 'Updated =') | Set-Content -LiteralPath $path
            Send-PhoneLine $phone 'quit'
            $phone.Process.WaitForExit(10000) | Should -BeTrue
            $phone.Process.ExitCode | Should -Be 0
            Stop-PhoneProcess $phone
            $phone = Start-PhoneProcess $script:phoneBinary @('--config', $phoneConfig, '--rnsconfig', $rnsConfig)
            Wait-PhoneLine $phone 'Reticulum Telephone ready' | Out-Null
            (Wait-PhoneLine $phone '^Identity hash:') | Should -BeExactly $identity
            Send-PhoneLine $phone 'p'
            Wait-PhoneLine $phone '^12 Updated:' | Out-Null
            Send-PhoneLine $phone 'q'
            Wait-PhoneLine $phone '^Phonebook closed' | Out-Null
            $single = Get-Content -Raw (Join-Path $script:phoneRoot 'tests/DotLXST.Tests/Fixtures/rnphone-config-single-list.json') | ConvertFrom-Json
            $single.config | Set-Content -LiteralPath $path
            Send-PhoneLine $phone 'reload'
            Wait-PhoneLine $phone '^Configuration reloaded' | Out-Null
            Send-PhoneLine $phone 'p'
            foreach ($entry in $single.phonebook.psobject.Properties) {
                $alias = $single.aliases.($entry.Value)
                Wait-PhoneLine $phone ("^$alias " + [regex]::Escape($entry.Name) + ': <' + $entry.Value + '>') | Out-Null
            }
            Send-PhoneLine $phone 'q'
            Wait-PhoneLine $phone '^Phonebook closed' -Occurrence 2 | Out-Null
            ($phone.Output -join "`n") | Should -Not -Match '(?m)^\d+ Wrong(Length|Hex):'
            Send-PhoneLine $phone 'q'
            $phone.Process.WaitForExit(10000) | Should -BeTrue
            $phone.Process.ExitCode | Should -Be 0
        } finally {
            Stop-PhoneProcess $phone
            if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force }
        }
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
load-module module-null-sink sink_name=lxst_ringer rate=48000 channels=2
load-module module-null-sink sink_name=lxst_alternate rate=48000 channels=2
load-module module-null-source source_name=lxst_alternate_input rate=48000 channels=2
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
    It 'calls in both directions, routes ringback/ringtone/busy audio, switches devices/profile, and shuts down without Python' {
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
load-module module-null-sink sink_name=lxst_ringer rate=48000 channels=2
load-module module-null-sink sink_name=lxst_alternate rate=48000 channels=2
load-module module-null-sink sink_name=lxst_busy rate=48000 channels=2
load-module module-null-source source_name=lxst_alternate_input rate=48000 channels=2
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
            $listing = Invoke-PhoneCommand $script:phoneBinary @('--list-devices') @{ PULSE_SERVER = $pulseAddress }
            $listing.Code | Should -Be 0
            $listing.Output | Should -Match 'Output : .*\[lxst_output\]'
            $listing.Output | Should -Match 'Input : .*\[lxst_input\]'
            foreach ($directory in @($firstConfig, $secondConfig)) {
                "[telephone]`nspeaker = lxst_output`nmicrophone = lxst_input`nallowed_callers = all`nringer = lxst_ringer`nringtone = ringtone.opus`n" |
                    Set-Content (Join-Path $directory 'config')
                $fixture = Get-Content -Raw (Join-Path $script:phoneRoot 'tests/DotLXST.Tests/Fixtures/lxst-file-vectors.json') | ConvertFrom-Json
                [IO.File]::WriteAllBytes((Join-Path $directory 'ringtone.opus'), [Convert]::FromBase64String($fixture.file_base64))
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
            Wait-PhoneSinkUsage $pulseAddress 'lxst_ringer'
            Wait-PhoneSinkUsage $pulseAddress 'lxst_output'
            Send-PhoneLine $first ''
            foreach ($phone in @($first, $second)) {
                Wait-PhoneLine $phone '^State: Established' | Out-Null
                Wait-PhoneLine $phone '^Audio transmit ready' | Out-Null
                Wait-PhoneLine $phone '^Audio receive ready' | Out-Null
            }
            Wait-PhoneSinkUsage $pulseAddress 'lxst_ringer' $false
            $firstPath = Join-Path $firstConfig 'config'
            (Get-Content -Raw $firstPath).Replace('speaker = lxst_output', 'speaker = lxst_alternate').
                Replace('microphone = lxst_input', 'microphone = lxst_alternate_input') | Set-Content $firstPath
            Send-PhoneLine $first 'reload'
            Wait-PhoneLine $first '^Configuration reloaded' | Out-Null
            Wait-PhoneSinkUsage $pulseAddress 'lxst_alternate'
            Wait-PhoneLine $first '^Audio transmit ready' -Occurrence 2 | Out-Null
            Send-PhoneLine $first 'profile'
            Wait-PhoneLine $first '^Profile: HighQuality' | Out-Null
            Wait-PhoneLine $first '^Audio transmit ready' -Occurrence 3 | Out-Null
            Wait-PhoneLine $second '^Audio transmit ready' -Occurrence 2 | Out-Null
            Send-PhoneLine $second ''
            foreach ($phone in @($first, $second)) { Wait-PhoneLine $phone '^State: Available' | Out-Null }
            Send-PhoneLine $second 'announce'
            Send-PhoneLine $first $secondIdentity
            Wait-PhoneLine $second '^State: Ringing' -Occurrence 2 | Out-Null
            Send-PhoneLine $second ''
            foreach ($phone in @($first, $second)) { Wait-PhoneLine $phone '^State: Established' -Occurrence 2 | Out-Null }
            Send-PhoneLine $first ''
            foreach ($phone in @($first, $second)) { Wait-PhoneLine $phone '^State: Available' -Occurrence 2 | Out-Null }
            (Get-Content -Raw $firstPath).Replace('allowed_callers = all', 'allowed_callers = none') | Set-Content $firstPath
            $secondPath = Join-Path $secondConfig 'config'
            (Get-Content -Raw $secondPath).Replace('speaker = lxst_output', 'speaker = lxst_busy') | Set-Content $secondPath
            Send-PhoneLine $first 'reload'
            Send-PhoneLine $second 'reload'
            Wait-PhoneLine $first '^Configuration reloaded' -Occurrence 2 | Out-Null
            Wait-PhoneLine $second '^Configuration reloaded' | Out-Null
            Send-PhoneLine $second $firstIdentity
            Wait-PhoneLine $second '^State: Busy' | Out-Null
            Wait-PhoneLine $second '^State: Available' -Occurrence 3 | Out-Null
            Wait-PhoneSinkUsage $pulseAddress 'lxst_busy'
            foreach ($phone in @($first, $second)) {
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
