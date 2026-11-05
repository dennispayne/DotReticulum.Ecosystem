[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Manifest,
    [Parameter(Mandatory)][string]$Results
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$candidate = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json
$resultsPath = [IO.Path]::GetFullPath($Results)
if (Test-Path -LiteralPath $resultsPath) { throw "Fresh results directory required: $resultsPath" }
New-Item -ItemType Directory -Path $resultsPath | Out-Null
function Assert-Frozen {
    foreach ($inputFile in $candidate.Files) {
        $actual = (Get-FileHash -LiteralPath $inputFile.Path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -cne $inputFile.Sha256) { throw "Frozen input changed: $($inputFile.Path)" }
    }
    if ((git rev-parse HEAD).Trim() -cne $candidate.Head) { throw 'Root HEAD changed.' }
    if ((git -C external\DotReticulum rev-parse HEAD).Trim() -cne $candidate.Stack) { throw 'Stack HEAD changed.' }
}
function Invoke-Cases([string]$Name, [string]$Filter, [int]$ExpectedPassed, [int]$ExpectedSkipped) {
    Assert-Frozen
    & dotnet vstest $candidate.TestAssembly "/TestCaseFilter:$Filter" "/Logger:trx;LogFileName=$Name.trx" "/ResultsDirectory:$resultsPath" *>&1 |
        Tee-Object -FilePath (Join-Path $resultsPath "$Name.log") | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Test process failed: $Name ($LASTEXITCODE)" }
    [xml]$trx = Get-Content -LiteralPath (Join-Path $resultsPath "$Name.trx") -Raw
    $nodes = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
    $passed = @($nodes | Where-Object outcome -eq Passed).Count
    $skipped = @($nodes | Where-Object outcome -eq NotExecuted).Count
    $failed = @($nodes | Where-Object { $_.outcome -notin @('Passed', 'NotExecuted') }).Count
    if ($passed -ne $ExpectedPassed -or $skipped -ne $ExpectedSkipped -or $failed -ne 0) {
        throw "Unexpected individual outcomes in ${Name}: passed=$passed skipped=$skipped other=$failed"
    }
    $script:outcomes += [ordered]@{
        Name = $Name; Passed = $passed; Skipped = $skipped; Other = $failed
        Cases = @($nodes | ForEach-Object { [ordered]@{
            Name = $_.testName; Outcome = $_.outcome
            Detail = ($_.SelectNodes("*[local-name()='Output']") | ForEach-Object InnerText) -join "`n"
        } })
    }
    Assert-Frozen
}
$outcomes = @()
Assert-Frozen
foreach ($key in @('DOTRETICULUM_LIVE', 'DOTRETICULUM_NOMADNET_WINDOWS_AUTHORIZATION',
    'DOTRETICULUM_NOMADNET_GUIDE', 'DOTRETICULUM_NOMADNET_MICRON', 'DOTRETICULUM_NOMADNET_PEER_CONTROLS')) {
    [Environment]::SetEnvironmentVariable($key, $null)
}
$env:DOTRETICULUM_NOMADNET_IMAGE_CONVERTER = $candidate.Converter
[Environment]::SetEnvironmentVariable('DOTRETICULUM_NOMADNET_AUTHORIZATION_GENERATOR', $null)
Invoke-Cases 'ordinary' 'FullyQualifiedName~NomadNet.Tests' 497 38
$env:DOTRETICULUM_NOMADNET_AUTHORIZATION_GENERATOR = $candidate.Generator
Invoke-Cases 'authorization-focused' '(FullyQualifiedName~NomadNetWindowsAuthorizationTests|FullyQualifiedName~NomadNetWindowsAuthorizationEnvironmentTests)' 8 0
$env:DOTRETICULUM_PYTHON = $candidate.Reference.Python
$env:DOTRETICULUM_UPSTREAM = $candidate.Reference.Rns
$env:DOTRETICULUM_LXMF_UPSTREAM = $candidate.Reference.Lxmf
$env:DOTRETICULUM_NOMADNET_SOURCE = $candidate.Reference.Primary
$env:PYTHONPATH = @($candidate.Reference.Primary, $candidate.Reference.Rns, $candidate.Reference.Lxmf) -join ';'
$env:DOTRETICULUM_LIVE = '1'
$env:DOTRETICULUM_NOMADNET_WINDOWS_AUTHORIZATION = '1'
$env:DOTRETICULUM_NOMADNET_BINARY = $candidate.Binary
$env:DOTRETICULUM_NOMADNET_AUTHORIZATION_ROOT = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$env:DOTRETICULUM_NOMADNET_AUTHORIZATION_EVIDENCE = Join-Path $resultsPath 'authorization'
$env:DOTRETICULUM_NOMADNET_GUIDE = '1'
$env:DOTRETICULUM_NOMADNET_MICRON = '1'
$env:DOTRETICULUM_NOMADNET_PEER_CONTROLS = '1'
$env:DOTRETICULUM_NOMADNET_IMAGE_CONVERTER = $candidate.Converter
$env:DOTRETICULUM_NOMADNET_GUIDE_EVIDENCE = Join-Path $resultsPath 'guide'
$env:DOTRETICULUM_NOMADNET_MICRON_EVIDENCE = Join-Path $resultsPath 'micron'
$env:DOTRETICULUM_NOMADNET_PEER_CONTROLS_EVIDENCE = Join-Path $resultsPath 'peers'
Invoke-Cases 'authorization-native' 'FullyQualifiedName~NomadNetWindowsAuthorizationNativeTests' 1 0
Invoke-Cases 'guide-micron-peers-native' '(FullyQualifiedName~NomadNetGuideNativeTests|FullyQualifiedName~NomadNetMicronGrammarNativeTests|FullyQualifiedName~NativePeerRemovalPersistsAndDoesNotInventPeersOrEditStaticConfiguration)' 7 0
Assert-Frozen
[ordered]@{ Manifest = [IO.Path]::GetFullPath($Manifest); FreshProcess = $PID; Outcomes = $outcomes } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $resultsPath 'accepted.json') -Encoding utf8
