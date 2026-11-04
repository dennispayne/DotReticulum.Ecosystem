[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ArtifactsPath,
    [Parameter(Mandatory)][string] $OutputDirectory,
    [switch] $NativeOnly
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$ArtifactsPath = [IO.Path]::GetFullPath($ArtifactsPath)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ($env:DOTRETICULUM_LIVE -ne '1' -or $env:DOTSIDEBAND_LIVE -ne '1') {
    throw 'Both live opt-ins must be enabled; disabled acceptance is not a pass.'
}
foreach ($name in 'DOTRETICULUM_PYTHON', 'DOTRETICULUM_UPSTREAM', 'DOTRETICULUM_LXST_UPSTREAM', 'DOTRETICULUM_SIDEBAND_UPSTREAM') {
    $value = [Environment]::GetEnvironmentVariable($name)
    if (-not $value -or -not (Test-Path -LiteralPath $value)) { throw "Missing pinned reference prerequisite $name" }
}
if (-not $IsWindows -and $ArtifactsPath.StartsWith('/mnt/', [StringComparison]::Ordinal)) {
    throw 'Linux test outputs, identities and profiles must use the native Linux filesystem.'
}
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$owned = @(
    'tests/Interop/sideband_retry_pn.py',
    'tests/Interop/sideband_retry_primary.py',
    'tests/Interop/sideband_retry_diagnostic.py',
    'tests/Interop/Invoke-SidebandPropagationRetryAcceptance.ps1',
    'tests/Sideband.Tests/GenerateSidebandPropagationRetryVectors.py',
    'tests/Sideband.Tests/SidebandPropagationRetryReferenceTests.cs',
    'tests/Sideband.Tests/SidebandRemoteCommandRetryLiveTests.cs',
    'tests/Sideband.Tests/Fixtures/sideband-propagation-retry.json',
    'src/Sideband/SidebandClient.Remote.cs',
    'src/Sideband/SidebandClient.Telemetry.cs'
)
function SourceHashes {
    $hashes = [ordered]@{}
    foreach ($path in $owned) {
        $hashes[$path] = (Get-FileHash (Join-Path $repository $path) -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    return $hashes
}
$before = SourceHashes
$originalPythonPath = $env:PYTHONPATH
$originalBridges = $env:DOTRETICULUM_REMOTE_RETRY_BRIDGES
$originalEvidence = $env:DOTRETICULUM_REMOTE_RETRY_EVIDENCE
Push-Location $repository
try {
    $roots = @($env:DOTRETICULUM_UPSTREAM)
    if ($env:DOTRETICULUM_LXMF_UPSTREAM) { $roots += $env:DOTRETICULUM_LXMF_UPSTREAM }
    $roots += $env:DOTRETICULUM_LXST_UPSTREAM
    $roots += $PSScriptRoot
    $env:PYTHONPATH = $roots -join [IO.Path]::PathSeparator
    $env:DOTRETICULUM_REMOTE_RETRY_BRIDGES = $PSScriptRoot
    $env:DOTRETICULUM_REMOTE_RETRY_EVIDENCE = Join-Path $OutputDirectory 'scenarios'
    [IO.Directory]::CreateDirectory($env:DOTRETICULUM_REMOTE_RETRY_EVIDENCE) | Out-Null
    Get-ChildItem -LiteralPath $env:DOTRETICULUM_REMOTE_RETRY_EVIDENCE -Filter '*.json' -File | Remove-Item -Force
    & $env:DOTRETICULUM_PYTHON (Join-Path $PSScriptRoot 'reference_environment.py') --require-lxmf --require-lxst
    if ($LASTEXITCODE -ne 0) { throw 'Actual imported reference verification failed.' }
    $generated = Join-Path $OutputDirectory 'retry-reference.json'
    & $env:DOTRETICULUM_PYTHON (Join-Path $repository 'tests/Sideband.Tests/GenerateSidebandPropagationRetryVectors.py') `
        --source $env:DOTRETICULUM_SIDEBAND_UPSTREAM --output $generated |
        Set-Content (Join-Path $OutputDirectory 'reference-generation.log') -Encoding utf8NoBOM
    if ($LASTEXITCODE -ne 0) { throw 'Actual primary notification execution failed.' }
    $fixtureHash = (Get-FileHash $generated -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($fixtureHash -ne $before['tests/Sideband.Tests/Fixtures/sideband-propagation-retry.json']) {
        throw 'Fresh actual-primary observations do not byte-match the fixture.'
    }
    $trx = Join-Path $OutputDirectory 'retry.trx'
    if (Test-Path -LiteralPath $trx) { Remove-Item -LiteralPath $trx -Force }
    $filter = if ($NativeOnly) { 'FullyQualifiedName~ActualPrimaryInitiallyPropagatedFailureRetryBothOrigins&DisplayName~nativeOrigin: True' }
        else { 'FullyQualifiedName~ActualPrimaryInitiallyPropagatedFailureRetryBothOrigins|FullyQualifiedName~SidebandPropagationRetryReferenceTests' }
    & dotnet test tests/Sideband.Tests/Sideband.Tests.csproj -c Release --artifacts-path $ArtifactsPath `
        --filter $filter --results-directory $OutputDirectory --logger 'trx;LogFileName=retry.trx' -v:quiet
    $exit = $LASTEXITCODE
    if (-not (Test-Path -LiteralPath $trx)) { throw "No test evidence was produced (exit $exit)." }
    [xml] $result = Get-Content -LiteralPath $trx -Raw
    $counts = $result.TestRun.ResultSummary.Counters
    $required = if ($NativeOnly) { 3 } else { 8 }
    if ($exit -ne 0 -or [int]$counts.total -ne $required -or [int]$counts.passed -ne $required -or [int]$counts.notExecuted -ne 0) {
        foreach ($failure in $result.TestRun.Results.UnitTestResult | Where-Object outcome -EQ 'Failed') {
            Write-Output $failure.Output.ErrorInfo.Message
            Write-Output $failure.Output.ErrorInfo.StackTrace
        }
        throw "Retry acceptance failed/skipped: passed=$($counts.passed), failed=$($counts.failed), skipped=$($counts.notExecuted)."
    }
    $liveName = 'ActualPrimaryInitiallyPropagatedFailureRetryBothOrigins'
    $live = @($result.TestRun.Results.UnitTestResult | Where-Object {
        $_.testName.Contains($liveName) -and $_.outcome -eq 'Passed'
    })
    $requiredLiveRows = if ($NativeOnly) { 3 } else { 6 }
    if ($live.Count -ne $requiredLiveRows) { throw "All $requiredLiveRows selected live policy/origin rows must execute." }
    $scenarios = @(Get-ChildItem -LiteralPath $env:DOTRETICULUM_REMOTE_RETRY_EVIDENCE -Filter '*.json' -File)
    $requiredScenarios = if ($NativeOnly) { 3 } else { 6 }
    if ($scenarios.Count -ne $requiredScenarios) { throw "Expected $requiredScenarios independently asserted endpoint scenarios." }
    foreach ($file in $scenarios) {
        $scenario = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        $expectedFailures = if ($scenario.policy -eq 2) { 2 } else { 1 }
        if ($scenario.first_failed_groups -ne $expectedFailures -or $scenario.healthy_failed_groups -ne 0 -or $scenario.pn_teardowns -lt 1) {
            throw "Inconsistent scenario evidence: $($file.Name)"
        }
    }
    $after = SourceHashes
    foreach ($path in $owned) {
        if ($before[$path] -ne $after[$path]) { throw "Acceptance source changed while running: $path" }
    }
    $assemblies = [ordered]@{}
    $runtimeDirectory = Join-Path $ArtifactsPath 'bin/Sideband.Tests/release'
    foreach ($assembly in Get-ChildItem -LiteralPath $runtimeDirectory -File -Filter '*.dll' |
        Where-Object Name -Match '^(Sideband|LXMF|DotReticulum)') {
        $assemblies[$assembly.Name] = (Get-FileHash -LiteralPath $assembly.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    if (-not $assemblies.Contains('Sideband.Tests.dll')) { throw 'Missing executed managed assembly provenance.' }
    [ordered]@{
        status = 'passed'
        scope = if ($NativeOnly) { 'Native-origin only. Does not accept the required both-origin gate or primary enabled-policy retry.' }
            else { 'Required both-origin gate' }
        os = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        passed = $required
        skipped = 0
        live_rows = $requiredLiveRows
        independently_asserted_endpoint_scenarios = $requiredScenarios
        primary_observations = '24 actual message_notification cases and one actual two-notification one-shot observation'
        fixture_sha256 = $fixtureHash
        source_hashes = $after
        executed_managed_assemblies = $assemblies
        source_scope = 'Eight additive acceptance paths plus the two unchanged production remote/telemetry dependencies; parent-owned UI/store/map code is not frozen by this manifest.'
        topology = 'Direct links to an actual Python LXMF PN that also hosts the TCP transport hub; both endpoints use transparent100ms per-read latency in each direction to avoid the independently observed primary SQLite callback/insertion race.'
        fault_seam = 'Public PN propagation destination link-established callback tears down each established link; the original callback is retained and delegated after recovery. No source patches, packet replacement, rejection-policy substitution or timeout/attempt overrides.'
        endpoint_observers = 'Native public StateChanged/ErrorReported; actual primary public LXMessage failed/delivery callback registration delegates the original application callbacks.'
        policies = @('fallback disabled: one Failed group', 'fallback enabled: first Failed then PN restored and Sent', 'fallback enabled/continuous PN failure: exactly two Failed groups and eight-second no-third-group observation')
        recovery = 'Every scenario restores the peer and sends/retrieves a NEW healthy propagated command with an authenticated exact echo.'
        storage_boundary = 'Sent and PN storage are asserted before explicit recipient retrieval. Stored requests are not called executed/delivered; recipient execution and automatic replies are asserted separately.'
        peer_settings = 'autopeer=false;8MB PN storage; default stamp cost16,transfer256KB,sync10240KB; explicit endpoint known conversation/allow requests authorization; automatic inbox sync disabled.'
        unchanged_defaults = 'LXMF retry limit5,delivery wait10s,path wait7s,processing4s and link establishment deadlines are not overridden.'
        limits = 'Managed actual-primary daemon core only, not primary GUI/published native UI. No claim for continuous third-group absence beyond the bounded observation, arbitrary Python-plugin ABI, codec/device filters, mixed PN/hop variants or universal elapsed delivery deadline.'
        attempt_schedule_boundary = 'Under the separate silent two-hop receiver-refusal probe, native charged six links and primary four with the same18.0004s individual outgoing deadline; native closed its final pending link after logical Failed. This is a finite LXMF scheduling fidelity boundary, not an unbounded LinkManager defect; this suite does not change it.'
        filter = $filter
    } | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $OutputDirectory 'retry-acceptance.json') -Encoding utf8NoBOM
}
finally {
    $env:PYTHONPATH = $originalPythonPath
    $env:DOTRETICULUM_REMOTE_RETRY_BRIDGES = $originalBridges
    $env:DOTRETICULUM_REMOTE_RETRY_EVIDENCE = $originalEvidence
    Pop-Location
}
