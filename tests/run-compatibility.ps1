#Requires -Version 7.0
[CmdletBinding()]
param(
    [switch] $Pester,
    [switch] $Gui,
    [switch] $Live,
    [switch] $LxstLive,
    [switch] $NomadNetLive,
    [switch] $Coverage,
    [switch] $RequireComplete,
    [string] $Python = $env:DOTRETICULUM_PYTHON,
    [string] $Upstream = $env:DOTRETICULUM_UPSTREAM,
    [string] $LxmfUpstream = $env:DOTRETICULUM_LXMF_UPSTREAM,
    [string] $LxstUpstream = $env:DOTRETICULUM_LXST_UPSTREAM,
    [string] $NomadNetSource = $env:DOTRETICULUM_NOMADNET_SOURCE,
    [string] $OutputDirectory,
    [string] $ArtifactsPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Compatibility\Reporting.psm1') -Force
$root = Split-Path $PSScriptRoot -Parent
$catalog = Read-CompatibilityCatalog (Join-Path $PSScriptRoot 'Compatibility\catalog.json')
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root "TestResults\compatibility-$([guid]::NewGuid().ToString('N'))" }
if (Test-Path -LiteralPath $OutputDirectory) { throw "Use a fresh results directory: $OutputDirectory" }
$null = New-Item -ItemType Directory -Path $OutputDirectory
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$saved = @{}
$names = @('DOTRETICULUM_LIVE', 'DOTLXST_LIVE', 'DOTRETICULUM_NOMADNET_LIVE',
    'DOTRETICULUM_NOMADNET_NODE_LIVE', 'DOTRETICULUM_PYTHON', 'DOTRETICULUM_UPSTREAM',
    'DOTRETICULUM_LXMF_UPSTREAM', 'DOTRETICULUM_LXST_UPSTREAM', 'DOTRETICULUM_NOMADNET_SOURCE', 'PYTHONPATH')
foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$failures = [Collections.Generic.List[string]]::new()
$commands = [Collections.Generic.List[string]]::new()
$cases = [Collections.Generic.List[object]]::new()
$executed = [Collections.Generic.List[string]]::new()
$nativeBinaries = [Collections.Generic.List[object]]::new()
$provenance = $null

function Format-Command([string] $Executable, [string[]] $Arguments) {
    '& ' + ((@($Executable) + $Arguments | ForEach-Object { "'" + $_.Replace("'", "''") + "'" }) -join ' ')
}

Push-Location $root
try {
    if ($Gui -and !$Pester) { throw '-Gui requires -Pester.' }
    foreach ($name in @('DOTRETICULUM_LXMD_BINARY', 'DOTLXST_RNPHONE_BINARY', 'DOTRETICULUM_RNPHONE_BINARY',
        'DOTRETICULUM_NOMADNET_BINARY', 'DOTRETICULUM_SIDEBAND_BINARY')) {
        $binary = [Environment]::GetEnvironmentVariable($name, 'Process')
        if ($binary) {
            $item = Get-Item -LiteralPath $binary -ErrorAction Stop
            if ($item.PSIsContainer) { throw "$name must name an executable file." }
            $nativeBinaries.Add([pscustomobject]@{
                Variable = $name; Path = $item.FullName; Sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
            })
        }
    }
    $liveEnabled = $Live -or $LxstLive -or $NomadNetLive
    $env:DOTRETICULUM_LIVE = if ($liveEnabled) { '1' } else { '0' }
    $env:DOTLXST_LIVE = if ($LxstLive) { '1' } else { '0' }
    $env:DOTRETICULUM_NOMADNET_LIVE = if ($NomadNetLive) { '1' } else { '0' }
    $env:DOTRETICULUM_NOMADNET_NODE_LIVE = if ($NomadNetLive) { '1' } else { '0' }
    if ($liveEnabled) {
        foreach ($entry in @(
            @{ Name = 'DOTRETICULUM_PYTHON'; Value = $Python },
            @{ Name = 'DOTRETICULUM_UPSTREAM'; Value = $Upstream },
            @{ Name = 'DOTRETICULUM_LXMF_UPSTREAM'; Value = $LxmfUpstream }
        )) {
            if ([string]::IsNullOrWhiteSpace($entry.Value)) { throw "Live mode requires $($entry.Name)." }
            [Environment]::SetEnvironmentVariable($entry.Name, (Resolve-Path -LiteralPath $entry.Value).Path, 'Process')
        }
        $paths = [Collections.Generic.List[string]]::new()
        $paths.Add($env:DOTRETICULUM_UPSTREAM)
        $paths.Add($env:DOTRETICULUM_LXMF_UPSTREAM)
        if ($LxstLive) {
            if (!$LxstUpstream) { throw '-LxstLive requires DOTRETICULUM_LXST_UPSTREAM.' }
            $env:DOTRETICULUM_LXST_UPSTREAM = (Resolve-Path -LiteralPath $LxstUpstream).Path
            $paths.Add($env:DOTRETICULUM_LXST_UPSTREAM)
        }
        if ($NomadNetLive) {
            if (!$NomadNetSource) { throw '-NomadNetLive requires DOTRETICULUM_NOMADNET_SOURCE.' }
            $env:DOTRETICULUM_NOMADNET_SOURCE = (Resolve-Path -LiteralPath $NomadNetSource).Path
            $paths.Add($env:DOTRETICULUM_NOMADNET_SOURCE)
        }
        $paths.Add((Join-Path $PSScriptRoot 'Interop'))
        $env:PYTHONPATH = $paths -join [IO.Path]::PathSeparator
        $verifier = Join-Path $PSScriptRoot 'Interop\reference_environment.py'
        $verifyArguments = @($verifier, '--require-lxmf')
        if ($LxstLive) { $verifyArguments += '--require-lxst' }
        $commands.Add((Format-Command $env:DOTRETICULUM_PYTHON $verifyArguments))
        $verified = & $env:DOTRETICULUM_PYTHON @verifyArguments 2> (Join-Path $OutputDirectory 'reference-error.log')
        $verified | Set-Content -LiteralPath (Join-Path $OutputDirectory 'reference.json') -Encoding utf8
        if ($LASTEXITCODE -ne 0) { throw 'Actual-import reference verification failed; no live tests were run.' }
        $provenance = ($verified -join "`n") | ConvertFrom-Json
    }

    $arguments = @('test', (Join-Path $root 'DotReticulum.Ecosystem.slnx'), '-c', 'Release',
        '--logger', 'trx', '--results-directory', (Join-Path $OutputDirectory 'xunit'), '--verbosity', 'quiet')
    if ($ArtifactsPath) { $arguments += @('--artifacts-path', $ArtifactsPath) }
    if ($Coverage) {
        $arguments += @('--settings', (Join-Path $PSScriptRoot 'coverage.runsettings'), '--collect:XPlat Code Coverage',
            '-m:1', '--', 'xUnit.ParallelizeTestCollections=false')
    }
    $commands.Add((Format-Command 'dotnet' $arguments))
    & dotnet @arguments 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory 'dotnet.log') | Out-Host
    if ($LASTEXITCODE -ne 0) { $failures.Add('The .NET build or tests failed; inspect dotnet.log and TRX.') }
    $executed.Add('xunit')
    foreach ($case in @(Read-TrxCases (Join-Path $OutputDirectory 'xunit'))) { $cases.Add($case) }
    if (!@($cases | Where-Object Runner -eq 'xunit').Count) { $failures.Add('No xUnit test results were produced.') }

    if ($Pester) {
        if (!(Get-Module Pester | Where-Object Version -GE ([version]'5.7.1'))) {
            Import-Module Pester -MinimumVersion 5.7.1 -ErrorAction Stop
        }
        $executed.Add('pester')
        foreach ($suite in @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Pester') -Filter '*.Tests.ps1' -File | Select-Object -ExpandProperty Name)) {
            $requiredBinary = switch ($suite) {
                'Sideband.Tests.ps1' { 'DOTRETICULUM_SIDEBAND_BINARY' }
                'NomadNet.Tests.ps1' { 'DOTRETICULUM_NOMADNET_BINARY' }
                default { '' }
            }
            if ($requiredBinary -and ![Environment]::GetEnvironmentVariable($requiredBinary)) {
                foreach ($test in $catalog.tests | Where-Object suite -eq $suite) {
                    $cases.Add([pscustomobject]@{
                        Runner = 'pester'; Suite = $suite; Name = $test.pattern; Case = $test.pattern
                        Status = 'Skipped'; Detail = "Set $requiredBinary to a published native executable."
                        Artifact = ''; AvailabilityOnly = $true
                    })
                }
                continue
            }
            $path = Join-Path $PSScriptRoot "Pester\$suite"
            $pesterCommand = "Invoke-Pester -Path '$($path.Replace("'", "''"))' -PassThru"
            if (!$Gui) { $pesterCommand += " -ExcludeTag 'GUI'" }
            $commands.Add($pesterCommand)
            $configuration = New-PesterConfiguration
            $configuration.Run.Path = $path
            $configuration.Run.PassThru = $true
            $configuration.Output.Verbosity = 'Minimal'
            if (!$Gui) { $configuration.Filter.ExcludeTag = @('GUI') }
            $configuration.TestResult.Enabled = $true
            $configuration.TestResult.OutputPath = Join-Path $OutputDirectory "$suite.xml"
            $result = Invoke-Pester -Configuration $configuration
            if ($result.Result -eq 'Failed' -or $result.FailedCount -gt 0 -or $result.TotalCount -eq 0) {
                $failures.Add("Pester suite failed or discovered no cases: $suite")
            }
            foreach ($test in $result.Tests) {
                $detail = ($test.ErrorRecord | Out-String).Trim()
                if ($test.Result -eq 'NotRun') { $detail = 'Not selected (GUI requires -Gui).' }
                elseif ($test.Result -eq 'Skipped' -and !$detail) {
                    $detail = "Skipped by $suite prerequisites/platform gates; see recorded mode flags and suite source."
                }
                $cases.Add([pscustomobject]@{
                    Runner = 'pester'; Suite = $suite; Name = $test.ExpandedPath -join '.'
                    Case = $test.ExpandedName
                    Status = switch ($test.Result) { 'Passed' { 'Pass' }; 'Skipped' { 'Skipped' }; 'NotRun' { 'Skipped' }; default { 'Fail' } }
                    Detail = $detail
                    Artifact = Join-Path $OutputDirectory "$suite.xml"
                })
            }
        }
    }
    if ($Coverage) {
        $summary = Join-Path $PSScriptRoot 'coverage_summary.ps1'
        $commands.Add((Format-Command (Join-Path $PSHOME 'pwsh') @('-NoProfile', '-File', $summary, $OutputDirectory)))
        & (Join-Path $PSHOME 'pwsh') -NoProfile -File $summary $OutputDirectory |
            Tee-Object -FilePath (Join-Path $OutputDirectory 'coverage.md') | Out-Host
        if ($LASTEXITCODE -ne 0) { $failures.Add('Production coverage reporting failed.') }
    }
}
catch {
    $failures.Add($_.Exception.Message)
}
finally {
    try {
        $report = New-CompatibilityReport -Catalog $catalog -Cases $cases.ToArray() -ExecutedRunners $executed.ToArray()
        foreach ($test in $report.Tests | Where-Object Status -eq 'Not covered') {
            $failures.Add("Selector matched no discovered cases: $($test.TestId) $($test.Suite) $($test.Pattern)")
        }
        if ($RequireComplete -and @($report.Claims | Where-Object Status -ne 'Pass').Count) {
            $failures.Add('Complete compatibility was required, but some catalogued claims did not pass.')
        }
        $failed = $failures.Count -gt 0 -or @($report.Cases | Where-Object Status -eq 'Fail').Count -gt 0
        $report | Add-Member -NotePropertyMembers @{
            Timestamp = [DateTimeOffset]::UtcNow.ToString('O'); Platform = [Runtime.InteropServices.RuntimeInformation]::OSDescription
            Commit = (& git rev-parse HEAD); References = $catalog.references; Commands = $commands.ToArray()
            WorkingTreeDirty = @(& git status --porcelain --untracked-files=normal).Count -gt 0
            CatalogSha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'Compatibility\catalog.json') -Algorithm SHA256).Hash
            Failures = $failures.ToArray(); NativeBinaries = $nativeBinaries.ToArray(); VerifiedReference = $provenance
            RunStatus = if ($failed) { 'Failed' } else { 'Passed selected checks' }
            Modes = @{ Live = ($Live -or $LxstLive -or $NomadNetLive); LxstLive = [bool]$LxstLive; NomadNetLive = [bool]$NomadNetLive; Gui = [bool]$Gui
                NativeCodecs = $env:DOTLXST_NATIVE; VirtualAudio = $env:DOTLXST_VIRTUAL_AUDIO; WindowsAudio = $env:DOTLXST_WINDOWS_AUDIO }
        }
        Write-CompatibilityReport -Report $report -Directory $OutputDirectory
    }
    finally {
        Pop-Location
        foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
    }
}
Write-Host "Compatibility report: $(Join-Path $OutputDirectory 'compatibility.md')"
if ($failed) {
    foreach ($failure in $failures) { [Console]::Error.WriteLine($failure) }
    exit 1
}
exit 0
