#Requires -Version 7.3
BeforeAll {
    $script:repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $script:initializer = Join-Path $script:repository 'tools\Initialize-DebugWorkspace.ps1'
}

Describe 'Isolated editor debugging' -Tag Tooling {
    It 'creates only isolated RNS configurations for each launch profile' {
        $directory = Join-Path $TestDrive 'debug'
        & $script:initializer -Directory $directory
        $profiles = Get-Content -Raw -LiteralPath (Join-Path $script:repository '.vscode\launch.json') | ConvertFrom-Json
        foreach ($profile in $profiles.configurations) {
            $option = if ($profile.name -like 'Telemetry:*') { '--config' } else { '--rnsconfig' }
            $index = [array]::IndexOf($profile.args, $option)
            $index | Should -BeGreaterOrEqual 0
            $relative = $profile.args[$index + 1].Replace('${workspaceFolder}\artifacts\debug\', '')
            $config = Get-Content -Raw -LiteralPath (Join-Path $directory "$relative\config")
            $config | Should -Match '(?m)^\s*share_instance = No\r?$'
            $config | Should -Match '(?m)^\s*enable_transport = No\r?$'
            $config | Should -Match '(?s)\[interfaces\]\s*$'
        }
        $phone = $profiles.configurations | Where-Object name -Like 'rnphone:*'
        $phone.args | Should -Contain '--headless-audio'
    }

    It 'resets accidental network configuration while preserving saved application state' {
        $directory = Join-Path $TestDrive 'preserved'
        & $script:initializer -Directory $directory
        $state = Join-Path $directory 'sideband\data\state.json'
        Set-Content -LiteralPath $state -Value '{"sentinel":true}'
        $config = Join-Path $directory 'sideband\rns\config'
        Add-Content -LiteralPath $config -Value "[[AccidentalNetwork]]`n  enabled = Yes"
        & $script:initializer -Directory $directory
        Get-Content -Raw -LiteralPath $state | Should -Match '"sentinel":true'
        Get-Content -Raw -LiteralPath $config | Should -Not -Match 'AccidentalNetwork'
    }
}
