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
            lxmf_try_propagation_on_fail = $false
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
        $settings.lxmf_try_propagation_on_fail | Should -BeFalse
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

    It 'rejects remote acceptance modes without an explicit valid peer and isolated profile configuration' {
        $rejectedData = Join-Path $root 'rejected-remote'
        $output = & $binary --smoke-ui --smoke-remote bad --data-dir $rejectedData --rnsconfig $rns 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 1
        $output | Should -Match '32-character hexadecimal'
        $output = & $binary --smoke-ui --smoke-remote '00112233445566778899aabbccddeeff' --data-dir $rejectedData 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 1
        $output | Should -Match 'isolated --rnsconfig'
        $output = & $binary --smoke-ui --smoke-map --smoke-remote '00112233445566778899aabbccddeeff' --data-dir $rejectedData --rnsconfig $rns 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 1
        $output = & $binary --smoke-objects --data-dir $rejectedData 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 1
        $output = & $binary --smoke-ui --smoke-objects --smoke-telemetry --data-dir $rejectedData 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 1
        $output = & $binary --smoke-ui --smoke-discovery --data-dir $rejectedData 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 1
        $output | Should -Match 'isolated --rnsconfig'
        $output = & $binary --smoke-ui --smoke-discovery --smoke-map --data-dir $rejectedData --rnsconfig $rns 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 1
        $output = & $binary --smoke-announces --data-dir $rejectedData 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 1
        $output = & $binary --smoke-ui --smoke-announces --smoke-objects --data-dir $rejectedData 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 1
        $output = & $binary --smoke-ui --smoke-announces --data-dir $rejectedData --rnsconfig $rns 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 1
        Test-Path -LiteralPath $rejectedData | Should -BeFalse
    }

    AfterAll {
        if ($root -and (Test-Path -LiteralPath $root)) { Remove-Item -LiteralPath $root -Recurse -Force }
    }
}

Describe 'sideband native raster map acceptance' -Tag 'Native' {
    BeforeAll {
        $mapBinary = $env:DOTRETICULUM_SIDEBAND_BINARY
        if ([string]::IsNullOrWhiteSpace($mapBinary) -or -not (Test-Path -LiteralPath $mapBinary -PathType Leaf)) {
            throw 'Set DOTRETICULUM_SIDEBAND_BINARY to the published sideband executable.'
        }
        $mapRoot = Join-Path (Join-Path (Get-Location) 'artifacts\tests') "sideband-map-pester-$PID"
        $mapFixture = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Sideband.Tests\Fixtures\sideband-map.mbtiles'))
        New-Item -ItemType Directory -Force -Path $mapRoot | Out-Null
    }

    It 'imports validated raster snapshots and persists an offline view across native process restarts' {
        $mapData = Join-Path $mapRoot 'data'
        $original = (Get-FileHash -LiteralPath $mapFixture).Hash
        $output = & $mapBinary --import-map $mapFixture --data-dir $mapData 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 0
        $output | Should -Match 'Imported raster MBTiles, stored zooms 0-1'
        $output | Should -Match 'without opening Reticulum, audio devices or online tiles'
        (Get-FileHash -LiteralPath (Join-Path $mapData 'maps\offline.mbtiles')).Hash | Should -Be $original
        $view = Get-Content -LiteralPath (Join-Path $mapData 'maps\view.json') -Raw | ConvertFrom-Json
        $view.useOffline | Should -BeTrue
        $view.zoom | Should -Be 1
        Test-Path -LiteralPath (Join-Path $mapData 'state.json') | Should -BeFalse
        & $mapBinary --import-map $mapFixture --data-dir $mapData 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 0
        (Get-FileHash -LiteralPath $mapFixture).Hash | Should -Be $original
        $invalid = Join-Path $mapRoot 'invalid.mbtiles'
        [IO.File]::WriteAllBytes($invalid, [byte[]]::new(4096))
        & $mapBinary --import-map $invalid --data-dir $mapData 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 1
        (Get-FileHash -LiteralPath (Join-Path $mapData 'maps\offline.mbtiles')).Hash | Should -Be $original
    }

    It 'rejects conflicting map modes and a live WAL without silently importing stale tiles' {
        $mapData = Join-Path $mapRoot 'rejected-data'
        & $mapBinary --import-map $mapFixture --data-dir $mapData --check 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 1
        Test-Path -LiteralPath $mapData | Should -BeFalse
        & $mapBinary --smoke-map --data-dir $mapData 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 1
        Test-Path -LiteralPath $mapData | Should -BeFalse
        $liveSource = Join-Path $mapRoot 'live.mbtiles'
        Copy-Item -LiteralPath $mapFixture -Destination $liveSource
        [IO.File]::WriteAllBytes("$liveSource-wal", [byte[]]@(1))
        $output = & $mapBinary --import-map $liveSource --data-dir $mapData 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 1
        $output | Should -Match 'Checkpoint and close'
        Test-Path -LiteralPath (Join-Path $mapData 'maps\offline.mbtiles') | Should -BeFalse
    }

    AfterAll {
        if ($mapRoot -and (Test-Path -LiteralPath $mapRoot)) { Remove-Item -LiteralPath $mapRoot -Recurse -Force }
    }
}

Describe 'sideband native primary map profile activation' -Tag 'Native' {
    BeforeAll {
        $mapProfileRoot = $null
        $mapProfileBinary = $env:DOTRETICULUM_SIDEBAND_BINARY
        if ([string]::IsNullOrWhiteSpace($mapProfileBinary) -or -not (Test-Path -LiteralPath $mapProfileBinary -PathType Leaf)) {
            throw 'Set DOTRETICULUM_SIDEBAND_BINARY to the published sideband executable.'
        }
    }

    It 'activates actual primary persisted map settings without following external paths or importing online consent' {
        $referenceMapProfile = $env:DOTRETICULUM_SIDEBAND_MAP_PROFILE
        if ([string]::IsNullOrWhiteSpace($referenceMapProfile)) {
            Set-ItResult -Skipped -Because 'Requires an actual primary profile from GenerateSidebandProfile.py --map-profile.'
            return
        }
        $observations = Get-Content -LiteralPath (Join-Path $referenceMapProfile 'observations.json') -Raw | ConvertFrom-Json
        $observations.upstream | Should -Be 'd069122af14880e43607aed0b71add4c7806fbe0'
        $observations.mapProfile.map_layer | Should -Be 'atlas'
        $referenceStorage = Join-Path $referenceMapProfile 'app_storage'
        $original = (Get-FileHash -LiteralPath (Join-Path $referenceStorage 'sideband_config')).Hash
        $mapProfileRoot = Join-Path (Join-Path (Get-Location) 'artifacts\tests') "sideband-map-profile-$PID-$([guid]::NewGuid().ToString('N'))"
        $imported = & $mapProfileBinary --import-profile $referenceMapProfile --data-dir $mapProfileRoot 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 0
        $imported | Should -Match 'Online consent is not imported'
        $imported | Should -Match 'External/offline map paths are never followed'
        $viewPath = Join-Path $mapProfileRoot 'maps\view.json'
        $view = Get-Content -LiteralPath $viewPath -Raw | ConvertFrom-Json
        $view.latitude | Should -Be $observations.mapProfile.map_lat
        $view.longitude | Should -Be $observations.mapProfile.map_lon
        $view.zoom | Should -Be $observations.mapProfile.map_zoom
        $view.minimumZoom | Should -Be 0
        $view.maximumZoom | Should -Be 19
        $view.useOffline | Should -BeFalse
        $view.provider.urlTemplate | Should -Match '^https://server.arcgisonline.com/'
        Test-Path -LiteralPath (Join-Path $mapProfileRoot 'maps\offline.mbtiles') | Should -BeFalse
        $before = (Get-FileHash -LiteralPath $viewPath).Hash
        & $mapProfileBinary --check --data-dir $mapProfileRoot 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 0
        (Get-FileHash -LiteralPath $viewPath).Hash | Should -Be $before
        (Get-FileHash -LiteralPath (Join-Path $referenceStorage 'sideband_config')).Hash | Should -Be $original
    }

    AfterAll {
        if ($mapProfileRoot -and (Test-Path -LiteralPath $mapProfileRoot)) {
            Remove-Item -LiteralPath $mapProfileRoot -Recurse -Force
        }
    }
}

Describe 'sideband native raster map rendering' -Tag 'GUI' {
    It 'renders imported raster tiles in the original published viewport with online access disabled' {
        $mapBinary = $env:DOTRETICULUM_SIDEBAND_BINARY
        if ([string]::IsNullOrWhiteSpace($mapBinary) -or -not (Test-Path -LiteralPath $mapBinary -PathType Leaf)) {
            throw 'Set DOTRETICULUM_SIDEBAND_BINARY to the published sideband executable.'
        }
        $mapRoot = Join-Path (Join-Path (Get-Location) 'artifacts\tests') "sideband-map-gui-$PID"
        $fixture = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Sideband.Tests\Fixtures\sideband-map.mbtiles'))
        $process = [Diagnostics.Process]::new()
        $started = $false
        try {
            & $mapBinary --import-map $fixture --data-dir $mapRoot 2>&1 | Out-Null
            $LASTEXITCODE | Should -Be 0
            $process.StartInfo.FileName = $mapBinary
            $process.StartInfo.UseShellExecute = $false
            $process.StartInfo.RedirectStandardOutput = $true
            $process.StartInfo.RedirectStandardError = $true
            foreach ($argument in @('--smoke-ui', '--smoke-map', '--data-dir', $mapRoot)) {
                $process.StartInfo.ArgumentList.Add($argument)
            }
            $process.Start() | Should -BeTrue
            $started = $true
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(20000)) {
                $process.Kill($true)
                $process.WaitForExit()
                throw "The published map did not finish its raster-render flow: $($stdout.GetAwaiter().GetResult()) $($stderr.GetAwaiter().GetResult())"
            }
            $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
            $process.ExitCode | Should -Be 0 -Because $output
            $output | Should -Match 'SMOKE_UI_RENDERED_INITIAL=1'
            $output | Should -Match 'SMOKE_UI_RENDERED_MAP=[1-9][0-9]*'
            $output | Should -Match 'SMOKE_UI_RENDERED_STATUS='
            $view = Get-Content -LiteralPath (Join-Path $mapRoot 'maps\view.json') -Raw | ConvertFrom-Json
            $view.useOffline | Should -BeTrue
            $view.PSObject.Properties.Name | Should -Not -Contain 'allowOnline'
        }
        finally {
            if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
            $process.Dispose()
            if (Test-Path -LiteralPath $mapRoot) { Remove-Item -LiteralPath $mapRoot -Recurse -Force }
        }
    }
}

Describe 'sideband native object and overlay rendering' -Tag 'GUI' {
    It 'renders persisted objects and clustered namespaced reference sensor shapes offline across native restarts' {
        $profile = $env:DOTRETICULUM_SIDEBAND_MAP_OBJECT_PROFILE
        if ([string]::IsNullOrWhiteSpace($profile)) {
            Set-ItResult -Skipped -Because 'Set DOTRETICULUM_SIDEBAND_MAP_OBJECT_PROFILE to the synthetic profile exported by SidebandMapOverlayUiTests.'
            return
        }
        $binary = $env:DOTRETICULUM_SIDEBAND_BINARY
        if ([string]::IsNullOrWhiteSpace($binary) -or -not (Test-Path -LiteralPath $binary -PathType Leaf)) {
            throw 'Set DOTRETICULUM_SIDEBAND_BINARY to the published sideband executable.'
        }
        if (-not (Test-Path -LiteralPath (Join-Path $profile 'telemetry\telemetry.sbdb') -PathType Leaf)) {
            throw 'The explicit synthetic map/object profile has no real native telemetry store.'
        }
        $root = Join-Path (Join-Path (Get-Location) 'artifacts\tests') "sideband-objects-map-gui-$PID"
        try {
            Copy-Item -LiteralPath $profile -Destination $root -Recurse -ErrorAction Stop
            foreach ($mode in @('objects', 'objects', 'map', 'empty-objects')) {
                if ($mode -eq 'map') {
                    $fixture = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Sideband.Tests\Fixtures\sideband-map.mbtiles'))
                    & $binary --import-map $fixture --data-dir $root 2>&1 | Out-Null
                    $LASTEXITCODE | Should -Be 0
                    $path = Join-Path $root 'maps\view.json'
                    $view = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
                    $view.latitude = 0
                    $view.longitude = 0
                    $view.zoom = 1
                    $view.includeInterfaces = $true
                    $view.includeConnections = $true
                    $view.clusterMarkers = $true
                    $view.historyLimitSeconds = 604800
                    $view | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $path
                }
                $process = [Diagnostics.Process]::new()
                $started = $false
                try {
                    $process.StartInfo.FileName = $binary
                    $process.StartInfo.UseShellExecute = $false
                    $process.StartInfo.RedirectStandardOutput = $true
                    $process.StartInfo.RedirectStandardError = $true
                    $renderRoot = if ($mode -eq 'empty-objects') { Join-Path $root 'empty-profile' } else { $root }
                    New-Item -ItemType Directory -Path $renderRoot -Force | Out-Null
                    foreach ($argument in @('--smoke-ui', '--data-dir', $renderRoot)) { $process.StartInfo.ArgumentList.Add($argument) }
                    if ($mode -eq 'map') { $process.StartInfo.ArgumentList.Add('--smoke-map') }
                    else { $process.StartInfo.ArgumentList.Add('--smoke-objects') }
                    $process.Start() | Should -BeTrue
                    $started = $true
                    $stdout = $process.StandardOutput.ReadToEndAsync()
                    $stderr = $process.StandardError.ReadToEndAsync()
                    if (-not $process.WaitForExit(20000)) {
                        $process.Kill($true)
                        $process.WaitForExit()
                        throw "The published $mode rendering did not finish: $($stdout.GetAwaiter().GetResult()) $($stderr.GetAwaiter().GetResult())"
                    }
                    $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
                    $process.ExitCode | Should -Be $(if ($mode -eq 'empty-objects') { 2 } else { 0 }) -Because $output
                    $output | Should -Match 'SMOKE_UI_RENDERED_INITIAL=1'
                    if ($mode -eq 'objects') { $output | Should -Match 'SMOKE_UI_RENDERED_OBJECTS=1' }
                    elseif ($mode -eq 'empty-objects') { $output | Should -Match 'Objects smoke requires a saved object' }
                    else {
                        $output | Should -Match 'SMOKE_UI_MAP_MARKERS=7'
                        $output | Should -Match 'SMOKE_UI_RENDERED_MAP_CLUSTERS=1'
                        $output | Should -Match 'SMOKE_UI_RENDERED_MAP=[1-9][0-9]*'
                    }
                }
                finally {
                    if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
                    $process.Dispose()
                }
            }
            $state = Get-Content -LiteralPath (Join-Path $root 'state.json') -Raw | ConvertFrom-Json
            @($state.contacts | Where-Object isObject).Count | Should -Be 1
            @($state.mapFixedLocations).Count | Should -Be 1
            $state.ownDeliveryHash | Should -BeNullOrEmpty
        }
        finally {
            if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
        }
    }
}

Describe 'sideband native runtime telemetry' -Tag @('GUI', 'RuntimeTelemetry') {
    It 'collects explicitly selected actual runtime statistics and renders them across native process restart without physical sensors' {
        $binary = $env:DOTRETICULUM_SIDEBAND_BINARY
        if ([string]::IsNullOrWhiteSpace($binary) -or -not (Test-Path -LiteralPath $binary -PathType Leaf)) {
            Set-ItResult -Skipped -Because 'Set DOTRETICULUM_SIDEBAND_BINARY to the published sideband executable.'
            return
        }
        $root = Join-Path (Join-Path (Get-Location) 'artifacts\tests') "sideband-runtime-native-$([Guid]::NewGuid().ToString('N'))"
        $data = Join-Path $root 'data'
        $rns = Join-Path $root 'rns'
        try {
            $null = New-Item -ItemType Directory -Force -Path $rns
            "[reticulum]`nenable_transport=yes`nshare_instance=no`n[logging]`nloglevel=0`n" |
                Set-Content -LiteralPath (Join-Path $rns 'config')
            $settings = Join-Path $root 'settings.json'
            '{"telemetry_enabled":true,"telemetry_s_rns_transport":true}' | Set-Content -LiteralPath $settings
            & $binary --help 2>&1 | Out-Null
            $LASTEXITCODE | Should -Be 0
            & $binary --import-settings $settings --data-dir $data 2>&1 | Out-Null
            $LASTEXITCODE | Should -Be 0
            $own = $null
            foreach ($iteration in 0..2) {
                $process = [Diagnostics.Process]::new()
                $started = $false
                try {
                    $process.StartInfo.FileName = $binary
                    $process.StartInfo.UseShellExecute = $false
                    $process.StartInfo.RedirectStandardOutput = $true
                    $process.StartInfo.RedirectStandardError = $true
                    $arguments = @('--smoke-ui', '--smoke-telemetry', '--data-dir', $data)
                    if ($iteration -gt 0) { $arguments += @('--rnsconfig', $rns) }
                    foreach ($argument in $arguments) {
                        $process.StartInfo.ArgumentList.Add($argument)
                    }
                    $process.Start() | Should -BeTrue
                    $started = $true
                    $stdout = $process.StandardOutput.ReadToEndAsync()
                    $stderr = $process.StandardError.ReadToEndAsync()
                    if (-not $process.WaitForExit(30000)) {
                        $process.Kill($true)
                        $process.WaitForExit()
                        throw "Runtime window did not finish: $($stdout.GetAwaiter().GetResult()) $($stderr.GetAwaiter().GetResult())"
                    }
                    $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
                    $process.ExitCode | Should -Be 0 -Because $output
                    $output | Should -Match 'SMOKE_UI_PREPARED_TELEMETRY_SENSORS=.*19'
                    $output | Should -Match 'SMOKE_UI_RUNTIME_TELEMETRY_TABLES=0'
                    $state = Get-Content -LiteralPath (Join-Path $data 'state.json') -Raw | ConvertFrom-Json
                    $state.telemetry.rnsTransportEnabled | Should -BeTrue
                    $state.telemetry.rnsTransportIncludeTables | Should -BeFalse
                    if ($iteration -eq 0) {
                        $output | Should -Match 'SMOKE_UI_RUNTIME_TELEMETRY_STATUS=Unavailable'
                        $output | Should -Match 'SMOKE_UI_RENDERED_TELEMETRY=runtime-unavailable'
                        $state.ownDeliveryHash | Should -BeNullOrEmpty
                        Test-Path -LiteralPath (Join-Path $data 'identity') | Should -BeFalse
                        Test-Path -LiteralPath (Join-Path $rns 'storage') | Should -BeFalse
                        continue
                    }
                    $output | Should -Match 'SMOKE_UI_RUNTIME_TELEMETRY_STATUS=(Available|Partial)'
                    $output | Should -Match 'SMOKE_UI_RENDERED_TELEMETRY=runtime-connected'
                    $state.ownDeliveryHash | Should -Match '^[a-f0-9]{32}$'
                    if ($iteration -eq 1) { $own = $state.ownDeliveryHash }
                    else { $state.ownDeliveryHash | Should -BeExactly $own }
                    @($state.messages).Count | Should -Be 0
                    Test-Path -LiteralPath (Join-Path $data 'telemetry\telemetry.sbdb') -PathType Leaf | Should -BeTrue
                }
                finally {
                    if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
                    $process.Dispose()
                }
            }
            $export = Join-Path $root 'export.json'
            & $binary --export-settings $export --data-dir $data 2>&1 | Out-Null
            $LASTEXITCODE | Should -Be 0
            (Get-Content -LiteralPath $export -Raw | ConvertFrom-Json).telemetry_s_rns_transport | Should -BeTrue
        }
        finally {
            if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
        }
    }
}

Describe 'sideband native manual telemetry preview' -Tag 'GUI' {
    It 'renders an explicitly configured local report without opening the network or audio devices' {
        $telemetryBinary = $env:DOTRETICULUM_SIDEBAND_BINARY
        if ([string]::IsNullOrWhiteSpace($telemetryBinary) -or
            -not (Test-Path -LiteralPath $telemetryBinary -PathType Leaf)) {
            throw 'Set DOTRETICULUM_SIDEBAND_BINARY to the published sideband executable.'
        }
        $telemetryRoot = Join-Path (Join-Path (Get-Location) 'artifacts\tests') "sideband-telemetry-gui-$PID"
        $telemetryData = Join-Path $telemetryRoot 'data'
        $process = [Diagnostics.Process]::new()
        $started = $false
        try {
            New-Item -ItemType Directory -Force -Path $telemetryRoot | Out-Null
            $settingsPath = Join-Path $telemetryRoot 'settings.json'
            @{
                telemetry_enabled = $true
                telemetry_s_information = $true
                telemetry_s_information_text = 'Synthetic manual preview, not a sensor measurement'
                telemetry_s_fixed_location = $true
                telemetry_s_fixed_latlon = @(51.5, -0.125)
                telemetry_s_fixed_altitude = 120
            } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $settingsPath
            & $telemetryBinary --import-settings $settingsPath --data-dir $telemetryData 2>&1 | Out-Null
            $LASTEXITCODE | Should -Be 0
            $process.StartInfo.FileName = $telemetryBinary
            $process.StartInfo.UseShellExecute = $false
            $process.StartInfo.RedirectStandardOutput = $true
            $process.StartInfo.RedirectStandardError = $true
            foreach ($argument in @('--smoke-ui', '--smoke-telemetry', '--data-dir', $telemetryData)) {
                $process.StartInfo.ArgumentList.Add($argument)
            }
            $process.Start() | Should -BeTrue
            $started = $true
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(20000)) {
                $process.Kill($true)
                $process.WaitForExit()
                throw "The published telemetry window did not finish its manual preview: $($stdout.GetAwaiter().GetResult()) $($stderr.GetAwaiter().GetResult())"
            }
            $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
            $process.ExitCode | Should -Be 0 -Because $output
            $output | Should -Match 'SMOKE_UI_RENDERED_INITIAL=1'
            $output | Should -Match 'SMOKE_UI_PREPARED_TELEMETRY_SENSORS=.*02'
            $output | Should -Match 'SMOKE_UI_RENDERED_TELEMETRY=manual-preview'
            $output | Should -Not -Match 'Synthetic manual preview'
            $state = Get-Content -LiteralPath (Join-Path $telemetryData 'state.json') -Raw | ConvertFrom-Json
            $state.telemetry.enabled | Should -BeTrue
            $state.ownDeliveryHash | Should -BeNullOrEmpty
            @($state.messages).Count | Should -Be 0
            Test-Path -LiteralPath (Join-Path $telemetryData 'rns') | Should -BeFalse
        }
        finally {
            if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
            $process.Dispose()
            if (Test-Path -LiteralPath $telemetryRoot) { Remove-Item -LiteralPath $telemetryRoot -Recurse -Force }
        }
    }
}

Describe 'sideband native repository window acceptance' -Tag 'GUI' {
    It 'serves synthetic HTTPS index and package routes from the real window and retains its certificate across restart' {
        $repositoryBinary = $env:DOTRETICULUM_SIDEBAND_BINARY
        if ([string]::IsNullOrWhiteSpace($repositoryBinary) -or
            -not (Test-Path -LiteralPath $repositoryBinary -PathType Leaf)) {
            throw 'Set DOTRETICULUM_SIDEBAND_BINARY to the published sideband executable.'
        }
        $repositoryRoot = Join-Path (Join-Path (Get-Location) 'artifacts\tests') "sideband-repository-gui-$PID"
        $public = Join-Path $repositoryRoot 'public'
        $data = Join-Path $repositoryRoot 'private'
        $process = $null
        $started = $false
        try {
            New-Item -ItemType Directory -Force -Path (Join-Path $public 'pkg') | Out-Null
            'Original synthetic native HTTPS acceptance page' | Set-Content -LiteralPath (Join-Path $public 'index.html')
            [IO.File]::WriteAllBytes((Join-Path $public 'pkg\synthetic.bin'), [byte[]]@(1, 2, 3, 4))
            $invalidData = Join-Path $repositoryRoot 'invalid'
            & $repositoryBinary --smoke-repository $public --data-dir $invalidData 2>&1 | Out-Null
            $LASTEXITCODE | Should -Be 1
            Test-Path -LiteralPath $invalidData | Should -BeFalse
            $certificateHash = $null
            foreach ($iteration in 1, 2) {
                $started = $false
                $process = [Diagnostics.Process]::new()
                $process.StartInfo.FileName = $repositoryBinary
                $process.StartInfo.UseShellExecute = $false
                $process.StartInfo.RedirectStandardOutput = $true
                $process.StartInfo.RedirectStandardError = $true
                foreach ($argument in @('--smoke-ui', '--smoke-repository', $public, '--data-dir', $data)) {
                    $process.StartInfo.ArgumentList.Add($argument)
                }
                $process.Start() | Should -BeTrue
                $started = $true
                $stdout = $process.StandardOutput.ReadToEndAsync()
                $stderr = $process.StandardError.ReadToEndAsync()
                if (-not $process.WaitForExit(20000)) {
                    $process.Kill($true)
                    $process.WaitForExit()
                    throw "The published mirror window did not finish its TLS flow: $($stdout.GetAwaiter().GetResult()) $($stderr.GetAwaiter().GetResult())"
                }
                $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
                $process.ExitCode | Should -Be 0 -Because $output
                $output | Should -Match 'SMOKE_UI_RENDERED_INITIAL=1'
                $output | Should -Match 'SMOKE_UI_REPOSITORY_HTTPS=1'
                $output | Should -Match 'SMOKE_UI_RENDERED_REPOSITORY=1'
                $process.Dispose()
                $process = $null
                $certificates = @(Get-ChildItem -LiteralPath (Join-Path $data 'repository-certificate') -Filter '*.pfx' -File)
                $certificates.Count | Should -Be 1
                $currentHash = (Get-FileHash -LiteralPath $certificates[0].FullName).Hash
                if ($iteration -eq 1) { $certificateHash = $currentHash }
                else { $currentHash | Should -Be $certificateHash }
            }
            $state = Get-Content -LiteralPath (Join-Path $data 'state.json') -Raw | ConvertFrom-Json
            $state.ownDeliveryHash | Should -BeNullOrEmpty
            @($state.messages).Count | Should -Be 0
        }
        finally {
            if ($null -ne $process) {
                if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
                $process.Dispose()
            }
            if (Test-Path -LiteralPath $repositoryRoot) { Remove-Item -LiteralPath $repositoryRoot -Recurse -Force }
        }
    }
}

Describe 'sideband native announce stream' -Tag 'GUI', 'Announces' {
    It 'renders actual-reference synthetic history across restarts and fails empty-history acceptance offline' {
        $binary = $env:DOTRETICULUM_SIDEBAND_BINARY
        if ([string]::IsNullOrWhiteSpace($binary) -or -not (Test-Path -LiteralPath $binary -PathType Leaf)) {
            throw 'Set DOTRETICULUM_SIDEBAND_BINARY to the published sideband executable.'
        }
        $root = Join-Path ([IO.Path]::GetTempPath()) "sideband-announces-$([guid]::NewGuid().ToString('N'))"
        $data = Join-Path $root 'data'
        $rns = Join-Path $root 'rns'
        try {
            New-Item -ItemType Directory -Path $rns -Force | Out-Null
            "[reticulum]`n share_instance = no`n enable_transport = no`n[logging]`n loglevel = 0`n[interfaces]`n" |
                Set-Content -LiteralPath (Join-Path $rns 'config')
            & $binary --init --data-dir $data --rnsconfig $rns 2>&1 | Out-Null
            $LASTEXITCODE | Should -Be 0
            $path = Join-Path $data 'state.json'
            $state = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            $fixture = Get-Content (Join-Path $PSScriptRoot '..\Sideband.Tests\Fixtures\sideband-announces.json') -Raw | ConvertFrom-Json
            $fixture.revision | Should -Be 'd069122af14880e43607aed0b71add4c7806fbe0'
            $records = @($fixture.snapshots[1].records | ForEach-Object {
                @{
                    DestinationHash = $_.dest
                    DestinationType = $_.type
                    ReceivedAt = [DateTimeOffset]::FromUnixTimeSeconds([long]$_.time).ToString('O')
                    Name = $_.name
                    StampCost = $_.cost
                    NodeVersion = $_.pn_ver
                    NodeEnabled = if ($_.type -eq 'lxmf.propagation') { $true } else { $null }
                    Signal = if ($null -ne $_.extras.link_stats) {
                        @{ Rssi = $_.extras.link_stats.rssi; Snr = $_.extras.link_stats.snr; Quality = $_.extras.link_stats.q }
                    } else { $null }
                }
            })
            $state | Add-Member -NotePropertyName Announces -NotePropertyValue $records -Force
            $state | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $path
            $before = (Get-FileHash $path).Hash
            foreach ($mode in 'history', 'history', 'empty') {
                if ($mode -eq 'empty') {
                    $state.Announces = @()
                    $state | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $path
                }
                $process = [Diagnostics.Process]::new()
                $started = $false
                try {
                    $process.StartInfo.FileName = $binary
                    $process.StartInfo.UseShellExecute = $false
                    $process.StartInfo.RedirectStandardOutput = $true
                    $process.StartInfo.RedirectStandardError = $true
                    foreach ($argument in '--smoke-ui', '--smoke-announces', '--data-dir', $data) {
                        $process.StartInfo.ArgumentList.Add($argument)
                    }
                    $process.Start() | Should -BeTrue
                    $started = $true
                    $stdout = $process.StandardOutput.ReadToEndAsync()
                    $stderr = $process.StandardError.ReadToEndAsync()
                    if (-not $process.WaitForExit(20000)) {
                        $process.Kill($true)
                        $process.WaitForExit()
                        throw 'Published announce rendering did not finish.'
                    }
                    $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
                    $process.ExitCode | Should -Be $(if ($mode -eq 'empty') { 2 } else { 0 }) -Because $output
                    $output | Should -Match 'SMOKE_UI_RENDERED_INITIAL=1'
                    if ($mode -eq 'history') {
                        $output | Should -Match 'SMOKE_UI_RENDERED_ANNOUNCES=4'
                        (Get-FileHash $path).Hash | Should -Be $before
                    }
                    else { $output | Should -Match 'Announce smoke requires saved announce history' }
                }
                finally {
                    if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
                    $process.Dispose()
                }
            }
        }
        finally { if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force } }
    }
}

Describe 'sideband primary voice history migration' -Tag 'GUI', 'VoiceImport' {
    It 'imports actual primary call assertions and renders them offline across native restart without granting trust or changing the source' {
        $binary = $env:DOTRETICULUM_SIDEBAND_BINARY
        if ([string]::IsNullOrWhiteSpace($binary) -or -not (Test-Path -LiteralPath $binary -PathType Leaf)) {
            throw 'Set DOTRETICULUM_SIDEBAND_BINARY to the published sideband executable.'
        }
        $profile = $env:DOTRETICULUM_SIDEBAND_VOICE_PROFILE
        if ([string]::IsNullOrWhiteSpace($profile)) {
            Set-ItResult -Skipped -Because 'Generate a private actual primary profile with GenerateSidebandProfile.py --voice-profile and set DOTRETICULUM_SIDEBAND_VOICE_PROFILE.'
            return
        }
        $observations = Get-Content -LiteralPath (Join-Path $profile 'observations.json') -Raw | ConvertFrom-Json
        $observations.upstream | Should -Be 'd069122af14880e43607aed0b71add4c7806fbe0'
        $observations.voiceLog.voiceSha256 | Should -Be '51e61510ea5eee832e7b9ad452459998c76561d970e0e773a253fe17cc7f2ab8'
        @($observations.voiceLog.entries).Count | Should -Be 6
        $source = Join-Path $profile 'app_storage'
        (Get-FileHash -LiteralPath (Join-Path $source 'lxst_call_log')).Hash.ToLowerInvariant() |
            Should -Be $observations.voiceLog.sha256
        $before = @(Get-ChildItem -LiteralPath $source -File | Sort-Object Name | ForEach-Object {
            "$($_.Name):$((Get-FileHash -LiteralPath $_.FullName).Hash)"
        }) -join '|'
        $root = Join-Path ([IO.Path]::GetTempPath()) "sideband-voice-import-$([guid]::NewGuid().ToString('N'))"
        $data = Join-Path $root 'native'
        try {
            $output = & $binary --import-profile $profile --data-dir $data 2>&1 | Out-String
            $LASTEXITCODE | Should -Be 0 -Because $output
            $output | Should -Match 'historical assertions'
            $path = Join-Path $data 'state.json'
            $state = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            $state.ownDeliveryHash | Should -Be $observations.ownDeliveryHash
            @($state.calls).Count | Should -Be 6
            @($state.contacts).Count | Should -Be 1
            @($state.voicePeers).Where({ $null -ne $_ }).Count | Should -Be 0
            $state.voiceEnabled | Should -BeFalse
            for ($index = 0; $index -lt 6; $index++) {
                $state.calls[$index].event | Should -Be $observations.voiceLog.entries[$index].event
                $state.calls[$index].identityHash | Should -Be $observations.voiceLog.entries[$index].identity
                $state.calls[$index].imported | Should -BeTrue
                $actualTime = ([DateTimeOffset]$state.calls[$index].time).ToUnixTimeMilliseconds() / 1000.0
                [Math]::Abs($actualTime - $observations.voiceLog.entries[$index].time) | Should -BeLessThan 0.002
            }
            $stateHash = (Get-FileHash -LiteralPath $path).Hash
            foreach ($restart in 1, 2) {
                $process = [Diagnostics.Process]::new()
                $started = $false
                try {
                    $process.StartInfo.FileName = $binary
                    $process.StartInfo.UseShellExecute = $false
                    $process.StartInfo.RedirectStandardOutput = $true
                    $process.StartInfo.RedirectStandardError = $true
                    foreach ($argument in '--smoke-ui', '--smoke-voice-history', '--data-dir', $data) {
                        $process.StartInfo.ArgumentList.Add($argument)
                    }
                    $process.Start() | Should -BeTrue
                    $started = $true
                    $stdout = $process.StandardOutput.ReadToEndAsync()
                    $stderr = $process.StandardError.ReadToEndAsync()
                    if (-not $process.WaitForExit(20000)) {
                        $process.Kill($true)
                        $process.WaitForExit()
                        throw 'Published imported voice-history rendering did not finish.'
                    }
                    $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
                    $process.ExitCode | Should -Be 0 -Because $output
                    $output | Should -Match 'SMOKE_UI_RENDERED_INITIAL=1'
                    $output | Should -Match 'SMOKE_UI_RENDERED_VOICE_HISTORY=6'
                    $output | Should -Match 'SMOKE_UI_IMPORTED_VOICE_ASSERTIONS=6'
                    $output | Should -Match 'SMOKE_UI_VOICE_CONNECTED=0'
                    (Get-FileHash -LiteralPath $path).Hash | Should -Be $stateHash
                }
                finally {
                    if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
                    $process.Dispose()
                }
            }
            & $binary --import-profile $profile --data-dir $data 2>&1 | Out-Null
            $LASTEXITCODE | Should -Be 1
            (Get-FileHash -LiteralPath $path).Hash | Should -Be $stateHash
            $config = Join-Path $root 'isolated-rns'
            $null = New-Item -ItemType Directory -Path $config -Force
            "[reticulum]`n  enable_transport = no`n  share_instance = no`n`n[logging]`n  loglevel = 4`n" |
                Set-Content -LiteralPath (Join-Path $config 'config') -Encoding utf8NoBOM
            $output = & $binary --init --data-dir (Join-Path $root 'empty') --rnsconfig $config 2>&1 | Out-String
            $LASTEXITCODE | Should -Be 0 -Because $output
            $output = & $binary --smoke-ui --smoke-voice-history --data-dir (Join-Path $root 'empty') 2>&1 | Out-String
            $LASTEXITCODE | Should -Be 2 -Because $output
            $output | Should -Match 'Voice history smoke requires saved call history'
            foreach ($arguments in @(
                @('--smoke-voice-history'),
                @('--smoke-ui', '--smoke-voice-history', '--rnsconfig', $config),
                @('--smoke-ui', '--smoke-voice-history', '--smoke-announces')
            )) {
                $output = & $binary @arguments --data-dir (Join-Path $root 'invalid') 2>&1 | Out-String
                $LASTEXITCODE | Should -Be 1 -Because $output
                $output | Should -Match 'requires --smoke-ui'
                Test-Path -LiteralPath (Join-Path $root 'invalid') | Should -BeFalse
            }
            $after = @(Get-ChildItem -LiteralPath $source -File | Sort-Object Name | ForEach-Object {
                "$($_.Name):$((Get-FileHash -LiteralPath $_.FullName).Hash)"
            }) -join '|'
            $after | Should -Be $before
        }
        finally { if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force } }
    }
}

Describe 'sideband primary announce profile migration' -Tag 'GUI', 'AnnounceImport' {
    It 'imports actual primary WAL announcements and renders persisted history after native restart without changing the source' {
        $binary = $env:DOTRETICULUM_SIDEBAND_BINARY
        if ([string]::IsNullOrWhiteSpace($binary) -or -not (Test-Path -LiteralPath $binary -PathType Leaf)) {
            throw 'Set DOTRETICULUM_SIDEBAND_BINARY to the published sideband executable.'
        }
        $profile = $env:DOTRETICULUM_SIDEBAND_ANNOUNCE_PROFILE
        if ([string]::IsNullOrWhiteSpace($profile)) {
            Set-ItResult -Skipped -Because 'Generate a private actual primary profile with GenerateSidebandProfile.py --announce-profile and set DOTRETICULUM_SIDEBAND_ANNOUNCE_PROFILE.'
            return
        }
        $observations = Get-Content -LiteralPath (Join-Path $profile 'observations.json') -Raw | ConvertFrom-Json
        $observations.upstream | Should -Be 'd069122af14880e43607aed0b71add4c7806fbe0'
        @($observations.announces).Count | Should -Be 4
        $source = Join-Path $profile 'app_storage'
        (Get-Item -LiteralPath (Join-Path $source 'sideband.db-wal')).Length | Should -BeGreaterThan 0
        $before = @(Get-ChildItem -LiteralPath $source -File | Sort-Object Name | ForEach-Object {
            "$($_.Name):$((Get-FileHash -LiteralPath $_.FullName).Hash)"
        }) -join '|'
        $root = Join-Path ([IO.Path]::GetTempPath()) "sideband-announce-import-$([guid]::NewGuid().ToString('N'))"
        $data = Join-Path $root 'native'
        try {
            $output = & $binary --import-profile $profile --data-dir $data 2>&1 | Out-String
            $LASTEXITCODE | Should -Be 0 -Because $output
            $output | Should -Match 'Activated 4 newest unique supported announcements'
            $output | Should -Match 'delivery stamp costs are unknown'
            $path = Join-Path $data 'state.json'
            $state = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            $state.ownDeliveryHash | Should -Be $observations.ownDeliveryHash
            @($state.announces).Count | Should -Be 4
            @($state.contacts).Count | Should -Be 1
            $state.contacts[0].trusted | Should -BeTrue
            $state.contacts[0].unread | Should -BeTrue
            @($state.messages).Count | Should -Be 2
            foreach ($observed in $observations.announces) {
                $actual = @($state.announces | Where-Object destinationHash -EQ $observed.dest)
                $actual.Count | Should -Be 1
                $actual[0].destinationType | Should -Be $observed.type
                $actual[0].name | Should -Be $observed.name
                $actual[0].nodeVersion | Should -Be $observed.pn_ver
                if ($observed.type -eq 'lxmf.delivery') { $actual[0].stampCost | Should -BeNullOrEmpty }
                else { $actual[0].stampCost | Should -Be $observed.cost }
            }
            $stateHash = (Get-FileHash -LiteralPath $path).Hash
            & $binary --import-profile $profile --data-dir $data 2>&1 | Out-Null
            $LASTEXITCODE | Should -Be 1
            (Get-FileHash -LiteralPath $path).Hash | Should -Be $stateHash
            foreach ($restart in 1, 2) {
                $process = [Diagnostics.Process]::new()
                $started = $false
                try {
                    $process.StartInfo.FileName = $binary
                    $process.StartInfo.UseShellExecute = $false
                    $process.StartInfo.RedirectStandardOutput = $true
                    $process.StartInfo.RedirectStandardError = $true
                    foreach ($argument in '--smoke-ui', '--smoke-announces', '--data-dir', $data) {
                        $process.StartInfo.ArgumentList.Add($argument)
                    }
                    $process.Start() | Should -BeTrue
                    $started = $true
                    $stdout = $process.StandardOutput.ReadToEndAsync()
                    $stderr = $process.StandardError.ReadToEndAsync()
                    if (-not $process.WaitForExit(20000)) {
                        $process.Kill($true)
                        $process.WaitForExit()
                        throw 'Published imported announcement rendering did not finish.'
                    }
                    $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
                    $process.ExitCode | Should -Be 0 -Because $output
                    $output | Should -Match 'SMOKE_UI_RENDERED_INITIAL=1'
                    $output | Should -Match 'SMOKE_UI_RENDERED_ANNOUNCES=4'
                    (Get-FileHash -LiteralPath $path).Hash | Should -Be $stateHash
                }
                finally {
                    if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
                    $process.Dispose()
                }
            }
            $after = @(Get-ChildItem -LiteralPath $source -File | Sort-Object Name | ForEach-Object {
                "$($_.Name):$((Get-FileHash -LiteralPath $_.FullName).Hash)"
            }) -join '|'
            $after | Should -Be $before
        }
        finally { if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force } }
    }
}

Describe 'sideband native cached discovery map' -Tag 'GUI', 'Discovery' {
    It 'renders selected cached discovery across restart and rejects an empty cache without autoconnect' {
        $profile = $env:DOTRETICULUM_SIDEBAND_DISCOVERY_PROFILE
        if ([string]::IsNullOrWhiteSpace($profile)) {
            Set-ItResult -Skipped -Because 'Set DOTRETICULUM_SIDEBAND_DISCOVERY_PROFILE to the synthetic isolated profile exported by SidebandDiscoveryUiTests.'
            return
        }
        $binary = $env:DOTRETICULUM_SIDEBAND_BINARY
        if ([string]::IsNullOrWhiteSpace($binary) -or -not (Test-Path -LiteralPath $binary -PathType Leaf)) {
            throw 'Set DOTRETICULUM_SIDEBAND_BINARY to the published sideband executable.'
        }
        $root = Join-Path ([IO.Path]::GetTempPath()) "sideband-discovery-$([guid]::NewGuid().ToString('N'))"
        try {
            Copy-Item -LiteralPath $profile -Destination $root -Recurse -ErrorAction Stop
            $data = Join-Path $root 'data'
            $rns = Join-Path $root 'rns'
            $cache = Join-Path $rns 'storage\discovery'
            @(Get-ChildItem -LiteralPath $cache -File).Count | Should -Be 1
            foreach ($mode in @('cached', 'cached', 'empty')) {
                if ($mode -eq 'empty') {
                    foreach ($file in Get-ChildItem -LiteralPath $cache -File) {
                        Remove-Item -LiteralPath $file.FullName -Force
                    }
                }
                $process = [Diagnostics.Process]::new()
                $started = $false
                try {
                    $process.StartInfo.FileName = $binary
                    $process.StartInfo.UseShellExecute = $false
                    $process.StartInfo.RedirectStandardOutput = $true
                    $process.StartInfo.RedirectStandardError = $true
                    foreach ($argument in @('--smoke-ui', '--smoke-discovery', '--data-dir', $data, '--rnsconfig', $rns)) {
                        $process.StartInfo.ArgumentList.Add($argument)
                    }
                    $process.Start() | Should -BeTrue
                    $started = $true
                    $stdout = $process.StandardOutput.ReadToEndAsync()
                    $stderr = $process.StandardError.ReadToEndAsync()
                    if (-not $process.WaitForExit(20000)) {
                        $process.Kill($true)
                        $process.WaitForExit()
                        throw "Published discovery rendering did not finish: $($stdout.GetAwaiter().GetResult()) $($stderr.GetAwaiter().GetResult())"
                    }
                    $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
                    $process.ExitCode | Should -Be $(if ($mode -eq 'empty') { 2 } else { 0 }) -Because $output
                    $output | Should -Match 'SMOKE_UI_RENDERED_INITIAL=1'
                    if ($mode -eq 'cached') { $output | Should -Match 'SMOKE_UI_RENDERED_DISCOVERY=1' }
                    else { $output | Should -Match 'Discovery smoke requires a valid cached transport advertisement' }
                    $output | Should -Not -Match 'synthetic-ui-secret'
                }
                finally {
                    if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
                    $process.Dispose()
                }
            }
        }
        finally {
            if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
        }
    }
}

Describe 'sideband native remote command window' -Tag 'GUI', 'Remote' {
    It 'exchanges real commands, cancels unresolved work and persists request policy across native restart' {
        $binary = $env:DOTRETICULUM_SIDEBAND_BINARY
        if (!$binary -or !(Test-Path -LiteralPath $binary -PathType Leaf)) {
            throw 'Set DOTRETICULUM_SIDEBAND_BINARY to the published sideband executable.'
        }
        $root = Join-Path (Join-Path (Get-Location) 'artifacts\tests') ('sideband-remote-gui-' + [Guid]::NewGuid().ToString('N'))
        $server = $null
        $window = $null
        try {
            $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
            try { $listener.Start(); $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
            finally { $listener.Stop() }
            New-Item -ItemType Directory -Force -Path (Join-Path $root 'server-rns'), (Join-Path $root 'client-rns') | Out-Null
            $common = "[reticulum]`n share_instance = no`n enable_transport = no`n`n[logging]`n loglevel = 0`n`n[interfaces]`n"
            Set-Content -LiteralPath (Join-Path $root 'server-rns\config') -Value ($common +
                " [[Remote]]`n type = TCPServerInterface`n enabled = yes`n listen_ip = 127.0.0.1`n listen_port = $port`n")
            Set-Content -LiteralPath (Join-Path $root 'client-rns\config') -Value ($common +
                " [[Remote]]`n type = TCPClientInterface`n enabled = yes`n target_host = 127.0.0.1`n target_port = $port`n")
            $settings = Join-Path $root 'settings.json'
            '{"telemetry_allow_requests_from_anyone":true}' | Set-Content -LiteralPath $settings
            & $binary --import-settings $settings --data-dir (Join-Path $root 'server') 2>&1 | Out-Null
            $LASTEXITCODE | Should -Be 0
            $start = [Diagnostics.ProcessStartInfo]::new($binary)
            $start.UseShellExecute = $false
            $start.RedirectStandardInput = $true
            $start.RedirectStandardOutput = $true
            $start.RedirectStandardError = $true
            foreach ($argument in @('--daemon', '--interactive', '--data-dir', (Join-Path $root 'server'),
                    '--rnsconfig', (Join-Path $root 'server-rns'))) { $start.ArgumentList.Add($argument) }
            $server = [Diagnostics.Process]::Start($start)
            $serverError = $server.StandardError.ReadToEndAsync()
            $ready = $server.StandardOutput.ReadLineAsync()
            if (!$ready.Wait(10000)) { throw 'The native remote peer did not become ready within ten seconds.' }
            $line = $ready.GetAwaiter().GetResult()
            $line | Should -Match '^READY identity=[0-9a-f]{32} delivery=([0-9a-f]{32}) voice=disabled'
            $peer = [regex]::Match($line, 'delivery=([0-9a-f]{32})').Groups[1].Value
            $serverOutput = $server.StandardOutput.ReadToEndAsync()
            $start = [Diagnostics.ProcessStartInfo]::new($binary)
            $start.UseShellExecute = $false
            $start.RedirectStandardOutput = $true
            $start.RedirectStandardError = $true
            foreach ($argument in @('--smoke-ui', '--smoke-remote', $peer, '--data-dir', (Join-Path $root 'client'),
                    '--rnsconfig', (Join-Path $root 'client-rns'))) { $start.ArgumentList.Add($argument) }
            $window = [Diagnostics.Process]::Start($start)
            $stdout = $window.StandardOutput.ReadToEndAsync()
            $stderr = $window.StandardError.ReadToEndAsync()
            if (!$window.WaitForExit(30000)) {
                $window.Kill($true)
                $window.WaitForExit()
                throw "The remote window did not finish: $($stdout.GetAwaiter().GetResult()) $($stderr.GetAwaiter().GetResult())"
            }
            $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
            $window.ExitCode | Should -Be 0 -Because $output
            $output | Should -Match 'SMOKE_UI_RENDERED_REMOTE=commands-replies-cancellation'
            $state = Get-Content -LiteralPath (Join-Path $root 'client\state.json') -Raw | ConvertFrom-Json
            foreach ($reply in 'Ping reply', 'Echo reply: repeated', 'Echo reply: after cancellation') {
                @($state.messages | Where-Object { $_.incoming -and $_.content -eq $reply }).Count | Should -Be 1
            }
            $state.telemetry.enabled | Should -BeFalse
            $state.voiceEnabled | Should -BeFalse
            $contact = @($state.contacts | Where-Object deliveryHash -EQ $peer)
            $contact.Count | Should -Be 1
            $contact[0].trusted | Should -BeFalse
            $contact[0].allowTelemetryRequests | Should -BeTrue
            $contact[0].sendTelemetry | Should -BeFalse
            $export = Join-Path $root 'export.json'
            & $binary --export-settings $export --data-dir (Join-Path $root 'client') 2>&1 | Out-Null
            $LASTEXITCODE | Should -Be 0
            $policy = Get-Content -LiteralPath $export -Raw | ConvertFrom-Json
            $policy.lxmf_ignore_unknown | Should -BeTrue
            $policy.telemetry_allow_requests_from_anyone | Should -BeFalse
            $policy.telemetry_allow_requests_from_trusted | Should -BeFalse
            $server.StandardInput.WriteLine('quit')
            $server.StandardInput.Flush()
            $server.StandardInput.Close()
            $server.WaitForExit(10000) | Should -BeTrue
            $server.ExitCode | Should -Be 0 -Because ($serverOutput.GetAwaiter().GetResult() + $serverError.GetAwaiter().GetResult())
        }
        finally {
            foreach ($process in @($window, $server)) {
                if ($process) {
                    if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
                    $process.Dispose()
                }
            }
            if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
        }
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

    It 'opens and renders a native window and validation state (media=<Media>)' -TestCases @(
        @{ Media = $false },
        @{ Media = $true }
    ) {
        param($Media)
        $activeData = $guiData
        if ($Media) {
            $reference = $env:DOTRETICULUM_SIDEBAND_PROFILE
            if ([string]::IsNullOrWhiteSpace($reference) -or
                -not (Test-Path -LiteralPath (Join-Path $reference 'observations.json'))) {
                Set-ItResult -Skipped -Because 'Native media rendering requires a generated Sideband media profile.'
                return
            }
            $observations = Get-Content -LiteralPath (Join-Path $reference 'observations.json') -Raw | ConvertFrom-Json
            if ($observations.PSObject.Properties.Name -notcontains 'media' -or
                $observations.media.PSObject.Properties.Name -notcontains 'image') {
                Set-ItResult -Skipped -Because 'The selected upstream profile contains no generated image.'
                return
            }
            $activeData = Join-Path $guiRoot 'media-data'
            & $guiBinary --import-profile $reference --data-dir $activeData 2>&1 | Out-Null
            $LASTEXITCODE | Should -Be 0
        }
        $start = [System.Diagnostics.ProcessStartInfo]::new()
        $start.FileName = $guiBinary
        $start.UseShellExecute = $false
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.ArgumentList.Add('--smoke-ui')
        $start.ArgumentList.Add('--data-dir')
        $start.ArgumentList.Add($activeData)
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
            if ($Media) {
                $output | Should -Match 'SMOKE_UI_RENDERED_MEDIA=1'
                $output | Should -Match 'SMOKE_UI_RENDERED_STATUS='
            }
            else {
                $output | Should -Match 'SMOKE_UI_STATUS_CHANGED=Select a contact before sending\.'
                $output | Should -Match 'SMOKE_UI_RENDERED_STATUS=Select a contact before sending\.'
            }
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
