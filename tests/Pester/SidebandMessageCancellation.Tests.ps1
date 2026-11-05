Describe 'Sideband bounded active-message cancellation' {
    It 'T445 runs selected native cancellation and real primary recovery across restart' -Tag 'Native', 'Live' {
        if ($env:DOTRETICULUM_SIDEBAND_MESSAGE_CANCELLATION -ne '1') {
            Set-ItResult -Skipped -Because 'Enable explicit native cancellation acceptance with exact binary/hash, assembly, isolated primary paths and persistent results.'
            return
        }
        $assembly = $env:DOTRETICULUM_SIDEBAND_MESSAGE_CANCELLATION_TEST_ASSEMBLY
        $results = $env:DOTRETICULUM_SIDEBAND_MESSAGE_CANCELLATION_RESULTS
        if (-not $assembly -or -not (Test-Path -LiteralPath $assembly -PathType Leaf) -or -not $results) {
            throw 'Select the cancellation managed verification assembly and persistent results.'
        }
        $null = New-Item -ItemType Directory -Path $results -Force
        $output = & dotnet test $assembly --filter 'FullyQualifiedName~SidebandMessageCancellationNativeTests' --logger 'trx;LogFileName=native-cancellation.trx' --results-directory $results --nologo -v:q 2>&1 | Out-String
        $exit = $LASTEXITCODE
        $output | Set-Content -LiteralPath (Join-Path $results 'native-cancellation.log') -Encoding utf8NoBOM
        $exit | Should -Be 0 -Because $output
        [xml] $trx = Get-Content -LiteralPath (Join-Path $results 'native-cancellation.trx') -Raw
        $trx.TestRun.ResultSummary.Counters.executed | Should -Be 1
        $trx.TestRun.ResultSummary.Counters.passed | Should -Be 1
        $trx.TestRun.ResultSummary.Counters.failed | Should -Be 0
    }

    It 'T446 independently regenerates actual primary cancellation and no-op observations' -Tag 'Reference' {
        if (-not $env:DOTRETICULUM_PYTHON -or -not $env:DOTRETICULUM_SIDEBAND_UPSTREAM) {
            Set-ItResult -Skipped -Because 'Select the isolated pinned reference Python, RNS path and clean primary Sideband checkout.'
            return
        }
        $repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $output = Join-Path $TestDrive 'sideband-message-cancellation.json'
        $previous = $env:PYTHONPATH
        try {
            $env:PYTHONPATH = (Join-Path $repository 'tests\Interop') + [IO.Path]::PathSeparator + $previous
            & $env:DOTRETICULUM_PYTHON (Join-Path $repository 'tests\Sideband.Tests\GenerateMessageCancellationVectors.py') --source $env:DOTRETICULUM_SIDEBAND_UPSTREAM --output $output
            $LASTEXITCODE | Should -Be 0
            (Get-FileHash -LiteralPath $output).Hash | Should -Be (Get-FileHash -LiteralPath (
                Join-Path $repository 'tests\Sideband.Tests\Fixtures\sideband-message-cancellation.json')).Hash
        }
        finally { $env:PYTHONPATH = $previous }
    }
}
