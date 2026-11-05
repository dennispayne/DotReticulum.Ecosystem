#Requires -Version 7.0
[CmdletBinding()]
param(
    [string] $Binary = 'artifacts\tests\core-native-e52\rnphone\publish\rnphone.exe',
    [string] $Python = 'artifacts\builds\lxst-win-reference\Scripts\python.exe',
    [string] $LxstSource = 'artifacts\builds\lxst-primary-win-054',
    [Parameter(Mandatory)] [string] $RnsSource,
    [string] $CodecDirectory = 'artifacts\builds\lxst-msys2\ucrt64\bin',
    [ValidateRange(1, 720)] [int] $SteadySeconds = 600,
    [switch] $ProbeOnly
)
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'C111 requires Windows; Linux is not claimed.' }
if ($env:DOTLXST_PYTHON_ENDURANCE -ne '1') { throw 'Explicit opt-in DOTLXST_PYTHON_ENDURANCE=1 is required.' }
if (!$ProbeOnly -and $SteadySeconds -lt 600) { throw 'Acceptance requires at least 600 steady seconds.' }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $repo
$binaryPath = (Resolve-Path $Binary).Path
$pythonPath = (Resolve-Path $Python).Path
$lxstPath = (Resolve-Path $LxstSource).Path
$rnsPath = (Resolve-Path $RnsSource).Path
$codecPath = (Resolve-Path $CodecDirectory).Path
$expected = '6eafd7c9637f0f34d3ca016bf593a913b74b8fe95306d47b81e7c4c1bfc8c63b'
if ((Get-FileHash $binaryPath).Hash.ToLowerInvariant() -ne $expected) { throw 'Not the accepted historical Windows NativeAOT binary.' }
$run = Join-Path $repo ('artifacts\tests\lxst-python-endurance\' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $run | Out-Null
$processes = [Collections.Generic.List[object]]::new()
$observations = [Collections.Generic.List[object]]::new()
$receipt = [ordered]@{
    contract = 'C111'; tests = @('T1100','T1101','T1102','T1103','T1104')
    scope = if ($ProbeOnly) { 'probe-only, not prolonged acceptance' } else { 'Windows NativeAOT rnphone to actual pinned Python Telephone, direct TCP' }
    physicalAudio = $false; qualityClaim = $false; impairedOrMultihop = $false; currentLinux = $false
    backendShim = 'Explicit synthetic Python device backend and forwarding public pipeline sink observers; no source/protocol/clock patches. Native --headless-audio synthetic silence/discard.'
    sourceHead = (& git rev-parse HEAD).Trim()
    stackHead = (& git -C external\DotReticulum rev-parse HEAD).Trim()
    historicalNative = @{
        path=$binaryPath; sha256=$expected; currentHeadBuild=$false
        accepted=(Get-Content 'artifacts\tests\core-native-e52\rnphone\accepted.json' -Raw | ConvertFrom-Json)
        provenance='Retained accepted.json, source-before.json/source-after.json, publish.log and project.assets.json; historical e52 stack, not current HEAD/stack.'
    }
    requestedSteadySeconds = $SteadySeconds; startedUtc = [DateTime]::UtcNow.ToString('o')
    passed = $false
}
function Get-Freeze {
    $files = @(
        $binaryPath; $pythonPath
        (Join-Path $repo 'tests\Pester\Invoke-LxstPythonEndurance.ps1')
        (Join-Path $repo 'tests\Interop\live_lxst_node.py')
        (Join-Path $repo 'tests\Interop\lxst_synthetic_audio.py')
        (Join-Path $repo 'tests\Interop\reference_environment.py')
        (Join-Path $repo 'artifacts\tests\lxst-file-drain\manifest.json')
        (Join-Path $repo 'artifacts\tests\core-native-e52\rnphone\accepted.json')
        (Join-Path $repo 'artifacts\tests\core-native-e52\rnphone\source-before.json')
        (Join-Path $repo 'artifacts\tests\core-native-e52\rnphone\source-after.json')
        (Join-Path $repo 'artifacts\tests\core-native-e52\rnphone\publish.log')
    )
    foreach ($directory in @('src\LXST','src\rnphone')) {
        $files += Get-ChildItem (Join-Path $repo $directory) -Recurse -File |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | Select-Object -ExpandProperty FullName
    }
    foreach ($directory in @((Join-Path $lxstPath 'LXST'), (Join-Path $rnsPath 'RNS'))) {
        $files += Get-ChildItem $directory -Recurse -File -Filter '*.py' | Select-Object -ExpandProperty FullName
    }
    $files += Get-ChildItem $codecPath -Filter '*.dll' | Where-Object { $_.Name -match 'opus|codec2|gcc|winpthread' } | Select-Object -ExpandProperty FullName
    $managed = Join-Path $repo 'artifacts\builds\lxst-closeout-win\bin\LXST.Tests\release'
    $files += Get-ChildItem $managed -File | Where-Object { $_.Name -match '^(LXST|DotReticulum).*\.(dll|pdb)$' } | Select-Object -ExpandProperty FullName
    @($files | Sort-Object -Unique | ForEach-Object {
        [pscustomobject]@{ path=$_; sha256=(Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant() }
    })
}
function Start-OwnedProcess([string] $File, [string[]] $Arguments, [string] $Name) {
    $start = [Diagnostics.ProcessStartInfo]::new($File)
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $start.Environment['PATH'] = "$codecPath;$env:PATH"
    $start.Environment['PYTHONPATH'] = "$lxstPath;$rnsPath"
    $start.Environment['PYTHONIOENCODING'] = 'utf-8'
    $start.Environment['DOTRETICULUM_LXST_UPSTREAM'] = $lxstPath
    $start.Environment['DOTRETICULUM_UPSTREAM'] = $rnsPath
    $process = [Diagnostics.Process]::Start($start)
    $running = [pscustomobject]@{
        Process=$process; Name=$Name; Arguments=$Arguments
        Lines=[Collections.Generic.List[string]]::new()
        Errors=[Collections.Generic.List[string]]::new()
        Read=$process.StandardOutput.ReadLineAsync(); ErrorRead=$process.StandardError.ReadLineAsync()
    }
    $processes.Add($running)
    return $running
}
function Read-Owned($Running) {
    while ($Running.Read -and $Running.Read.IsCompleted) {
        $line = $Running.Read.GetAwaiter().GetResult()
        if ($null -eq $line) { $Running.Read=$null; break }
        $Running.Lines.Add($line)
        $Running.Read=$Running.Process.StandardOutput.ReadLineAsync()
    }
    while ($Running.ErrorRead -and $Running.ErrorRead.IsCompleted) {
        $line = $Running.ErrorRead.GetAwaiter().GetResult()
        if ($null -eq $line) { $Running.ErrorRead=$null; break }
        $Running.Errors.Add($line)
        $Running.ErrorRead=$Running.Process.StandardError.ReadLineAsync()
    }
    foreach ($line in $Running.Lines) {
        if ($line -match '^@@.*"event": "error"') { throw "Python peer error: $line" }
    }
}
function Send-Owned($Running, [string] $Line) {
    $Running.Process.StandardInput.WriteLine($Line)
    $Running.Process.StandardInput.Flush()
}
function Wait-Owned($Running, [string] $Pattern, [int] $After = 0, [int] $Seconds = 30) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $Seconds) {
        Read-Owned $Running
        $matches = @($Running.Lines | Where-Object { $_ -match $Pattern })
        if ($matches.Count -gt $After) { return $matches[$After] }
        if ($Running.Process.HasExited) { break }
        Start-Sleep -Milliseconds 20
    }
    throw "$($Running.Name) timed out: $Pattern; output=$($Running.Lines -join '`n'); errors=$($Running.Errors -join '`n')"
}
function Command-Owned($Running, [string] $Command, [string] $Pattern) {
    Read-Owned $Running
    $before = @($Running.Lines | Where-Object { $_ -match $Pattern }).Count
    Send-Owned $Running $Command
    Wait-Owned $Running $Pattern $before
}
function Read-Counters($Native, $Peer, [double] $Elapsed) {
    $line = Command-Owned $Native 'status' '^Call status: Established; received=(\d+); transmitted=(\d+)'
    $match = [regex]::Match($line, 'received=(\d+); transmitted=(\d+)')
    $peerLine = Command-Owned $Peer 'frame-observations' '^@@.*"event": "frame-observations"'
    $pythonCounters = $peerLine.Substring(2) | ConvertFrom-Json
    if (!$pythonCounters.latest.receive.finite -or $pythonCounters.latest.receive.samples -le 0 -or
        $pythonCounters.latest.receive.channels -le 0 -or $pythonCounters.latest.transmit.bytes -le 0) {
        throw 'Missing finite decoded Python samples or encoded transmitted frame bytes.'
    }
    $record = [pscustomobject]@{
        elapsedSeconds=$Elapsed; nativePid=$Native.Process.Id; pythonPid=$Peer.Process.Id
        nativeReceived=[long]$match.Groups[1].Value; nativeTransmitted=[long]$match.Groups[2].Value
        pythonReceived=[long]$pythonCounters.received; pythonTransmitted=[long]$pythonCounters.transmitted
        python=$pythonCounters
    }
    $observations.Add($record)
    return $record
}
function Assert-Progress($Before, $After) {
    foreach ($field in @('nativeReceived','nativeTransmitted','pythonReceived','pythonTransmitted')) {
        if ($After.$field -le $Before.$field) { throw "No frame progress: $field" }
    }
}
function Get-CodecModules($Running) {
    $Running.Process.Refresh()
    @($Running.Process.Modules | Where-Object { $_.ModuleName -match 'opus|codec2' } | ForEach-Object {
        [pscustomobject]@{ path=$_.FileName; sha256=(Get-FileHash $_.FileName).Hash.ToLowerInvariant(); version=$_.FileVersionInfo.FileVersion }
    })
}
function New-Peer([string] $Name) {
    Start-OwnedProcess $pythonPath @((Join-Path $repo 'tests\Interop\live_lxst_node.py'), '--config',
        (Join-Path $run $Name), '--tcp-server-port', "$port", '--telephone', '--synthetic-audio') $Name
}
$before = Get-Freeze
$before | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $run 'freeze-before.json')
$native = $null
$peer = $null
try {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
    $listener.Stop()
    $peer = New-Peer 'python-first'
    $ready = (Wait-Owned $peer '^@@.*"event": "ready"').Substring(2) | ConvertFrom-Json
    $receipt.pythonReady = $ready
    $phoneConfig = Join-Path $run 'native-phone'
    $rnsConfig = Join-Path $run 'native-rns'
    New-Item -ItemType Directory -Path $phoneConfig,$rnsConfig | Out-Null
    "[telephone]`nallowed_callers = all`n" | Set-Content (Join-Path $phoneConfig 'config')
    "[reticulum]`nshare_instance = no`n[logging]`nloglevel = 4`n[interfaces]`n[[Client]]`ntype = TCPClientInterface`nenabled = yes`ntarget_host = 127.0.0.1`ntarget_port = $port`n" | Set-Content (Join-Path $rnsConfig 'config')
    $native = Start-OwnedProcess $binaryPath @('--headless-audio','--config',$phoneConfig,'--rnsconfig',$rnsConfig) 'native'
    Wait-Owned $native 'Headless audio: synthetic silence' | Out-Null
    Wait-Owned $native 'Reticulum Telephone ready' | Out-Null
    $identity = Wait-Owned $native '^Identity hash:'
    $destination = [regex]::Match((Wait-Owned $native '^Destination hash:'), '[a-f0-9]{32}').Value
    $receipt.nativeIdentity = $identity
    $receipt.nativeDestination = $destination
    $receipt.nativePid = $native.Process.Id
    $receipt.nativeStartTime = $native.Process.StartTime.ToUniversalTime().ToString('o')
    $receipt.nativeProcessPath = $native.Process.MainModule.FileName
    Command-Owned $peer 'wait-client' '^@@.*"event": "client-ready"' | Out-Null
    Command-Owned $peer 'announce' '^@@.*"event": "announced"' | Out-Null
    Send-Owned $native $ready.identity_hash
    Wait-Owned $peer '^@@.*"event": "established"' | Out-Null
    Wait-Owned $native '^State: Established' | Out-Null
    Start-Sleep -Seconds 2
    $baseline = Read-Counters $native $peer 0
    $receipt.nativeCodecModules = Get-CodecModules $native
    $receipt.pythonCodecModules = Get-CodecModules $peer
    $steady = [Diagnostics.Stopwatch]::StartNew()
    $last = $baseline
    $interval = if ($ProbeOnly) { 2 } else { 15 }
    $next = [Math]::Min($interval, $SteadySeconds)
    while ($true) {
        Read-Owned $native
        Read-Owned $peer
        if ($native.Process.HasExited -or $peer.Process.HasExited) { throw 'A peer exited during steady call.' }
        if ($steady.Elapsed.TotalSeconds -ge $next) {
            $current = Read-Counters $native $peer $steady.Elapsed.TotalSeconds
            Assert-Progress $last $current
            if ($current.python.latest.receive.codec -ne 'Opus' -or $current.python.latest.transmit.codec -ne 'Opus') {
                throw 'Steady phase did not use actual Opus in both directions.'
            }
            $last = $current
            Write-Host ("C111 steady {0:N3}s native rx/tx={1}/{2} Python rx/tx={3}/{4}" -f
                $current.elapsedSeconds,$current.nativeReceived,$current.nativeTransmitted,$current.pythonReceived,$current.pythonTransmitted)
            if ($current.elapsedSeconds -ge $SteadySeconds) { break }
            $next = [Math]::Min($next + $interval, $SteadySeconds)
        }
        Start-Sleep -Milliseconds 40
    }
    $receipt.steadyElapsedSeconds = $steady.Elapsed.TotalSeconds
    $receipt.steadyBaseline = $baseline
    $receipt.steadyFinal = $last
    # Actual Codec2 is additionally negotiated after the prolonged Opus interval.
    Command-Owned $peer 'profile 48' '^@@.*"event": "profile"' | Out-Null
    Start-Sleep -Seconds 3
    $codec2Before = Read-Counters $native $peer -1
    Start-Sleep -Seconds 3
    $codec2After = Read-Counters $native $peer -1
    Assert-Progress $codec2Before $codec2After
    if ($codec2After.python.latest.receive.codec -ne 'Codec2' -or $codec2After.python.latest.transmit.codec -ne 'Codec2') { throw 'Codec2 did not execute both directions.' }
    $receipt.codec2 = $codec2After
    $receipt.nativeAllCodecModules = Get-CodecModules $native
    $receipt.pythonAllCodecModules = Get-CodecModules $peer
    Command-Owned $peer 'hangup' '^@@.*"event": "hungup"' | Out-Null
    Command-Owned $native 'status' '^Call status: Available;' | Out-Null
    # Reverse-origin call, followed by abrupt actual-Python process failure.
    Send-Owned $native 'announce'
    Start-Sleep -Seconds 1
    Send-Owned $peer ("call $destination")
    Wait-Owned $native '^State: Ringing' 1 | Out-Null
    Send-Owned $native ''
    Wait-Owned $peer '^@@.*"event": "established"' 1 | Out-Null
    Start-Sleep -Seconds 2
    $reverseBefore = Read-Counters $native $peer -2
    Start-Sleep -Seconds 3
    $reverseAfter = Read-Counters $native $peer -2
    Assert-Progress $reverseBefore $reverseAfter
    $receipt.reverseCall = $reverseAfter
    $peer.Process.Kill($true)
    $peer.Process.WaitForExit()
    $failureWatch = [Diagnostics.Stopwatch]::StartNew()
    Command-Owned $native 'status' '^Call status: Available;' | Out-Null
    $receipt.failureRecoverySeconds = $failureWatch.Elapsed.TotalSeconds
    $peer = New-Peer 'python-recovered'
    $recovered = (Wait-Owned $peer '^@@.*"event": "ready"').Substring(2) | ConvertFrom-Json
    Command-Owned $peer 'wait-client' '^@@.*"event": "client-ready"' | Out-Null
    Command-Owned $peer 'announce' '^@@.*"event": "announced"' | Out-Null
    Send-Owned $native $recovered.identity_hash
    Wait-Owned $peer '^@@.*"event": "established"' | Out-Null
    Start-Sleep -Seconds 2
    $recoveryBefore = Read-Counters $native $peer -3
    Start-Sleep -Seconds 3
    $recoveryAfter = Read-Counters $native $peer -3
    Assert-Progress $recoveryBefore $recoveryAfter
    $receipt.recoveredCall = $recoveryAfter
    Command-Owned $peer 'hangup' '^@@.*"event": "hungup"' | Out-Null
    Command-Owned $native 'status' '^Call status: Available;' | Out-Null
    if ($native.Process.Id -ne $receipt.nativePid -or $native.Process.HasExited) { throw 'Native process was replaced.' }
    Send-Owned $native 'quit'
    if (!$native.Process.WaitForExit(10000) -or $native.Process.ExitCode -ne 0) { throw 'Native clean exit failed.' }
    $receipt.passed = $true
} catch {
    $receipt.failure = $_.ToString()
    throw
} finally {
    foreach ($running in $processes) {
        if (!$running.Process.HasExited) {
            try { Send-Owned $running 'quit' } catch { }
            if (!$running.Process.WaitForExit(5000)) { $running.Process.Kill($true); $running.Process.WaitForExit() }
        }
        try { Read-Owned $running } catch { }
        $running.Lines | Set-Content (Join-Path $run ($running.Name + '.stdout.txt'))
        $running.Errors | Set-Content (Join-Path $run ($running.Name + '.stderr.txt'))
    }
    $after = Get-Freeze
    $after | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $run 'freeze-after.json')
    $receipt.hashesUnchanged = (($before | ConvertTo-Json -Compress) -ceq ($after | ConvertTo-Json -Compress))
    if (!$receipt.hashesUnchanged) { $receipt.passed=$false }
    $receipt.finishedUtc = [DateTime]::UtcNow.ToString('o')
    $receipt.observations = $observations.ToArray()
    $receiptPath = Join-Path $run 'receipt.json'
    $receipt | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $receiptPath
    (Get-FileHash $receiptPath).Hash.ToLowerInvariant() | Set-Content (Join-Path $run 'receipt.sha256')
    (Get-Item $receiptPath).IsReadOnly = $true
    Write-Host "C111 receipt: $receiptPath"
    foreach ($running in $processes) { $running.Process.Dispose() }
}
if (!$receipt.passed) { throw 'C111 acceptance failed or frozen inputs changed.' }
