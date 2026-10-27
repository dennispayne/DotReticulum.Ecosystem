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
}

Describe 'Pester selection gate' -Tag Integration {
    It 'fails when no test matches the requested category' {
        $runner = Join-Path $script:repository 'tests\run-pester.ps1'
        & pwsh -NoProfile -File $runner -Path $PSCommandPath -Tag nonexistent-category 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 1
    }
}
