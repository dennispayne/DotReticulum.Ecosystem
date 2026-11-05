Describe 'Sideband bounded explicit MQTT telemetry forwarding' {
    It 'T669 independently regenerates actual primary sensor topics and bundled-paho scalar bytes' -Tag 'Reference' {
        if (-not $env:DOTRETICULUM_PYTHON -or -not $env:DOTRETICULUM_SIDEBAND_UPSTREAM) {
            Set-ItResult -Skipped -Because 'Select isolated pinned RNS/LXMF Python and actual Sideband with its external geoid grid.'
            return
        }
        $repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $output = Join-Path $TestDrive 'sideband-mqtt.json'
        & $env:DOTRETICULUM_PYTHON (Join-Path $repository 'tests\Sideband.Tests\GenerateMqttVectors.py') `
            --source $env:DOTRETICULUM_SIDEBAND_UPSTREAM --output $output
        $LASTEXITCODE | Should -Be 0
        (Get-FileHash $output).Hash | Should -Be (Get-FileHash (
            Join-Path $repository 'tests\Sideband.Tests\Fixtures\sideband-mqtt.json')).Hash
    }

    It 'T670 selected native settings windows mask credentials and save enabled and disabled MQTT across restart while offline' -Tag 'Native', 'GUI' {
        if ($env:DOTRETICULUM_SIDEBAND_MQTT -ne '1') {
            Set-ItResult -Skipped -Because 'Select frozen Sideband binary/hash, fresh synthetic output and desktop; no broker or audio device is opened.'
            return
        }
        $binary = $env:DOTRETICULUM_SIDEBAND_BINARY
        $hash = $env:DOTRETICULUM_SIDEBAND_MQTT_SHA256
        $root = Join-Path $env:DOTRETICULUM_SIDEBAND_MQTT_RESULTS 'windows'
        if (-not $binary -or -not $hash) { throw 'Select explicit Sideband binary and MQTT hash.' }
        (Get-FileHash $binary).Hash.ToLowerInvariant() | Should -Be $hash
        if (Test-Path $root) { throw "Select fresh synthetic window results: $root" }
        $null = New-Item -ItemType Directory -Path $root -Force
        foreach ($enabled in $false, $true) {
            $data = Join-Path $root "profile-$enabled"
            $config = Join-Path $root "rns-$enabled"
            $null = New-Item -ItemType Directory -Path $config -Force
            "[reticulum]`n share_instance = no`n[interfaces]`n" |
                Set-Content (Join-Path $config 'config') -Encoding utf8NoBOM
            $output = & $binary --init --data-dir $data --rnsconfig $config 2>&1 | Out-String
            $LASTEXITCODE | Should -Be 0 -Because $output
            $identity = (Get-Content (Join-Path $data 'state.json') -Raw | ConvertFrom-Json).ownDeliveryHash
            $settings = Join-Path $root "settings-$enabled.json"
            @{telemetry_enabled=$true;telemetry_s_information=$true;telemetry_s_information_text='synthetic-window-report'
                telemetry_to_mqtt=$enabled;telemetry_mqtt_host='example.invalid';telemetry_mqtt_port='1884'
                telemetry_mqtt_user='synthetic-private-user';telemetry_mqtt_pass='synthetic-private-password'} |
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
                        throw 'Synthetic MQTT settings window exceeded its deadline.'
                    }
                    $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
                    $output | Set-Content (Join-Path $root "window-$enabled-$restart.log") -Encoding utf8NoBOM
                    $process.ExitCode | Should -Be 0 -Because $output
                    $output | Should -Match 'SMOKE_UI_RENDERED_INITIAL=1'
                    $output | Should -Match 'SMOKE_UI_MQTT=masked-persisted-controls;plain-tcp-warning;connected:0'
                    $output | Should -Not -Match 'synthetic-private'
                    $state = Get-Content (Join-Path $data 'state.json') -Raw | ConvertFrom-Json
                    $state.ownDeliveryHash | Should -Be $identity
                    $state.telemetry.mqtt.enabled | Should -Be $enabled
                    $state.telemetry.mqtt.port | Should -Be 1884
                    $state.telemetry.mqtt.username | Should -Be 'synthetic-private-user'
                    $state.telemetry.mqtt.password | Should -Be 'synthetic-private-password'
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
