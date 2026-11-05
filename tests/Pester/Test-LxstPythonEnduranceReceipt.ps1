#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)] [string] $Receipt)
$ErrorActionPreference = 'Stop'
$file = Get-Item -LiteralPath $Receipt
$directory = $file.DirectoryName
$actual = (Get-FileHash $file.FullName).Hash.ToLowerInvariant()
$expected = (Get-Content (Join-Path $directory 'receipt.sha256') -Raw).Trim()
if (!$file.IsReadOnly -or $actual -cne $expected) { throw 'Receipt is not read-only or its digest changed.' }
$record = Get-Content $file.FullName -Raw | ConvertFrom-Json
if (!$record.passed -or !$record.hashesUnchanged -or $record.steadyElapsedSeconds -lt 600 -or
    $record.scope -match 'probe-only' -or $record.requestedSteadySeconds -lt 600) {
    throw 'Not a successful 600-second prolonged interoperability receipt.'
}
$before = Get-Content (Join-Path $directory 'freeze-before.json') -Raw | ConvertFrom-Json
$after = Get-Content (Join-Path $directory 'freeze-after.json') -Raw | ConvertFrom-Json
if (($before | ConvertTo-Json -Compress) -cne ($after | ConvertTo-Json -Compress)) { throw 'Before/after input hashes differ.' }
foreach ($inputFile in $before) {
    if ((Get-FileHash -LiteralPath $inputFile.path).Hash.ToLowerInvariant() -cne $inputFile.sha256) {
        throw "Frozen input changed: $($inputFile.path)"
    }
}
$steady = @($record.observations | Where-Object { $_.elapsedSeconds -ge 0 })
if ($steady.Count -lt 41 -or $steady[0].elapsedSeconds -ne 0 -or $steady[-1].elapsedSeconds -lt 600) {
    throw 'Insufficient sustained observations.'
}
$minima = [ordered]@{}
foreach ($field in @('nativeReceived','nativeTransmitted','pythonReceived','pythonTransmitted')) {
    $minima[$field] = [long]::MaxValue
}
$maxGap = 0.0
for ($index=1; $index -lt $steady.Count; $index++) {
    $previous = $steady[$index-1]
    $current = $steady[$index]
    $gap = $current.elapsedSeconds - $previous.elapsedSeconds
    if ($gap -le 0 -or $gap -gt 20) { throw "Nonpositive or excessive observation gap: $gap seconds." }
    $maxGap = [Math]::Max($maxGap,$gap)
    if ($current.nativePid -ne $record.nativePid -or $current.pythonPid -ne $steady[0].pythonPid) { throw 'A steady peer was replaced.' }
    foreach ($field in @($minima.Keys)) {
        $delta = $current.$field - $previous.$field
        if ($delta -le 0) { throw "Frame progression failed: $field" }
        $minima[$field] = [Math]::Min($minima[$field],$delta)
    }
    if ($current.python.latest.receive.codec -ne 'Opus' -or $current.python.latest.transmit.codec -ne 'Opus' -or
        !$current.python.latest.receive.finite -or $current.python.latest.receive.samples -le 0 -or
        $current.python.latest.receive.sha256 -notmatch '^[a-f0-9]{64}$' -or $current.python.latest.transmit.sha256 -notmatch '^[a-f0-9]{64}$') {
        throw 'Missing actual encoded/decoded Opus evidence.'
    }
}
foreach ($phase in @('codec2','reverseCall','recoveredCall')) {
    if (!$record.$phase -or $record.$phase.nativePid -ne $record.nativePid -or
        !$record.$phase.python.latest.receive.finite) { throw "Missing $phase on the original native process." }
}
if ($record.codec2.python.latest.receive.codec -ne 'Codec2' -or
    $record.codec2.python.latest.transmit.codec -ne 'Codec2' -or $record.failureRecoverySeconds -gt 30) {
    throw 'Codec2 or bounded peer-failure recovery evidence is missing.'
}
[ordered]@{
    contract='C111'; pesterCases=1; receipt=$file.FullName; receiptSha256=$actual
    steadySeconds=$record.steadyElapsedSeconds; intervals=$steady.Count-1
    maximumObservationGapSeconds=$maxGap; minimumFrameIncreases=$minima
    nativePid=$record.nativePid; nativeIdentity=$record.nativeIdentity
    nativeSha256=$record.historicalNative.sha256; sourceFilesChecked=$before.Count
    sourceAndBinaryUnchanged=$true; accepted=$true
} | ConvertTo-Json -Depth 4
