#Requires -Version 7.0
BeforeAll {
    $script:repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $script:wixProjectPath = Join-Path $script:repository 'packaging\windows\DotReticulum.Ecosystem.wixproj'
    $script:authoringPath = Join-Path $script:repository 'packaging\windows\Product.wxs'
    $script:buildScriptPath = Join-Path $script:repository 'tools\Build-WindowsMsi.ps1'
    $script:wixProject = Get-Content -LiteralPath $script:wixProjectPath -Raw
    $script:authoring = [xml](Get-Content -LiteralPath $script:authoringPath -Raw)
    $script:buildScript = Get-Content -LiteralPath $script:buildScriptPath -Raw
    $script:namespaceManager = [Xml.XmlNamespaceManager]::new($script:authoring.NameTable)
    $script:namespaceManager.AddNamespace('wxs', 'http://wixtoolset.org/schemas/v4/wxs')
}

Describe 'Windows MSI packaging authoring' -Tag 'Installer' {
    It 'keeps packaging separate from the standard solution and uses WiX v4' {
        $solution = Get-Content -LiteralPath (Join-Path $script:repository 'DotReticulum.Ecosystem.slnx') -Raw
        $solution | Should -Not -Match 'DotReticulum\.Ecosystem\.wixproj'
        $script:wixProject | Should -Match 'WixToolset\.Sdk/4\.0\.6'
        $script:wixProject | Should -Match 'WixToolset\.UI\.wixext" Version="4\.0\.6"'
        $script:wixProject | Should -Match 'WixToolset\.Heat" Version="4\.0\.6"'
    }

    It 'defines all five app features as default-installed and references their payload groups' {
        $expectedGroups = @{
            Telemetry = 'TelemetryFiles'
            Lxmd = 'LxmdFiles'
            Rnphone = 'RnphoneFiles'
            NomadNet = 'NomadNetFiles'
            Sideband = 'SidebandFiles'
        }
        $features = @($script:authoring.SelectNodes('/wxs:Wix/wxs:Package/wxs:Feature/wxs:Feature', $script:namespaceManager))
        (@($features | ForEach-Object { $_.GetAttribute('Id') } | Sort-Object) -join ',') |
            Should -Be (($expectedGroups.Keys | Sort-Object) -join ',')

        foreach ($feature in $features) {
            $feature.GetAttribute('Level') | Should -Be '1'
            $group = $feature.SelectSingleNode('wxs:ComponentGroupRef', $script:namespaceManager)
            $group.GetAttribute('Id') | Should -Be $expectedGroups[$feature.GetAttribute('Id')]
        }
    }

    It 'installs per-machine shortcuts without a service or user-configuration action' {
        $package = $script:authoring.SelectSingleNode('/wxs:Wix/wxs:Package', $script:namespaceManager)
        $package.GetAttribute('Scope') | Should -Be 'perMachine'
        $programData = $script:authoring.SelectSingleNode('/wxs:Wix/wxs:Package/wxs:StandardDirectory[@Id="CommonAppDataFolder"]', $script:namespaceManager)
        $programData | Should -Not -BeNullOrEmpty

        $shortcuts = @($script:authoring.SelectNodes('//wxs:Shortcut', $script:namespaceManager))
        (@($shortcuts | ForEach-Object { $_.GetAttribute('Name') } | Sort-Object) -join ',') |
            Should -Be 'lxmd,nomadnet,rnphone,sideband,telemetry'
        @($script:authoring.SelectNodes('//wxs:ServiceInstall|//wxs:ServiceControl', $script:namespaceManager)).Count | Should -Be 0
        $script:buildScript | Should -Not -Match 'New-Service|sc\.exe|ServiceInstall|ServiceControl'
    }

    It 'pins and packages the native codec runtime and Codec2 source materials' {
        foreach ($hash in @(
            '11a85ff886cc34f85cb8c9f2ceaeb7809a9d601ebec068305998a1962bbfec41',
            '80b2497f019977dd9528b1abc8621b1328a0115ab1f724047ccbb56bf323f11d',
            '110a34239e8f122da15e041b2601e2f3c6d4c909d738bdf978700601e0f64449',
            'b4ebe2826e848c5e790e06ea7b37d3e0594e005f42382b0506886f17df8127da',
            'bb114bcf26225f50f874497ebfd6b8224fff2a20bda3f8ee45cd07ce5f02657b',
            '8a9fa96bcba9ae6caf77b9834e7c3a4072078761aeb7aa8121967e3f65faf656',
            'cbccae52b2c2ecc5d2757e407da567eb681241ff8dadce39d779a7219dbcf449',
            '2379f3dfc592c27111cfab1e52f6d4fd73341f39819eac119a5fed367ec87e50',
            '1c687cab0df756bf9e40a537a04fec430bd7a6bc5a5ee8b8968bbc062307d3a6'
        )) {
            $script:buildScript | Should -Match $hash
        }
        foreach ($file in @('libcodec2.dll', 'libopus-0.dll', 'libopusfile-0.dll', 'libogg-0.dll', 'libgcc_s_seh-1.dll', 'libwinpthread-1.dll')) {
            $script:buildScript | Should -Match ([regex]::Escape("'$file'"))
        }
        $script:buildScript | Should -Match 'codec2-1\.2\.0-source\.tar\.gz'
        $script:buildScript | Should -Match 'codec2-msys2-source\.patch'
        $script:buildScript | Should -Match 'mingw-w64-codec2-PKGBUILD'
    }

    It 'refuses to overwrite an existing output directory' {
        $occupied = Join-Path $TestDrive 'occupied-msi-output'
        $null = New-Item -ItemType Directory -Path $occupied
        $output = & pwsh -NoProfile -File $script:buildScriptPath -Version '1.0.0' -OutputDirectory $occupied 2>&1
        $LASTEXITCODE | Should -Not -Be 0
        ($output -join "`n") | Should -Match 'Refusing to overwrite existing MSI build output'
    }
}
