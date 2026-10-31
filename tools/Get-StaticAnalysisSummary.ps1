#Requires -Version 7.3
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ReportsDirectory,
    [string] $CodeQLStatus = 'unknown',
    [string] $PowerShellStatus = 'unknown'
)
$ErrorActionPreference = 'Stop'
$expected = [ordered]@{
    '/language:actions' = @{ Tool = 'CodeQL'; Status = $CodeQLStatus }
    '/language:csharp' = @{ Tool = 'CodeQL'; Status = $CodeQLStatus }
    '/language:python' = @{ Tool = 'CodeQL'; Status = $CodeQLStatus }
    '/language:powershell' = @{ Tool = 'PSScriptAnalyzer'; Status = $PowerShellStatus }
}
$reports = @{}
foreach ($file in Get-ChildItem -LiteralPath $ReportsDirectory -Recurse -File -Filter '*.sarif') {
    $document = Get-Content -Raw -LiteralPath $file.FullName | ConvertFrom-Json -AsHashtable
    if ($document.version -ne '2.1.0' -or !@($document.runs).Count) { throw "Invalid SARIF: $($file.Name)" }
    foreach ($run in $document.runs) {
        $category = ([string]$run.automationDetails.id).TrimEnd('/')
        if (!$expected.Contains($category) -or $reports.ContainsKey($category) -or
            $run.tool.driver.name -ne $expected[$category].Tool -or $run.results -isnot [array]) {
            throw "Unexpected, duplicate or incomplete analysis: $category"
        }
        $counts = @{ error = 0; warning = 0; note = 0; none = 0 }
        $suppressed = 0
        foreach ($result in $run.results) {
            if (@($result['suppressions'] | Where-Object { $_ -and $_['status'] -eq 'accepted' }).Count) { $suppressed++; continue }
            $rule = @($run.tool.driver['rules'] | Where-Object { $_ -and $_['id'] -eq $result.ruleId }) | Select-Object -First 1
            $configuration = if ($rule -and $rule['defaultConfiguration']) { $rule['defaultConfiguration'] } else { @{} }
            $level = if ($result['level']) { $result['level'] } elseif ($configuration['level']) {
                $configuration['level']
            } else { 'warning' }
            if (!$counts.ContainsKey($level)) { throw "Unknown SARIF level: $level" }
            $counts[$level]++
        }
        $reports[$category] = @{
            Counts = $counts; Suppressed = $suppressed
            Failed = @($run['invocations'] | Where-Object { $_ -and $_['executionSuccessful'] -eq $false }).Count -gt 0
        }
    }
}
'## Static analysis'
''
'Analysis completion is separate from findings; missing reports never mean zero findings.'
''
'| Target | Job status | Errors | Warnings | Notes | Suppressed |'
'|---|---|---:|---:|---:|---:|'
foreach ($category in $expected.Keys) {
    $entry = $reports[$category]
    if (!$entry -or $entry.Failed) {
        "| $category | $($expected[$category].Status); incomplete | Unavailable | Unavailable | Unavailable | Unavailable |"
    } else {
        "| $category | $($expected[$category].Status) | $($entry.Counts.error) | $($entry.Counts.warning) | $($entry.Counts.note + $entry.Counts.none) | $($entry.Suppressed) |"
    }
}
