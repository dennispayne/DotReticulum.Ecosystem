#Requires -Version 7.0
BeforeAll {
    Import-Module (Join-Path (Split-Path $PSScriptRoot -Parent) 'NativeBuild.psm1') -Force
    $script:commit = '0123456789abcdef0123456789abcdef01234567'

    function New-Fixture {
        param([string] $Rid = 'win-x64')
        $directory = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        foreach ($target in Get-CiNativeTargets $Rid) {
            $folder = Join-Path $directory $target.Name
            $null = New-Item -ItemType Directory -Path $folder -Force
            $exe = $target.Name + $(if ($Rid -eq 'win-x64') { '.exe' } else { '' })
            Set-Content -LiteralPath (Join-Path $folder $exe) -Value 'fixture executable'
            Set-Content -LiteralPath (Join-Path $folder 'native-library.bin') -Value 'required runtime dependency'
            Set-Content -LiteralPath (Join-Path $folder 'debug.pdb') -Value 'excluded debug symbols'
        }
        New-CiNativeManifest -Directory $directory -Rid $Rid -Commit $script:commit
        $directory
    }

    function Edit-Manifest {
        param([string] $Directory, [scriptblock] $Edit)
        $path = Join-Path $Directory 'build-manifest.json'
        $manifest = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
        & $Edit $manifest
        $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $path
    }
}

Describe 'CI native artifact handoff' -Tag 'Tooling' {
    It 'verifies all five Windows applications and their runtime dependencies without requiring debug files' {
        $directory = New-Fixture
        Get-ChildItem -LiteralPath $directory -Recurse -Filter '*.pdb' -File | Remove-Item
        $targets = @(Get-VerifiedCiNativeBuild $directory win-x64 $script:commit)
        $targets.Count | Should -Be 5
        $targets.Variable | Should -Contain 'DOTRETICULUM_SIDEBAND_BINARY'
        $targets.Variable | Should -Contain 'DOTLXST_RNPHONE_BINARY'
    }

    It 'keeps the macOS artifact limited to the two release-supported applications' {
        $directory = New-Fixture osx-arm64
        $targets = @(Get-VerifiedCiNativeBuild $directory osx-arm64 $script:commit)
        $targets.Count | Should -Be 2
        $targets.Name | Should -Contain 'telemetry'
        $targets.Name | Should -Contain 'lxmd'
    }

    It 'rejects a wrong source commit or target platform' {
        $directory = New-Fixture
        { Get-VerifiedCiNativeBuild $directory win-x64 ('f' * 40) } | Should -Throw '*does not match*'
        { Get-VerifiedCiNativeBuild $directory linux-x64 $script:commit } | Should -Throw '*does not match*'
    }

    It 'rejects tampered runtime files and missing applications rather than silently rebuilding them' {
        $directory = New-Fixture
        Set-Content -LiteralPath (Join-Path $directory 'sideband\native-library.bin') -Value 'changed'
        { Get-VerifiedCiNativeBuild $directory win-x64 $script:commit } | Should -Throw '*missing or changed*'
        $directory = New-Fixture
        Remove-Item -LiteralPath (Join-Path $directory 'lxmd\lxmd.exe')
        { Get-VerifiedCiNativeBuild $directory win-x64 $script:commit } | Should -Throw '*missing or changed*'
        $directory = New-Fixture
        Edit-Manifest $directory { param($manifest)
            $manifest.files = @($manifest.files | Where-Object path -NotLike '*lxmd.exe')
        }
        { Get-VerifiedCiNativeBuild $directory win-x64 $script:commit } | Should -Throw '*Unverified native runtime file*'
    }

    It 'rejects escaping, absolute and duplicate paths in downloaded manifests' {
        $directory = New-Fixture
        Edit-Manifest $directory { param($manifest) $manifest.files[0].path = '..\outside' }
        { Get-VerifiedCiNativeBuild $directory win-x64 $script:commit } | Should -Throw '*escaping*'
        $directory = New-Fixture
        Edit-Manifest $directory { param($manifest) $manifest.files[0].path = [IO.Path]::GetTempPath() }
        { Get-VerifiedCiNativeBuild $directory win-x64 $script:commit } | Should -Throw '*relative*'
        $directory = New-Fixture
        Edit-Manifest $directory { param($manifest) $manifest.files += $manifest.files[0] }
        { Get-VerifiedCiNativeBuild $directory win-x64 $script:commit } | Should -Throw '*duplicate*'
    }

    It 'rejects additional runtime dependencies, including hidden files, before exporting applications' {
        foreach ($name in 'extra-library.dll', '.hidden-library') {
            $directory = New-Fixture
            Set-Content -LiteralPath (Join-Path $directory "sideband\$name") -Value 'not covered by signed manifest'
            { Get-VerifiedCiNativeBuild $directory win-x64 $script:commit } | Should -Throw '*Unverified native runtime file*'
        }
    }
}
