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
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('# Ecosystem Test')
$lines.Add('')
$lines.Add("Producing commit: ``$Commit``. Managed jobs: $ManagedStatus. Compatibility jobs: $CompatibilityStatus.")
$lines.Add('Scheduled runs repeat these bounded gates; no pinned Python peers, GUI or physical hardware are provisioned.')
foreach ($rid in 'linux-x64', 'win-x64') {
    $target = Join-Path $summary $rid
    $null = New-Item -ItemType Directory -Path $target -Force
    $cases = [Collections.Generic.List[object]]::new()
    $failures = [Collections.Generic.List[string]]::new()
    $lines.Add('')
    $lines.Add("## $rid")
    $lines.Add('| Suite | Passed | Failed | Skipped |')
    $lines.Add('|---|---:|---:|---:|')
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
            $lines.Add("| $suite | Unavailable | Unavailable | Unavailable |")
            continue
        }
        $results = @(Read-TrxCases $path)
        if (!$results.Count) { $failures.Add("No discovered $suite cases.") }
        foreach ($case in $results) { $cases.Add($case) }
        $lines.Add("| $suite | $(@($results | Where-Object Status -eq 'Pass').Count) | $(@($results | Where-Object Status -eq 'Fail').Count) | $(@($results | Where-Object Status -eq 'Skipped').Count) |")
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
        $lines.Add("| Native/tooling | $(@($native.Cases | Where-Object Status -eq 'Pass').Count) | $(@($native.Cases | Where-Object Status -eq 'Fail').Count) | $(@($native.Cases | Where-Object Status -eq 'Skipped').Count) |")
    } else { $failures.Add('Missing native compatibility artifact.'); $lines.Add('| Native/tooling | Unavailable | Unavailable | Unavailable |') }
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
        RunStatus = if ($failures.Count -or $failed) { 'Failed or incomplete' } else { 'Passed selected checks' }
    }
    Write-CompatibilityReport -Report $report -Directory $target
    $lines.Add('')
    $lines.Add((Get-Content -Raw -LiteralPath (Join-Path $target 'compatibility.md')))
    $coveragePaths = @('Unit', 'Integration' | ForEach-Object {
        Join-Path $Directory "test-results-$_-$rid-$Attempt"
    })
    if ($failures.Count) { $failed = $true }
    if (@($coveragePaths | Where-Object { !(Test-Path -LiteralPath $_) }).Count) {
        $lines.Add('Coverage unavailable: both suite artifacts are required.')
        $failed = $true
    } else {
        & (Join-Path $PSScriptRoot 'coverage_summary.ps1') -Results $coveragePaths |
            Tee-Object -FilePath (Join-Path $target 'coverage.md') | ForEach-Object { $lines.Add($_) }
        if ($LASTEXITCODE -ne 0) { $failed = $true }
    }
}
$lines | Set-Content -LiteralPath (Join-Path $summary 'tests.md') -Encoding utf8
if ($env:GITHUB_STEP_SUMMARY) { $lines | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY }
$lines
if ($failed) { exit 1 }
