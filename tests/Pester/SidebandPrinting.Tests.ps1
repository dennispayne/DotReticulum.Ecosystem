#Requires -Version 7.0
Describe 'C108 native selected saved paper software printing' -Tag 'Native', 'C108' -Skip:(-not $env:DOTRETICULUM_PRINTING_BINARY) {
    BeforeAll {
        $binary = [IO.Path]::GetFullPath($env:DOTRETICULUM_PRINTING_BINARY)
        $work = [IO.Path]::GetFullPath($env:DOTRETICULUM_PRINTING_WORKSPACE)
        $null = New-Item $work -ItemType Directory -Force
        $fixture = Get-Content (Join-Path $PSScriptRoot '..\Sideband.Tests\Fixtures\sideband-paper.json') -Raw | ConvertFrom-Json
        function Quote-PrintArg([string] $Value) { "'" + $Value.Replace("'", "'\''") + "'" }
        function New-PrintFixture([string] $Mode = 'capture', [bool] $Enabled = $true) {
            $profile = Join-Path $work ('public native paper ' + [guid]::NewGuid().ToString('N'))
            $config = Join-Path $profile isolated-rns
            $null = New-Item $config -ItemType Directory -Force
            "[reticulum]`n share_instance = no`n enable_transport = no`n`n[logging]`n loglevel = 0`n`n[interfaces]`n" |
                Set-Content (Join-Path $config config) -Encoding utf8NoBOM
            $result = & $binary --init --data-dir $profile --rnsconfig $config 2>&1 | Out-String
            if ($LASTEXITCODE -ne 0) { throw "Could not initialize private native profile: $result" }
            $command = (Quote-PrintArg (Get-Command pwsh).Source) + ' -NoProfile -File ' +
                (Quote-PrintArg (Join-Path $PSScriptRoot 'NomadNetPrinterStub.ps1')) +
                " -Mode $Mode -Output " + (Quote-PrintArg (Join-Path $profile captures)) +
                ' -Marker ' + (Quote-PrintArg (Join-Path $profile marker)) +
                " -Extra 'public literal ; `$(ignored)'"
            $statePath = Join-Path $profile state.json
            $state = Get-Content $statePath -Raw | ConvertFrom-Json
            $id = [guid]::NewGuid().ToString()
            $state.Contacts = @(@{ DeliveryHash = $fixture.destination; DisplayName = 'Public synthetic paper peer' })
            $state.Messages = @(@{
                Id = $id; PeerDeliveryHash = $fixture.destination; Incoming = $false
                Title = ''; Content = $fixture.cases[0].content
                Timestamp = [DateTimeOffset]::UtcNow.ToString('O'); DeliveryState = 'Paper'
                DeliveryMethod = 'Paper'; PaperUri = $fixture.cases[0].uri; ImportedPaperEnvelope = $false
            })
            $state | Add-Member Printing @{ Enabled = $Enabled; Command = $command } -Force
            $state | ConvertTo-Json -Depth 30 | Set-Content $statePath -Encoding utf8NoBOM
            @{ Profile = $profile; Id = $id; Command = $command; StatePath = $statePath }
        }
        function Start-Print($Item, [string] $Command = $Item.Command) {
            $start = [Diagnostics.ProcessStartInfo]::new($binary)
            $start.UseShellExecute = $false
            $start.RedirectStandardOutput = $true
            $start.RedirectStandardError = $true
            foreach ($argument in @('--print-paper', $Item.Id, '--data-dir', $Item.Profile,
                '--confirm-print-command', $Command)) { $start.ArgumentList.Add($argument) }
            [Diagnostics.Process]::Start($start)
        }
        function Complete-Print($Process) {
            $out = $Process.StandardOutput.ReadToEndAsync()
            $err = $Process.StandardError.ReadToEndAsync()
            try {
                if (-not $Process.WaitForExit(35000)) { throw 'Private native print did not meet its deadline.' }
                @{ Code = $Process.ExitCode; Output = $out.GetAwaiter().GetResult() + $err.GetAwaiter().GetResult() }
            }
            finally {
                if (-not $Process.HasExited) { $Process.Kill($true); $Process.WaitForExit() }
                $Process.Dispose()
            }
        }
        function Invoke-Print($Item, [string] $Command = $Item.Command) { Complete-Print (Start-Print $Item $Command) }
        function Wait-PrivateFile([string] $Path) {
            $deadline = [Diagnostics.Stopwatch]::StartNew()
            while (-not [IO.File]::Exists($Path)) {
                if ($deadline.Elapsed.TotalSeconds -gt 12) { throw 'Private process observation did not become ready.' }
                Start-Sleep -Milliseconds 20
            }
        }
    }

    It 'T1008 runs actual native help and refuses disabled or stale-command execution' -Tag 'T1008' {
        $help = & $binary --help 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 0
        $help | Should -Match '\-\-print-paper'
        $item = New-PrintFixture -Enabled $false
        $before = (Get-FileHash $item.StatePath).Hash
        $result = Invoke-Print $item
        $result.Code | Should -Not -Be 0
        $result.Output | Should -Match 'disabled'
        Test-Path (Join-Path $item.Profile captures) | Should -BeFalse
        (Get-FileHash $item.StatePath).Hash | Should -BeExactly $before
        $item = New-PrintFixture
        $result = Invoke-Print $item 'different reviewed command'
        $result.Code | Should -Not -Be 0
        $result.Output | Should -Match 'command changed'
        Test-Path (Join-Path $item.Profile captures) | Should -BeFalse
    }

    It 'T1009 prints exact original paper QR through one private filename, decodes independently and survives clean restart' -Tag 'T1009' {
        $item = New-PrintFixture
        $before = (Get-FileHash $item.StatePath).Hash
        foreach ($restart in 0..1) {
            $result = Invoke-Print $item
            $result.Code | Should -Be 0 -Because $result.Output
            $result.Output | Should -Match 'Software success'
            (Get-FileHash $item.StatePath).Hash | Should -BeExactly $before
        }
        $receipts = @(Get-ChildItem (Join-Path $item.Profile captures) -Filter '*.json')
        $receipts.Count | Should -Be 2
        foreach ($receipt in $receipts) {
            $capture = Get-Content $receipt.FullName -Raw | ConvertFrom-Json
            $capture.extra | Should -BeExactly 'public literal ; $(ignored)'
            Test-Path $capture.original | Should -BeFalse
            $decoded = & $env:DOTRETICULUM_PYTHON (Join-Path $PSScriptRoot '..\Interop\decode_paper_qr.py') $capture.copy
            $LASTEXITCODE | Should -Be 0
            $decoded | Should -BeExactly $fixture.cases[0].uri
        }
        @(Get-ChildItem (Join-Path $item.Profile 'storage\print-jobs') -File).Count | Should -Be 0
    }

    It 'T1010 rejects historical imports and malformed consent without producing a print job' -Tag 'T1010' {
        $item = New-PrintFixture
        $state = Get-Content $item.StatePath -Raw | ConvertFrom-Json
        $state.Messages[0].ImportedPaperEnvelope = $true
        $state | ConvertTo-Json -Depth 30 | Set-Content $item.StatePath -Encoding utf8NoBOM
        $result = Invoke-Print $item
        $result.Code | Should -Not -Be 0
        $result.Output | Should -Match 'Only original saved outgoing'
        $output = & $binary --print-paper $item.Id --data-dir $item.Profile 2>&1 | Out-String
        $LASTEXITCODE | Should -Not -Be 0
        $output | Should -Match 'consent'
        Test-Path (Join-Path $item.Profile captures) | Should -BeFalse
    }

    It 'T1011 reports nonzero printer status and recovers in another native process' -Tag 'T1011' {
        $item = New-PrintFixture -Mode fail
        $result = Invoke-Print $item
        $result.Code | Should -Be 1
        $result.Output | Should -Match 'code 7'
        $state = Get-Content $item.StatePath -Raw | ConvertFrom-Json
        $item.Command = $item.Command.Replace('-Mode fail', '-Mode capture')
        $state.Printing.Command = $item.Command
        $state | ConvertTo-Json -Depth 30 | Set-Content $item.StatePath -Encoding utf8NoBOM
        (Invoke-Print $item).Code | Should -Be 0
        @(Get-ChildItem (Join-Path $item.Profile 'storage\print-jobs') -File).Count | Should -Be 0
    }

    It 'T1012 contains abandoned descendants at timeout and cleans stale jobs after abrupt native-owner restart' -Tag 'T1012' {
        foreach ($abrupt in $false, $true) {
            $item = New-PrintFixture -Mode orphan
            $process = Start-Print $item
            $child = $null
            try {
                $marker = Join-Path $item.Profile marker.child
                Wait-PrivateFile $marker
                $child = [Diagnostics.Process]::GetProcessById([int][IO.File]::ReadAllText($marker))
                if ($abrupt) {
                    $process.Kill()
                    $process.WaitForExit()
                    $null = Complete-Print $process
                }
                else {
                    $result = Complete-Print $process
                    $result.Code | Should -Be 1
                    $result.Output | Should -Match 'timed out'
                }
                $process = $null
                $child.WaitForExit(5000) | Should -BeTrue
                $state = Get-Content $item.StatePath -Raw | ConvertFrom-Json
                $item.Command = $item.Command.Replace('-Mode orphan', '-Mode capture')
                $state.Printing.Command = $item.Command
                $state | ConvertTo-Json -Depth 30 | Set-Content $item.StatePath -Encoding utf8NoBOM
                (Invoke-Print $item).Code | Should -Be 0
                @(Get-ChildItem (Join-Path $item.Profile 'storage\print-jobs') -File).Count | Should -Be 0
                $stream = [IO.File]::Open((Join-Path $item.Profile 'captures\held-public-capture.json'),
                    [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
                $stream.Dispose()
            }
            finally {
                if ($process) {
                    if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
                    $process.Dispose()
                }
                if ($child) { $child.Dispose() }
            }
        }
    }
}
