BeforeAll {
    $script:EditorRepository = if ($env:DOTRETICULUM_TEST_REPOSITORY) { $env:DOTRETICULUM_TEST_REPOSITORY }
        else { Split-Path (Split-Path $PSScriptRoot -Parent) -Parent }
    $script:EditorEvidence = Join-Path $script:EditorRepository ('artifacts\tests\nomad-editor\acceptance-' + [guid]::NewGuid().ToString('N'))
    $null = New-Item $script:EditorEvidence -ItemType Directory -Force

    function Invoke-EditorNative([string] $Label, [string[]] $Arguments, [string[]] $Commands, [int] $ExpectedExit = 0) {
        if (-not $env:DOTRETICULUM_NOMADNET_BINARY) { throw 'DOTRETICULUM_NOMADNET_BINARY is required.' }
        $info = [Diagnostics.ProcessStartInfo]::new($env:DOTRETICULUM_NOMADNET_BINARY)
        $info.UseShellExecute = $false
        $info.WorkingDirectory = $script:EditorEvidence
        $info.RedirectStandardInput = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $info.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
        $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
        foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $info
        $started = $false
        try {
            $started = $process.Start()
            if (-not $started) { throw 'Editor process did not start.' }
            $output = $process.StandardOutput.ReadToEndAsync()
            $errors = $process.StandardError.ReadToEndAsync()
            foreach ($command in $Commands) { $process.StandardInput.WriteLine($command) }
            $process.StandardInput.Close()
            $null = $process.WaitForExitAsync().WaitAsync([TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult()
            $text = $output.GetAwaiter().GetResult()
            $errorText = $errors.GetAwaiter().GetResult()
            [IO.File]::WriteAllText((Join-Path $script:EditorEvidence "$Label.stdout"), $text)
            [IO.File]::WriteAllText((Join-Path $script:EditorEvidence "$Label.stderr"), $errorText)
            $process.ExitCode | Should -Be $ExpectedExit -Because $errorText
            return $text
        }
        finally {
            if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
            $process.Dispose()
        }
    }

    function New-EditorProfile([string] $Name) {
        $root = Join-Path $script:EditorEvidence $Name
        $rns = Join-Path $root rns
        $profileDirectory = Join-Path $root profile
        $null = New-Item $rns -ItemType Directory -Force
        "[reticulum]`nshare_instance=no`nenable_transport=no`n[logging]`nloglevel=0`n[interfaces]`n" |
            Set-Content (Join-Path $rns config) -Encoding utf8NoBOM
        $null = Invoke-EditorNative "$Name-init" @('--init', '--config', $profileDirectory) @()
        $settingsPath = Join-Path $profileDirectory settings.json
        $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
        $settings.clientAnnounceAtStart = $false
        $settings.periodicLxmfSync = $false
        $settings | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding utf8NoBOM
        return @{ Root=$root; Profile=$profileDirectory; Rns=$rns;
            Arguments=@('--config', $profileDirectory, '--rnsconfig', $rns) }
    }
}

Describe 'Bounded original NomadNet editor and local guide workflows' {
    It 'T395 native page file permissions rename guide and restart workflows preserve identity and contents' {
        if ($env:DOTRETICULUM_NOMADNET_EDITOR -ne '1') {
            Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_EDITOR=1 and select the exact NativeAOT binary.'
            return
        }
        $help = Invoke-EditorNative help @('--help') @()
        $help | Should -Match 'edit open'
        $help | Should -Match 'guide'
        $p = New-EditorProfile authoring
        $hostingPath = Join-Path $p.Profile settings.json
        $hosting = Get-Content $hostingPath -Raw | ConvertFrom-Json
        $hosting.enableNode = $true
        $hosting.nodeAnnounceAtStart = $false
        $hosting | ConvertTo-Json -Depth 10 | Set-Content $hostingPath -Encoding utf8NoBOM
        $identity = 'a' * 32
        $text = ">Synthetic notice`nfirst $([char]0x03bb)`n"
        $json = ConvertTo-Json -InputObject $text -Compress
        $output = Invoke-EditorNative authoring $p.Arguments @(
            'edit mkdir pages', 'edit mkdir files', 'edit new pages notice.mu', "edit text $json",
            'edit find "first"', 'edit replace ["first","second"]', 'edit preview', 'edit close cancel',
            'edit save', 'node refresh', 'node paths', "edit permissions [`"$identity`"]", 'edit rename files renamed.txt',
            'node refresh', 'node paths',
            'edit close discard', 'guide topics', 'guide start', 'guide links', 'guide open 1',
            'guide hosting#access', 'guide back', 'utility identity', 'quit')
        $output | Should -Not -Match 'Error:'
        $output | Should -Match 'Close cancelled; buffer retained'
        $output | Should -Match 'Synthetic notice'
        $output | Should -Match 'Authoring local documents'
        $output | Should -Match 'Access lists'
        $output | Should -Match '/page/notice.mu'
        $output | Should -Match '/file/renamed.txt'
        $saved = Join-Path $p.Profile 'storage\files\renamed.txt'
        (Get-Content $saved -Raw) | Should -BeExactly ($text -replace 'first', 'second')
        (Get-Content "$saved.allowed" -Raw) | Should -BeExactly "$identity`n"
        Test-Path (Join-Path $p.Profile 'storage\pages\notice.mu') | Should -BeFalse
        $sourceHash = (Get-FileHash $saved).Hash
        $restart = Invoke-EditorNative authoring-restart $p.Arguments @(
            'edit resume', 'edit show', 'edit permissions public', 'edit save-as files copy.txt',
            'edit delete confirm', 'edit close discard', 'utility identity', 'quit')
        $restart | Should -Not -Match 'Error:'
        $restart | Should -Match 'files/renamed.txt'
        (Get-FileHash $saved).Hash | Should -BeExactly $sourceHash
        Test-Path "$saved.allowed" | Should -BeFalse
        Test-Path (Join-Path $p.Profile 'storage\files\copy.txt') | Should -BeFalse
        [regex]::Match($restart, 'Speed test: ([0-9a-f]{32})').Groups[1].Value |
            Should -BeExactly ([regex]::Match($output, 'Speed test: ([0-9a-f]{32})').Groups[1].Value)
        [regex]::Match($output, 'Speed test: ([0-9a-f]{32})').Success | Should -BeTrue
        @(Get-ChildItem $p.Profile -Recurse -Force -Filter '.editor-*').Count | Should -Be 0
    }

    It 'T396 native invalid input binary traversal nonoverwrite dirty EOF and reparse refusal recover without corrupting files' {
        if ($env:DOTRETICULUM_NOMADNET_EDITOR -ne '1') {
            Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_EDITOR=1 with the selected NativeAOT executable.'
            return
        }
        $p = New-EditorProfile failures
        $pages = Join-Path $p.Profile 'storage\pages'
        $null = New-Item $pages -ItemType Directory -Force
        [IO.File]::WriteAllBytes((Join-Path $pages binary.dat), [byte[]]@(255, 128))
        [IO.File]::WriteAllText((Join-Path $pages existing.mu), 'original')
        $before = (Get-FileHash (Join-Path $pages existing.mu)).Hash
        $output = Invoke-EditorNative failures $p.Arguments @(
            'edit open pages ../escape', 'edit open pages binary.dat', 'edit text "override"', 'edit save',
            'edit close discard', 'edit new pages existing.mu', 'edit open pages existing.mu',
            'edit text "unsaved"', 'quit', 'edit open pages another.mu', 'edit close discard',
            'edit new pages safe.mu', 'edit text "safe"', 'edit save',
            'edit save-as pages existing.mu', 'edit rename pages existing.mu', 'edit delete',
            'edit close discard', 'guide missing', 'guide start', 'utility identity', 'quit')
        $output | Should -Match 'Error:'
        $output | Should -Match 'Binary files are read-only'
        $output | Should -Match 'Unsaved changes'
        $output | Should -Match 'already exists'
        $output | Should -Match 'never overwrites'
        $output | Should -Match 'requires edit delete confirm'
        $output | Should -Match 'Local application guide'
        (Get-FileHash (Join-Path $pages existing.mu)).Hash | Should -BeExactly $before
        [Convert]::ToHexString([IO.File]::ReadAllBytes((Join-Path $pages binary.dat))) | Should -BeExactly 'FF80'
        $null = Invoke-EditorNative dirty-eof $p.Arguments @('edit open pages safe.mu', 'edit text "lost draft"') 1
        (Get-Content (Join-Path $pages safe.mu) -Raw) | Should -BeExactly 'safe'
        $outside = Join-Path $p.Root outside
        $null = New-Item $outside -ItemType Directory
        $link = Join-Path $pages linked
        if ($IsWindows) { $null = New-Item $link -ItemType Junction -Target $outside }
        elseif ($IsLinux) { $null = New-Item $link -ItemType SymbolicLink -Target $outside }
        else { throw 'This native editor gate supports Windows/Linux only; do not silently pass another host.' }
        try {
            $reparse = Invoke-EditorNative reparse $p.Arguments @(
                'edit new pages linked/escape.mu', 'edit list pages', 'utility identity', 'quit')
            $reparse | Should -Match 'cannot traverse symbolic links or reparse points'
            @(Get-ChildItem $outside -Force).Count | Should -Be 0
        }
        finally { [IO.Directory]::Delete($link) }
        @(Get-ChildItem $p.Profile -Recurse -Force -Filter '.editor-*').Count | Should -Be 0
    }

    It 'T397 regenerates byte-identical actual-primary editor observations without copying guide assets' {
        if ($env:DOTRETICULUM_NOMADNET_EDITOR_REFERENCE -ne '1') {
            Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_EDITOR_REFERENCE=1 with the verified pinned primary Python environment.'
            return
        }
        foreach ($name in 'DOTRETICULUM_PYTHON', 'DOTRETICULUM_UPSTREAM', 'DOTRETICULUM_LXMF_UPSTREAM', 'DOTRETICULUM_NOMADNET_SOURCE') {
            if (-not [Environment]::GetEnvironmentVariable($name)) { throw "$name is required." }
        }
        $env:PYTHONPATH = @($env:DOTRETICULUM_UPSTREAM, $env:DOTRETICULUM_LXMF_UPSTREAM,
            $env:DOTRETICULUM_NOMADNET_SOURCE) -join [IO.Path]::PathSeparator
        $fixture = Join-Path $script:EditorEvidence reference.json
        & $env:DOTRETICULUM_PYTHON (Join-Path $script:EditorRepository 'tests\Interop\GenerateNomadNetEditorVectors.py') `
            --output $fixture --observations (Join-Path $script:EditorEvidence imports.json) `
            --work (Join-Path $script:EditorEvidence reference-work)
        $LASTEXITCODE | Should -Be 0
        (Get-FileHash $fixture).Hash | Should -BeExactly (
            Get-FileHash (Join-Path $script:EditorRepository 'tests\NomadNet.Tests\NomadNetEditorVectors.json')).Hash
    }

    It 'T399 native configuration validates rollback and restart without changing the running service identity' {
        if ($env:DOTRETICULUM_NOMADNET_EDITOR -ne '1') {
            Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_EDITOR=1 and select the exact NativeAOT executable.'
            return
        }
        $p = New-EditorProfile configuration
        $settings = Join-Path $p.Profile settings.json
        [IO.File]::WriteAllBytes($settings, [byte[]](@(239, 187, 191) + [IO.File]::ReadAllBytes($settings)))
        $before = (Get-FileHash $settings).Hash
        $invalid = Invoke-EditorNative config-invalid $p.Arguments @(
            'config edit', 'edit text "{\"schemaVersion\":2,\"displayName\":\"invalid\"}"', 'config validate', 'edit save',
            'edit close discard', 'utility identity', 'quit')
        $invalid | Should -Match 'Error:'
        (Get-FileHash $settings).Hash | Should -BeExactly $before
        $output = Invoke-EditorNative config-save $p.Arguments @(
            'config edit', 'edit replace-all ["NomadNet user","Synthetic editor"]', 'config validate', 'edit save',
            'edit close discard', 'clipboard on', 'client announce', 'config show', 'utility identity', 'quit')
        [regex]::Matches($output, 'Error:').Count | Should -Be 1
        $output | Should -Match 'Configuration was edited for restart; restart before changing runtime settings'
        $output | Should -Match 'Configuration buffer valid'
        $output | Should -Match 'Restart NomadNet'
        (Get-Content $settings -Raw | ConvertFrom-Json).displayName | Should -BeExactly 'Synthetic editor'
        (Get-Content $settings -Raw | ConvertFrom-Json).clipboardCopy | Should -BeFalse
        (Get-Content $settings -Raw | ConvertFrom-Json).lastClientAnnounce | Should -BeNullOrEmpty
        $restart = Invoke-EditorNative config-restart $p.Arguments @('edit resume', 'edit show', 'edit close discard', 'utility identity', 'quit')
        $restart | Should -Not -Match 'Error:'
        $restart | Should -Match 'Synthetic editor'
        [regex]::Match($restart, 'Speed test: ([0-9a-f]{32})').Groups[1].Value |
            Should -BeExactly ([regex]::Match($invalid, 'Speed test: ([0-9a-f]{32})').Groups[1].Value)
        [regex]::Match($invalid, 'Speed test: ([0-9a-f]{32})').Success | Should -BeTrue
    }
}
