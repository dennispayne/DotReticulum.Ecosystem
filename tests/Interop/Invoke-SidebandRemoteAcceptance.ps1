[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $ArtifactsPath,
    [Parameter(Mandatory)]
    [string] $OutputDirectory,
    [switch] $IncludeRegression
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$ArtifactsPath = [System.IO.Path]::GetFullPath($ArtifactsPath)
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
if ($env:DOTRETICULUM_LIVE -ne '1' -or $env:DOTSIDEBAND_LIVE -ne '1') {
    throw 'Actual-primary acceptance requires DOTRETICULUM_LIVE=1 and DOTSIDEBAND_LIVE=1; disabled modes are not a pass.'
}
foreach ($name in 'DOTRETICULUM_PYTHON', 'DOTRETICULUM_UPSTREAM', 'DOTRETICULUM_LXST_UPSTREAM', 'DOTRETICULUM_SIDEBAND_UPSTREAM') {
    $value = [Environment]::GetEnvironmentVariable($name)
    if (-not $value -or -not (Test-Path -LiteralPath $value)) {
        throw "Missing reference prerequisite $name"
    }
}
if (-not $IsWindows -and $ArtifactsPath.StartsWith('/mnt/', [StringComparison]::Ordinal)) {
    throw 'Use a native Linux filesystem for test outputs, identities and permissions, not the mounted Windows checkout.'
}
[System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$originalPythonPath = $env:PYTHONPATH
Push-Location $repository
try {
    $pythonRoots = @($env:DOTRETICULUM_UPSTREAM)
    if ($env:DOTRETICULUM_LXMF_UPSTREAM) { $pythonRoots += $env:DOTRETICULUM_LXMF_UPSTREAM }
    $pythonRoots += $env:DOTRETICULUM_LXST_UPSTREAM
    $pythonRoots += $PSScriptRoot
    $env:PYTHONPATH = $pythonRoots -join [System.IO.Path]::PathSeparator
    & $env:DOTRETICULUM_PYTHON (Join-Path $PSScriptRoot 'reference_environment.py') --require-lxmf --require-lxst
    if ($LASTEXITCODE -ne 0) { throw 'Actual imported reference verification failed.' }
    $generated = Join-Path $OutputDirectory 'sideband-remote-reference.json'
    & $env:DOTRETICULUM_PYTHON (Join-Path $repository 'tests/Sideband.Tests/GenerateSidebandRemoteVectors.py') `
        --source $env:DOTRETICULUM_SIDEBAND_UPSTREAM --output $generated |
        Set-Content (Join-Path $OutputDirectory 'reference-generation.log') -Encoding utf8NoBOM
    if ($LASTEXITCODE -ne 0) { throw 'Actual imported primary method execution failed.' }
    $referenceHash = (Get-FileHash $generated -Algorithm SHA256).Hash
    if ($referenceHash -ne (Get-FileHash (Join-Path $repository 'tests/Sideband.Tests/Fixtures/sideband-remote.json') -Algorithm SHA256).Hash) {
        throw 'Fresh actual-primary observations do not byte-match the committed corpus.'
    }
    $generatedAdmission = Join-Path $OutputDirectory 'sideband-admission-reference.json'
    & $env:DOTRETICULUM_PYTHON (Join-Path $repository 'tests/Sideband.Tests/GenerateSidebandAdmissionVectors.py') `
        --source $env:DOTRETICULUM_SIDEBAND_UPSTREAM --output $generatedAdmission |
        Set-Content (Join-Path $OutputDirectory 'admission-reference-generation.log') -Encoding utf8NoBOM
    if ($LASTEXITCODE -ne 0) { throw 'Actual primary ordinary admission execution failed.' }
    $admissionHash = (Get-FileHash $generatedAdmission -Algorithm SHA256).Hash
    if ($admissionHash -ne (Get-FileHash (Join-Path $repository 'tests/Sideband.Tests/Fixtures/sideband-admission.json') -Algorithm SHA256).Hash) {
        throw 'Fresh primary ordinary-admission observations do not byte-match the corpus.'
    }
    $filter = 'FullyQualifiedName~SidebandRemoteCommand|FullyQualifiedName~ActualPrimaryRemoteCommands|FullyQualifiedName~SidebandIncomingAdmission|FullyQualifiedName~ActualPrimaryOrdinaryAdmission'
    if ($IncludeRegression) { $filter += '|FullyQualifiedName~SidebandTelemetry|FullyQualifiedName~SidebandPaper|FullyQualifiedName~SidebandRemoteUiTests|FullyQualifiedName~SidebandSettingsTests' }
    $trx = Join-Path $OutputDirectory 'remote.trx'
    if (Test-Path -LiteralPath $trx) { Remove-Item -LiteralPath $trx -Force }
    & dotnet test tests/Sideband.Tests/Sideband.Tests.csproj -c Release --artifacts-path $ArtifactsPath `
        --filter $filter --results-directory $OutputDirectory --logger 'trx;LogFileName=remote.trx' -v:quiet
    $testExit = $LASTEXITCODE
    if (-not (Test-Path -LiteralPath $trx)) { throw "No test evidence was produced (exit $testExit)." }
    [xml] $results = Get-Content -LiteralPath $trx -Raw
    $counts = $results.TestRun.ResultSummary.Counters
    if ($testExit -ne 0 -or [int]$counts.failed -ne 0 -or [int]$counts.notExecuted -ne 0 -or [int]$counts.total -lt 21) {
        foreach ($failure in $results.TestRun.Results.UnitTestResult | Where-Object outcome -EQ 'Failed') {
            Write-Output $failure.Output.ErrorInfo.Message
            Write-Output $failure.Output.ErrorInfo.StackTrace
        }
        throw "Acceptance failed or skipped cases: passed=$($counts.passed), failed=$($counts.failed), notExecuted=$($counts.notExecuted)."
    }
    foreach ($application in 'ActualPrimaryRemoteCommandsAuthorizationMalformedRecoveryAndRestart', 'ActualPrimaryOrdinaryAdmissionDistinguishesDiscoveryMetadataAndConversation') {
        $executed = @($results.TestRun.Results.UnitTestResult | Where-Object { $_.testName.Contains($application) -and $_.outcome -eq 'Passed' })
        if ($executed.Count -ne 3) { throw "Expected all three actual-primary topologies for $application; found $($executed.Count)." }
    }
    [ordered]@{
        status = 'passed'
        os = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
        passed = [int]$counts.passed
        skipped = [int]$counts.notExecuted
        reference_fixture_sha256 = $referenceHash.ToLowerInvariant()
        admission_fixture_sha256 = $admissionHash.ToLowerInvariant()
        admission_reference_observations = '112 delivery dispatches, 10 ingestion/conversation-creation observations, one announce trace'
        legacy_contact_boundary = 'Preexisting native contact origins cannot be reconstructed. Missing ConversationKnown defaults true for backward compatibility/imports. Only new automatic discovery and metadata-only contacts start false.'
        topologies = @('direct', 'Python transport', '.NET transport')
        tcp_delay_ms_per_read_each_direction = 100
        application = 'Actual imported unmodified primary daemon core, not the primary GUI or native published CLI'
        native_published_remote_execution = 'not claimed by this managed application suite'
        upstream_loopback_boundary = 'Undelayed Linux primary send_command can return false after transmission with UNIQUE constraint failed: lxm.lxm_hash. Tests retain the accepted assertion and add disclosed transparent TCP latency, not a primary code shim.'
        python_plugin_execution = 'unsupported in native application; no production Python ABI bridge'
        filters = $filter
    } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $OutputDirectory 'remote-acceptance.json') -Encoding utf8NoBOM
}
finally {
    $env:PYTHONPATH = $originalPythonPath
    Pop-Location
}
