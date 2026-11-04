#Requires -Version 7.0
BeforeAll {
    function Start-ConsolePeer {
        param([string] $File, [string[]] $Arguments, [string] $Name)
        $start = [Diagnostics.ProcessStartInfo]::new($File)
        $start.UseShellExecute = $false
        $start.RedirectStandardInput = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
        $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
        $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
        foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::Start($start)
        [pscustomobject]@{ Process=$process; Name=$Name; Lines=[Collections.Generic.List[string]]::new()
            Read=$process.StandardOutput.ReadLineAsync(); Errors=$process.StandardError.ReadToEndAsync() }
    }
    function Send-ConsoleLine($Peer, [string] $Line) {
        $Peer.Process.StandardInput.WriteLine($Line)
        $Peer.Process.StandardInput.Flush()
    }
    function Read-ConsoleLines($Peer) {
        while ($Peer.Read -and $Peer.Read.IsCompleted) {
            $line = $Peer.Read.GetAwaiter().GetResult()
            if ($null -eq $line) { $Peer.Read=$null; break }
            if ($Peer.Lines.Count -ge 20000) { throw 'Excessive console output.' }
            $Peer.Lines.Add($line)
            $Peer.Read=$Peer.Process.StandardOutput.ReadLineAsync()
        }
    }
    function Wait-ConsoleLine($Peer, [string] $Pattern, [int] $After = 0, [int] $Seconds = 30) {
        $watch=[Diagnostics.Stopwatch]::StartNew()
        do {
            Read-ConsoleLines $Peer
            for ($i=$After; $i -lt $Peer.Lines.Count; $i++) {
                if ($Peer.Lines[$i] -match $Pattern) { return $Peer.Lines[$i] }
            }
            if ($Peer.Process.HasExited -and !$Peer.Read) { break }
            Start-Sleep -Milliseconds 20
        } while ($watch.Elapsed.TotalSeconds -lt $Seconds)
        $errors=if ($Peer.Errors.IsCompleted) { $Peer.Errors.GetAwaiter().GetResult() } else { '(running)' }
        throw "Missing $Pattern from $($Peer.Name).`n$($Peer.Lines -join "`n")`n$errors"
    }
    function Invoke-ConsoleLine($Peer, [string] $Line, [string] $Pattern) {
        Read-ConsoleLines $Peer
        $after=$Peer.Lines.Count
        Send-ConsoleLine $Peer $Line
        Wait-ConsoleLine $Peer $Pattern $after
    }
    function Stop-ConsolePeer($Peer, [switch] $Abrupt) {
        if (!$Peer) { return }
        try {
            if (!$Peer.Process.HasExited) {
                if ($Abrupt) { $Peer.Process.Kill($true) }
                else {
                    Send-ConsoleLine $Peer 'quit'
                    $Peer.Process.StandardInput.Close()
                    if (!$Peer.Process.WaitForExit(10000)) { $Peer.Process.Kill($true); throw 'Console did not stop in ten seconds.' }
                }
            }
            $Peer.Process.WaitForExit()
            while ($Peer.Read) {
                $line=$Peer.Read.GetAwaiter().GetResult()
                if ($null -eq $line) { $Peer.Read=$null; break }
                if ($Peer.Lines.Count -ge 20000) { throw 'Excessive console output.' }
                $Peer.Lines.Add($line)
                $Peer.Read=$Peer.Process.StandardOutput.ReadLineAsync()
            }
            $errors=$Peer.Errors.GetAwaiter().GetResult()
            $Peer.Lines -join "`n" | Set-Content (Join-Path $script:CaseRoot ($Peer.Name+'.stdout')) -Encoding utf8NoBOM
            $errors | Set-Content (Join-Path $script:CaseRoot ($Peer.Name+'.stderr')) -Encoding utf8NoBOM
            if (!$Abrupt) { $Peer.Process.ExitCode | Should -Be 0 }
        }
        finally { $Peer.Process.Dispose() }
    }
    function Initialize-ConsoleCase([string] $Name) {
        if ($env:DOTRETICULUM_SIDEBAND_CONSOLE -ne '1') {
            Set-ItResult -Skipped -Because 'Set DOTRETICULUM_SIDEBAND_CONSOLE=1 with the selected frozen native executable and SHA-256.'
            return $false
        }
        $script:Binary=$env:DOTRETICULUM_SIDEBAND_BINARY
        if (!$script:Binary -or !(Test-Path -LiteralPath $script:Binary -PathType Leaf)) { throw 'Select DOTRETICULUM_SIDEBAND_BINARY.' }
        $hash=(Get-FileHash -LiteralPath $script:Binary).Hash.ToLowerInvariant()
        $hash | Should -Be $env:DOTRETICULUM_SIDEBAND_CONSOLE_SHA256
        $result=if ($env:DOTRETICULUM_SIDEBAND_CONSOLE_RESULTS) { $env:DOTRETICULUM_SIDEBAND_CONSOLE_RESULTS } else { Join-Path (Get-Location) 'artifacts\tests\sideband-console' }
        $script:CaseRoot=Join-Path $result ($Name+'-'+[Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force $script:CaseRoot | Out-Null
        return $true
    }
    function New-ConsoleConfig([string] $Name, [int] $Port = 0, [switch] $Server) {
        $directory=Join-Path $script:CaseRoot $Name
        New-Item -ItemType Directory -Force $directory | Out-Null
        $text="[reticulum]`n share_instance = no`n enable_transport = no`n`n[logging]`n loglevel = 0`n`n[interfaces]`n"
        if ($Port) {
            $text += if ($Server) { " [[Console]]`n type = TCPServerInterface`n enabled = yes`n listen_ip = 127.0.0.1`n listen_port = $Port`n" }
                else { " [[Console]]`n type = TCPClientInterface`n enabled = yes`n target_host = 127.0.0.1`n target_port = $Port`n" }
        }
        Set-Content -LiteralPath (Join-Path $directory 'config') -Value $text -Encoding utf8NoBOM
        return $directory
    }
    function Start-NativeConsole([string] $Name, [string] $Profile, [string] $Config, [switch] $Verbose) {
        $arguments=@('--daemon','--interactive','--synthetic-audio','--config',$Profile,'--rnsconfig',$Config)
        if ($Verbose) { $arguments+='--verbose' }
        $peer=Start-ConsolePeer $script:Binary $arguments $Name
        $ready=Wait-ConsoleLine $peer '^READY identity='
        Add-Member -InputObject $peer -NotePropertyName Delivery -NotePropertyValue ([regex]::Match($ready,'delivery=([0-9a-f]{32})').Groups[1].Value)
        Add-Member -InputObject $peer -NotePropertyName Identity -NotePropertyValue ([regex]::Match($ready,'identity=([0-9a-f]{32})').Groups[1].Value)
        return $peer
    }
    function New-ConsolePort {
        $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
        try { $listener.Start(); return ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
        finally { $listener.Stop() }
    }
    function Wait-ConsoleMessage($Peer, [string] $Hash, [string] $Text) {
        $watch=[Diagnostics.Stopwatch]::StartNew()
        do {
            Read-ConsoleLines $Peer
            if (@($Peer.Lines | Where-Object { $_ -eq $Text }).Count) { return }
            Send-ConsoleLine $Peer "conv msgs $Hash 32"
            Start-Sleep -Milliseconds 100
            Read-ConsoleLines $Peer
            if (@($Peer.Lines | Where-Object { $_ -eq $Text }).Count) { return }
        } while ($watch.Elapsed.TotalSeconds -lt 45)
        throw "Missing message $Text at $($Peer.Name)."
    }
    function Wait-ConsoleAnnouncement($Peer, [string] $Hash, [string] $Name) {
        Read-ConsoleLines $Peer
        $after=$Peer.Lines.Count
        $pattern='^'+[regex]::Escape($Hash)+' '+[regex]::Escape($Name)+' '
        $watch=[Diagnostics.Stopwatch]::StartNew()
        do {
            Send-ConsoleLine $Peer 'net announces'
            Start-Sleep -Milliseconds 100
            Read-ConsoleLines $Peer
            if (@($Peer.Lines | Select-Object -Skip $after | Where-Object { $_ -match $pattern }).Count) { return }
        } while ($watch.Elapsed.TotalSeconds -lt 30)
        throw "Missing actual announcement $Name at $($Peer.Name)."
    }
    function Invoke-PrimaryCli($Peer, [string] $Line) {
        Read-ConsoleLines $Peer
        $after=$Peer.Lines.Count
        $encoded=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Line))
        Send-ConsoleLine $Peer "cli $encoded"
        $raw=Wait-ConsoleLine $Peer '^@@.*"event": "console"' $after
        $result=$raw.Substring(2) | ConvertFrom-Json
        $result.error | Should -BeNullOrEmpty
        $result.result | Should -Not -Match 'Unknown destination|Failed to'
        return $result
    }
}

Describe 'Sideband frozen desktop/headless interactive console' -Tag 'Native','SidebandConsole' {
    It 'T321 imports private profile and immutable map snapshots at supported long Unicode paths and preserves identity across restart' {
        if (!(Initialize-ConsoleCase native-snapshots)) { return }
        $repository=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $config=New-ConsoleConfig snapshot-rns
        $peer=$null
        try {
            $peer=Start-NativeConsole snapshot-identity (Join-Path $script:CaseRoot identity-profile) $config
            $delivery=$peer.Delivery
            Stop-ConsolePeer $peer
            $peer=$null
            $identities=@(Get-ChildItem -LiteralPath $config -File -Recurse -Filter sideband)
            $identities.Count | Should -Be 1
            $minimum=if ($IsWindows) { 600 } else { 300 }
            $root=$script:CaseRoot
            while ($root.Length -le $minimum) { $root=Join-Path $root ('p'*60) }
            $root=Join-Path $root 'escaped # % λ'
            $root.Length | Should -BeGreaterThan $minimum
            $primary=Join-Path $root primary
            New-Item -ItemType Directory -Force $primary | Out-Null
            $fixture=Get-Content (Join-Path $repository 'tests\Sideband.Tests\Fixtures\sideband-profile.json') -Raw | ConvertFrom-Json
            [IO.File]::WriteAllBytes((Join-Path $primary sideband.db), [Convert]::FromBase64String($fixture.emptyDatabase))
            [IO.File]::WriteAllBytes((Join-Path $primary sideband_config), [Convert]::FromBase64String($fixture.configuration))
            Copy-Item -LiteralPath $identities[0].FullName -Destination (Join-Path $primary primary_identity)
            $before=@(Get-ChildItem $primary -File | Sort-Object Name | Get-FileHash | ForEach-Object Hash)
            $imported=Join-Path $root imported
            & $script:Binary --import-profile $primary --data-dir $imported *> (Join-Path $script:CaseRoot import-profile.log)
            $LASTEXITCODE | Should -Be 0
            (Get-Content (Join-Path $imported state.json) -Raw | ConvertFrom-Json).OwnDeliveryHash | Should -Be $delivery
            & $script:Binary --import-map (Join-Path $repository 'tests\Sideband.Tests\Fixtures\sideband-map.mbtiles') --data-dir $imported *> (Join-Path $script:CaseRoot import-map.log)
            $LASTEXITCODE | Should -Be 0
            Test-Path -LiteralPath (Join-Path $imported 'maps\offline.mbtiles') | Should -BeTrue
            $privateConfig=Join-Path $imported reticulum
            Set-Content -LiteralPath (Join-Path $privateConfig config) -Value "[reticulum]`n share_instance = no`n enable_transport = no`n[interfaces]`n" -Encoding utf8NoBOM
            $peer=Start-NativeConsole snapshot-restarted $imported $privateConfig
            $peer.Delivery | Should -Be $delivery
            Stop-ConsolePeer $peer
            $peer=$null
            $after=@(Get-ChildItem $primary -File | Sort-Object Name | Get-FileHash | ForEach-Object Hash)
            ($after -join '|') | Should -Be ($before -join '|')
        }
        finally { Stop-ConsolePeer $peer }
    }

    It 'activates quoted conversation, settings, telemetry and map workflows and reloads persistent state' {
        if (!(Initialize-ConsoleCase offline)) { return }
        (& $script:Binary --help) -join "`n" | Should -Match 'sideband'
        $LASTEXITCODE | Should -Be 0
        $profile=Join-Path $script:CaseRoot profile
        $config=New-ConsoleConfig rns
        $peer=$null
        try {
            $peer=Start-NativeConsole first $profile $config
            Invoke-ConsoleLine $peer 'log' '^Current log level: 0 \(squelch\)' | Out-Null
            foreach ($level in 0..7) {
                Invoke-ConsoleLine $peer "log $level" "^Log level set to $level " | Out-Null
            }
            Send-ConsoleLine $peer 'log 8'
            Invoke-ConsoleLine $peer 'log' '^Current log level: 7 \(extreme\)' | Out-Null
            Invoke-ConsoleLine $peer "conv create 0123456789abcdef0123456789abcdef 'Quoted Ω 雪'" '^Conversation created' | Out-Null
            Invoke-ConsoleLine $peer "conv trust 'Quoted Ω 雪'" '^OK conv trust' | Out-Null
            Invoke-ConsoleLine $peer "config set general display_name 'Native console Ω'" '^general display_name = Native console Ω' | Out-Null
            Invoke-ConsoleLine $peer 'config set sensors information true' '^sensors information = true' | Out-Null
            Invoke-ConsoleLine $peer "config set sensors information_text 'literal ; `$(not-a-shell)'" '^sensors information_text = literal' | Out-Null
            Invoke-ConsoleLine $peer 'telemetry on' '^Telemetry collection enabled' | Out-Null
            Invoke-ConsoleLine $peer 'telemetry' 'information \(Available\).*not-a-shell' | Out-Null
            Invoke-ConsoleLine $peer 'config set map lat 51.5' '^map lat = 51.5' | Out-Null
            Invoke-ConsoleLine $peer 'config set map cluster false' '^map cluster = false' | Out-Null
            Invoke-ConsoleLine $peer 'config reload' '^Configuration reloaded' | Out-Null
            Invoke-ConsoleLine $peer 'status' '^Sideband Status' | Out-Null
            Invoke-ConsoleLine $peer 'voice status' '^STATUS voice=disabled' | Out-Null
            Stop-ConsolePeer $peer
            $peer.Errors.GetAwaiter().GetResult() | Should -Match 'ERROR Invalid log level: 8'
            $peer=$null
            $state=Get-Content (Join-Path $profile 'state.json') -Raw | ConvertFrom-Json
            $state.DisplayName | Should -Be 'Native console Ω'
            $state.Contacts.Count | Should -Be 1
            $state.Contacts[0].Trusted | Should -BeTrue
            $peer=Start-NativeConsole second $profile $config
            Invoke-ConsoleLine $peer 'log' '^Current log level: 0 \(squelch\)' | Out-Null
            Invoke-ConsoleLine $peer 'config get general display_name' '^general display_name = Native console Ω' | Out-Null
            Invoke-ConsoleLine $peer 'config get map cluster' '^map cluster = false' | Out-Null
            Invoke-ConsoleLine $peer "conv delete 'Quoted Ω 雪'" '^OK conv delete' | Out-Null
            Stop-ConsolePeer $peer
            $peer=$null
            (Get-Content (Join-Path $profile 'state.json') -Raw | ConvertFrom-Json).Contacts.Count | Should -Be 0
            $peer=Start-NativeConsole verbose $profile $config -Verbose
            Invoke-ConsoleLine $peer 'log' '^Current log level: 6 \(debug\)' | Out-Null
            Stop-ConsolePeer $peer
            $peer=$null
        }
        finally { Stop-ConsolePeer $peer }
    }

    It 'rejects malformed, huge and unsupported commands, cancels owned pending work and exits at EOF' {
        if (!(Initialize-ConsoleCase boundaries)) { return }
        $peer=$null
        try {
            $peer=Start-NativeConsole boundaries (Join-Path $script:CaseRoot profile) (New-ConsoleConfig rns)
            foreach ($line in @("conv create 'unterminated", ('x'*8193), 'raw sideband.config', 'plugins reload',
                'config set connect tcp true','config set lxmf sync_limit 3','telemetry list -1','status extra')) {
                Send-ConsoleLine $peer $line
            }
            Invoke-ConsoleLine $peer 'conv send 0123456789abcdef0123456789abcdef pending' '^QUEUED conv send' | Out-Null
            Invoke-ConsoleLine $peer 'status' '^Sideband Status' | Out-Null
            Invoke-ConsoleLine $peer 'cancel' '^OK operations cancelled' | Out-Null
            Invoke-ConsoleLine $peer 'help conv sync' '^conv sync status' | Out-Null
            $peer.Process.StandardInput.Close()
            $peer.Process.WaitForExit(10000) | Should -BeTrue
            $peer.Process.ExitCode | Should -Be 0
            $errors=$peer.Errors.GetAwaiter().GetResult()
            $errors | Should -Match 'Unbalanced quotes'
            $errors | Should -Match '8,192 characters'
            $errors | Should -Match 'not supported'
            $errors | Should -Match 'not activated'
            $errors | Should -Match 'Too many arguments'
            Stop-ConsolePeer $peer
            $peer=$null
        }
        finally { Stop-ConsolePeer $peer }
    }

    It 'exchanges actual native messages both ways and preserves identity, trust and history across clean and abrupt restart' {
        if (!(Initialize-ConsoleCase native-pair)) { return }
        $port=New-ConsolePort
        $server=$null; $client=$null
        $serverProfile=Join-Path $script:CaseRoot server
        $clientProfile=Join-Path $script:CaseRoot client
        $serverConfig=New-ConsoleConfig server-rns $port -Server
        $clientConfig=New-ConsoleConfig client-rns $port
        try {
            $server=Start-NativeConsole server $serverProfile $serverConfig
            $client=Start-NativeConsole client $clientProfile $clientConfig
            Invoke-ConsoleLine $client 'log 7' '^Log level set to 7 \(extreme\)' | Out-Null
            Invoke-ConsoleLine $client 'log stream' '^Streaming native runtime reports' | Out-Null
            $streamAfter=$client.Lines.Count
            Invoke-ConsoleLine $server 'conv announce' '^LXMF announce sent' | Out-Null
            Invoke-ConsoleLine $client 'conv announce' '^LXMF announce sent' | Out-Null
            Invoke-ConsoleLine $client "conv create $($server.Delivery) ''" '^Conversation created' | Out-Null
            $automatic=Get-Content (Join-Path $clientProfile 'state.json') -Raw | ConvertFrom-Json
            @($automatic.Contacts | Where-Object DeliveryHash -EQ $server.Delivery)[0].NameIsUserDefined | Should -BeFalse
            Invoke-ConsoleLine $client "conv name $($server.Delivery) 'Native server'" '^OK conv name' | Out-Null
            Invoke-ConsoleLine $client "conv trust 'Native server'" '^OK conv trust' | Out-Null
            Invoke-ConsoleLine $server "config set general display_name 'Changed remote'" '^general display_name = Changed remote' | Out-Null
            Start-Sleep -Milliseconds 1100
            Invoke-ConsoleLine $server 'conv announce' '^LXMF announce sent' | Out-Null
            Wait-ConsoleAnnouncement $client $server.Delivery 'Changed remote'
            Invoke-ConsoleLine $server 'voice enable' '^OK voice enable' | Out-Null
            Invoke-ConsoleLine $client 'voice enable' '^OK voice enable' | Out-Null
            Invoke-ConsoleLine $server 'voice announce' '^OK voice announce' | Out-Null
            Invoke-ConsoleLine $client 'voice announce' '^OK voice announce' | Out-Null
            Invoke-ConsoleLine $client "voice dial $($server.Identity) 64" '^OK voice dial queued' | Out-Null
            $deadline=[DateTime]::UtcNow.AddSeconds(30)
            do {
                $status=Invoke-ConsoleLine $server 'voice status' '^STATUS '
                if ($status -match 'voice=Ringing') { break }
                Start-Sleep -Milliseconds 50
            } while ([DateTime]::UtcNow -lt $deadline)
            $status | Should -Match 'voice=Ringing'
            Invoke-ConsoleLine $server 'voice answer' '^OK voice answer' | Out-Null
            foreach ($peer in @($server,$client)) {
                $deadline=[DateTime]::UtcNow.AddSeconds(30)
                do {
                    $status=Invoke-ConsoleLine $peer 'voice status' '^STATUS '
                    $match=[regex]::Match($status,'voice=InCall .*rx=(\d+) tx=(\d+)')
                    if ($match.Success -and [long]$match.Groups[1].Value -ge 5 -and [long]$match.Groups[2].Value -ge 5) { break }
                    Start-Sleep -Milliseconds 50
                } while ([DateTime]::UtcNow -lt $deadline)
                $match.Success | Should -BeTrue
                [long]$match.Groups[1].Value | Should -BeGreaterOrEqual 5
                [long]$match.Groups[2].Value | Should -BeGreaterOrEqual 5
            }
            Invoke-ConsoleLine $client 'voice hangup' '^OK voice hangup' | Out-Null
            Invoke-ConsoleLine $client "conv send 'Native server' 'client to server Ω'" '^Message submitted' | Out-Null
            Wait-ConsoleMessage $server $client.Delivery 'client to server Ω'
            Invoke-ConsoleLine $server "conv send $($client.Delivery) 'server to client 雪'" '^Message submitted' | Out-Null
            Wait-ConsoleMessage $client $server.Delivery 'server to client 雪'
            Wait-ConsoleLine $client '^LOG \[[0-7]\] ' $streamAfter | Out-Null
            Invoke-ConsoleLine $client '' '^Log stream ended' | Out-Null
            $hash=$client.Delivery
            Stop-ConsolePeer $client
            $client=Start-NativeConsole client-clean $clientProfile $clientConfig
            $client.Delivery | Should -Be $hash
            Invoke-ConsoleLine $client "conv send 'Native server' 'clean restart'" '^Message submitted' | Out-Null
            Wait-ConsoleMessage $server $hash 'clean restart'
            Stop-ConsolePeer $client -Abrupt
            $client=Start-NativeConsole client-abrupt $clientProfile $clientConfig
            $client.Delivery | Should -Be $hash
            Invoke-ConsoleLine $client "conv send 'Native server' 'abrupt restart'" '^Message submitted' | Out-Null
            Wait-ConsoleMessage $server $hash 'abrupt restart'
            $state=Get-Content (Join-Path $clientProfile 'state.json') -Raw | ConvertFrom-Json
            $savedContact=@($state.Contacts | Where-Object DeliveryHash -EQ $server.Delivery)
            $savedContact.Count | Should -Be 1
            $savedContact[0].Trusted | Should -BeTrue
            $savedContact[0].DisplayName | Should -Be 'Native server'
            $savedContact[0].NameIsUserDefined | Should -BeTrue
            @($state.Messages | Where-Object Content -EQ 'server to client 雪').Count | Should -Be 1
            Invoke-ConsoleLine $client "conv name $($server.Delivery) ''" '^OK conv name' | Out-Null
            $state=Get-Content (Join-Path $clientProfile 'state.json') -Raw | ConvertFrom-Json
            $automatic=@($state.Contacts | Where-Object DeliveryHash -EQ $server.Delivery)
            $automatic[0].DisplayName | Should -Be 'Changed remote'
            $automatic[0].NameIsUserDefined | Should -BeFalse
            $automatic[0].Trusted | Should -BeTrue
            Stop-ConsolePeer $client
            $client=Start-NativeConsole client-automatic $clientProfile $clientConfig
            $client.Delivery | Should -Be $hash
            Invoke-ConsoleLine $server "config set general display_name 'Later remote'" '^general display_name = Later remote' | Out-Null
            Start-Sleep -Milliseconds 1100
            Invoke-ConsoleLine $server 'conv announce' '^LXMF announce sent' | Out-Null
            Wait-ConsoleAnnouncement $client $server.Delivery 'Later remote'
            $deadline=[DateTime]::UtcNow.AddSeconds(10)
            do {
                $state=Get-Content (Join-Path $clientProfile 'state.json') -Raw | ConvertFrom-Json
                $automatic=@($state.Contacts | Where-Object DeliveryHash -EQ $server.Delivery)
                if ($automatic[0].DisplayName -eq 'Later remote') { break }
                Start-Sleep -Milliseconds 50
            } while ([DateTime]::UtcNow -lt $deadline)
            $automatic[0].DisplayName | Should -Be 'Later remote'
            $automatic[0].NameIsUserDefined | Should -BeFalse
            $automatic[0].Trusted | Should -BeTrue
            [ordered]@{ Identity=$hash; Clean=$true; Abrupt=$true; BothDirections=$true
                Audio='Native Opus64 synthetic/discard call, at least five received/transmitted frames at each peer; not physical audio'
                SavedNameSurvivesActualChangedAnnounce=$true
                AutomaticCreationAndClearedNameFollowAnnouncesAfterRestart=$true
                Nondefault='none; startup announce remains enabled, with explicit conversation and LXST announces' } |
                ConvertTo-Json | Set-Content (Join-Path $script:CaseRoot 'traffic-receipt.json') -Encoding utf8NoBOM
        }
        finally { Stop-ConsolePeer $client; Stop-ConsolePeer $server }
    }

    It 'exercises actual primary CLI/core handlers and the frozen native console both ways after clean and abrupt restart' {
        if (!(Initialize-ConsoleCase primary)) { return }
        if ($env:DOTRETICULUM_LIVE -ne '1' -or $env:DOTRETICULUM_SIDEBAND_LIVE -ne '1') {
            Set-ItResult -Skipped -Because 'Set both live opt-ins and the pinned actual Python/RNS/LXMF/LXST/Sideband environment.'
            return
        }
        $port=New-ConsolePort
        $primary=$null; $native=$null
        $profile=Join-Path $script:CaseRoot native
        $config=New-ConsoleConfig native-rns $port
        try {
            $repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
            $primary=Start-ConsolePeer $env:DOTRETICULUM_PYTHON @((Join-Path $repo 'tests\Interop\sideband_console_node.py'),
                '--config',(Join-Path $script:CaseRoot primary),'--source',$env:DOTRETICULUM_SIDEBAND_UPSTREAM,
                '--tcp-server-port',"$port") primary
            $ready=(Wait-ConsoleLine $primary '^@@.*"event": "ready"').Substring(2) | ConvertFrom-Json
            $ready.revision | Should -Be 'd069122af14880e43607aed0b71add4c7806fbe0'
            $native=Start-NativeConsole native $profile $config
            Invoke-PrimaryCli $primary 'conv announce' | Out-Null
            Invoke-ConsoleLine $native 'conv announce' '^LXMF announce sent' | Out-Null
            Invoke-ConsoleLine $native "conv create $($ready.dest) 'Primary'" '^Conversation created' | Out-Null
            Invoke-PrimaryCli $primary "conv create $($native.Delivery) Native" | Out-Null
            Invoke-PrimaryCli $primary "conv trust $($native.Delivery)" | Out-Null
            Invoke-ConsoleLine $native "conv send Primary 'native to primary CLI Ω'" '^Message submitted' | Out-Null
            $deadline=[DateTime]::UtcNow.AddSeconds(45)
            do {
                $messages=Invoke-PrimaryCli $primary 'conv msgs Native'
                if ($messages.result -match 'native to primary CLI Ω') { break }
                Start-Sleep -Milliseconds 100
            } while ([DateTime]::UtcNow -lt $deadline)
            $messages.result | Should -Match 'native to primary CLI Ω'
            Invoke-PrimaryCli $primary "conv send Native 'primary to native CLI 雪'" | Out-Null
            Wait-ConsoleMessage $native $ready.dest 'primary to native CLI 雪'
            $hash=$native.Delivery
            foreach ($abrupt in @($false,$true)) {
                Stop-ConsolePeer $native -Abrupt:$abrupt
                $native=Start-NativeConsole ("native-restart-"+$abrupt) $profile $config
                $native.Delivery | Should -Be $hash
                Invoke-ConsoleLine $native "conv send Primary 'native restart $abrupt'" '^Message submitted' | Out-Null
                $deadline=[DateTime]::UtcNow.AddSeconds(45)
                do {
                    $messages=Invoke-PrimaryCli $primary 'conv msgs Native'
                    if ($messages.result -match "native restart $abrupt") { break }
                    Start-Sleep -Milliseconds 100
                } while ([DateTime]::UtcNow -lt $deadline)
                $messages.result | Should -Match "native restart $abrupt"
                Invoke-PrimaryCli $primary "conv send Native 'primary restart $abrupt'" | Out-Null
                Wait-ConsoleMessage $native $ready.dest "primary restart $abrupt"
            }
            [ordered]@{ NativeIdentity=$hash; PrimaryRevision=$ready.revision; CoreDigest=$ready.source_sha256
                CliModules=$ready.cli_modules; BothDirections=$true; Clean=$true; Abrupt=$true
                Topology='direct isolated TCP'; Shims='none; context observes terminal-only calls' } |
                ConvertTo-Json -Depth 6 | Set-Content (Join-Path $script:CaseRoot 'traffic-receipt.json') -Encoding utf8NoBOM
        }
        finally { Stop-ConsolePeer $native; Stop-ConsolePeer $primary }
    }
}
