Describe 'sideband published stamp settings' -Tag 'Native', 'StampPolicy' {
    BeforeAll {
        $script:stampEnabled = $env:DOTRETICULUM_SIDEBAND_STAMPS -eq '1'
        if ($script:stampEnabled) {
            $script:stampBinary = $env:DOTRETICULUM_SIDEBAND_BINARY
            if ([string]::IsNullOrWhiteSpace($script:stampBinary) -or
                -not (Test-Path -LiteralPath $script:stampBinary -PathType Leaf)) {
                throw 'Stamp acceptance requires the actual published DOTRETICULUM_SIDEBAND_BINARY.'
            }
        }
    }

    It 'preserves executed primary policy transitions across native process restart and atomically refuses malformed changes' {
        if (-not $script:stampEnabled) {
            Set-ItResult -Skipped -Because 'Enable strict published stamp acceptance with DOTRETICULUM_SIDEBAND_STAMPS=1 and a native binary.'
            return
        }
        $root = Join-Path (Join-Path (Get-Location) 'artifacts\tests') ('sideband-stamp-native-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Force $root | Out-Null
        try {
            $fixture = Get-Content (Join-Path $PSScriptRoot '..\Sideband.Tests\Fixtures\sideband-stamp-policy.json') -Raw | ConvertFrom-Json
            $profile = Join-Path $root 'data'
            $config = Join-Path $root 'isolated-rns'
            New-Item -ItemType Directory -Force $config | Out-Null
            "[reticulum]`n share_instance = no`n enable_transport = no`n`n[logging]`n loglevel = 0`n`n[interfaces]`n" |
                Set-Content (Join-Path $config 'config')
            $inputPath = Join-Path $root 'input.json'
            $lastPolicy = $null
            foreach ($row in $fixture.rows) {
                $row.changes | ConvertTo-Json -Depth 10 | Set-Content $inputPath
                $statePath = Join-Path $profile 'state.json'
                $before = if (Test-Path $statePath) { (Get-FileHash $statePath).Hash } else { $null }
                $output = & $script:stampBinary --import-settings $inputPath --data-dir $profile 2>&1 | Out-String
                if ($row.name -in @('tooHigh', 'boolean', 'fractional', 'string')) {
                    $LASTEXITCODE | Should -Not -Be 0 -Because $output
                    (Get-FileHash $statePath).Hash | Should -BeExactly $before
                }
                else {
                    $LASTEXITCODE | Should -Be 0 -Because $output
                    $lastPolicy = $row.observed.configured
                }
                $export = Join-Path $root ($row.name + '.json')
                $output = & $script:stampBinary --export-settings $export --data-dir $profile 2>&1 | Out-String
                $LASTEXITCODE | Should -Be 0 -Because $output
                $settings = Get-Content $export -Raw | ConvertFrom-Json
                $settings.lxmf_require_stamps | Should -Be $lastPolicy.lxmf_require_stamps
                $settings.lxmf_inbound_stamp_cost | Should -Be $lastPolicy.lxmf_inbound_stamp_cost
                $settings.lxmf_ignore_invalid_stamps | Should -Be $lastPolicy.lxmf_ignore_invalid_stamps
                (Get-Content $statePath -Raw | ConvertFrom-Json).OwnDeliveryHash | Should -BeNullOrEmpty
            }
            $output = & $script:stampBinary --check --data-dir $profile --rnsconfig $config 2>&1 | Out-String
            $LASTEXITCODE | Should -Be 0 -Because $output
        }
        finally { if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force } }
    }

    It 'activates the actual primary saved stamp policy through offline profile import and retains it across native restart' {
        if (-not $script:stampEnabled) {
            Set-ItResult -Skipped -Because 'Enable strict published stamp acceptance with DOTRETICULUM_SIDEBAND_STAMPS=1 and a native binary.'
            return
        }
        $source = $env:DOTRETICULUM_SIDEBAND_STAMP_PROFILE
        if ([string]::IsNullOrWhiteSpace($source)) { throw 'Set DOTRETICULUM_SIDEBAND_STAMP_PROFILE to the actual generated primary snapshot.' }
        $root = Join-Path (Join-Path (Get-Location) 'artifacts\tests') ('sideband-stamp-import-' + [guid]::NewGuid().ToString('N'))
        $profile = Join-Path $root 'native'
        $hashes = @(Get-ChildItem $source -File -Recurse | ForEach-Object { Get-FileHash $_.FullName })
        try {
            $output = & $script:stampBinary --import-profile $source --data-dir $profile 2>&1 | Out-String
            $LASTEXITCODE | Should -Be 0 -Because $output
            foreach ($restart in 0..1) {
                $settingsPath = Join-Path $root "export-$restart.json"
                $output = & $script:stampBinary --export-settings $settingsPath --data-dir $profile 2>&1 | Out-String
                $LASTEXITCODE | Should -Be 0 -Because $output
                $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
                $settings.lxmf_require_stamps | Should -BeTrue
                $settings.lxmf_inbound_stamp_cost | Should -BeNullOrEmpty
                $settings.lxmf_ignore_invalid_stamps | Should -BeTrue
                $output = & $script:stampBinary --check --data-dir $profile 2>&1 | Out-String
                $LASTEXITCODE | Should -Be 0 -Because $output
            }
            foreach ($hash in $hashes) { (Get-FileHash $hash.Path).Hash | Should -BeExactly $hash.Hash }
        }
        finally { if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force } }
    }
}
