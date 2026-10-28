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

        $directory = Join-Path (Get-Location) 'artifacts\nomadnet-pester'
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

        $work = Join-Path (Get-Location) 'artifacts\nomadnet-terminal-pester'
        if (Test-Path $work) { Remove-Item $work -Recurse -Force }
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

        $work = Join-Path $root 'artifacts\nomadnet-live-pester'
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
            Join-Path $root 'artifacts\nomadnet\nomadnet-source'
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
            Join-Path $root 'artifacts\nomadnet-upstream-live'
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
            "    target_port = $port"
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
            [IO.File]::WriteAllText($nodeAppConfig, $configuration, [Text.UTF8Encoding]::new($false))
            $pages = Join-Path $nodeConfig 'storage\pages'
            $files = Join-Path $nodeConfig 'storage\files'
            $null = New-Item $pages -ItemType Directory -Force
            $null = New-Item $files -ItemType Directory -Force
            [IO.File]::WriteAllText((Join-Path $pages 'readme.mu'), "# Live page`n`nNOMADNET_UPSTREAM_PAGE_OK", [Text.UTF8Encoding]::new($false))
            [IO.File]::WriteAllText((Join-Path $files 'live.txt'), 'UPSTREAM_FILE_012345', [Text.UTF8Encoding]::new($false))

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
                $pending = $null
                $addressLine = & $waitNomadNetOutput $process.StandardOutput ([ref]$pending) `
                    'LXMF delivery address:\s*([0-9a-f]{32})' 60 'Native NomadNet'
                $address = [regex]::Match($addressLine, 'LXMF delivery address:\s*([0-9a-f]{32})').Groups[1].Value
                foreach ($command in $commands) { $process.StandardInput.WriteLine($command) }
                $process.StandardInput.Flush()
                $outputTask = $process.StandardOutput.ReadToEndAsync()
                if (-not $process.WaitForExit(180000)) { throw 'Native NomadNet did not exit after its terminal commands.' }
                $output = $outputTask.GetAwaiter().GetResult()
                return [PSCustomObject]@{ Address = $address; Output = $output }
            }.GetNewClosure()

            $firstRun = & $startNative @(
                "browse $($ready.node) /page/readme.mu",
                "fetch $($ready.node) /file/live.txt live.txt",
                "send $($ready.delivery) native-to-upstream-nomadnet",
                'quit'
            )
            $upstream.StandardInput.WriteLine('stats')
            $upstream.StandardInput.Flush()
            $statsLine = & $waitNomadNetOutput $upstream.StandardOutput ([ref]$upstreamPending) `
                '"event"\s*:\s*"stats"' 10 'Upstream NomadNet request counters'
            $stats = $statsLine.Substring(2) | ConvertFrom-Json
            $stats.served_pages | Should -Be 1
            $stats.served_files | Should -Be 1
            if ($firstRun.Output -notmatch 'NOMADNET_UPSTREAM_PAGE_OK') {
                $logPath = Join-Path $nodeConfig 'logfile'
                $log = if (Test-Path $logPath) { Get-Content $logPath -Tail 30 | Out-String } else { '(no upstream log file)' }
                throw "The upstream page response was not rendered. Node state: $($stats | ConvertTo-Json -Compress). Log: $log Client output: $($firstRun.Output)"
            }
            $firstRun.Output | Should -Match 'NOMADNET_UPSTREAM_PAGE_OK'
            $firstRun.Output | Should -Match 'Saved 20 bytes'
            [IO.File]::ReadAllText((Join-Path $nativeData 'downloads\live.txt')) | Should -Be 'UPSTREAM_FILE_012345'

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
            foreach ($process in @($client, $upstream, $bootstrap)) {
                if ($null -ne $process) {
                    if (-not $process.HasExited) {
                        try { $process.Kill(); $process.WaitForExit(5000) } catch {}
                    }
                    $process.Dispose()
                }
            }
            if (Test-Path $work) { Remove-Item $work -Recurse -Force }
        }
    }
}
