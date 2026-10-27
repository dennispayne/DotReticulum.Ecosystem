#Requires -Version 7.0
BeforeAll {
    $script:repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $script:verifier = Join-Path $script:repository 'tests\Interop\reference_environment.py'
    $script:python = $env:DOTRETICULUM_PYTHON
    $script:upstream = $env:DOTRETICULUM_UPSTREAM

    function Invoke-ReferenceCheck {
        param(
            [string] $Root = $script:upstream,
            [string] $ModulePath = $Root,
            [switch] $Lxst,
            [string] $LxstRoot = $env:DOTRETICULUM_LXST_UPSTREAM
        )
        $start = [System.Diagnostics.ProcessStartInfo]::new($script:python)
        $start.ArgumentList.Add($script:verifier)
        $start.ArgumentList.Add('--require-lxmf')
        if ($Lxst) {
            $start.ArgumentList.Add('--require-lxst')
            $start.Environment['DOTRETICULUM_LXST_UPSTREAM'] = $LxstRoot
            $ModulePath += [IO.Path]::PathSeparator + $LxstRoot
        }
        $start.Environment['DOTRETICULUM_UPSTREAM'] = $Root
        $start.Environment['PYTHONPATH'] = $ModulePath
        $start.Environment['PYTHONIOENCODING'] = 'utf-8'
        $start.UseShellExecute = $false
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $process = [System.Diagnostics.Process]::Start($start)
        try {
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            if (!$process.WaitForExit(30000)) {
                $process.Kill($true)
                $process.WaitForExit()
                throw 'Reference verification exceeded 30 seconds.'
            }

            @{
                ExitCode = $process.ExitCode
                Output = $stdout.GetAwaiter().GetResult()
                Error = $stderr.GetAwaiter().GetResult()
            }
        } finally {
            $process.Dispose()
        }
    }
}

Describe 'Pinned reference provenance' -Tag Integration -Skip:(!$env:DOTRETICULUM_PYTHON -or !$env:DOTRETICULUM_UPSTREAM) {
    It 'accepts the exact installed LXMF wheel and clean pinned RNS checkout' {
        $result = Invoke-ReferenceCheck
        $result.ExitCode | Should -Be 0 -Because $result.Error
        $reference = $result.Output | ConvertFrom-Json
        $reference.rns_revision | Should -Be 'a29bfb51c468945c96a1ee22cae0114e993e2391'
        $reference.lxmf_version | Should -Be '1.2.1'
    }

    It 'rejects a correct import selected from a different declared checkout' {
        $result = Invoke-ReferenceCheck -Root $TestDrive -ModulePath $script:upstream
        $result.ExitCode | Should -Not -Be 0
        $result.Error | Should -Match 'RNS imported from'
    }

    It 'rejects modifications to the actual imported RNS source' {
        $checkout = Join-Path $TestDrive 'rns'
        git clone --quiet --no-hardlinks $script:upstream $checkout 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 0
        Add-Content -LiteralPath (Join-Path $checkout 'RNS\__init__.py') -Value "`n# Provenance rejection test."
        $result = Invoke-ReferenceCheck -Root $checkout
        $result.ExitCode | Should -Not -Be 0
        $result.Error | Should -Match 'Reference RNS source contains modifications'
    }

    It 'rejects additional untracked RNS modules' {
        $checkout = Join-Path $TestDrive 'rns-extra'
        git clone --quiet --no-hardlinks $script:upstream $checkout 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 0
        Set-Content -LiteralPath (Join-Path $checkout 'RNS\unexpected_reference_module.py') -Value ''
        $result = Invoke-ReferenceCheck -Root $checkout
        $result.ExitCode | Should -Not -Be 0
        $result.Error | Should -Match 'Reference RNS source contains modifications or untracked files'
    }

    It 'rejects LXMF source drift even when its distribution version is unchanged' {
        $baseline = Invoke-ReferenceCheck
        $baseline.ExitCode | Should -Be 0 -Because $baseline.Error
        $distribution = ($baseline.Output | ConvertFrom-Json).lxmf_distribution
        $shadow = Join-Path $TestDrive 'shadow'
        New-Item -ItemType Directory -Path $shadow | Out-Null
        Copy-Item -LiteralPath (Join-Path $distribution 'LXMF') -Destination $shadow -Recurse
        $metadata = Get-ChildItem -LiteralPath $distribution -Directory |
            Where-Object Name -Match '^lxmf-1\.2\.1\.dist-info$'
        @($metadata).Count | Should -Be 1
        Copy-Item -LiteralPath $metadata.FullName -Destination $shadow -Recurse
        Add-Content -LiteralPath (Join-Path $shadow 'LXMF\LXMF.py') -Value "`n# Provenance rejection test."
        $path = $shadow + [IO.Path]::PathSeparator + $script:upstream
        $result = Invoke-ReferenceCheck -ModulePath $path
        $result.ExitCode | Should -Not -Be 0
        $result.Error | Should -Match 'LXMF source LXMF.py does not match the pinned wheel'
    }
}

Describe 'Pinned LXST provenance' -Tag Integration -Skip:(!$env:DOTRETICULUM_PYTHON -or !$env:DOTRETICULUM_UPSTREAM -or !$env:DOTRETICULUM_LXST_UPSTREAM) {
    It 'accepts the exact imported checkout including upstream-generated native filter outputs' {
        $result = Invoke-ReferenceCheck -Lxst
        $result.ExitCode | Should -Be 0 -Because $result.Error
        ($result.Output | ConvertFrom-Json).lxst_revision | Should -Be '721b2eee6dd37606e423914a4937a11a3c3a761f'
    }

    It 'rejects untracked LXST source despite allowing native filter build outputs' {
        $checkout = Join-Path $TestDrive 'lxst-extra'
        git clone --quiet --no-hardlinks $env:DOTRETICULUM_LXST_UPSTREAM $checkout 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 0
        Set-Content -LiteralPath (Join-Path $checkout 'LXST\unexpected_reference_module.py') -Value ''
        $result = Invoke-ReferenceCheck -Lxst -LxstRoot $checkout
        $result.ExitCode | Should -Not -Be 0
        $result.Error | Should -Match 'Reference LXST source contains modifications or untracked files'
    }
}
