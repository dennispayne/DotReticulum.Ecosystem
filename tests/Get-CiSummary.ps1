#Requires -Version 7.3
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Directory,
    [Parameter(Mandatory)] [ValidatePattern('^[a-f0-9]{40}$')] [string] $Commit,
    [Parameter(Mandatory)] [string] $Attempt,
    [string] $ManagedStatus = 'unknown',
    [string] $CompatibilityStatus = 'unknown'
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Compatibility\Reporting.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'ManagedBuild.psm1') -Force
$catalog = Read-CompatibilityCatalog (Join-Path $PSScriptRoot 'Compatibility\catalog.json')
$expectedReports = @(Get-EcosystemTestAssemblies (Split-Path $PSScriptRoot -Parent) |
    ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) + '.trx' } | Sort-Object)
$catalogText = (Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'Compatibility\catalog.json')).Replace("`r`n", "`n")
$catalogDigests = @($catalogText, $catalogText.Replace("`n", "`r`n") | ForEach-Object {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($_)))
})
$summary = Join-Path $Directory 'summary'
$null = New-Item -ItemType Directory -Path $summary -Force
$failed = $ManagedStatus -ne 'success' -or $CompatibilityStatus -ne 'success'
$jobsFailed = $failed
$platforms = @{}
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('# Ecosystem Test')
$lines.Add('')
$lines.Add("Producing commit: ``$Commit``. Managed jobs: $ManagedStatus. Compatibility jobs: $CompatibilityStatus.")
$lines.Add('Scheduled runs repeat these bounded gates; no pinned Python peers, GUI or physical hardware are provisioned.')
$lines.Add('macOS is build/help-only; no macOS test or coverage jobs are selected. Skipped includes unselected cases.')
foreach ($rid in 'linux-x64', 'win-x64') {
    $target = Join-Path $summary $rid
    $null = New-Item -ItemType Directory -Path $target -Force
    $cases = [Collections.Generic.List[object]]::new()
    $failures = [Collections.Generic.List[string]]::new()
    $suites = @{}
    foreach ($suite in 'Unit', 'Integration') {
        $path = Join-Path $Directory "test-results-$suite-$rid-$Attempt\$suite"
        $actualReports = @(
            if (Test-Path -LiteralPath $path) {
                Get-ChildItem -LiteralPath $path -Filter '*.trx' -File | Select-Object -ExpandProperty Name | Sort-Object
            }
        )
        if (!(Test-Path -LiteralPath $path) -or
            ($actualReports -join "`n") -cne ($expectedReports -join "`n")) {
            $failures.Add("Incomplete $suite artifact.")
            $suites[$suite] = 'Unavailable'
            continue
        }
        $results = @(Read-TrxCases $path)
        if (!$results.Count) { $failures.Add("No discovered $suite cases.") }
        foreach ($case in $results) { $cases.Add($case) }
        $suites[$suite] = "$(@($results | Where-Object Status -eq 'Pass').Count) / $(@($results | Where-Object Status -eq 'Fail').Count) / $(@($results | Where-Object Status -eq 'Skipped').Count)"
    }
    $compatibility = Join-Path $Directory "compatibility-$rid-$Attempt\compatibility\compatibility.json"
    $native = $null
    if (Test-Path -LiteralPath $compatibility) {
        $native = Get-Content -Raw -LiteralPath $compatibility | ConvertFrom-Json
        if ($native.Commit -cne $Commit -or $native.CatalogSha256 -cnotin $catalogDigests) {
            throw 'Compatibility evidence commit/catalog mismatch.'
        }
        foreach ($case in $native.Cases) { $cases.Add($case) }
        foreach ($failure in $native.Failures) { $failures.Add($failure) }
        if ($native.RunStatus -ne 'Passed selected checks') { $failures.Add('Native compatibility job did not pass.') }
        $suites['Native/tooling'] = "$(@($native.Cases | Where-Object Status -eq 'Pass').Count) / $(@($native.Cases | Where-Object Status -eq 'Fail').Count) / $(@($native.Cases | Where-Object Status -eq 'Skipped').Count)"
    } else { $failures.Add('Missing native compatibility artifact.'); $suites['Native/tooling'] = 'Unavailable' }
    $report = New-CompatibilityReport -Catalog $catalog -Cases $cases.ToArray() -ExecutedRunners @('xunit', 'pester')
    foreach ($test in $report.Tests | Where-Object Status -eq 'Not covered') {
        $failures.Add("Unmatched selector: $($test.TestId)")
    }
    if (@($cases | Where-Object Status -eq 'Fail').Count) { $failures.Add('Executed cases failed.') }
    $report | Add-Member -NotePropertyMembers @{
        Timestamp = [DateTimeOffset]::UtcNow.ToString('O'); Platform = $rid; Commit = $Commit
        WorkingTreeDirty = if ($native) { $native.WorkingTreeDirty } else { $true }
        CatalogSha256 = if ($native) { $native.CatalogSha256 } else { $null }
        Modes = if ($native) { $native.Modes } else { $null }
        References = $catalog.references; Failures = $failures.ToArray()
        NativeBinaries = if ($native) { $native.NativeBinaries } else { @() }
        VerifiedReference = if ($native) { $native.VerifiedReference } else { $null }
        Commands = @('Build-once Unit/Integration VSTest and native Pester; see producing artifacts')
        RunStatus = if ($failures.Count -or $jobsFailed) { 'Failed or incomplete' } else { 'Passed selected checks' }
    }
    Write-CompatibilityReport -Report $report -Directory $target
    $coveragePaths = @('Unit', 'Integration' | ForEach-Object {
        Join-Path $Directory "test-results-$_-$rid-$Attempt"
    })
    $coverage = @{}
    if (@($coveragePaths | Where-Object { !(Test-Path -LiteralPath $_) }).Count) {
        $failures.Add('Coverage unavailable: both suite artifacts are required.')
    } else {
        $coverageLines = @(& (Join-Path $PSScriptRoot 'coverage_summary.ps1') -Results $coveragePaths -JsonPath (Join-Path $target 'coverage.json'))
        $coverageLines | Set-Content -LiteralPath (Join-Path $target 'coverage.md') -Encoding utf8
        if ($LASTEXITCODE -ne 0) { $failures.Add('Production coverage reporting failed.') }
        else {
            foreach ($line in $coverageLines | Where-Object { $_ -like '| *' -and $_ -notlike '| Project *' }) {
                $cells = @($line.Split('|') | ForEach-Object { $_.Trim() })
                $coverage[$cells[1]] = @{ Lines = $cells[2]; Branches = $cells[3] }
            }
        }
    }
    if ($failures.Count) {
        $failed = $true
        $report.RunStatus = 'Failed or incomplete'
        $report.Failures = $failures.ToArray()
        Write-CompatibilityReport -Report $report -Directory $target
    }
    $platforms[$rid] = @{ Report = $report; Suites = $suites; Coverage = $coverage }
}
$windows = $platforms['win-x64']
$linux = $platforms['linux-x64']
$lines.Add('')
$lines.Add('## Execution')
$lines.Add('Suite counts are **passed / failed / skipped**. Selected checks passing is not full upstream parity.')
$lines.Add('')
$lines.Add('| Suite | Windows x64 | Linux x64 | macOS ARM64 |')
$lines.Add('|---|---|---|---|')
foreach ($suite in 'Unit', 'Integration', 'Native/tooling') {
    $lines.Add("| $suite | $($windows.Suites[$suite]) | $($linux.Suites[$suite]) | Not run |")
}
$lines.Add("| Verdict | $($windows.Report.RunStatus) | $($linux.Report.RunStatus) | Not run |")
$lines.Add("| Dirty worktree | $($windows.Report.WorkingTreeDirty) | $($linux.Report.WorkingTreeDirty) | Not evaluated |")
$lines.Add('')
$lines.Add('## Compatibility claims')
$lines.Add('| ClaimId | Family | Claim | Windows x64 | Linux x64 | macOS ARM64 |')
$lines.Add('|---|---|---|---|---|---|')
foreach ($claim in $windows.Report.Claims) {
    $other = $linux.Report.Claims | Where-Object ClaimId -eq $claim.ClaimId
    $title = $claim.Title -replace '\|', '\|' -replace '\r?\n', ' '
    $lines.Add("| $($claim.ClaimId) | $($claim.Family) | $title | $($claim.Status) | $($other.Status) | Not run |")
}
$lines.Add('')
$lines.Add('<details><summary>Test mappings</summary>')
$lines.Add('')
$lines.Add('| TestId | ClaimId | Evidence | Windows x64 | Linux x64 | macOS ARM64 |')
$lines.Add('|---|---|---|---|---|---|')
foreach ($test in $windows.Report.Tests) {
    $other = $linux.Report.Tests | Where-Object TestId -eq $test.TestId
    $lines.Add("| $($test.TestId) | $($test.ClaimId) | $($test.Evidence) | $($test.Status) ($($test.Cases.Count)) | $($other.Status) ($($other.Cases.Count)) | Not run |")
}
$lines.Add('')
$lines.Add('</details>')
$lines.Add('')
$lines.Add('## Production coverage')
$lines.Add('Per-platform union across suites, not averaged percentages or a union across operating systems.')
$lines.Add('')
$lines.Add('| Project | Windows lines | Linux lines | Windows branches | Linux branches | macOS |')
$lines.Add('|---|---|---|---|---|---|')
foreach ($name in @(@($windows.Coverage.Keys) + @($linux.Coverage.Keys) | Sort-Object -Unique)) {
    $win = $windows.Coverage[$name]
    $lin = $linux.Coverage[$name]
    $lines.Add("| $name | $(if ($win) { $win.Lines } else { 'Unavailable' }) | $(if ($lin) { $lin.Lines } else { 'Unavailable' }) | $(if ($win) { $win.Branches } else { 'Unavailable' }) | $(if ($lin) { $lin.Branches } else { 'Unavailable' }) | Not collected |")
}
if (!$windows.Coverage.Count -or !$linux.Coverage.Count) { $lines.Add('Coverage unavailable for one or more tested platforms; inspect the failure diagnostics.') }
$lines.Add('')
$lines.Add('Detailed selectors, boundaries, reference pins, per-case results and binary provenance remain in the per-platform summary artifacts.')
foreach ($rid in 'win-x64', 'linux-x64') {
    foreach ($failure in $platforms[$rid].Report.Failures) { $lines.Add("- ${rid}: $failure") }
}
$lines | Set-Content -LiteralPath (Join-Path $summary 'tests.md') -Encoding utf8
if ($env:GITHUB_STEP_SUMMARY) { $lines | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY }
$lines
if ($failed) { exit 1 }
