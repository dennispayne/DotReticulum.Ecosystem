Describe 'NomadNet native executable' -Tag 'Native' {
    BeforeAll {
        $script:NomadNetBinary = $env:DOTRETICULUM_NOMADNET_BINARY
        if ([string]::IsNullOrWhiteSpace($script:NomadNetBinary)) {
            $script:NomadNetBinary = (Get-Command nomadnet -ErrorAction SilentlyContinue)?.Source
        }
        if (-not [string]::IsNullOrWhiteSpace($script:NomadNetBinary)) {
            $script:NomadNetBinary = [IO.Path]::GetFullPath($script:NomadNetBinary)
        }
    }

    It 'prints help and version without starting a Reticulum node' {
        if ([string]::IsNullOrWhiteSpace($script:NomadNetBinary) -or -not (Test-Path $script:NomadNetBinary)) {
            Set-ItResult -Skipped -Because 'Set DOTRETICULUM_NOMADNET_BINARY to the published executable.'
            return
        }

        $help = & $script:NomadNetBinary --help 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 0
        $help | Should -Match 'NomadNet'
        $help | Should -Match 'browse'

        $version = & $script:NomadNetBinary --version 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 0
        $version | Should -Match '1\.4\.4'
    }

    It 'initializes and reloads bounded local state without network access' {
        if ([string]::IsNullOrWhiteSpace($script:NomadNetBinary) -or -not (Test-Path $script:NomadNetBinary)) {
            Set-ItResult -Skipped -Because 'Set DOTRETICULUM_NOMADNET_BINARY to the published executable.'
            return
        }

        $directory = Join-Path (Get-Location) 'artifacts\tests\nomadnet-pester'
        if (Test-Path $directory) { Remove-Item $directory -Recurse -Force }
        try {
            & $script:NomadNetBinary --init --config $directory
            $LASTEXITCODE | Should -Be 0
            Test-Path (Join-Path $directory 'settings.json') | Should -BeTrue
            Test-Path (Join-Path $directory 'state.json') | Should -BeTrue

            $status = & $script:NomadNetBinary --status --config $directory 2>&1 | Out-String
            $LASTEXITCODE | Should -Be 0
            $status | Should -Match 'Initialized: True'
            $status | Should -Match 'Contacts: 0'
        }
        finally {
            if (Test-Path $directory) { Remove-Item $directory -Recurse -Force }
        }
    }

    It 'starts the real terminal loop, reports status, exits cleanly and retains its LXMF identity' {
        if ([string]::IsNullOrWhiteSpace($script:NomadNetBinary) -or -not (Test-Path $script:NomadNetBinary)) {
            Set-ItResult -Skipped -Because 'Set DOTRETICULUM_NOMADNET_BINARY to the published executable.'
            return
        }

        $work = Join-Path (Get-Location) 'artifacts\tests\nomadnet-terminal-pester'
        if ((Test-Path $work) -and $env:DOTRETICULUM_NOMADNET_KEEP_WORK -ne '1') { Remove-Item $work -Recurse -Force }
        $data = Join-Path $work 'data'
        $rns = Join-Path $work 'rns'
        $null = New-Item $data -ItemType Directory -Force
        $null = New-Item $rns -ItemType Directory -Force
        @(
            '[reticulum]'
            '  enable_transport = No'
            '  share_instance = No'
            '  instance_name = nomadnet_native_acceptance'
            ''
            '[logging]'
            '  loglevel = 4'
            ''
            '[interfaces]'
        ) | Set-Content -Path (Join-Path $rns 'config') -Encoding utf8

        $process = $null
        $firstAddress = $null
        try {
            foreach ($run in 1..2) {
                $start = [Diagnostics.ProcessStartInfo]::new()
                $start.FileName = $script:NomadNetBinary
                $start.WorkingDirectory = (Get-Location).Path
                $start.UseShellExecute = $false
                $start.RedirectStandardInput = $true
                $start.RedirectStandardOutput = $true
                $start.RedirectStandardError = $true
                foreach ($argument in @('--config', $data, '--rnsconfig', $rns)) { $start.ArgumentList.Add($argument) }
                $process = [Diagnostics.Process]::new()
                $process.StartInfo = $start
                $null = $process.Start()
                $process.BeginErrorReadLine()
                $banner = $process.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(20)).GetAwaiter().GetResult()
                $addressLine = $process.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(20)).GetAwaiter().GetResult()
                $banner | Should -Match 'NomadNet'
                $addressLine | Should -Match 'LXMF delivery address:\s*([0-9a-f]{32})'
                $match = [regex]::Match($addressLine, 'LXMF delivery address:\s*([0-9a-f]{32})')
                $address = $match.Groups[1].Value
                if ($run -eq 1) { $firstAddress = $address } else { $address | Should -Be $firstAddress }

                $process.StandardInput.WriteLine('status')
                $process.StandardInput.WriteLine('contacts')
                $process.StandardInput.WriteLine('quit')
                $process.StandardInput.Flush()
                $process.WaitForExit(20000) | Should -BeTrue
                $process.ExitCode | Should -Be 0
                $process.Dispose()
                $process = $null
            }
        }
        finally {
            if ($null -ne $process) {
                if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
                $process.Dispose()
            }
            if (Test-Path $work) { Remove-Item $work -Recurse -Force }
        }
    }
}

Describe 'NomadNet native hosted workflows' -Tag 'Native' {
    It 'hosts pages and resources, caches pages, transfers attachments, ingests paper and reloads state' {
        if ([string]::IsNullOrWhiteSpace($env:DOTRETICULUM_NOMADNET_BINARY)) {
            Set-ItResult -Skipped -Because 'Select an exact published binary with DOTRETICULUM_NOMADNET_BINARY.'
            return
        }
        $binary = [IO.Path]::GetFullPath($env:DOTRETICULUM_NOMADNET_BINARY)
        if (-not (Test-Path $binary)) { throw "Selected binary does not exist: $binary" }
        $work = Join-Path (Get-Location) "artifacts\tests\nomadnet-functional-$([Guid]::NewGuid().ToString('N'))"
        $server = $null
        $client = $null
        $processes = [Collections.Generic.List[Diagnostics.Process]]::new()
        $readUntil = {
            param($process, [string] $pattern, [int] $seconds = 60)
            $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
            while ([DateTime]::UtcNow -lt $deadline) {
                $line = $process.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds($seconds)).GetAwaiter().GetResult()
                if ($null -eq $line) { throw "Process ended before '$pattern'." }
                if ($line -match $pattern) { return $line }
            }
            throw "Expected output was not observed: $pattern"
        }
        $start = {
            param([string] $name, [string[]] $extra = @())
            $info = [Diagnostics.ProcessStartInfo]::new($binary)
            $info.UseShellExecute = $false
            $info.RedirectStandardInput = $true
            $info.RedirectStandardOutput = $true
            $info.RedirectStandardError = $true
            foreach ($argument in @('--config', (Join-Path $work $name), '--rnsconfig', (Join-Path $work "$name-rns")) + $extra) {
                $info.ArgumentList.Add($argument)
            }
            $process = [Diagnostics.Process]::new()
            $process.StartInfo = $info
            $null = $process.Start()
            $process.BeginErrorReadLine()
            $processes.Add($process)
            return $process
        }.GetNewClosure()
        $send = {
            param($process, [string] $command)
            $process.StandardInput.WriteLine($command)
            $process.StandardInput.Flush()
        }
        try {
            $null = New-Item (Join-Path $work 'server-rns') -ItemType Directory -Force
            $null = New-Item (Join-Path $work 'client-rns') -ItemType Directory -Force
            $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
            $listener.Start()
            $port = $listener.LocalEndpoint.Port
            $listener.Stop()
            @('[reticulum]', '  share_instance = No', '  enable_transport = No', '[interfaces]',
                '  [[Server]]', '    type = TCPServerInterface', '    enabled = Yes',
                '    listen_ip = 127.0.0.1', "    listen_port = $port") |
                Set-Content (Join-Path $work 'server-rns\config') -Encoding utf8
            @('[reticulum]', '  share_instance = No', '  enable_transport = No', '[interfaces]',
                '  [[Client]]', '    type = TCPClientInterface', '    enabled = Yes',
                '    target_host = 127.0.0.1', "    target_port = $port") |
                Set-Content (Join-Path $work 'client-rns\config') -Encoding utf8
            $null = New-Item (Join-Path $work 'server\storage\pages') -ItemType Directory -Force
            $null = New-Item (Join-Path $work 'server\storage\files') -ItemType Directory -Force
            '{"schemaVersion":1,"displayName":"Native host","enableNode":true,"enablePropagation":true,"deliveryTransferLimitBytes":4000000}' |
                Set-Content (Join-Path $work 'server\settings.json') -Encoding utf8
            [IO.File]::WriteAllText((Join-Path $work 'server\storage\pages\index.mu'), "#!c=60`n>Hosted native page`nFIRST_PAGE", [Text.UTF8Encoding]::new($false))
            if (-not [OperatingSystem]::IsWindows()) {
                [IO.File]::SetUnixFileMode((Join-Path $work 'server\storage\pages\index.mu'),
                    [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)
            }
            $bytes = [byte[]]::new(2200000)
            [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
            [IO.File]::WriteAllBytes((Join-Path $work 'server\storage\files\large.bin'), $bytes)
            $server = & $start 'server'
            $serverDelivery = [regex]::Match((& $readUntil $server 'LXMF delivery address:\s*([0-9a-f]{32})'), '[0-9a-f]{32}').Value
            $serverNode = [regex]::Match((& $readUntil $server 'Hosted node address:\s*([0-9a-f]{32})'), '[0-9a-f]{32}').Value
            $propagationNode = [regex]::Match((& $readUntil $server 'Propagation node address:\s*([0-9a-f]{32})'), '[0-9a-f]{32}').Value
            $client = & $start 'client'
            $clientDelivery = [regex]::Match((& $readUntil $client 'LXMF delivery address:\s*([0-9a-f]{32})'), '[0-9a-f]{32}').Value
            & $send $client "browse $serverNode"
            $null = & $readUntil $client 'FIRST_PAGE'
            [IO.File]::WriteAllText((Join-Path $work 'server\storage\pages\index.mu'), "#!c=60`nSECOND_PAGE", [Text.UTF8Encoding]::new($false))
            & $send $client "browse $serverNode"
            $null = & $readUntil $client '\(cached page\)'
            $null = & $readUntil $client 'FIRST_PAGE'
            & $send $client 'reload'
            $null = & $readUntil $client 'SECOND_PAGE'
            & $send $client "fetch $serverNode /file/large.bin downloaded.bin"
            $null = & $readUntil $client 'Saved 2200000 bytes' 120
            $expected = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
            (Get-FileHash (Join-Path $work 'client\downloads\downloaded.bin') -Algorithm SHA256).Hash | Should -Be $expected
            & $send $client "attach $serverDelivery $(Join-Path $work 'client\downloads\downloaded.bin')"
            $null = & $readUntil $client 'Message queued:'
            $deadline = [DateTime]::UtcNow.AddSeconds(120)
            $attachment = $null
            while ([DateTime]::UtcNow -lt $deadline -and $null -eq $attachment) {
                $attachment = Get-ChildItem (Join-Path $work 'server\attachments') -Filter '0.bin' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($null -eq $attachment) { Start-Sleep -Milliseconds 100 }
            }
            $attachment | Should -Not -BeNullOrEmpty
            (Get-FileHash $attachment.FullName -Algorithm SHA256).Hash | Should -Be $expected
            & $send $client "paper $serverDelivery native-paper-content"
            $paperLine = & $readUntil $client 'lxm://[A-Za-z0-9_-]+'
            $uri = [regex]::Match($paperLine, 'lxm://[A-Za-z0-9_-]+').Value
            & $send $server "ingest $uri"
            $null = & $readUntil $server 'Paper ingest: LocalDelivery'
            & $send $server "ingest $uri"
            $null = & $readUntil $server 'Paper ingest: Duplicate'
            & $send $client "propagation $propagationNode"
            $null = & $readUntil $client 'Propagation selection saved.'
            & $send $client "send-propagated $clientDelivery propagated-native-content"
            $null = & $readUntil $client 'Message queued:'
            $deadline = [DateTime]::UtcNow.AddSeconds(90)
            $sent = $false
            while ([DateTime]::UtcNow -lt $deadline -and -not $sent) {
                try { $state = [IO.File]::ReadAllText((Join-Path $work 'client\state.json')) | ConvertFrom-Json }
                catch [IO.IOException] { Start-Sleep -Milliseconds 100; continue }
                $sent = @($state.messages | Where-Object { $_.content -eq 'propagated-native-content' -and $_.status -eq 'sent' }).Count -gt 0
                if (-not $sent) { Start-Sleep -Milliseconds 100 }
            }
            $sent | Should -BeTrue
            & $send $client 'sync'
            $null = & $readUntil $client 'Propagation sync: 1 messages \(Complete\)' 90
            & $send $client 'sync'
            $null = & $readUntil $client 'Propagation sync: 0 messages \(Complete\)' 30
            & $send $server 'quit'
            $server.WaitForExit(30000) | Should -BeTrue
            $server.ExitCode | Should -Be 0
            $server = & $start 'server'
            $restarted = [regex]::Match((& $readUntil $server 'LXMF delivery address:\s*([0-9a-f]{32})'), '[0-9a-f]{32}').Value
            $restarted | Should -Be $serverDelivery
            & $send $server 'messages'
            $null = & $readUntil $server 'native-paper-content'
            & $send $server 'quit'
            & $send $client 'quit'
            $server.WaitForExit(30000) | Should -BeTrue
            $client.WaitForExit(30000) | Should -BeTrue
            $server.ExitCode | Should -Be 0
            $client.ExitCode | Should -Be 0
        }
        finally {
            foreach ($process in $processes) {
                if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
                $process.Dispose()
            }
            if (Test-Path $work) { Remove-Item $work -Recurse -Force }
        }
    }
}

Describe 'NomadNet live LXMF interoperability' -Tag 'Live' {
    It 'exchanges direct messages with the pinned Python LXMF peer' {
        if ($env:DOTRETICULUM_NOMADNET_LIVE -ne '1' -or
            [string]::IsNullOrWhiteSpace($env:DOTRETICULUM_PYTHON) -or
            [string]::IsNullOrWhiteSpace($env:DOTRETICULUM_UPSTREAM) -or
            [string]::IsNullOrWhiteSpace($env:DOTRETICULUM_NOMADNET_BINARY)) {
            Set-ItResult -Skipped -Because 'Set DOTRETICULUM_NOMADNET_LIVE=1, DOTRETICULUM_PYTHON, DOTRETICULUM_UPSTREAM, and DOTRETICULUM_NOMADNET_BINARY.'
            return
        }

        $root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $interop = Join-Path $root 'tests\Interop\nomadnet_live_peer.py'
        $python = [IO.Path]::GetFullPath($env:DOTRETICULUM_PYTHON)
        $binary = [IO.Path]::GetFullPath($env:DOTRETICULUM_NOMADNET_BINARY)
        if (-not (Test-Path $interop) -or -not (Test-Path $binary)) {
            throw 'The NomadNet live adapter or native executable is missing.'
        }

        $work = Join-Path (Get-Location) "artifacts\tests\nomadnet-live-$([Guid]::NewGuid().ToString('N'))"
        if (Test-Path $work) { Remove-Item $work -Recurse -Force }
        $pythonConfig = Join-Path $work 'python'
        $appConfig = Join-Path $work 'app-rns'
        $appData = Join-Path $work 'app'
        $null = New-Item $pythonConfig -ItemType Directory -Force
        $null = New-Item $appConfig -ItemType Directory -Force
        $null = New-Item $appData -ItemType Directory -Force
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $port = $listener.LocalEndpoint.Port
        $listener.Stop()

        $appProcess = $null
        $peerProcess = $null
        try {
            $configLines = @(
                '[reticulum]'
                '  enable_transport = No'
                '  share_instance = No'
                '  instance_name = nomadnet_native_live'
                ''
                '[logging]'
                '  loglevel = 4'
                ''
                '[interfaces]'
                '  [[PythonReference]]'
                '    type = TCPClientInterface'
                '    enabled = Yes'
                '    target_host = 127.0.0.1'
                "    target_port = $port"
            )
            $configLines | Set-Content -Path (Join-Path $appConfig 'config') -Encoding utf8

            $peerStart = [Diagnostics.ProcessStartInfo]::new()
            $peerStart.FileName = $python
            $peerStart.WorkingDirectory = $root
            $peerStart.UseShellExecute = $false
            $peerStart.RedirectStandardInput = $true
            $peerStart.RedirectStandardOutput = $true
            $peerStart.RedirectStandardError = $true
            $peerStart.ArgumentList.Add($interop)
            $peerStart.ArgumentList.Add('--config')
            $peerStart.ArgumentList.Add($pythonConfig)
            $peerStart.ArgumentList.Add('--tcp-server-port')
            $peerStart.ArgumentList.Add("$port")
            $peerProcess = [Diagnostics.Process]::new()
            $peerProcess.StartInfo = $peerStart
            $null = $peerProcess.Start()
            $peerProcess.BeginErrorReadLine()
            $readyLine = $peerProcess.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(60)).GetAwaiter().GetResult()
            while ($readyLine -notmatch '"event"\s*:\s*"ready"') {
                if ($null -eq $readyLine) { throw 'Python LXMF peer exited before it became ready.' }
                $readyLine = $peerProcess.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(60)).GetAwaiter().GetResult()
            }
            $peerHash = ($readyLine.Substring(2) | ConvertFrom-Json).destination

            $appStart = [Diagnostics.ProcessStartInfo]::new()
            $appStart.FileName = $binary
            $appStart.WorkingDirectory = $root
            $appStart.UseShellExecute = $false
            $appStart.RedirectStandardInput = $true
            $appStart.RedirectStandardOutput = $true
            $appStart.RedirectStandardError = $true
            $appStart.ArgumentList.Add('--config')
            $appStart.ArgumentList.Add($appData)
            $appStart.ArgumentList.Add('--rnsconfig')
            $appStart.ArgumentList.Add($appConfig)
            $appProcess = [Diagnostics.Process]::new()
            $appProcess.StartInfo = $appStart
            $null = $appProcess.Start()
            $appProcess.BeginErrorReadLine()
            $appHash = $null
            $deadline = [DateTime]::UtcNow.AddSeconds(60)
            while ([DateTime]::UtcNow -lt $deadline -and $null -eq $appHash) {
                $line = $appProcess.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult()
                if ($null -eq $line) { throw 'NomadNet exited before announcing its LXMF destination.' }
                if ($line -match 'LXMF delivery address:\s*([0-9a-f]{32})') { $appHash = $Matches[1] }
            }
            if ($null -eq $appHash) { throw 'NomadNet did not report its delivery destination.' }

            $peerProcess.StandardInput.WriteLine('announce')
            $peerProcess.StandardInput.Flush()
            $peerProcess.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult() |
                Should -Match '"event"\s*:\s*"announced"'

            $appProcess.StandardInput.WriteLine("send $peerHash native-to-python")
            $appProcess.StandardInput.Flush()
            $deadline = [DateTime]::UtcNow.AddSeconds(90)
            $received = $null
            while ([DateTime]::UtcNow -lt $deadline -and $null -eq $received) {
                $line = $peerProcess.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult()
                if ($null -eq $line) { throw 'Python LXMF peer exited while receiving.' }
                if ($line -match '"event"\s*:\s*"received"') { $received = $line.Substring(2) | ConvertFrom-Json }
            }
            $received.content | Should -Be 'native-to-python'

            $peerProcess.StandardInput.WriteLine("send $appHash python-to-native")
            $peerProcess.StandardInput.Flush()
            $deadline = [DateTime]::UtcNow.AddSeconds(90)
            $replySeen = $false
            $pendingOutputLine = $null
            while ([DateTime]::UtcNow -lt $deadline -and -not $replySeen) {
                $appProcess.StandardInput.WriteLine("messages $peerHash")
                $appProcess.StandardInput.Flush()
                $pollDeadline = [DateTime]::UtcNow.AddSeconds(2)
                while ([DateTime]::UtcNow -lt $pollDeadline -and -not $replySeen) {
                    if ($null -eq $pendingOutputLine) {
                        $pendingOutputLine = $appProcess.StandardOutput.ReadLineAsync()
                    }
                    try {
                        $line = $pendingOutputLine.WaitAsync([TimeSpan]::FromSeconds(1)).GetAwaiter().GetResult()
                        $pendingOutputLine = $null
                    }
                    catch [TimeoutException] {
                        $line = ''
                    }
                    if ($null -eq $line) { throw 'NomadNet exited while receiving the Python message.' }
                    if ($line.Contains('python-to-native')) { $replySeen = $true }
                    elseif ($line.Contains('No stored messages.')) { break }
                }
            }

            $replySeen | Should -BeTrue
        }
        finally {
            foreach ($process in @($appProcess, $peerProcess)) {
                if ($null -ne $process) {
                    if (-not $process.HasExited) {
                        try { $process.StandardInput.WriteLine('quit'); $process.StandardInput.Flush() } catch {}
                        if (-not $process.WaitForExit(5000)) { Stop-Process -Id $process.Id -Force }
                    }
                    $process.Dispose()
                }
            }
            if (Test-Path $work) { Remove-Item $work -Recurse -Force }
        }
    }
}

Describe 'NomadNet upstream node interoperability' -Tag 'Live' {
    It 'browses and downloads from upstream NomadNet, exchanges LXMF, and reloads durable history' {
        $readNomadNetLine = {
            param([System.IO.StreamReader] $Reader, [ref] $PendingLine, [int] $WaitMilliseconds)
            if ($null -eq $PendingLine.Value) {
                $PendingLine.Value = $Reader.ReadLineAsync()
            }
            try {
                $line = $PendingLine.Value.WaitAsync([TimeSpan]::FromMilliseconds($WaitMilliseconds)).GetAwaiter().GetResult()
                $PendingLine.Value = $null
                return [PSCustomObject]@{ Completed = $true; Line = $line }
            }
            catch [TimeoutException] {
                return [PSCustomObject]@{ Completed = $false; Line = $null }
            }
        }.GetNewClosure()
        $waitNomadNetOutput = {
            param([System.IO.StreamReader] $Reader, [ref] $PendingLine, [string] $Pattern, [int] $TimeoutSeconds, [string] $Description)
            $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
            while ([DateTime]::UtcNow -lt $deadline) {
                $result = & $readNomadNetLine $Reader $PendingLine 500
                if (-not $result.Completed) { continue }
                if ($null -eq $result.Line) { throw "${Description}: process closed its output before the expected event." }
                if ($result.Line -match '"event"\s*:\s*"(error|failed|request-failed)"') { throw "${Description}: $($result.Line)" }
                if ($result.Line -match $Pattern) { return $result.Line }
            }
            throw "${Description}: timed out waiting for '$Pattern'."
        }.GetNewClosure()

        if (-not [OperatingSystem]::IsLinux() -or
            $env:DOTRETICULUM_NOMADNET_NODE_LIVE -ne '1' -or
            [string]::IsNullOrWhiteSpace($env:DOTRETICULUM_PYTHON) -or
            [string]::IsNullOrWhiteSpace($env:DOTRETICULUM_UPSTREAM) -or
            [string]::IsNullOrWhiteSpace($env:DOTRETICULUM_LXMF_UPSTREAM) -or
            [string]::IsNullOrWhiteSpace($env:DOTRETICULUM_NOMADNET_BINARY)) {
            Set-ItResult -Skipped -Because 'Enable the Linux upstream test with DOTRETICULUM_NOMADNET_NODE_LIVE=1 and the pinned Python and native binary paths.'
            return
        }

        $root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $source = if ([string]::IsNullOrWhiteSpace($env:DOTRETICULUM_NOMADNET_SOURCE)) {
            Join-Path $root 'artifacts\builds\nomadnet\nomadnet-source'
        } else {
            $env:DOTRETICULUM_NOMADNET_SOURCE
        }
        $interop = Join-Path $root 'tests\Interop\nomadnet_live_node.py'
        $python = [IO.Path]::GetFullPath($env:DOTRETICULUM_PYTHON)
        $binary = [IO.Path]::GetFullPath($env:DOTRETICULUM_NOMADNET_BINARY)
        if (-not (Test-Path $source) -or -not (Test-Path $interop) -or -not (Test-Path $binary)) {
            throw 'The primary NomadNet source, adapter, or Linux NativeAOT executable is missing.'
        }

        $work = if ([string]::IsNullOrWhiteSpace($env:DOTRETICULUM_NOMADNET_WORK)) {
            Join-Path $root 'artifacts\tests\nomadnet-upstream-live'
        } else {
            [IO.Path]::GetFullPath($env:DOTRETICULUM_NOMADNET_WORK)
        }
        if (Test-Path $work) { Remove-Item $work -Recurse -Force }
        $nodeConfig = Join-Path $work 'upstream'
        $nodeRns = Join-Path $work 'upstream-rns'
        $nativeData = Join-Path $work 'native'
        $nativeRns = Join-Path $work 'native-rns'
        $null = New-Item $nodeConfig -ItemType Directory -Force
        $null = New-Item $nodeRns -ItemType Directory -Force
        $null = New-Item $nativeData -ItemType Directory -Force
        $null = New-Item $nativeRns -ItemType Directory -Force

        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $port = $listener.LocalEndpoint.Port
        $listener.Stop()
        $topology = if ([string]::IsNullOrWhiteSpace($env:DOTRETICULUM_NOMADNET_TRANSPORT)) { 'direct' } else { $env:DOTRETICULUM_NOMADNET_TRANSPORT }
        if ($topology -notin @('direct', 'python', 'native')) { throw "Unsupported NomadNet topology: $topology" }
        $nativeTargetPort = $port
        $transport = $null
        $transportConfig = Join-Path $work 'transport-rns'
        if ($topology -ne 'direct') {
            $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
            $listener.Start()
            $nativeTargetPort = $listener.LocalEndpoint.Port
            $listener.Stop()
            $null = New-Item $transportConfig -ItemType Directory -Force
            @('[reticulum]', '  share_instance = No', '  enable_transport = Yes', '[interfaces]',
                '  [[Upstream]]', '    type = TCPClientInterface', '    enabled = Yes',
                '    target_host = 127.0.0.1', "    target_port = $port",
                '  [[Native]]', '    type = TCPServerInterface', '    enabled = Yes',
                '    listen_ip = 127.0.0.1', "    listen_port = $nativeTargetPort") |
                Set-Content (Join-Path $transportConfig 'config') -Encoding utf8
        }
        @(
            '[reticulum]'
            '  enable_transport = No'
            '  share_instance = No'
            ''
            '[interfaces]'
            '  [[NomadNetUpstreamServer]]'
            '    type = TCPServerInterface'
            '    enabled = Yes'
            '    listen_ip = 127.0.0.1'
            "    listen_port = $port"
        ) | Set-Content -Path (Join-Path $nodeRns 'config') -Encoding utf8
        @(
            '[reticulum]'
            '  enable_transport = No'
            '  share_instance = No'
            ''
            '[interfaces]'
            '  [[NomadNetUpstreamClient]]'
            '    type = TCPClientInterface'
            '    enabled = Yes'
            '    target_host = 127.0.0.1'
            "    target_port = $nativeTargetPort"
        ) | Set-Content -Path (Join-Path $nativeRns 'config') -Encoding utf8

        $pythonPath = "$source`:$($env:DOTRETICULUM_UPSTREAM)`:$($env:DOTRETICULUM_LXMF_UPSTREAM)`:$root/tests/Interop"
        $environment = @{
            DOTRETICULUM_UPSTREAM = $env:DOTRETICULUM_UPSTREAM
            DOTRETICULUM_LXMF_UPSTREAM = $env:DOTRETICULUM_LXMF_UPSTREAM
            DOTRETICULUM_NOMADNET_SOURCE = $source
            PYTHONPATH = $pythonPath
        }
        $bootstrap = $null
        $upstream = $null
        $client = $null
        try {
            $bootstrapStart = [Diagnostics.ProcessStartInfo]::new()
            $bootstrapStart.FileName = $python
            $bootstrapStart.WorkingDirectory = $root
            $bootstrapStart.UseShellExecute = $false
            foreach ($argument in @('-u', '-m', 'nomadnet.nomadnet', '--daemon', '--console',
                '--config', $nodeConfig, '--rnsconfig', $nodeRns)) {
                $bootstrapStart.ArgumentList.Add($argument)
            }
            foreach ($entry in $environment.GetEnumerator()) { $bootstrapStart.Environment[$entry.Key] = $entry.Value }
            $bootstrap = [Diagnostics.Process]::new()
            $bootstrap.StartInfo = $bootstrapStart
            $null = $bootstrap.Start()
            $identityPath = Join-Path $nodeConfig 'storage\identity'
            $nodeAppConfig = Join-Path $nodeConfig 'config'
            $deadline = [DateTime]::UtcNow.AddSeconds(60)
            while ([DateTime]::UtcNow -lt $deadline -and
                (-not (Test-Path $identityPath) -or -not (Test-Path $nodeAppConfig))) {
                if ($bootstrap.HasExited) { throw 'Upstream NomadNet exited before creating its initial configuration.' }
                Start-Sleep -Milliseconds 100
            }
            (Test-Path $identityPath) | Should -BeTrue
            (Test-Path $nodeAppConfig) | Should -BeTrue
            Start-Sleep -Seconds 1
            $bootstrap.Kill()
            $bootstrap.WaitForExit()
            $bootstrap.Dispose()
            $bootstrap = $null

            $configuration = [IO.File]::ReadAllText($nodeAppConfig)
            if (-not $configuration.Contains('enable_node = no')) {
                throw 'Expected the unmodified upstream default node setting was not found.'
            }
            $configuration = $configuration.Replace('enable_node = no', 'enable_node = yes')
            $configuration = $configuration.Replace('max_accepted_size = 500', 'max_accepted_size = 4000')
            [IO.File]::WriteAllText($nodeAppConfig, $configuration, [Text.UTF8Encoding]::new($false))
            $pages = Join-Path $nodeConfig 'storage\pages'
            $files = Join-Path $nodeConfig 'storage\files'
            $null = New-Item $pages -ItemType Directory -Force
            $null = New-Item $files -ItemType Directory -Force
            [IO.File]::WriteAllText((Join-Path $pages 'readme.mu'), "# Live page`n`nNOMADNET_UPSTREAM_PAGE_OK", [Text.UTF8Encoding]::new($false))
            [IO.File]::WriteAllText((Join-Path $files 'live.txt'), 'UPSTREAM_FILE_012345', [Text.UTF8Encoding]::new($false))
            $large = [byte[]]::new(2200000)
            [Security.Cryptography.RandomNumberGenerator]::Fill($large)
            [IO.File]::WriteAllBytes((Join-Path $files 'large.bin'), $large)
            $expectedLargeHash = (Get-FileHash (Join-Path $files 'large.bin') -Algorithm SHA256).Hash
            '{"schemaVersion":1,"displayName":"Native app","enableNode":true,"deliveryTransferLimitBytes":4000000}' |
                Set-Content (Join-Path $nativeData 'settings.json') -Encoding utf8
            $null = New-Item (Join-Path $nativeData 'storage\pages') -ItemType Directory -Force
            $null = New-Item (Join-Path $nativeData 'storage\files') -ItemType Directory -Force
            [IO.File]::WriteAllText((Join-Path $nativeData 'storage\pages\index.mu'), "#!c=0`nNATIVE_HOST_PAGE_OK", [Text.UTF8Encoding]::new($false))
            [IO.File]::WriteAllBytes((Join-Path $nativeData 'storage\files\large.bin'), $large)

            $upstreamStart = [Diagnostics.ProcessStartInfo]::new()
            $upstreamStart.FileName = $python
            $upstreamStart.WorkingDirectory = $root
            $upstreamStart.UseShellExecute = $false
            $upstreamStart.RedirectStandardInput = $true
            $upstreamStart.RedirectStandardOutput = $true
            foreach ($argument in @('-u', $interop, '--config', $nodeConfig, '--rnsconfig', $nodeRns)) {
                $upstreamStart.ArgumentList.Add($argument)
            }
            foreach ($entry in $environment.GetEnumerator()) { $upstreamStart.Environment[$entry.Key] = $entry.Value }
            $upstream = [Diagnostics.Process]::new()
            $upstream.StartInfo = $upstreamStart
            $null = $upstream.Start()
            $upstreamPending = $null
            $readyLine = & $waitNomadNetOutput $upstream.StandardOutput ([ref]$upstreamPending) `
                '"event"\s*:\s*"ready"' 60 'Upstream NomadNet'
            $ready = $readyLine.Substring(2) | ConvertFrom-Json
            $ready.version | Should -Be '1.4.4'
            $ready.module | Should -Match ([regex]::Escape($source))
            $ready.node | Should -Match '^[0-9a-f]{32}$'
            $ready.delivery | Should -Match '^[0-9a-f]{32}$'
            if ($topology -ne 'direct') {
                $transportStart = [Diagnostics.ProcessStartInfo]::new()
                $transportStart.FileName = if ($topology -eq 'python') { $python } else { $binary }
                $transportStart.UseShellExecute = $false
                $transportStart.RedirectStandardInput = $true
                $transportStart.RedirectStandardOutput = $true
                $transportStart.RedirectStandardError = $true
                $arguments = if ($topology -eq 'python') {
                    @('-u', (Join-Path $root 'tests\Interop\nomadnet_transport_peer.py'), '--config', $transportConfig)
                } else {
                    @('--daemon', '--config', (Join-Path $work 'transport-app'), '--rnsconfig', $transportConfig)
                }
                foreach ($argument in $arguments) { $transportStart.ArgumentList.Add($argument) }
                foreach ($entry in $environment.GetEnumerator()) { $transportStart.Environment[$entry.Key] = $entry.Value }
                $transport = [Diagnostics.Process]::new()
                $transport.StartInfo = $transportStart
                $null = $transport.Start()
                $transport.BeginErrorReadLine()
                $transportPending = $null
                $pattern = if ($topology -eq 'python') { '"event"\s*:\s*"transport-ready"' } else { 'Daemon ready.' }
                $null = & $waitNomadNetOutput $transport.StandardOutput ([ref]$transportPending) $pattern 30 'Transport node readiness'
            }
            Start-Sleep -Seconds 8

            $startNative = {
                param([string[]] $commands)
                $start = [Diagnostics.ProcessStartInfo]::new()
                $start.FileName = $binary
                $start.WorkingDirectory = $root
                $start.UseShellExecute = $false
                $start.RedirectStandardInput = $true
                $start.RedirectStandardOutput = $true
                foreach ($argument in @('--config', $nativeData, '--rnsconfig', $nativeRns)) {
                    $start.ArgumentList.Add($argument)
                }
                $process = [Diagnostics.Process]::new()
                $process.StartInfo = $start
                $null = $process.Start()
                try {
                    $pending = $null
                    $addressLine = & $waitNomadNetOutput $process.StandardOutput ([ref]$pending) `
                        'LXMF delivery address:\s*([0-9a-f]{32})' 60 'Native NomadNet'
                    $address = [regex]::Match($addressLine, 'LXMF delivery address:\s*([0-9a-f]{32})').Groups[1].Value
                    $transcript = [Text.StringBuilder]::new()
                    foreach ($command in $commands | Where-Object { $_ -ne 'quit' }) {
                        $process.StandardInput.WriteLine($command)
                        $process.StandardInput.WriteLine('status')
                        $process.StandardInput.Flush()
                        do {
                            $line = $process.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(120)).GetAwaiter().GetResult()
                            if ($null -eq $line) { throw "Native process exited during '$command'." }
                            $null = $transcript.AppendLine($line)
                            if ($line -match 'Error:') { throw "Native command '$command' failed: $line" }
                        } while ($line -notmatch 'Reticulum interfaces:')
                    }
                    if (@($commands | Where-Object { $_ -like 'send *' }).Count -gt 0) {
                        $deadline = [DateTime]::UtcNow.AddSeconds(90)
                        $delivered = $false
                        while ([DateTime]::UtcNow -lt $deadline -and -not $delivered) {
                            try { $state = [IO.File]::ReadAllText((Join-Path $nativeData 'state.json')) | ConvertFrom-Json }
                            catch [IO.IOException] { Start-Sleep -Milliseconds 100; continue }
                            $delivered = @($state.messages | Where-Object {
                                $_.content -eq 'native-to-upstream-nomadnet' -and $_.status -eq 'delivered'
                            }).Count -gt 0
                            if (-not $delivered) { Start-Sleep -Milliseconds 100 }
                        }
                        if (-not $delivered) {
                            $statuses = @($state.messages | ForEach-Object { "$($_.direction):$($_.status)" }) -join ','
                            throw "Native direct message did not reach delivered state before shutdown. Statuses: $statuses. Transcript: $transcript"
                        }
                    }
                    $outputTask = $process.StandardOutput.ReadToEndAsync()
                    $process.StandardInput.WriteLine('quit')
                    $process.StandardInput.Flush()
                    if (-not $process.WaitForExit(180000)) { throw 'Native NomadNet did not exit after its terminal commands.' }
                    if ($process.ExitCode -ne 0) { throw "Native NomadNet exited with $($process.ExitCode)." }
                    $output = $transcript.ToString() + $outputTask.GetAwaiter().GetResult()
                    return [PSCustomObject]@{ Address = $address; Output = $output }
                }
                finally {
                    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
                    $process.Dispose()
                }
            }.GetNewClosure()

            $firstRun = & $startNative @(
                "browse $($ready.node) /page/readme.mu",
                "fetch $($ready.node) /file/live.txt live.txt",
                "fetch $($ready.node) /file/large.bin large.bin",
                "send $($ready.delivery) native-to-upstream-nomadnet",
                'quit'
            )
            $upstream.StandardInput.WriteLine('stats')
            $upstream.StandardInput.Flush()
            $statsLine = & $waitNomadNetOutput $upstream.StandardOutput ([ref]$upstreamPending) `
                '"event"\s*:\s*"stats"' 10 'Upstream NomadNet request counters'
            $stats = $statsLine.Substring(2) | ConvertFrom-Json
            $stats.served_pages | Should -Be 1
            $stats.served_files | Should -Be 2
            if ($firstRun.Output -notmatch 'NOMADNET_UPSTREAM_PAGE_OK') {
                $logPath = Join-Path $nodeConfig 'logfile'
                $log = if (Test-Path $logPath) { Get-Content $logPath -Tail 30 | Out-String } else { '(no upstream log file)' }
                throw "The upstream page response was not rendered. Node state: $($stats | ConvertTo-Json -Compress). Log: $log Client output: $($firstRun.Output)"
            }
            $firstRun.Output | Should -Match 'NOMADNET_UPSTREAM_PAGE_OK'
            $firstRun.Output | Should -Match 'Saved 20 bytes'
            [IO.File]::ReadAllText((Join-Path $nativeData 'downloads\live.txt')) | Should -Be 'UPSTREAM_FILE_012345'
            (Get-FileHash (Join-Path $nativeData 'downloads\large.bin') -Algorithm SHA256).Hash | Should -Be $expectedLargeHash

            $upstreamConversation = Join-Path (Join-Path $nodeConfig 'storage\conversations') $firstRun.Address
            $deadline = [DateTime]::UtcNow.AddSeconds(60)
            while ([DateTime]::UtcNow -lt $deadline -and
                (-not (Test-Path $upstreamConversation) -or
                    (Get-ChildItem $upstreamConversation -File | Where-Object { $_.Name -notin @('unread', '.index') }).Count -eq 0)) {
                Start-Sleep -Milliseconds 250
            }
            (Get-ChildItem $upstreamConversation -File | Where-Object { $_.Name -notin @('unread', '.index') }).Count | Should -BeGreaterThan 0

            $clientStart = [Diagnostics.ProcessStartInfo]::new()
            $clientStart.FileName = $binary
            $clientStart.WorkingDirectory = $root
            $clientStart.UseShellExecute = $false
            $clientStart.RedirectStandardInput = $true
            $clientStart.RedirectStandardOutput = $true
            foreach ($argument in @('--config', $nativeData, '--rnsconfig', $nativeRns)) { $clientStart.ArgumentList.Add($argument) }
            $client = [Diagnostics.Process]::new()
            $client.StartInfo = $clientStart
            $null = $client.Start()
            $clientPending = $null
            $secondAddressLine = & $waitNomadNetOutput $client.StandardOutput ([ref]$clientPending) `
                'LXMF delivery address:\s*([0-9a-f]{32})' 60 'Restarted native NomadNet'
            $secondAddress = [regex]::Match($secondAddressLine, 'LXMF delivery address:\s*([0-9a-f]{32})').Groups[1].Value
            $secondAddress | Should -Be $firstRun.Address
            $hostLine = & $waitNomadNetOutput $client.StandardOutput ([ref]$clientPending) `
                'Hosted node address:\s*([0-9a-f]{32})' 30 'Native hosted node'
            $hostAddress = [regex]::Match($hostLine, '[0-9a-f]{32}').Value

            $upstream.StandardInput.WriteLine("request $hostAddress /page/index.mu")
            $upstream.StandardInput.Flush()
            $responseLine = & $waitNomadNetOutput $upstream.StandardOutput ([ref]$upstreamPending) `
                '"event"\s*:\s*"response"' 90 'Reference request to native page'
            (($responseLine.Substring(2) | ConvertFrom-Json).text) | Should -Match 'NATIVE_HOST_PAGE_OK'
            $upstream.StandardInput.WriteLine("request $hostAddress /file/large.bin")
            $upstream.StandardInput.Flush()
            $responseLine = & $waitNomadNetOutput $upstream.StandardOutput ([ref]$upstreamPending) `
                '"event"\s*:\s*"response"' 120 'Reference request to native large file'
            $response = $responseLine.Substring(2) | ConvertFrom-Json
            $response.size | Should -Be 2200000
            $response.sha256 | Should -Be $expectedLargeHash.ToLowerInvariant()

            $upstream.StandardInput.WriteLine("send-file $secondAddress $(Join-Path $files 'large.bin')")
            $upstream.StandardInput.Flush()
            $null = & $waitNomadNetOutput $upstream.StandardOutput ([ref]$upstreamPending) `
                '"event"\s*:\s*"delivered"' 120 'Reference attachment delivery'
            $deadline = [DateTime]::UtcNow.AddSeconds(30)
            $nativeAttachment = $null
            while ([DateTime]::UtcNow -lt $deadline -and $null -eq $nativeAttachment) {
                $nativeAttachment = Get-ChildItem (Join-Path $nativeData 'attachments') -Filter '0.bin' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($null -eq $nativeAttachment) { Start-Sleep -Milliseconds 100 }
            }
            $nativeAttachment | Should -Not -BeNullOrEmpty
            (Get-FileHash $nativeAttachment.FullName -Algorithm SHA256).Hash | Should -Be $expectedLargeHash
            $client.StandardInput.WriteLine("attach $($ready.delivery) $(Join-Path $nativeData 'downloads\large.bin')")
            $client.StandardInput.Flush()
            $null = & $waitNomadNetOutput $client.StandardOutput ([ref]$clientPending) 'Message queued:' 30 'Native attachment send'
            $deadline = [DateTime]::UtcNow.AddSeconds(120)
            $upstreamAttachment = $null
            while ([DateTime]::UtcNow -lt $deadline -and $null -eq $upstreamAttachment) {
                $upstreamAttachment = Get-ChildItem (Join-Path $nodeConfig 'storage\attachments') -Filter 'file_0' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($null -eq $upstreamAttachment) { Start-Sleep -Milliseconds 100 }
            }
            $upstreamAttachment | Should -Not -BeNullOrEmpty
            (Get-FileHash $upstreamAttachment.FullName -Algorithm SHA256).Hash | Should -Be $expectedLargeHash
            $client.StandardInput.WriteLine("paper $($ready.delivery) native-to-upstream-paper")
            $client.StandardInput.Flush()
            $paperLine = & $waitNomadNetOutput $client.StandardOutput ([ref]$clientPending) 'lxm://[A-Za-z0-9_-]+' 30 'Native paper generation'
            $uri = [regex]::Match($paperLine, 'lxm://[A-Za-z0-9_-]+').Value
            $upstream.StandardInput.WriteLine("ingest $uri")
            $upstream.StandardInput.Flush()
            $ingestedLine = & $waitNomadNetOutput $upstream.StandardOutput ([ref]$upstreamPending) '"event"\s*:\s*"ingested"' 30 'Upstream paper ingestion'
            (($ingestedLine.Substring(2) | ConvertFrom-Json).accepted) | Should -BeTrue
            $upstream.StandardInput.WriteLine("paper $secondAddress upstream-to-native-paper")
            $upstream.StandardInput.Flush()
            $paperLine = & $waitNomadNetOutput $upstream.StandardOutput ([ref]$upstreamPending) '"event"\s*:\s*"paper"' 30 'Upstream paper generation'
            $uri = ($paperLine.Substring(2) | ConvertFrom-Json).uri
            $client.StandardInput.WriteLine("ingest $uri")
            $client.StandardInput.Flush()
            $null = & $waitNomadNetOutput $client.StandardOutput ([ref]$clientPending) 'Paper ingest: LocalDelivery' 30 'Native paper ingestion'

            $upstream.StandardInput.WriteLine("send $secondAddress upstream-to-native-durable")
            $upstream.StandardInput.Flush()
            $queuedLine = & $waitNomadNetOutput $upstream.StandardOutput ([ref]$upstreamPending) `
                '"event"\s*:\s*"queued"' 30 'Upstream LXMF send'
            (($queuedLine.Substring(2) | ConvertFrom-Json).event) | Should -Be 'queued'
            $null = & $waitNomadNetOutput $upstream.StandardOutput ([ref]$upstreamPending) `
                '"event"\s*:\s*"delivered"' 90 'Upstream LXMF delivery'
            $messagePath = Join-Path $nativeData 'state.json'
            $deadline = [DateTime]::UtcNow.AddSeconds(60)
            $persisted = $false
            while ([DateTime]::UtcNow -lt $deadline -and -not $persisted) {
                if (Test-Path $messagePath) {
                    $state = [IO.File]::ReadAllText($messagePath) | ConvertFrom-Json
                    $persisted = @($state.messages | Where-Object { $_.content -eq 'upstream-to-native-durable' }).Count -gt 0
                }
                if (-not $persisted) { Start-Sleep -Milliseconds 250 }
            }
            $persisted | Should -BeTrue

            $client.StandardInput.WriteLine('quit')
            $client.StandardInput.Flush()
            $clientOutputTask = $client.StandardOutput.ReadToEndAsync()
            $client.WaitForExit(30000) | Should -BeTrue
            $client.Dispose()
            $client = $null
            $clientOutputTask.GetAwaiter().GetResult() | Out-Null

            $reloaded = & $startNative @("messages $($ready.delivery)", 'quit')
            $reloaded.Address | Should -Be $firstRun.Address
            $reloaded.Output | Should -Match 'upstream-to-native-durable'
            $upstream.StandardInput.WriteLine('quit')
            $upstream.StandardInput.Flush()
            $upstream.WaitForExit(10000) | Should -BeTrue
            $upstream.Dispose()
            $upstream = $null
        }
        finally {
            foreach ($process in @($client, $transport, $upstream, $bootstrap)) {
                if ($null -ne $process) {
                    if (-not $process.HasExited) {
                        try { $process.Kill(); $process.WaitForExit(5000) } catch {}
                    }
                    $process.Dispose()
                }
            }
            if ((Test-Path $work) -and $env:DOTRETICULUM_NOMADNET_KEEP_WORK -ne '1') { Remove-Item $work -Recurse -Force }
        }
    }
}
