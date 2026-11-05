#Requires -Version 7.0
Describe 'Additional historical native propagation status retention' -Tag 'Integration','Live','Windows' {
    It 'T1140 retains actual primary status through a real five-minute authorization timeout and recovers without restarting' {
        if ($env:DOTSIDEBAND_PROPAGATION_RETENTION -ne '1') {
            Set-ItResult -Skipped -Because 'Requires DOTSIDEBAND_PROPAGATION_RETENTION=1 and the exact historical candidate with fresh output paths.'
            return
        }
        $root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $candidate = $env:DOTSIDEBAND_PROPAGATION_RETENTION_CANDIDATE
        $results = $env:DOTSIDEBAND_PROPAGATION_RETENTION_RESULTS
        $candidate | Should -Not -BeNullOrEmpty
        $results | Should -Not -BeNullOrEmpty
        & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Invoke-SidebandPropagationRetention.ps1') `
            -Live -Candidate $candidate -OutputDirectory $results
        $LASTEXITCODE | Should -Be 0
        $receipt = Get-Content (Join-Path ([IO.Path]::GetFullPath($results, $root)) 'result.json') -Raw | ConvertFrom-Json
        $receipt.Passed | Should -BeTrue
        $receipt.ControlRetrySeconds | Should -BeGreaterOrEqual 300
        $receipt.ElapsedSeconds | Should -BeLessThan 540
        $receipt.NativeSha256 | Should -BeExactly 'e7fd33ac23008bf393e53fe2ca32427866d368d3d094edc09f5758c8a9f4e239'
    }
}
