Describe 'NomadNet bounded terminal utilities' -Tag 'Utilities' {
    BeforeAll {
        $enabled = $env:DOTRETICULUM_NOMADNET_UTILITIES -eq '1'
        if ($enabled) {
            if (-not $env:DOTRETICULUM_NOMADNET_BINARY) { throw 'DOTRETICULUM_NOMADNET_BINARY is required.' }
            $work = Join-Path (Get-Location) "artifacts\tests\nomad-utilities-native-$([Guid]::NewGuid().ToString('N'))"
            $null = New-Item $work -ItemType Directory -Force
            $processes = [Collections.Generic.List[Diagnostics.Process]]::new()
            $pending = @{}
            $logs = @{}
            $errors = @{}
            function Start-UtilityProcess([string] $name, [string[]] $arguments) {
                $info = [Diagnostics.ProcessStartInfo]::new($env:DOTRETICULUM_NOMADNET_BINARY)
                $info.WorkingDirectory = $work
                $info.UseShellExecute = $false
                $info.RedirectStandardInput = $true
                $info.RedirectStandardOutput = $true
                $info.RedirectStandardError = $true
                $info.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
                $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
                foreach ($argument in $arguments) { $info.ArgumentList.Add($argument) }
                $process = [Diagnostics.Process]::new()
                $process.StartInfo = $info
                $null = $process.Start()
                $processes.Add($process)
                $logs[$process.Id] = @{ Name=$name; Text=[Text.StringBuilder]::new() }
                $errors[$process.Id] = $process.StandardError.ReadToEndAsync()
                return $process
            }
            function Send-Utility($process, [string] $command) {
                $process.StandardInput.WriteLine($command)
                $process.StandardInput.Flush()
            }
            function Read-Utility($process, [string] $pattern, [int] $seconds = 45) {
                $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
                while ([DateTime]::UtcNow -lt $deadline) {
                    if (-not $pending.ContainsKey($process.Id)) { $pending[$process.Id] = $process.StandardOutput.ReadLineAsync() }
                    try { $line = $pending[$process.Id].WaitAsync([TimeSpan]::FromMilliseconds(200)).GetAwaiter().GetResult() }
                    catch [TimeoutException] { continue }
                    $pending.Remove($process.Id)
                    if ($null -eq $line) { throw "Closed stdout before $pattern; diagnostics: $work" }
                    $null = $logs[$process.Id].Text.AppendLine($line)
                    if ($line -match $pattern) { return $line }
                }
                throw "Timed out before $pattern; diagnostics: $work"
            }
            function Stop-Utility($process) {
                Send-Utility $process 'quit'
                $process.WaitForExit(30000) | Should -BeTrue
                $process.ExitCode | Should -Be 0
                if ($pending.ContainsKey($process.Id)) {
                    $null = $logs[$process.Id].Text.AppendLine($pending[$process.Id].GetAwaiter().GetResult())
                    $pending.Remove($process.Id)
                }
                $null = $logs[$process.Id].Text.Append($process.StandardOutput.ReadToEnd())
            }
            function New-UtilityRns([string] $name, [int] $port = 0, [bool] $client = $false) {
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
            function Start-UtilityNative([string] $name, [string] $profile, [string] $rns) {
                $process = Start-UtilityProcess $name @('--config', $profile, '--rnsconfig', $rns)
                Send-Utility $process 'utility identity'
                $line = Read-Utility $process 'Speed test: [0-9a-f]{32}'
                $address = [regex]::Match($line, 'Speed test: ([0-9a-f]{32})').Groups[1].Value
                return @{ Process=$process; Address=$address }
            }
            function Read-UtilityJson($process) {
                $first = (Read-Utility $process '(^|> )[\{\[]').Trim()
                $first = $first -replace '^.*?>\s*', ''
                $lines = [Collections.Generic.List[string]]::new()
                $lines.Add($first)
                if ($first -notmatch '^[\[{]\s*[\]}]$') {
                    do {
                        $line = Read-Utility $process '.'
                        $lines.Add($line)
                    } while ($line -notmatch '^[\]}]$')
                }
                return ConvertFrom-Json -InputObject ($lines -join "`n") -NoEnumerate -ErrorAction Stop
            }
        }
    }

    It 'T359 regenerates the exact primary utility-controller and transport vectors' {
        if (-not $enabled) { Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_UTILITIES=1 with selected binary and pinned primary environment.'; return }
        foreach ($name in 'DOTRETICULUM_PYTHON', 'DOTRETICULUM_UPSTREAM', 'DOTRETICULUM_LXMF_UPSTREAM',
            'DOTRETICULUM_NOMADNET_SOURCE') {
            if (-not [Environment]::GetEnvironmentVariable($name)) { throw "$name is required." }
        }
        $repository = if ($env:DOTRETICULUM_TEST_REPOSITORY) { $env:DOTRETICULUM_TEST_REPOSITORY }
            else { Split-Path (Split-Path $PSScriptRoot -Parent) -Parent }
        $env:PYTHONPATH = @($env:DOTRETICULUM_UPSTREAM, $env:DOTRETICULUM_LXMF_UPSTREAM,
            $env:DOTRETICULUM_NOMADNET_SOURCE) -join [IO.Path]::PathSeparator
        $fixture = Join-Path $work 'vectors.json'
        & $env:DOTRETICULUM_PYTHON (Join-Path $repository 'tests\Interop\GenerateNomadNetUtilityVectors.py') `
            --output $fixture --observations (Join-Path $work 'imports.json')
        $LASTEXITCODE | Should -Be 0
        (Get-FileHash $fixture).Hash | Should -Be (Get-FileHash (
            Join-Path $repository 'tests\NomadNet.Tests\NomadNetUtilityVectors.json')).Hash
    }

    It 'T360 inspects actual owner state and joins cancellation without enabling a listener across restart' {
        if (-not $enabled) { Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_UTILITIES=1 with selected native binary.'; return }
        $profile = Join-Path $work 'offline-profile'
        $rns = New-UtilityRns 'offline-rns'
        $native = Start-UtilityNative 'offline' $profile $rns
        $process = $native.Process
        Send-Utility $process 'utility help'
        $null = Read-Utility $process 'Utilities never enable a listener'
        foreach ($table in 'paths', 'rates', 'interfaces') {
            Send-Utility $process "utility $table"
            $null = Read-UtilityJson $process
        }
        Send-Utility $process 'utility drop 00000000000000000000000000000000'
        $null = Read-Utility $process 'No path to expire'
        Send-Utility $process 'utility speed status'
        $null = Read-Utility $process 'Speed-test listening=False'
        Send-Utility $process 'utility speed send 00000000000000000000000000000000 16 MB'
        $null = Read-Utility $process 'Utility operation started'
        Send-Utility $process 'utility cancel'
        $null = Read-Utility $process 'Utility operation joined'
        Send-Utility $process 'utility status'
        $null = Read-Utility $process 'Utility idle'
        Stop-Utility $process
        $errors[$process.Id].GetAwaiter().GetResult() | Should -Match 'cancelled or timed out'
        @(Get-ChildItem (Join-Path $profile 'storage\utility-work') -Force).Count | Should -Be 0
        $restart = Start-UtilityNative 'offline-restart' $profile $rns
        $restart.Address | Should -Be $native.Address
        Send-Utility $restart.Process 'utility speed status'
        $null = Read-Utility $restart.Process 'Speed-test listening=False'
        Stop-Utility $restart.Process
    }

    It 'T361 exchanges actual segmented speed resources in both native origins and recovers after refusal' {
        if (-not $enabled) { Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_UTILITIES=1 with selected native binary.'; return }
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $port = $listener.LocalEndpoint.Port
        $listener.Stop()
        $aliceProfile = Join-Path $work 'alice'
        $bobProfile = Join-Path $work 'bob'
        $aliceRns = New-UtilityRns 'alice-rns' $port
        $bobRns = New-UtilityRns 'bob-rns' $port $true
        $alice = Start-UtilityNative 'alice' $aliceProfile $aliceRns
        $bob = Start-UtilityNative 'bob' $bobProfile $bobRns
        Send-Utility $alice.Process "utility speed listen $($bob.Address)"
        $null = Read-Utility $alice.Process 'Speed-test listener:'
        Send-Utility $bob.Process 'utility speed listen 00000000000000000000000000000000'
        $null = Read-Utility $bob.Process 'Speed-test listener:'
        Send-Utility $alice.Process "utility speed send $($bob.Address) 2 KB"
        $null = Read-Utility $alice.Process 'Utility operation started'
        Send-Utility $alice.Process 'utility wait'
        Send-Utility $alice.Process 'utility status'
        $null = Read-Utility $alice.Process 'Utility idle'
        Send-Utility $bob.Process 'utility speed off'
        $null = Read-Utility $bob.Process 'Speed-test listener stopped'
        Send-Utility $bob.Process "utility speed listen $($alice.Address)"
        $null = Read-Utility $bob.Process 'Speed-test listener:'
        foreach ($pair in @(@($alice, $bob), @($bob, $alice))) {
            $sender, $receiver = $pair
            Send-Utility $sender.Process "utility speed send $($receiver.Address) 4 MB"
            $upload = Read-Utility $sender.Process 'Upload: bytes=4194304 .*sha256=[0-9a-f]{64}'
            $hash = [regex]::Match($upload, 'sha256=([0-9a-f]{64})').Groups[1].Value
            $download = $null
            $deadline = [DateTime]::UtcNow.AddSeconds(15)
            do {
                Send-Utility $receiver.Process 'utility speed status'
                $null = Read-Utility $receiver.Process 'Speed-test listening=True'
                Send-Utility $receiver.Process 'utility identity'
                $null = Read-Utility $receiver.Process 'Speed test:'
                if ($logs[$receiver.Process.Id].Text.ToString() -match "Download: bytes=4194304 .*sha256=$hash") {
                    $download = $Matches[0]
                }
            } until ($download -or [DateTime]::UtcNow -gt $deadline)
            $download | Should -Not -BeNullOrEmpty
        }
        foreach ($peer in $alice, $bob) {
            Send-Utility $peer.Process 'utility speed off'
            $null = Read-Utility $peer.Process 'Speed-test listener stopped'
            Stop-Utility $peer.Process
        }
        $errors[$alice.Process.Id].GetAwaiter().GetResult() | Should -Match 'Utility failed:'
        $errors[$bob.Process.Id].GetAwaiter().GetResult() | Should -Match 'unauthorized peer'
        foreach ($profile in $aliceProfile, $bobProfile) {
            $entries = @(Get-ChildItem (Join-Path $profile 'storage\utility-work') -Force)
            $entries.Count | Should -Be 1
            $entries[0].Name | Should -Be '.dotreticulum-resource-storage'
            (Get-Content $entries[0].FullName -Raw) | Should -Be "DotReticulum resource storage v1`n"
        }
    }

    AfterAll {
        if ($enabled) {
            foreach ($process in $processes) {
                if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
                $record = $logs[$process.Id]
                $record.Text.ToString() | Set-Content (Join-Path $work ($record.Name + '.stdout.log')) -Encoding utf8NoBOM
                $errors[$process.Id].GetAwaiter().GetResult() |
                    Set-Content (Join-Path $work ($record.Name + '.stderr.log')) -Encoding utf8NoBOM
                $process.Dispose()
            }
        }
    }
}
