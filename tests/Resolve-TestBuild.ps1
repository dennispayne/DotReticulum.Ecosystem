#Requires -Version 7.3
[CmdletBinding()]
param(
    [ValidatePattern('^\d*$')] [string] $RunId,
    [string] $Repository = $env:GITHUB_REPOSITORY,
    [string] $EventPath = $env:GITHUB_EVENT_PATH,
    [string] $NativeManifest
)
$ErrorActionPreference = 'Stop'
if ($Repository -notmatch '^[\w.-]+/[\w.-]+$') { throw 'Expected an explicit owner/repository.' }
function Get-BuildApi([string] $Endpoint) {
    $json = & gh api $Endpoint
    if ($LASTEXITCODE -ne 0) { throw "Build API request failed: $Endpoint" }
    $json | ConvertFrom-Json
}
$eventData = if ($EventPath) { Get-Content -Raw -LiteralPath $EventPath | ConvertFrom-Json -AsHashtable } else { @{} }
$trigger = $eventData['workflow_run']
if ($trigger) {
    if ($RunId -and $RunId -ne [string]$trigger.id) { throw 'Selection differs from the triggering Build.' }
    $RunId = [string]$trigger.id
}
if (!$RunId) {
    $runs = Get-BuildApi "repos/$Repository/actions/workflows/build.yml/runs?branch=main&status=success&per_page=20"
    $candidate = $runs.workflow_runs | Where-Object { $_.event -in 'push', 'workflow_dispatch' } | Select-Object -First 1
    if (!$candidate) { throw 'No successful main Build is available.' }
    $RunId = [string]$candidate.id
}
$run = Get-BuildApi "repos/$Repository/actions/runs/$RunId"
if ($run.repository.full_name -cne $Repository -or
    ($run.head_repository.full_name -cne $Repository -and $run.event -ne 'pull_request') -or
    $run.path -cne '.github/workflows/build.yml' -or $run.status -ne 'completed' -or
    $run.conclusion -ne 'success' -or $run.head_sha -cnotmatch '^[a-f0-9]{40}$' -or
    $run.event -notin 'push', 'pull_request', 'workflow_dispatch') {
    throw 'Producer must be a successful Build in this repository.'
}
$trusted = $run.event -in 'push', 'workflow_dispatch' -and $run.head_branch -eq 'main'
if (!$trusted -and !$trigger) { throw 'Manual/scheduled Test requires a successful main Build.' }
$artifacts = Get-BuildApi "repos/$Repository/actions/runs/$RunId/artifacts?per_page=100"
foreach ($rid in 'linux-x64', 'win-x64', 'osx-arm64') {
    foreach ($kind in 'native', 'managed') {
        $items = @($artifacts.artifacts | Where-Object { $_.name -ceq "$kind-$rid" })
        if ($items.Count -ne 1 -or $items[0].expired) { throw "Missing, duplicate or expired $kind-$rid artifact." }
    }
}
$commit = $run.head_sha
if ($NativeManifest) {
    $manifest = Get-Content -Raw -LiteralPath $NativeManifest | ConvertFrom-Json
    if ($manifest.rid -cne 'linux-x64' -or $manifest.commit -cnotmatch '^[a-f0-9]{40}$') {
        throw 'Invalid Linux producing manifest.'
    }
    if ($manifest.commit -cne $run.head_sha) {
        if ($trusted -or $run.event -ne 'pull_request') { throw 'Producing manifest commit mismatch.' }
        $merge = Get-BuildApi "repos/$Repository/git/commits/$($manifest.commit)"
        if (@($merge.parents).Count -ne 2 -or $merge.parents[1].sha -cne $run.head_sha) {
            throw 'Producing commit is not a merge of the PR head.'
        }
    }
    $commit = $manifest.commit
}
Write-Host "Selected Build $RunId attempt $($run.run_attempt), checkout $commit."
if ($env:GITHUB_OUTPUT) {
    @("run-id=$RunId", "commit=$commit", "attested=$($trusted.ToString().ToLowerInvariant())") |
        Add-Content -LiteralPath $env:GITHUB_OUTPUT
}
