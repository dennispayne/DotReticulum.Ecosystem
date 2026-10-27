#Requires -Version 7.0
BeforeAll {
    $script:repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $script:helper = Join-Path $script:repository 'tools\new-release-tag.ps1'
    $script:telemetry = Join-Path $script:repository 'src\DotTelemetry\DotTelemetry.csproj'
    $script:lxmf = Join-Path $script:repository 'src\DotLXMF\DotLXMF.csproj'
    $script:lxst = Join-Path $script:repository 'src\DotLXST\DotLXST.csproj'

    function Invoke-TagHelper {
        param([string] $Application, [string] $Project)
        Push-Location $script:fixture
        try {
            $output = & pwsh -NoProfile -File $script:helper -Application $Application -Project $Project 2>&1
            @{ ExitCode = $LASTEXITCODE; Output = $output -join "`n" }
        } finally {
            Pop-Location
        }

    }
}

Describe 'Executable release tags' -Tag Integration {
    BeforeEach {
        $script:fixture = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        git init --quiet -b main $script:fixture
        $LASTEXITCODE | Should -Be 0
        git -C $script:fixture -c user.name=Fixture -c user.email=fixture@example.invalid commit --quiet --allow-empty -m fixture
        $LASTEXITCODE | Should -Be 0
        git -C $script:fixture config user.name Fixture
        git -C $script:fixture config user.email fixture@example.invalid
    }

    It 'retains telemetry three-component tags without requiring a remote' {
        $result = Invoke-TagHelper -Application telemetry -Project $script:telemetry
        $result.ExitCode | Should -Be 0 -Because $result.Output
        $tag = git -C $script:fixture tag --list
        $tag | Should -Match '^telemetry-v\d+\.\d+\.\d+$'
        git -C $script:fixture cat-file -t "refs/tags/$tag" | Should -Be 'tag'
    }

    It 'uses the upstream release and assembly height in lxmd four-component tags' {
        $result = Invoke-TagHelper -Application lxmd -Project $script:lxmf
        $result.ExitCode | Should -Be 0 -Because $result.Output
        $tag = git -C $script:fixture tag --list
        $tag | Should -Match '^lxmd-v1\.2\.1\.\d+$'
        git -C $script:fixture cat-file -t "refs/tags/$tag" | Should -Be 'tag'
    }

    It 'refuses a dirty worktree before creating a tag' {
        Set-Content -LiteralPath (Join-Path $script:fixture 'uncommitted.txt') -Value 'fixture'
        $result = Invoke-TagHelper -Application lxmd -Project $script:lxmf
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'working tree has uncommitted changes'
        @(git -C $script:fixture tag --list).Count | Should -Be 0
    }

    It 'uses the pinned LXST release and assembly height in rnphone tags' {
        $result = Invoke-TagHelper -Application rnphone -Project $script:lxst
        $result.ExitCode | Should -Be 0 -Because $result.Output
        $tag = git -C $script:fixture tag --list
        $tag | Should -Match '^rnphone-v0\.4\.5\.\d+$'
        git -C $script:fixture cat-file -t "refs/tags/$tag" | Should -Be 'tag'
    }

    It 'restores versioning for a fresh project without overwriting ordinary build outputs' {
        $project = Join-Path $script:fixture 'Fresh.csproj'
        @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><PackageReference Include="Nerdbank.GitVersioning" Version="3.10.94" PrivateAssets="all" /></ItemGroup>
</Project>
'@ | Set-Content -LiteralPath $project
        '{"version":"1.2.1"}' | Set-Content -LiteralPath (Join-Path $script:fixture 'version.json')
        git -C $script:fixture add Fresh.csproj version.json
        git -C $script:fixture commit --quiet -m 'Add fresh versioned project'
        $LASTEXITCODE | Should -Be 0
        $result = Invoke-TagHelper -Application lxmd -Project $project
        $result.ExitCode | Should -Be 0 -Because $result.Output
        git -C $script:fixture tag --list | Should -Match '^lxmd-v1\.2\.1\.\d+$'
        Test-Path -LiteralPath (Join-Path $script:fixture 'obj') | Should -BeFalse
    }

    It 'reports version-query failures without creating a tag' {
        $result = Invoke-TagHelper -Application lxmd -Project (Join-Path $script:fixture 'missing.csproj')
        $result.ExitCode | Should -Not -Be 0
        $result.Output | Should -Match 'Could not determine the release version'
        @(git -C $script:fixture tag --list).Count | Should -Be 0
    }
}

Describe 'Pester selection gate' -Tag Integration {
    It 'fails when discovery cannot resolve the requested path' {
        $runner = Join-Path $script:repository 'tests\run-pester.ps1'
        $missing = Join-Path $TestDrive 'missing.Tests.ps1'
        & pwsh -NoProfile -File $runner -Path $missing 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 1
    }

    It 'fails when a selected test file cannot be discovered' {
        $runner = Join-Path $script:repository 'tests\run-pester.ps1'
        $broken = Join-Path $TestDrive 'broken.Tests.ps1'
        "throw 'Discovery fixture failure'" | Set-Content -LiteralPath $broken
        & pwsh -NoProfile -File $runner -Path $broken 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 1
    }

    It 'fails when no test matches the requested category' {
        $runner = Join-Path $script:repository 'tests\run-pester.ps1'
        & pwsh -NoProfile -File $runner -Path $PSCommandPath -Tag nonexistent-category 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 1
    }
}

Describe 'Release platform selection' -Tag Integration {
    BeforeAll {
        $workflow = Get-Content -Raw -LiteralPath (Join-Path $script:repository '.github\workflows\release.yml')
        $selection = [regex]::Match($workflow, '(?ms)      - name: Select release target.*?        run: \|\r?\n(?<body>.*?)(?=^  package:)')
        if (!$selection.Success) { throw 'Release selection step was not found.' }
        $script:selectionPath = Join-Path $TestDrive 'release-selection.ps1'
        ($selection.Groups['body'].Value -replace '(?m)^          ', '') |
            Set-Content -LiteralPath $script:selectionPath
    }

    It 'selects <Application> platforms for <RefType> without publishing' -TestCases @(
        @{ Application = 'telemetry'; RefType = 'branch'; RefName = 'main'; PlatformCount = 3 }
        @{ Application = 'lxmd'; RefType = 'branch'; RefName = 'main'; PlatformCount = 3 }
        @{ Application = 'rnphone'; RefType = 'branch'; RefName = 'main'; PlatformCount = 2 }
        @{ Application = 'rnphone'; RefType = 'tag'; RefName = 'rnphone-v0.4.5.12'; PlatformCount = 2 }
    ) {
        param($Application, $RefType, $RefName, $PlatformCount)
        $outputPath = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        $start = [Diagnostics.ProcessStartInfo]::new('pwsh')
        $start.UseShellExecute = $false
        $start.ArgumentList.Add('-NoProfile')
        $start.ArgumentList.Add('-File')
        $start.ArgumentList.Add($script:selectionPath)
        $start.Environment['REQUESTED_APPLICATION'] = $Application
        $start.Environment['GITHUB_REF_TYPE'] = $RefType
        $start.Environment['GITHUB_REF_NAME'] = $RefName
        $start.Environment['GITHUB_SHA'] = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
        $start.Environment['GITHUB_OUTPUT'] = $outputPath
        $process = [Diagnostics.Process]::Start($start)
        try {
            $process.WaitForExit(30000) | Should -BeTrue
            $process.ExitCode | Should -Be 0
        } finally {
            if (!$process.HasExited) { $process.Kill($true) }
            $process.Dispose()
        }
        $selected = ConvertFrom-StringData (Get-Content -Raw -LiteralPath $outputPath)
        $selected.application | Should -Be $Application
        $matrix = $selected.matrix | ConvertFrom-Json
        @($matrix.include).Count | Should -Be $PlatformCount
        if ($Application -eq 'rnphone') {
            $matrix.include[0].rid | Should -Be 'linux-x64'
            $matrix.include[1].rid | Should -Be 'win-x64'
            $selected.project | Should -Be 'src/rnphone/rnphone.csproj'
        }
        if ($RefType -eq 'tag') { $selected.version | Should -Be '0.4.5.12' }
    }
}

Describe 'Solution dependency build configuration' -Tag Integration {
    It 'preserves <Configuration> through a dependency omitted from the solution' -TestCases @(
        @{ Configuration = 'Release' }
        @{ Configuration = 'Debug' }
    ) {
        param($Configuration)
        $fixture = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
        $owner = Join-Path $fixture 'src\Owner'
        $dependency = Join-Path $fixture 'external\Dependency'
        New-Item -ItemType Directory -Path $owner, $dependency | Out-Null
        $project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>'
        $project | Set-Content -LiteralPath (Join-Path $dependency 'Dependency.csproj')
        '<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>' |
            Set-Content -LiteralPath (Join-Path $dependency 'Directory.Build.props')
        @(
            '<Project Sdk="Microsoft.NET.Sdk">'
            '  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>'
            '  <ItemGroup><ProjectReference Include="..\..\external\Dependency\Dependency.csproj" /></ItemGroup>'
            '</Project>'
        ) | Set-Content -LiteralPath (Join-Path $owner 'Owner.csproj')
        '<Solution><Project Path="src\Owner\Owner.csproj" /></Solution>' |
            Set-Content -LiteralPath (Join-Path $fixture 'Fixture.slnx')
        $targets = [Security.SecurityElement]::Escape((Join-Path $script:repository 'Directory.Build.targets'))
        "<Project><Import Project=`"$targets`" /></Project>" |
            Set-Content -LiteralPath (Join-Path $fixture 'Directory.Build.targets')
        $output = Join-Path $fixture 'output'
        $log = & dotnet build (Join-Path $fixture 'Fixture.slnx') -c $Configuration --artifacts-path $output --nologo -v quiet 2>&1
        $LASTEXITCODE | Should -Be 0 -Because ($log -join "`n")
        $assemblies = @(Get-ChildItem (Join-Path $output 'bin\Dependency') -Recurse -Filter Dependency.dll)
        $assemblies.Count | Should -Be 1
        $assemblies[0].Directory.Name | Should -Be $Configuration.ToLowerInvariant()
    }
}
