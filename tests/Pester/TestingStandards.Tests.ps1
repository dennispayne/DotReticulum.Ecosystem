#Requires -Version 7.0
BeforeAll {
    $script:testsRoot = Split-Path $PSScriptRoot -Parent
    Import-Module (Join-Path $testsRoot 'Compatibility\Reporting.psm1') -Force
    Import-Module (Join-Path $testsRoot 'Coverage.psm1') -Force

    function New-Catalog {
        $catalog = Read-CompatibilityCatalog (Join-Path $testsRoot 'Compatibility\catalog.json')
        $catalog.claims = @($catalog.claims[0], $catalog.claims[1])
        $catalog.tests = @($catalog.tests[0])
        $catalog
    }

    function Write-Catalog($Catalog) {
        $path = Join-Path $TestDrive 'catalog.json'
        $Catalog | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $path
        $path
    }

    function Write-Coverage([string] $FileName, [int] $FirstHit, [int] $SecondHit) {
        $source = [Security.SecurityElement]::Escape((Join-Path $TestDrive 'Production.cs'))
        $path = Join-Path $TestDrive $FileName
        @"
<CoverageSession><Modules><Module><ModuleName>production</ModuleName>
<Files><File uid="1" fullPath="$source" /></Files>
<Classes><Class><Methods><Method><Name>Production.Method</Name><FileRef uid="1" />
<SequencePoints>
<SequencePoint sl="10" vc="$FirstHit" />
<SequencePoint sl="10" vc="$FirstHit" fileid="1" />
<SequencePoint sl="11" vc="$SecondHit" fileid="1" />
</SequencePoints><BranchPoints>
<BranchPoint sl="10" vc="$FirstHit" offset="0" path="0" offsetend="1" fileid="1" />
<BranchPoint sl="10" vc="$SecondHit" offset="0" path="1" offsetend="2" fileid="1" />
</BranchPoints></Method></Methods></Class></Classes></Module></Modules></CoverageSession>
"@ | Set-Content -LiteralPath $path
        Get-Item -LiteralPath $path
    }
}

Describe 'Compatibility catalog and aggregation' -Tag 'Tooling' {
    It 'rejects duplicate IDs, dangling claims, unknown references and invalid revisions' {
        $catalog = New-Catalog
        $catalog.tests += $catalog.tests[0]
        { Read-CompatibilityCatalog (Write-Catalog $catalog) } | Should -Throw '*duplicate*'
        $catalog = New-Catalog
        $catalog.tests[0].claimId = 'C999'
        { Read-CompatibilityCatalog (Write-Catalog $catalog) } | Should -Throw '*Unknown claim*'
        $catalog = New-Catalog
        $catalog.tests[0] | Add-Member -NotePropertyName reference -NotePropertyValue Unknown
        { Read-CompatibilityCatalog (Write-Catalog $catalog) } | Should -Throw '*Unknown reference*'
        $catalog = New-Catalog
        $catalog.references[0].revision = 'a29bfb5'
        { Read-CompatibilityCatalog (Write-Catalog $catalog) } | Should -Throw '*full revision*'
        $catalog = New-Catalog
        $catalog.tests[0].pattern = '[unclosed'
        { Read-CompatibilityCatalog (Write-Catalog $catalog) } | Should -Throw
    }

    It 'never allows a passed case to hide a skipped, uncovered or failed case' {
        Get-CompatibilityStatus @('Pass', 'Skipped') | Should -Be 'Partial'
        Get-CompatibilityStatus @('Pass', 'Not covered') | Should -Be 'Partial'
        Get-CompatibilityStatus @('Pass', 'Not run') | Should -Be 'Partial'
        Get-CompatibilityStatus @('Pass', 'Fail', 'Skipped') | Should -Be 'Fail'
        Get-CompatibilityStatus @('Skipped', 'Skipped') | Should -Be 'Skipped'
        Get-CompatibilityStatus @('Not run') | Should -Be 'Not run'
        Get-CompatibilityStatus @() | Should -Be 'Not covered'
    }

    It 'does not pass claims without selectors and reports unmatched execution cases' {
        $catalog = New-Catalog
        $cases = @(
            [pscustomobject]@{ Runner = 'xunit'; Suite = 'LXMF.Tests'; Name = 'LXMF.Tests.LxmfVectorTests.One'; Status = 'Pass' },
            [pscustomobject]@{ Runner = 'xunit'; Suite = 'LXMF.Tests'; Name = 'Other.Test'; Status = 'Pass' }
        )
        $report = New-CompatibilityReport $catalog $cases @('xunit')
        $report.Claims[0].Status | Should -Be 'Pass'
        $report.Claims[1].Status | Should -Be 'Not covered'
        $report.UnmappedCases.Count | Should -Be 1
        $report.Cases.Count | Should -Be 2
        $report = New-CompatibilityReport $catalog @() @('xunit')
        $report.Tests[0].Status | Should -Be 'Not covered'
        $report = New-CompatibilityReport $catalog @() @()
        $report.Tests[0].Status | Should -Be 'Not run'
    }

    It 'retains separate theory outcomes from namespaced TRX and fails unknown outcomes' {
        @'
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
<TestDefinitions>
<UnitTest id="a"><TestMethod className="LXMF.Tests.Example" name="Theory" codeBase="LXMF.Tests.dll" /></UnitTest>
<UnitTest id="b"><TestMethod className="LXMF.Tests.Example" name="Theory" codeBase="LXMF.Tests.dll" /></UnitTest>
<UnitTest id="c"><TestMethod className="LXMF.Tests.Example" name="Broken" codeBase="LXMF.Tests.dll" /></UnitTest>
</TestDefinitions>
<Results><UnitTestResult testId="a" testName="Theory(1)" outcome="Passed" />
<UnitTestResult testId="b" testName="Theory(2)" outcome="NotExecuted"><Output>Requires reference</Output></UnitTestResult>
<UnitTestResult testId="c" testName="Broken" outcome="Aborted" /></Results></TestRun>
'@ | Set-Content -LiteralPath (Join-Path $TestDrive 'cases.trx')
        $cases = @(Read-TrxCases $TestDrive)
        $cases.Count | Should -Be 3
        $cases[0].Name | Should -Be 'LXMF.Tests.Example.Theory'
        $cases[0].Suite | Should -Be 'LXMF.Tests'
        $cases[1].Detail | Should -Be 'Requires reference'
        $cases[2].Status | Should -Be 'Fail'
        Get-CompatibilityStatus @($cases.Status) | Should -Be 'Fail'
    }

    It 'keeps aggregate reference pins identical to the actual import verifier and stack RNS pin' {
        $catalog = Read-CompatibilityCatalog (Join-Path $testsRoot 'Compatibility\catalog.json')
        $verifier = Get-Content -Raw -LiteralPath (Join-Path $testsRoot 'Interop\reference_environment.py')
        foreach ($id in @('RNS', 'LXMF', 'LXST')) {
            $reference = $catalog.references | Where-Object id -eq $id
            $verifier | Should -Match ('PINNED_' + $id + '_SHA = "' + $reference.revision + '"')
        }
        $stack = Get-Content -Raw -LiteralPath (Join-Path $testsRoot '..\external\DotReticulum\tests\Interop\reference_environment.py')
        $stack | Should -Match ('PINNED_RNS_SHA = "' + ($catalog.references | Where-Object id -eq RNS).revision + '"')
    }

    It 'writes failure evidence and restores process flags after failed live preflight' {
        $before = $env:DOTRETICULUM_LIVE
        $oldPath = $env:PYTHONPATH
        try {
            $env:DOTRETICULUM_LIVE = 'sentinel'
            $env:PYTHONPATH = 'untouched'
            $output = Join-Path $TestDrive 'failed-preflight'
            & (Join-Path $testsRoot 'run-compatibility.ps1') -Live -Python (Join-Path $TestDrive 'missing-python') -OutputDirectory $output
            $LASTEXITCODE | Should -Be 1
            $env:DOTRETICULUM_LIVE | Should -Be 'sentinel'
            $env:PYTHONPATH | Should -Be 'untouched'
            $report = Get-Content -Raw -LiteralPath (Join-Path $output 'compatibility.json') | ConvertFrom-Json
            $report.Failures.Count | Should -BeGreaterThan 0
            $report.Cases.Count | Should -Be 0
            @($report.Claims | Where-Object Status -eq 'Pass').Count | Should -Be 0
            { & (Join-Path $testsRoot 'run-compatibility.ps1') -OutputDirectory $output } | Should -Throw '*fresh results directory*'
        }

        finally {
            $env:DOTRETICULUM_LIVE = $before
            $env:PYTHONPATH = $oldPath
        }
    }

    It 'propagates failure through the public runner process' {
        $output = Join-Path $TestDrive 'wrapper-failure'
        & (Join-Path $PSHOME 'pwsh') -NoProfile -File (Join-Path $testsRoot 'run-tests.ps1') -Gui -OutputDirectory $output
        $LASTEXITCODE | Should -Be 1
        $report = Get-Content -Raw -LiteralPath (Join-Path $output 'compatibility.json') | ConvertFrom-Json
        $report.RunStatus | Should -Be 'Failed'
        $report.Failures | Should -Contain '-Gui requires -Pester.'
    }
}

Describe 'Production coverage union' -Tag 'Tooling' {
    It 'unions hits across suites and counts shared lines and distinct branch paths once' {
        $reports = @((Write-Coverage 'one.xml' 1 0), (Write-Coverage 'two.xml' 0 1))
        $modules = Merge-ProductionCoverage $reports @('production')
        $modules['production'].Lines.Count | Should -Be 2
        @($modules['production'].Lines.Values | Where-Object { $_ }).Count | Should -Be 2
        $modules['production'].Branches.Count | Should -Be 2
        @($modules['production'].Branches.Values | Where-Object { $_ }).Count | Should -Be 2
    }

    It 'fails missing production modules instead of silently reporting inflated coverage' {
        $report = Write-Coverage 'partial.xml' 1 0
        { Merge-ProductionCoverage @($report) @('production', 'unloaded') } | Should -Throw '*unloaded*'
        { Merge-ProductionCoverage @() @('production') } | Should -Throw '*production*'
    }

    It 'derives distinct case-sensitive assembly names including desktop core and executables' {
        $expected = @(Get-ProductionModules (Split-Path $testsRoot -Parent))
        $expected.Count | Should -Be 8
        $expected | Should -Contain 'Sideband.Core'
        $expected | Should -Contain 'sideband'
        $expected | Should -Contain 'lxmd'
        $expected | Should -Contain 'rnphone'
    }
}
