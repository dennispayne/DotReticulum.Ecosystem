BeforeAll {
    $script:FormRepository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $script:FormEvidence = if ($env:DOTRETICULUM_NOMADNET_FORM_EVIDENCE) {
        $env:DOTRETICULUM_NOMADNET_FORM_EVIDENCE
    } else { Join-Path $script:FormRepository ('artifacts\tests\nomad-config-forms\acceptance-' + [guid]::NewGuid().ToString('N')) }
    $null = New-Item $script:FormEvidence -ItemType Directory -Force

    function Start-FormNative([string] $Label, [string[]] $Arguments) {
        if (-not (Test-Path $env:DOTRETICULUM_NOMADNET_BINARY -PathType Leaf)) { throw 'Select the exact published native executable.' }
        $info = [Diagnostics.ProcessStartInfo]::new($env:DOTRETICULUM_NOMADNET_BINARY)
        $info.UseShellExecute = $false
        $info.RedirectStandardInput = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $info
        if (-not $process.Start()) { $process.Dispose(); throw 'Native process did not start.' }
        return @{Process=$process; Errors=$process.StandardError.ReadToEndAsync(); Text=[Text.StringBuilder]::new(); Label=$Label}
    }
    function Send-FormCommand($Native, [string] $Command) {
        $Native.Process.StandardInput.WriteLine($Command)
        $Native.Process.StandardInput.Flush()
    }
    function Receive-FormLine($Native, [string] $Pattern) {
        for ($count = 0; $count -lt 4096; $count++) {
            $line = $Native.Process.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult()
            if ($null -eq $line) { throw "Native input ended before $Pattern. $($Native.Text)" }
            if ($Native.Text.Length + $line.Length -gt 2000000) { throw 'Native output exceeded the evidence bound.' }
            $null = $Native.Text.AppendLine($line)
            if ($line -match $Pattern) { return $line }
        }
        throw "Native line limit exceeded before $Pattern."
    }
    function Stop-FormNative($Native, [bool] $Quit = $true, [int] $ExitCode = 0) {
        if ($Quit) { Send-FormCommand $Native quit }
        $Native.Process.StandardInput.Close()
        $remaining = $Native.Process.StandardOutput.ReadToEndAsync()
        $null = $Native.Process.WaitForExitAsync().WaitAsync([TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult()
        $null = $Native.Text.Append($remaining.GetAwaiter().GetResult())
        $errors = $Native.Errors.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $script:FormEvidence ($Native.Label + '.stdout')), $Native.Text.ToString())
        [IO.File]::WriteAllText((Join-Path $script:FormEvidence ($Native.Label + '.stderr')), $errors)
        $Native.Process.ExitCode | Should -Be $ExitCode -Because $errors
        return $Native.Text.ToString()
    }
    function Dispose-FormNative($Native) {
        try {
            if (-not $Native.Process.HasExited) { $Native.Process.Kill($true); $Native.Process.WaitForExit() }
        } finally {
            [IO.File]::WriteAllText((Join-Path $script:FormEvidence ($Native.Label + '.partial.stdout')), $Native.Text.ToString())
            $Native.Process.Dispose()
        }
    }
    function Invoke-FormNative([string] $Label, [string[]] $Arguments, [string[]] $Commands, [int] $ExitCode = 0) {
        $native = Start-FormNative $Label $Arguments
        try {
            foreach ($command in $Commands) { Send-FormCommand $native $command }
            return Stop-FormNative $native $false $ExitCode
        } finally { Dispose-FormNative $native }
    }
    function New-FormProfile([string] $Name, [bool] $Fractional = $false) {
        $root = Join-Path $script:FormEvidence $Name
        $rns = Join-Path $root selected-rns
        $profile = Join-Path $root profile
        $null = New-Item $rns -ItemType Directory -Force
        $text = "# Selected RNS original`r`n[reticulum]`r`n share_instance = no # retained`r`n enable_transport = no`r`n[logging]`r`n loglevel = 0`r`n[interfaces]`r`n"
        [IO.File]::WriteAllText((Join-Path $rns config), $text)
        $null = Invoke-FormNative "$Name-init" @('--init', '--config', $profile) @()
        $settingsPath = Join-Path $profile settings.json
        $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
        $settings.clientAnnounceAtStart = $false
        $settings.periodicLxmfSync = $false
        if (-not $Fractional) { $settings.nodePageRefreshIntervalMinutes = 0; $settings.nodeFileRefreshIntervalMinutes = 0 }
        $settings | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding utf8NoBOM
        return @{Root=$root;Profile=$profile;Rns=$rns;Original=$text;Settings=$settingsPath;
            Source=(Join-Path $profile config);Arguments=@('--config',$profile,'--rnsconfig',$rns)}
    }
}

Describe 'Executed-primary bounded configuration forms and ConfigObj persistence' {
    It 'T460 native opt-in typed sections confirm cancel reset save restart and identity survive' {
        if ($env:DOTRETICULUM_NOMADNET_FORMS -ne '1' -or -not $IsWindows) {
            Set-ItResult -Skipped -Because 'Enable selected Windows NativeAOT acceptance with DOTRETICULUM_NOMADNET_FORMS=1.'
            return
        }
        (Invoke-FormNative forms-help @('--help') @()) | Should -Match 'config reset confirm'
        $p = New-FormProfile lifecycle
        $native = Start-FormNative forms-lifecycle $p.Arguments
        try {
            Send-FormCommand $native status
            $line = Receive-FormLine $native 'Reticulum identity: ([0-9a-f]{32})'
            $identity = [regex]::Match($line, 'Reticulum identity: ([0-9a-f]{32})').Groups[1].Value
            Send-FormCommand $native 'config section client'
            $null = Receive-FormLine $native 'Error:.*Explicitly enable'
            Send-FormCommand $native 'config primary enable'
            $null = Receive-FormLine $native 'Error:.*confirm'
            Test-Path $p.Source | Should -BeFalse
            Send-FormCommand $native 'config primary enable confirm'
            $null = Receive-FormLine $native 'Configuration source saved'
            [IO.File]::AppendAllText($p.Source, "[unknown]`nvalue = '''first`n# retained private synthetic`nlast'''`n")
            $before = (Get-FileHash $p.Source).Hash
            Send-FormCommand $native 'config section client'
            Send-FormCommand $native 'config set announce_interval 123'
            Send-FormCommand $native 'config cancel'
            $null = Receive-FormLine $native 'Configuration cancelled'
            (Get-FileHash $p.Source).Hash | Should -BeExactly $before
            Send-FormCommand $native 'config section client'
            Send-FormCommand $native 'config set announce_interval 180'
            Send-FormCommand $native 'config save'
            $null = Receive-FormLine $native 'Error:.*confirm'
            (Get-FileHash $p.Source).Hash | Should -BeExactly $before
            Send-FormCommand $native 'config save confirm'
            $null = Receive-FormLine $native 'Section saved'
            Send-FormCommand $native 'config cancel'
            Send-FormCommand $native 'node-config enable true'
            $null = Receive-FormLine $native 'Error:.*restart'
            $null = Stop-FormNative $native
        } finally { Dispose-FormNative $native }
        (Get-Content $p.Settings -Raw | ConvertFrom-Json).usePrimaryConfiguration | Should -BeTrue
        (Get-Content $p.Source -Raw) | Should -Match 'announce_interval = 180'
        $native = Start-FormNative forms-reset $p.Arguments
        try {
            Send-FormCommand $native 'config section client'
            Send-FormCommand $native 'config fields'
            $null = Receive-FormLine $native 'announce_interval = "180"'
            Send-FormCommand $native 'config reset'
            $null = Receive-FormLine $native 'Error:.*confirm'
            Send-FormCommand $native 'config reset confirm'
            Send-FormCommand $native 'config cancel'
            $null = Receive-FormLine $native 'Configuration cancelled'
            (Get-Content $p.Source -Raw) | Should -Match 'announce_interval = 180'
            Send-FormCommand $native 'config section client'
            Send-FormCommand $native 'config reset confirm'
            Send-FormCommand $native 'config save confirm'
            $null = Receive-FormLine $native 'Section saved'
            Send-FormCommand $native 'config cancel'
            foreach ($section in @('node','logging','textui','utilities','rrc')) {
                Send-FormCommand $native "config section $section"
                $null = Receive-FormLine $native "Configuration section: $section"
                Send-FormCommand $native 'config cancel'
                $null = Receive-FormLine $native 'Configuration cancelled'
            }
            Send-FormCommand $native status
            $text = Stop-FormNative $native
            $text | Should -Match "Reticulum identity: $identity"
            $text | Should -Match 'stored-only'
        } finally { Dispose-FormNative $native }
        $text = Invoke-FormNative forms-final $p.Arguments @('config section client','config fields','config cancel','config primary disable confirm','status','quit')
        $text | Should -Match 'announce_interval = "360"'
        $text | Should -Match "Reticulum identity: $identity"
        $text | Should -Not -Match 'Error:'
        (Get-Content $p.Settings -Raw | ConvertFrom-Json).usePrimaryConfiguration | Should -BeFalse
        (Get-Content $p.Settings -Raw | ConvertFrom-Json).clientAnnounceIntervalMinutes | Should -Be 360
        (Get-Content $p.Source -Raw) | Should -Match '(?s)\[unknown\].*# retained private synthetic.*last'
        @(Get-ChildItem $p.Root -Recurse -Force -Filter '.editor-*').Count | Should -Be 0
    }

    It 'T461 native selected RNS forms retain unrelated text activate owner policies preserve safety refusal and restart' {
        if ($env:DOTRETICULUM_NOMADNET_FORMS -ne '1' -or -not $IsWindows) {
            Set-ItResult -Skipped -Because 'Enable selected Windows NativeAOT configuration-form acceptance.'
            return
        }
        $p = New-FormProfile selected-rns
        $before = (Get-FileHash $p.Settings).Hash
        $text = Invoke-FormNative forms-rns $p.Arguments @('config section reticulum',
            'config set respond_to_probes true','config set use_implicit_proof false',
            'config set panic_on_interface_error true','config set shared_instance_port 65536',
            'config set instance_name "selected-synthetic"','config save confirm','config cancel','quit')
        $text | Should -Match 'Error:.*fixed safety ownership'
        $text | Should -Match 'Error:.*1–65535'
        $text | Should -Match 'Section saved'
        (Get-FileHash $p.Settings).Hash | Should -BeExactly $before
        $saved = Get-Content (Join-Path $p.Rns config) -Raw
        $saved | Should -Match 'share_instance = no # retained'
        $saved | Should -Match 'instance_name = selected-synthetic'
        $saved | Should -Match 'respond_to_probes = yes'
        $saved | Should -Match 'use_implicit_proof = no'
        $saved | Should -Match '\[logging\]\r\n loglevel = 0\r\n\[interfaces\]'
        Test-Path $p.Source | Should -BeFalse
        $text = Invoke-FormNative forms-rns-restart $p.Arguments @('config section reticulum','config fields','config cancel','quit')
        $text | Should -Match 'instance_name = "selected-synthetic"'
        $text | Should -Match 'respond_to_probes = "yes"'
        $text | Should -Match 'use_implicit_proof = "no"'
        $text | Should -Not -Match 'Error:'
    }

    It 'T462 native invalid source lease conflict reparse refusal and healthy recovery preserve files' {
        if ($env:DOTRETICULUM_NOMADNET_FORMS -ne '1' -or -not $IsWindows) {
            Set-ItResult -Skipped -Because 'Enable selected Windows NativeAOT configuration-form acceptance.'
            return
        }
        $p = New-FormProfile failures $true
        $before = (Get-FileHash $p.Settings).Hash
        $text = Invoke-FormNative forms-fractional $p.Arguments @('config primary enable confirm','status','quit')
        $text | Should -Match 'Error:.*whole minutes'
        $text | Should -Match 'Reticulum identity:'
        (Get-FileHash $p.Settings).Hash | Should -BeExactly $before
        Test-Path $p.Source | Should -BeFalse
        [IO.File]::WriteAllText($p.Source, "[client]`nenable_client = invalid`n")
        $text = Invoke-FormNative forms-invalid-source $p.Arguments @('config primary enable confirm','status','quit')
        $text | Should -Match 'Error:'
        (Get-FileHash $p.Settings).Hash | Should -BeExactly $before
        Remove-Item $p.Source
        $outside = Join-Path $p.Root outside
        $null = New-Item $outside -ItemType Directory
        [IO.File]::WriteAllText((Join-Path $outside retained), 'Synthetic outside unchanged')
        $null = New-Item $p.Source -ItemType Junction -Value $outside
        try {
            $text = Invoke-FormNative forms-reparse $p.Arguments @('config primary enable confirm','status','quit')
            $text | Should -Match 'Error:.*reparse'
            (Get-Content (Join-Path $outside retained) -Raw) | Should -BeExactly 'Synthetic outside unchanged'
            (Get-FileHash $p.Settings).Hash | Should -BeExactly $before
        } finally { [IO.Directory]::Delete($p.Source) }
        $settings = Get-Content $p.Settings -Raw | ConvertFrom-Json
        $settings.nodePageRefreshIntervalMinutes = 0; $settings.nodeFileRefreshIntervalMinutes = 0
        $settings | ConvertTo-Json -Depth 10 | Set-Content $p.Settings -Encoding utf8NoBOM
        $native = Start-FormNative forms-conflict $p.Arguments
        try {
            Send-FormCommand $native 'config primary enable confirm'
            $null = Receive-FormLine $native 'Configuration source saved'
            $other = Start-FormNative forms-lease $p.Arguments
            try { $null = Stop-FormNative $other $false 1 } finally { Dispose-FormNative $other }
            Send-FormCommand $native 'config section client'
            Send-FormCommand $native 'config set announce_interval 180'
            $null = Receive-FormLine $native 'Configuration field staged'
            [IO.File]::AppendAllText($p.Source, "# external mutation`n")
            $conflicted = (Get-FileHash $p.Source).Hash
            Send-FormCommand $native 'config save confirm'
            $null = Receive-FormLine $native 'Error:.*changed externally'
            (Get-FileHash $p.Source).Hash | Should -BeExactly $conflicted
            Send-FormCommand $native 'config cancel'
            Send-FormCommand $native 'config section client'
            Send-FormCommand $native 'config set announce_interval "not-a-number"'
            $null = Receive-FormLine $native 'Error:.*whole number'
            Send-FormCommand $native 'config set announce_interval 120'
            Send-FormCommand $native 'config save confirm'
            $null = Receive-FormLine $native 'Section saved'
            Send-FormCommand $native 'config cancel'
            Send-FormCommand $native status
            (Stop-FormNative $native) | Should -Match 'Reticulum identity:'
        } finally { Dispose-FormNative $native }
        $text = Invoke-FormNative forms-conflict-restart $p.Arguments @('config section client','config fields','config cancel','status','quit')
        $text | Should -Match 'announce_interval = "120"'
        $text | Should -Not -Match 'Error:'
        (Get-Content $p.Source -Raw) | Should -Match '# external mutation'
        @(Get-ChildItem $p.Root -Recurse -Force -Filter '.editor-*').Count | Should -Be 0
    }

    It 'T463 actual pinned primary section forms reset defaults ConfigObj and writer independently regenerate exactly' {
        if ($env:DOTRETICULUM_NOMADNET_FORM_REFERENCE -ne '1') {
            Set-ItResult -Skipped -Because 'Enable pinned primary execution with DOTRETICULUM_NOMADNET_FORM_REFERENCE=1 and selected reference paths.'
            return
        }
        $output = Join-Path $script:FormEvidence regenerated-forms.json
        & $env:DOTRETICULUM_PYTHON (Join-Path $script:FormRepository 'tests\Interop\GenerateNomadNetConfigurationFormVectors.py') `
            --output $output --observations (Join-Path $script:FormEvidence reference-imports.json) `
            --work (Join-Path $script:FormEvidence reference-work)
        $LASTEXITCODE | Should -Be 0
        (Get-FileHash $output).Hash | Should -BeExactly (Get-FileHash (Join-Path $script:FormRepository 'tests\NomadNet.Tests\NomadNetConfigurationFormVectors.json')).Hash
    }
}
