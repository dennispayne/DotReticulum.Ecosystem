Describe 'sideband published startup announcement settings' -Tag 'Native', 'StartupAnnouncement' {
    BeforeAll {
        $script:startupEnabled = $env:DOTRETICULUM_SIDEBAND_STARTUP_ANNOUNCE -eq '1'
        if ($script:startupEnabled) {
            $script:startupBinary = $env:DOTRETICULUM_SIDEBAND_BINARY
            if ([string]::IsNullOrWhiteSpace($script:startupBinary) -or
                -not (Test-Path -LiteralPath $script:startupBinary -PathType Leaf)) {
                throw 'Select an actual published DOTRETICULUM_SIDEBAND_BINARY.'
            }
        }
    }

    It 'persists strict startup enablement and rejects permissive truthiness without replacing old state' {
        if (-not $script:startupEnabled) {
            Set-ItResult -Skipped -Because 'Enable strict acceptance with DOTRETICULUM_SIDEBAND_STARTUP_ANNOUNCE=1 and a published binary.'
            return
        }
        $root = Join-Path (Join-Path (Get-Location) 'artifacts\tests') ('sideband-startup-' + [guid]::NewGuid().ToString('N'))
        $data = Join-Path $root 'data'
        $null = New-Item -ItemType Directory -Force $root
        try {
            foreach ($value in @('false', 'true', 'null', '0', '1', '"false"')) {
                $inputPath = Join-Path $root 'input.json'
                '{"start_announce":' + $value + '}' | Set-Content $inputPath
                $state = Join-Path $data 'state.json'
                $before = if (Test-Path $state) { (Get-FileHash $state).Hash } else { $null }
                $text = & $script:startupBinary --import-settings $inputPath --data-dir $data 2>&1 | Out-String
                if ($value -in @('true', 'false')) { $LASTEXITCODE | Should -Be 0 -Because $text }
                else {
                    $LASTEXITCODE | Should -Not -Be 0 -Because $text
                    (Get-FileHash $state).Hash | Should -BeExactly $before
                }
            }
            $export = Join-Path $root 'export.json'
            $text = & $script:startupBinary --export-settings $export --data-dir $data 2>&1 | Out-String
            $LASTEXITCODE | Should -Be 0 -Because $text
            (Get-Content $export -Raw | ConvertFrom-Json).start_announce | Should -BeTrue
            (Get-Content $state -Raw | ConvertFrom-Json).OwnDeliveryHash | Should -BeNullOrEmpty
        }
        finally { if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force } }
    }

    It 'activates actual primary startup-disabled profiles and retains immutable source bytes over offline restarts' {
        if (-not $script:startupEnabled) {
            Set-ItResult -Skipped -Because 'Enable strict acceptance with DOTRETICULUM_SIDEBAND_STARTUP_ANNOUNCE=1 and a published binary.'
            return
        }
        $source = $env:DOTRETICULUM_SIDEBAND_STARTUP_PROFILE
        if ([string]::IsNullOrWhiteSpace($source)) { throw 'Set DOTRETICULUM_SIDEBAND_STARTUP_PROFILE to an actual generated primary snapshot.' }
        $root = Join-Path (Join-Path (Get-Location) 'artifacts\tests') ('sideband-startup-import-' + [guid]::NewGuid().ToString('N'))
        $data = Join-Path $root 'native'
        $hashes = @(Get-ChildItem $source -File -Recurse | ForEach-Object { Get-FileHash $_.FullName })
        try {
            $text = & $script:startupBinary --import-profile $source --data-dir $data 2>&1 | Out-String
            $LASTEXITCODE | Should -Be 0 -Because $text
            foreach ($restart in 0..1) {
                $export = Join-Path $root "export-$restart.json"
                $text = & $script:startupBinary --export-settings $export --data-dir $data 2>&1 | Out-String
                $LASTEXITCODE | Should -Be 0 -Because $text
                (Get-Content $export -Raw | ConvertFrom-Json).start_announce | Should -BeFalse
                $text = & $script:startupBinary --check --data-dir $data 2>&1 | Out-String
                $LASTEXITCODE | Should -Be 0 -Because $text
            }
            foreach ($hash in $hashes) { (Get-FileHash $hash.Path).Hash | Should -BeExactly $hash.Hash }
        }
        finally { if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force } }
    }
}
