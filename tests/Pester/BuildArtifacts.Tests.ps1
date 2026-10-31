#Requires -Version 7.3
BeforeAll {
    $script:root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    Import-Module (Join-Path $script:root 'tests\ManagedBuild.psm1') -Force
    $script:commit = 'a' * 40
    $script:resolver = Join-Path $script:root 'tests\Resolve-TestBuild.ps1'
    $script:pwsh = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
    function gh {
        param([string] $Command, [string] $Endpoint)
        throw "Unmocked GitHub command: $Command $Endpoint"
    }

    function New-ManagedFixture {
        param([string] $Extra, [switch] $Link)
        $directory = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $null = New-Item -ItemType Directory -Path $directory
        $archive = Join-Path $directory 'managed.tar'
        $assemblies = @(Get-EcosystemTestAssemblies $script:root)
        $stream = [IO.File]::Create($archive)
        $writer = [System.Formats.Tar.TarWriter]::new($stream)
        try {
            foreach ($name in @($assemblies | ForEach-Object { $_.Replace('\', '/') }) + @('collector/coverlet.collector.dll') + @($Extra | Where-Object { $_ })) {
                $kind = if ($Link -and $name -eq $Extra) {
                    [System.Formats.Tar.TarEntryType]::SymbolicLink
                } else { [System.Formats.Tar.TarEntryType]::RegularFile }
                $entry = [System.Formats.Tar.PaxTarEntry]::new($kind, $name)
                if ($kind -eq [System.Formats.Tar.TarEntryType]::SymbolicLink) { $entry.LinkName = '../../outside' }
                else { $entry.DataStream = [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes('fixture')) }
                try { $writer.WriteEntry($entry) }
                finally { if ($entry.DataStream) { $entry.DataStream.Dispose() } }
            }
        }
        finally { $writer.Dispose(); $stream.Dispose() }
        @{
            schemaVersion = 1; commit = $script:commit; rid = 'win-x64'; assemblies = $assemblies
            sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
        } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $directory 'managed-manifest.json')
        $directory
    }
}

Describe 'Portable ecosystem test Build' -Tag Tooling {
    It 'verifies the complete five-assembly archive and packaged coverage collector without executing it' {
        $directory = New-ManagedFixture
        $build = Test-EcosystemManagedBuild $script:root $directory win-x64 $script:commit
        $build.Assemblies.Count | Should -Be 5
    }

    It 'rejects a changed archive, wrong commit and wrong RID before extraction' {
        $directory = New-ManagedFixture
        { Test-EcosystemManagedBuild $script:root $directory win-x64 ('b' * 40) } | Should -Throw '*commit mismatch*'
        { Test-EcosystemManagedBuild $script:root $directory linux-x64 $script:commit } | Should -Throw '*RID*'
        Add-Content -LiteralPath (Join-Path $directory 'managed.tar') -Value 'changed'
        { Test-EcosystemManagedBuild $script:root $directory win-x64 $script:commit } | Should -Throw '*digest*'
    }

    It 'rejects incomplete assembly inventories and archive traversal or links' {
        $directory = New-ManagedFixture
        $path = Join-Path $directory 'managed-manifest.json'
        $manifest = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
        $manifest.assemblies = @($manifest.assemblies | Select-Object -First 1)
        $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $path
        { Test-EcosystemManagedBuild $script:root $directory win-x64 $script:commit } | Should -Throw '*inventory*'
        $directory = New-ManagedFixture -Extra 'tests/LXMF.Tests/bin/Release/net10.0/../../outside'
        { Test-EcosystemManagedBuild $script:root $directory win-x64 $script:commit } | Should -Throw '*archive entry*'
        $directory = New-ManagedFixture -Extra 'collector/linked.dll' -Link
        { Test-EcosystemManagedBuild $script:root $directory win-x64 $script:commit } | Should -Throw '*archive entry*'
    }
}

Describe 'Exact cross-workflow producer selection' -Tag Tooling {
    BeforeEach {
        $script:oldOutput = $env:GITHUB_OUTPUT
        $script:oldSummary = $env:GITHUB_STEP_SUMMARY
        $env:GITHUB_OUTPUT = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $env:GITHUB_STEP_SUMMARY = $null
        $script:producer = @{
            id = 123; repository = @{ full_name = 'owner/ecosystem' }; head_repository = @{ full_name = 'owner/ecosystem' }
            path = '.github/workflows/build.yml'; status = 'completed'; conclusion = 'success'
            head_sha = $script:commit; event = 'push'; head_branch = 'main'; run_attempt = 1
        }
        $script:artifacts = @(foreach ($rid in 'linux-x64', 'win-x64', 'osx-arm64') {
            foreach ($kind in 'native', 'managed') { @{ name = "$kind-$rid"; expired = $false } }
        })
        $script:parents = @(@{ sha = 'c' * 40 }, @{ sha = $script:commit })
        $global:EcosystemBuildFixture = @{
            Producer = $script:producer; Artifacts = $script:artifacts; Parents = $script:parents
        }
        Mock gh {
            param([string] $Command, [string] $Endpoint)
            $global:LASTEXITCODE = 0
            if ($Endpoint -like '*artifacts*') { @{ artifacts = $global:EcosystemBuildFixture.Artifacts } | ConvertTo-Json -Depth 6 }
            elseif ($Endpoint -like '*git/commits*') { @{ parents = $global:EcosystemBuildFixture.Parents } | ConvertTo-Json -Depth 6 }
            else { $global:EcosystemBuildFixture.Producer | ConvertTo-Json -Depth 6 }
        }
    }
    AfterEach {
        $env:GITHUB_OUTPUT = $script:oldOutput
        $env:GITHUB_STEP_SUMMARY = $script:oldSummary
        Remove-Variable EcosystemBuildFixture -Scope Global
    }

    It 'selects a successful main producer and requires signed provenance' {
        & $script:resolver -RunId 123 -Repository owner/ecosystem -EventPath ''
        Get-Content -Raw -LiteralPath $env:GITHUB_OUTPUT | Should -Match 'attested=true'
        Get-Content -Raw -LiteralPath $env:GITHUB_OUTPUT | Should -Match "commit=$script:commit"
    }

    It 'rejects failed or foreign workflows and missing, expired or duplicate artifacts' {
        $script:producer.conclusion = 'failure'
        { & $script:resolver -RunId 123 -Repository owner/ecosystem -EventPath '' } | Should -Throw '*successful Build*'
        $script:producer.conclusion = 'success'
        $script:producer.path = '.github/workflows/release.yml'
        { & $script:resolver -RunId 123 -Repository owner/ecosystem -EventPath '' } | Should -Throw '*successful Build*'
        $script:producer.path = '.github/workflows/build.yml'
        $script:artifacts[0].expired = $true
        { & $script:resolver -RunId 123 -Repository owner/ecosystem -EventPath '' } | Should -Throw '*expired*'
        $script:artifacts[0].expired = $false
        $script:artifacts += $script:artifacts[0]
        $global:EcosystemBuildFixture.Artifacts = $script:artifacts
        { & $script:resolver -RunId 123 -Repository owner/ecosystem -EventPath '' } | Should -Throw '*duplicate*'
        $script:artifacts = @($script:artifacts | Select-Object -Skip 1)
        $script:artifacts = @($script:artifacts | Where-Object name -ne 'native-linux-x64')
        $global:EcosystemBuildFixture.Artifacts = $script:artifacts
        { & $script:resolver -RunId 123 -Repository owner/ecosystem -EventPath '' } | Should -Throw '*Missing*'
    }

    It 'preserves a validated PR merge but rejects manual PR selection and unrelated merges' {
        $script:producer.event = 'pull_request'
        $script:producer.head_branch = 'feature'
        { & $script:resolver -RunId 123 -Repository owner/ecosystem -EventPath '' } | Should -Throw '*main Build*'
        $event = Join-Path $TestDrive 'event.json'
        @{ workflow_run = @{ id = 123 } } | ConvertTo-Json | Set-Content -LiteralPath $event
        $manifest = Join-Path $TestDrive 'native.json'
        @{ rid = 'linux-x64'; commit = 'd' * 40 } | ConvertTo-Json | Set-Content -LiteralPath $manifest
        & $script:resolver -RunId 123 -Repository owner/ecosystem -EventPath $event -NativeManifest $manifest
        Get-Content -Raw -LiteralPath $env:GITHUB_OUTPUT | Should -Match 'attested=false'
        Get-Content -Raw -LiteralPath $env:GITHUB_OUTPUT | Should -Match ('commit=' + ('d' * 40))
        $script:parents[1].sha = 'e' * 40
        { & $script:resolver -RunId 123 -Repository owner/ecosystem -EventPath $event -NativeManifest $manifest } |
            Should -Throw '*merge of the PR head*'
        { & $script:resolver -RunId 124 -Repository owner/ecosystem -EventPath $event } | Should -Throw '*triggering Build*'
    }
}

Describe 'Static analysis evidence' -Tag Tooling {
    It 'writes the finding before failing the pinned PowerShell analysis gate' -Tag Analysis {
        if (!(Get-Module -ListAvailable PSScriptAnalyzer | Where-Object Version -EQ ([version]'1.24.0'))) {
            Set-ItResult -Skipped -Because 'The dedicated Static analysis job installs PSScriptAnalyzer 1.24.0.'
            return
        }
        $directory = Join-Path $TestDrive 'analysis-fixture'
        $null = New-Item -ItemType Directory -Path $directory
        Set-Content -LiteralPath (Join-Path $directory 'Invoke-Fixture.ps1') -Value "Invoke-Expression 'Write-Output fixture'"
        & git -C $directory init --quiet
        if ($LASTEXITCODE -ne 0) { throw 'Cannot initialize analyzer fixture.' }
        & git -C $directory add -- Invoke-Fixture.ps1
        if ($LASTEXITCODE -ne 0) { throw 'Cannot stage analyzer fixture.' }
        $report = Join-Path $directory 'analysis.sarif'
        & $script:pwsh -NoProfile -File (Join-Path $script:root 'tools\Invoke-PowerShellAnalysis.ps1') -Repository $directory `
            -Settings (Join-Path $script:root 'PSScriptAnalyzerSettings.psd1') -Report $report *> $null
        $LASTEXITCODE | Should -Be 1
        $sarif = Get-Content -Raw -LiteralPath $report | ConvertFrom-Json
        $sarif.runs[0].results.ruleId | Should -Contain 'PSAvoidUsingInvokeExpression'
        $sarif.runs[0].invocations[0].executionSuccessful | Should -BeTrue
    }

    It 'reports missing analyses as unavailable instead of zero findings and rejects duplicates' {
        $directory = Join-Path $TestDrive 'sarif'
        $null = New-Item -ItemType Directory -Path $directory
        $summary = Join-Path $script:root 'tools\Get-StaticAnalysisSummary.ps1'
        (& $summary -ReportsDirectory $directory | Out-String) | Should -Match 'Unavailable'
        $run = @{
            tool = @{ driver = @{ name = 'CodeQL' } }; automationDetails = @{ id = '/language:python/' }
            results = @(@{ ruleId = 'fixture'; level = 'warning' })
        }
        @{ version = '2.1.0'; runs = @($run) } | ConvertTo-Json -Depth 10 |
            Set-Content -LiteralPath (Join-Path $directory 'python.sarif')
        (& $summary -ReportsDirectory $directory | Out-String) | Should -Match '/language:python \| unknown \| 0 \| 1'
        Copy-Item -LiteralPath (Join-Path $directory 'python.sarif') -Destination (Join-Path $directory 'duplicate.sarif')
        { & $summary -ReportsDirectory $directory } | Should -Throw '*duplicate*'
    }

    It 'fails incomplete consolidated CI evidence while preserving its diagnostic summary' {
        $directory = Join-Path $TestDrive 'missing-ci'
        $null = New-Item -ItemType Directory -Path $directory
        & $script:pwsh -NoProfile -File (Join-Path $script:root 'tests\Get-CiSummary.ps1') -Directory $directory `
            -Commit $script:commit -Attempt 1 -ManagedStatus failure -CompatibilityStatus failure *> $null
        $LASTEXITCODE | Should -Be 1
        Get-Content -Raw -LiteralPath (Join-Path $directory 'summary\tests.md') | Should -Match 'Unavailable'
    }

    It 'accepts the same catalog from LF and CRLF checkouts without accepting incomplete evidence' -TestCases @(
        @{ Ending = "`n" }
        @{ Ending = "`r`n" }
    ) {
        param($Ending)
        $directory = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $text = (Get-Content -Raw -LiteralPath (Join-Path $script:root 'tests\Compatibility\catalog.json')).Replace("`r`n", "`n").Replace("`n", $Ending)
        $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text)))
        foreach ($rid in 'linux-x64', 'win-x64') {
            $target = Join-Path $directory "compatibility-$rid-1\compatibility"
            $null = New-Item -ItemType Directory -Path $target -Force
            @{
                Commit = $script:commit; CatalogSha256 = $digest; Cases = @(); Failures = @()
                RunStatus = 'Passed selected checks'; WorkingTreeDirty = $true
                NativeBinaries = @(); VerifiedReference = $null; Modes = @{ Live = $false }
            } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $target 'compatibility.json')
        }
        $log = Join-Path $directory 'summary.log'
        & $script:pwsh -NoProfile -File (Join-Path $script:root 'tests\Get-CiSummary.ps1') -Directory $directory `
            -Commit $script:commit -Attempt 1 -ManagedStatus success -CompatibilityStatus success *> $log
        $LASTEXITCODE | Should -Be 1
        Get-Content -Raw -LiteralPath $log | Should -Not -Match 'commit/catalog mismatch'
        $report = Get-Content -Raw -LiteralPath (Join-Path $directory 'summary\win-x64\compatibility.json') | ConvertFrom-Json
        $report.RunStatus | Should -Be 'Failed or incomplete'
        $report.CatalogSha256 | Should -BeExactly $digest
        $report.Modes.Live | Should -BeFalse

        foreach ($invalid in @(
            @{ Commit = $script:commit; CatalogSha256 = '0' * 64 }
            @{ Commit = 'b' * 40; CatalogSha256 = $digest }
        )) {
            $invalid | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'compatibility-linux-x64-1\compatibility\compatibility.json')
            & $script:pwsh -NoProfile -File (Join-Path $script:root 'tests\Get-CiSummary.ps1') -Directory $directory `
                -Commit $script:commit -Attempt 1 -ManagedStatus success -CompatibilityStatus success *> $log
            $LASTEXITCODE | Should -Be 1
            Get-Content -Raw -LiteralPath $log | Should -Match 'commit/catalog mismatch'
        }
    }
}
