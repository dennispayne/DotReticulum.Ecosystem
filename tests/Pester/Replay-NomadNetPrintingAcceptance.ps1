#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $CandidateDirectory,
    [Parameter(Mandatory)] [string] $EvidenceDirectory,
    [string] $Repository = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent),
    [switch] $FrozenProduction
)
$ErrorActionPreference = 'Stop'
$CandidateDirectory = (Resolve-Path $CandidateDirectory).Path
$Repository = (Resolve-Path $Repository).Path
$receipt = Get-Content (Join-Path $CandidateDirectory 'handoff.json') -Raw | ConvertFrom-Json
if (-not $receipt.noRepublish -or $receipt.failed -or $receipt.skipped -or $receipt.passed -ne 20) {
    throw 'Select a successfully accepted immutable printing candidate.'
}
$binary = Join-Path $CandidateDirectory ('binary\' + [IO.Path]::GetFileName($receipt.binary))
if ((Get-FileHash $binary).Hash -ne $receipt.sha256) { throw 'Frozen native binary hash differs.' }
foreach ($file in $receipt.harness) {
    if ((Get-FileHash (Join-Path $Repository $file.path)).Hash -ne $file.sha256) {
        throw "Current printing harness differs from frozen acceptance: $($file.path)"
    }
}
if (-not $FrozenProduction) {
    foreach ($relative in 'src\NomadNet\Models.cs', 'src\NomadNet\NomadNetConfigImport.cs',
        'src\NomadNet\Program.cs', 'src\NomadNet\Program.Printing.cs',
        'src\NomadNet\NomadNetPrinter.cs', 'src\NomadNet\NomadNetPrintingPolicy.cs',
        'src\NomadNet\NomadNetWindowsPrintProcess.cs') {
        if ((Get-FileHash (Join-Path $Repository $relative)).Hash -ne
            (Get-FileHash (Join-Path $CandidateDirectory ('source\' + $relative))).Hash) {
            throw "Current printing production differs from frozen acceptance: $relative"
        }
    }
}
foreach ($reference in $receipt.referenceBefore) {
    $name = switch ($reference.name) {
        'RNS' { 'DOTRETICULUM_UPSTREAM' }
        'LXMF' { 'DOTRETICULUM_LXMF_UPSTREAM' }
        'NomadNet' { 'DOTRETICULUM_NOMADNET_SOURCE' }
        default { throw 'Unexpected reference receipt entry.' }
    }
    [Environment]::SetEnvironmentVariable($name, $reference.path, 'Process')
}
$env:DOTRETICULUM_PYTHON = $receipt.python
if ($receipt.powerShell) { $env:DOTRETICULUM_PWSH = $receipt.powerShell }
$operator = Join-Path $CandidateDirectory 'source\tests\Pester\Invoke-NomadNetPrintingAcceptance.ps1'
$nativeSourceRoot = if ($receipt.PSObject.Properties.Name -contains 'nativeSourceRoot') { $receipt.nativeSourceRoot }
    else { $receipt.repository }
& $operator -Binary $binary -Repository $Repository -EvidenceDirectory $EvidenceDirectory `
    -NativeObjectDirectory $receipt.nativeObjectDirectory -CompiledSourceDirectory (Join-Path $CandidateDirectory 'source') `
    -ManagedTestDirectory (Join-Path $CandidateDirectory 'managed') -ManagedSourceRoot $receipt.managedSourceRoot `
    -NativeSourceRoot $nativeSourceRoot
if ($LASTEXITCODE) { exit $LASTEXITCODE }
