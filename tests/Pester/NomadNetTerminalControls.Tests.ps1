BeforeAll {
    $script:C95Root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $script:C95Evidence = $env:DOTRETICULUM_NOMADNET_TERMINAL_EVIDENCE
    if (-not $script:C95Evidence) {
        $script:C95Evidence = Join-Path $script:C95Root ('artifacts\tests\nomad-terminal-controls\acceptance-' + [guid]::NewGuid().ToString('N'))
    }
    $null = New-Item $script:C95Evidence -ItemType Directory -Force
    $script:C95Children = [Collections.Generic.List[object]]::new()
    function Assert-C95Guard {
        if ($env:DOTRETICULUM_NOMADNET_TERMINAL_CONTROLS -ne '1') { return }
        if (-not $env:DOTRETICULUM_NOMADNET_TERMINAL_CANDIDATE) { throw 'Select the frozen C95 candidate manifest.' }
        $candidate = Get-Content $env:DOTRETICULUM_NOMADNET_TERMINAL_CANDIDATE -Raw | ConvertFrom-Json
        $binary = [IO.Path]::GetFullPath($env:DOTRETICULUM_NOMADNET_BINARY)
        $binary | Should -BeExactly ([IO.Path]::GetFullPath((Join-Path $script:C95Root $candidate.Binary.Path)))
        (Get-FileHash $binary).Hash.ToLowerInvariant() | Should -BeExactly $candidate.Binary.Sha256
        foreach ($source in $candidate.Sources) {
            (Get-FileHash (Join-Path $script:C95Root $source.Path)).Hash.ToLowerInvariant() | Should -BeExactly $source.Sha256
        }
        (& git -C (Join-Path $script:C95Root 'external\DotReticulum') rev-parse HEAD) | Should -BeExactly $candidate.Stack
    }
    function Start-C95([string] $Label, [string] $Executable, [string[]] $Arguments) {
        $info = [Diagnostics.ProcessStartInfo]::new($Executable)
        $info.UseShellExecute = $false
        $info.RedirectStandardInput = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $info.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
        $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
        foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
        foreach ($variable in 'TMPDIR','TMP','TEMP') { $info.Environment[$variable] = $script:C95Evidence }
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $info
        if (-not $process.Start()) { $process.Dispose(); throw 'Selected child did not start.' }
        $child = @{Process=$process; Errors=$process.StandardError.ReadToEndAsync(); Pending=$null;
            Text=[Text.StringBuilder]::new(); Label=$Label}
        $script:C95Children.Add($child)
        return $child
    }
    function Send-C95($Child, [string] $Command) {
        $Child.Process.StandardInput.WriteLine($Command)
        $Child.Process.StandardInput.Flush()
    }
    function Read-C95($Child, [string] $Pattern, [int] $Seconds = 40) {
        $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
        while ([DateTime]::UtcNow -lt $deadline) {
            if ($null -eq $Child.Pending) { $Child.Pending = $Child.Process.StandardOutput.ReadLineAsync() }
            try { $line = $Child.Pending.WaitAsync([TimeSpan]::FromMilliseconds(500)).GetAwaiter().GetResult() }
            catch [TimeoutException] { continue }
            $Child.Pending = $null
            if ($null -eq $line) { throw "$($Child.Label) ended before $Pattern. $($Child.Text)" }
            if ($Child.Text.Length + $line.Length -gt 2000000) { throw 'Child output exceeded2 MiB.' }
            $null = $Child.Text.AppendLine($line)
            if ($line -match $Pattern) { return $line }
            if ($line -match '"event"\s*:\s*"(error|failed|failure)"|(^|nomadnet> )Error:') { throw $line }
        }
        throw "$($Child.Label) timed out before $Pattern. $($Child.Text)"
    }
    function Stop-C95($Child, [bool] $Quit = $true, [int] $Expected = 0) {
        if ($Quit) { Send-C95 $Child quit }
        $Child.Process.StandardInput.Close()
        $tail = $Child.Process.StandardOutput.ReadToEndAsync()
        $null = $Child.Process.WaitForExitAsync().WaitAsync([TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult()
        $null = $Child.Text.Append($tail.GetAwaiter().GetResult())
        $Child.Process.ExitCode | Should -Be $Expected -Because $Child.Errors.GetAwaiter().GetResult()
        return $Child.Text.ToString()
    }
    function Invoke-C95([string] $Label, [string[]] $Arguments, [string[]] $Commands = @(), [int] $Expected = 0) {
        $child = Start-C95 $Label $env:DOTRETICULUM_NOMADNET_BINARY $Arguments
        foreach ($command in $Commands) { Send-C95 $child $command }
        return Stop-C95 $child $false $Expected
    }
    function New-C95Profile([string] $Name, [int] $Port = 0) {
        $root = Join-Path $script:C95Evidence $Name
        $rns = Join-Path $root selected-rns
        $profile = Join-Path $root profile
        $null = New-Item $rns -ItemType Directory -Force
        $interface = if ($Port) {
            "[[NativeLoopback]]`ntype=TCPServerInterface`nenabled=yes`nlisten_ip=127.0.0.1`nlisten_port=$Port`n"
        } else { '' }
        [IO.File]::WriteAllText((Join-Path $rns config),
            "[reticulum]`nshare_instance=no`nenable_transport=no`n[logging]`nloglevel=0`n[interfaces]`n$interface")
        $null = Invoke-C95 "$Name-init" @('--init','--config',$profile)
        $settings = Join-Path $profile settings.json
        $value = Get-Content $settings -Raw | ConvertFrom-Json
        $value.clientAnnounceAtStart = $false
        $value.periodicLxmfSync = $false
        $value.nodePageRefreshIntervalMinutes = 0
        $value.nodeFileRefreshIntervalMinutes = 0
        $value.introTime = 0
        $value | ConvertTo-Json -Depth 10 | Set-Content $settings -Encoding utf8NoBOM
        return @{Root=$root;Profile=$profile;Rns=$rns;Settings=$settings;
            Arguments=@('--config',$profile,'--rnsconfig',$rns)}
    }
}

Describe 'Selected Nomad terminal-control acceptance' -Tag 'TerminalControls' {
    BeforeEach { Assert-C95Guard }
    AfterEach {
        foreach ($child in $script:C95Children) {
            try {
                if (-not $child.Process.HasExited) { $child.Process.Kill($true); $child.Process.WaitForExit() }
                [IO.File]::WriteAllText((Join-Path $script:C95Evidence ($child.Label + '.stdout')), $child.Text.ToString())
                [IO.File]::WriteAllText((Join-Path $script:C95Evidence ($child.Label + '.stderr')), $child.Errors.GetAwaiter().GetResult())
            } finally { $child.Process.Dispose() }
        }
        $script:C95Children.Clear()
        Assert-C95Guard
    }

    It 'T559 actual primary regenerates terminal constructor forms menu names and refresh fixtures byte identically' {
        if ($env:DOTRETICULUM_NOMADNET_TERMINAL_CONTROLS -ne '1') {
            Set-ItResult -Skipped -Because 'Select C95 and actual pinned reference paths.'
            return
        }
        $output = Join-Path $script:C95Evidence regenerated.json
        $child = Start-C95 reference $env:DOTRETICULUM_PYTHON @(
            (Join-Path $script:C95Root 'tests\Interop\GenerateNomadNetTerminalControlVectors.py'),
            '--output',$output,'--observations',(Join-Path $script:C95Evidence reference-imports.json),
            '--work',(Join-Path $script:C95Evidence reference-work))
        $null = Stop-C95 $child $false
        (Get-FileHash $output).Hash | Should -BeExactly (Get-FileHash (
            Join-Path $script:C95Root 'tests\NomadNet.Tests\NomadNetTerminalControlVectors.json')).Hash
    }

    It 'T560 native forms stage cancel save reset activate all ten controls only after same-identity restart' {
        if ($env:DOTRETICULUM_NOMADNET_TERMINAL_CONTROLS -ne '1') {
            Set-ItResult -Skipped -Because 'Select the frozen Windows native C95 candidate.'
            return
        }
        $p = New-C95Profile forms
        $native = Start-C95 forms-initial $env:DOTRETICULUM_NOMADNET_BINARY $p.Arguments
        $identity = [regex]::Match((Read-C95 $native 'LXMF delivery address:'),'[0-9a-f]{32}').Value
        Send-C95 $native 'config primary enable confirm'
        $null = Read-C95 $native 'Configuration source saved'
        Send-C95 $native 'config section textui'
        Send-C95 $native 'config set intro_text "cancelled"'
        Send-C95 $native 'config cancel'
        $null = Read-C95 $native 'Configuration cancelled'
        Send-C95 $native 'config section textui'
        foreach ($field in @('intro_time 0.125','intro_text "Activated title"','theme "light"','colormode "88"',
            'glyphs "plain"','mouse_enabled false','hide_guide true','sanitize_names false','animation_interval 2')) {
            Send-C95 $native "config set $field"
        }
        Send-C95 $native 'config save confirm'
        $null = Read-C95 $native 'Section saved'
        Send-C95 $native 'config section utilities'
        Send-C95 $native 'config set enable_utilities true'
        Send-C95 $native 'config save confirm'
        $null = Read-C95 $native 'Section saved'
        Send-C95 $native menu
        (Read-C95 $native '\[1:Conversations\]') | Should -Match 'Guide\]'
        $null = Stop-C95 $native
        $native = Start-C95 forms-restarted $env:DOTRETICULUM_NOMADNET_BINARY $p.Arguments
        [regex]::Match((Read-C95 $native 'LXMF delivery address:'),'[0-9a-f]{32}').Value | Should -Be $identity
        $null = Read-C95 $native 'Activated title'
        $menu = Read-C95 $native '\[1:Conversations\]'
        $menu | Should -Match '^\* '
        $menu | Should -Match 'Utilities\]'
        $menu | Should -Not -Match 'Guide\]'
        Send-C95 $native 'config section textui'
        Send-C95 $native 'config set animation_interval 0'
        Send-C95 $native 'config save confirm'
        $null = Read-C95 $native 'Error:.*animation interval'
        Send-C95 $native 'config cancel'
        $null = Read-C95 $native 'Configuration cancelled'
        Send-C95 $native 'config section textui'
        Send-C95 $native 'config reset confirm'
        Send-C95 $native 'config save confirm'
        $null = Read-C95 $native 'Section saved'
        $null = Stop-C95 $native
        $reset = Invoke-C95 forms-reset $p.Arguments @('menu','quit')
        $reset | Should -Match 'Nomad Network'
        $reset | Should -Match 'Guide\]'
        $reset | Should -Match 'Utilities\]'
        $reset | Should -Match $identity
    }

    It 'T561 native name rendering changes cosmetic sanitation without modifying persisted contacts' {
        if ($env:DOTRETICULUM_NOMADNET_TERMINAL_CONTROLS -ne '1') {
            Set-ItResult -Skipped -Because 'Select native C95 acceptance.'
            return
        }
        $p = New-C95Profile names
        $hash = '12345678901234567890123456789012'
        $first = Invoke-C95 names-clean $p.Arguments @("contact add $hash ① Ａlice😀",'contacts','quit')
        $first | Should -Match "$hash  1 Alice trust="
        $state = Join-Path $p.Profile state.json
        $stateHash = (Get-FileHash $state).Hash
        $value = Get-Content $p.Settings -Raw | ConvertFrom-Json
        $value.sanitizeNames = $false
        $value | ConvertTo-Json -Depth 10 | Set-Content $p.Settings -Encoding utf8NoBOM
        $second = Invoke-C95 names-original $p.Arguments @('contacts','quit')
        $second | Should -Match "$hash  ① Ａlice😀 trust="
        (Get-FileHash $state).Hash | Should -BeExactly $stateHash
    }

    It 'T562 native first-run guide remains reachable when hidden while utility visibility does not grant authorization' {
        if ($env:DOTRETICULUM_NOMADNET_TERMINAL_CONTROLS -ne '1') {
            Set-ItResult -Skipped -Because 'Select native C95 acceptance.'
            return
        }
        $p = New-C95Profile first-run
        Remove-Item (Join-Path $p.Profile state.json)
        $value = Get-Content $p.Settings -Raw | ConvertFrom-Json
        $value.hideGuide = $true
        $value.enableUtilities = $true
        $value | ConvertTo-Json -Depth 10 | Set-Content $p.Settings -Encoding utf8NoBOM
        $first = Invoke-C95 first-guide $p.Arguments @('guide topics','menu select 4','utility speed status','quit')
        $first | Should -Not -Match 'Guide\]'
        $first | Should -Match 'Utilities\]'
        $first | Should -Match 'Local application guide'
        $first | Should -Match 'Speed sizes:'
        $first | Should -Match 'Speed-test listening=False'
        $second = Invoke-C95 second-guide $p.Arguments @('menu','quit')
        $second | Should -Not -Match 'Local application guide'
        $second | Should -Not -Match 'Error:'
    }

    It 'T563 selected native utility remains functional after terminal-source restart against actual primary probe consumer' -Tag 'Live' {
        if ($env:DOTRETICULUM_NOMADNET_TERMINAL_CONTROLS -ne '1' -or $env:DOTRETICULUM_LIVE -ne '1') {
            Set-ItResult -Skipped -Because 'Select C95 AND live actual-primary direct-loopback utility acceptance.'
            return
        }
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
        $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
        $p = New-C95Profile live $port
        $value = Get-Content $p.Settings -Raw | ConvertFrom-Json
        $value.enableUtilities = $true
        $value.hideGuide = $true
        $value.glyphs = 'plain'
        $value | ConvertTo-Json -Depth 10 | Set-Content $p.Settings -Encoding utf8NoBOM
        $native = Start-C95 live-native $env:DOTRETICULUM_NOMADNET_BINARY $p.Arguments
        $null = Read-C95 $native 'Utilities\]'
        $pythonRns = Join-Path $script:C95Evidence python-rns
        $null = New-Item $pythonRns -ItemType Directory -Force
        [IO.File]::WriteAllText((Join-Path $pythonRns config),
            "[reticulum]`nshare_instance=no`nenable_transport=no`n[interfaces]`n[[Native]]`ntype=TCPClientInterface`nenabled=yes`ntarget_host=127.0.0.1`ntarget_port=$port`n")
        $python = Start-C95 live-primary $env:DOTRETICULUM_PYTHON @('-u',
            (Join-Path $script:C95Root 'tests\Interop\nomadnet_utility_peer.py'),
            '--config',(Join-Path $script:C95Evidence python-profile),'--rnsconfig',$pythonRns)
        $readyLine = Read-C95 $python '"event"\s*:\s*"ready"'
        $ready = $readyLine.Substring($readyLine.IndexOf('@@') + 2) | ConvertFrom-Json
        $ready.revision | Should -Be 'f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2'
        Start-Sleep -Seconds 1
        Send-C95 $python '{"op":"announce"}'
        $null = Read-C95 $python '"event"\s*:\s*"announced"'
        Send-C95 $native "utility probe $($ready.probe_name) $($ready.probe) 16 1"
        $null = Read-C95 $native 'Probe sent=1 received=1 loss=0.0%'
        $null = Read-C95 $python '"event"\s*:\s*"probe-received"'
        Send-C95 $native menu
        (Read-C95 $native '\[1:Conversations\]') | Should -Match 'Utilities\]'
        $null = Stop-C95 $native
        Send-C95 $python '{"op":"quit"}'
        $null = Stop-C95 $python $false
    }
}
