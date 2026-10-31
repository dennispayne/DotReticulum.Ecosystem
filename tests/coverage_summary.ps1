#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory, Position = 0)] [string[]] $Results)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Coverage.psm1') -Force
try {
    $reports = @(Get-ChildItem -LiteralPath $Results -Recurse -File -Filter 'coverage.opencover.xml')
    if (!$reports.Count) { throw "No coverage.opencover.xml reports found in $Results." }
    $expected = @(Get-ProductionModules (Split-Path $PSScriptRoot -Parent))
    $modules = Merge-ProductionCoverage -Reports $reports -ExpectedModules $expected
    $totals = @{ Lines = @(0L, 0L); Branches = @(0L, 0L) }
    function Format-Coverage([long] $Hit, [long] $Total) {
        if (!$Total) { return 'n/a (no instrumented points)' }
        [string]::Format([cultureinfo]::InvariantCulture, '{0}/{1} ({2:F2}%)', $Hit, $Total, (100.0 * $Hit / $Total))
    }
    '| Project | Lines | Branches |'
    '|---|---:|---:|'
    foreach ($name in $modules.Keys | Sort-Object) {
        $cells = foreach ($kind in @('Lines', 'Branches')) {
            $points = $modules[$name][$kind]
            $hit = @($points.Values | Where-Object { $_ }).Count
            $totals[$kind][0] += $hit
            $totals[$kind][1] += $points.Count
            Format-Coverage $hit $points.Count
        }
        "| $name | $($cells -join ' | ') |"
    }
    "| **Overall** | $(Format-Coverage $totals.Lines[0] $totals.Lines[1]) | $(Format-Coverage $totals.Branches[0] $totals.Branches[1]) |"
    "`nMerged $($reports.Count) reports by source line and branch identity, not averaged suite percentages. Excludes DotReticulum submodule and third-party code."
    exit 0
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 2
}
