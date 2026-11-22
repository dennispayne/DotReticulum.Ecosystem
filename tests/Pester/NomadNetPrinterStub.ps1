param(
    [ValidateSet('capture', 'fail', 'fail125', 'hang', 'flood', 'childhang', 'orphan', 'orphanstream', 'orphan-session', 'doublefork', 'holdcapture')]
    [string] $Mode = 'capture',
    [Parameter(Mandatory)] [string] $Output,
    [string] $Marker,
    [string] $Extra,
    [Parameter(ValueFromRemainingArguments)] [string[]] $JobPaths
)
$ErrorActionPreference = 'Stop'
$null = New-Item $Output -ItemType Directory -Force
if ($JobPaths.Count -ne 1 -or -not [IO.File]::Exists($JobPaths[0])) { throw 'Expected one existing native print job.' }
if ($Marker -and $Mode -ne 'holdcapture') { [IO.File]::WriteAllText($Marker, [string]$PID) }
if ($Mode -eq 'fail') { exit 7 }
if ($Mode -eq 'fail125') { exit 125 }
if ($Mode -eq 'hang') { Start-Sleep -Seconds 90; exit 0 }
if ($Mode -eq 'childhang') {
    $pwsh = if ($env:DOTRETICULUM_PWSH) { $env:DOTRETICULUM_PWSH }
        elseif (Test-Path (Join-Path $PSHOME 'pwsh')) { Join-Path $PSHOME 'pwsh' }
        else { (Get-Process -Id $PID).Path }
    $info = [Diagnostics.ProcessStartInfo]::new($pwsh)
    $info.UseShellExecute = $false
    foreach ($arg in @('-NoProfile', '-Command', 'Start-Sleep -Seconds 90')) { $info.ArgumentList.Add($arg) }
    $child = [Diagnostics.Process]::Start($info)
    try {
        [IO.File]::WriteAllText("$Marker.child", [string]$child.Id)
        $child.WaitForExit()
    }
    finally { $child.Dispose() }
    exit 0
}
if ($Mode -in @('orphan', 'orphanstream', 'orphan-session', 'doublefork')) {
    $pwsh = (Get-Process -Id $PID).Path
    if ($Mode -eq 'orphan-session' -and -not $IsLinux) { throw 'Private-session fixture requires Linux setsid.' }
    $info = [Diagnostics.ProcessStartInfo]::new($(if ($Mode -eq 'orphan-session') { '/usr/bin/setsid' } else { $pwsh }))
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $Mode -ne 'orphanstream'
    $info.RedirectStandardError = $Mode -ne 'orphanstream'
    if ($Mode -eq 'orphan-session') { $info.ArgumentList.Add($pwsh) }
    $childMode = if ($Mode -eq 'doublefork') { 'orphan-session' } else { 'holdcapture' }
    $childMarker = if ($Mode -eq 'doublefork') { "$Marker.middle" } else { "$Marker.child" }
    $ready = if ($Mode -eq 'doublefork') { "$Marker.middle.child" } else { "$Marker.child" }
    foreach ($arg in @('-NoProfile', '-File', $PSCommandPath, '-Mode', $childMode,
        '-Output', $Output, '-Marker', $childMarker, '-Extra', 'public orphan child', $JobPaths[0])) { $info.ArgumentList.Add($arg) }
    $child = [Diagnostics.Process]::Start($info)
    try {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while (-not [IO.File]::Exists($ready)) {
            if ($child.HasExited -or $watch.Elapsed.TotalSeconds -gt 10) { throw 'Synthetic child did not acquire its files.' }
            Start-Sleep -Milliseconds 10
        }
        if ($Mode -eq 'doublefork') { [IO.File]::WriteAllText("$Marker.child", [IO.File]::ReadAllText($ready)) }
    }
    finally { $child.Dispose() }
    exit 0
}
if ($Mode -eq 'holdcapture') {
    $receipt = Join-Path $Output 'held-public-capture.json'
    $job = [IO.File]::Open($JobPaths[0], [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $capture = [IO.File]::Open($receipt, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $capture.Write([Text.Encoding]::UTF8.GetBytes('{"public":"owned descendant holds this capture"}'))
        $capture.Flush()
        [IO.File]::WriteAllText($Marker, [string]$PID)
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while (-not [IO.File]::Exists("$Marker.release") -and $watch.Elapsed.TotalSeconds -lt 90) {
            Start-Sleep -Milliseconds 20
        }
    }
    finally { $capture.Dispose(); $job.Dispose() }
    exit 0
}
if ($Mode -eq 'flood') {
    $buffer = 'x' * 4096
    for ($i = 0; $i -lt 16384; $i++) {
        [Console]::Out.Write($buffer)
        [Console]::Error.Write($buffer)
    }
    exit 0
}
$name = [Guid]::NewGuid().ToString('N')
$copy = Join-Path $Output ($name + [IO.Path]::GetExtension($JobPaths[0]))
[IO.File]::Copy($JobPaths[0], $copy)
@{ original = $JobPaths[0]; copy = $copy; extra = $Extra; pid = $PID } |
    ConvertTo-Json -Compress | Set-Content (Join-Path $Output "$name.json") -Encoding utf8NoBOM
exit 0
