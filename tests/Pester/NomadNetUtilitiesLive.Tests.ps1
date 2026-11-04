Describe 'NomadNet selected native and actual-primary utility interoperability' -Tag 'Utilities', 'Live' {
    It 'T362 exchanges native/primary speed resources and verifies probe proofs and an authorized rncp file' {
        if ($env:DOTRETICULUM_NOMADNET_UTILITIES -ne '1' -or $env:DOTRETICULUM_LIVE -ne '1') {
            Set-ItResult -Skipped -Because 'Set BOTH DOTRETICULUM_NOMADNET_UTILITIES=1 and DOTRETICULUM_LIVE=1 with an explicit native executable and the pinned Python reference environment.'
            return
        }
        foreach ($name in 'DOTRETICULUM_NOMADNET_BINARY', 'DOTRETICULUM_PYTHON',
            'DOTRETICULUM_UPSTREAM', 'DOTRETICULUM_LXMF_UPSTREAM', 'DOTRETICULUM_NOMADNET_SOURCE') {
            if (-not [Environment]::GetEnvironmentVariable($name)) { throw "$name is required for enabled actual-primary utility acceptance." }
        }
        $repository = if ($env:DOTRETICULUM_TEST_REPOSITORY) { $env:DOTRETICULUM_TEST_REPOSITORY }
            else { Split-Path (Split-Path $PSScriptRoot -Parent) -Parent }
        $evidence = if ($env:DOTRETICULUM_NOMADNET_UTILITY_EVIDENCE) { $env:DOTRETICULUM_NOMADNET_UTILITY_EVIDENCE }
            else { Join-Path (Get-Location) "artifacts/tests/nomad-utilities/live utility $([guid]::NewGuid().ToString('N'))" }
        $result = & (Join-Path $repository 'tests/Pester/Invoke-NomadNetUtilityAcceptance.ps1') `
            -Repository $repository -EvidenceDirectory $evidence
        $result.success | Should -BeTrue
        $result.nativeFormat | Should -BeIn @('ELF', 'PE without CLR directory')
        $result.binarySha256Before | Should -Be $result.binarySha256After
        @($result.observations).Count | Should -Be 5
        Test-Path -LiteralPath (Join-Path $evidence receipt.json) | Should -BeTrue
    }

    It 'T363 sustains unchanged native/primary processes for 600 seconds with sixteen spaced both-origin 4MiB pairs' -Tag 'Endurance' {
        if ($env:DOTRETICULUM_NOMADNET_UTILITIES_ENDURANCE -ne '1' -or
            $env:DOTRETICULUM_NOMADNET_UTILITIES -ne '1' -or $env:DOTRETICULUM_LIVE -ne '1') {
            Set-ItResult -Skipped -Because 'Endurance requires ALL THREE DOTRETICULUM_NOMADNET_UTILITIES_ENDURANCE=1, DOTRETICULUM_NOMADNET_UTILITIES=1 and DOTRETICULUM_LIVE=1; it runs for at least 600 measured seconds.'
            return
        }
        foreach ($name in 'DOTRETICULUM_NOMADNET_BINARY', 'DOTRETICULUM_PYTHON',
            'DOTRETICULUM_UPSTREAM', 'DOTRETICULUM_LXMF_UPSTREAM', 'DOTRETICULUM_NOMADNET_SOURCE') {
            if (-not [Environment]::GetEnvironmentVariable($name)) { throw "$name is required for enabled endurance acceptance." }
        }
        $repository = if ($env:DOTRETICULUM_TEST_REPOSITORY) { $env:DOTRETICULUM_TEST_REPOSITORY }
            else { Split-Path (Split-Path $PSScriptRoot -Parent) -Parent }
        $evidence = if ($env:DOTRETICULUM_NOMADNET_UTILITY_EVIDENCE) {
            "$($env:DOTRETICULUM_NOMADNET_UTILITY_EVIDENCE)-endurance"
        } else { Join-Path (Get-Location) "artifacts/tests/nomad-utilities/live endurance $([guid]::NewGuid().ToString('N'))" }
        $result = & (Join-Path $repository 'tests/Pester/Invoke-NomadNetUtilityAcceptance.ps1') `
            -Repository $repository -EvidenceDirectory $evidence -Endurance -TimeoutSeconds 1100
        $result.success | Should -BeTrue
        $result.nativeFormat | Should -BeIn @('ELF', 'PE without CLR directory')
        $result.binarySha256Before | Should -Be $result.binarySha256After
        @($result.observations).Count | Should -Be 5
        $result.mode | Should -Be endurance
        $result.endurance.completed | Should -BeTrue
        $result.endurance.measuredSeconds | Should -BeGreaterOrEqual 600
        @($result.endurance.pairs).Count | Should -Be 16
        @($result.launches).Count | Should -Be 2
        @($result.storageChecks).Count | Should -Be 2
        @($result.storageChecks | Where-Object leftoverPayloads -NE 0).Count | Should -Be 0
        @($result.exits | Where-Object { -not $_.exited -or $_.code -ne 0 }).Count | Should -Be 0
        $result.monotonicSeconds | Should -BeLessOrEqual 1200
        Test-Path -LiteralPath (Join-Path $evidence receipt.json) | Should -BeTrue
    }
}
