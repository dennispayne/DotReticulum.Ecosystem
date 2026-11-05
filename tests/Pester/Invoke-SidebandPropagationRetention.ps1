#Requires -Version 7.0
[CmdletBinding()]
param(
    [switch] $Live,
    [string] $Candidate = 'artifacts\tests\sideband-propagation-telemetry\parent-current\candidate.json',
    [string] $OutputDirectory = ('artifacts\tests\sideband-propagation-telemetry\retention-' + [guid]::NewGuid().ToString('N'))
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $Live) { throw 'Explicit -Live is required for the real five-minute workload.' }
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $root
$candidatePath = (Resolve-Path $Candidate).Path
$candidateHash = (Get-FileHash $candidatePath).Hash.ToLowerInvariant()
$frozen = Get-Content $candidatePath -Raw | ConvertFrom-Json
$expectedBinary = 'e7fd33ac23008bf393e53fe2ca32427866d368d3d094edc09f5758c8a9f4e239'
if ($frozen.Binary.Sha256 -cne $expectedBinary) { throw 'This additional workload selects only the independently accepted historical native binary.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path $output) { throw 'Select fresh output; never overwrite candidate or failed receipts.' }
$null = New-Item $output -ItemType Directory
$head = (& git rev-parse HEAD)
$stack = (& git -C external\DotReticulum rev-parse HEAD)
$clock = [Diagnostics.Stopwatch]::StartNew()
$processes = [Collections.Generic.List[object]]::new()
$observations = [Collections.Generic.List[object]]::new()
$workloadFiles = @($PSCommandPath, (Join-Path $root 'tests\Interop\sideband_propagation_retention_node.py'),
    (Join-Path $root 'tests\Interop\reference_environment.py'), (Join-Path $root 'tests\Interop\live_lxmf_node.py'),
    (Join-Path $PSScriptRoot 'SidebandPropagationRetention.Tests.ps1'))
$workloadHashes = @($workloadFiles | ForEach-Object { @{Path=$_;Sha256=(Get-FileHash $_).Hash.ToLowerInvariant()} })

function AssertHashes($Files) {
    foreach ($file in $Files) {
        if ((Get-FileHash -LiteralPath $file.Path).Hash.ToLowerInvariant() -cne $file.Sha256) {
            throw "Selected immutable input changed: $($file.Path)"
        }
    }
}
function AssertProvenance {
    if ((Get-FileHash $candidatePath).Hash.ToLowerInvariant() -cne $candidateHash) { throw 'Historical candidate changed.' }
    if ((& git rev-parse HEAD) -cne $head -or (& git -C external\DotReticulum rev-parse HEAD) -cne $stack) {
        throw 'Current HEAD/stack changed during the workload.'
    }
    $directories = @('src\Sideband','src\Sideband.Desktop','src\LXMF','src\LXST','external\DotReticulum\src')
    if (Test-Path src\Shared) { $directories += 'src\Shared' }
    $files = @(Get-ChildItem $directories -File -Recurse | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
    foreach ($directory in '.','external\DotReticulum') {
        $files += @(Get-ChildItem $directory -File -Force |
            Where-Object { $_.Name -match '^Directory\.|^global\.json$|^version\.json$|^NuGet\.Config$|^\.editorconfig$' })
    }
    if (Compare-Object @($frozen.Sources.Path) @($files.FullName | Sort-Object -Unique)) { throw 'Relevant production inventory changed.' }
    AssertHashes @($frozen.Sources + $frozen.ReferenceFiles + $frozen.Binary + $workloadHashes)
}
function Pump {
    if ($clock.Elapsed.TotalSeconds -gt 500) { throw 'The bounded real workload exceeded500 seconds.' }
    foreach ($state in $processes) {
        foreach ($channel in @('Out','Err')) {
            $taskName = $channel + 'Task'
            while ($null -ne $state.$taskName -and $state.$taskName.IsCompleted) {
                $line = $state.$taskName.GetAwaiter().GetResult()
                if ($null -eq $line) { $state.$taskName = $null; break }
                if ($line.Length -gt 65536 -or $state.Lines.Count -ge 12000) { throw 'Observation output exceeded bounds.' }
                $row = @{ElapsedSeconds=$clock.Elapsed.TotalSeconds;Channel=$channel;Text=$line}
                $state.Lines.Add($row)
                $json = $row | ConvertTo-Json -Compress
                [IO.File]::AppendAllText($state.Log, $json + "`n")
                if ($line.StartsWith('@@')) { $state.Events.Add(($line.Substring(2) | ConvertFrom-Json)) }
                $reader = if ($channel -eq 'Out') { $state.Process.StandardOutput } else { $state.Process.StandardError }
                $state.$taskName = $reader.ReadLineAsync()
            }
        }
    }
}
function StartObserved($Name, $Executable, $Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $state = [pscustomobject]@{Name=$Name;Process=$process;Lines=[Collections.Generic.List[object]]::new();
        Events=[Collections.Generic.List[object]]::new();Log=(Join-Path $output ($Name + '.jsonl'));
        OutTask=$process.StandardOutput.ReadLineAsync();ErrTask=$process.StandardError.ReadLineAsync();
        Pid=$process.Id;StartUtc=$process.StartTime.ToUniversalTime().ToString('O')}
    $processes.Add($state)
    return $state
}
function Send($State, $Command) {
    if ($State.Process.HasExited) { throw "$($State.Name) exited before command." }
    $State.Process.StandardInput.WriteLine($Command)
    $State.Process.StandardInput.Flush()
}
function WaitLine($State, $After, $Text, $Seconds = 20) {
    $end = $clock.Elapsed.TotalSeconds + $Seconds
    while ($clock.Elapsed.TotalSeconds -lt $end) {
        Pump
        for ($index=$After; $index -lt $State.Lines.Count; $index++) {
            if ($State.Lines[$index].Text.Contains($Text, [StringComparison]::Ordinal)) { return $State.Lines[$index] }
        }
        if ($State.Process.HasExited) { throw "$($State.Name) exited before $Text" }
        Start-Sleep -Milliseconds 40
    }
    throw "$($State.Name) did not report '$Text' within the bounded wait."
}
function NativeCommand($Command, $Acknowledgement) {
    Pump
    $after = $native.Lines.Count
    Send $native $Command
    return WaitLine $native $after $Acknowledgement
}
function PeerCommand($Command, $Event) {
    Pump
    $after = $primary.Events.Count
    Send $primary $Command
    $end = $clock.Elapsed.TotalSeconds + 15
    while ($clock.Elapsed.TotalSeconds -lt $end) {
        Pump
        for ($index=$after; $index -lt $primary.Events.Count; $index++) {
            if ($primary.Events[$index].event -eq $Event) { return $primary.Events[$index] }
        }
        if ($primary.Process.HasExited) { throw 'Actual primary exited.' }
        Start-Sleep -Milliseconds 40
    }
    throw "Primary did not report $Event."
}
function ReadStatus {
    Pump
    $after = $native.Lines.Count
    Send $native 'telemetry sensors'
    $null = WaitLine $native $after 'Other automatic sensors:'
    return @($native.Lines | Select-Object -Skip $after | Where-Object { $_.Text.StartsWith('propagation_status:') })[-1]
}
function ReadAvailable {
    return NativeCommand 'telemetry' 'lxmf_propagation (Available):'
}
function PauseUntil($At) {
    while ($clock.Elapsed.TotalSeconds -lt $At) { Pump; Start-Sleep -Milliseconds 80 }
}
try {
    AssertProvenance
    @{Candidate=@{Path=$candidatePath;Sha256=$candidateHash};Binary=$frozen.Binary;
        NativeVersioningHead=$frozen.Head;WorkloadHead=$head;Stack=$stack;SdkAtNativeBuild=$frozen.Sdk;
        CompilerAtNativeBuild=$frozen.Compiler;Production=$frozen.Sources;References=$frozen.ReferenceFiles;
        Workload=$workloadHashes;Boundary='Additional workload on the exact historical binary, not a current HEAD rebuild or replacement candidate.'} |
        ConvertTo-Json -Depth 10 | Set-Content (Join-Path $output inputs.json) -Encoding utf8NoBOM
    foreach ($entry in Get-ChildItem Env: | Where-Object { $_.Name -match '^(DOTRETICULUM_|DOTLXST_|DOTSIDEBAND_|PYTHONPATH$|GITHUB_)' }) {
        [Environment]::SetEnvironmentVariable($entry.Name, $null, 'Process')
    }
    foreach ($entry in $frozen.Reference.PSObject.Properties) {
        [Environment]::SetEnvironmentVariable($entry.Name, $entry.Value, 'Process')
    }
    $env:PYTHONPATH = $env:DOTRETICULUM_UPSTREAM
    $env:PYTHONDONTWRITEBYTECODE = '1'
    $env:PYTHONIOENCODING = 'utf-8'
    $env:DOTRETICULUM_LIVE = '1'
    & $frozen.Reference.DOTRETICULUM_PYTHON tests\Interop\reference_environment.py --require-lxmf 2>&1 |
        Tee-Object (Join-Path $output reference.log)
    if ($LASTEXITCODE -ne 0) { throw 'Actual reference import verification failed.' }
    & $frozen.Binary.Path --version 2>&1 | Set-Content (Join-Path $output native-version.txt) -Encoding utf8NoBOM
    if ($LASTEXITCODE -ne 0) { throw 'Historical native version command failed.' }
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    $primaryDirectory = Join-Path $output primary
    $primary = StartObserved 'primary' $frozen.Reference.DOTRETICULUM_PYTHON @(
        (Join-Path $root 'tests\Interop\sideband_propagation_retention_node.py'), '--config', $primaryDirectory, '--port', "$port")
    $null = WaitLine $primary 0 '"event": "ready"' 35
    $ready = @($primary.Events | Where-Object event -EQ ready)[0]
    $actualPrimary = [Diagnostics.Process]::GetProcessById([int]$ready.pid)
    if ($actualPrimary.HasExited) { throw 'Actual primary exited before observation.' }
    $actualPrimaryStartUtc = $actualPrimary.StartTime.ToUniversalTime().ToString('O')
    if ($ready.pid -ne $primary.Pid) {
        $processInfo = Get-CimInstance Win32_Process -Filter "ProcessId = $($ready.pid)"
        if ($processInfo.ParentProcessId -ne $primary.Pid) { throw 'Unexpected Python redirector process relationship.' }
    }
    $actualPrimary.Dispose()
    $network = Join-Path $output network
    $profile = Join-Path $output profile
    $null = New-Item $network -ItemType Directory
    "[reticulum]`n share_instance = no`n enable_transport = no`n[logging]`n loglevel = 0`n[interfaces]`n [[C105 primary]]`n type = TCPClientInterface`n enabled = yes`n target_host = 127.0.0.1`n target_port = $port`n" |
        Set-Content (Join-Path $network config) -Encoding utf8NoBOM
    $native = StartObserved 'native' $frozen.Binary.Path @('--daemon','--interactive','--synthetic-audio','--config',$profile,'--rnsconfig',$network)
    $nativeReady = WaitLine $native 0 'READY identity='
    $escaped = $ready.identity_path.Replace('\','\\').Replace('"','\"')
    $null = NativeCommand ('config set sensors propagation_identity "' + $escaped + '"') 'sensors propagation_identity = '
    $null = NativeCommand 'config set sensors propagation true' 'sensors propagation = true'
    $null = NativeCommand 'telemetry on' 'Telemetry collection enabled.'
    # The worker owns all queries; console report/status reads never force one.
    PauseUntil ($clock.Elapsed.TotalSeconds + 2)
    $success = ReadAvailable
    $uptimeMatch = [regex]::Match($success.Text, '\buptime: ([0-9.Ee+-]+)')
    if (-not $uptimeMatch.Success) { throw 'Console did not expose a source uptime reading.' }
    $firstStatus = ReadStatus
    if ($firstStatus.Text -cne 'propagation_status: Active; ') { throw 'Initial real success has unexpected diagnostics.' }
    $opens = @($primary.Events | Where-Object event -EQ 'control-open')
    if ($opens.Count -ne 1 -or $opens[0].allowed -ne 1) { throw 'Expected one authorized initial control link.' }
    $denied = PeerCommand 'control-policy deny' 'policy'
    if ($denied.allowed) { throw 'Explicit denial policy was not applied.' }
    $observations.Add(@{Case='real-success';Elapsed=$success.ElapsedSeconds;VisibleReading=$success.Text;Status=$firstStatus.Text;
        Uptime=$uptimeMatch.Groups[1].Value;ControlLink=$opens[0];NativePid=$native.Pid;PrimaryPid=$ready.pid;PrimaryLauncherPid=$primary.Pid})
    $next = $clock.Elapsed.TotalSeconds + 18
    $failure = $null
    for ($poll=1; $poll -le 22; $poll++) {
        PauseUntil $next
        $status = ReadStatus
        $reading = ReadAvailable
        if ($reading.Text -cne $success.Text) { throw 'Last successful visible source reading changed under denied policy.' }
        $opens = @($primary.Events | Where-Object event -EQ 'control-open')
        $observations.Add(@{Case='denied-policy-poll';Poll=$poll;Elapsed=$clock.Elapsed.TotalSeconds;
            Status=$status.Text;ReadingUnchanged=$true;ControlLinks=$opens.Count})
        $observations | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $output progress.json) -Encoding utf8NoBOM
        if ($status.Text.Contains('query failed: Timeout; retaining last successful status.')) { $failure=$status; break }
        if ($status.Text -cne 'propagation_status: Active; ') { throw 'Unexpected source state while awaiting its real stale interval.' }
        $next += 18
    }
    if ($null -eq $failure) { throw 'No failed automatic query observed within the bounded real interval.' }
    $opens = @($primary.Events | Where-Object event -EQ 'control-open')
    if ($opens.Count -ne 2 -or $opens[1].allowed -ne 0) { throw 'Expected exactly one denied automatic retry, no hidden queries.' }
    $delta = $opens[1].monotonic - $opens[0].monotonic
    if ($delta -lt 300 -or $delta -gt 390) { throw 'Observed control retry did not respect the real five-minute interval.' }
    $timeout = @($native.Lines | Where-Object { $_.Text.Contains('LXMF propagation telemetry status query failed: Timeout.') })[0]
    if ($timeout.ElapsedSeconds - $success.ElapsedSeconds -lt 300) { throw 'Failed retry was observed before five minutes elapsed.' }
    $observations.Add(@{Case='real-stale-failure-retention';Status=$failure.Text;VisibleReadingUnchanged=$true;
        ControlLinkIntervalSeconds=$delta;NativeTimeoutElapsedSeconds=$timeout.ElapsedSeconds;
        SourceWakeIntervalSeconds=18;ImpliedWakeIntervals=($delta/18);
        Boundary='Public control-link timestamps establish live retry spacing. Individual idle worker wakes and LastAttempt/LastSuccess timestamps are not exported by this console; no direct timing claim for each wake. Displayed map is truncated/redacted by existing console.'})
    $restored = PeerCommand 'control-policy own' 'policy'
    if (-not $restored.allowed) { throw 'Actual allow_control did not restore policy.' }
    $null = NativeCommand 'config set sensors propagation false' 'sensors propagation = false'
    $null = NativeCommand 'config set sensors propagation true' 'sensors propagation = true'
    PauseUntil ($clock.Elapsed.TotalSeconds + 2)
    $recovery = ReadAvailable
    $recoveredUptime = [regex]::Match($recovery.Text, '\buptime: ([0-9.Ee+-]+)')
    if (-not $recoveredUptime.Success -or
        [double]::Parse($recoveredUptime.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture) -le
        [double]::Parse($uptimeMatch.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture)) {
        throw 'Restored source did not produce newer real primary data.'
    }
    $recoveryStatus = ReadStatus
    if ($recoveryStatus.Text -cne 'propagation_status: Active; ') { throw 'Recovery did not clear the failure diagnostic.' }
    $null = NativeCommand 'status' 'LXMF router: active'
    $null = NativeCommand ('conv create ' + $ready.delivery + ' RetentionPeer') ('Conversation created with ' + $ready.delivery)
    $message = 'synthetic-c105-after-real-stale-timeout'
    Send $native ('conv send ' + $ready.delivery + ' ' + $message)
    $messageDigest = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($message)))
    $inbox = $null
    $messageEnd = $clock.Elapsed.TotalSeconds + 35
    while ($clock.Elapsed.TotalSeconds -lt $messageEnd) {
        $observedInbox = PeerCommand 'inbox' 'inbox'
        if (@($observedInbox.messages | Where-Object sha256 -EQ $messageDigest).Count -eq 1) { $inbox=$observedInbox; break }
        PauseUntil ($clock.Elapsed.TotalSeconds + 1)
    }
    if ($null -eq $inbox) { throw 'Continued application messaging did not reach actual primary persisted inbox.' }
    $observations.Add(@{Case='explicit-toggle-recovery-and-continued-messaging';Elapsed=$clock.Elapsed.TotalSeconds;
        VisibleReading=$recovery.Text;Status=$recoveryStatus.Text;Inbox=$inbox;
        NativePidUnchanged=($native.Process.Id -eq $native.Pid);PrimaryPidUnchanged=($inbox.pid -eq $ready.pid)})
    if ($inbox.pid -ne $ready.pid) { throw 'Actual primary process identity changed.' }
    Send $native 'quit'
    $end = $clock.Elapsed.TotalSeconds + 10
    while (-not $native.Process.HasExited -and $clock.Elapsed.TotalSeconds -lt $end) { Pump; Start-Sleep -Milliseconds 80 }
    if (-not $native.Process.HasExited -or $native.Process.ExitCode -ne 0) { throw 'Native did not shut down cleanly.' }
    Send $primary 'quit'
    $end = $clock.Elapsed.TotalSeconds + 10
    while (-not $primary.Process.HasExited -and $clock.Elapsed.TotalSeconds -lt $end) { Pump; Start-Sleep -Milliseconds 80 }
    if (-not $primary.Process.HasExited -or $primary.Process.ExitCode -ne 0) { throw 'Actual primary did not shut down cleanly.' }
    Pump
    AssertProvenance
    @{ClaimId='C112';TestId='T1140';Passed=$true;NativeSha256=$expectedBinary;CandidateSha256=$candidateHash;NativeVersioningHead=$frozen.Head;
        WorkloadHead=$head;ElapsedSeconds=$clock.Elapsed.TotalSeconds;ControlRetrySeconds=$delta;
        NativePid=$native.Pid;NativeStartUtc=$native.StartUtc;PrimaryPid=$ready.pid;PrimaryStartUtc=$actualPrimaryStartUtc;
        PrimaryLauncherPid=$primary.Pid;PrimaryLauncherStartUtc=$primary.StartUtc;
        ActualImports=$ready;Observations=$observations;ProductionInputs=$frozen.Sources.Count;
        Boundary='Additional real Windows/direct-loopback workload on unchanged historical native binary; retained visible source map and explicit stale failure diagnostic, >=300s automatic retry, restored authorization and explicit-toggle recovery, continued persisted delivery. No rebuilt/current-HEAD native, direct individual wake timestamps, untruncated map, Linux/hardware/GUI/hop/loss or bidirectional application messaging claim.'} |
        ConvertTo-Json -Depth 12 | Set-Content (Join-Path $output result.json) -Encoding utf8NoBOM
}
catch {
    @{ClaimId='C112';TestId='T1140';Passed=$false;Error=$_.Exception.Message;ElapsedSeconds=$clock.Elapsed.TotalSeconds;Observations=$observations;
        NativeSha256=$expectedBinary;CandidateSha256=$candidateHash;WorkloadHead=$head} |
        ConvertTo-Json -Depth 10 | Set-Content (Join-Path $output failure.json) -Encoding utf8NoBOM
    throw
}
finally {
    foreach ($state in $processes) {
        if (-not $state.Process.HasExited) { $state.Process.Kill($true); $state.Process.WaitForExit(5000) | Out-Null }
        $state.Process.Dispose()
    }
}
