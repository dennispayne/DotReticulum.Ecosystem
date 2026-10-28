Describe 'sideband native acceptance' -Tag 'Native' {
    BeforeAll {
        $binary = $env:DOTRETICULUM_SIDEBAND_BINARY
        if ([string]::IsNullOrWhiteSpace($binary)) {
            throw 'Set DOTRETICULUM_SIDEBAND_BINARY to the published sideband executable.'
        }
        if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) {
            throw "Sideband executable not found: $binary"
        }

        $root = Join-Path (Get-Location) "artifacts\sideband-pester-$PID"
        $data = Join-Path $root 'data'
        $rns = Join-Path $root 'rns'
        New-Item -ItemType Directory -Force -Path $root | Out-Null
    }

    It 'prints help and initializes, validates, and preserves a native identity' {
        $help = & $binary --help 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 0
        $help | Should -Match 'sideband'

        $init = & $binary --init --data-dir $data --rnsconfig $rns 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 0
        $init | Should -Match 'Identity: ([0-9a-f]{32})'
        $firstHash = [regex]::Match($init, 'Identity: ([0-9a-f]{32})').Groups[1].Value
        Test-Path (Join-Path $data 'state.json') | Should -BeTrue

        $check = & $binary --check --data-dir $data --rnsconfig $rns 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 0
        $check | Should -Match ([regex]::Escape("Identity: $firstHash"))
    }

    AfterAll {
        if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
    }
}
