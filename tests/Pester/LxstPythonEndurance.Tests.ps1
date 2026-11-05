#Requires -Version 7.0
Describe 'C111 actual pinned Python telephone endurance' -Tag Integration, Live, LxstPythonEndurance {
    It 'T1100 sustains bidirectional real-codec frames for 600 steady seconds then reverses and recovers' {
        if (!$IsWindows -or $env:DOTLXST_PYTHON_ENDURANCE -ne '1' -or !$env:DOTRETICULUM_UPSTREAM) {
            Set-ItResult -Skipped -Because 'Requires Windows, DOTLXST_PYTHON_ENDURANCE=1 and pinned DOTRETICULUM_UPSTREAM.'
            return
        }
        & (Join-Path $PSScriptRoot 'Invoke-LxstPythonEndurance.ps1') -RnsSource $env:DOTRETICULUM_UPSTREAM
    }
}
