#Requires -Version 7.3
BeforeAll {
    $script:root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $script:launcher = Join-Path $script:root 'tools\Start-LanguageServer.ps1'
    $script:platform = [Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier
    $script:toolName = if ($IsWindows) { 'csharp-ls.exe' } else { 'csharp-ls' }
}

Describe 'Node-free language server restoration' -Tag Tooling {
    BeforeEach {
        $script:cache = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $script:csharp = Join-Path $script:cache "$script:platform\csharp\0.28.0"
        $script:powershell = Join-Path $script:cache "$script:platform\powershell\4.7.0"
        Mock dotnet {
            $toolPath = $args[([array]::IndexOf($args, '--tool-path') + 1)]
            $null = New-Item -ItemType Directory -Force -Path $toolPath
            $toolName = if ($IsWindows) { 'csharp-ls.exe' } else { 'csharp-ls' }
            Set-Content -LiteralPath (Join-Path $toolPath $toolName) -Value 'fixture'
            Write-Output 'installer output must not become protocol output'
            $global:LASTEXITCODE = 0
        }
    }

    It 'restores once without stdout and reuses a complete offline cache' {
        $output = @(& $script:launcher -Server csharp -CacheRoot $script:cache -RestoreOnly)
        $output.Count | Should -Be 0
        Test-Path -LiteralPath (Join-Path $script:csharp 'restored') | Should -BeTrue
        & $script:launcher -Server csharp -CacheRoot $script:cache -RestoreOnly
        Should -Invoke dotnet -Times 1 -Exactly
    }

    It 'repairs a marked cache whose executable is missing' {
        & $script:launcher -Server csharp -CacheRoot $script:cache -RestoreOnly
        Remove-Item -LiteralPath (Join-Path $script:csharp "tool\$script:toolName")
        & $script:launcher -Server csharp -CacheRoot $script:cache -RestoreOnly
        Should -Invoke dotnet -Times 2 -Exactly
        Test-Path -LiteralPath (Join-Path $script:csharp "tool\$script:toolName") | Should -BeTrue
    }

    It 'reports installer failure without marking the cache and releases the lock for retry' {
        Mock dotnet { $global:LASTEXITCODE = 27 }
        { & $script:launcher -Server csharp -CacheRoot $script:cache -RestoreOnly } | Should -Throw '*exit 27*'
        Test-Path -LiteralPath (Join-Path $script:csharp 'restored') | Should -BeFalse
        $lock = [IO.File]::Open((Join-Path $script:csharp 'restore.lock'), 'Open', 'ReadWrite', 'None')
        $lock.Dispose()
    }

    It 'rejects a tampered PowerShell archive before extraction or execution' {
        Mock Invoke-WebRequest {
            param($Uri, $OutFile)
            Set-Content -LiteralPath $OutFile -Value 'tampered'
        }
        Mock Expand-Archive { throw 'Must not extract an unverified archive.' }
        { & $script:launcher -Server powershell -CacheRoot $script:cache -RestoreOnly } | Should -Throw '*SHA-256 mismatch*'
        Should -Invoke Expand-Archive -Times 0
        Test-Path -LiteralPath (Join-Path $script:powershell 'restored') | Should -BeFalse
    }
}
