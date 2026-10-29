Set-StrictMode -Version Latest

function Read-CompatibilityCatalog {
    param([string] $Path)
    $catalog = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
    if ($catalog.schemaVersion -ne 1) { throw 'Unsupported compatibility catalog schema.' }
    foreach ($group in @(
        @{ Items = @($catalog.claims); Pattern = '^C[1-9]\d*$' },
        @{ Items = @($catalog.tests); Pattern = '^T[1-9]\d*$' }
    )) {
        $ids = @{}
        foreach ($item in $group.Items) {
            if ($item.id -cnotmatch $group.Pattern -or $ids.ContainsKey($item.id)) {
                throw "Invalid or duplicate compatibility ID: $($item.id)"
            }
            $ids[$item.id] = $true
        }
    }
    foreach ($test in $catalog.tests) {
        if ($test.claimId -notin $catalog.claims.id) { throw "Unknown claim $($test.claimId) in $($test.id)." }
        if ($test.runner -notin @('xunit', 'pester') -or !$test.suite -or !$test.pattern -or !$test.evidence) {
            throw "Incomplete selector for $($test.id)."
        }
        if ($test.PSObject.Properties['reference'] -and $test.reference -notin $catalog.references.id) {
            throw "Unknown reference in $($test.id)."
        }
    }
    $referenceIds = @{}
    foreach ($reference in $catalog.references) {
        if ($referenceIds.ContainsKey($reference.id)) { throw "Duplicate reference $($reference.id)." }
        $referenceIds[$reference.id] = $true
        if ($reference.revision -notmatch '^[a-f0-9]{40}$' -or $reference.source -notlike 'rns://*') {
            throw "Reference $($reference.id) requires a full revision and primary rns:// locator."
        }
    }
    foreach ($claim in $catalog.claims) {
        if (!$claim.family -or !$claim.title -or !$claim.source -or !$claim.boundary) {
            throw "Incomplete claim $($claim.id)."
        }
    }
    foreach ($test in $catalog.tests) {
        $null = [WildcardPattern]::new($test.pattern).IsMatch('')
    }
    return $catalog
}

function Get-CompatibilityStatus {
    param([object[]] $Results)
    if ($Results -contains 'Fail') { return 'Fail' }
    if (!$Results.Count) { return 'Not covered' }
    if (@($Results | Where-Object { $_ -ne 'Pass' }).Count -eq 0) { return 'Pass' }
    if ($Results -contains 'Pass') { return 'Partial' }
    if (@($Results | Where-Object { $_ -ne 'Skipped' }).Count -eq 0) { return 'Skipped' }
    if (@($Results | Where-Object { $_ -ne 'Not run' }).Count -eq 0) { return 'Not run' }
    return 'Not covered'
}

function Read-TrxCases {
    param([string] $Directory)
    foreach ($file in Get-ChildItem -LiteralPath $Directory -Filter '*.trx' -Recurse -File) {
        $xml = [xml]::new()
        $xml.Load($file.FullName)
        $definitions = @{}
        foreach ($definition in $xml.SelectNodes("//*[local-name()='UnitTest']")) {
            $method = $definition.SelectSingleNode("*[local-name()='TestMethod']")
            $definitions[$definition.GetAttribute('id')] = @{
                Name = $method.GetAttribute('className') + '.' + $method.GetAttribute('name')
                Suite = [IO.Path]::GetFileNameWithoutExtension($method.GetAttribute('codeBase'))
            }
        }
        foreach ($test in $xml.SelectNodes("//*[local-name()='UnitTestResult']")) {
            $definition = $definitions[$test.GetAttribute('testId')]
            if (!$definition) { throw "Missing TRX definition in $($file.FullName)." }
            $status = switch ($test.GetAttribute('outcome')) {
                'Passed' { 'Pass' }
                'NotExecuted' { 'Skipped' }
                default { 'Fail' }
            }
            [pscustomobject]@{
                Runner = 'xunit'; Suite = $definition.Suite; Name = $definition.Name
                Case = $test.GetAttribute('testName'); Status = $status
                Detail = $test.InnerText.Trim(); Artifact = $file.FullName
            }
        }
    }
}

function New-CompatibilityReport {
    param($Catalog, [object[]] $Cases, [string[]] $ExecutedRunners)
    $tests = @(
        foreach ($test in $Catalog.tests) {
            $matched = @($Cases | Where-Object {
                $_.Runner -eq $test.runner -and $_.Suite -eq $test.suite -and $_.Name -like $test.pattern
            })
            $status = if ($test.runner -notin $ExecutedRunners) { 'Not run' }
            else { Get-CompatibilityStatus -Results @($matched | ForEach-Object Status) }
            [pscustomobject]@{
                TestId = $test.id; ClaimId = $test.claimId; Evidence = $test.evidence
                Runner = $test.runner; Suite = $test.suite; Pattern = $test.pattern
                Status = $status; Cases = $matched
            }
        }
    )
    $claims = @(
        foreach ($claim in $Catalog.claims) {
            $mine = @($tests | Where-Object ClaimId -eq $claim.id)
            [pscustomobject]@{
                ClaimId = $claim.id; Family = $claim.family; Title = $claim.title
                Source = $claim.source; Boundary = $claim.boundary
                Implementation = if ($claim.PSObject.Properties['implementation']) { $claim.implementation } else { 'Scoped; see project inventory' }
                Status = Get-CompatibilityStatus -Results @($mine | ForEach-Object Status)
                TestIds = @($mine | ForEach-Object TestId)
            }
        }
    )
    $unmapped = @($Cases | Where-Object {
        $case = $_
        !@($Catalog.tests | Where-Object {
            $_.runner -eq $case.Runner -and $_.suite -eq $case.Suite -and $case.Name -like $_.pattern
        }).Count
    })
    [pscustomobject]@{ Claims = $claims; Tests = $tests; UnmappedCases = $unmapped; Cases = $Cases }
}

function Write-CompatibilityReport {
    param($Report, [string] $Directory)
    $Report | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $Directory 'compatibility.json') -Encoding utf8
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('# Ecosystem compatibility evidence')
    $lines.Add('')
    $lines.Add("Run: $($Report.Timestamp). Platform: $($Report.Platform). Commit: ``$($Report.Commit)``. Dirty worktree: $($Report.WorkingTreeDirty).")
    $lines.Add("Execution result: **$($Report.RunStatus)** (not a full-parity verdict).")
    $availability = @($Report.Cases | Where-Object { $_.PSObject.Properties['AvailabilityOnly'] -and $_.AvailabilityOnly }).Count
    $lines.Add("Discovered cases: $($Report.Cases.Count - $availability). Additional unavailable-suite records: $availability. Unmapped records: $($Report.UnmappedCases.Count). No percentage implies full parity.")
    $lines.Add('')
    $lines.Add('| ClaimId | Family | Claim | Result | TestId | Implementation | Boundary |')
    $lines.Add('|---|---|---|---|---|---|---|')
    foreach ($claim in $Report.Claims) {
        $cells = @($claim.ClaimId, $claim.Family, $claim.Title, $claim.Status, ($claim.TestIds -join ', '), $claim.Implementation, $claim.Boundary)
        $lines.Add('| ' + (($cells | ForEach-Object { $_ -replace '\|', '\|' -replace '\r?\n', ' ' }) -join ' | ') + ' |')
    }
    $lines.Add('')
    $lines.Add('## References')
    foreach ($reference in $Report.References) {
        $lines.Add("- $($reference.id) $($reference.version): ``$($reference.revision)`` from ``$($reference.source)``.")
    }
    $lines.Add('')
    $lines.Add('Expected pins are not proof that a reference executed. See provenance artifacts and each case outcome; V/U evidence is not L evidence.')
    $lines.Add('')
    $lines.Add('## Test mappings')
    $lines.Add('| TestId | ClaimId | Evidence | Result | Cases/availability records | Selector |')
    $lines.Add('|---|---|---|---|---:|---|')
    foreach ($test in $Report.Tests) {
        $lines.Add("| $($test.TestId) | $($test.ClaimId) | $($test.Evidence) | $($test.Status) | $($test.Cases.Count) | ``$($test.Suite):$($test.Pattern -replace '\|', '\|')`` |")
    }
    $lines.Add('')
    $lines.Add('## Commands')
    foreach ($command in $Report.Commands) { $lines.Add("- ``$($command -replace '`', '')``") }
    foreach ($failure in $Report.Failures) { $lines.Add("- Failure: $failure") }
    $lines -join "`n" | Set-Content -LiteralPath (Join-Path $Directory 'compatibility.md') -Encoding utf8
}

Export-ModuleMember -Function Read-CompatibilityCatalog, Get-CompatibilityStatus, Read-TrxCases, New-CompatibilityReport, Write-CompatibilityReport
