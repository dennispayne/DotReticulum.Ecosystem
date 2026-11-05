BeforeAll {
    $script:PermissionRepository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $script:PermissionEvidence = if ($env:DOTRETICULUM_NOMADNET_PERMISSION_EVIDENCE) {
        $env:DOTRETICULUM_NOMADNET_PERMISSION_EVIDENCE
    } else { Join-Path $script:PermissionRepository ('artifacts\tests\nomad-folder-permissions\acceptance-' + [guid]::NewGuid().ToString('N')) }
    $null = New-Item $script:PermissionEvidence -ItemType Directory -Force

    function Start-PermissionNative([string] $Label, [string[]] $Arguments) {
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
    function Send-PermissionCommand($Native, [string] $Command) {
        $Native.Process.StandardInput.WriteLine($Command)
        $Native.Process.StandardInput.Flush()
    }
    function Receive-PermissionLine($Native, [string] $Pattern) {
        for ($count = 0; $count -lt 4096; $count++) {
            $line = $Native.Process.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult()
            if ($null -eq $line) { throw "Native input ended before $Pattern. $($Native.Text)" }
            if ($Native.Text.Length + $line.Length -gt 2000000) { throw 'Native output exceeded the evidence bound.' }
            $null = $Native.Text.AppendLine($line)
            if ($line -match $Pattern) { return $line }
        }
        throw "Native line limit exceeded before $Pattern."
    }
    function Get-PermissionToken($Native) {
        Send-PermissionCommand $Native 'edit folder permissions inspect pages tree'
        $line = Receive-PermissionLine $Native 'Folder permissions: ([0-9a-f]{32});'
        return [regex]::Match($line, 'Folder permissions: ([0-9a-f]{32});').Groups[1].Value
    }
    function Stop-PermissionNative($Native, [bool] $Quit = $true) {
        if ($Quit) { Send-PermissionCommand $Native quit }
        $Native.Process.StandardInput.Close()
        $remaining = $Native.Process.StandardOutput.ReadToEndAsync()
        $null = $Native.Process.WaitForExitAsync().WaitAsync([TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult()
        $null = $Native.Text.Append($remaining.GetAwaiter().GetResult())
        $errors = $Native.Errors.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $script:PermissionEvidence ($Native.Label + '.stdout')), $Native.Text.ToString())
        [IO.File]::WriteAllText((Join-Path $script:PermissionEvidence ($Native.Label + '.stderr')), $errors)
        $Native.Process.ExitCode | Should -Be 0 -Because $errors
        return $Native.Text.ToString()
    }
    function Dispose-PermissionNative($Native) {
        try {
            if (-not $Native.Process.HasExited) { $Native.Process.Kill($true); $Native.Process.WaitForExit() }
        } finally {
            [IO.File]::WriteAllText((Join-Path $script:PermissionEvidence ($Native.Label + '.partial.stdout')), $Native.Text.ToString())
            $Native.Process.Dispose()
        }
    }
    function New-PermissionProfile([string] $Name) {
        $root = Join-Path $script:PermissionEvidence $Name
        $rns = Join-Path $root rns
        $profile = Join-Path $root profile
        $null = New-Item $rns -ItemType Directory -Force
        "[reticulum]`n share_instance = no`n enable_transport = no`n[logging]`n loglevel = 0`n[interfaces]`n" |
            Set-Content (Join-Path $rns config) -Encoding utf8NoBOM
        $native = Start-PermissionNative "$Name-init" @('--init', '--config', $profile)
        try { $null = Stop-PermissionNative $native $false } finally { Dispose-PermissionNative $native }
        $settingsPath = Join-Path $profile settings.json
        $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
        $settings.clientAnnounceAtStart = $false
        $settings.periodicLxmfSync = $false
        $settings | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding utf8NoBOM
        $pages = Join-Path $profile 'storage\pages'
        $tree = Join-Path $pages tree
        $null = New-Item (Join-Path $tree nested) -ItemType Directory -Force
        [IO.File]::WriteAllText((Join-Path $tree a), "Synthetic retained`n")
        [IO.File]::WriteAllBytes((Join-Path $tree 'nested\b'), [byte[]]@(0,255,13,10))
        [IO.File]::WriteAllText((Join-Path $tree a.allowed), (('a' * 32) + "`n"))
        [IO.File]::WriteAllText((Join-Path $tree 'nested\b.allowed'), (('a' * 32) + "`n"))
        [IO.File]::WriteAllText((Join-Path $tree orphan.allowed), 'Unrelated orphan policy')
        return @{Root=$root;Profile=$profile;Pages=$pages;Tree=$tree;Arguments=@('--config',$profile,'--rnsconfig',$rns)}
    }
}

Describe 'Bounded source-observed recursive NomadNet folder permissions' {
    It 'T437 native recursive summaries confirmed access replacement public conversion and restarts preserve bytes policies and identity' {
        if ($env:DOTRETICULUM_NOMADNET_PERMISSIONS -ne '1' -or -not $IsWindows) {
            Set-ItResult -Skipped -Because 'Enable selected Windows NativeAOT acceptance with DOTRETICULUM_NOMADNET_PERMISSIONS=1.'
            return
        }
        $help = Start-PermissionNative help @('--help')
        try { (Stop-PermissionNative $help $false) | Should -Match 'edit folder permissions public' } finally { Dispose-PermissionNative $help }
        $p = New-PermissionProfile lifecycle
        $settingsHash = (Get-FileHash (Join-Path $p.Profile settings.json)).Hash
        $contentHash = (Get-FileHash (Join-Path $p.Tree 'nested\b')).Hash
        $native = Start-PermissionNative lifecycle $p.Arguments
        try {
            Send-PermissionCommand $native 'edit open pages tree/a'
            $token = Get-PermissionToken $native
            $native.Text.ToString() | Should -Match 'total=2; restricted=2; common="aaaaaaaa'
            Send-PermissionCommand $native "edit folder permissions apply $token confirm [`"$('b' * 32)`",`"$('c' * 32)`"]"
            $null = Receive-PermissionLine $native 'Folder permissions committed: restricted'
            Send-PermissionCommand $native 'edit text "Synthetic retained after bulk\n"'
            Send-PermissionCommand $native 'edit save'
            Send-PermissionCommand $native status
            $line = Receive-PermissionLine $native 'Reticulum identity: ([0-9a-f]{32})'
            $identity = [regex]::Match($line, 'Reticulum identity: ([0-9a-f]{32})').Groups[1].Value
            (Stop-PermissionNative $native) | Should -Not -Match 'Error:'
        } finally { Dispose-PermissionNative $native }
        (Get-Content (Join-Path $p.Tree a.allowed) -Raw) | Should -BeExactly (('b' * 32) + "`n" + ('c' * 32) + "`n")
        $native = Start-PermissionNative public-restart $p.Arguments
        try {
            Send-PermissionCommand $native 'edit resume'
            Send-PermissionCommand $native 'edit show'
            $token = Get-PermissionToken $native
            Send-PermissionCommand $native "edit folder permissions public $token"
            $null = Receive-PermissionLine $native 'Error:'
            Test-Path (Join-Path $p.Tree a.allowed) | Should -BeTrue
            Send-PermissionCommand $native "edit folder permissions public $token confirm"
            $null = Receive-PermissionLine $native 'Folder permissions committed: public'
            $null = Get-PermissionToken $native
            Send-PermissionCommand $native status
            $text = Stop-PermissionNative $native
            $text | Should -Match 'total=2; restricted=0; common=null'
            $text | Should -Match 'Synthetic retained after bulk'
            $text | Should -Match "Reticulum identity: $identity"
        } finally { Dispose-PermissionNative $native }
        $native = Start-PermissionNative final-restart $p.Arguments
        try {
            $null = Get-PermissionToken $native
            Send-PermissionCommand $native status
            $text = Stop-PermissionNative $native
            $text | Should -Not -Match 'Error:'
            $text | Should -Match 'total=2; restricted=0; common=null'
            $text | Should -Match "Reticulum identity: $identity"
        } finally { Dispose-PermissionNative $native }
        (Get-FileHash (Join-Path $p.Profile settings.json)).Hash | Should -BeExactly $settingsHash
        (Get-FileHash (Join-Path $p.Tree 'nested\b')).Hash | Should -BeExactly $contentHash
        (Get-Content (Join-Path $p.Tree orphan.allowed) -Raw) | Should -BeExactly 'Unrelated orphan policy'
        Test-Path (Join-Path $p.Tree a.allowed) | Should -BeFalse
        Test-Path (Join-Path $p.Tree 'nested\b.allowed') | Should -BeFalse
        @(Get-ChildItem $p.Pages -Directory -Force -Filter '.editor-permissions-*').Count | Should -Be 0
    }

    It 'T438 native confirmations cancellation conflicts locked-policy rollback reparse refusal and recovery preserve unrelated files' {
        if ($env:DOTRETICULUM_NOMADNET_PERMISSIONS -ne '1' -or -not $IsWindows) {
            Set-ItResult -Skipped -Because 'Requires selected Windows NativeAOT and actual deny-delete sharing/junction behavior.'
            return
        }
        $p = New-PermissionProfile failures
        $native = Start-PermissionNative failures $p.Arguments
        try {
            $token = Get-PermissionToken $native
            Send-PermissionCommand $native "edit folder permissions public wrong confirm"
            $null = Receive-PermissionLine $native 'Error:'
            Send-PermissionCommand $native 'edit folder cancel'
            $null = Receive-PermissionLine $native 'Folder plan cancelled'
            Send-PermissionCommand $native "edit folder permissions public $token confirm"
            $null = Receive-PermissionLine $native 'Error:'
            $token = Get-PermissionToken $native
            [IO.File]::WriteAllText((Join-Path $p.Tree a), 'External change retained')
            Send-PermissionCommand $native "edit folder permissions public $token confirm"
            $null = Receive-PermissionLine $native 'Error:.*Folder changed'
            $token = Get-PermissionToken $native
            $locked = [IO.FileStream]::new((Join-Path $p.Tree 'nested\b.allowed'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            try {
                Send-PermissionCommand $native "edit folder permissions apply $token confirm [`"$('b' * 32)`"]"
                $null = Receive-PermissionLine $native 'Error:'
                (Get-Content (Join-Path $p.Tree a.allowed) -Raw) | Should -BeExactly (('a' * 32) + "`n")
            } finally { $locked.Dispose() }
            Send-PermissionCommand $native 'edit open pages tree/a'
            Send-PermissionCommand $native 'edit text "Unsaved retained"'
            $token = Get-PermissionToken $native
            Send-PermissionCommand $native "edit folder permissions public $token confirm"
            $null = Receive-PermissionLine $native 'Error:.*Save or discard'
            Send-PermissionCommand $native 'edit close discard'
            $outside = Join-Path $p.Root outside
            $null = New-Item $outside -ItemType Directory
            [IO.File]::WriteAllText((Join-Path $outside untouched), 'Outside retained')
            $junction = Join-Path $p.Tree junction
            $null = New-Item $junction -ItemType Junction -Target $outside
            try {
                Send-PermissionCommand $native 'edit folder permissions inspect pages tree'
                $null = Receive-PermissionLine $native 'Error:.*symbolic links or reparse'
            } finally { [IO.Directory]::Delete($junction) }
            $token = Get-PermissionToken $native
            Send-PermissionCommand $native "edit folder permissions apply $token confirm [`"$('b' * 32)`"]"
            $null = Receive-PermissionLine $native 'Folder permissions committed: restricted'
            # A new unexpected stage entry must survive nonrecursive cleanup, with an
            # explicit committed failure rather than a false rollback/success claim.
            foreach ($index in 0..199) { [IO.File]::WriteAllText((Join-Path $p.Tree "extra$index"), 'Synthetic extra') }
            $token = Get-PermissionToken $native
            $watcher = [IO.FileSystemWatcher]::new($p.Pages, '.editor-permissions-*')
            $watcher.NotifyFilter = [IO.NotifyFilters]::DirectoryName
            $eventId = 'nomad-permission-stage-' + [guid]::NewGuid().ToString('N')
            $null = Register-ObjectEvent $watcher Created -SourceIdentifier $eventId
            $watcher.EnableRaisingEvents = $true
            try {
                Send-PermissionCommand $native "edit folder permissions apply $token confirm [`"$('c' * 32)`"]"
                $created = Wait-Event -SourceIdentifier $eventId -Timeout 10
                $created | Should -Not -BeNullOrEmpty
                $stage = Join-Path $p.Pages $created.SourceEventArgs.Name
                $marker = Join-Path $stage operator-retained
                [IO.File]::WriteAllText($marker, 'Synthetic unexpected entry retained')
                $null = Receive-PermissionLine $native 'Error:.*Permissions committed; transaction cleanup incomplete'
                (Get-Content $marker -Raw) | Should -BeExactly 'Synthetic unexpected entry retained'
                (Get-Content (Join-Path $p.Tree a.allowed) -Raw) | Should -BeExactly (('c' * 32) + "`n")
                [IO.File]::Delete($marker)
                [IO.Directory]::Delete($stage)
            } finally {
                Unregister-Event -SourceIdentifier $eventId
                Remove-Event -SourceIdentifier $eventId -ErrorAction SilentlyContinue
                $watcher.Dispose()
            }
            $null = Get-PermissionToken $native
            Send-PermissionCommand $native status
            $line = Receive-PermissionLine $native 'Reticulum identity: ([0-9a-f]{32})'
            $identity = [regex]::Match($line, 'Reticulum identity: ([0-9a-f]{32})').Groups[1].Value
            $null = Stop-PermissionNative $native
            (Get-Content (Join-Path $p.Tree a) -Raw) | Should -BeExactly 'External change retained'
            (Get-Content (Join-Path $outside untouched) -Raw) | Should -BeExactly 'Outside retained'
        } finally { Dispose-PermissionNative $native }
        $native = Start-PermissionNative recovery-restart $p.Arguments
        try {
            $null = Get-PermissionToken $native
            Send-PermissionCommand $native 'edit resume'
            Send-PermissionCommand $native 'edit show'
            Send-PermissionCommand $native status
            $text = Stop-PermissionNative $native
            $text | Should -Not -Match 'Error:'
            $text | Should -Match 'total=202; restricted=202; common="cccccccc'
            $text | Should -Match 'External change retained'
            $text | Should -Match "Reticulum identity: $identity"
        } finally { Dispose-PermissionNative $native }
        @(Get-ChildItem $p.Pages -Directory -Force -Filter '.editor-permissions-*').Count | Should -Be 0
    }

    It 'T439 independently regenerates byte-identical actual-primary recursive permission receipts including real confirmation callbacks' {
        if ($env:DOTRETICULUM_NOMADNET_PERMISSION_REFERENCE -ne '1') {
            Set-ItResult -Skipped -Because 'Enable pinned primary execution with DOTRETICULUM_NOMADNET_PERMISSION_REFERENCE=1 and select reference paths.'
            return
        }
        $output = Join-Path $script:PermissionEvidence regenerated-permissions.json
        & $env:DOTRETICULUM_PYTHON (Join-Path $script:PermissionRepository 'tests\Interop\GenerateNomadNetFolderPermissionVectors.py') `
            --output $output --observations (Join-Path $script:PermissionEvidence reference-imports.json) `
            --work (Join-Path $script:PermissionEvidence reference-work)
        $LASTEXITCODE | Should -Be 0
        (Get-FileHash $output).Hash | Should -BeExactly (Get-FileHash (Join-Path $script:PermissionRepository 'tests\NomadNet.Tests\NomadNetFolderPermissionVectors.json')).Hash
    }
}
