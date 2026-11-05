BeforeAll {
    $script:C92Root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $script:C92Evidence = $env:DOTRETICULUM_NOMADNET_RRC_SETTINGS_EVIDENCE
    if (-not $script:C92Evidence) {
        $script:C92Evidence = Join-Path $script:C92Root ('artifacts\tests\nomad-rrc-settings\acceptance-' + [guid]::NewGuid().ToString('N'))
    }
    $null = New-Item $script:C92Evidence -ItemType Directory -Force
    $script:C92Children = [Collections.Generic.List[object]]::new()
    function Start-C92([string] $Label, [string] $Executable, [string[]] $Arguments) {
        $info = [Diagnostics.ProcessStartInfo]::new($Executable)
        $info.UseShellExecute = $false
        $info.RedirectStandardInput = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $info.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
        $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
        foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
        foreach ($variable in 'TMPDIR','TMP','TEMP') { $info.Environment[$variable] = $script:C92Evidence }
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $info
        if (-not $process.Start()) { $process.Dispose(); throw 'Selected child did not start.' }
        $child = @{Process=$process; Errors=$process.StandardError.ReadToEndAsync(); Pending=$null;
            Text=[Text.StringBuilder]::new(); Label=$Label}
        $script:C92Children.Add($child)
        return $child
    }
    function Send-C92($Child, [string] $Command) {
        $Child.Process.StandardInput.WriteLine($Command)
        $Child.Process.StandardInput.Flush()
    }
    function Read-C92($Child, [string] $Pattern, [int] $Seconds = 60) {
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
            if ($line -match '"event"\s*:\s*"(error|failed)"|(^|nomadnet> )Error:') { throw $line }
        }
        throw "$($Child.Label) timed out before $Pattern. $($Child.Text)"
    }
    function Exit-C92($Child, [int] $Expected = 0, [bool] $Quit = $true) {
        if ($Quit) { Send-C92 $Child quit }
        $Child.Process.StandardInput.Close()
        $tail = $Child.Process.StandardOutput.ReadToEndAsync()
        $null = $Child.Process.WaitForExitAsync().WaitAsync([TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult()
        $null = $Child.Text.Append($tail.GetAwaiter().GetResult())
        $Child.Process.ExitCode | Should -Be $Expected -Because $Child.Errors.GetAwaiter().GetResult()
        return $Child.Text.ToString()
    }
    function Invoke-C92([string] $Label, [string[]] $Arguments, [string[]] $Commands = @(), [int] $Expected = 0) {
        $child = Start-C92 $Label $env:DOTRETICULUM_NOMADNET_BINARY $Arguments
        foreach ($command in $Commands) { Send-C92 $child $command }
        return Exit-C92 $child $Expected $false
    }
    function New-C92Profile([string] $Name) {
        $root = Join-Path $script:C92Evidence $Name
        $rns = Join-Path $root rns
        $profile = Join-Path $root profile
        $null = New-Item $rns -ItemType Directory -Force
        "[reticulum]`nshare_instance=no`n[logging]`nloglevel=0`n[interfaces]`n" |
            Set-Content (Join-Path $rns config) -Encoding utf8NoBOM
        $null = Invoke-C92 "$Name-init" @('--init','--config',$profile)
        $settings = Join-Path $profile settings.json
        $value = Get-Content $settings -Raw | ConvertFrom-Json
        $value.clientAnnounceAtStart = $false
        $value.periodicLxmfSync = $false
        $value.nodePageRefreshIntervalMinutes = 0
        $value.nodeFileRefreshIntervalMinutes = 0
        $value | ConvertTo-Json -Depth 10 | Set-Content $settings -Encoding utf8NoBOM
        return @{Root=$root;Profile=$profile;Rns=$rns;Settings=$settings;Arguments=@('--config',$profile,'--rnsconfig',$rns)}
    }
}

Describe 'Pinned RRC settings operational native acceptance' -Tag 'RrcSettings' {
AfterEach {
    foreach ($child in $script:C92Children) {
        try {
            if (-not $child.Process.HasExited) { $child.Process.Kill($true); $child.Process.WaitForExit() }
            [IO.File]::WriteAllText((Join-Path $script:C92Evidence ($child.Label + '.stdout')), $child.Text.ToString())
            [IO.File]::WriteAllText((Join-Path $script:C92Evidence ($child.Label + '.stderr')), $child.Errors.GetAwaiter().GetResult())
        } finally { $child.Process.Dispose() }
    }
    $script:C92Children.Clear()
}

    It 'T497 independently executes primary defaults history cleanup widgets palette gutters and placeholder inventory' {
        if ($env:DOTRETICULUM_NOMADNET_RRC_SETTINGS -ne '1') {
            Set-ItResult -Skipped -Because 'Select C92 native/reference acceptance and actual pinned Python/primary sources.'
            return
        }
        $output = Join-Path $script:C92Evidence regenerated.json
        $child = Start-C92 reference $env:DOTRETICULUM_PYTHON @(
            (Join-Path $script:C92Root 'tests\Interop\GenerateNomadNetRrcSettingsVectors.py'),
            '--output',$output,'--observations',(Join-Path $script:C92Evidence imports.json),
            '--work',(Join-Path $script:C92Evidence reference))
        $null = Exit-C92 $child 0 $false
        (Get-FileHash $output).Hash | Should -BeExactly (Get-FileHash (
            Join-Path $script:C92Root 'tests\NomadNet.Tests\NomadNetRrcSettingsVectors.json')).Hash
        (Get-Content $output -Raw | ConvertFrom-Json).esotericRuntimeAttributeReads | Should -Be 0
    }

    It 'T498 native custom styles panes global retention forms and identity survive real primary rrcd messages and restart' {
        if ($env:DOTRETICULUM_NOMADNET_RRC_SETTINGS -ne '1' -or -not $IsWindows) {
            Set-ItResult -Skipped -Because 'Select Windows native and pinned actual primary/rrcd acceptance; Linux WSL storage is unavailable.'
            return
        }
        $p = New-C92Profile lifecycle
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
        $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
        foreach ($name in 'hub','python','native') {
            $rns = if ($name -eq 'native') { $p.Rns } else { Join-Path $script:C92Evidence "$name-rns" }
            $null = New-Item $rns -ItemType Directory -Force
            $text = @('[reticulum]','share_instance=no',('enable_transport=' + ($name -eq 'hub')),'[logging]','loglevel=0','[interfaces]')
            if ($name -eq 'hub') {
                $text += @('[[Server]]','type=TCPServerInterface','enabled=yes','listen_ip=127.0.0.1',"listen_port=$port")
            } else {
                $text += @('[[Client]]','type=TCPClientInterface','enabled=yes','target_host=127.0.0.1',"target_port=$port")
            }
            $text | Set-Content (Join-Path $rns config) -Encoding utf8NoBOM
        }
        $settings = Get-Content $p.Settings -Raw | ConvertFrom-Json
        $settings.rrcHistoryPerRoomCap = 3
        $settings.rrcFilterLoadedHistory = $false
        $settings.rrcEphemeralNoticeMinutes = 0.001
        $settings.rrcColorMentionTimestamps = $true
        $settings.rrcNickColors = $true
        $settings.rrcJustifyMessages = $true
        $settings.rrcSpaceMessages = $true
        $settings.rrcShowGutters = $false
        $settings.rrcMentionColor = 'ABCDEF'
        $settings.rrcNickColorsTheme = @('112233','445566','778899')
        $settings.rrcEnableEsoterics = $true
        $settings.rrcRenderMarkdown = $false
        $settings.rrcRenderMicron = $false
        $settings | ConvertTo-Json -Depth 10 | Set-Content $p.Settings -Encoding utf8NoBOM
        $primary = Join-Path $script:C92Evidence python
        $bootstrap = Start-C92 bootstrap $env:DOTRETICULUM_PYTHON @('-u','-m','nomadnet.nomadnet',
            '--daemon','--console','--config',$primary,'--rnsconfig',(Join-Path $script:C92Evidence python-rns))
        $config = Join-Path $primary config
        $deadline = [DateTime]::UtcNow.AddSeconds(60)
        while (-not (Test-Path $config) -and [DateTime]::UtcNow -lt $deadline) {
            if ($bootstrap.Process.HasExited) { throw 'Actual primary bootstrap failed.' }
            Start-Sleep -Milliseconds 100
        }
        Test-Path $config | Should -BeTrue
        Start-Sleep -Seconds 1
        $bootstrap.Process.Kill($true); $bootstrap.Process.WaitForExit()
        [IO.File]::WriteAllText($config,[IO.File]::ReadAllText($config).Replace('enable_node = no','enable_node = yes'))
        $hub = Start-C92 hub $env:DOTRETICULUM_PYTHON @('-u',(Join-Path $script:C92Root 'tests\Interop\nomadnet_rrc_hub.py'),
            '--config',(Join-Path $script:C92Evidence hub),'--rnsconfig',(Join-Path $script:C92Evidence hub-rns))
        $ready = ((Read-C92 $hub '"event"\s*:\s*"hub-ready"').Substring(2) | ConvertFrom-Json)
        $ready.revision | Should -Be 'f6d7e9d72bf83c70d7a9373ae8c5edaa052a7bf2'
        $python = Start-C92 python $env:DOTRETICULUM_PYTHON @('-u',(Join-Path $script:C92Root 'tests\Interop\nomadnet_live_node.py'),
            '--config',$primary,'--rnsconfig',(Join-Path $script:C92Evidence python-rns))
        ((Read-C92 $python '"event"\s*:\s*"ready"').Substring(2) | ConvertFrom-Json).revision |
            Should -Be 'f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2'
        Send-C92 $python "rrc-connect $($ready.hash) Python"
        $null = Read-C92 $python '"event"\s*:\s*"rrc-connected"'
        Send-C92 $python 'rrc-join lobby'
        $null = Read-C92 $python '"event"\s*:\s*"rrc-joined"'
        $native = Start-C92 native $env:DOTRETICULUM_NOMADNET_BINARY $p.Arguments
        $address = [regex]::Match((Read-C92 $native 'LXMF delivery address:'),'[0-9a-f]{32}').Value
        Send-C92 $native "rrc connect $($ready.hash) Native"
        $null = Read-C92 $native 'RRC command complete'
        Send-C92 $native 'rrc join lobby'
        $null = Read-C92 $native 'RRC command complete'
        for ($index=0; $index -lt 6; $index++) {
            Send-C92 $python ("rrc-send-json lobby " + (ConvertTo-Json -InputObject (
                "C92Keep$index Hello @Native and @Python " + ('long ' * 18)) -Compress))
            $null = Read-C92 $native "Python: C92Keep$index"
        }
        Send-C92 $native 'rrc send lobby C92NativeReturn'
        $null = Read-C92 $native 'RRC command complete'
        $null = Read-C92 $python '"event"\s*:\s*"rrc-message".*C92NativeReturn.*"nick"\s*:\s*"Native"'
        Send-C92 $native 'rrc pane users maybe'
        $null = Read-C92 $native 'Error:.*rrc pane'
        Send-C92 $native 'rrc pane users off'
        $null = Read-C92 $native 'pane selection changed'
        Send-C92 $native 'rrc pane channels off'
        $null = Read-C92 $native 'pane selection changed'
        $offset = $native.Text.Length
        Send-C92 $native 'rrc view lobby color'
        $null = Read-C92 $native 'RRC view complete'
        $view = $native.Text.ToString().Substring($offset)
        $view | Should -Match 'C92Keep4'
        $view | Should -Match 'C92Keep5'
        $view | Should -Not -Match 'C92Keep0|C92Keep3|> users|> channels'
        $view | Should -Match ([regex]::Escape("`e[1;38;2;171;205;239m"))
        $view | Should -Match "`n            "
        Send-C92 $native 'config primary enable confirm'
        $null = Read-C92 $native 'Configuration source saved'
        $source = Join-Path $p.Profile config
        $before = (Get-FileHash $source).Hash
        Send-C92 $native 'config section rrc'
        Send-C92 $native 'config set history_per_room_cap 1'
        Send-C92 $native 'config reset confirm'
        Send-C92 $native 'config cancel'
        $null = Read-C92 $native 'Configuration cancelled'
        (Get-FileHash $source).Hash | Should -BeExactly $before
        Send-C92 $native 'config section rrc'
        Send-C92 $native 'config set mention_color "broken"'
        $null = Read-C92 $native 'Error:.*(Color|color|hexadecimal)'
        Send-C92 $native 'config cancel'
        $null = Read-C92 $native 'Configuration cancelled'
        (Get-FileHash $source).Hash | Should -BeExactly $before
        Send-C92 $native 'config section rrc'
        Send-C92 $native 'config set show_gutters true'
        Send-C92 $native 'config set filter_loaded_history true'
        Send-C92 $native 'config save confirm'
        $null = Read-C92 $native 'Section saved'
        $null = Exit-C92 $native
        $identityPaths = @(Get-ChildItem $p.Rns -Recurse -File -Filter nomadnet_identity)
        $identityPaths.Count | Should -Be 1
        $identityPath = $identityPaths[0].FullName
        $identity = (Get-FileHash $identityPath).Hash
        $native = Start-C92 restarted $env:DOTRETICULUM_NOMADNET_BINARY $p.Arguments
        [regex]::Match((Read-C92 $native 'LXMF delivery address:'),'[0-9a-f]{32}').Value | Should -Be $address
        Send-C92 $native "rrc open $($ready.hash)"
        $null = Read-C92 $native 'RRC command complete'
        Send-C92 $native 'rrc pane users off'
        $null = Read-C92 $native 'pane selection changed'
        Send-C92 $native 'rrc pane channels off'
        $null = Read-C92 $native 'pane selection changed'
        $offset = $native.Text.Length
        Send-C92 $native 'rrc view lobby color'
        $null = Read-C92 $native 'RRC view complete'
        $restored = $native.Text.ToString().Substring($offset)
        $restored | Should -Match 'C92Keep4'
        $restored | Should -Not -Match 'C92Keep0|C92Keep3'
        $restored | Should -Match '> users|> channels'
        Send-C92 $native 'config section rrc'
        Send-C92 $native 'config reset confirm'
        Send-C92 $native 'config save confirm'
        $null = Read-C92 $native 'Section saved'
        $null = Exit-C92 $native
        $reset = Get-Content $source -Raw
        $reset | Should -Match 'history_per_room_cap = 500'
        $reset | Should -Match 'enable_esoterics = no'
        (Invoke-C92 final $p.Arguments @('status','quit')) | Should -Match "LXMF delivery address: $address"
        (Get-FileHash $identityPath).Hash | Should -BeExactly $identity
    }

    It 'T499 native malformed settings fail explicitly without identity mutation then recover unchanged' {
        if ($env:DOTRETICULUM_NOMADNET_RRC_SETTINGS -ne '1' -or -not $IsWindows) {
            Set-ItResult -Skipped -Because 'Select Windows NativeAOT C92 acceptance; Linux WSL storage is unavailable.'
            return
        }
        $p = New-C92Profile malformed
        $before = Invoke-C92 before $p.Arguments @('status','quit')
        $address = [regex]::Match($before,'LXMF delivery address: ([0-9a-f]{32})').Groups[1].Value
        $valid = [IO.File]::ReadAllBytes($p.Settings)
        $settingsHash = (Get-FileHash $p.Settings).Hash
        $settings = Get-Content $p.Settings -Raw | ConvertFrom-Json
        $settings.rrcNickColorsTheme = @('not-a-color')
        $settings | ConvertTo-Json -Depth 10 | Set-Content $p.Settings -Encoding utf8NoBOM
        $null = Invoke-C92 invalid $p.Arguments @() 1
        $script:C92Children[$script:C92Children.Count - 1].Errors.GetAwaiter().GetResult() |
            Should -Match '(palette|hexadecimal)'
        [IO.File]::WriteAllBytes($p.Settings,$valid)
        (Invoke-C92 recovered $p.Arguments @('rrc pane users invalid','status','quit')) |
            Should -Match "LXMF delivery address: $address"
        (Get-FileHash $p.Settings).Hash | Should -BeExactly $settingsHash
        @(Get-ChildItem $p.Root -Recurse -Force -Filter '*.new').Count | Should -Be 0
    }
}
