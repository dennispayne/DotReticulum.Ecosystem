#Requires -Version 7.3
[CmdletBinding()]
param(
    [string] $Report,
    [Parameter(Mandatory)] [string] $Output,
    [Parameter(Mandatory)] [string] $Label,
    [switch] $NotCollected
)
$ErrorActionPreference = 'Stop'
if ($NotCollected) {
    if ($Report) { throw 'Uncollected coverage cannot have a report.' }
    $message = 'not collected'
    $color = 'lightgrey'
} else {
    if (!$Report) { throw 'Measured coverage requires a report.' }
    $coverage = Get-Content -Raw -LiteralPath $Report | ConvertFrom-Json
    if ($coverage.schemaVersion -ne 1 -or ($coverage.lines.total -isnot [long] -and $coverage.lines.total -isnot [int]) -or
        ($coverage.lines.covered -isnot [long] -and $coverage.lines.covered -isnot [int]) -or
        $coverage.lines.total -le 0 -or $coverage.lines.covered -lt 0 -or $coverage.lines.covered -gt $coverage.lines.total) {
        throw 'Invalid measured coverage counts.'
    }
    $percentage = 100.0 * $coverage.lines.covered / $coverage.lines.total
    $message = [string]::Format([cultureinfo]::InvariantCulture, '{0:F2}%', $percentage)
    $color = if ($percentage -ge 80) { 'brightgreen' } elseif ($percentage -ge 60) { 'yellow' } else { 'orange' }
}
@{ schemaVersion = 1; label = $Label; message = $message; color = $color; cacheSeconds = 300 } |
    ConvertTo-Json | Set-Content -LiteralPath $Output -Encoding utf8
