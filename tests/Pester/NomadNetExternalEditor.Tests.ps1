BeforeAll {
    $script:ExternalRepository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $script:ExternalEvidence = if ($env:DOTRETICULUM_NOMADNET_EDITOR_EVIDENCE) {
        $env:DOTRETICULUM_NOMADNET_EDITOR_EVIDENCE
    } else {
        Join-Path $script:ExternalRepository ('artifacts\tests\nomad-external-editor\acceptance-' + [guid]::NewGuid().ToString('N'))
    }
    $null = New-Item $script:ExternalEvidence -ItemType Directory -Force

    function Invoke-ExternalNative([string] $Label, [string[]] $Arguments, [string[]] $Commands) {
        if (-not (Test-Path $env:DOTRETICULUM_NOMADNET_BINARY -PathType Leaf)) { throw 'Select the exact published native binary.' }
        $info = [Diagnostics.ProcessStartInfo]::new($env:DOTRETICULUM_NOMADNET_BINARY)
        $info.UseShellExecute = $false
        $info.RedirectStandardInput = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $info
        $started = $false
        try {
            $started = $process.Start()
            if (-not $started) { throw 'Native process did not start.' }
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            foreach ($command in $Commands) { $process.StandardInput.WriteLine($command) }
            $process.StandardInput.Close()
            $null = $process.WaitForExitAsync().WaitAsync([TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult()
            $text = $stdout.GetAwaiter().GetResult()
            $errorText = $stderr.GetAwaiter().GetResult()
            [IO.File]::WriteAllText((Join-Path $script:ExternalEvidence "$Label.stdout"), $text)
            [IO.File]::WriteAllText((Join-Path $script:ExternalEvidence "$Label.stderr"), $errorText)
            $process.ExitCode | Should -Be 0 -Because $errorText
            return $text
        } finally {
            if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
            $process.Dispose()
        }
    }

    function New-ExternalProfile([string] $Name) {
        $root = Join-Path $script:ExternalEvidence $Name
        $rns = Join-Path $root 'explicit-rns'
        $profile = Join-Path $root profile
        $null = New-Item $rns -ItemType Directory -Force
        $text = "# Native selected original`n[reticulum]`n share_instance = no`n enable_transport = no`n[logging]`n loglevel = 0`n[interfaces]`n"
        [IO.File]::WriteAllText((Join-Path $rns config), $text)
        $null = Invoke-ExternalNative "$Name-init" @('--init', '--config', $profile) @()
        $settingsFile = Join-Path $profile settings.json
        $settings = Get-Content $settingsFile -Raw | ConvertFrom-Json
        $settings.clientAnnounceAtStart = $false
        $settings.periodicLxmfSync = $false
        $settings | ConvertTo-Json -Depth 10 | Set-Content $settingsFile -Encoding utf8NoBOM
        return @{ Profile=$profile; Rns=$rns; Original=$text; Settings=$settingsFile;
            Arguments=@('--config', $profile, '--rnsconfig', $rns) }
    }
}

Describe 'Configured external editor and explicitly selected RNS configuration' {
    It 'ExternalEditor native real Vim ConPTY edits failure recovery descendants and restarts pass' {
        if ($env:DOTRETICULUM_NOMADNET_EXTERNAL_EDITOR -ne '1' -or -not $IsWindows) {
            Set-ItResult -Skipped -Because 'Requires Windows ConPTY, DOTRETICULUM_NOMADNET_EXTERNAL_EDITOR=1, selected native binary and real Git for Windows Vim.'
            return
        }
        $resultFile = Join-Path $script:ExternalEvidence 'actual-conpty.trx'
        & dotnet test (Join-Path $script:ExternalRepository 'tests\NomadNet.Tests\NomadNet.Tests.csproj') -c Release --no-restore `
            --filter 'FullyQualifiedName~NomadNetExternalEditorNativeTests' `
            --logger 'trx;LogFileName=actual-conpty.trx' --results-directory $script:ExternalEvidence -v quiet 2>&1 |
            Tee-Object (Join-Path $script:ExternalEvidence actual-conpty.log) | Out-Host
        $LASTEXITCODE | Should -Be 0
        [xml] $result = Get-Content $resultFile -Raw
        $result.TestRun.ResultSummary.Counters.total | Should -Be 2
        $result.TestRun.ResultSummary.Counters.passed | Should -Be 2
        $result.TestRun.ResultSummary.Counters.failed | Should -Be 0
        $result.TestRun.ResultSummary.Counters.notExecuted | Should -Be 0
    }

    It 'ExternalEditor native rejects redirected terminal ownership and continues without changing configuration' {
        if ($env:DOTRETICULUM_NOMADNET_EXTERNAL_EDITOR -ne '1') {
            Set-ItResult -Skipped -Because 'Enable selected native external-editor acceptance.'
            return
        }
        $p = New-ExternalProfile redirected
        $before = (Get-FileHash $p.Settings).Hash
        $rnsBefore = (Get-FileHash (Join-Path $p.Rns config)).Hash
        $text = Invoke-ExternalNative redirected $p.Arguments @('config external native', 'config external rns', 'status', 'quit')
        ([regex]::Matches($text, 'redirected input/output is unsupported')).Count | Should -Be 2
        $text | Should -Match 'Reticulum identity:'
        (Get-FileHash $p.Settings).Hash | Should -BeExactly $before
        (Get-FileHash (Join-Path $p.Rns config)).Hash | Should -BeExactly $rnsBefore
        @(Get-ChildItem $p.Profile -Directory -Force -Filter '.editor-session-*').Count | Should -Be 0
    }

    It 'ExternalEditor native selected RNS buffer preserves exact comments and restarts with the same identity' {
        if ($env:DOTRETICULUM_NOMADNET_EXTERNAL_EDITOR -ne '1') {
            Set-ItResult -Skipped -Because 'Enable selected native external-editor acceptance.'
            return
        }
        $p = New-ExternalProfile selected
        $text = Invoke-ExternalNative selected $p.Arguments @('config edit rns',
            'edit replace-all ["Native selected original","Native selected changed"]',
            'config validate', 'edit save', 'config show rns', 'status', 'quit')
        $text | Should -Not -Match 'Error:'
        $text | Should -Match 'Configuration buffer valid'
        $text | Should -Match 'Restart NomadNet'
        (Get-Content (Join-Path $p.Rns config) -Raw) | Should -BeExactly ($p.Original -replace 'Native selected original','Native selected changed')
        $identity = [regex]::Match($text, 'Reticulum identity: ([0-9a-f]{32})').Groups[1].Value
        $identity.Length | Should -Be 32
        $restart = Invoke-ExternalNative selected-restart $p.Arguments @('config show rns', 'status', 'quit')
        $restart | Should -Match 'Native selected changed'
        $restart | Should -Match "Reticulum identity: $identity"
    }

    It 'ExternalEditor regenerates byte-identical actual-primary configuration observations with explicit terminal boundary' {
        if ($env:DOTRETICULUM_NOMADNET_EDITOR_REFERENCE -ne '1') {
            Set-ItResult -Skipped -Because 'Enable primary-reference execution with pinned Python/RNS/LXMF/NomadNet paths.'
            return
        }
        $output = Join-Path $script:ExternalEvidence regenerated-external.json
        & $env:DOTRETICULUM_PYTHON (Join-Path $script:ExternalRepository 'tests\Interop\GenerateNomadNetExternalEditorVectors.py') `
            --output $output --observations (Join-Path $script:ExternalEvidence reference-imports.json) `
            --work (Join-Path $script:ExternalEvidence reference-work)
        $LASTEXITCODE | Should -Be 0
        (Get-FileHash $output).Hash | Should -BeExactly (Get-FileHash (Join-Path $script:ExternalRepository 'tests\NomadNet.Tests\NomadNetExternalEditorVectors.json')).Hash
    }
}
