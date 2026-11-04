Describe 'NomadNet explicit bounded printing software' -Tag 'Printing' {
    BeforeAll {
        $enabled = $env:DOTRETICULUM_NOMADNET_PRINTING -eq '1'
        if ($enabled) {
            $repository = if ($env:DOTRETICULUM_TEST_REPOSITORY) { $env:DOTRETICULUM_TEST_REPOSITORY }
                else { Split-Path (Split-Path $PSScriptRoot -Parent) -Parent }
            foreach ($name in 'DOTRETICULUM_NOMADNET_BINARY', 'DOTRETICULUM_PYTHON',
                'DOTRETICULUM_UPSTREAM', 'DOTRETICULUM_LXMF_UPSTREAM', 'DOTRETICULUM_NOMADNET_SOURCE') {
                if (-not [Environment]::GetEnvironmentVariable($name)) { throw "$name is required." }
            }
            $work = Join-Path (Get-Location) "artifacts\tests\nomad-printing\acceptance-$([Guid]::NewGuid().ToString('N'))"
            $null = New-Item $work -ItemType Directory -Force
            $processes = [Collections.Generic.List[Diagnostics.Process]]::new()
            $pending = @{}
            $logs = @{}
            $errors = @{}
            $events = @{}
            $results = [Collections.Generic.List[object]]::new()
            function Start-PrintProcess([string] $name, [string] $binary, [string[]] $arguments) {
                $info = [Diagnostics.ProcessStartInfo]::new($binary)
                $info.UseShellExecute = $false
                $info.RedirectStandardInput = $true
                $info.RedirectStandardOutput = $true
                $info.RedirectStandardError = $true
                $info.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
                $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
                $info.WorkingDirectory = $work
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
                $events[$p.Id] = [Collections.Generic.List[object]]::new()
                return $p
            }
            function Send-Print($p, [string] $command) {
                $p.StandardInput.WriteLine($command)
                $p.StandardInput.Flush()
            }
            function Read-Print($p, [string] $pattern, [int] $seconds = 45) {
                $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
                while ([DateTime]::UtcNow -lt $deadline) {
                    if (-not $pending.ContainsKey($p.Id)) { $pending[$p.Id] = $p.StandardOutput.ReadLineAsync() }
                    try { $line = $pending[$p.Id].WaitAsync([TimeSpan]::FromMilliseconds(100)).GetAwaiter().GetResult() }
                    catch [TimeoutException] { continue }
                    $pending.Remove($p.Id)
                    if ($null -eq $line) { throw "$($logs[$p.Id].Name) closed stdout before $pattern; evidence: $work" }
                    $null = $logs[$p.Id].Text.AppendLine($line)
                    if ($line -match '^@@') {
                        $value = $line.Substring(2) | ConvertFrom-Json
                        if ($value.event -eq 'error') { throw "$line; evidence: $work" }
                        if ($events[$p.Id].Count -ge 128) { throw 'Printing reference event backlog exceeds bound.' }
                        $events[$p.Id].Add($value)
                    }
                    if ($line -match $pattern) { return $line }
                }
                throw "$($logs[$p.Id].Name) timed out before $pattern; evidence: $work"
            }
            function Read-PrintEvent($p, [string] $event) {
                while ($true) {
                    for ($i = 0; $i -lt $events[$p.Id].Count; $i++) {
                        if ($events[$p.Id][$i].event -eq $event) {
                            $result = $events[$p.Id][$i]
                            $events[$p.Id].RemoveAt($i)
                            return $result
                        }
                    }
                    $null = Read-Print $p '^@@'
                }
            }
            function Stop-Print($p) {
                Send-Print $p 'quit'
                $p.WaitForExit(30000) | Should -BeTrue
                $p.ExitCode | Should -Be 0 -Because "process diagnostics retained in $work"
                if ($pending.ContainsKey($p.Id)) {
                    $null = $logs[$p.Id].Text.AppendLine($pending[$p.Id].GetAwaiter().GetResult())
                    $pending.Remove($p.Id)
                }
                $null = $logs[$p.Id].Text.Append($p.StandardOutput.ReadToEnd())
            }
            function New-PrintRns([string] $name, [int] $port = 0, [bool] $client = $false) {
                $path = Join-Path $work $name
                $null = New-Item $path -ItemType Directory -Force
                $config = "[reticulum]`nshare_instance=no`nenable_transport=no`n[logging]`nloglevel=0`n[interfaces]`n"
                if ($port) {
                    $config += if ($client) { "[[Loopback]]`ntype=TCPClientInterface`nenabled=yes`ntarget_host=127.0.0.1`ntarget_port=$port`nannounce_cap=100`nbitrate=10000000`ningress_control=no`n" }
                        else { "[[Loopback]]`ntype=TCPServerInterface`nenabled=yes`nlisten_ip=127.0.0.1`nlisten_port=$port`nannounce_cap=100`nbitrate=10000000`ningress_control=no`n" }
                }
                [IO.File]::WriteAllText((Join-Path $path 'config'), $config)
                return $path
            }
            function Quote-Print([string] $text) { return '"' + $text.Replace('\', '\\').Replace('"', '\"') + '"' }
            $capture = Join-Path $work 'public captures'
            $marker = Join-Path $work 'printer-marker'
            $powershell = if ($env:DOTRETICULUM_PWSH) { $env:DOTRETICULUM_PWSH }
                elseif (Test-Path (Join-Path $PSHOME 'pwsh')) { Join-Path $PSHOME 'pwsh' }
                else { (Get-Process -Id $PID).Path }
            function Printer-Command([string] $mode = 'capture') {
                return @((Quote-Print $powershell), '-NoProfile -File',
                    (Quote-Print (Join-Path $repository 'tests\Pester\NomadNetPrinterStub.ps1')),
                    "-Mode $mode -Output", (Quote-Print $capture), '-Marker', (Quote-Print $marker),
                    '-Extra', (Quote-Print 'public ; $(no-shell)')) -join ' '
            }
            function Native-Printing([string] $command, [string] $pattern = 'Printing configuration saved') {
                Send-Print $runtime.Native $command
                $null = Read-Print $runtime.Native $pattern
            }
            function Print-Captures { return @(Get-ChildItem $capture -File -Filter '*.json' -ErrorAction SilentlyContinue) }
            function Wait-PrintCapture([int] $count) {
                $deadline = [DateTime]::UtcNow.AddSeconds(25)
                while ([DateTime]::UtcNow -lt $deadline) {
                    $found = @(Print-Captures)
                    if ($found.Count -ge $count) {
                        $cleanupDeadline = [DateTime]::UtcNow.AddSeconds(5)
                        while (@(Get-ChildItem (Join-Path $profile 'storage\print-jobs') -File).Count -and
                            [DateTime]::UtcNow -lt $cleanupDeadline) { Start-Sleep -Milliseconds 25 }
                        @(Get-ChildItem (Join-Path $profile 'storage\print-jobs') -File).Count | Should -Be 0
                        return $found
                    }
                    Start-Sleep -Milliseconds 50
                }
                throw "Print capture count did not reach $count; evidence: $work"
            }
            function Print-State { return Get-Content (Join-Path $profile 'state.json') -Raw | ConvertFrom-Json }
            function Primary-Message([string] $text) {
                Send-Print $primary "send $delivery $text"
                $queued = Read-PrintEvent $primary 'queued'
                $null = Read-PrintEvent $primary 'delivered'
                $deadline = [DateTime]::UtcNow.AddSeconds(15)
                do {
                    $message = @((Print-State).messages | Where-Object id -EQ $queued.hash)
                    if ($message.Count -eq 1) { return $message[0] }
                    Start-Sleep -Milliseconds 50
                } while ([DateTime]::UtcNow -lt $deadline)
                throw "Successfully delivered primary message not persisted; evidence: $work"
            }
            function No-NewPrint([int] $count) {
                Start-Sleep -Milliseconds 900
                @(Print-Captures).Count | Should -Be $count
            }
            function Start-PrintNative([string] $name) {
                $p = Start-PrintProcess $name $env:DOTRETICULUM_NOMADNET_BINARY @('--config', $profile, '--rnsconfig', $nativeRns)
                $line = Read-Print $p 'LXMF delivery address:'
                $match = [regex]::Match($line, '([0-9a-f]{32})')
                $match.Success | Should -BeTrue
                $script:delivery = $match.Groups[1].Value
                return $p
            }
            $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
            $listener.Start()
            $port = $listener.LocalEndpoint.Port
            $listener.Stop()
            $profile = Join-Path $work 'native profile'
            $nativeRns = New-PrintRns 'native-rns' $port
            $primaryRns = New-PrintRns 'primary-rns' $port $true
            $runtime = @{ Native=(Start-PrintNative 'native') }
            $primaryProfile = Join-Path $work 'primary'
            $null = New-Item $primaryProfile -ItemType Directory
            [IO.File]::WriteAllText((Join-Path $primaryProfile 'config'),
                "[client]`nuser_interface=none`nannounce_at_start=no`nperiodic_lxmf_sync=no`n[node]`nenable_node=no`n[logging]`nloglevel=0`ndestination=console`n")
            $primary = Start-PrintProcess 'actual-primary' $env:DOTRETICULUM_PYTHON @('-u',
                (Join-Path $repository 'tests\Interop\nomadnet_printing_peer.py'),
                '--config', $primaryProfile, '--rnsconfig', $primaryRns)
            $ready = Read-PrintEvent $primary 'ready'
            $ready.revision | Should -Be 'f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2'
            $peer = $ready.delivery
            Send-Print $primary 'announce'
            $null = Read-PrintEvent $primary 'announced'
            Send-Print $runtime.Native 'announce'
            $null = Read-Print $runtime.Native 'Destinations announced'
            Native-Printing ('printing command ' + (Printer-Command))
        }
    }

    It 'T330 regenerates canonical actual-primary filter and synthetic-format observations' {
        if (-not $enabled) { Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_PRINTING=1 with selected native binary and pinned primary environment; no physical printer is used.'; return }
        $fixture = Join-Path $work 'vectors.json'
        $p = Start-PrintProcess 'vectors' $env:DOTRETICULUM_PYTHON @('-u',
            (Join-Path $repository 'tests\Interop\GenerateNomadNetPrintingVectors.py'), '--output', $fixture,
            '--profile', (Join-Path $work 'vectors-primary'), '--rnsconfig', (Join-Path $work 'vectors-rns'))
        $stdout = $p.StandardOutput.ReadToEndAsync()
        $p.WaitForExit(90000) | Should -BeTrue
        $p.ExitCode | Should -Be 0 -Because $errors[$p.Id].GetAwaiter().GetResult()
        $null = $logs[$p.Id].Text.Append($stdout.GetAwaiter().GetResult())
        (Get-FileHash $fixture).Hash | Should -Be (Get-FileHash (Join-Path $repository 'tests\NomadNet.Tests\NomadNetPrintingVectors.json')).Hash
    }
    It 'T331 imports active custom-template printing configuration without creating missing foreign files' {
        if (-not $enabled) { Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_PRINTING=1.'; return }
        $config = Join-Path $work 'printing.ini'
        $missing = Join-Path $work 'never-created-template'
        [IO.File]::WriteAllText($config, "[printing]`nprint_messages=yes`nprint_from=trusted,$peer`nmessage_template=`"$missing`"`nprint_command='printer --flag'`n")
        $target = Join-Path $work 'imported'
        $p = Start-PrintProcess 'import' $env:DOTRETICULUM_NOMADNET_BINARY @('--import-config', $config, '--config', $target)
        $stdout = $p.StandardOutput.ReadToEndAsync()
        $p.WaitForExit(30000) | Should -BeTrue
        $p.ExitCode | Should -Be 0
        $null = $logs[$p.Id].Text.Append($stdout.GetAwaiter().GetResult())
        $saved = Get-Content (Join-Path $target 'settings.json') -Raw | ConvertFrom-Json
        $saved.printMessages | Should -BeTrue
        $saved.printTrustedMessages | Should -BeTrue
        $saved.allowedMessagePrintDestinations | Should -Contain $peer
        Test-Path $missing | Should -BeFalse
    }
    It 'T332 persists incoming primary messages without implicit printer use by default' {
        if (-not $enabled) { Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_PRINTING=1.'; return }
        $message = Primary-Message 'public-disabled-print'
        $message.content | Should -Be 'public-disabled-print'
        No-NewPrint 0
    }
    It 'T333 opted-in scalar everywhere prints a full-primary incoming message' {
        if (-not $enabled) { Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_PRINTING=1.'; return }
        Native-Printing 'printing from everywhere'
        Native-Printing 'printing on'
        $null = Primary-Message 'public-everywhere-print'
        $files = Wait-PrintCapture 1
        $receipt = Get-Content $files[0].FullName -Raw | ConvertFrom-Json
        Get-Content $receipt.copy -Raw | Should -Match 'public-everywhere-print'
        $receipt.extra | Should -BeExactly 'public ; $(no-shell)'
        Test-Path $receipt.original | Should -BeFalse
    }
    It 'T334 trusted policy denies discovered unknown senders' {
        if (-not $enabled) { Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_PRINTING=1.'; return }
        Native-Printing 'printing from trusted'
        $null = Primary-Message 'public-unknown-denied'
        No-NewPrint 1
    }
    It 'T335 persisted explicitly trusted contact permits incoming printing' {
        if (-not $enabled) { Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_PRINTING=1.'; return }
        Native-Printing "contact trust $peer trusted" 'Contact policy saved'
        $null = Primary-Message 'public-trusted-permitted'
        $null = Wait-PrintCapture 2
        Native-Printing "contact trust $peer unknown" 'Contact policy saved'
    }
    It 'T336 exact lowercase configured source permits an untrusted sender' {
        if (-not $enabled) { Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_PRINTING=1.'; return }
        Native-Printing "printing from $peer"
        $null = Primary-Message 'public-exact-source'
        $null = Wait-PrintCapture 3
    }
    It 'T337 uppercase configured source remains case-sensitive and denied' {
        if (-not $enabled) { Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_PRINTING=1.'; return }
        Native-Printing ('printing from ' + $peer.ToUpperInvariant())
        $null = Primary-Message 'public-uppercase-denied'
        No-NewPrint 3
    }
    It 'T338 list everywhere is inert while exact-source list entries allow' {
        if (-not $enabled) { Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_PRINTING=1.'; return }
        Native-Printing 'printing from everywhere,unused'
        $null = Primary-Message 'public-list-everywhere-denied'
        No-NewPrint 3
        Native-Printing "printing from TRUSTED,$peer"
        $null = Primary-Message 'public-list-source-permitted'
        $null = Wait-PrintCapture 4
    }
    It 'T339 manual stored-message print works while automatic printing is disabled' {
        if (-not $enabled) { Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_PRINTING=1.'; return }
        Native-Printing 'printing off'
        $message = Primary-Message 'public-manual-stored-print'
        Native-Printing "print $($message.id)" 'Message print completed'
        $null = Wait-PrintCapture 5
    }
    It 'T340 custom synthetic template is captured and persists without interpreted inserted content' {
        if (-not $enabled) { Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_PRINTING=1.'; return }
        $template = Join-Path $work 'public custom template.txt'
        [IO.File]::WriteAllText($template, 'PUBLIC|{origin}|{stime}|{rtime}|{mtitle}|{{literal}}|{mbody}')
        Native-Printing "printing template $template"
        Remove-Item $template
        $message = Primary-Message 'public-{origin}-body'
        Native-Printing "print $($message.id)" 'Message print completed'
        $null = Wait-PrintCapture 6
        $receipts = @(Print-Captures | ForEach-Object { Get-Content $_.FullName -Raw | ConvertFrom-Json })
        @($receipts | Where-Object { (Get-Content $_.copy -Raw) -match 'PUBLIC\|.*\|\{literal\}\|public-\{origin\}-body' }).Count | Should -Be 1
        (Get-Content (Join-Path $profile 'settings.json') -Raw | ConvertFrom-Json).printingMessageTemplate | Should -Match '^PUBLIC'
    }
    It 'T341 paper QR printing saves outbound history only after success and actual primary decrypts independent decode' {
        if (-not $enabled) { Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_PRINTING=1.'; return }
        Native-Printing "paper-print $peer public-paper-print-success" 'Paper print completed; conversation saved'
        $null = Wait-PrintCapture 7
        $png = @(Get-ChildItem $capture -File -Filter '*.png')
        $png.Count | Should -Be 1
        Send-Print $primary ('decode-paper ' + $png[0].FullName)
        $decoded = Read-PrintEvent $primary 'decoded'
        $decoded.accepted | Should -BeTrue
        $received = Read-PrintEvent $primary 'received'
        $received.content | Should -Be 'public-paper-print-success'
        $received.signature | Should -BeTrue
        $received.method | Should -Be 3 # Pinned ingest_lxm_uri routes paper through PROPAGATED delivery.
        @((Print-State).messages | Where-Object { $_.content -eq 'public-paper-print-success' -and $_.status -eq 'paper' }).Count | Should -Be 1
    }
    It 'T342 failed paper print reports nonzero subprocess status and creates no conversation entry' {
        if (-not $enabled) { Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_PRINTING=1.'; return }
        Native-Printing ('printing command ' + (Printer-Command 'fail'))
        Native-Printing "paper-print $peer public-paper-print-failed" 'Paper print failed; conversation not saved: Printer exited with code 7'
        @((Print-State).messages | Where-Object content -EQ 'public-paper-print-failed').Count | Should -Be 0
        @(Get-ChildItem (Join-Path $profile 'storage\print-jobs') -File).Count | Should -Be 0
    }
    It 'T343 missing printer fails explicitly and healthy service recovers' {
        if (-not $enabled) { Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_PRINTING=1.'; return }
        Native-Printing 'printing command no-such-public-printer-9348'
        $message = Primary-Message 'public-printer-recovery'
        Native-Printing "print $($message.id)" 'Message print failed'
        Native-Printing ('printing command ' + (Printer-Command))
        Native-Printing "print $($message.id)" 'Message print completed'
        $null = Wait-PrintCapture 8
    }
    It 'T344 output flood is bounded and subsequent print succeeds' {
        if (-not $enabled) { Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_PRINTING=1.'; return }
        Native-Printing ('printing command ' + (Printer-Command 'flood'))
        $message = @((Print-State).messages | Where-Object direction -EQ 'inbound')[0]
        Native-Printing "print $($message.id)" 'Message print failed'
        Native-Printing ('printing command ' + (Printer-Command))
        Native-Printing "print $($message.id)" 'Message print completed'
        $null = Wait-PrintCapture 9
    }
    It 'T345 blocked incoming printer cannot block message persistence and times out with recovery' {
        if (-not $enabled) { Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_PRINTING=1.'; return }
        Native-Printing ('printing command ' + (Printer-Command 'hang'))
        Native-Printing 'printing from everywhere'
        Native-Printing 'printing on'
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $null = Primary-Message 'public-hang-first'
        $null = Primary-Message 'public-hang-next-persisted'
        $watch.Elapsed.TotalSeconds | Should -BeLessThan 12
        Native-Printing 'printing off'
        Native-Printing ('printing command ' + (Printer-Command))
        $message = @((Print-State).messages | Where-Object content -EQ 'public-hang-next-persisted')[0]
        Native-Printing "print $($message.id)" 'Message print completed' # both queued hangs time out first
        $null = Wait-PrintCapture 10
        Get-Content (Join-Path $profile 'logfile') -Raw | Should -Match 'Printer timed out'
    }
    It 'T346 orderly shutdown cancels active and queued jobs without reporting false completion' {
        if (-not $enabled) { Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_PRINTING=1.'; return }
        Native-Printing ('printing command ' + (Printer-Command 'childhang'))
        Native-Printing 'printing on'
        $null = Primary-Message 'public-cancel-active'
        $startup = [Diagnostics.Stopwatch]::StartNew()
        while (-not [IO.File]::Exists("$marker.child") -and $startup.Elapsed.TotalSeconds -lt 12) {
            Start-Sleep -Milliseconds 20
        }
        [IO.File]::Exists("$marker.child") | Should -BeTrue
        $observedChild = [int][IO.File]::ReadAllText("$marker.child")
        $null = Primary-Message 'public-cancel-queued'
        $watch = [Diagnostics.Stopwatch]::StartNew()
        Stop-Print $runtime.Native
        $watch.Elapsed.TotalSeconds | Should -BeLessThan 12
        @(Get-ChildItem (Join-Path $profile 'storage\print-jobs') -File).Count | Should -Be 0
        Get-Content (Join-Path $profile 'logfile') -Raw | Should -Match 'cancelled'
        try {
            $child = [Diagnostics.Process]::GetProcessById($observedChild)
            try {
                $joined = [Diagnostics.Stopwatch]::StartNew()
                while (-not $child.HasExited -and $joined.Elapsed.TotalSeconds -lt 5) {
                    Start-Sleep -Milliseconds 20
                    $child.Refresh()
                }
                $child.HasExited | Should -BeTrue -Because 'The observed synthetic descendant must be stopped before shutdown completes.'
            }
            finally { $child.Dispose() }
        }
        catch [ArgumentException] { }
    }
    It 'T347 clean restart retains opt-in policy command template and contact classification' {
        if (-not $enabled) { Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_PRINTING=1.'; return }
        $runtime.Native = Start-PrintNative 'native-clean-restart'
        $saved = Get-Content (Join-Path $profile 'settings.json') -Raw | ConvertFrom-Json
        $saved.printMessages | Should -BeTrue
        $saved.printAllMessages | Should -BeTrue
        $saved.printingMessageTemplate | Should -Match '^PUBLIC'
        Native-Printing ('printing command ' + (Printer-Command))
        Send-Print $runtime.Native 'announce'
        $null = Read-Print $runtime.Native 'Destinations announced'
        $null = Primary-Message 'public-clean-restart-print'
        $null = Wait-PrintCapture 11
    }
    It 'T348 abrupt restart removes stale owned job files before a healthy opt-in print' {
        if (-not $enabled) { Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_PRINTING=1.'; return }
        Native-Printing ('printing command ' + (Printer-Command 'hang'))
        $null = Primary-Message 'public-abrupt-active'
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        while (@(Get-ChildItem (Join-Path $profile 'storage\print-jobs') -File).Count -eq 0 -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 50 }
        @(Get-ChildItem (Join-Path $profile 'storage\print-jobs') -File).Count | Should -Be 1
        $runtime.Native.Kill($true)
        $runtime.Native.WaitForExit(10000) | Should -BeTrue
        $runtime.Native = Start-PrintNative 'native-abrupt-restart'
        Native-Printing ('printing command ' + (Printer-Command))
        Send-Print $runtime.Native 'announce'
        $null = Read-Print $runtime.Native 'Destinations announced'
        $null = Primary-Message 'public-abrupt-restart-print'
        $null = Wait-PrintCapture 12
        @(Get-ChildItem (Join-Path $profile 'storage\print-jobs') -File).Count | Should -Be 0
    }
    It 'T349 malformed explicit commands leave persisted policy unchanged and both-origin manual printing remains operational' {
        if (-not $enabled) { Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_PRINTING=1.'; return }
        $settings = Join-Path $profile 'settings.json'
        $before = (Get-FileHash $settings).Hash
        Native-Printing 'printing command "unterminated' 'Error: Unterminated print command quote'
        Native-Printing 'printing from trusted,nope' 'Printing configuration saved'
        # Invalid template syntax is rejected before the settings transaction.
        $template = Join-Path $work 'invalid-template'
        [IO.File]::WriteAllText($template, '{mbody!r}')
        $before = (Get-FileHash $settings).Hash
        Native-Printing "printing template $template" 'Error: Print templates support only'
        (Get-FileHash $settings).Hash | Should -Be $before
        Send-Print $runtime.Native "send $peer public-native-outbound-print"
        $null = Read-Print $runtime.Native 'queued|Queued'
        $received = Read-PrintEvent $primary 'received'
        $received.content | Should -Be 'public-native-outbound-print'
        $received.signature | Should -BeTrue
        $message = @((Print-State).messages | Where-Object content -EQ 'public-native-outbound-print')[0]
        Native-Printing "print $($message.id)" 'Message print completed'
        $null = Wait-PrintCapture 13
        Stop-Print $runtime.Native
        Stop-Print $primary
    }

    AfterAll {
        if ($enabled) {
            foreach ($p in $processes) {
                try {
                    if (-not $p.HasExited) { $p.Kill($true); $null = $p.WaitForExit(10000) }
                    if ($pending.ContainsKey($p.Id)) {
                        try { $null = $logs[$p.Id].Text.AppendLine($pending[$p.Id].WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult()) } catch { }
                        $pending.Remove($p.Id)
                    }
                    $null = $logs[$p.Id].Text.Append($p.StandardOutput.ReadToEnd())
                    $logs[$p.Id].Text.ToString() | Set-Content (Join-Path $work ($logs[$p.Id].Name + '.stdout'))
                    $errors[$p.Id].GetAwaiter().GetResult() | Set-Content (Join-Path $work ($logs[$p.Id].Name + '.stderr'))
                }
                finally { $p.Dispose() }
            }
            @{ binary=$env:DOTRETICULUM_NOMADNET_BINARY; sha256=(Get-FileHash $env:DOTRETICULUM_NOMADNET_BINARY).Hash;
                fixture=(Get-FileHash (Join-Path $repository 'tests\NomadNet.Tests\NomadNetPrintingVectors.json')).Hash;
                platform=[Runtime.InteropServices.RuntimeInformation]::OSDescription; source=(git -C $repository rev-parse HEAD);
                topology='native leaf TCP server / actual full primary leaf TCP client; public synthetic printer stub only; no physical printer';
                cases=20; captures=@(Print-Captures).Count } | ConvertTo-Json -Depth 4 |
                Set-Content (Join-Path $work 'receipt.json') -Encoding utf8NoBOM
            Write-Host "Printing evidence: $work"
        }
    }
}
