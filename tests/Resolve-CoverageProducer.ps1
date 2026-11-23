#Requires -Version 7.3
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^[\w.-]+/[\w.-]+$')] [string] $Repository,
    [Parameter(Mandatory)] [ValidatePattern('^[a-f0-9]{40}$')] [string] $Commit,
    [Parameter(Mandatory)] [ValidateSet('workflow_run', 'workflow_dispatch')] [string] $TriggerEvent
)
$ErrorActionPreference = 'Stop'
$json = & gh api "repos/$Repository/actions/workflows/build.yml/runs?head_sha=$Commit&status=success&per_page=100"
if ($LASTEXITCODE -ne 0) { throw 'Cannot retrieve coverage producer Builds.' }
$response = $json | ConvertFrom-Json
if ($response.total_count -gt @($response.workflow_runs).Count) {
    throw 'Coverage producer Build results are incomplete.'
}
$runs = @($response.workflow_runs | Where-Object {
    $_.head_sha -ceq $Commit -and $_.repository.full_name -ceq $Repository -and
    $_.path -ceq '.github/workflows/build.yml' -and $_.status -eq 'completed' -and $_.conclusion -eq 'success'
})
$main = @($runs | Where-Object {
    $_.head_repository.full_name -ceq $Repository -and $_.head_branch -eq 'main' -and
    $_.event -in 'push', 'workflow_dispatch'
})
$pullRequests = @($runs | Where-Object { $_.event -eq 'pull_request' })
if ($main.Count -and $pullRequests.Count) {
    throw 'Test producer is ambiguous between main and pull request Builds.'
}
if ($main.Count) { return 'main' }
if ($TriggerEvent -eq 'workflow_run' -and $pullRequests.Count) { return 'pull_request' }
throw 'Test does not identify a successful trusted main Build.'
