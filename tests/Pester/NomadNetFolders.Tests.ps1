BeforeAll {
    $script:FolderRepository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $script:FolderEvidence = if ($env:DOTRETICULUM_NOMADNET_FOLDER_EVIDENCE) {
        $env:DOTRETICULUM_NOMADNET_FOLDER_EVIDENCE
    } else { Join-Path $script:FolderRepository ('artifacts\tests\nomad-folders\acceptance-' + [guid]::NewGuid().ToString('N')) }
    $null = New-Item $script:FolderEvidence -ItemType Directory -Force

    function Start-FolderNative([string] $Label, [string[]] $Arguments) {
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
        return @{ Process=$process; Errors=$process.StandardError.ReadToEndAsync();
            Text=[Text.StringBuilder]::new(); Label=$Label }
    }

    function Send-FolderCommand($Native, [string] $Command) {
        $Native.Process.StandardInput.WriteLine($Command)
        $Native.Process.StandardInput.Flush()
    }

    function Receive-FolderLine($Native, [string] $Pattern) {
        for ($count = 0; $count -lt 4096; $count++) {
            $line = $Native.Process.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult()
            if ($null -eq $line) { throw "Native input ended before $Pattern. $($Native.Text)" }
            if ($Native.Text.Length + $line.Length -gt 2000000) { throw 'Native output exceeded the evidence bound.' }
            $null = $Native.Text.AppendLine($line)
            if ($line -match $Pattern) { return $line }
        }
        throw "Native line limit exceeded before $Pattern."
    }

    function Get-FolderToken($Native, [string] $Scope, [string] $Path) {
        Send-FolderCommand $Native "edit folder inspect $Scope $Path"
        $line = Receive-FolderLine $Native 'Folder plan: ([0-9a-f]{32});'
        return [regex]::Match($line, 'Folder plan: ([0-9a-f]{32});').Groups[1].Value
    }

    function Stop-FolderNative($Native, [bool] $Quit = $true) {
        if ($Quit) { Send-FolderCommand $Native quit }
        $Native.Process.StandardInput.Close()
        $rest = $Native.Process.StandardOutput.ReadToEndAsync()
        $null = $Native.Process.WaitForExitAsync().WaitAsync([TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult()
        $null = $Native.Text.Append($rest.GetAwaiter().GetResult())
        $errors = $Native.Errors.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $script:FolderEvidence ($Native.Label + '.stdout')), $Native.Text.ToString())
        [IO.File]::WriteAllText((Join-Path $script:FolderEvidence ($Native.Label + '.stderr')), $errors)
        $Native.Process.ExitCode | Should -Be 0 -Because $errors
        return $Native.Text.ToString()
    }

    function Dispose-FolderNative($Native) {
        try {
            if (-not $Native.Process.HasExited) { $Native.Process.Kill($true); $Native.Process.WaitForExit() }
        } finally {
            [IO.File]::WriteAllText((Join-Path $script:FolderEvidence ($Native.Label + '.partial.stdout')), $Native.Text.ToString())
            $Native.Process.Dispose()
        }
    }

    function New-FolderProfile([string] $Name) {
        $root = Join-Path $script:FolderEvidence $Name
        $rns = Join-Path $root rns
        $profile = Join-Path $root profile
        $null = New-Item $rns -ItemType Directory -Force
        "[reticulum]`n share_instance = no`n enable_transport = no`n[logging]`n loglevel = 0`n[interfaces]`n" |
            Set-Content (Join-Path $rns config) -Encoding utf8NoBOM
        $native = Start-FolderNative "$Name-init" @('--init', '--config', $profile)
        try { $null = Stop-FolderNative $native $false } finally { Dispose-FolderNative $native }
        $settingsPath = Join-Path $profile settings.json
        $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
        $settings.clientAnnounceAtStart = $false
        $settings.periodicLxmfSync = $false
        $settings.enableNode = $true
        $settings.nodeAnnounceAtStart = $false
        $settings | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding utf8NoBOM
        return @{Root=$root;Profile=$profile;Pages=(Join-Path $profile 'storage\pages');
            Files=(Join-Path $profile 'storage\files');Arguments=@('--config',$profile,'--rnsconfig',$rns)}
    }
}

Describe 'Bounded source-observed NomadNet folder create rename move and removal' {
    It 'T427 native folders preserve content permissions selection hosting and identity through moves removals and restarts' {
        if ($env:DOTRETICULUM_NOMADNET_FOLDERS -ne '1' -or -not $IsWindows) {
            Set-ItResult -Skipped -Because 'Enable Windows selected NativeAOT acceptance with DOTRETICULUM_NOMADNET_FOLDERS=1.'
            return
        }
        $help = Start-FolderNative help @('--help')
        try { (Stop-FolderNative $help $false) | Should -Match 'edit folder remove' } finally { Dispose-FolderNative $help }
        $p = New-FolderProfile lifecycle
        $native = Start-FolderNative lifecycle $p.Arguments
        try {
            foreach ($command in @('edit mkdir pages source/sub', 'edit mkdir pages source/empty', 'edit mkdir files archive',
                'edit new pages source/notice.mu', 'edit text ">Synthetic folder notice\nOriginal sample.\n"', 'edit save',
                ('edit permissions ["' + ('a' * 32) + '"]'))) { Send-FolderCommand $native $command }
            $token = Get-FolderToken $native pages source
            Send-FolderCommand $native "edit folder rename $token pages renamed"
            $null = Receive-FolderLine $native 'Folder moved;'
            Send-FolderCommand $native 'edit close discard'
            $token = Get-FolderToken $native pages renamed
            Send-FolderCommand $native "edit folder move $token files archive/moved"
            $null = Receive-FolderLine $native 'Folder moved;'
            foreach ($command in @('edit resume','edit show','node refresh','node paths','status')) { Send-FolderCommand $native $command }
            $line = Receive-FolderLine $native 'Reticulum identity: ([0-9a-f]{32})'
            $identity = [regex]::Match($line, 'Reticulum identity: ([0-9a-f]{32})').Groups[1].Value
            $text = Stop-FolderNative $native
            $text | Should -Not -Match 'Error:'
            $text | Should -Match 'Synthetic folder notice'
            $text | Should -Match '/file/archive/moved/notice.mu'
            (Get-Content (Join-Path $p.Files 'archive\moved\notice.mu') -Raw) | Should -BeExactly ">Synthetic folder notice`nOriginal sample.`n"
            (Get-Content (Join-Path $p.Files 'archive\moved\notice.mu.allowed') -Raw) | Should -BeExactly (('a' * 32) + "`n")
        } finally { Dispose-FolderNative $native }
        $native = Start-FolderNative lifecycle-remove $p.Arguments
        try {
            Send-FolderCommand $native 'edit resume'
            Send-FolderCommand $native 'edit show'
            $token = Get-FolderToken $native files archive/moved
            Send-FolderCommand $native "edit folder remove $token confirm"
            $null = Receive-FolderLine $native 'Folder removed;'
            foreach ($command in @('edit status','node refresh','status')) { Send-FolderCommand $native $command }
            $text = Stop-FolderNative $native
            $text | Should -Not -Match 'Error:'
            $text | Should -Match 'Synthetic folder notice'
            $text | Should -Match 'Editor: closed/'
            $text | Should -Match "Reticulum identity: $identity"
            Test-Path (Join-Path $p.Files 'archive\moved') | Should -BeFalse
        } finally { Dispose-FolderNative $native }
        $native = Start-FolderNative lifecycle-after-remove $p.Arguments
        try {
            foreach ($command in @('edit resume','status')) { Send-FolderCommand $native $command }
            $text = Stop-FolderNative $native
            $text | Should -Match 'No valid saved editor selection'
            $text | Should -Match "Reticulum identity: $identity"
            @(Get-ChildItem $p.Files -Directory -Force -Recurse -Filter '.editor-folder-delete-*').Count | Should -Be 0
        } finally { Dispose-FolderNative $native }
    }

    It 'T428 native folder failures confirmations conflicts cancellation and reparse refusal preserve files and recover' {
        if ($env:DOTRETICULUM_NOMADNET_FOLDERS -ne '1' -or -not $IsWindows) {
            Set-ItResult -Skipped -Because 'Requires selected Windows NativeAOT and actual Windows deny-delete sharing/junction behavior.'
            return
        }
        $p = New-FolderProfile failure
        $native = Start-FolderNative failure $p.Arguments
        try {
            foreach ($command in @('edit mkdir pages source/sub','edit mkdir files occupied',
                'edit new pages source/notice.mu','edit text "Synthetic retained\n"','edit save','edit close discard')) {
                Send-FolderCommand $native $command
            }
            $token = Get-FolderToken $native pages source
            Send-FolderCommand $native "edit folder remove $token"
            $null = Receive-FolderLine $native 'Error:'
            Send-FolderCommand $native 'edit folder remove wrong confirm'
            $null = Receive-FolderLine $native 'Error:'
            $notice = Join-Path $p.Pages 'source\notice.mu'
            [IO.File]::WriteAllText($notice, "External writer wins`n")
            Send-FolderCommand $native "edit folder remove $token confirm"
            $null = Receive-FolderLine $native 'Error:.*Folder changed'
            (Get-Content $notice -Raw) | Should -BeExactly "External writer wins`n"
            $token = Get-FolderToken $native pages source
            Send-FolderCommand $native "edit folder move $token files occupied"
            $null = Receive-FolderLine $native 'Error:.*never overwrite'
            $token = Get-FolderToken $native pages source
            $locked = [IO.FileStream]::new($notice, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            try {
                Send-FolderCommand $native "edit folder remove $token confirm"
                $null = Receive-FolderLine $native 'Error:'
                Test-Path $notice | Should -BeTrue
            } finally { $locked.Dispose() }
            $outside = Join-Path $p.Root outside
            $null = New-Item $outside -ItemType Directory
            [IO.File]::WriteAllText((Join-Path $outside untouched), 'Outside retained')
            $junction = Join-Path $p.Pages 'source\junction'
            $null = New-Item $junction -ItemType Junction -Target $outside
            try {
                Send-FolderCommand $native 'edit folder inspect pages source'
                $null = Receive-FolderLine $native 'Error:.*symbolic links or reparse'
            } finally { [IO.Directory]::Delete($junction) }
            (Get-Content (Join-Path $outside untouched) -Raw) | Should -BeExactly 'Outside retained'
            foreach ($command in @('edit folder inspect pages .','edit folder inspect pages ../escape','edit mkdir config invalid')) {
                Send-FolderCommand $native $command
                $null = Receive-FolderLine $native 'Error:'
            }
            $token = Get-FolderToken $native pages source
            Send-FolderCommand $native 'edit folder cancel'
            $null = Receive-FolderLine $native 'Folder plan cancelled'
            Send-FolderCommand $native "edit folder remove $token confirm"
            $null = Receive-FolderLine $native 'Error:'
            $token = Get-FolderToken $native pages source
            Send-FolderCommand $native "edit folder move $token files recovered"
            $null = Receive-FolderLine $native 'Folder moved;'
            foreach ($command in @('edit resume','edit show','status')) { Send-FolderCommand $native $command }
            $text = Stop-FolderNative $native
            $text | Should -Match 'External writer wins'
            $identity = [regex]::Match($text, 'Reticulum identity: ([0-9a-f]{32})').Groups[1].Value
            $identity.Length | Should -Be 32
            @(Get-ChildItem $p.Pages -Force -Directory -Filter '.editor-folder-delete-*').Count | Should -Be 0
        } finally { Dispose-FolderNative $native }
        $native = Start-FolderNative failure-restart $p.Arguments
        try {
            foreach ($command in @('edit resume','edit show','status')) { Send-FolderCommand $native $command }
            $text = Stop-FolderNative $native
            $text | Should -Not -Match 'Error:'
            $text | Should -Match 'External writer wins'
            $text | Should -Match "Reticulum identity: $identity"
        } finally { Dispose-FolderNative $native }
    }

    It 'T429 regenerates byte-identical actual-primary folder observation receipt without copying assets' {
        if ($env:DOTRETICULUM_NOMADNET_FOLDER_REFERENCE -ne '1') {
            Set-ItResult -Skipped -Because 'Enable pinned primary execution with DOTRETICULUM_NOMADNET_FOLDER_REFERENCE=1 and select reference paths.'
            return
        }
        $output = Join-Path $script:FolderEvidence regenerated-folders.json
        & $env:DOTRETICULUM_PYTHON (Join-Path $script:FolderRepository 'tests\Interop\GenerateNomadNetFolderVectors.py') `
            --output $output --observations (Join-Path $script:FolderEvidence reference-imports.json) `
            --work (Join-Path $script:FolderEvidence reference-work)
        $LASTEXITCODE | Should -Be 0
        (Get-FileHash $output).Hash | Should -BeExactly (Get-FileHash (Join-Path $script:FolderRepository 'tests\NomadNet.Tests\NomadNetFolderVectors.json')).Hash
    }
}
