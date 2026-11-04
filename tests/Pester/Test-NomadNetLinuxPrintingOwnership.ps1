param([Parameter(Mandatory)][string]$ReplayDirectory)
$ErrorActionPreference = 'Stop'
if (-not $IsLinux) { throw 'Linux supervisor ownership requires Linux.' }
$evidence = (Resolve-Path $ReplayDirectory).Path
$receipt = Get-Content (Join-Path $evidence 'replay.json') -Raw | ConvertFrom-Json
$binary = $receipt.binary
if ((Get-FileHash $binary).Hash -ne $receipt.sha256) { throw 'Selected native binary changed.' }
$profile = Join-Path $receipt.workspace 'native profile'
$rns = Join-Path $receipt.workspace 'native-rns'
$message = @((Get-Content (Join-Path $profile 'state.json') -Raw | ConvertFrom-Json).messages)[0]
if (-not $message.id) { throw 'Expected an actually persisted public primary message.' }
$root = Join-Path $evidence 'linux-ownership'
if (Test-Path $root) { throw 'Ownership evidence directory must be absent.' }
$null = New-Item $root -ItemType Directory
$pwsh = Join-Path $PSHOME 'pwsh'
$stub = (Resolve-Path (Join-Path $env:DOTRETICULUM_TEST_REPOSITORY 'tests\Pester\NomadNetPrinterStub.ps1')).Path
$results = [Collections.Generic.List[object]]::new()
function InvokePrivate([string[]]$Arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new($binary)
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $private = [Diagnostics.Process]::Start($info)
    try {
        $stdout = $private.StandardOutput.ReadToEndAsync()
        $stderr = $private.StandardError.ReadToEndAsync()
        if (-not $private.WaitForExit(5000)) { throw 'Private mode did not fail within its bound.' }
        @{exit=$private.ExitCode;stdout=$stdout.GetAwaiter().GetResult();stderr=$stderr.GetAwaiter().GetResult()}
    }
    finally { if (-not $private.HasExited) { $private.Kill(); $private.WaitForExit() }; $private.Dispose() }
}
$invalid = InvokePrivate @('--internal-print-supervisor','--help')
if ($invalid.exit -ne 125 -or $invalid.stdout -or $invalid.stderr.Length -gt 256 -or
    $invalid.stderr -notmatch 'private mode requires a command and private job filename') { throw 'Invalid private mode lacks bounded context.' }
$results.Add(@{case='invalid-private-mode';passed=$true;diagnostic=$invalid.stderr;exit=$invalid.exit})
$jobs = Join-Path $root 'diagnostic\storage\print-jobs'
$null = [IO.Directory]::CreateDirectory($jobs,([IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute))
$jobPath = Join-Path $jobs ([Guid]::NewGuid().ToString('N')+'.txt')
[IO.File]::WriteAllText($jobPath,'PUBLIC DIAGNOSTIC PAYLOAD NOT FOR STDERR')
$missing = InvokePrivate @('--internal-print-supervisor','no-such-public-nomadnet-diagnostic-printer-9348',$jobPath)
if ($missing.exit -ne 125 -or $missing.stdout -or $missing.stderr.Length -gt 256 -or
    $missing.stderr -notmatch 'command startup failed \(Win32Exception, errno=2\)' -or
    $missing.stderr.Contains($jobPath) -or $missing.stderr.Contains('PUBLIC DIAGNOSTIC PAYLOAD') -or
    [IO.File]::Exists($jobPath)) { throw 'Native failure lacks safe operation/errno context or job cleanup.' }
$results.Add(@{case='native-error-context';passed=$true;diagnostic=$missing.stderr;exit=$missing.exit;privateJobRemoved=$true})
function Quote([string]$value) { '"' + $value.Replace('\','\\').Replace('"','\"') + '"' }
function ReadParent([int]$identifier) {
    $line = [IO.File]::ReadAllLines("/proc/$identifier/status") | Where-Object { $_.StartsWith('PPid:') }
    [int]($line -replace '^PPid:\s*','')
}
function ReadSession([int]$identifier) {
    $stat = [IO.File]::ReadAllText("/proc/$identifier/stat")
    [int]($stat.Substring($stat.LastIndexOf(')')+2).Split(' ')[3])
}
function Gone([int]$identifier) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ([IO.Directory]::Exists("/proc/$identifier") -and $watch.Elapsed.TotalSeconds -lt 5) { Start-Sleep -Milliseconds 20 }
    if ([IO.Directory]::Exists("/proc/$identifier")) { throw "Owned observed process was not reaped: $identifier" }
}
foreach ($case in @('orphan-timeout','doublefork-timeout','doublefork-owner-abrupt','literal-help','literal-version')) {
    $directory = Join-Path $root $case
    $null = New-Item $directory -ItemType Directory
    $captures = Join-Path $directory 'public captures'
    $marker = Join-Path $directory 'marker'
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = [Diagnostics.ProcessStartInfo]::new($binary)
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.RedirectStandardInput = $true
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    foreach ($argument in @('--config',$profile,'--rnsconfig',$rns)) { $process.StartInfo.ArgumentList.Add($argument) }
    $script:pending = $null
    $lines = [Text.StringBuilder]::new()
    $stderr = $null
    $started = $false
    function Send([string]$command) { $process.StandardInput.WriteLine($command); $process.StandardInput.Flush() }
    function Read([string]$pattern,[int]$seconds=35) {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while ($watch.Elapsed.TotalSeconds -lt $seconds) {
            if ($null -eq $script:pending) { $script:pending = $process.StandardOutput.ReadLineAsync() }
            try { $line = $script:pending.WaitAsync([TimeSpan]::FromMilliseconds(100)).GetAwaiter().GetResult() }
            catch [TimeoutException] { continue }
            $script:pending = $null
            if ($null -eq $line) { throw 'Native stdout closed unexpectedly.' }
            $null = $lines.AppendLine($line)
            if ($line -match $pattern) { return $line }
        }
        throw "Expected native output: $pattern"
    }
    function Command([string]$mode,[string]$extra='public explicit Linux ownership') {
        @((Quote $pwsh),'-NoProfile -File',(Quote $stub),"-Mode $mode -Output",(Quote $captures),
            '-Marker',(Quote $marker),'-Extra',(Quote $extra)) -join ' '
    }
    try {
        $started = $process.Start()
        if (-not $started) { throw 'Native process did not start.' }
        $stderr = $process.StandardError.ReadToEndAsync()
        $null = Read 'LXMF delivery address:'
        Send 'printing off'
        $null = Read 'Printing configuration saved'
        if ($case.StartsWith('literal-')) {
            $literal = if ($case -eq 'literal-help') { '--help' } else { '--version' }
            Send ('printing command ' + (Command 'capture' $literal))
            $null = Read 'Printing configuration saved'
            Send "print $($message.id)"
            $null = Read 'Message print completed'
            $capture = @(Get-ChildItem $captures -Filter '*.json')
            if ($capture.Count -ne 1 -or (Get-Content $capture[0] -Raw | ConvertFrom-Json).extra -ne $literal) {
                throw 'Printer argument was consumed as a global CLI option.'
            }
            $results.Add(@{case=$case;passed=$true;literal=$literal})
        }
        else {
            $mode = if ($case.StartsWith('doublefork')) { 'doublefork' } else { 'orphan' }
            Send ('printing command ' + (Command $mode))
            $null = Read 'Printing configuration saved'
            $watch = [Diagnostics.Stopwatch]::StartNew()
            Send "print $($message.id)"
            while (-not [IO.File]::Exists("$marker.child") -and $watch.Elapsed.TotalSeconds -lt 12) { Start-Sleep -Milliseconds 20 }
            if (-not [IO.File]::Exists("$marker.child")) { throw 'Descendant did not acquire the public files.' }
            $rootPid = [int][IO.File]::ReadAllText($marker)
            $leafPid = [int][IO.File]::ReadAllText("$marker.child")
            Gone $rootPid
            if ($mode -eq 'doublefork') { Gone ([int][IO.File]::ReadAllText("$marker.middle")) }
            $supervisor = ReadParent $leafPid
            if ($supervisor -eq 1 -or $supervisor -eq $process.Id -or (ReadParent $supervisor) -ne $process.Id) {
                throw 'Orphan was not adopted by the native owner''s dedicated child-subreaper.'
            }
            if ((ReadSession $supervisor) -ne $supervisor) { throw 'Supervisor did not create its private session.' }
            $leafSession = ReadSession $leafPid
            if ($mode -eq 'doublefork' -and $leafSession -eq $supervisor) { throw 'Double-fork fixture did not detach its session.' }
            if (-not ([IO.File]::ReadAllText("/proc/$leafPid/status") -match 'NoNewPrivs:\s+1')) {
                throw 'No-new-privileges was not inherited by the printing process.'
            }
            $held = Join-Path $captures 'held-public-capture.json'
            $locked = $false
            try { $file = [IO.File]::Open($held,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None); $file.Dispose() }
            catch [IO.IOException] { $locked = $true }
            if (-not $locked) { throw 'Public capture was not held exclusively by the descendant.' }
            if ($case.EndsWith('owner-abrupt')) {
                # This handle is the exact native owner created above, never a PID reported by a peer/stub.
                $process.Kill()
                if (-not $process.WaitForExit(5000)) { throw 'Owned native parent did not stop.' }
            }
            else { $null = Read 'Message print failed: Printer timed out' }
            Gone $leafPid
            Gone $supervisor
            if ($watch.Elapsed.TotalSeconds -ge 25) { throw 'Deadline plus cleanup exceeded the bound.' }
            $file = [IO.File]::Open($held,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
            $file.Dispose()
            if (@(Get-ChildItem (Join-Path $profile 'storage\print-jobs') -File).Count) { throw 'Private job survived ownership cleanup.' }
            $results.Add(@{
                case=$case;passed=$true;commandRootExitedFirst=$true;dedicatedSubreaper=$supervisor
                leaf=$leafPid;detachedLeafSession=$leafSession;noNewPrivileges=$true
                leafAndSupervisorReaped=$true;privateJobRemoved=$true;exclusiveCaptureReopened=$true
                ownerAbruptEOF=$case.EndsWith('owner-abrupt');elapsed=$watch.Elapsed.TotalSeconds
                reportedPidKillUsed=$false
            })
            if ($process.HasExited) {
                $process.Dispose()
                $process = [Diagnostics.Process]::new()
                $process.StartInfo = [Diagnostics.ProcessStartInfo]::new($binary)
                $process.StartInfo.UseShellExecute = $false
                $process.StartInfo.RedirectStandardInput = $true
                $process.StartInfo.RedirectStandardOutput = $true
                $process.StartInfo.RedirectStandardError = $true
                foreach ($argument in @('--config',$profile,'--rnsconfig',$rns)) { $process.StartInfo.ArgumentList.Add($argument) }
                $script:pending = $null
                $stderr.GetAwaiter().GetResult() | Set-Content (Join-Path $directory 'abrupt.stderr')
                if (-not $process.Start()) { throw 'Native recovery did not start.' }
                $stderr = $process.StandardError.ReadToEndAsync()
                $null = Read 'LXMF delivery address:'
            }
            Send ('printing command ' + (Command 'capture'))
            $null = Read 'Printing configuration saved'
            Send "print $($message.id)"
            $null = Read 'Message print completed'
            $results[$results.Count-1].healthySubsequentJob = $true
        }
        Send 'quit'
        if (-not $process.WaitForExit(10000) -or $process.ExitCode) { throw 'Native clean shutdown failed.' }
        $archive = Join-Path $directory 'archive'
        $null = New-Item $archive -ItemType Directory
        Get-ChildItem $captures -File | Copy-Item -Destination $archive
        [IO.Directory]::Delete($captures,$true)
        $results[$results.Count-1].strictCaptureDirectoryDeleteWithoutRetry = $true
    }
    finally {
        if ($started -and -not $process.HasExited) { $process.Kill(); $null = $process.WaitForExit(5000) }
        $lines.ToString() | Set-Content (Join-Path $directory 'native.stdout') -Encoding utf8NoBOM
        if ($stderr) { $stderr.GetAwaiter().GetResult() | Set-Content (Join-Path $directory 'native.stderr') }
        $process.Dispose()
        foreach ($release in @("$marker.child.release","$marker.middle.child.release")) {
            if ([IO.File]::Exists($marker)) { [IO.File]::WriteAllText($release,'public cooperative failed-case fallback') }
        }
    }
}
if ((Get-FileHash $binary).Hash -ne $receipt.sha256) { throw 'Native binary changed.' }
@{
    binary=$binary;sha256=$receipt.sha256;stack=$receipt.stack;passed=$results.Count;failed=0
    noPublish=$true;physicalPrinterUsed=$false;cases=$results.ToArray()
    kernel=(& uname -r);libc=((& ldd --version)[0]);fixtureOnlySetsid=(& /usr/bin/setsid --version)
} | ConvertTo-Json -Depth 7 | Set-Content (Join-Path $evidence 'linux-ownership.json') -Encoding utf8NoBOM
Write-Output 'Selected Linux NativeAOT supervisor ownership:7/0 (including abrupt owner EOF, literals and diagnostics).'
