#Requires -Version 7.3
[CmdletBinding()]
param(
    [string] $Repository = (Split-Path $PSScriptRoot -Parent),
    [string] $Settings = (Join-Path $PSScriptRoot '..\PSScriptAnalyzerSettings.psd1'),
    [string] $Report = (Join-Path $PSScriptRoot '..\TestResults\analysis\powershell.sarif')
)
$ErrorActionPreference = 'Stop'
Import-Module PSScriptAnalyzer -RequiredVersion 1.24.0 -ErrorAction Stop
$root = (Resolve-Path -LiteralPath $Repository).Path
$scripts = @(& git -C $root -c core.quotepath=false ls-files -- '*.ps1' '*.psm1' '*.psd1')
if ($LASTEXITCODE -ne 0 -or !$scripts.Count) { throw 'Cannot discover tracked PowerShell scripts.' }
$diagnostics = @(foreach ($script in $scripts) {
    Invoke-ScriptAnalyzer -Path (Join-Path $root $script) -Settings $Settings -ErrorAction Stop
})
$results = @(foreach ($diagnostic in $diagnostics) {
    $relative = [IO.Path]::GetRelativePath($root, $diagnostic.ScriptPath)
    $uri = (($relative -split '[\\/]') | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/'
    $level = switch ([string]$diagnostic.Severity) {
        'Error' { 'error' }
        'ParseError' { 'error' }
        'Warning' { 'warning' }
        'Information' { 'note' }
        default { throw "Unknown diagnostic severity: $($diagnostic.Severity)" }
    }
    Write-Host "$relative`:$($diagnostic.Line): $level $($diagnostic.RuleName): $($diagnostic.Message)"
    @{
        ruleId = $diagnostic.RuleName; level = $level; message = @{ text = $diagnostic.Message }
        locations = @(@{ physicalLocation = @{
            artifactLocation = @{ uri = $uri }; region = @{ startLine = [Math]::Max(1, $diagnostic.Line) }
        } })
    }
})
$rules = @($diagnostics.RuleName | Sort-Object -Unique | ForEach-Object { @{ id = $_; shortDescription = @{ text = $_ } } })
@{
    '$schema' = 'https://json.schemastore.org/sarif-2.1.0.json'; version = '2.1.0'
    runs = @(@{
        tool = @{ driver = @{ name = 'PSScriptAnalyzer'; version = '1.24.0'; rules = $rules } }
        automationDetails = @{ id = '/language:powershell/' }
        invocations = @(@{ executionSuccessful = $true }); results = $results
    })
} | ConvertTo-Json -Depth 20 |
    ForEach-Object {
        $null = New-Item -ItemType Directory -Force -Path (Split-Path $Report -Parent)
        $_ | Set-Content -LiteralPath $Report -Encoding utf8
    }
Write-Host "Analyzed $($scripts.Count) tracked scripts; $($results.Count) findings."
if (@($results | Where-Object level -In 'error', 'warning').Count) { exit 1 }
