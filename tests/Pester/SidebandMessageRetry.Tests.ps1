Describe 'Sideband bounded saved-message text retry' {
    It 'T418 runs the exact selected native executable against the actual primary and across restart' -Tag 'Native', 'Live' {
        if ($env:DOTRETICULUM_SIDEBAND_MESSAGE_RETRY -ne '1') {
            Set-ItResult -Skipped -Because 'Enable explicit native retry acceptance with exact binary/hash, managed verification assembly, primary paths and persistent results.'
            return
        }
        $assembly = $env:DOTRETICULUM_SIDEBAND_MESSAGE_RETRY_TEST_ASSEMBLY
        $results = $env:DOTRETICULUM_SIDEBAND_MESSAGE_RETRY_RESULTS
        if (-not $assembly -or -not (Test-Path -LiteralPath $assembly -PathType Leaf) -or -not $results) {
            throw 'Select DOTRETICULUM_SIDEBAND_MESSAGE_RETRY_TEST_ASSEMBLY and persistent DOTRETICULUM_SIDEBAND_MESSAGE_RETRY_RESULTS.'
        }
        $null = New-Item -ItemType Directory -Path $results -Force
        $output = & dotnet test $assembly --filter 'FullyQualifiedName~SidebandMessageRetryNativeTests' --logger 'trx;LogFileName=native-retry.trx' --results-directory $results --nologo -v:q 2>&1 | Out-String
        $exit = $LASTEXITCODE
        $output | Set-Content -LiteralPath (Join-Path $results 'native-retry.log') -Encoding utf8NoBOM
        $exit | Should -Be 0 -Because $output
        [xml] $trx = Get-Content -LiteralPath (Join-Path $results 'native-retry.trx') -Raw
        $trx.TestRun.ResultSummary.Counters.executed | Should -Be 1
        $trx.TestRun.ResultSummary.Counters.passed | Should -Be 1
        $trx.TestRun.ResultSummary.Counters.failed | Should -Be 0
    }

    It 'T419 independently regenerates exact executed-primary retry and failure observations' -Tag 'Reference' {
        if (-not $env:DOTRETICULUM_PYTHON -or -not $env:DOTRETICULUM_SIDEBAND_UPSTREAM) {
            Set-ItResult -Skipped -Because 'Select the isolated pinned reference Python, RNS path and clean primary Sideband checkout.'
            return
        }
        $output = Join-Path $TestDrive 'sideband-message-retry.json'
        $repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $previous = $env:PYTHONPATH
        try {
            $env:PYTHONPATH = (Join-Path $repository 'tests\Interop') + [IO.Path]::PathSeparator + $previous
            & $env:DOTRETICULUM_PYTHON (Join-Path $repository 'tests\Sideband.Tests\GenerateMessageRetryVectors.py') --source $env:DOTRETICULUM_SIDEBAND_UPSTREAM --output $output
            $LASTEXITCODE | Should -Be 0
            (Get-FileHash -LiteralPath $output).Hash | Should -Be (Get-FileHash -LiteralPath (
                Join-Path $repository 'tests\Sideband.Tests\Fixtures\sideband-message-retry.json')).Hash
        }
        finally { $env:PYTHONPATH = $previous }
    }
}
