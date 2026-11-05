#Requires -Version 7.0
BeforeAll {
    if ($env:DOTRETICULUM_NOMADNET_HOST_STATISTICS -eq '1') {
        if (-not $env:DOTRETICULUM_NOMADNET_HOST_STATISTICS_CANDIDATE -or -not $env:DOTRETICULUM_NOMADNET_HOST_STATISTICS_REPLAY) {
            throw 'Use Invoke-NomadNetHostedStatisticsAcceptance.ps1 with a frozen candidate.'
        }
        $script:frozen = Get-Content $env:DOTRETICULUM_NOMADNET_HOST_STATISTICS_CANDIDATE -Raw | ConvertFrom-Json
        $script:replay = $env:DOTRETICULUM_NOMADNET_HOST_STATISTICS_REPLAY
    }
}
Describe 'C107 native hosted attempt statistics evidence' -Tag 'HostedStatistics' {
    It 'T980 executes the selected native binary help without replacing its hash' {
        if ($env:DOTRETICULUM_NOMADNET_HOST_STATISTICS -ne '1') {
            Set-ItResult -Skipped -Because 'Select C107 frozen native hosted statistics acceptance.'
            return
        }
        $help = & $script:frozen.Binary.Path --help 2>&1
        $LASTEXITCODE | Should -Be 0
        ($help -join "`n") | Should -Match 'Nomad'
        (Get-FileHash $script:frozen.Binary.Path).Hash.ToLowerInvariant() | Should -BeExactly $script:frozen.Binary.Sha256
    }
    It 'T981 records actual-primary native request/reset/restart flow with exact final counters' {
        if ($env:DOTRETICULUM_NOMADNET_HOST_STATISTICS -ne '1') {
            Set-ItResult -Skipped -Because 'Select C107 actual-primary/native hosted statistics replay.'
            return
        }
        $flowFiles = @(Get-ChildItem $script:replay -Recurse -Filter accepted-flow.json -File)
        $flowFiles.Count | Should -Be 1
        $flow = Get-Content $flowFiles[0].FullName -Raw | ConvertFrom-Json
        $flow.Test | Should -BeExactly 'T960'
        $flow.BinarySha256 | Should -BeExactly $script:frozen.Binary.Sha256
        $flow.Final.NodeConnects | Should -Be 1
        $flow.Final.ServedPageRequests | Should -Be 1
        $flow.Final.ServedFileRequests | Should -Be 1
        $flow.Final.ServedMediaRequests | Should -Be 3
    }
    It 'T982 uses the pinned actual primary source and independently executed reset contract' {
        if ($env:DOTRETICULUM_NOMADNET_HOST_STATISTICS -ne '1') {
            Set-ItResult -Skipped -Because 'Select C107 pinned actual-primary statistics reference.'
            return
        }
        $vectors = Get-Content (Join-Path $script:replay vectors.json) -Raw | ConvertFrom-Json
        $vectors.revision | Should -BeExactly 'f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2'
        $vectors.reset.served_page_requests | Should -Be 0
        $vectors.reset.served_file_requests | Should -Be 0
        $vectors.reset.node_connects | Should -Be 0
        $vectors.reset.served_media_requests | Should -Be 3
        $imports = Get-Content (Join-Path $script:replay reference-imports.json) -Raw | ConvertFrom-Json
        [IO.Path]::GetFullPath($imports.nodeModule) | Should -BeExactly (
            Join-Path $script:frozen.Reference.Primary 'nomadnet\Node.py')
    }
}
