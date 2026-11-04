#Requires -Version 7.0
<#
.SYNOPSIS
Exercises a selected native NomadNet executable against the pinned primary utility controllers.
.DESCRIPTION
Requires both live opt-ins. The Python bridge executes unchanged controllers and Urwid
widgets with a disclosed synthetic headless owner, not a rendered primary application.
Receipts cover the selected executable only, not current-source or compiler provenance.
Linux evidence must reside on local ext4, not a Windows-mounted workspace.
Endurance additionally requires its own opt-in and runs sixteen bidirectional 4 MiB
pairs across at least 600 monotonic seconds in the same two processes.
#>
[CmdletBinding()]
param(
    [string] $Binary = $env:DOTRETICULUM_NOMADNET_BINARY,
    [Parameter(Mandatory)] [string] $EvidenceDirectory,
    [string] $Repository = $env:DOTRETICULUM_TEST_REPOSITORY,
    [string] $Python = $env:DOTRETICULUM_PYTHON,
    [string] $RnsSource = $env:DOTRETICULUM_UPSTREAM,
    [string] $LxmfSource = $env:DOTRETICULUM_LXMF_UPSTREAM,
    [string] $NomadNetSource = $env:DOTRETICULUM_NOMADNET_SOURCE,
    [string] $CandidateLabel = $env:DOTRETICULUM_NOMADNET_UTILITY_CANDIDATE_LABEL,
    [switch] $Endurance,
    [ValidateRange(30, 1100)] [int] $TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
if ($env:DOTRETICULUM_NOMADNET_UTILITIES -ne '1' -or $env:DOTRETICULUM_LIVE -ne '1') {
    throw 'Actual-primary utility acceptance requires DOTRETICULUM_NOMADNET_UTILITIES=1 AND DOTRETICULUM_LIVE=1.'
}
if ($Endurance -and $env:DOTRETICULUM_NOMADNET_UTILITIES_ENDURANCE -ne '1') {
    throw 'Endurance requires DOTRETICULUM_NOMADNET_UTILITIES_ENDURANCE=1 in addition to BOTH live/utilities opt-ins.'
}
if ($Endurance -and -not $PSBoundParameters.ContainsKey('TimeoutSeconds')) { $TimeoutSeconds = 1100 }
if ($Endurance -and $TimeoutSeconds -lt 700) { throw 'Endurance global work deadline must be between 700 and 1100 seconds.' }
if (-not $Repository) { $Repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent }
$EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $EvidenceDirectory) {
    throw 'Evidence directory must be absent; passing and failing receipts are never overwritten.'
}
$null = New-Item -ItemType Directory -Path $EvidenceDirectory
$utf8 = [Text.UTF8Encoding]::new($false)
$peers = [Collections.Generic.List[object]]::new()
$observations = [Collections.Generic.List[object]]::new()
$cleanupFailures = [Collections.Generic.List[string]]::new()
$harness = [Collections.Generic.List[object]]::new()
$referenceBefore = @()
$referenceAfter = @()
$binaryBefore = $null
$binaryAfter = $null
$nativeFormat = $null
$ready = $null
$failure = $null
$complete = $false
$clock = [Diagnostics.Stopwatch]::StartNew()
$enduranceResult = $null
$storageChecks = [Collections.Generic.List[object]]::new()
$pins = @{
    RNS = 'a29bfb51c468945c96a1ee22cae0114e993e2391'
    LXMF = 'e8ba02483217af43be284a72f1c059c730b7d047'
    NomadNet = 'f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2'
}
$utilitiesHash = '61a75d64a244d13e543d7c915d1611bbde87cf28db8450def4424a5634c7c1b1'
$limits = @{ lineCharacters=65536; streamCharacters=4194304; events=1024; lines=4096; commands=1024 }
$knownEvents = @('ready', 'speed-listen', 'speed-send', 'speed-result', 'speed-received',
    'probe-result', 'file-result', 'file-received', 'failure', 'rejection', 'cancelled',
    'stopped', 'status', 'utility-log', 'reference-log', 'rncp-callback',
    'rncp-callback-repeat', 'probe-received', 'announced', 'speed-off', 'rncp-listen')

function Assert-UtilityCondition([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

function Get-UtilityHash([string] $Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-UtilityNativeFormat([string] $Path) {
    if ([IO.Path]::GetFileNameWithoutExtension($Path) -eq 'dotnet' -or
        [IO.Path]::GetExtension($Path) -in '.dll', '.ps1', '.py', '.cmd', '.bat', '.sh') {
        throw 'Select the actual native executable, not dotnet, a managed DLL or a launcher script.'
    }
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        $header = $reader.ReadBytes(64)
        Assert-UtilityCondition ($header.Length -eq 64) 'Native executable header is truncated.'
        if ($IsLinux) {
            Assert-UtilityCondition ($header[0] -eq 127 -and $header[1] -eq 69 -and
                $header[2] -eq 76 -and $header[3] -eq 70) 'Linux requires an actual ELF native executable.'
            return 'ELF'
        }
        Assert-UtilityCondition $IsWindows 'This acceptance workflow currently verifies Windows and Linux only.'
        Assert-UtilityCondition ($header[0] -eq 77 -and $header[1] -eq 90) 'Windows requires a PE native executable.'
        $offset = [BitConverter]::ToInt32($header, 60)
        Assert-UtilityCondition ($offset -ge 64 -and $offset -le $stream.Length - 256) 'Invalid PE offset.'
        $null = $stream.Seek($offset, [IO.SeekOrigin]::Begin)
        Assert-UtilityCondition ($reader.ReadUInt32() -eq 17744) 'Invalid PE signature.'
        $null = $stream.Seek($offset + 24, [IO.SeekOrigin]::Begin)
        $magic = $reader.ReadUInt16()
        Assert-UtilityCondition ($magic -in 267, 523) 'Unsupported PE optional header.'
        $directoryOffset = if ($magic -eq 523) { 112 } else { 96 }
        $null = $stream.Seek($offset + 24 + $directoryOffset + 14 * 8, [IO.SeekOrigin]::Begin)
        Assert-UtilityCondition ($reader.ReadUInt64() -eq 0) 'Managed CLR executable is not a native acceptance candidate.'
        return 'PE without CLR directory'
    }
    finally { $reader.Dispose() }
}

function Get-UtilityReferenceSnapshot {
    $roots = @{ RNS=$RnsSource; LXMF=$LxmfSource; NomadNet=$NomadNetSource }
    foreach ($entry in $roots.GetEnumerator() | Sort-Object Name) {
        $head = (& git -C $entry.Value rev-parse HEAD) -join ''
        if ($LASTEXITCODE) { throw "Cannot inspect reference checkout $($entry.Name)." }
        $dirty = @(& git -C $entry.Value status --porcelain)
        if ($LASTEXITCODE) { throw "Cannot inspect reference status $($entry.Name)." }
        Assert-UtilityCondition ($head -eq $pins[$entry.Name]) "Wrong pinned reference revision: $($entry.Name)."
        Assert-UtilityCondition ($dirty.Count -eq 0) "Reference checkout must be clean: $($entry.Name)."
        $module = switch ($entry.Name) {
            'RNS' { 'RNS/__init__.py' }
            'LXMF' { 'LXMF/__init__.py' }
            'NomadNet' { 'nomadnet/__init__.py' }
        }
        @{ name=$entry.Name; path=$entry.Value; head=$head; dirty=$dirty
            module=(Join-Path $entry.Value $module); moduleSha256=(Get-UtilityHash (Join-Path $entry.Value $module)) }
    }
}

function New-UtilityStream($Reader, [string] $Path, [string] $Name) {
    $buffer = [char[]]::new(4096)
    $log = [IO.StreamWriter]::new($Path, $false, $utf8)
    $log.AutoFlush = $true
    return [pscustomobject]@{
        Reader=$Reader; Buffer=$buffer; Next=$Reader.ReadAsync($buffer, 0, $buffer.Length)
        Pending=[Text.StringBuilder]::new(); Total=0; Closed=$false; Log=$log; Name=$Name
        Path=$Path; Lines=[Collections.Generic.List[string]]::new()
    }
}

function Start-UtilityPeer([string] $Name, [bool] $Primary, [int] $Port) {
    Assert-UtilityCondition ($peers.Count -lt 2) 'Only two owned acceptance processes are permitted.'
    $directory = Join-Path $EvidenceDirectory $Name
    $rns = Join-Path $directory rns
    $homeDirectory = Join-Path $directory home
    $null = New-Item -ItemType Directory -Path $rns, $homeDirectory -Force
    $configuration = "[reticulum]`nshare_instance=no`nenable_transport=no`n[logging]`nloglevel=0`n[interfaces]`n"
    $configuration += if ($Primary) {
        "[[Loopback]]`ntype=TCPServerInterface`nenabled=yes`nlisten_ip=127.0.0.1`nlisten_port=$Port`n"
    } else {
        "[[Loopback]]`ntype=TCPClientInterface`nenabled=yes`ntarget_host=127.0.0.1`ntarget_port=$Port`n"
    }
    $configuration += "announce_cap=100`nbitrate=10000000`ningress_control=no`n"
    [IO.File]::WriteAllText((Join-Path $rns config), $configuration, $utf8)
    $executable = if ($Primary) { $Python } else { $Binary }
    $info = [Diagnostics.ProcessStartInfo]::new($executable)
    $info.WorkingDirectory = $directory
    $info.UseShellExecute = $false
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardInputEncoding = $utf8
    $info.StandardOutputEncoding = $utf8
    $info.StandardErrorEncoding = $utf8
    if ($Primary) {
        $info.ArgumentList.Add('-u')
        $info.ArgumentList.Add((Join-Path $EvidenceDirectory 'source/tests/Interop/nomadnet_utility_peer.py'))
        $info.Environment['PYTHONPATH'] = @($RnsSource, $LxmfSource, $NomadNetSource) -join [IO.Path]::PathSeparator
        $info.Environment['PYTHON_CPU_COUNT'] = '2'
        $info.Environment['DOTRETICULUM_UPSTREAM'] = $RnsSource
        $info.Environment['DOTRETICULUM_LXMF_UPSTREAM'] = $LxmfSource
        $info.Environment['DOTRETICULUM_NOMADNET_SOURCE'] = $NomadNetSource
    }
    foreach ($argument in @('--config', (Join-Path $directory profile), '--rnsconfig', $rns)) {
        $info.ArgumentList.Add($argument)
    }
    foreach ($key in 'TMP', 'TEMP', 'TMPDIR') { $info.Environment[$key] = $directory }
    foreach ($key in 'HOME', 'USERPROFILE') { $info.Environment[$key] = $homeDirectory }
    foreach ($entry in @{ XDG_CONFIG_HOME='.config'; XDG_CACHE_HOME='.cache'; XDG_DATA_HOME='.local/share' }.GetEnumerator()) {
        $info.Environment[$entry.Key] = Join-Path $homeDirectory $entry.Value
    }
    $selectedEnvironment = @{}
    foreach ($key in 'PYTHONPATH', 'PYTHON_CPU_COUNT', 'DOTRETICULUM_UPSTREAM',
        'DOTRETICULUM_LXMF_UPSTREAM', 'DOTRETICULUM_NOMADNET_SOURCE', 'TMP', 'TEMP', 'TMPDIR',
        'HOME', 'USERPROFILE', 'XDG_CONFIG_HOME', 'XDG_CACHE_HOME', 'XDG_DATA_HOME', 'DOTNET_ROOT') {
        $selectedEnvironment[$key] = $info.Environment[$key]
    }
    $process = [Diagnostics.Process]::Start($info)
    $inputLog = $outputStream = $errorStream = $null
    try {
        $inputLog = [IO.StreamWriter]::new((Join-Path $directory commands.txt), $false, $utf8)
        $inputLog.AutoFlush = $true
        $outputStream = New-UtilityStream $process.StandardOutput (Join-Path $directory stdout.log) stdout
        $errorStream = New-UtilityStream $process.StandardError (Join-Path $directory stderr.log) stderr
        $peer = [pscustomobject]@{
            Name=$Name; Primary=$Primary; Process=$process; InputLog=$inputLog; CommandCount=0
            Output=$outputStream; Error=$errorStream
            Events=[Collections.Generic.List[object]]::new(); Failures=[Collections.Generic.List[string]]::new()
            KnownReferenceErrors=[Collections.Generic.List[string]]::new()
            Launch=@{ executable=$executable; arguments=@($info.ArgumentList); cwd=$directory
                selectedEnvironment=$selectedEnvironment }
        }
        $peers.Add($peer)
        return $peer
    } catch {
        if (-not $process.HasExited) { $process.Kill($true); $null = $process.WaitForExit(10000) }
        foreach ($stream in @($outputStream, $errorStream)) {
            if ($stream) { $stream.Log.Dispose(); $stream.Reader.Dispose() }
        }
        if ($inputLog) { $inputLog.Dispose() }
        $process.Dispose()
        throw
    }
}

function Add-UtilityLine($Peer, $Stream, [string] $Line) {
    Assert-UtilityCondition ($Line.Length -le $limits.lineCharacters) 'Process line exceeds 64 KiB character bound.'
    Assert-UtilityCondition ($Stream.Lines.Count -lt $limits.lines) 'Process line backlog exceeded its bound.'
    $Stream.Lines.Add($Line)
    if ($Stream.Name -eq 'stderr') {
        if ($Line -and ($Peer.Primary -or $Line -match
            'disposed object|Utility failed:|Incoming speed test failed|Could not read completed speed test|cancelled or timed out')) {
            $Peer.Failures.Add("$($Peer.Name) stderr: $Line")
        }
        return
    }
    if (-not $Peer.Primary -or -not $Line) { return }
    if (-not $Line.StartsWith('@@', [StringComparison]::Ordinal)) {
        $Peer.Failures.Add("Unframed primary stdout: $($Line.Substring(0, [Math]::Min(2048, $Line.Length)))")
        return
    }
    try {
        $event = $Line.Substring(2) | ConvertFrom-Json -Depth 16 -ErrorAction Stop
        if ($event -isnot [pscustomobject] -or $event.event -notin $knownEvents) { throw 'Unknown/missing event name.' }
    } catch {
        $Peer.Failures.Add("Invalid primary JSON frame: $($_.Exception.Message)")
        return
    }
    Assert-UtilityCondition ($Peer.Events.Count -lt $limits.events) 'Primary event backlog exceeded its bound.'
    $Peer.Events.Add($event)
    if ($event.event -eq 'failure') {
        $Peer.Failures.Add("Primary adapter failure: $($event.operation): $($event.error)")
    } elseif ($event.event -eq 'reference-log' -and $event.message -match '\[Error\]') {
        if ($IsWindows -and $event.message -match 'An error occurred while saving received resource:.*WinError 32') {
            $Peer.KnownReferenceErrors.Add([string]$event.message)
        } else {
            $Peer.Failures.Add("Unexpected reference error: $($event.message)")
        }
    }
}

function Receive-UtilityOutput {
    foreach ($peer in $peers) {
        foreach ($stream in @($peer.Output, $peer.Error)) {
            for ($batch = 0; $batch -lt 32 -and -not $stream.Closed -and $stream.Next.IsCompleted; $batch++) {
                $count = $stream.Next.GetAwaiter().GetResult()
                if ($count -eq 0) {
                    $stream.Closed = $true
                    if ($stream.Pending.Length) {
                        Add-UtilityLine $peer $stream $stream.Pending.ToString().TrimEnd("`r")
                        $null = $stream.Pending.Clear()
                    }
                    break
                }
                $stream.Total += $count
                Assert-UtilityCondition ($stream.Total -le $limits.streamCharacters) 'Process stream exceeded its 4 MiB character bound.'
                $stream.Log.Write($stream.Buffer, 0, $count)
                $null = $stream.Pending.Append($stream.Buffer, 0, $count)
                $text = $stream.Pending.ToString()
                $start = 0
                while (($end = $text.IndexOf("`n", $start, [StringComparison]::Ordinal)) -ge 0) {
                    Add-UtilityLine $peer $stream $text.Substring($start, $end - $start).TrimEnd("`r")
                    $start = $end + 1
                }
                $null = $stream.Pending.Clear()
                $null = $stream.Pending.Append($text.Substring($start))
                Assert-UtilityCondition ($stream.Pending.Length -le $limits.lineCharacters) 'Unterminated process line exceeded its bound.'
                $stream.Next = $stream.Reader.ReadAsync($stream.Buffer, 0, $stream.Buffer.Length)
            }
        }
    }
}

function Assert-UtilityProcesses {
    foreach ($peer in $peers) {
        if ($peer.Failures.Count) { throw ($peer.Failures -join "`n") }
        Assert-UtilityCondition (-not $peer.Process.HasExited) "$($peer.Name) exited during acceptance."
    }
    Assert-UtilityCondition ($clock.Elapsed.TotalSeconds -lt $TimeoutSeconds) 'Overall monotonic utility acceptance deadline exceeded.'
}

function Send-UtilityCommand($Peer, [string] $Command) {
    Assert-UtilityCondition ($Command -notmatch '[\r\n]') 'Commands must contain exactly one line.'
    Assert-UtilityCondition ($utf8.GetByteCount($Command) -le 65536) 'Command exceeds 64 KiB.'
    Assert-UtilityCondition ($Peer.CommandCount -lt $limits.commands) 'Command count exceeded its bound.'
    $Peer.CommandCount++
    $Peer.InputLog.WriteLine($Command)
    $Peer.Process.StandardInput.WriteLine($Command)
    $Peer.Process.StandardInput.Flush()
}

function Send-UtilityReference($Peer, $Command) {
    Send-UtilityCommand $Peer ($Command | ConvertTo-Json -Depth 8 -Compress)
}

function Wait-UtilityEvent($Peer, [string] $Name, [int] $After = 0, [int] $WaitSeconds = 90) {
    $until = $clock.Elapsed.TotalSeconds + $WaitSeconds
    do {
        Receive-UtilityOutput
        Assert-UtilityProcesses
        for ($index = $After; $index -lt $Peer.Events.Count; $index++) {
            if ($Peer.Events[$index].event -eq $Name) { return $Peer.Events[$index] }
        }
        Assert-UtilityCondition (-not $Peer.Process.HasExited) "$($Peer.Name) exited before event $Name."
        Start-Sleep -Milliseconds 20
    } while ($clock.Elapsed.TotalSeconds -lt $until)
    throw "Timeout waiting for primary event $Name."
}

function Wait-UtilityText($Peer, [string] $Pattern, [int] $After = 0, [int] $WaitSeconds = 90) {
    $until = $clock.Elapsed.TotalSeconds + $WaitSeconds
    do {
        Receive-UtilityOutput
        Assert-UtilityProcesses
        for ($index = $After; $index -lt $Peer.Output.Lines.Count; $index++) {
            if ($Peer.Output.Lines[$index] -match $Pattern) { return $Peer.Output.Lines[$index] }
        }
        Assert-UtilityCondition (-not $Peer.Process.HasExited) "$($Peer.Name) exited before $Pattern."
        Start-Sleep -Milliseconds 20
    } while ($clock.Elapsed.TotalSeconds -lt $until)
    throw "Timeout waiting for native output $Pattern."
}

function Assert-UtilityResponsive {
    Receive-UtilityOutput
    Assert-UtilityProcesses
    $nativeIndex = $native.Output.Lines.Count
    Send-UtilityCommand $native 'utility status'
    $null = Wait-UtilityText $native '(?:^|nomadnet> )Utility idle\.$' $nativeIndex 10
    Send-UtilityCommand $native 'utility identity'
    $identity = Wait-UtilityText $native 'Speed test: ([0-9a-f]{32})' $nativeIndex 10
    Assert-UtilityCondition ($identity -match [regex]::Escape($nativeSpeed)) 'Native speed identity changed during service.'
    $identityLines = @($native.Output.Lines | Select-Object -Skip $nativeIndex |
        Where-Object { $_ -match "Identity: $nativeIdentity" })
    Assert-UtilityCondition ($identityLines.Count -eq 1) 'Native identity is not stable/responsive.'
    $primaryIndex = $primary.Events.Count
    Send-UtilityReference $primary @{ op='status' }
    $status = Wait-UtilityEvent $primary status $primaryIndex 10
    Assert-UtilityCondition ($status.listening -eq $true -and $status.sending -eq $false -and
        $status.probing -eq $false -and $status.file_sending -eq $false) 'Primary status is not idle/listening.'
    return @{ seconds=$clock.Elapsed.TotalSeconds; nativePid=$native.Process.Id
        primaryPid=$primary.Process.Id; primary=$status; nativeIdentity=$nativeIdentity; nativeSpeed=$nativeSpeed }
}

function Assert-UtilityStorage([string] $Phase) {
    $root = Join-Path $EvidenceDirectory native
    $checked = [Collections.Generic.List[string]]::new()
    $pending = [Collections.Generic.Queue[string]]::new()
    $pending.Enqueue($root)
    $examined = 0
    while ($pending.Count) {
        $directory = $pending.Dequeue()
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
            $examined++
            Assert-UtilityCondition ($examined -le 4096) 'Native storage inspection exceeded its finite entry bound.'
            $attributes = [IO.File]::GetAttributes($entry)
            Assert-UtilityCondition (($attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Native storage contains an unexpected reparse point.'
            if ($attributes -band [IO.FileAttributes]::Directory) { $pending.Enqueue($entry); continue }
            $name = [IO.Path]::GetFileName($entry)
            if ($name -eq '.dotreticulum-resource-storage') {
                Assert-UtilityCondition ([IO.FileInfo]::new($entry).Length -eq $utf8.GetByteCount("DotReticulum resource storage v1`n") -and
                    [IO.File]::ReadAllText($entry) -ceq "DotReticulum resource storage v1`n") 'Invalid resource ownership marker.'
                $checked.Add($entry)
            } elseif ($entry -match '[/\\]utility-work[/\\]' -or
                $name -match '^(resource-|input-|speed-)' -or $entry -match '[/\\]resources[/\\]') {
                throw "Leftover native private staging/resource payload: $entry"
            }
        }
    }
    return @{ phase=$Phase; examined=$examined; validMarkers=$checked.ToArray(); leftoverPayloads=0 }
}

function Invoke-UtilitySpeedPair([int] $Number) {
    $started = $clock.Elapsed.TotalSeconds
    $nativeIndex = $native.Output.Lines.Count
    $primaryIndex = $primary.Events.Count
    Send-UtilityCommand $native "utility speed send $($ready.speed) 4 MB"
    $upload = Wait-UtilityText $native 'Upload: bytes=4194304 .*sha256=[0-9a-f]{64}' $nativeIndex
    $received = Wait-UtilityEvent $primary speed-received $primaryIndex
    $digest = [regex]::Match($upload, 'sha256=([0-9a-f]{64})').Groups[1].Value
    Assert-UtilityCondition ($received.status -eq 'complete' -and $received.bytes -eq 4194304 -and
        $received.sha256 -eq $digest) 'Endurance native-to-primary status/length/digest differs.'
    $primaryIndex = $primary.Events.Count
    $nativeIndex = $native.Output.Lines.Count
    Send-UtilityReference $primary @{ op='speed-send'; peer=$nativeSpeed; size=4194304 }
    $sent = Wait-UtilityEvent $primary speed-result $primaryIndex
    Assert-UtilityCondition ($sent.status -eq 'complete' -and $sent.bytes -eq 4194304 -and
        $sent.sha256 -match '^[0-9a-f]{64}$') 'Endurance actual-primary sender failed.'
    $until = $clock.Elapsed.TotalSeconds + 15
    do {
        $pollIndex = $native.Output.Lines.Count
        Send-UtilityCommand $native 'utility speed status'
        Send-UtilityCommand $native 'utility identity'
        $null = Wait-UtilityText $native 'Speed test: [0-9a-f]{32}' $pollIndex 10
        $download = @($native.Output.Lines | Select-Object -Skip $nativeIndex |
            Where-Object { $_ -match "Download: bytes=4194304 .*sha256=$($sent.sha256)" } |
            Select-Object -Last 1)
        if (-not $download.Count) { Start-Sleep -Milliseconds 100 }
    } until ($download.Count -or $clock.Elapsed.TotalSeconds -ge $until)
    Assert-UtilityCondition ($download.Count -eq 1) 'Endurance primary-to-native length/digest did not complete.'
    $responsive = Assert-UtilityResponsive
    return @{ pair=$Number; startSeconds=$started; endSeconds=$clock.Elapsed.TotalSeconds
        upload=$upload; primaryReceived=$received; primarySent=$sent; download=$download[0]; responsive=$responsive }
}

try {
    foreach ($entry in @{ Binary=$Binary; Python=$Python; RnsSource=$RnsSource;
        LxmfSource=$LxmfSource; NomadNetSource=$NomadNetSource; Repository=$Repository }.GetEnumerator()) {
        Assert-UtilityCondition (-not [string]::IsNullOrWhiteSpace($entry.Value)) "Selected $($entry.Key) is required."
        Assert-UtilityCondition (Test-Path -LiteralPath $entry.Value) "Selected $($entry.Key) does not exist on this host."
    }
    $Binary = (Resolve-Path -LiteralPath $Binary).Path
    $Python = (Resolve-Path -LiteralPath $Python).Path
    $Repository = (Resolve-Path -LiteralPath $Repository).Path
    $RnsSource = (Resolve-Path -LiteralPath $RnsSource).Path
    $LxmfSource = (Resolve-Path -LiteralPath $LxmfSource).Path
    $NomadNetSource = (Resolve-Path -LiteralPath $NomadNetSource).Path
    if ($IsLinux) {
        $filesystem = (& stat -f -c '%T' -- $EvidenceDirectory) -join ''
        Assert-UtilityCondition ($LASTEXITCODE -eq 0 -and $filesystem -eq 'ext2/ext3') 'Linux acceptance requires local ext4 evidence/cwd storage.'
    } else { $filesystem = 'Windows filesystem' }
    $nativeFormat = Get-UtilityNativeFormat $Binary
    $binaryBefore = Get-UtilityHash $Binary
    if ($env:DOTRETICULUM_NOMADNET_BINARY_SHA256) {
        Assert-UtilityCondition ($binaryBefore -eq $env:DOTRETICULUM_NOMADNET_BINARY_SHA256) 'Selected binary SHA256 differs from the explicit expected hash.'
    }
    $referenceBefore = @(Get-UtilityReferenceSnapshot)
    foreach ($relative in 'tests/Pester/Invoke-NomadNetUtilityAcceptance.ps1',
        'tests/Pester/NomadNetUtilitiesLive.Tests.ps1', 'tests/Interop/nomadnet_utility_peer.py',
        'tests/Interop/GenerateNomadNetClientRuntimeVectors.py', 'tests/Interop/nomadnet_live_node.py',
        'tests/Interop/reference_environment.py') {
        $target = Join-Path $EvidenceDirectory "source/$relative"
        $null = New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force
        Copy-Item -LiteralPath (Join-Path $Repository $relative) -Destination $target
        $harness.Add(@{ path=$relative; sha256=(Get-UtilityHash $target) })
    }
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try { $listener.Start(); $port = $listener.LocalEndpoint.Port }
    finally { $listener.Stop() }
    $primary = Start-UtilityPeer primary $true $port
    $ready = Wait-UtilityEvent $primary ready
    Assert-UtilityCondition ($ready.revision -eq $pins.NomadNet -and
        $ready.utilities_sha256 -eq $utilitiesHash) 'Primary ready source provenance differs.'
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    foreach ($snapshot in $referenceBefore) {
        $importName = if ($snapshot.name -eq 'NomadNet') { 'nomadnet' } else { $snapshot.name }
        Assert-UtilityCondition ([string]::Equals([IO.Path]::GetFullPath($ready.imports.$importName),
            [IO.Path]::GetFullPath($snapshot.module), $comparison)) "Wrong actual imported $importName module."
    }
    foreach ($address in 'identity', 'speed', 'probe', 'rncp') {
        Assert-UtilityCondition ($ready.$address -match '^[0-9a-f]{32}$') "Invalid primary $address address."
    }
    Assert-UtilityCondition ($ready.owner -match 'synthetic minimal app' -and
        $ready.rncp_receiver -match 'actual RNS.Utilities.rncp') 'Primary bridge ownership disclosure missing.'
    $native = Start-UtilityPeer native $false $port
    Send-UtilityCommand $native 'utility identity'
    $identityOutput = Wait-UtilityText $native 'Speed test: ([0-9a-f]{32})'
    $nativeSpeed = [regex]::Match($identityOutput, 'Speed test: ([0-9a-f]{32})').Groups[1].Value
    $nativeIdentity = [regex]::Match(($native.Output.Lines -join "`n"), 'Identity: ([0-9a-f]{32})').Groups[1].Value
    Assert-UtilityCondition ($nativeIdentity.Length -eq 32) 'Native identity output framing differs.'
    $observations.Add(@{ case='framing-imports-and-explicit-paths'; primary=$ready
        nativeIdentity=$nativeIdentity; nativeSpeed=$nativeSpeed })
    Send-UtilityReference $primary @{ op='speed-listen'; peer=$nativeSpeed }
    $null = Wait-UtilityEvent $primary speed-listen
    Send-UtilityCommand $native "utility speed listen $($ready.speed)"
    $null = Wait-UtilityText $native 'Speed-test listener:'
    $nativeIndex = $native.Output.Lines.Count
    $primaryIndex = $primary.Events.Count
    Send-UtilityCommand $native "utility speed send $($ready.speed) 4 MB"
    $upload = Wait-UtilityText $native 'Upload: bytes=4194304 .*sha256=[0-9a-f]{64}' $nativeIndex
    $received = Wait-UtilityEvent $primary speed-received $primaryIndex
    $digest = [regex]::Match($upload, 'sha256=([0-9a-f]{64})').Groups[1].Value
    Assert-UtilityCondition ($received.status -eq 'complete' -and $received.bytes -eq 4194304 -and
        $received.sha256 -eq $digest) 'Native-to-primary speed status/length/digest differs.'
    $observations.Add(@{ case='native-to-primary-4MiB'; native=$upload; primary=$received })
    $nativeIndex = $native.Output.Lines.Count
    Send-UtilityCommand $native "utility probe $($ready.probe_name) $($ready.probe) 16 3"
    $probe = Wait-UtilityText $native 'Probe sent=3 received=3 loss=0.0%' $nativeIndex
    $observations.Add(@{ case='native-probe-to-primary-proof-destination'; native=$probe })
    $primaryIndex = $primary.Events.Count
    Send-UtilityReference $primary @{ op='rncp-listen'; identity=$nativeIdentity }
    $null = Wait-UtilityEvent $primary rncp-listen $primaryIndex
    $file = Join-Path $EvidenceDirectory 'synthetic public payload file.bin'
    $bytes = [byte[]]::new(1048576)
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    [IO.File]::WriteAllBytes($file, $bytes)
    $digest = Get-UtilityHash $file
    $nativeIndex = $native.Output.Lines.Count
    $primaryIndex = $primary.Events.Count
    Send-UtilityCommand $native "utility send $($ready.rncp) $file"
    $delivered = Wait-UtilityText $native 'Utility file delivered: bytes=1048576' $nativeIndex
    $received = Wait-UtilityEvent $primary file-received $primaryIndex
    Assert-UtilityCondition ($received.status -eq 'complete' -and $received.saved -eq $true -and
        $received.bytes -eq 1048576 -and $received.sha256 -eq $digest -and
        $received.name -eq [IO.Path]::GetFileName($file)) 'Native file transfer status/path/length/digest differs.'
    $savedFile = Join-Path $EvidenceDirectory 'primary/profile/received/synthetic public payload file.bin'
    Assert-UtilityCondition ((Get-Item -LiteralPath $savedFile).Length -eq 1048576 -and
        (Get-UtilityHash $savedFile) -eq $digest) 'Actual rncp persisted file differs.'
    $referenceErrors = @($received.reference_errors)
    if ($IsWindows) {
        Assert-UtilityCondition ($referenceErrors.Count -eq 1 -and $primary.KnownReferenceErrors.Count -eq 1 -and
            $referenceErrors[0] -eq $primary.KnownReferenceErrors[0] -and
            $referenceErrors[0] -match 'An error occurred while saving received resource:.*WinError 32') `
            'Expected disclosed upstream Windows rncp open-file move error was not observed exactly once.'
    } else {
        Assert-UtilityCondition ($referenceErrors.Count -eq 0 -and $primary.KnownReferenceErrors.Count -eq 0) `
            'Unexpected upstream rncp reference errors on Linux.'
    }
    $observations.Add(@{ case='native-file-to-actual-rncp-listener-1MiB'; path=$file
        native=$delivered; primary=$received; referenceErrorsAsserted=$true })
    $primaryIndex = $primary.Events.Count
    $nativeIndex = $native.Output.Lines.Count
    Send-UtilityReference $primary @{ op='speed-send'; peer=$nativeSpeed; size=4194304 }
    $sent = Wait-UtilityEvent $primary speed-result $primaryIndex
    Assert-UtilityCondition ($sent.status -eq 'complete' -and $sent.bytes -eq 4194304 -and
        $sent.sha256 -match '^[0-9a-f]{64}$') 'Actual primary speed sender did not complete.'
    $download = $null
    $until = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $pollIndex = $native.Output.Lines.Count
        Send-UtilityCommand $native 'utility speed status'
        Send-UtilityCommand $native 'utility identity'
        $null = Wait-UtilityText $native 'Speed test: [0-9a-f]{32}' $pollIndex
        $download = @($native.Output.Lines | Select-Object -Skip $nativeIndex |
            Where-Object { $_ -match 'Download: bytes=4194304 .*sha256=[0-9a-f]{64}' } |
            Select-Object -Last 1)
        if (-not $download.Count) { Start-Sleep -Milliseconds 100 }
    } until ($download.Count -or [DateTime]::UtcNow -ge $until)
    Assert-UtilityCondition ($download.Count -eq 1) 'Native receiver did not publish completion after primary proof.'
    $digest = [regex]::Match($download[0], 'sha256=([0-9a-f]{64})').Groups[1].Value
    Assert-UtilityCondition ($sent.sha256 -eq $digest) 'Primary-to-native speed digest differs.'
    $observations.Add(@{ case='primary-to-native-4MiB'; native=$download[0]; primary=$sent })
    if ($Endurance) {
        $serviceStart = $clock.Elapsed.TotalSeconds
        $nativePid = $native.Process.Id
        $primaryPid = $primary.Process.Id
        $pairs = [Collections.Generic.List[object]]::new()
        $heartbeats = [Collections.Generic.List[object]]::new()
        $enduranceResult = [ordered]@{
            minimumSeconds=600; requiredPairs=16; intervalSeconds=40
            startSeconds=$serviceStart; endSeconds=$null; measuredSeconds=$null
            nativePid=$nativePid; primaryPid=$primaryPid
            pairs=$pairs; heartbeats=$heartbeats; completed=$false
        }
        for ($number = 1; $number -le 16; $number++) {
            $scheduled = $serviceStart + ($number - 1) * 40
            if ($pairs.Count) { $scheduled = [Math]::Max($scheduled, $pairs[$pairs.Count - 1].startSeconds + 40) }
            while ($clock.Elapsed.TotalSeconds -lt $scheduled) {
                $heartbeats.Add((Assert-UtilityResponsive))
                $remaining = $scheduled - $clock.Elapsed.TotalSeconds
                if ($remaining -gt 0) { Start-Sleep -Milliseconds ([int]([Math]::Min(5000, $remaining * 1000))) }
            }
            Assert-UtilityCondition ($native.Process.Id -eq $nativePid -and
                $primary.Process.Id -eq $primaryPid -and $peers.Count -eq 2) 'Endurance processes were replaced.'
            $pairs.Add((Invoke-UtilitySpeedPair $number))
        }
        $heartbeats.Add((Assert-UtilityResponsive))
        $enduranceResult.endSeconds = $clock.Elapsed.TotalSeconds
        $enduranceResult.measuredSeconds = $enduranceResult.endSeconds - $serviceStart
        Assert-UtilityCondition ($enduranceResult.measuredSeconds -ge 600 -and $pairs.Count -eq 16 -and
            $pairs[15].startSeconds - $pairs[0].startSeconds -ge 600) 'Endurance duration or spaced pair coverage is incomplete.'
        $storageChecks.Add((Assert-UtilityStorage 'alive-after-endurance'))
        $enduranceResult.completed = $true
    }
    Assert-UtilityProcesses
    $complete = $true
}
catch { $failure = $_.Exception.ToString() }
finally {
    foreach ($peer in $peers) {
        try {
            if (-not $peer.Process.HasExited) {
                if ($peer.Primary) { Send-UtilityReference $peer @{ op='quit' } }
                else { Send-UtilityCommand $peer quit }
            }
        } catch { $cleanupFailures.Add($_.Exception.Message) }
    }
    $cleanupUntil = $clock.Elapsed.TotalSeconds + 30
    do {
        try { Receive-UtilityOutput } catch { $cleanupFailures.Add($_.Exception.Message); break }
        if (@($peers | Where-Object { -not $_.Process.HasExited }).Count -eq 0) { break }
        Start-Sleep -Milliseconds 20
    } while ($clock.Elapsed.TotalSeconds -lt $cleanupUntil)
    foreach ($peer in $peers) {
        try {
            if (-not $peer.Process.HasExited) {
                $cleanupFailures.Add("$($peer.Name) required forced process-tree termination.")
                $peer.Process.Kill($true)
                $null = $peer.Process.WaitForExit(10000)
            }
        } catch { $cleanupFailures.Add($_.Exception.Message) }
    }
    $drainUntil = $clock.Elapsed.TotalSeconds + 10
    do {
        try { Receive-UtilityOutput } catch { $cleanupFailures.Add($_.Exception.Message); break }
        if (@($peers | Where-Object { -not $_.Output.Closed -or -not $_.Error.Closed }).Count -eq 0) { break }
        Start-Sleep -Milliseconds 20
    } while ($clock.Elapsed.TotalSeconds -lt $drainUntil)
    foreach ($peer in $peers) {
        if (-not $peer.Output.Closed -or -not $peer.Error.Closed) {
            $cleanupFailures.Add("$($peer.Name) streams did not close within the drain deadline.")
        }
        if (-not $peer.Process.HasExited -or $peer.Process.ExitCode -ne 0) {
            $cleanupFailures.Add("$($peer.Name) did not exit with status zero.")
        }
        if ($peer.Primary -and @($peer.Events | Where-Object { $_.event -eq 'stopped' -and $_.exit_code -eq 0 }).Count -ne 1) {
            $cleanupFailures.Add('Primary did not emit exactly one successful stopped frame after cleanup.')
        }
        try {
            $peer.Output.Log.Dispose()
            $peer.Error.Log.Dispose()
            $peer.InputLog.Dispose()
            $peer.Output.Reader.Dispose()
            $peer.Error.Reader.Dispose()
            $peer.Process.StandardInput.Dispose()
        } catch { $cleanupFailures.Add($_.Exception.Message) }
    }
    try {
        if ($Endurance -and $peers.Count -eq 2) {
            $storageChecks.Add((Assert-UtilityStorage 'after-owned-shutdown'))
        }
        if ($binaryBefore) {
            $binaryAfter = Get-UtilityHash $Binary
            if ($binaryBefore -ne $binaryAfter) { $cleanupFailures.Add('Selected executable changed during acceptance.') }
        }
        if ($referenceBefore.Count) {
            $referenceAfter = @(Get-UtilityReferenceSnapshot)
            if (($referenceBefore | ConvertTo-Json -Depth 8 -Compress) -ne
                ($referenceAfter | ConvertTo-Json -Depth 8 -Compress)) {
                $cleanupFailures.Add('Pinned reference checkout snapshot changed during acceptance.')
            }
        }
    } catch { $cleanupFailures.Add($_.Exception.Message) }
    $runtimeFailures = @($peers | ForEach-Object { $_.Failures.ToArray() })
    if ($clock.Elapsed.TotalSeconds -gt 1200) { $cleanupFailures.Add('Total monotonic acceptance deadline exceeded 1200 seconds.') }
    if ($cleanupFailures.Count -or $runtimeFailures.Count) { $complete = $false }
    $receipt = [ordered]@{
        schema=1; status=$(if ($complete) { 'complete' } else { 'failed' }); success=$complete
        utc=[DateTime]::UtcNow.ToString('o'); failure=$failure; cleanupFailures=$cleanupFailures.ToArray()
        runtimeFailures=$runtimeFailures; evidenceDirectory=$EvidenceDirectory
        binary=$Binary; binarySha256Before=$binaryBefore; binarySha256After=$binaryAfter; nativeFormat=$nativeFormat
        candidateLabel=$CandidateLabel; repository=$Repository; referenceBefore=$referenceBefore; referenceAfter=$referenceAfter
        observations=$observations.ToArray(); harness=$harness.ToArray(); limits=$limits; timeoutSeconds=$TimeoutSeconds
        mode=$(if ($Endurance) { 'endurance' } else { 'standard' })
        endurance=$enduranceResult; storageChecks=$storageChecks.ToArray(); monotonicSeconds=$clock.Elapsed.TotalSeconds
        launches=@($peers | ForEach-Object { $_.Launch })
        exits=@($peers | ForEach-Object { @{ name=$_.Name; exited=$_.Process.HasExited
            code=$(if ($_.Process.HasExited) { $_.Process.ExitCode } else { $null }) } })
        logs=@($peers | ForEach-Object { @{ name=$_.Name; stdout=$_.Output.Path; stderr=$_.Error.Path
            stdoutCharacters=$_.Output.Total; stderrCharacters=$_.Error.Total } })
        topology='Two actual processes over isolated IPv4 loopback direct TCP; no transport hop or physical/hardware claim.'
        primaryOwner='Unchanged pinned primary utility controllers/widgets with disclosed synthetic headless app/UI scheduling and selection; not full NomadNetworkApp startup or terminal rendering.'
        probeReceiver='Real explicit RNS PROVE_ALL nomadnetwork.utilities.probe endpoint, not a claimed primary probe service.'
        fileReceiver='Actual RNS.Utilities.rncp public listener callbacks with explicit identity allowlist and isolated cwd; not a NomadNet receiver.'
        windowsRncp='Unmodified upstream may copy the open resource successfully then log WinError32 on source unlink. Exact error and persisted bytes are independently asserted and retained, not suppressed or called an error-free reference run.'
        scope='Selected native executable only. This helper neither builds nor certifies current-source/compiler provenance. Unmodified primary teardown is never delayed.'
        dotnetRoot=$env:DOTNET_ROOT
        dotnetOnPath=$(if (Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue) {
            (Get-Command dotnet -CommandType Application).Source
        } else { $null })
    }
    $receiptPath = Join-Path $EvidenceDirectory receipt.json
    $receipt | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $receiptPath -Encoding utf8NoBOM
    Write-Host "NomadNet utility acceptance $($receipt.status): $receiptPath"
    foreach ($peer in $peers) { $peer.Process.Dispose() }
}
if (-not $complete) { throw "Selected native/primary utility acceptance failed; retained receipt: $receiptPath`n$failure" }
[pscustomobject]$receipt
