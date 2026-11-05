BeforeAll {
    $script:C93Root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $script:C93Evidence = $env:DOTRETICULUM_NOMADNET_RNS_POLICY_EVIDENCE
    if (-not $script:C93Evidence) {
        $script:C93Evidence = Join-Path $script:C93Root ('artifacts\tests\nomad-rns-policy\acceptance-' + [guid]::NewGuid().ToString('N'))
    }
    $null = New-Item $script:C93Evidence -ItemType Directory -Force
    $script:C93Children = [Collections.Generic.List[object]]::new()
    function Start-C93([string] $Label, [string] $Executable, [string[]] $Arguments) {
        $info = [Diagnostics.ProcessStartInfo]::new($Executable)
        $info.UseShellExecute = $false
        $info.RedirectStandardInput = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $info.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
        $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
        foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
        foreach ($variable in 'TMPDIR','TMP','TEMP') { $info.Environment[$variable] = $script:C93Evidence }
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $info
        if (-not $process.Start()) { $process.Dispose(); throw 'Selected child did not start.' }
        $child = @{Process=$process; Errors=$process.StandardError.ReadToEndAsync(); Pending=$null;
            Text=[Text.StringBuilder]::new(); Label=$Label}
        $script:C93Children.Add($child)
        return $child
    }
    function Send-C93($Child, [string] $Command) {
        $Child.Process.StandardInput.WriteLine($Command)
        $Child.Process.StandardInput.Flush()
    }
    function Read-C93($Child, [string] $Pattern, [int] $Seconds = 60) {
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
    function Exit-C93($Child, [int] $Expected = 0, [bool] $Quit = $true) {
        if ($Quit) { Send-C93 $Child quit }
        $Child.Process.StandardInput.Close()
        $tail = $Child.Process.StandardOutput.ReadToEndAsync()
        $null = $Child.Process.WaitForExitAsync().WaitAsync([TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult()
        $null = $Child.Text.Append($tail.GetAwaiter().GetResult())
        $Child.Process.ExitCode | Should -Be $Expected -Because $Child.Errors.GetAwaiter().GetResult()
        return $Child.Text.ToString()
    }
    function Invoke-C93([string] $Label, [string[]] $Arguments, [string[]] $Commands = @(), [int] $Expected = 0) {
        $child = Start-C93 $Label $env:DOTRETICULUM_NOMADNET_BINARY $Arguments
        foreach ($command in $Commands) { Send-C93 $child $command }
        return Exit-C93 $child $Expected $false
    }
    function New-C93Profile([string] $Name, [int] $Port = 0) {
        $root = Join-Path $script:C93Evidence $Name
        $rns = Join-Path $root selected-rns
        $profile = Join-Path $root profile
        $null = New-Item $rns -ItemType Directory -Force
        $interface = if ($Port) {
            "# Unrelated interface retained`r`n[[NativeLoopback]]`r`ntype=TCPServerInterface`r`nenabled=yes`r`nlisten_ip=127.0.0.1`r`nlisten_port=$Port`r`n"
        } else { '' }
        $text = "# C93 selected original`r`n[reticulum]`r`nshare_instance=no # retained`r`nenable_transport=no`r`nrespond_to_probes=yes`r`nuse_implicit_proof=yes`r`n[logging]`r`nloglevel=0`r`n[interfaces]`r`n$interface"
        $source = Join-Path $rns config
        [IO.File]::WriteAllText($source,$text,[Text.UTF8Encoding]::new($false))
        $null = Invoke-C93 "$Name-init" @('--init','--config',$profile)
        $settings = Join-Path $profile settings.json
        $value = Get-Content $settings -Raw | ConvertFrom-Json
        $value.clientAnnounceAtStart = $false
        $value.periodicLxmfSync = $false
        $value.nodePageRefreshIntervalMinutes = 0
        $value.nodeFileRefreshIntervalMinutes = 0
        $value | ConvertTo-Json -Depth 10 | Set-Content $settings -Encoding utf8NoBOM
        return @{Root=$root;Profile=$profile;Rns=$rns;Source=$source;Settings=$settings;Original=$text;Interface=$interface;
            Arguments=@('--config',$profile,'--rnsconfig',$rns)}
    }
    function Read-C93Policy($Native, [string] $Policy) {
        Send-C93 $Native status
        $null = Read-C93 $Native $Policy
        return [regex]::Match((Read-C93 $Native 'RNS probe responder:'),'RNS probe responder: ([0-9a-f]{32}|disabled)').Groups[1].Value
    }
    function Test-C93Outbound($Native, $Python, [string] $Destination) {
        Send-C93 $Python announce
        $null = Read-C93 $Python '"event"\s*:\s*"announced"'
        Send-C93 $Native "utility probe rnstransport.probe $Destination 16 1"
        $null = Read-C93 $Native 'Probe sent=1 received=1 loss=0.0%'
    }
}

Describe 'Selected Nomad RNS owner configuration acceptance' -Tag 'RnsPolicy' {
    AfterEach {
        foreach ($child in $script:C93Children) {
            try {
                if (-not $child.Process.HasExited) { $child.Process.Kill($true); $child.Process.WaitForExit() }
                [IO.File]::WriteAllText((Join-Path $script:C93Evidence ($child.Label + '.stdout')), $child.Text.ToString())
                [IO.File]::WriteAllText((Join-Path $script:C93Evidence ($child.Label + '.stderr')), $child.Errors.GetAwaiter().GetResult())
            } finally { $child.Process.Dispose() }
        }
        $script:C93Children.Clear()
    }

    It 'T514 actual primary selected RNS forms independently regenerate defaults cancel save restart reset and panic difference' {
        if ($env:DOTRETICULUM_NOMADNET_RNS_POLICY -ne '1') {
            Set-ItResult -Skipped -Because 'Select C93 acceptance and actual pinned Python/primary sources.'
            return
        }
        $output = Join-Path $script:C93Evidence regenerated.json
        $child = Start-C93 reference $env:DOTRETICULUM_PYTHON @(
            (Join-Path $script:C93Root 'tests\Interop\GenerateNomadNetRnsPolicyVectors.py'),
            '--output',$output,'--observations',(Join-Path $script:C93Evidence imports.json),
            '--work',(Join-Path $script:C93Evidence reference))
        $null = Exit-C93 $child 0 $false
        (Get-FileHash $output).Hash | Should -BeExactly (Get-FileHash (
            Join-Path $script:C93Root 'tests\NomadNet.Tests\NomadNetRnsPolicyVectors.json')).Hash
        (Get-Content $output -Raw | ConvertFrom-Json).primaryConfigUnchanged | Should -BeTrue
    }

    It 'T515 actual native Nomad selected forms switch implicit explicit probe owner proofs only after identity preserving restarts' {
        if ($env:DOTRETICULUM_NOMADNET_RNS_POLICY -ne '1' -or -not $IsWindows) {
            Set-ItResult -Skipped -Because 'Select Windows native and pinned actual Python RNS; Linux WSL EIO remains unavailable.'
            return
        }
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
        $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
        $p = New-C93Profile lifecycle $port
        $settingsHash = (Get-FileHash $p.Settings).Hash
        $native = Start-C93 initial $env:DOTRETICULUM_NOMADNET_BINARY $p.Arguments
        $address = [regex]::Match((Read-C93 $native 'LXMF delivery address:'),'[0-9a-f]{32}').Value
        (Read-C93Policy $native 'RNS owner policy: transport=False; respond_to_probes=True; use_implicit_proof=True') | Should -Be disabled
        Send-C93 $native 'config section reticulum'
        Send-C93 $native 'config set use_implicit_proof false'
        Send-C93 $native 'config cancel'
        $null = Read-C93 $native 'Configuration cancelled'
        [IO.File]::ReadAllText($p.Source) | Should -BeExactly $p.Original
        Send-C93 $native 'config section reticulum'
        Send-C93 $native 'config set respond_to_probes "maybe"'
        $null = Read-C93 $native 'Error:.*Checkbox'
        Send-C93 $native 'config set panic_on_interface_error true'
        $null = Read-C93 $native 'Error:.*fixed safety ownership'
        Send-C93 $native 'config set enable_transport true'
        Send-C93 $native 'config set respond_to_probes true'
        Send-C93 $native 'config set use_implicit_proof false'
        Send-C93 $native 'config save'
        $null = Read-C93 $native 'Error:.*confirm'
        [IO.File]::ReadAllText($p.Source) | Should -BeExactly $p.Original
        Send-C93 $native 'config save confirm'
        $null = Read-C93 $native 'Section saved.*Restart'
        (Read-C93Policy $native 'RNS owner policy: transport=False; respond_to_probes=True; use_implicit_proof=True') | Should -Be disabled
        [IO.File]::ReadAllText($p.Source) | Should -Match ([regex]::Escape($p.Interface))
        (Get-FileHash $p.Settings).Hash | Should -BeExactly $settingsHash
        $null = Exit-C93 $native
        $identityFiles = @(Get-ChildItem $p.Rns -Recurse -File | Where-Object Name -In 'nomadnet_identity','transport_identity')
        $identityFiles.Count | Should -Be 2
        $identityHashes = @{}
        foreach ($file in $identityFiles) { $identityHashes[$file.FullName] = (Get-FileHash $file.FullName).Hash }
        $native = Start-C93 explicit $env:DOTRETICULUM_NOMADNET_BINARY $p.Arguments
        [regex]::Match((Read-C93 $native 'LXMF delivery address:'),'[0-9a-f]{32}').Value | Should -Be $address
        $probe = Read-C93Policy $native 'RNS owner policy: transport=True; respond_to_probes=True; use_implicit_proof=False'
        $probe | Should -Match '^[0-9a-f]{32}$'
        $python = Start-C93 python $env:DOTRETICULUM_PYTHON @('-u',
            (Join-Path $script:C93Root 'external\DotReticulum\tests\Interop\proof_probe_node.py'),
            '--config',(Join-Path $script:C93Evidence python),'--rnsd-port',"$port",'--implicit-proof','no')
        $ready = ((Read-C93 $python '"event"\s*:\s*"ready"').Substring(2) | ConvertFrom-Json)
        $ready.revision | Should -Be '1.5.7'
        $null = Read-C93 $python ('"event"\s*:\s*"announce".*"dest"\s*:\s*"' + $probe + '"') 45
        Send-C93 $python "probe $probe"
        $proof = ((Read-C93 $python '"event"\s*:\s*"receipt"').Substring(2) | ConvertFrom-Json)
        $proof.proved | Should -BeTrue
        $proof.proof_length | Should -Be 96
        Test-C93Outbound $native $python $ready.dest
        Send-C93 $native 'config section reticulum'
        Send-C93 $native 'config set use_implicit_proof true'
        Send-C93 $native 'config save confirm'
        $null = Read-C93 $native 'Section saved.*Restart'
        (Read-C93Policy $native 'use_implicit_proof=False') | Should -Be $probe
        $null = Exit-C93 $native
        $native = Start-C93 implicit $env:DOTRETICULUM_NOMADNET_BINARY $p.Arguments
        [regex]::Match((Read-C93 $native 'LXMF delivery address:'),'[0-9a-f]{32}').Value | Should -Be $address
        (Read-C93Policy $native 'transport=True; respond_to_probes=True; use_implicit_proof=True') | Should -Be $probe
        $null = Read-C93 $python ('"event"\s*:\s*"announce".*"dest"\s*:\s*"' + $probe + '"') 45
        Send-C93 $python "probe $probe"
        $proof = ((Read-C93 $python '"event"\s*:\s*"receipt"').Substring(2) | ConvertFrom-Json)
        $proof.proved | Should -BeTrue
        $proof.proof_length | Should -Be 64
        Test-C93Outbound $native $python $ready.dest
        Send-C93 $native 'config section reticulum'
        Send-C93 $native 'config set respond_to_probes false'
        Send-C93 $native 'config save confirm'
        $null = Read-C93 $native 'Section saved.*Restart'
        (Read-C93Policy $native 'respond_to_probes=True') | Should -Be $probe
        $null = Exit-C93 $native
        $native = Start-C93 disabled $env:DOTRETICULUM_NOMADNET_BINARY $p.Arguments
        [regex]::Match((Read-C93 $native 'LXMF delivery address:'),'[0-9a-f]{32}').Value | Should -Be $address
        (Read-C93Policy $native 'transport=True; respond_to_probes=False') | Should -Be disabled
        Start-Sleep -Seconds 6
        Test-C93Outbound $native $python $ready.dest
        Send-C93 $python "probe $probe"
        ((Read-C93 $python '"event"\s*:\s*"receipt"').Substring(2) | ConvertFrom-Json).proved | Should -BeFalse
        Send-C93 $native 'config section reticulum'
        Send-C93 $native 'config reset confirm'
        Send-C93 $native 'config set share_instance false'
        Send-C93 $native 'config set respond_to_probes true'
        Send-C93 $native 'config save confirm'
        $null = Read-C93 $native 'Section saved.*Restart'
        $null = Exit-C93 $native
        $native = Start-C93 nontransport $env:DOTRETICULUM_NOMADNET_BINARY $p.Arguments
        [regex]::Match((Read-C93 $native 'LXMF delivery address:'),'[0-9a-f]{32}').Value | Should -Be $address
        (Read-C93Policy $native 'transport=False; respond_to_probes=True; use_implicit_proof=True') | Should -Be disabled
        Start-Sleep -Seconds 6
        Test-C93Outbound $native $python $ready.dest
        Send-C93 $python "probe $probe"
        ((Read-C93 $python '"event"\s*:\s*"receipt"').Substring(2) | ConvertFrom-Json).proved | Should -BeFalse
        $null = Exit-C93 $native
        $null = Exit-C93 $python
        foreach ($file in $identityFiles) { (Get-FileHash $file.FullName).Hash | Should -BeExactly $identityHashes[$file.FullName] }
        (Get-FileHash $p.Settings).Hash | Should -BeExactly $settingsHash
        [IO.File]::ReadAllText($p.Source) | Should -Match ([regex]::Escape($p.Interface))
        Test-Path (Join-Path $p.Profile config) | Should -BeFalse
        @(Get-ChildItem $p.Root -Recurse -Force -Filter '.editor-*').Count | Should -Be 0
    }

    It 'T516 native malformed selected RNS policies fail explicitly without identity mutation then recover unchanged' {
        if ($env:DOTRETICULUM_NOMADNET_RNS_POLICY -ne '1' -or -not $IsWindows) {
            Set-ItResult -Skipped -Because 'Select Windows C93 NativeAOT acceptance; Linux WSL EIO remains unavailable.'
            return
        }
        $p = New-C93Profile malformed
        $before = Invoke-C93 before $p.Arguments @('status','quit')
        $address = [regex]::Match($before,'LXMF delivery address: ([0-9a-f]{32})').Groups[1].Value
        foreach ($key in 'respond_to_probes','use_implicit_proof') {
            [IO.File]::WriteAllText($p.Source,$p.Original.Replace("$key=yes","$key=maybe"))
            $null = Invoke-C93 "bad-$key" $p.Arguments @() 1
            $script:C93Children[$script:C93Children.Count - 1].Errors.GetAwaiter().GetResult() |
                Should -Match ([regex]::Escape("'$key' must be yes or no, got 'maybe'."))
        }
        [IO.File]::WriteAllText($p.Source,$p.Original)
        (Invoke-C93 recovered $p.Arguments @('status','quit')) | Should -Match "LXMF delivery address: $address"
        [IO.File]::ReadAllText($p.Source) | Should -BeExactly $p.Original
    }
}
