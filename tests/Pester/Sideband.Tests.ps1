Describe 'sideband native acceptance' -Tag 'Native' {
    BeforeAll {
        $binary = $env:DOTRETICULUM_SIDEBAND_BINARY
        if ([string]::IsNullOrWhiteSpace($binary) -or -not (Test-Path -LiteralPath $binary -PathType Leaf)) {
            throw 'Set DOTRETICULUM_SIDEBAND_BINARY to the published sideband executable.'
        }

        $root = Join-Path (Join-Path (Get-Location) 'artifacts\tests') "sideband-pester-$PID"
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

    It 'imports upstream JSON settings, reports unsupported keys and exports them across native restarts' {
        $inputPath = Join-Path $root 'input.json'
        $outputPath = Join-Path $root 'export.json'
        @{
            display_name = 'Native settings peer'
            dark_ui = $false
            lxmf_propagation_node = '00112233445566778899aabbccddeeff'
            propagation_by_default = $true
            lxmf_periodic_sync = $true
            lxmf_sync_interval = 120
            unsupported_reference_setting = @(1, 'retained')
        } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $inputPath
        $imported = & $binary --config $data --import-settings $inputPath 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 0
        $imported | Should -Match 'Preserved but not applied: unsupported_reference_setting'
        & $binary --config $data --export-settings $outputPath
        $LASTEXITCODE | Should -Be 0
        $settings = Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json
        $settings.display_name | Should -Be 'Native settings peer'
        $settings.dark_ui | Should -BeFalse
        $settings.lxmf_propagation_node | Should -Be '00112233445566778899aabbccddeeff'
        $settings.lxmf_periodic_sync | Should -BeTrue
        $settings.lxmf_sync_interval | Should -Be 120
        $settings.unsupported_reference_setting[1] | Should -Be 'retained'
        $original = Get-FileHash -LiteralPath (Join-Path $data 'state.json')
        '{"display_name":"must not replace","lxmf_propagation_node":"bad"}' | Set-Content -LiteralPath $inputPath
        & $binary --config $data --import-settings $inputPath 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 1
        (Get-FileHash -LiteralPath (Join-Path $data 'state.json')).Hash | Should -Be $original.Hash
        '{"display_name":"must not replace","lxmf_sync_interval":1.5}' | Set-Content -LiteralPath $inputPath
        & $binary --config $data --import-settings $inputPath 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 1
        (Get-FileHash -LiteralPath (Join-Path $data 'state.json')).Hash | Should -Be $original.Hash
    }

    AfterAll {
        if ($root -and (Test-Path -LiteralPath $root)) { Remove-Item -LiteralPath $root -Recurse -Force }
    }
}

Describe 'sideband desktop GUI acceptance' -Tag 'GUI' {
    BeforeAll {
        $guiBinary = $env:DOTRETICULUM_SIDEBAND_BINARY
        if ([string]::IsNullOrWhiteSpace($guiBinary) -or -not (Test-Path -LiteralPath $guiBinary -PathType Leaf)) {
            throw 'Set DOTRETICULUM_SIDEBAND_BINARY to the published sideband executable.'
        }

        $guiRoot = Join-Path (Join-Path (Get-Location) 'artifacts\tests') "sideband-gui-pester-$PID"
        $guiData = Join-Path $guiRoot 'data'
        $guiRns = Join-Path $guiRoot 'rns'
        New-Item -ItemType Directory -Force -Path $guiRoot | Out-Null
    }

    It 'opens and renders a native window, then visibly reports the send validation state' {
        $start = [System.Diagnostics.ProcessStartInfo]::new()
        $start.FileName = $guiBinary
        $start.UseShellExecute = $false
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.ArgumentList.Add('--smoke-ui')
        $start.ArgumentList.Add('--data-dir')
        $start.ArgumentList.Add($guiData)
        $start.ArgumentList.Add('--rnsconfig')
        $start.ArgumentList.Add($guiRns)
        $process = [System.Diagnostics.Process]::new()
        $process.StartInfo = $start
        $stdoutLines = [System.Collections.Generic.List[string]]::new()

        try {
            $process.Start() | Should -BeTrue
            $stderr = $process.StandardError.ReadToEndAsync()
            $stdoutLine = $process.StandardOutput.ReadLineAsync()
            $deadline = [DateTime]::UtcNow.AddSeconds(20)
            $window = $false
            while ([DateTime]::UtcNow -lt $deadline) {
                if ($stdoutLine.IsCompleted) {
                    $line = $stdoutLine.GetAwaiter().GetResult()
                    if ($null -ne $line) {
                        $stdoutLines.Add($line)
                        $stdoutLine = $process.StandardOutput.ReadLineAsync()
                    }
                }
                if ($process.HasExited) { break }
                if ($IsWindows) {
                    $process.Refresh()
                    $window = $process.MainWindowHandle -ne [IntPtr]::Zero
                }
                else {
                    $window = $stdoutLines.Contains('SMOKE_UI_RENDERED_INITIAL=1')
                }
                if ($window) { break }
                Start-Sleep -Milliseconds 100
            }
            $window | Should -BeTrue -Because 'the native Avalonia window must be created and render its first frame.'
            $process.HasExited | Should -BeFalse

            $exited = $process.WaitForExit(20000)
            if (-not $exited) {
                $process.Kill($true)
                $process.WaitForExit()
                $output = ($stdoutLines -join "`n") + "`n" + $stderr.GetAwaiter().GetResult()
                throw "GUI did not render the native send-status flow. Output: $output"
            }
            $exited | Should -BeTrue -Because 'the native window is closed only after the Avalonia send action updates and renders the validation status.'
            $process.WaitForExit()
            while (-not $stdoutLine.IsCompleted) { Start-Sleep -Milliseconds 10 }
            while ($null -ne ($line = $stdoutLine.GetAwaiter().GetResult())) {
                $stdoutLines.Add($line)
                $stdoutLine = $process.StandardOutput.ReadLineAsync()
                while (-not $stdoutLine.IsCompleted) { Start-Sleep -Milliseconds 10 }
            }
            $output = ($stdoutLines -join "`n") + "`n" + $stderr.GetAwaiter().GetResult()
            $output | Should -Match 'SMOKE_UI_OPENED=Sideband'
            $output | Should -Match 'SMOKE_UI_RENDERED_INITIAL=1'
            $output | Should -Match 'SMOKE_UI_STATUS_CHANGED=Select a contact before sending\.'
            $output | Should -Match 'SMOKE_UI_RENDERED_STATUS=Select a contact before sending\.'
            $process.ExitCode | Should -Be 0
        }
        finally {
            if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
            $process.Dispose()
        }
    }

    AfterAll {
        if ($guiRoot -and (Test-Path -LiteralPath $guiRoot)) { Remove-Item -LiteralPath $guiRoot -Recurse -Force }
    }
}
