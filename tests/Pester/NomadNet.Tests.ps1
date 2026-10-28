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
            while ([DateTime]::UtcNow -lt $deadline -and -not $replySeen) {
                $appProcess.StandardInput.WriteLine("messages $peerHash")
                $appProcess.StandardInput.Flush()
                $pollDeadline = [DateTime]::UtcNow.AddSeconds(2)
                while ([DateTime]::UtcNow -lt $pollDeadline -and -not $replySeen) {
                    $line = $appProcess.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult()
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
