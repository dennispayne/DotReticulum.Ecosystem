Describe 'Explicit Linux battery source and desktop settings' {
    It 'T780 independently regenerates actual primary battery with disclosed synthetic sysfs boundaries' -Tag 'Reference' {
        if (-not $env:DOTRETICULUM_PYTHON -or -not $env:DOTRETICULUM_SIDEBAND_UPSTREAM) {
            Set-ItResult -Skipped -Because 'Select pinned reference Python and actual Sideband; no physical battery is used.'
            return
        }
        $repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $output = Join-Path $TestDrive 'sideband-battery.json'
        & $env:DOTRETICULUM_PYTHON (Join-Path $repository 'tests\Sideband.Tests\GenerateBatteryVectors.py') `
            --source $env:DOTRETICULUM_SIDEBAND_UPSTREAM --output $output
        $LASTEXITCODE | Should -Be 0
        (Get-FileHash $output).Hash | Should -Be (Get-FileHash (
            Join-Path $repository 'tests\Sideband.Tests\Fixtures\sideband-battery.json')).Hash
    }

    It 'T781 selected Windows native battery controls survive restart and expose unsupported platform without hardware reads' -Tag 'Native', 'GUI' {
        if ($env:DOTRETICULUM_SIDEBAND_BATTERY -ne '1') {
            Set-ItResult -Skipped -Because 'Select exact Windows native binary/hash, fresh synthetic output and desktop.'
            return
        }
        if (-not $IsWindows) { throw 'This gate verifies Windows unavailability, not Linux physical battery access.' }
        $binary = $env:DOTRETICULUM_SIDEBAND_BINARY
        $hash = $env:DOTRETICULUM_SIDEBAND_BATTERY_SHA256
        $root = $env:DOTRETICULUM_SIDEBAND_BATTERY_RESULTS
        if (-not $binary -or -not $hash -or -not $root) { throw 'Select explicit binary/hash and synthetic results.' }
        if (Test-Path $root) { throw 'Use fresh native results.' }
        $null = New-Item -ItemType Directory -Path $root
        (Get-FileHash $binary).Hash.ToLowerInvariant() | Should -Be $hash
        foreach ($enabled in $false, $true) {
            $data = Join-Path $root "profile-$enabled"
            $config = Join-Path $root "rns-$enabled"
            $null = New-Item -ItemType Directory -Path $config
            "[reticulum]`n share_instance = no`n[interfaces]`n" |
                Set-Content (Join-Path $config 'config') -Encoding utf8NoBOM
            $output = & $binary --init --data-dir $data --rnsconfig $config 2>&1 | Out-String
            $LASTEXITCODE | Should -Be 0 -Because $output
            $identity = (Get-Content (Join-Path $data 'state.json') -Raw | ConvertFrom-Json).ownDeliveryHash
            $settings = Join-Path $root "settings-$enabled.json"
            @{telemetry_enabled=$true;telemetry_s_battery=$enabled;telemetry_s_information=$true
                telemetry_s_information_text='synthetic battery acceptance'} |
                ConvertTo-Json | Set-Content $settings -Encoding utf8NoBOM
            $output = & $binary --import-settings $settings --data-dir $data 2>&1 | Out-String
            $LASTEXITCODE | Should -Be 0 -Because $output
            foreach ($restart in 1, 2) {
                $process = [Diagnostics.Process]::new()
                $started = $false
                try {
                    $process.StartInfo.FileName = $binary
                    $process.StartInfo.UseShellExecute = $false
                    $process.StartInfo.RedirectStandardOutput = $true
                    $process.StartInfo.RedirectStandardError = $true
                    foreach ($argument in '--smoke-ui', '--smoke-telemetry', '--data-dir', $data) {
                        $process.StartInfo.ArgumentList.Add($argument)
                    }
                    $process.Start() | Should -BeTrue
                    $started = $true
                    $stdout = $process.StandardOutput.ReadToEndAsync()
                    $stderr = $process.StandardError.ReadToEndAsync()
                    if (-not $process.WaitForExit(30000)) {
                        $process.Kill($true); $process.WaitForExit()
                        throw 'Synthetic battery settings window exceeded its deadline.'
                    }
                    $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
                    $output | Set-Content (Join-Path $root "window-$enabled-$restart.log") -Encoding utf8NoBOM
                    $process.ExitCode | Should -Be 0 -Because $output
                    $output | Should -Match 'SMOKE_UI_RENDERED_INITIAL=1'
                    if ($enabled) {
                        $output | Should -Match 'SMOKE_UI_BATTERY=explicit-opt-in;persisted-controls;connected:0'
                        $output | Should -Match 'SMOKE_UI_BATTERY_STATUS=Unavailable'
                        $output | Should -Match 'SMOKE_UI_RENDERED_TELEMETRY=battery-preview'
                    }
                    else { $output | Should -Not -Match 'SMOKE_UI_BATTERY_STATUS=' }
                    $state = Get-Content (Join-Path $data 'state.json') -Raw | ConvertFrom-Json
                    $state.ownDeliveryHash | Should -Be $identity
                    $state.telemetry.batteryEnabled | Should -Be $enabled
                    $state.telemetry.enabled | Should -BeTrue
                }
                finally {
                    if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
                    $process.Dispose()
                }
            }
        }
        (Get-FileHash $binary).Hash.ToLowerInvariant() | Should -Be $hash
    }
}
