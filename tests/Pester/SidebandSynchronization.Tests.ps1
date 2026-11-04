Describe 'sideband published manual synchronization limits' -Tag 'Native', 'SynchronizationLimits' {
    BeforeAll {
        $script:syncEnabled = $env:DOTRETICULUM_SIDEBAND_SYNC_LIMITS -eq '1'
        if ($script:syncEnabled) {
            $script:syncBinary = $env:DOTRETICULUM_SIDEBAND_BINARY
            if ([string]::IsNullOrWhiteSpace($script:syncBinary) -or
                -not (Test-Path -LiteralPath $script:syncBinary -PathType Leaf)) {
                throw 'Select an actual published DOTRETICULUM_SIDEBAND_BINARY.'
            }
        }
    }

    It 'persists reference null boolean and count transitions and atomically refuses malformed inactive counts' {
        if (-not $script:syncEnabled) {
            Set-ItResult -Skipped -Because 'Enable strict native acceptance with DOTRETICULUM_SIDEBAND_SYNC_LIMITS=1 and a published binary.'
            return
        }
        $root = Join-Path (Join-Path (Get-Location) 'artifacts\tests') ('sideband-sync-' + [guid]::NewGuid().ToString('N'))
        $data = Join-Path $root 'data'
        $null = New-Item -ItemType Directory -Force $root
        try {
            $fixture = Get-Content (Join-Path $PSScriptRoot '..\Sideband.Tests\Fixtures\sideband-sync-limits.json') -Raw | ConvertFrom-Json
            $limit = $null
            $maximum = 3
            foreach ($row in $fixture.rows) {
                $inputPath = Join-Path $root 'settings.json'
                $row.changes | ConvertTo-Json | Set-Content $inputPath
                $state = Join-Path $data 'state.json'
                $before = if (Test-Path $state) { (Get-FileHash $state).Hash } else { $null }
                $text = & $script:syncBinary --import-settings $inputPath --data-dir $data 2>&1 | Out-String
                if ($row.name -in @('zero', 'negative', 'string')) {
                    $LASTEXITCODE | Should -Not -Be 0 -Because $text
                    (Get-FileHash $state).Hash | Should -BeExactly $before
                }
                else {
                    $LASTEXITCODE | Should -Be 0 -Because $text
                    $limit = $row.observed.configured.lxmf_sync_limit
                    $maximum = $row.observed.configured.lxmf_sync_max
                }
                $export = Join-Path $root ($row.name + '.json')
                $text = & $script:syncBinary --export-settings $export --data-dir $data 2>&1 | Out-String
                $LASTEXITCODE | Should -Be 0 -Because $text
                $settings = Get-Content $export -Raw | ConvertFrom-Json
                $settings.lxmf_sync_limit | Should -Be $limit
                $settings.lxmf_sync_max | Should -Be $maximum
                (Get-Content $state -Raw | ConvertFrom-Json).OwnDeliveryHash | Should -BeNullOrEmpty
            }
        }
        finally { if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force } }
    }

    It 'activates actual primary profile limits and retains immutable source bytes across offline native restarts' {
        if (-not $script:syncEnabled) {
            Set-ItResult -Skipped -Because 'Enable strict native acceptance with DOTRETICULUM_SIDEBAND_SYNC_LIMITS=1 and a published binary.'
            return
        }
        $source = $env:DOTRETICULUM_SIDEBAND_SYNC_PROFILE
        if ([string]::IsNullOrWhiteSpace($source)) { throw 'Set DOTRETICULUM_SIDEBAND_SYNC_PROFILE to the actual generated primary snapshot.' }
        $root = Join-Path (Join-Path (Get-Location) 'artifacts\tests') ('sideband-sync-import-' + [guid]::NewGuid().ToString('N'))
        $data = Join-Path $root 'native'
        $hashes = @(Get-ChildItem $source -File -Recurse | ForEach-Object { Get-FileHash $_.FullName })
        try {
            $text = & $script:syncBinary --import-profile $source --data-dir $data 2>&1 | Out-String
            $LASTEXITCODE | Should -Be 0 -Because $text
            foreach ($restart in 0..1) {
                $export = Join-Path $root "export-$restart.json"
                $text = & $script:syncBinary --export-settings $export --data-dir $data 2>&1 | Out-String
                $LASTEXITCODE | Should -Be 0 -Because $text
                $settings = Get-Content $export -Raw | ConvertFrom-Json
                $settings.lxmf_sync_limit | Should -BeTrue
                $settings.lxmf_sync_max | Should -Be 2
                $text = & $script:syncBinary --check --data-dir $data 2>&1 | Out-String
                $LASTEXITCODE | Should -Be 0 -Because $text
            }
            foreach ($hash in $hashes) { (Get-FileHash $hash.Path).Hash | Should -BeExactly $hash.Hash }
        }
        finally { if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force } }
    }
}
