Describe 'sideband published delivery limits' -Tag 'Native', 'DeliveryLimits' {
    BeforeAll {
        $script:limitEnabled = $env:DOTRETICULUM_SIDEBAND_DELIVERY_LIMITS -eq '1'
        if ($script:limitEnabled) {
            $script:limitBinary = $env:DOTRETICULUM_SIDEBAND_BINARY
            if ([string]::IsNullOrWhiteSpace($script:limitBinary) -or
                -not (Test-Path -LiteralPath $script:limitBinary -PathType Leaf)) {
                throw 'Delivery-limit acceptance requires an actual published DOTRETICULUM_SIDEBAND_BINARY.'
            }
        }
    }

    It 'persists executed primary boolean transitions across process restarts and refuses implicit truthiness atomically' {
        if (-not $script:limitEnabled) {
            Set-ItResult -Skipped -Because 'Enable strict published acceptance with DOTRETICULUM_SIDEBAND_DELIVERY_LIMITS=1 and a native binary.'
            return
        }
        $root = Join-Path (Join-Path (Get-Location) 'artifacts\tests') ('sideband-limits-' + [guid]::NewGuid().ToString('N'))
        $profile = Join-Path $root 'data'
        $null = New-Item -ItemType Directory -Force $root
        try {
            $fixture = Get-Content (Join-Path $PSScriptRoot '..\Sideband.Tests\Fixtures\sideband-delivery-limits.json') -Raw | ConvertFrom-Json
            $last = $true
            foreach ($row in $fixture.rows) {
                $inputPath = Join-Path $root 'input.json'
                $row.changes | ConvertTo-Json | Set-Content $inputPath
                $state = Join-Path $profile 'state.json'
                $before = if (Test-Path $state) { (Get-FileHash $state).Hash } else { $null }
                $output = & $script:limitBinary --import-settings $inputPath --data-dir $profile 2>&1 | Out-String
                if ($row.name -in @('null', 'zero', 'one', 'string')) {
                    $LASTEXITCODE | Should -Not -Be 0 -Because $output
                    (Get-FileHash $state).Hash | Should -BeExactly $before
                }
                else {
                    $LASTEXITCODE | Should -Be 0 -Because $output
                    $last = $row.observed.configured
                }
                $export = Join-Path $root ($row.name + '.json')
                $output = & $script:limitBinary --export-settings $export --data-dir $profile 2>&1 | Out-String
                $LASTEXITCODE | Should -Be 0 -Because $output
                (Get-Content $export -Raw | ConvertFrom-Json).lxm_limit_1mb | Should -Be $last
                (Get-Content $state -Raw | ConvertFrom-Json).OwnDeliveryHash | Should -BeNullOrEmpty
            }
        }
        finally { if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force } }
    }

    It 'activates the actual primary large-transfer profile setting through offline import and preserves immutable source bytes' {
        if (-not $script:limitEnabled) {
            Set-ItResult -Skipped -Because 'Enable strict published acceptance with DOTRETICULUM_SIDEBAND_DELIVERY_LIMITS=1 and a native binary.'
            return
        }
        $source = $env:DOTRETICULUM_SIDEBAND_DELIVERY_PROFILE
        if ([string]::IsNullOrWhiteSpace($source)) { throw 'Set DOTRETICULUM_SIDEBAND_DELIVERY_PROFILE to an actual generated primary snapshot.' }
        $root = Join-Path (Join-Path (Get-Location) 'artifacts\tests') ('sideband-limits-import-' + [guid]::NewGuid().ToString('N'))
        $profile = Join-Path $root 'native'
        $hashes = @(Get-ChildItem $source -File -Recurse | ForEach-Object { Get-FileHash $_.FullName })
        try {
            $output = & $script:limitBinary --import-profile $source --data-dir $profile 2>&1 | Out-String
            $LASTEXITCODE | Should -Be 0 -Because $output
            foreach ($restart in 0..1) {
                $export = Join-Path $root "export-$restart.json"
                $output = & $script:limitBinary --export-settings $export --data-dir $profile 2>&1 | Out-String
                $LASTEXITCODE | Should -Be 0 -Because $output
                (Get-Content $export -Raw | ConvertFrom-Json).lxm_limit_1mb | Should -BeFalse
                $output = & $script:limitBinary --check --data-dir $profile 2>&1 | Out-String
                $LASTEXITCODE | Should -Be 0 -Because $output
            }
            foreach ($hash in $hashes) { (Get-FileHash $hash.Path).Hash | Should -BeExactly $hash.Hash }
        }
        finally { if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force } }
    }
}
