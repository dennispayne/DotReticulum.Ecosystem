Describe 'NomadNet offline primary profile import' -Tag 'ProfileImport' {
    It 'imports an actual primary profile and messages both ways with the unchanged full primary application after imported-identity restart' {
        if ($env:DOTRETICULUM_NOMADNET_PROFILE_IMPORT -ne '1') {
            Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_PROFILE_IMPORT=1 with the published binary and pinned primary Python environment.'
            return
        }
        foreach ($variable in 'DOTRETICULUM_NOMADNET_BINARY', 'DOTRETICULUM_PYTHON',
            'DOTRETICULUM_UPSTREAM', 'DOTRETICULUM_NOMADNET_SOURCE', 'DOTRETICULUM_NOMADNET_PROFILE_CONVERTER') {
            if (-not [Environment]::GetEnvironmentVariable($variable)) { throw "$variable is required." }
        }
        Test-Path $env:DOTRETICULUM_NOMADNET_PROFILE_CONVERTER -PathType Leaf | Should -BeTrue
        $root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        if ($env:DOTRETICULUM_TEST_REPOSITORY) { $root = $env:DOTRETICULUM_TEST_REPOSITORY }
        $work = Join-Path (Get-Location) "artifacts\tests\nomad-profile-import\acceptance-$([Guid]::NewGuid().ToString('N'))"
        $null = New-Item $work -ItemType Directory -Force
        $processes = [Collections.Generic.List[Diagnostics.Process]]::new()
        $pending = @{}
        $errors = @{}
        $names = @{}
        $start = {
            param([string] $name, [string] $binary, [string[]] $arguments)
            $info = [Diagnostics.ProcessStartInfo]::new($binary)
            $info.UseShellExecute = $false
            $info.RedirectStandardInput = $true
            $info.RedirectStandardOutput = $true
            $info.RedirectStandardError = $true
            $info.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
            $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
            foreach ($argument in $arguments) { $info.ArgumentList.Add($argument) }
            $info.Environment['PYTHONPATH'] = @($env:DOTRETICULUM_UPSTREAM,
                $env:DOTRETICULUM_NOMADNET_SOURCE, (Join-Path $root 'tests\Interop')) -join [IO.Path]::PathSeparator
            $info.Environment['PYTHON_CPU_COUNT'] = '2'
            $info.Environment['PATH'] = (Split-Path $env:DOTRETICULUM_NOMADNET_PROFILE_CONVERTER -Parent) +
                [IO.Path]::PathSeparator + $env:PATH
            foreach ($variable in 'TMP', 'TEMP', 'TMPDIR') { $info.Environment[$variable] = $work }
            $process = [Diagnostics.Process]::new()
            $process.StartInfo = $info
            $null = $process.Start()
            $processes.Add($process)
            $names[$process.Id] = $name
            $errors[$process.Id] = $process.StandardError.ReadToEndAsync()
            return $process
        }.GetNewClosure()
        $send = {
            param($process, [string] $text)
            $process.StandardInput.WriteLine($text)
            $process.StandardInput.Flush()
        }
        $read = {
            param($process, [string] $pattern, [int] $seconds = 60)
            $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
            while ([DateTime]::UtcNow -lt $deadline) {
                if (-not $pending.ContainsKey($process.Id)) { $pending[$process.Id] = $process.StandardOutput.ReadLineAsync() }
                try { $line = $pending[$process.Id].WaitAsync([TimeSpan]::FromMilliseconds(250)).GetAwaiter().GetResult() }
                catch [TimeoutException] { continue }
                $pending.Remove($process.Id)
                if ($null -eq $line) { throw "$($names[$process.Id]) closed stdout before '$pattern'; diagnostics $work" }
                Add-Content (Join-Path $work "$($names[$process.Id]).stdout") $line
                if ($line -match $pattern) { return $line }
            }
            throw "$($names[$process.Id]) timed out before '$pattern'; diagnostics $work"
        }.GetNewClosure()
        $parseEvent = {
            param([string] $line, [string] $context)
            $marker = $line.IndexOf('@@', [StringComparison]::Ordinal)
            if ($marker -lt 0) { return $null }
            try { $event = ConvertFrom-Json -InputObject $line.Substring($marker + 2) -NoEnumerate -ErrorAction Stop }
            catch { throw "Invalid framed JSON from $context; diagnostics $work; raw line: $line; $($_.Exception.Message)" }
            if ($null -eq $event -or $event.GetType() -ne [System.Management.Automation.PSCustomObject] -or
                $event.event -isnot [string] -or
                [string]::IsNullOrWhiteSpace($event.event)) {
                throw "Invalid framed event schema from $context; diagnostics $work; raw line: $line"
            }
            return $event
        }.GetNewClosure()
        $eventBacklogs = @{}
        $readEvent = {
            param($process, [string] $expected, [int] $seconds = 60)
            $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
            if (-not $eventBacklogs.ContainsKey($process.Id)) {
                $eventBacklogs[$process.Id] = [Collections.Generic.List[object]]::new()
            }
            $backlog = $eventBacklogs[$process.Id]
            for ($index = 0; $index -lt $backlog.Count; $index++) {
                if ($backlog[$index].event -eq $expected) {
                    $event = $backlog[$index]
                    $backlog.RemoveAt($index)
                    return $event
                }
            }
            while ([DateTime]::UtcNow -lt $deadline) {
                $remaining = [Math]::Max(1, [Math]::Ceiling(($deadline - [DateTime]::UtcNow).TotalSeconds))
                try { $line = & $read $process '.' $remaining }
                catch { throw "$($names[$process.Id]) awaiting framed event '$expected'; diagnostics $work; $($_.Exception.Message)" }
                $event = & $parseEvent $line "$($names[$process.Id]) awaiting '$expected'"
                if ($null -eq $event) { continue }
                if ($event.event -in 'error', 'failed') {
                    throw "$($names[$process.Id]) reported '$($event.event)' awaiting '$expected'; diagnostics $work; raw line: $line"
                }
                if ($event.event -eq $expected) { return $event }
                if ($backlog.Count -ge 32) {
                    throw "$($names[$process.Id]) exceeded 32 pending events awaiting '$expected'; diagnostics $work"
                }
                $backlog.Add($event)
            }
            throw "$($names[$process.Id]) timed out awaiting framed event '$expected'; diagnostics $work"
        }.GetNewClosure()
        $offline = {
            param([string] $name, [string] $binary, [string[]] $arguments)
            $process = & $start $name $binary $arguments
            $output = $process.StandardOutput.ReadToEndAsync()
            $process.WaitForExit(90000) | Should -BeTrue
            $text = $output.GetAwaiter().GetResult()
            $diagnostics = $errors[$process.Id].GetAwaiter().GetResult()
            $text | Set-Content (Join-Path $work "$name.stdout") -Encoding utf8
            $diagnostics | Set-Content (Join-Path $work "$name.stderr") -Encoding utf8
            @{ Code=$process.ExitCode; Text=$text; Error=$diagnostics }
        }.GetNewClosure()
        $snapshot = {
            param([string] $directory)
            @(Get-ChildItem $directory -File -Recurse | Sort-Object FullName | ForEach-Object {
                "$([IO.Path]::GetRelativePath($directory, $_.FullName)) $((Get-FileHash $_.FullName -Algorithm SHA256).Hash)"
            }) -join "`n"
        }
        try {
            (& $parseEvent '[2026-11-04 05:03:52] [Warning] startup warning@@{"event":"ready"}' 'regression').event |
                Should -BeExactly 'ready'
            (& $parseEvent '[2026-11-04 05:03:52] diagnostic with "event": "ready" but no frame' 'regression') |
                Should -BeNullOrEmpty
            { & $parseEvent '[2026-11-04] warning@@{"event":' 'regression' } | Should -Throw '*Invalid framed JSON*'
            { & $parseEvent '@@[{"event":"ready"}]' 'regression' } | Should -Throw '*Invalid framed event schema*'
            { & $parseEvent '@@{"event":7}' 'regression' } | Should -Throw '*Invalid framed event schema*'
            { & $parseEvent '@@{"notEvent":"ready"}' 'regression' } | Should -Throw '*Invalid framed event schema*'
            { & $parseEvent '@@{"event":"ready"} trailing garbage' 'regression' } | Should -Throw '*Invalid framed JSON*'
            $help = & $offline 'help' $env:DOTRETICULUM_NOMADNET_BINARY @('--help')
            $help.Code | Should -Be 0
            $help.Text | Should -Match '\-\-import-profile'
            $source = Join-Path $work 'primary'
            $target = Join-Path $work 'native'
            $fixture = Join-Path $work 'executed-primary.json'
            $generated = & $offline 'generator' $env:DOTRETICULUM_PYTHON @(
                (Join-Path $root 'tests\Interop\GenerateNomadNetProfileImportVectors.py'),
                '--profile', $source, '--rnsconfig', (Join-Path $work 'primary-rns'), '--output', $fixture)
            $generated.Code | Should -Be 0 -Because $generated.Error
            (Get-FileHash $fixture).Hash | Should -Be (Get-FileHash (Join-Path $root 'tests\NomadNet.Tests\NomadNetProfileImportVectors.json')).Hash
            $primary = ($generated.Text.Trim() -split "`n" | Select-Object -Last 1) | ConvertFrom-Json
            $before = & $snapshot $source
            $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
            $listener.Start()
            $port = $listener.LocalEndpoint.Port
            $listener.Stop()
            $selectedRns = Join-Path $work 'native-rns'
            $null = New-Item $selectedRns -ItemType Directory
            @"
[reticulum]
share_instance=No
enable_transport=No
[logging]
loglevel=4
[interfaces]
[[ProfilePeer]]
type=TCPClientInterface
enabled=Yes
target_host=127.0.0.1
target_port=$port
"@ | Set-Content (Join-Path $selectedRns 'config') -Encoding utf8
            $linked = Join-Path $work 'primary-link'
            if ($IsWindows) { $null = New-Item $linked -ItemType Junction -Target $source }
            else { $null = New-Item $linked -ItemType SymbolicLink -Target $source }
            try {
                $unsafe = & $offline 'linked-source-rejected' $env:DOTRETICULUM_NOMADNET_BINARY @(
                    '--import-profile', $linked, '--config', (Join-Path $work 'unsafe-target'), '--rnsconfig', $selectedRns)
                $unsafe.Code | Should -Be 1
                $unsafe.Error | Should -Match 'links/reparse points'
                Test-Path (Join-Path $work 'unsafe-target') | Should -BeFalse
            }
            finally { [IO.Directory]::Delete($linked) }
            $import = & $offline 'import' $env:DOTRETICULUM_NOMADNET_BINARY @(
                '--import-profile', $source, '--config', $target, '--rnsconfig', $selectedRns)
            $import.Code | Should -Be 0 -Because $import.Error
            $import.Text | Should -Match '4 messages, 2 attachments'
            (& $snapshot $source) | Should -BeExactly $before
            $state = Get-Content (Join-Path $target 'state.json') -Raw | ConvertFrom-Json
            $state.messages.Count | Should -Be 4
            foreach ($expected in (Get-Content $fixture -Raw | ConvertFrom-Json).cases) {
                $actual = @($state.messages.Where({ $_.content -eq $expected.content }))
                $actual.Count | Should -Be 1
                $actual[0].title | Should -BeExactly $expected.title
                $actual[0].historicalState | Should -Be $expected.state
                $actual[0].historicalMethod | Should -Be $expected.method
            }
            $state.messages.Where({ $_.status -eq 'paper' }).Count | Should -Be 1
            $media = $state.messages.Where({ $_.content -eq 'media-stripped body' })[0]
            $media.historicalAuthentication | Should -Be 'container-hash-mismatch-not-authenticated'
            $media.historicalSignatureValidated | Should -BeTrue
            $media.imageAttachmentIndex | Should -Be 1
            ($media.importedAttachmentNames -split "`n") | Should -Contain 'public.txt'
            $nativeFile = Join-Path $target "attachments\$($media.id)\0.bin"
            [IO.File]::ReadAllText($nativeFile) | Should -BeExactly 'public profile attachment'
            $stateHash = (Get-FileHash (Join-Path $target 'state.json')).Hash
            $conflict = & $offline 'non-overwrite' $env:DOTRETICULUM_NOMADNET_BINARY @(
                '--import-profile', $source, '--config', $target, '--rnsconfig', $selectedRns)
            $conflict.Code | Should -Be 1
            (Get-FileHash (Join-Path $target 'state.json')).Hash | Should -Be $stateHash
            $python = & $start 'python-peer' $env:DOTRETICULUM_PYTHON @(
                (Join-Path $root 'tests\Interop\nomadnet_live_peer.py'),
                '--config', (Join-Path $work 'python-peer'), '--tcp-server-port', "$port")
            $ready = & $readEvent $python 'ready'
            $native = & $start 'terminal' $env:DOTRETICULUM_NOMADNET_BINARY @('--config', $target)
            (& $read $native 'LXMF delivery address:') | Should -Match $primary.delivery
            & $send $native 'client status'
            & $read $native 'Client display name: Imported primary user' | Out-Null
            & $send $native 'contacts'
            & $read $native 'Profile peer' | Out-Null
            & $send $native 'conversations'
            & $read $native 'unread=2 failed=1 messages=4 Profile peer' | Out-Null
            & $read $native 'Conversations complete.' | Out-Null
            & $send $native 'images manual'
            & $read $native 'Inline images: manual.' | Out-Null
            & $send $native 'messages'
            & $read $native 'Messages complete.' | Out-Null
            & $send $native 'conversation images load'
            & $read $native '--- conversation image complete ---' | Out-Null
            & $send $native 'conversation images status'
            & $read $native 'previews=1' | Out-Null
            & $send $native "attachment $($media.id) 0 recovered.txt"
            & $read $native 'recovered.txt' | Out-Null
            [IO.File]::ReadAllText((Join-Path $target 'downloads\recovered.txt')) | Should -BeExactly 'public profile attachment'
            & $send $native "attachment $($media.id) 1 recovered.png"
            & $read $native 'recovered.png' | Out-Null
            (Get-FileHash (Join-Path $target 'downloads\recovered.png')).Hash |
                Should -Be (Get-FileHash (Join-Path $source "storage\attachments\$($media.id)\file_1")).Hash
            & $send $native "conversation active $($ready.destination)"
            & $read $native 'Conversation command complete.' | Out-Null
            & $send $native 'announce'
            & $send $python 'announce'
            & $readEvent $python 'announced' | Out-Null
            & $send $native "send $($ready.destination) imported-native-to-python"
            (& $readEvent $python 'received').content | Should -BeExactly 'imported-native-to-python'
            & $send $python "send $($primary.delivery) python-to-imported-native"
            & $readEvent $python 'delivered' | Out-Null
            & $read $native 'Conversation received:.*python-to-imported-native' | Out-Null
            & $send $native 'quit'
            $native.WaitForExit(20000) | Should -BeTrue
            $native.ExitCode | Should -Be 0
            & $send $python 'quit'
            $python.WaitForExit(10000) | Should -BeTrue
            $python.ExitCode | Should -Be 0
            $applicationProfile = Join-Path $work 'primary-application-peer'
            $applicationRns = Join-Path $work 'primary-application-rns'
            $null = New-Item $applicationProfile, $applicationRns -ItemType Directory
            @('[client]', 'user_interface=text', 'periodic_lxmf_sync=no', '[node]',
                'enable_node=yes', 'disable_propagation=yes', 'announce_at_start=no',
                '[textui]', 'colormode=256', '[logging]', 'loglevel=2', 'destination=console') |
                Set-Content (Join-Path $applicationProfile 'config') -Encoding utf8
            @('[reticulum]', 'share_instance=No', 'enable_transport=No', '[logging]', 'loglevel=2',
                '[interfaces]', '[[ProfileServer]]', 'type=TCPServerInterface', 'enabled=Yes',
                'listen_ip=127.0.0.1', "listen_port=$port") |
                Set-Content (Join-Path $applicationRns 'config') -Encoding utf8
            $application = & $start 'primary-application-peer' $env:DOTRETICULUM_PYTHON @(
                '-u', (Join-Path $root 'tests\Interop\nomadnet_live_node.py'),
                '--config', $applicationProfile, '--rnsconfig', $applicationRns)
            $applicationReady = & $readEvent $application 'ready'
            $applicationReady.revision | Should -Be 'f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2'
            { & $readEvent $application '__never_emitted__' 1 } |
                Should -Throw "*awaiting*'__never_emitted__'*"
            $identityHash = (Get-FileHash (Join-Path $target 'identity')).Hash
            $daemon = & $start 'daemon-restart' $env:DOTRETICULUM_NOMADNET_BINARY @('--config', $target, '--daemon')
            (& $read $daemon 'LXMF delivery address:') | Should -Match $primary.delivery
            & $read $daemon 'Daemon ready.' | Out-Null
            $busy = & $offline 'lease-conflict' $env:DOTRETICULUM_NOMADNET_BINARY @('--config', $target)
            $busy.Code | Should -Be 1
            $busy.Error | Should -Match 'already in use'
            Stop-Process -Id $daemon.Id -Force
            $daemon.WaitForExit(10000) | Should -BeTrue
            $restart = & $start 'terminal-restart' $env:DOTRETICULUM_NOMADNET_BINARY @('--config', $target)
            (& $read $restart 'LXMF delivery address:') | Should -Match $primary.delivery
            & $send $restart 'messages'
            & $read $restart 'python-to-imported-native' | Out-Null
            & $read $restart 'Messages complete.' | Out-Null
            & $send $application 'wait-network'
            & $readEvent $application 'network-ready' | Out-Null
            & $send $application 'observe-client-announces'
            & $readEvent $application 'observing-client-announces' | Out-Null
            & $send $restart 'announce'
            & $read $restart 'Announced' | Out-Null
            & $send $application "client-announces $($primary.delivery) 1"
            $observed = & $readEvent $application 'client-announces'
            $observed.delivery | Should -Be $primary.delivery
            & $send $application 'announce'
            & $readEvent $application 'announced' | Out-Null
            & $send $restart "conversation active $($applicationReady.delivery)"
            & $read $restart 'Conversation command complete.' | Out-Null
            $nativeBody = 'imported-restarted-to-primary'
            $primaryBody = 'primary-to-imported-restarted'
            & $send $restart "send $($applicationReady.delivery) $nativeBody"
            $received = & $readEvent $application 'received'
            $received.content | Should -BeExactly $nativeBody
            Test-Path (Join-Path $applicationProfile "storage\conversations\$($primary.delivery)\$($received.message_hash)") |
                Should -BeTrue -Because 'the unchanged primary delivery callback must persist the imported identity conversation'
            & $send $application "send $($primary.delivery) $primaryBody"
            $queued = & $readEvent $application 'queued'
            $delivered = & $readEvent $application 'delivered'
            $delivered.message_hash | Should -Be $queued.message_hash
            & $read $restart "Conversation received:.*$primaryBody" | Out-Null
            $restartedState = Get-Content (Join-Path $target 'state.json') -Raw | ConvertFrom-Json
            @($restartedState.messages.Where({ $_.id -eq $queued.message_hash -and $_.content -eq $primaryBody })).Count |
                Should -Be 1
            (Get-FileHash (Join-Path $target 'identity')).Hash | Should -Be $identityHash
            & $send $restart 'quit'
            $restart.WaitForExit(20000) | Should -BeTrue
            $restart.ExitCode | Should -Be 0
            (& $snapshot $source) | Should -BeExactly $before
            & $send $application 'quit'
            $application.WaitForExit(20000) | Should -BeTrue
            $application.ExitCode | Should -Be 0
            @{ Binary=(Get-FileHash $env:DOTRETICULUM_NOMADNET_BINARY).Hash;
                Fixture=(Get-FileHash $fixture).Hash; SourcePreserved=$true; MessagesBothDirections=$true;
                ImagePreviewAndExport=$true; Converter=(Get-FileHash $env:DOTRETICULUM_NOMADNET_PROFILE_CONVERTER).Hash;
                SameIdentityRestart=$true; PrimaryCommit='f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2';
                PrimaryApplicationBothDirectionsAfterRestart=$true;
                StrictFramedReferenceEvents=$true; ReferenceMissingEventDeadlineVerified=$true;
                PrimaryApplicationModule=$applicationReady.module;
                ImportedIdentityUnchanged=$true; PrimaryApplicationPersistedInbound=$true;
                NativeToPrimaryAfterRestartBodyBytes=[Text.Encoding]::UTF8.GetByteCount($nativeBody);
                PrimaryToNativeAfterRestartBodyBytes=[Text.Encoding]::UTF8.GetByteCount($primaryBody);
                ReferenceDependencyBoundary='Primary requires RNS>=1.6/LXMF>=1.2.2; selected actual-application flows use pinned RNS1.5.7/LXMF1.2.1, not full dependency parity';
                ProfileOrigin='Complete unchanged primary NomadNetworkApp constructor; actual Directory/Conversation/LXMessage persistence APIs';
                ImportedMessages=4; PaperDocuments=1;
                ImportedFileBytes=(Get-Item $nativeFile).Length;
                ImportedImageBytes=(Get-Item (Join-Path $target "attachments\$($media.id)\1.bin")).Length;
                Topology='IPv4 loopback: imported native TCPClientInterface first connects to pinned Python LXMF endpoint, then unchanged full primary application TCPServerInterface after native clean/abrupt restart; no transport hop';
                NetworkMethod='LXMF DIRECT'; NativeToPythonBodyBytes=25; PythonToNativeBodyBytes=25;
                Exclusions='No new multi-hop/loss/large-Resource acceptance; no RRC hub/history or complete ConfigObj/router-state migration; primary minimum dependencies remain newer than selected pins' } |
                ConvertTo-Json | Set-Content (Join-Path $work 'acceptance.json') -Encoding utf8
        }
        finally {
            foreach ($process in $processes) {
                if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force; $null = $process.WaitForExit(10000) }
                if ($errors.ContainsKey($process.Id)) {
                    $errors[$process.Id].GetAwaiter().GetResult() |
                        Set-Content (Join-Path $work "$($names[$process.Id]).stderr") -Encoding utf8
                }
                $process.Dispose()
            }
            if ($env:DOTRETICULUM_NOMADNET_KEEP_WORK -ne '1') { Remove-Item $work -Recurse -Force }
        }
    }
}

Describe 'NomadNet primary directory migration' -Tag 'DirectoryImport' {
    It 'imports actual current/legacy policy atomically and preserves it through terminal and daemon ownership/restarts' {
        if ($env:DOTRETICULUM_NOMADNET_DIRECTORY_IMPORT -ne '1') {
            Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_DIRECTORY_IMPORT=1 with native binary and pinned primary Python environment.'
            return
        }
        foreach ($variable in 'DOTRETICULUM_NOMADNET_BINARY', 'DOTRETICULUM_PYTHON',
            'DOTRETICULUM_UPSTREAM', 'DOTRETICULUM_NOMADNET_SOURCE') {
            if (-not [Environment]::GetEnvironmentVariable($variable)) { throw "$variable is required." }
        }
        $root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        if ($env:DOTRETICULUM_TEST_REPOSITORY) { $root = $env:DOTRETICULUM_TEST_REPOSITORY }
        $work = Join-Path (Get-Location) "artifacts\tests\nomadnet-directory-import-$([Guid]::NewGuid().ToString('N'))"
        $null = New-Item $work -ItemType Directory -Force
        $processes = [Collections.Generic.List[Diagnostics.Process]]::new()
        $pending = @{}
        $errors = @{}
        $names = @{}
        $start = {
            param([string] $name, [string] $binary, [string[]] $arguments)
            $info = [Diagnostics.ProcessStartInfo]::new($binary)
            $info.UseShellExecute = $false
            $info.RedirectStandardInput = $true
            $info.RedirectStandardOutput = $true
            $info.RedirectStandardError = $true
            $info.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
            $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
            foreach ($argument in $arguments) { $info.ArgumentList.Add($argument) }
            $paths = @($env:DOTRETICULUM_UPSTREAM, $env:DOTRETICULUM_NOMADNET_SOURCE, (Join-Path $root 'tests\Interop'))
            if ($env:DOTRETICULUM_LXMF_UPSTREAM) { $paths += $env:DOTRETICULUM_LXMF_UPSTREAM }
            $info.Environment['PYTHONPATH'] = $paths -join [IO.Path]::PathSeparator
            $info.Environment['PYTHON_CPU_COUNT'] = '2'
            foreach ($variable in 'TMP', 'TEMP', 'TMPDIR') { $info.Environment[$variable] = $work }
            $process = [Diagnostics.Process]::new()
            $process.StartInfo = $info
            $null = $process.Start()
            $processes.Add($process)
            $names[$process.Id] = $name
            $errors[$process.Id] = $process.StandardError.ReadToEndAsync()
            return $process
        }.GetNewClosure()
        $send = {
            param($process, [string] $text)
            $process.StandardInput.WriteLine($text)
            $process.StandardInput.Flush()
        }
        $read = {
            param($process, [string] $pattern, [int] $seconds = 45)
            $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
            while ([DateTime]::UtcNow -lt $deadline) {
                if (-not $pending.ContainsKey($process.Id)) { $pending[$process.Id] = $process.StandardOutput.ReadLineAsync() }
                try { $line = $pending[$process.Id].WaitAsync([TimeSpan]::FromMilliseconds(250)).GetAwaiter().GetResult() }
                catch [TimeoutException] { continue }
                $pending.Remove($process.Id)
                if ($null -eq $line) { throw "$($names[$process.Id]) closed stdout before '$pattern'; diagnostics $work" }
                Add-Content (Join-Path $work "$($names[$process.Id]).stdout") $line
                if ($line -match '"event"\s*:\s*"(error|failed)"') { throw $line }
                if ($line -match $pattern) { return $line }
            }
            throw "$($names[$process.Id]) timed out before '$pattern'; diagnostics $work"
        }.GetNewClosure()
        $offline = {
            param([string] $name, [string] $binary, [string[]] $arguments)
            $process = & $start $name $binary $arguments
            $output = $process.StandardOutput.ReadToEndAsync()
            $process.WaitForExit(60000) | Should -BeTrue
            $text = $output.GetAwaiter().GetResult()
            $diagnostics = $errors[$process.Id].GetAwaiter().GetResult()
            $text | Set-Content (Join-Path $work "$name.stdout") -Encoding utf8
            $diagnostics | Set-Content (Join-Path $work "$name.stderr") -Encoding utf8
            @{ Code=$process.ExitCode; Text=$text; Error=$diagnostics }
        }.GetNewClosure()
        $stop = {
            param($process)
            & $send $process 'quit'
            $process.WaitForExit(20000) | Should -BeTrue
            $process.ExitCode | Should -Be 0
        }.GetNewClosure()
        try {
            $fixturePath = Join-Path $work 'executed-primary.json'
            $primaryProfiles = Join-Path $work 'primary'
            $generation = & $offline 'generator' $env:DOTRETICULUM_PYTHON @(
                (Join-Path $root 'tests\Interop\GenerateNomadNetDirectoryImportVectors.py'), $fixturePath, $primaryProfiles)
            $generation.Code | Should -Be 0 -Because $generation.Error
            $fixture = Get-Content $fixturePath -Raw | ConvertFrom-Json
            $fixture.commit | Should -Be 'f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2'
            $fixture.sources.'nomadnet/Directory.py'.blobSha256 | Should -Be '29467f537a64feda32a1c6fe075b3836bccbeeecee1a84628075e0e8fa3c723a'
            foreach ($vector in $fixture.vectors) {
                $source = Join-Path $primaryProfiles $vector.name
                $target = Join-Path $work "native-$($vector.name)"
                $sourcePath = Join-Path $source 'storage\directory'
                $sourceHash = (Get-FileHash $sourcePath).Hash
                $arguments = @('--import-directory', $source, '--config', $target)
                $imported = & $offline "import-$($vector.name)" $env:DOTRETICULUM_NOMADNET_BINARY $arguments
                $imported.Code | Should -Be 0 -Because $imported.Error
                $imported.Text | Should -Match "$($vector.observed.Count) contacts added; 2 ignored destinations added"
                $imported.Text | Should -Match "$($vector.announceCount) transient announces not imported"
                (Get-FileHash $sourcePath).Hash | Should -Be $sourceHash
                $statePath = Join-Path $target 'state.json'
                $state = Get-Content $statePath -Raw | ConvertFrom-Json
                $state.contacts.Count | Should -Be $vector.observed.Count
                foreach ($expected in $vector.observed) {
                    $actual = @($state.contacts | Where-Object destinationHash -EQ $expected.hash)[0]
                    $actual.name | Should -BeExactly $expected.name
                    $actual.trustLevel | Should -Be $expected.trust
                    $actual.hostsNode | Should -Be $expected.node
                    $actual.preferredDelivery | Should -Be $expected.delivery
                    $actual.identifyOnConnect | Should -Be $expected.identify
                    $actual.sortRank | Should -Be $expected.rank
                    $actual.notes | Should -BeExactly $expected.notes
                }
                foreach ($hash in $vector.ignoredHashes) { $state.ignoredDestinations | Should -Contain $hash }
                $repeat = & $offline "repeat-$($vector.name)" $env:DOTRETICULUM_NOMADNET_BINARY $arguments
                $repeat.Code | Should -Be 0
                $repeat.Text | Should -Match '0 contacts added; 0 ignored destinations added'
                # Execute the entire unchanged primary constructor as an independent reader,
                # not just the generator's direct Directory methods.
                @('[client]', 'user_interface=text', 'announce_at_start=no', 'periodic_lxmf_sync=no',
                    '[node]', 'enable_node=yes', 'disable_propagation=yes', 'announce_at_start=no',
                    '[textui]', 'colormode=256', '[logging]', 'loglevel=2', 'destination=console') |
                    Set-Content (Join-Path $source 'config') -Encoding utf8
                $rns = Join-Path $work "rns-$($vector.name)"
                $null = New-Item $rns -ItemType Directory -Force
                @('[reticulum]', 'share_instance=no', 'enable_transport=no', '[logging]', 'loglevel=0', '[interfaces]') |
                    Set-Content (Join-Path $rns 'config') -Encoding utf8
                $primary = & $start "primary-$($vector.name)" $env:DOTRETICULUM_PYTHON @(
                    '-u', (Join-Path $root 'tests\Interop\nomadnet_live_node.py'), '--config', $source, '--rnsconfig', $rns)
                $null = & $read $primary '"event"\s*:\s*"ready"'
                & $send $primary 'directory-stats'
                $line = & $read $primary '"event"\s*:\s*"directory"'
                $observed = $line.Substring($line.IndexOf('@@') + 2) | ConvertFrom-Json
                $observed.contacts.Count | Should -Be $state.contacts.Count
                foreach ($expected in $vector.observed) {
                    $actual = @($observed.contacts | Where-Object hash -EQ $expected.hash)[0]
                    ($actual | ConvertTo-Json -Compress) | Should -BeExactly ($expected | ConvertTo-Json -Compress)
                }
                foreach ($hash in $vector.ignoredHashes) { $observed.ignored | Should -Contain $hash }
                & $stop $primary
            }
            $source = Join-Path $primaryProfiles 'current'
            $target = Join-Path $work 'native-current'
            $rns = Join-Path $work 'native-rns'
            $null = New-Item $rns -ItemType Directory -Force
            @('[reticulum]', 'share_instance=no', 'enable_transport=no', '[logging]', 'loglevel=0', '[interfaces]') |
                Set-Content (Join-Path $rns 'config') -Encoding utf8
            @{ schemaVersion=1; displayName='Directory acceptance'; clientAnnounceAtStart=$false;
                periodicLxmfSync=$false } | ConvertTo-Json | Set-Content (Join-Path $target 'settings.json') -Encoding utf8
            $arguments = @('--config', $target, '--rnsconfig', $rns)
            $native = & $start 'native-terminal1' $env:DOTRETICULUM_NOMADNET_BINARY $arguments
            $address = [regex]::Match((& $read $native 'LXMF delivery address:'), '[0-9a-f]{32}').Value
            & $send $native "directory-import $source"
            $null = & $read $native '0 contacts added; 0 ignored destinations added'
            & $send $native 'contacts'
            $null = & $read $native '01010101010101010101010101010101.*Trusted node trust=255 ignored=True delivery=2'
            $null = & $read $native 'Contacts complete'
            & $send $native 'nodes'
            foreach ($node in @($fixture.vectors[0].observed | Where-Object node -EQ $true)) {
                $null = & $read $native "$($node.hash).*"
            }
            $null = & $read $native 'Nodes complete'
            $locked = & $offline 'locked-import' $env:DOTRETICULUM_NOMADNET_BINARY @(
                '--import-directory', $source, '--config', $target)
            $locked.Code | Should -Be 1
            $locked.Error | Should -Match 'already in use'
            & $send $native 'contact notes 01010101010101010101010101010101 native override'
            $null = & $read $native 'Contact policy saved'
            $before = (Get-FileHash (Join-Path $target 'state.json')).Hash
            & $send $native "directory-import $source"
            $null = & $read $native 'Error:.*conflicts with saved contact'
            (Get-FileHash (Join-Path $target 'state.json')).Hash | Should -Be $before
            & $send $native "directory-import $(Join-Path $work 'missing-primary')"
            $null = & $read $native 'Error:'
            & $send $native 'status'
            $null = & $read $native 'LXMF delivery address:'
            & $stop $native
            $daemon = & $start 'native-daemon' $env:DOTRETICULUM_NOMADNET_BINARY ($arguments + '--daemon')
            (& $read $daemon 'LXMF delivery address:') | Should -Match $address
            $locked = & $offline 'daemon-locked-import' $env:DOTRETICULUM_NOMADNET_BINARY @(
                '--import-directory', $source, '--config', $target)
            $locked.Code | Should -Be 1
            if ($IsWindows) { Stop-Process -Id $daemon.Id -Force }
            else { & /bin/kill -TERM $daemon.Id; $LASTEXITCODE | Should -Be 0 }
            $daemon.WaitForExit(20000) | Should -BeTrue
            if (-not $IsWindows) { $daemon.ExitCode | Should -Be 0 }
            $native = & $start 'native-terminal2' $env:DOTRETICULUM_NOMADNET_BINARY $arguments
            (& $read $native 'LXMF delivery address:') | Should -Match $address
            & $send $native 'contacts'
            $null = & $read $native '01010101010101010101010101010101.*ignored=True delivery=2 notes=native override'
            $null = & $read $native 'Contacts complete'
            & $stop $native
            $status = & $offline 'stored-status' $env:DOTRETICULUM_NOMADNET_BINARY @('--status', '--config', $target)
            $status.Code | Should -Be 0
            $status.Text | Should -Match 'Contacts: 4'
            $invalid = & $offline 'invalid-options' $env:DOTRETICULUM_NOMADNET_BINARY @(
                '--import-directory', $source, '--config', $target, '--daemon')
            $invalid.Code | Should -Be 1
            $invalid.Error | Should -Match 'cannot be combined'
            $untouched = Join-Path $work 'invalid-source-target'
            $invalid = & $offline 'missing-source' $env:DOTRETICULUM_NOMADNET_BINARY @(
                '--import-directory', (Join-Path $work 'missing-primary'), '--config', $untouched)
            $invalid.Code | Should -Be 1
            Test-Path $untouched | Should -BeFalse
            @('actual-primary constructors: current, legacy, duplicate-last-wins',
                'native offline import, idempotence, terminal policy, conflict, failed-source recovery',
                'exclusive terminal/daemon ownership, daemon shutdown, identity/policy restart') |
                ConvertTo-Json | Set-Content (Join-Path $work 'acceptance.json') -Encoding utf8
        }
        finally {
            foreach ($process in $processes) {
                if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force; $process.WaitForExit() }
                $errors[$process.Id].GetAwaiter().GetResult() |
                    Set-Content (Join-Path $work "$($names[$process.Id]).stderr") -Encoding utf8
                $process.Dispose()
            }
            if ($env:DOTRETICULUM_NOMADNET_KEEP_WORK -ne '1') { Remove-Item $work -Recurse -Force }
        }
    }
}

Describe 'NomadNet conversation image presentation' -Tag 'Live', 'ConversationImages' {
    It 'exchanges actual primary image messages and presents bounded local ASCII under policy, restart, cancellation and converter failure' {
        if ($env:DOTRETICULUM_NOMADNET_CONVERSATION_IMAGES_LIVE -ne '1') {
            Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_CONVERSATION_IMAGES_LIVE=1 with exact native/reference/converter paths.'
            return
        }
        foreach ($variable in 'DOTRETICULUM_NOMADNET_BINARY', 'DOTRETICULUM_PYTHON',
            'DOTRETICULUM_UPSTREAM', 'DOTRETICULUM_NOMADNET_SOURCE', 'DOTRETICULUM_NOMADNET_IMAGE_CONVERTER') {
            if (-not [Environment]::GetEnvironmentVariable($variable)) { throw "$variable is required." }
        }
        $root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $work = Join-Path (Get-Location) "artifacts\tests\nomadnet-conversation-images-$([Guid]::NewGuid().ToString('N'))"
        $null = New-Item $work -ItemType Directory -Force
        $processes = [Collections.Generic.List[Diagnostics.Process]]::new()
        $pending = @{}
        $errors = @{}
        $names = @{}
        $start = {
            param([string] $name, [string] $binary, [string[]] $arguments)
            $info = [Diagnostics.ProcessStartInfo]::new($binary)
            $info.UseShellExecute = $false
            $info.RedirectStandardInput = $true
            $info.RedirectStandardOutput = $true
            $info.RedirectStandardError = $true
            $info.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
            $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
            foreach ($argument in $arguments) { $info.ArgumentList.Add($argument) }
            $paths = @($env:DOTRETICULUM_UPSTREAM, $env:DOTRETICULUM_NOMADNET_SOURCE, (Join-Path $root 'tests\Interop'))
            if ($env:DOTRETICULUM_LXMF_UPSTREAM) { $paths += $env:DOTRETICULUM_LXMF_UPSTREAM }
            $info.Environment['PYTHONPATH'] = $paths -join [IO.Path]::PathSeparator
            $info.Environment['PYTHON_CPU_COUNT'] = '2'
            foreach ($variable in 'TMP', 'TEMP', 'TMPDIR') { $info.Environment[$variable] = $work }
            $process = [Diagnostics.Process]::new()
            $process.StartInfo = $info
            $null = $process.Start()
            $processes.Add($process)
            $names[$process.Id] = $name
            $errors[$process.Id] = $process.StandardError.ReadToEndAsync()
            return $process
        }.GetNewClosure()
        $send = {
            param($process, [string] $text)
            $process.StandardInput.WriteLine($text)
            $process.StandardInput.Flush()
        }
        $read = {
            param($process, [string] $pattern, [int] $seconds = 45, [bool] $expectedError = $false)
            $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
            while ([DateTime]::UtcNow -lt $deadline) {
                if (-not $pending.ContainsKey($process.Id)) { $pending[$process.Id] = $process.StandardOutput.ReadLineAsync() }
                try { $line = $pending[$process.Id].WaitAsync([TimeSpan]::FromMilliseconds(250)).GetAwaiter().GetResult() }
                catch [TimeoutException] { continue }
                $pending.Remove($process.Id)
                if ($null -eq $line) { throw "$($names[$process.Id]) closed stdout before '$pattern'; diagnostics $work" }
                Add-Content (Join-Path $work "$($names[$process.Id]).stdout") $line
                if ($line -match '"event"\s*:\s*"(error|failed)"' -or
                    ($line -match '(^|nomadnet> )Error:' -and (-not $expectedError -or $line -notmatch $pattern))) { throw $line }
                if ($line -match $pattern) { return $line }
            }
            throw "$($names[$process.Id]) timed out before '$pattern'; diagnostics $work"
        }.GetNewClosure()
        $json = { param([string] $line) $line.Substring($line.IndexOf('@@') + 2) | ConvertFrom-Json }
        $stop = {
            param($process)
            & $send $process 'quit'
            $process.WaitForExit(20000) | Should -BeTrue
            $process.ExitCode | Should -Be 0
        }.GetNewClosure()
        $frame = {
            param($process, [string] $id = '[0-9a-f]{64}')
            $header = & $read $process "--- conversation image $id ---"
            $rows = [Collections.Generic.List[string]]::new()
            do {
                $line = & $read $process '^[ .:\-=+*#%@]+$|--- conversation image complete ---'
                if ($line -notmatch '--- conversation image complete ---') { $rows.Add($line) }
            } while ($line -notmatch '--- conversation image complete ---')
            $rows.Count | Should -Be 8
            foreach ($row in $rows) {
                $row.Length | Should -Be 32
                $row | Should -Match '^[ .:\-=+*#%@]+$'
            }
            ($rows -join '') | Should -Match '[#%@]'
            $header
        }.GetNewClosure()
        $nativeArguments = @('--config', (Join-Path $work 'native'), '--rnsconfig', (Join-Path $work 'native-rns'))
        $pythonArguments = @('-u', (Join-Path $root 'tests\Interop\nomadnet_live_node.py'),
            '--config', (Join-Path $work 'python'), '--rnsconfig', (Join-Path $work 'python-rns'))
        try {
            $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
            $listener.Start()
            $port = $listener.LocalEndpoint.Port
            $listener.Stop()
            foreach ($name in 'native', 'python') {
                $null = New-Item (Join-Path $work "$name-rns") -ItemType Directory -Force
                $null = New-Item (Join-Path $work $name) -ItemType Directory -Force
                $lines = @('[reticulum]', ' share_instance=No', ' enable_transport=No', '[logging]', ' loglevel=2', '[interfaces]')
                if ($name -eq 'native') {
                    $lines += @(' [[Server]]', ' type=TCPServerInterface', ' enabled=Yes', ' listen_ip=127.0.0.1', " listen_port=$port")
                } else {
                    $lines += @(' [[Client]]', ' type=TCPClientInterface', ' enabled=Yes', ' target_host=127.0.0.1', " target_port=$port")
                }
                $lines | Set-Content (Join-Path $work "$name-rns\config") -Encoding utf8
            }
            @('[client]', 'user_interface=text', 'periodic_lxmf_sync=no', '[node]',
                'enable_node=yes', 'disable_propagation=yes', 'announce_at_start=no',
                '[textui]', 'colormode=256', '[logging]', 'loglevel=2', 'destination=console') |
                Set-Content (Join-Path $work 'python\config') -Encoding utf8
            $settingsPath = Join-Path $work 'native\settings.json'
            @{ schemaVersion=1; displayName='Conversation image acceptance'; periodicLxmfSync=$false;
                imageLoading=0; mediaConverter=$env:DOTRETICULUM_NOMADNET_IMAGE_CONVERTER } |
                ConvertTo-Json | Set-Content $settingsPath -Encoding utf8
            $fixture = Get-Content (Join-Path $root 'tests\NomadNet.Tests\NomadNetConversationImageVectors.json') -Raw | ConvertFrom-Json
            $png = [Convert]::FromBase64String($fixture.vectors[0].imageBytes)
            $webp = [Convert]::FromBase64String($fixture.vectors[2].imageBytes)
            [IO.File]::WriteAllBytes((Join-Path $work 'primary.png'), $png)
            [IO.File]::WriteAllBytes((Join-Path $work 'primary.webp'), $webp)
            [IO.File]::WriteAllBytes((Join-Path $work 'always.png'), $png)
            $pngSha = (Get-FileHash (Join-Path $work 'primary.png')).Hash.ToLowerInvariant()
            $native = & $start 'native1' $env:DOTRETICULUM_NOMADNET_BINARY $nativeArguments
            $nativeDelivery = [regex]::Match((& $read $native 'LXMF delivery address:'), '[0-9a-f]{32}').Value
            $python = & $start 'python1' $env:DOTRETICULUM_PYTHON $pythonArguments
            $ready = & $json (& $read $python '"event"\s*:\s*"ready"')
            $ready.revision | Should -Be 'f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2'
            & $send $python 'wait-network'
            $null = & $read $python '"event"\s*:\s*"network-ready"'
            & $send $native 'announce'
            $null = & $read $native 'Announced'
            & $send $python 'announce'
            $null = & $read $python '"event"\s*:\s*"announced"'
            & $send $native "conversation active $($ready.delivery)"
            $null = & $read $native 'Conversation command complete'
            & $send $python "send-image $nativeDelivery $(Join-Path $work 'primary.png')"
            $queued = & $json (& $read $python '"event"\s*:\s*"queued"')
            $firstId = $queued.message_hash
            $null = & $read $python '"event"\s*:\s*"delivered"'
            $null = & $read $native 'Conversation received:.*upstream-image-primary.png'
            & $send $native "messages $($ready.delivery)"
            $null = & $read $native 'Messages complete'
            $state = Get-Content (Join-Path $work 'native\state.json') -Raw | ConvertFrom-Json
            @($state.messages | Where-Object id -EQ $firstId)[0].imageAttachmentIndex | Should -Be 0
            (Get-FileHash (Join-Path $work "native\attachments\$firstId\0.bin")).Hash.ToLowerInvariant() | Should -Be $pngSha
            $conversions = Join-Path $work 'native\storage\cache\conversions'
            @(Get-ChildItem $conversions -Filter *.webp -ErrorAction SilentlyContinue).Count | Should -Be 0
            & $send $native 'conversation images load'
            $null = & $read $native 'Conversation images are disabled' 45 $true
            & $send $native 'images manual'
            $null = & $read $native 'Inline images: manual'
            & $send $native "messages $($ready.delivery)"
            $null = & $read $native 'Messages complete'
            @(Get-ChildItem $conversions -Filter *.webp -ErrorAction SilentlyContinue).Count | Should -Be 0
            & $send $native 'conversation images load'
            $null = & $frame $native $firstId
            & $send $native 'images auto'
            $null = & $read $native 'Inline images: auto'
            & $send $python "send-image-raw $nativeDelivery $(Join-Path $work 'primary.webp')"
            $second = & $json (& $read $python '"event"\s*:\s*"queued"')
            $null = & $frame $native $second.message_hash
            $null = & $read $python '"event"\s*:\s*"delivered"'
            & $send $native 'images always'
            $null = & $read $native 'Inline images: always'
            & $send $python "send-image $nativeDelivery $(Join-Path $work 'always.png')"
            $third = & $json (& $read $python '"event"\s*:\s*"queued"')
            $null = & $frame $native $third.message_hash
            $null = & $read $python '"event"\s*:\s*"delivered"'
            & $send $native "image $($ready.delivery) $(Join-Path $work 'primary.png')"
            $null = & $read $native 'Message queued'
            $received = & $json (& $read $python '"event"\s*:\s*"received"')
            $received.image.sha256 | Should -Be $pngSha
            $received.image.size | Should -Be $png.Length
            $null = & $frame $native $received.message_hash
            (Get-FileHash (Join-Path $work "python\storage\attachments\$($received.message_hash)\file_0")).Hash.ToLowerInvariant() | Should -Be $pngSha
            @(Get-ChildItem $conversions -Filter *.webp).Count | Should -Be 2
            & $send $native 'conversation images reload'
            & $send $native 'conversation images cancel'
            $null = & $read $native 'Conversation image loading cancelled'
            & $send $native 'conversation images status'
            $null = & $read $native 'Conversation images: loading=False; previews=0'
            & $send $native 'images manual'
            $null = & $read $native 'Inline images: manual'
            & $stop $python
            & $stop $native
            $native = & $start 'native2' $env:DOTRETICULUM_NOMADNET_BINARY $nativeArguments
            (& $read $native 'LXMF delivery address:') | Should -Match $nativeDelivery
            $python = & $start 'python2' $env:DOTRETICULUM_PYTHON $pythonArguments
            $restored = & $json (& $read $python '"event"\s*:\s*"ready"')
            $restored.delivery | Should -Be $ready.delivery
            & $send $native "messages $($ready.delivery)"
            $null = & $read $native 'Messages complete'
            & $send $native 'conversation images status'
            $null = & $read $native 'Conversation images: loading=False; previews=0'
            & $send $native 'conversation images load'
            foreach ($index in 1..4) { $null = & $frame $native }
            & $stop $native
            $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
            $settings.imageLoading | Should -Be 1
            $settings.mediaConverter = Join-Path $work 'missing-converter'
            $settings | ConvertTo-Json | Set-Content $settingsPath -Encoding utf8
            $native = & $start 'native3' $env:DOTRETICULUM_NOMADNET_BINARY $nativeArguments
            $null = & $read $native 'LXMF delivery address:'
            & $send $native "messages $($ready.delivery)"
            $null = & $read $native 'Messages complete'
            & $send $native 'conversation images load'
            $null = & $read $native 'Conversation image loading started'
            $deadline = [DateTime]::UtcNow.AddSeconds(15)
            do {
                & $send $native 'conversation images status'
                $status = & $read $native 'Conversation images:'
                if ($status -match 'loading=False') { break }
                Start-Sleep -Milliseconds 50
            } while ([DateTime]::UtcNow -lt $deadline)
            $status | Should -Match 'loading=False; previews=0'
            & $send $python 'wait-network'
            $null = & $read $python '"event"\s*:\s*"network-ready"'
            & $send $native "send $($ready.delivery) CONVERTER_FAILURE_RECOVERY"
            $receivedRecovery = & $json (& $read $python '"event"\s*:\s*"received"')
            $receivedRecovery.content | Should -Be 'CONVERTER_FAILURE_RECOVERY'
            & $stop $native
            $failureReports = $errors[$native.Id].GetAwaiter().GetResult()
            $failureReports | Should -Match "Could not load conversation image $firstId"
            $failureReports | Should -Match 'configured local FFmpeg executable'
            @{ primaryRevision=$ready.revision; nativeBinary=$env:DOTRETICULUM_NOMADNET_BINARY;
                nativeSha256=(Get-FileHash $env:DOTRETICULUM_NOMADNET_BINARY).Hash;
                topology='Direct native application / actual primary application over isolated loopback TCP; no hardware or Kitty assertion';
                pngSha256=$pngSha; pngBytes=$png.Length; webpBytes=$webp.Length;
                primaryInboundImages=1; nativeInboundImages=3; verifiedNativeAsciiFrames=8;
                policyModes=@('never','manual','auto','always'); restartedApplications=2;
                nativeCancelJoined=$true; converterFailureContinuedMessaging=$true } |
                ConvertTo-Json | Set-Content (Join-Path $work 'acceptance.json') -Encoding utf8
        }
        finally {
            foreach ($process in $processes) {
                if (-not $process.HasExited) {
                    try { & $send $process 'quit' } catch {}
                    if (-not $process.WaitForExit(5000)) { $process.Kill($true); $process.WaitForExit() }
                }
                [IO.File]::WriteAllText((Join-Path $work "$($names[$process.Id]).stderr"), $errors[$process.Id].GetAwaiter().GetResult())
                $process.Dispose()
            }
            if ($env:DOTRETICULUM_NOMADNET_KEEP_WORK -ne '1') { Remove-Item $work -Recurse -Force }
        }
    }
}

Describe 'NomadNet configured propagation peer workflows' -Tag 'Live', 'NodeConfiguration' {
    It 'imports and persists node limits and static priority policies, peers application attachments both ways, and restores all profiles' {
        if ($env:DOTRETICULUM_NOMADNET_NODE_CONFIG_LIVE -ne '1') {
            Set-ItResult -Skipped -Because 'Enable actual application node-configuration acceptance with DOTRETICULUM_NOMADNET_NODE_CONFIG_LIVE=1.'
            return
        }
        foreach ($variable in 'DOTRETICULUM_NOMADNET_BINARY', 'DOTRETICULUM_PYTHON',
            'DOTRETICULUM_UPSTREAM', 'DOTRETICULUM_NOMADNET_SOURCE') {
            if (-not [Environment]::GetEnvironmentVariable($variable)) { throw "$variable is required." }
        }
        $root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $work = Join-Path (Get-Location) "artifacts\tests\nomadnet-node-config-$([Guid]::NewGuid().ToString('N'))"
        $null = New-Item $work -ItemType Directory -Force
        $processes = [Collections.Generic.List[Diagnostics.Process]]::new()
        $pending = @{}
        $errors = @{}
        $names = @{}
        $proxy = $null
        $start = {
            param([string] $name, [string] $binary, [string[]] $arguments)
            $info = [Diagnostics.ProcessStartInfo]::new($binary)
            $info.UseShellExecute = $false
            $info.RedirectStandardInput = $true
            $info.RedirectStandardOutput = $true
            $info.RedirectStandardError = $true
            $info.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
            $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
            foreach ($argument in $arguments) { $info.ArgumentList.Add($argument) }
            $paths = @($env:DOTRETICULUM_UPSTREAM, $env:DOTRETICULUM_NOMADNET_SOURCE, (Join-Path $root 'tests\Interop'))
            if ($env:DOTRETICULUM_LXMF_UPSTREAM) { $paths += $env:DOTRETICULUM_LXMF_UPSTREAM }
            $info.Environment['PYTHONPATH'] = $paths -join [IO.Path]::PathSeparator
            $info.Environment['PYTHON_CPU_COUNT'] = '2'
            foreach ($variable in 'TMP', 'TEMP', 'TMPDIR') { $info.Environment[$variable] = $work }
            $process = [Diagnostics.Process]::new()
            $process.StartInfo = $info
            $null = $process.Start()
            $processes.Add($process)
            $names[$process.Id] = $name
            $errors[$process.Id] = $process.StandardError.ReadToEndAsync()
            return $process
        }.GetNewClosure()
        $send = {
            param($process, [string] $text)
            $process.StandardInput.WriteLine($text)
            $process.StandardInput.Flush()
        }
        $read = {
            param($process, [string] $pattern, [int] $seconds = 120)
            $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
            while ([DateTime]::UtcNow -lt $deadline) {
                if (-not $pending.ContainsKey($process.Id)) { $pending[$process.Id] = $process.StandardOutput.ReadLineAsync() }
                try { $line = $pending[$process.Id].WaitAsync([TimeSpan]::FromMilliseconds(500)).GetAwaiter().GetResult() }
                catch [TimeoutException] { continue }
                $pending.Remove($process.Id)
                if ($null -eq $line) { throw "$($names[$process.Id]) closed stdout before '$pattern'; diagnostics $work" }
                Add-Content (Join-Path $work "$($names[$process.Id]).stdout") $line
                if ($line -match '"event"\s*:\s*"(error|failed)"|(^|nomadnet> )Error:') { throw $line }
                if ($line -match $pattern) { return $line }
            }
            throw "$($names[$process.Id]) timed out before '$pattern'; diagnostics $work"
        }.GetNewClosure()
        $json = { param([string] $line) $line.Substring($line.IndexOf('@@') + 2) | ConvertFrom-Json }
        $stop = {
            param($process)
            & $send $process 'quit'
            $process.WaitForExit(20000) | Should -BeTrue
            $process.ExitCode | Should -Be 0
        }.GetNewClosure()
        $nativeArguments = {
            param([string] $profile)
            @('--config', (Join-Path $work $profile), '--rnsconfig', (Join-Path $work "$profile-rns"))
        }.GetNewClosure()
        $pythonArguments = @('-u', (Join-Path $root 'tests\Interop\nomadnet_live_node.py'),
            '--config', (Join-Path $work 'python-node'), '--rnsconfig', (Join-Path $work 'python-node-rns'))
        $waitAttachment = {
            param([string] $profile, [string] $expected)
            $deadline = [DateTime]::UtcNow.AddSeconds(180)
            do {
                $files = @(Get-ChildItem (Join-Path $work "$profile\attachments") -Filter '*.bin' -File -Recurse -ErrorAction SilentlyContinue)
                foreach ($file in $files) {
                    if ($file.Length -eq 2200000 -and (Get-FileHash $file.FullName).Hash.ToLowerInvariant() -eq $expected) { return }
                }
                Start-Sleep -Milliseconds 100
            } while ([DateTime]::UtcNow -lt $deadline)
            throw "Application $profile did not persist the SHA-checked attachment; diagnostics $work"
        }.GetNewClosure()
        $waitPeerRoute = {
            param($process, $transport, [string] $target, [string] $local)
            $deadline = [DateTime]::UtcNow.AddSeconds(30)
            & $send $process "request-path $target"
            $null = & $read $process '"event"\s*:\s*"path-requested"'
            $ready = $false
            do {
                & $send $process 'peer-stats'
                $peer = @((& $json (& $read $process '"event"\s*:\s*"peer-stats"')).peers |
                    Where-Object hash -EQ $target)[0]
                & $send $process "path-stats $target"
                $route = & $json (& $read $process '"event"\s*:\s*"path-stats"')
                & $send $transport "path-stats $target"
                $transportRoute = & $json (& $read $transport '"event"\s*:\s*"path-stats"')
                & $send $transport "path-stats $local"
                $localRoute = & $json (& $read $transport '"event"\s*:\s*"path-stats"')
                $ready = $peer.stamp_cost -eq 13 -and $peer.transfer_kb -eq 4000 -and
                    $peer.sync_kb -eq 8000 -and $route.hops -eq 2 -and
                    $route.online -eq $true -and $route.active -eq $true -and
                    $transportRoute.online -eq $true -and $transportRoute.active -eq $true -and
                    $localRoute.online -eq $true -and $localRoute.active -eq $true
                if (-not $ready) { Start-Sleep -Milliseconds 100 }
            } while (-not $ready -and [DateTime]::UtcNow -lt $deadline)
            $ready | Should -BeTrue -Because 'actual propagation costs and the transport route, not only an application announce, must be ready'
        }.GetNewClosure()
        try {
            $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
            $listener.Start()
            $port = $listener.LocalEndpoint.Port
            $listener.Stop()
            if (-not ('NomadNetAcceptance.TcpImpairmentProxy' -as [type])) {
                Add-Type -Path (Join-Path $root 'tests\NomadNet.Tests\TcpImpairmentProxy.cs')
            }
            $proxy = [NomadNetAcceptance.TcpImpairmentProxy]::new($port)
            foreach ($name in 'transport', 'native-node', 'native-client', 'python-node') {
                $null = New-Item (Join-Path $work "$name-rns") -ItemType Directory -Force
                if ($name -ne 'native-node') { $null = New-Item (Join-Path $work $name) -ItemType Directory -Force }
                $lines = @('[reticulum]', ' share_instance = No', ' enable_transport = No', '[logging]', ' loglevel = 2', '[interfaces]')
                if ($name -eq 'transport') {
                    $lines[2] = ' enable_transport = Yes'
                    $lines += @(' [[Server]]', ' type = TCPServerInterface', ' enabled = Yes',
                        ' listen_ip = 127.0.0.1', " listen_port = $port")
                }
                elseif ($name -in @('native-client', 'python-node')) {
                    $lines += @(' [[Client]]', ' type = TCPClientInterface', ' enabled = Yes',
                        ' target_host = 127.0.0.1', " target_port = $port")
                }
                $lines | Set-Content (Join-Path $work "$name-rns\config") -Encoding utf8
            }
            $transport = & $start 'transport' $env:DOTRETICULUM_PYTHON @('-u',
                (Join-Path $root 'tests\Interop\nomadnet_transport_peer.py'), '--config', (Join-Path $work 'transport-rns'))
            $null = & $read $transport '"event"\s*:\s*"transport-ready"'
            $source = Join-Path $work 'primary-config'
            @('[client]', 'max_accepted_size=4000', 'periodic_lxmf_sync=no', '[node]',
                'enable_node=yes', 'disable_propagation=no', 'announce_at_start=no',
                'max_transfer_size=1000', 'max_sync_size=2000', 'message_storage_limit=8',
                'propagation_cost=13', 'max_peers=0') | Set-Content $source -Encoding utf8
            $import = & $start 'native-import' $env:DOTRETICULUM_NOMADNET_BINARY @('--import-config', $source, '--config', (Join-Path $work 'native-node'))
            $null = & $read $import 'Imported core configuration'
            $import.WaitForExit(20000) | Should -BeTrue
            $import.ExitCode | Should -Be 0
            $nativeNode = & $start 'native-bootstrap' $env:DOTRETICULUM_NOMADNET_BINARY (& $nativeArguments 'native-node')
            $nativeDelivery = [regex]::Match((& $read $nativeNode 'LXMF delivery address:'), '[0-9a-f]{32}').Value
            $nativePn = [regex]::Match((& $read $nativeNode 'Propagation node address:'), '[0-9a-f]{32}').Value
            @('[client]', 'user_interface=text', 'max_accepted_size=4000', 'periodic_lxmf_sync=no',
                '[node]', 'enable_node=yes', 'disable_propagation=no', 'announce_at_start=no',
                'max_transfer_size=4000', 'max_sync_size=8000', 'message_storage_limit=16',
                'propagation_cost=13', 'max_peers=0', "static_peers=$nativePn", "prioritise_destinations=$nativeDelivery",
                '[textui]', 'colormode=256', '[logging]', 'loglevel=7', 'destination=console') |
                Set-Content (Join-Path $work 'python-node\config') -Encoding utf8
            $python = & $start 'python-node' $env:DOTRETICULUM_PYTHON $pythonArguments
            $ready = & $json (& $read $python '"event"\s*:\s*"ready"')
            $ready.revision | Should -Be 'f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2'
            '{"schemaVersion":1,"displayName":"Peer receiver","periodicLxmfSync":false,"deliveryTransferLimitBytes":4000000}' |
                Set-Content (Join-Path $work 'native-client\settings.json') -Encoding utf8
            $client = & $start 'native-client' $env:DOTRETICULUM_NOMADNET_BINARY (& $nativeArguments 'native-client')
            $clientDelivery = [regex]::Match((& $read $client 'LXMF delivery address:'), '[0-9a-f]{32}').Value
            foreach ($edit in @('transfer 4000', 'sync 8000', 'storage 16', 'cost 13', 'peers 0',
                "static $($ready.propagation) on", "priority $clientDelivery on")) {
                & $send $nativeNode "node-config $edit"
                $null = & $read $nativeNode 'restart required'
                $null = & $read $nativeNode 'Running node: transfer=1000 KB; sync=2000 KB; storage=8000000 bytes'
            }
            & $stop $nativeNode
            @('[reticulum]', ' share_instance = No', '[logging]', ' loglevel = 2', '[interfaces]',
                ' [[Client]]', ' type = TCPClientInterface', ' enabled = Yes', ' target_host = 127.0.0.1',
                " target_port = $($proxy.Port)") | Set-Content (Join-Path $work 'native-node-rns\config') -Encoding utf8
            $nativeNode = & $start 'native-configured' $env:DOTRETICULUM_NOMADNET_BINARY (& $nativeArguments 'native-node')
            [regex]::Match((& $read $nativeNode 'LXMF delivery address:'), '[0-9a-f]{32}').Value | Should -Be $nativeDelivery
            [regex]::Match((& $read $nativeNode 'Propagation node address:'), '[0-9a-f]{32}').Value | Should -Be $nativePn
            & $send $nativeNode 'node-config'
            $null = & $read $nativeNode "Saved static peers: $($ready.propagation)"
            $null = & $read $nativeNode "Saved priority destinations: $clientDelivery"
            $null = & $read $nativeNode 'Running node: transfer=4000 KB; sync=8000 KB; storage=16000000 bytes; cost=13'
            & $send $python 'node-config-stats'
            $configuration = & $json (& $read $python '"event"\s*:\s*"node-configuration"')
            $configuration.effective_storage_bytes | Should -Be 16000000
            $configuration.effective_max_peers | Should -Be 0
            $configuration.effective_static_peers | Should -Contain $nativePn
            $configuration.effective_priorities | Should -Contain $nativeDelivery
            foreach ($process in @($nativeNode, $client)) {
                & $send $process 'announce'
                $null = & $read $process 'Destinations announced.'
            }
            & $send $python 'announce'
            $null = & $read $python '"event"\s*:\s*"announced"'
            & $waitPeerRoute $python $transport $nativePn $ready.propagation
            & $send $python "propagation $nativePn"
            $null = & $read $python '"event"\s*:\s*"propagation-selected"'
            $bytes = [byte[]]::new(2200000)
            [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
            $payload = Join-Path $work 'peer-transfer.bin'
            [IO.File]::WriteAllBytes($payload, $bytes)
            $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
            & $send $python "send-file-propagated $clientDelivery $payload"
            $null = & $read $python '"event"\s*:\s*"delivered"' 180
            $deadline = [DateTime]::UtcNow.AddSeconds(30)
            do {
                & $send $nativeNode 'status'
                $stored = & $read $nativeNode 'Propagation stored messages: \d+'
                if ($stored -match 'Propagation stored messages: 1$') { break }
                Start-Sleep -Milliseconds 100
            } while ([DateTime]::UtcNow -lt $deadline)
            $stored | Should -Match 'Propagation stored messages: 1$'
            $proxy.ArmResourceDrop()
            & $send $nativeNode "node-peer-sync $($ready.propagation)"
            $null = & $read $nativeNode 'Propagation peer sync complete:' 180
            $deadline = [DateTime]::UtcNow.AddSeconds(180)
            do {
                & $send $python 'stats'
                $statistics = & $json (& $read $python '"event"\s*:\s*"stats"')
                if ($statistics.propagation_messages -eq 1) { break }
                Start-Sleep -Milliseconds 200
            } while ([DateTime]::UtcNow -lt $deadline)
            $statistics.propagation_messages | Should -Be 1
            & $send $nativeNode 'node-peers'
            $firstPeer = & $read $nativeNode "Peer $($ready.propagation).*outgoing=[1-9]"
            & $send $python 'peer-stats'
            $firstPythonPeer = @((& $json (& $read $python '"event"\s*:\s*"peer-stats"')).peers |
                Where-Object hash -EQ $nativePn)[0]
            $firstPythonPeer.incoming | Should -BeGreaterThan 0
            foreach ($process in @($client, $nativeNode, $python)) { & $stop $process }
            $python = & $start 'python-restarted' $env:DOTRETICULUM_PYTHON $pythonArguments
            $restored = & $json (& $read $python '"event"\s*:\s*"ready"')
            $restored.propagation | Should -Be $ready.propagation
            $restored.delivery | Should -Be $ready.delivery
            & $send $python 'stats'
            (& $json (& $read $python '"event"\s*:\s*"stats"')).propagation_messages | Should -Be 1
            $nativeNode = & $start 'native-restarted' $env:DOTRETICULUM_NOMADNET_BINARY (& $nativeArguments 'native-node')
            [regex]::Match((& $read $nativeNode 'Propagation node address:'), '[0-9a-f]{32}').Value | Should -Be $nativePn
            & $send $nativeNode 'node-peers'
            $restoredPeer = & $read $nativeNode "Peer $($ready.propagation).*handled=[1-9]"
            $client = & $start 'client-restarted' $env:DOTRETICULUM_NOMADNET_BINARY (& $nativeArguments 'native-client')
            [regex]::Match((& $read $client 'LXMF delivery address:'), '[0-9a-f]{32}').Value | Should -Be $clientDelivery
            foreach ($process in @($nativeNode, $client)) {
                & $send $process 'announce'
                $null = & $read $process 'Destinations announced.'
            }
            & $send $python 'announce'
            $null = & $read $python '"event"\s*:\s*"announced"'
            & $waitPeerRoute $python $transport $nativePn $ready.propagation
            & $send $client "propagation $($ready.propagation)"
            $null = & $read $client 'Propagation selection saved.'
            & $send $client 'sync'
            $null = & $read $client 'Propagation sync: 1 messages \(Complete\)' 180
            & $waitAttachment 'native-client' $digest
            & $send $client "contact delivery $nativeDelivery propagated"
            $null = & $read $client 'Contact policy saved.'
            & $send $client "attach $nativeDelivery $payload"
            $null = & $read $client 'Message queued:' 180
            $deadline = [DateTime]::UtcNow.AddSeconds(180)
            do {
                & $send $python 'stats'
                $statistics = & $json (& $read $python '"event"\s*:\s*"stats"')
                if ($statistics.propagation_messages -eq 1) { break }
                Start-Sleep -Milliseconds 200
            } while ([DateTime]::UtcNow -lt $deadline)
            $statistics.propagation_messages | Should -Be 1
            & $send $python "peer-sync $nativePn"
            $null = & $read $python '"event"\s*:\s*"peer-sync-requested"'
            $deadline = [DateTime]::UtcNow.AddSeconds(180)
            do {
                & $send $python 'peer-stats'
                $secondPythonPeer = @((& $json (& $read $python '"event"\s*:\s*"peer-stats"')).peers |
                    Where-Object hash -EQ $nativePn)[0]
                if ($secondPythonPeer.key_ready -and $null -ne $secondPythonPeer.stamp_cost) { break }
                Start-Sleep -Seconds 1
            } while ([DateTime]::UtcNow -lt $deadline)
            $secondPythonPeer.key_ready | Should -BeTrue
            & $send $python "peer-sync $nativePn"
            $null = & $read $python '"event"\s*:\s*"peer-sync-requested"'
            & $waitAttachment 'native-node' $digest
            & $send $python 'peer-stats'
            $secondPythonPeer = @((& $json (& $read $python '"event"\s*:\s*"peer-stats"')).peers |
                Where-Object hash -EQ $nativePn)[0]
            $secondPythonPeer.outgoing | Should -BeGreaterThan 0
            & $send $nativeNode 'node-peers'
            $secondPeer = & $read $nativeNode "Peer $($ready.propagation).*incoming=[1-9]"
            $proxy.Dropped | Should -Be 1
            $proxy.ForwardedBytes | Should -BeGreaterThan $bytes.Length
            @{ nativeBinary = $env:DOTRETICULUM_NOMADNET_BINARY;
                nativeSha256 = (Get-FileHash $env:DOTRETICULUM_NOMADNET_BINARY).Hash;
                primaryRevision = $ready.revision; payloadBytes = 2200000; payloadSha256 = $digest;
                firstNativePeer = $firstPeer; restoredNativePeer = $restoredPeer; finalNativePeer = $secondPeer;
                pythonIncoming = $firstPythonPeer.incoming; pythonOutgoing = $secondPythonPeer.outgoing;
                maxPeers = 0; staticPeersExceedDynamicCap = $true;
                droppedResourceParts = $proxy.Dropped; delayedFrames = $proxy.Forwarded; forwardedBytes = $proxy.ForwardedBytes;
                delayMilliseconds = 5; topology = 'native PN and actual Python NomadNet PN peer across a separate actual Python transport hop; native PN leg delayed5ms with one Resource-part drop' } |
                ConvertTo-Json | Set-Content (Join-Path $work 'acceptance.json') -Encoding utf8
        }
        finally {
            foreach ($process in $processes) {
                if (-not $process.HasExited) {
                    try { & $send $process 'quit' } catch { }
                    if (-not $process.WaitForExit(5000)) { $process.Kill($true); $process.WaitForExit() }
                }
                [IO.File]::WriteAllText((Join-Path $work "$($names[$process.Id]).stderr"), $errors[$process.Id].GetAwaiter().GetResult())
                $process.Dispose()
            }
            if ($null -ne $proxy) { $proxy.DisposeAsync().AsTask().GetAwaiter().GetResult() }
            if ($env:DOTRETICULUM_NOMADNET_KEEP_WORK -ne '1') { Remove-Item $work -Recurse -Force }
        }
    }
}

Describe 'NomadNet actual RRC presentation workflows' -Tag 'Live', 'RrcPresentation' {
    It 'preserves original group composition and renders all four policies through an actual Python transport hub across restart' {
        if ($env:DOTRETICULUM_NOMADNET_RRC_PRESENTATION_LIVE -ne '1' -or
            [string]::IsNullOrWhiteSpace($env:DOTRETICULUM_NOMADNET_BINARY) -or
            [string]::IsNullOrWhiteSpace($env:DOTRETICULUM_PYTHON) -or
            [string]::IsNullOrWhiteSpace($env:DOTRETICULUM_NOMADNET_SOURCE) -or
            [string]::IsNullOrWhiteSpace($env:DOTRETICULUM_NOMADNET_RRCD_SOURCE)) {
            Set-ItResult -Skipped -Because 'Select actual pinned primary/rrcd/Python and native binaries and enable DOTRETICULUM_NOMADNET_RRC_PRESENTATION_LIVE=1.'
            return
        }
        $root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $work = Join-Path (Get-Location) "artifacts\tests\nomadnet-rrc-presentation-$([Guid]::NewGuid().ToString('N'))"
        $null = New-Item $work -ItemType Directory -Force
        $processes = [Collections.Generic.List[Diagnostics.Process]]::new()
        $pending = @{}
        $errors = @{}
        $names = @{}
        $start = {
            param([string] $name, [string] $binary, [string[]] $arguments)
            $info = [Diagnostics.ProcessStartInfo]::new($binary)
            $info.UseShellExecute = $false
            $info.RedirectStandardInput = $true
            $info.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
            $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
            $info.RedirectStandardOutput = $true
            $info.RedirectStandardError = $true
            foreach ($argument in $arguments) { $info.ArgumentList.Add($argument) }
            $info.Environment['PYTHONPATH'] = $env:DOTRETICULUM_UPSTREAM + [IO.Path]::PathSeparator + $env:DOTRETICULUM_NOMADNET_SOURCE
            if (-not [string]::IsNullOrWhiteSpace($env:DOTRETICULUM_LXMF_UPSTREAM)) {
                $info.Environment['PYTHONPATH'] += [IO.Path]::PathSeparator + $env:DOTRETICULUM_LXMF_UPSTREAM
            }
            foreach ($variable in 'TMPDIR', 'TMP', 'TEMP') { $info.Environment[$variable] = $work }
            $process = [Diagnostics.Process]::new()
            $process.StartInfo = $info
            $null = $process.Start()
            $processes.Add($process)
            $names[$process.Id] = $name
            $errors[$process.Id] = $process.StandardError.ReadToEndAsync()
            return $process
        }.GetNewClosure()
        $read = {
            param($process, [string] $pattern, [int] $seconds = 60)
            $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
            while ([DateTime]::UtcNow -lt $deadline) {
                if (-not $pending.ContainsKey($process.Id)) { $pending[$process.Id] = $process.StandardOutput.ReadLineAsync() }
                try { $line = $pending[$process.Id].WaitAsync([TimeSpan]::FromMilliseconds(500)).GetAwaiter().GetResult() }
                catch [TimeoutException] { continue }
                $pending.Remove($process.Id)
                if ($null -eq $line) { throw "$($names[$process.Id]) closed stdout before '$pattern'." }
                Add-Content (Join-Path $work "$($names[$process.Id]).stdout") $line
                if ($line -match '"event"\s*:\s*"(error|failed)"|(^|nomadnet> )Error:') { throw $line }
                if ($line -match $pattern) { return $line }
            }
            throw "$($names[$process.Id]) timed out waiting for '$pattern'; diagnostics: $work"
        }.GetNewClosure()
        $send = {
            param($process, [string] $command)
            $process.StandardInput.WriteLine($command)
            $process.StandardInput.Flush()
        }
        try {
            $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
            $listener.Start()
            $port = $listener.LocalEndpoint.Port
            $listener.Stop()
            foreach ($name in 'hub', 'python', 'native') {
                $null = New-Item (Join-Path $work "$name-rns"), (Join-Path $work $name) -ItemType Directory -Force
                $config = @('[reticulum]', '  share_instance = No', '  enable_transport = No', '[logging]', '  loglevel = 2', '[interfaces]')
                if ($name -eq 'hub') {
                    $config[2] = '  enable_transport = Yes'
                    $config += @('  [[Server]]', '    type = TCPServerInterface', '    enabled = Yes',
                        '    listen_ip = 127.0.0.1', "    listen_port = $port")
                }
                else {
                    $config += @('  [[Client]]', '    type = TCPClientInterface', '    enabled = Yes',
                        '    target_host = 127.0.0.1', "    target_port = $port")
                }
                $config | Set-Content (Join-Path $work "$name-rns\config") -Encoding utf8
            }
            $bootstrap = & $start 'bootstrap' $env:DOTRETICULUM_PYTHON @('-u', '-m', 'nomadnet.nomadnet',
                '--daemon', '--console', '--config', (Join-Path $work 'python'), '--rnsconfig', (Join-Path $work 'python-rns'))
            $configuration = Join-Path $work 'python\config'
            $deadline = [DateTime]::UtcNow.AddSeconds(60)
            while (-not (Test-Path $configuration) -and [DateTime]::UtcNow -lt $deadline) {
                if ($bootstrap.HasExited) { throw 'Actual primary bootstrap failed.' }
                Start-Sleep -Milliseconds 100
            }
            Test-Path $configuration | Should -BeTrue
            Start-Sleep -Seconds 1
            $bootstrap.Kill($true)
            $bootstrap.WaitForExit()
            [IO.File]::WriteAllText($configuration, [IO.File]::ReadAllText($configuration).Replace('enable_node = no', 'enable_node = yes'),
                [Text.UTF8Encoding]::new($false))
            $hub = & $start 'hub' $env:DOTRETICULUM_PYTHON @('-u', (Join-Path $root 'tests\Interop\nomadnet_rrc_hub.py'),
                '--config', (Join-Path $work 'hub'), '--rnsconfig', (Join-Path $work 'hub-rns'))
            $ready = ((& $read $hub '"event"\s*:\s*"hub-ready"').Substring(2) | ConvertFrom-Json)
            $ready.revision | Should -Be 'f6d7e9d72bf83c70d7a9373ae8c5edaa052a7bf2'
            $python = & $start 'python' $env:DOTRETICULUM_PYTHON @('-u', (Join-Path $root 'tests\Interop\nomadnet_live_node.py'),
                '--config', (Join-Path $work 'python'), '--rnsconfig', (Join-Path $work 'python-rns'))
            $appReady = ((& $read $python '"event"\s*:\s*"ready"').Substring(2) | ConvertFrom-Json)
            $appReady.revision | Should -Be 'f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2'
            & $send $python "rrc-connect $($ready.hash) Python"
            $null = & $read $python '"event"\s*:\s*"rrc-connected"'
            & $send $python 'rrc-join lobby'
            $null = & $read $python '"event"\s*:\s*"rrc-joined"'
            $nativeArgs = @('--config', (Join-Path $work 'native'), '--rnsconfig', (Join-Path $work 'native-rns'))
            $native = & $start 'native' $env:DOTRETICULUM_NOMADNET_BINARY $nativeArgs
            $address = [regex]::Match((& $read $native 'LXMF delivery address:'), '[0-9a-f]{32}').Value
            & $send $native "rrc connect $($ready.hash) Native"
            $null = & $read $native 'RRC command complete'
            & $send $native 'rrc join lobby'
            $null = & $read $native 'RRC command complete'
            $fixture = [IO.File]::ReadAllText((Join-Path $root 'tests\NomadNet.Tests\NomadNetPresentationVectors.json')) | ConvertFrom-Json
            $index = 0
            foreach ($vector in @($fixture.rrc | Where-Object name -In 'emphasis', 'escaped-micron', 'raw-micron', 'multiline')) {
                foreach ($pair in @(@('markdown', $vector.markdown), @('micron', $vector.micron))) {
                    $value = if ($pair[1]) { 'on' } else { 'off' }
                    & $send $native "rrc render $($pair[0]) $value"
                    $null = & $read $native 'RRC rendering policy saved'
                }
                $marker = "Presentation$index"
                $body = $marker + ' ' + $vector.input
                $json = ConvertTo-Json -InputObject $body -Compress
                & $send $python "rrc-send-json lobby $json"
                $expected = ($marker + ' ' + $vector.visible).Split("`n")
                $line = & $read $native ("Python: " + [regex]::Escape($marker))
                ($line -replace '^.*Python: ', '') | Should -BeExactly $expected[0]
                for ($row = 1; $row -lt $expected.Count; $row++) {
                    $line = & $read $native ('^' + [regex]::Escape($expected[$row]) + '$')
                    $line | Should -BeExactly $expected[$row]
                }
                & $send $native "rrc send-json lobby $json"
                $received = ((& $read $python ('"event"\s*:\s*"rrc-message".*' + $marker + '.*"nick"\s*:\s*"Native"')).Substring(2) | ConvertFrom-Json)
                $received.text | Should -BeExactly $body
                $received.nick | Should -BeExactly 'Native'
                $index++
            }
            $index | Should -Be 16
            & $send $native 'quit'
            $native.WaitForExit(20000) | Should -BeTrue
            $native.ExitCode | Should -Be 0
            $saved = [IO.File]::ReadAllText((Join-Path $work 'native\settings.json')) | ConvertFrom-Json
            $native = & $start 'native-restarted' $env:DOTRETICULUM_NOMADNET_BINARY $nativeArgs
            [regex]::Match((& $read $native 'LXMF delivery address:'), '[0-9a-f]{32}').Value | Should -Be $address
            $restored = [IO.File]::ReadAllText((Join-Path $work 'native\settings.json')) | ConvertFrom-Json
            $restored.rrcRenderMarkdown | Should -Be $saved.rrcRenderMarkdown
            $restored.rrcRenderMicron | Should -Be $saved.rrcRenderMicron
            & $send $native "rrc connect $($ready.hash)"
            $null = & $read $native 'RRC command complete'
            & $send $native 'rrc history lobby'
            $null = & $read $native 'Presentation15'
            $null = & $read $native 'RRC command complete'
            & $send $python 'rrc-send lobby after-presentation-restart'
            $null = & $read $native 'Python: after-presentation-restart'
        }
        finally {
            foreach ($process in $processes) {
                if (-not $process.HasExited) {
                    try { & $send $process 'quit' } catch {}
                    if (-not $process.WaitForExit(5000)) { $process.Kill($true); $process.WaitForExit() }
                }
                [IO.File]::WriteAllText((Join-Path $work "$($names[$process.Id]).stderr"), $errors[$process.Id].GetAwaiter().GetResult())
                $process.Dispose()
            }
            if ($env:DOTRETICULUM_NOMADNET_KEEP_WORK -ne '1') { Remove-Item $work -Recurse -Force }
        }
    }
}

Describe 'NomadNet actual application image workflows' -Tag 'Live', 'Images' {
    It 'converts and previews images in both directions with primary ImageData and restores both conversion caches' {
        if ($env:DOTRETICULUM_NOMADNET_IMAGE_LIVE -ne '1' -or
            [string]::IsNullOrWhiteSpace($env:DOTRETICULUM_NOMADNET_IMAGE_CONVERTER) -or
            [string]::IsNullOrWhiteSpace($env:DOTRETICULUM_NOMADNET_BINARY) -or
            [string]::IsNullOrWhiteSpace($env:DOTRETICULUM_PYTHON) -or
            [string]::IsNullOrWhiteSpace($env:DOTRETICULUM_NOMADNET_SOURCE)) {
            Set-ItResult -Skipped -Because 'Select exact native, Python/primary and FFmpeg binaries and enable DOTRETICULUM_NOMADNET_IMAGE_LIVE=1.'
            return
        }
        $root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        if ($env:DOTRETICULUM_NOMADNET_HOST_POLICY_LIVE -eq '1' -and $env:DOTRETICULUM_NOMADNET_CONFIG_LIVE -ne '1') {
            throw 'Hosted policy acceptance requires the explicit core configuration mode.'
        }
        if ($env:DOTRETICULUM_NOMADNET_CLIENT_ANNOUNCE_LIVE -eq '1' -and $env:DOTRETICULUM_NOMADNET_HOST_POLICY_LIVE -ne '1') {
            throw 'Client announce acceptance requires the explicit hosted-policy/configuration modes.'
        }
        if ($env:DOTRETICULUM_NOMADNET_PRESENTATION_LIVE -eq '1' -and
            ($env:DOTRETICULUM_NOMADNET_CLIENT_ANNOUNCE_LIVE -ne '1' -or $env:DOTRETICULUM_NOMADNET_CONVERSATION_LIVE -ne '1')) {
            throw 'Presentation acceptance requires explicit client-announcement and conversation modes.'
        }
        $work = Join-Path (Get-Location) "artifacts\tests\nomadnet-images-$([Guid]::NewGuid().ToString('N'))"
        $null = New-Item $work -ItemType Directory -Force
        $processes = [Collections.Generic.List[Diagnostics.Process]]::new()
        $pending = @{}
        $errors = @{}
        $names = @{}
        $start = {
            param([string] $name, [string] $executable, [string[]] $arguments, [string] $backend = 'ffmpeg')
            $info = [Diagnostics.ProcessStartInfo]::new($executable)
            $info.UseShellExecute = $false
            $info.RedirectStandardInput = $true
            $info.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
            $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
            $info.RedirectStandardOutput = $true
            $info.RedirectStandardError = $true
            foreach ($argument in $arguments) { $info.ArgumentList.Add($argument) }
            $info.Environment['PYTHONPATH'] = $env:DOTRETICULUM_UPSTREAM + [IO.Path]::PathSeparator + $env:DOTRETICULUM_NOMADNET_SOURCE
            if (-not [string]::IsNullOrWhiteSpace($env:DOTRETICULUM_LXMF_UPSTREAM)) {
                $info.Environment['PYTHONPATH'] += [IO.Path]::PathSeparator + $env:DOTRETICULUM_LXMF_UPSTREAM
            }
            $info.Environment['PATH'] = (Split-Path $env:DOTRETICULUM_NOMADNET_IMAGE_CONVERTER -Parent) + [IO.Path]::PathSeparator + $env:PATH
            $info.Environment['RNGIT_MEDIA_BACKEND'] = $backend
            $info.Environment['NOMADNET_IMAGE_BACKEND'] = 'ffmpeg'
            foreach ($variable in 'TMPDIR', 'TMP', 'TEMP') { $info.Environment[$variable] = $work }
            $process = [Diagnostics.Process]::new()
            $process.StartInfo = $info
            $null = $process.Start()
            $processes.Add($process)
            $names[$process.Id] = $name
            $errors[$process.Id] = $process.StandardError.ReadToEndAsync()
            return $process
        }.GetNewClosure()
        $read = {
            param($process, [string] $pattern, [int] $seconds = 60, [bool] $allowExpectedCommandError = $false)
            $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
            while ([DateTime]::UtcNow -lt $deadline) {
                if (-not $pending.ContainsKey($process.Id)) { $pending[$process.Id] = $process.StandardOutput.ReadLineAsync() }
                try { $line = $pending[$process.Id].WaitAsync([TimeSpan]::FromMilliseconds(500)).GetAwaiter().GetResult() }
                catch [TimeoutException] { continue }
                $pending.Remove($process.Id)
                if ($null -eq $line) { throw "$($names[$process.Id]) closed stdout before '$pattern'." }
                Add-Content (Join-Path $work "$($names[$process.Id]).stdout") $line
                if ($line -match '"event"\s*:\s*"(error|failed|request-failed)"' -or
                    ($line -match '(^|nomadnet> )Error:' -and (-not $allowExpectedCommandError -or $line -notmatch $pattern))) { throw $line }
                if ($line -match $pattern) { return $line }
            }
            throw "$($names[$process.Id]) timed out waiting for '$pattern'; diagnostics: $work"
        }.GetNewClosure()
        $send = {
            param($process, [string] $text)
            $process.StandardInput.WriteLine($text)
            $process.StandardInput.Flush()
        }
        $readState = {
            $stream = [IO.FileStream]::new((Join-Path $work 'native\state.json'), [IO.FileMode]::Open,
                [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
            $reader = [IO.StreamReader]::new($stream)
            try { $reader.ReadToEnd() | ConvertFrom-Json }
            finally { $reader.Dispose() }
        }.GetNewClosure()
        try {
            $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
            $listener.Start()
            $port = $listener.LocalEndpoint.Port
            $listener.Stop()
            foreach ($name in 'native', 'python') {
                $null = New-Item (Join-Path $work "$name-rns") -ItemType Directory -Force
                $null = New-Item (Join-Path $work "$name\storage\pages") -ItemType Directory -Force
                $config = @('[reticulum]', '  share_instance = No', '  enable_transport = No', '[logging]', '  loglevel = 2', '[interfaces]')
                if ($name -eq 'native') {
                    $config += @('  [[Server]]', '    type = TCPServerInterface', '    enabled = Yes',
                        '    listen_ip = 127.0.0.1', "    listen_port = $port")
                }
                else {
                    $config += @('  [[Client]]', '    type = TCPClientInterface', '    enabled = Yes',
                        '    target_host = 127.0.0.1', "    target_port = $port")
                }
                $config | Set-Content (Join-Path $work "$name-rns\config") -Encoding utf8
            }
            $bootstrap = & $start 'bootstrap' $env:DOTRETICULUM_PYTHON @('-u', '-m', 'nomadnet.nomadnet',
                '--daemon', '--console', '--config', (Join-Path $work 'python'), '--rnsconfig', (Join-Path $work 'python-rns'))
            $configuration = Join-Path $work 'python\config'
            $deadline = [DateTime]::UtcNow.AddSeconds(60)
            while (-not (Test-Path $configuration) -and [DateTime]::UtcNow -lt $deadline) {
                if ($bootstrap.HasExited) { throw 'Primary image app bootstrap failed.' }
                Start-Sleep -Milliseconds 100
            }
            (Test-Path $configuration) | Should -BeTrue
            Start-Sleep -Seconds 1
            $bootstrap.Kill($true)
            $bootstrap.WaitForExit()
            $text = [IO.File]::ReadAllText($configuration).Replace('enable_node = no', 'enable_node = yes').
                Replace('disable_propagation = no', 'disable_propagation = yes')
            [IO.File]::WriteAllText($configuration, $text, [Text.UTF8Encoding]::new($false))
            $fixture = [IO.File]::ReadAllText((Join-Path $root 'tests\NomadNet.Tests\NomadNetImageVectors.json')) | ConvertFrom-Json
            $vector = @($fixture.vectors | Where-Object { $_.inputWidth -eq 2400 })[0]
            foreach ($name in 'native', 'python') {
                $pages = Join-Path $work "$name\storage\pages"
                [IO.File]::WriteAllBytes((Join-Path $pages 'picture.png'), [Convert]::FromBase64String($vector.input))
                [IO.File]::WriteAllText((Join-Path $pages 'index.mu'), "#!c=0`nImage application contract`n" + '`(public generated image`/media/picture.png)' + "`n", [Text.UTF8Encoding]::new($false))
                if ($env:DOTRETICULUM_NOMADNET_INLINE_MEDIA_LIVE -eq '1') {
                    [IO.File]::WriteAllText((Join-Path $pages 'index.mu'), "#!c=600`nImage application contract`n" +
                        '`(public generated image`/media/picture.png)' + "`n" + '`{/page/inline-images.mu}' + "`n", [Text.UTF8Encoding]::new($false))
                    [IO.File]::WriteAllText((Join-Path $pages 'inline-images.mu'), "INLINE_PARTIAL`n" +
                        '`(missing generated image`/media/missing.webp)' + "`n" +
                        '`(partial generated image`/media/picture.png)' + "`n", [Text.UTF8Encoding]::new($false))
                    if (-not [OperatingSystem]::IsWindows()) {
                        [IO.File]::SetUnixFileMode((Join-Path $pages 'inline-images.mu'), [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)
                    }
                }
                if ($env:DOTRETICULUM_NOMADNET_HISTORY_LIVE -eq '1') {
                    [IO.File]::WriteAllText((Join-Path $pages 'history.mu'), "#!c=0`nHISTORY_BRANCH", [Text.UTF8Encoding]::new($false))
                    if (-not [OperatingSystem]::IsWindows()) {
                        [IO.File]::SetUnixFileMode((Join-Path $pages 'history.mu'), [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)
                    }
                }
                if ($env:DOTRETICULUM_NOMADNET_DIRECTORY_LIVE -eq '1') {
                    [IO.File]::WriteAllText((Join-Path $pages 'private.mu'), "#!c=0`nDIRECTORY_PRIVATE", [Text.UTF8Encoding]::new($false))
                    [IO.File]::WriteAllText((Join-Path $pages 'private.mu.allowed'), '', [Text.UTF8Encoding]::new($false))
                    if (-not [OperatingSystem]::IsWindows()) {
                        foreach ($file in 'private.mu', 'private.mu.allowed') {
                            [IO.File]::SetUnixFileMode((Join-Path $pages $file), [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)
                        }
                    }
                }
                if (-not [OperatingSystem]::IsWindows()) {
                    [IO.File]::SetUnixFileMode((Join-Path $pages 'index.mu'), [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)
                }
            }
            $settingsPath = Join-Path $work 'native\settings.json'
            $settings = @{ schemaVersion = 1; displayName = 'Image acceptance'; enableNode = $true;
                mediaConverter = $env:DOTRETICULUM_NOMADNET_IMAGE_CONVERTER }
            if ($env:DOTRETICULUM_NOMADNET_CONFIG_LIVE -eq '1') {
                $customPages = Join-Path $work 'configured-pages'
                $customFiles = Join-Path $work 'configured-files'
                $customDownloads = Join-Path $work 'configured-downloads'
                $null = New-Item $customPages, $customFiles -ItemType Directory -Force
                Copy-Item (Join-Path $work 'native\storage\pages\*') $customPages
                Remove-Item (Join-Path $work 'native\storage\pages\*')
                [IO.File]::WriteAllText((Join-Path $customFiles 'public.txt'), 'CONFIGURED_FILE_ROOT')
                $importSource = Join-Path $work 'upstream-core-config'
                $importTarget = Join-Path $work 'imported-profile'
                @"
[client]
required_stamp_cost = 13
accept_invalid_stamps = no
max_accepted_size = 4000
downloads_path = '$customDownloads'
[node]
enable_node = yes
disable_propagation = yes
node_name = 'Configured native node'
pages_path = '$customPages'
files_path = '$customFiles'
[textui]
image_loading = manual
mouse_enabled = yes
"@ | Set-Content $importSource -Encoding utf8
                if ($env:DOTRETICULUM_NOMADNET_HOST_POLICY_LIVE -eq '1') {
                    [IO.File]::AppendAllText($importSource, "`n", [Text.UTF8Encoding]::new($false))
                    $sourceText = [IO.File]::ReadAllText($importSource).Replace('[textui]',
                        "announce_at_start = no`npage_refresh_interval = 1`nfile_refresh_interval = 0`n[textui]")
                    [IO.File]::WriteAllText($importSource, $sourceText, [Text.UTF8Encoding]::new($false))
                }
                $originalConfigHash = (Get-FileHash $importSource).Hash
                $importer = & $start 'config-import' $env:DOTRETICULUM_NOMADNET_BINARY @(
                    '--import-config', $importSource, '--config', $importTarget)
                $null = & $read $importer 'Imported core configuration.*unsupported settings: 1'
                $importer.WaitForExit(20000) | Should -BeTrue
                $importer.ExitCode | Should -Be 0
                $errors[$importer.Id].GetAwaiter().GetResult() | Should -Match 'textui.mouse_enabled'
                $occupied = & $start 'config-import-occupied' $env:DOTRETICULUM_NOMADNET_BINARY @(
                    '--import-config', $importSource, '--config', $importTarget)
                $occupied.WaitForExit(20000) | Should -BeTrue
                $occupied.ExitCode | Should -Be 1
                $errors[$occupied.Id].GetAwaiter().GetResult() | Should -Match 'unoccupied target'
                (Get-FileHash $importSource).Hash | Should -Be $originalConfigHash
                $settings = [IO.File]::ReadAllText((Join-Path $importTarget 'settings.json')) | ConvertFrom-Json
                $settings.inboundStampCost | Should -Be 13
                $settings.acceptInvalidStamps | Should -BeFalse
                $settings.deliveryTransferLimitBytes | Should -Be 4000000
                $settings.mediaConverter = $env:DOTRETICULUM_NOMADNET_IMAGE_CONVERTER
            }
            if ($env:DOTRETICULUM_NOMADNET_CLIENT_ANNOUNCE_LIVE -eq '1') {
                $settings.enableClient | Should -BeFalse
                $settings.clientAnnounceAtStart = $false
                $settings.clientAnnounceIntervalMinutes = 30
                # Disclosed old persisted-state fixture; real 90-second job deferral is not accelerated.
                $settings.lastClientAnnounce = [DateTimeOffset]::UtcNow.AddMinutes(-60).ToString('O')
            }
            $settings | ConvertTo-Json | Set-Content $settingsPath -Encoding utf8
            $nativeArgs = @('--config', (Join-Path $work 'native'), '--rnsconfig', (Join-Path $work 'native-rns'))
            $pythonArgs = @('-u', (Join-Path $root 'tests\Interop\nomadnet_live_node.py'),
                '--config', (Join-Path $work 'python'), '--rnsconfig', (Join-Path $work 'python-rns'))
            if ($env:DOTRETICULUM_NOMADNET_HOST_POLICY_LIVE -eq '1') {
                $python = & $start 'python' $env:DOTRETICULUM_PYTHON $pythonArgs
                $ready = ((& $read $python '"event"\s*:\s*"ready"').Substring(2) | ConvertFrom-Json)
                & $send $python 'observe-node-announces'
                $null = & $read $python '"event"\s*:\s*"observing-node-announces"'
                if ($env:DOTRETICULUM_NOMADNET_CLIENT_ANNOUNCE_LIVE -eq '1') {
                    & $send $python 'observe-client-announces'
                    $null = & $read $python '"event"\s*:\s*"observing-client-announces"'
                    $clientStarted = [DateTimeOffset]::UtcNow
                }
                $native = & $start 'native' $env:DOTRETICULUM_NOMADNET_BINARY $nativeArgs
                $nativeNode = [regex]::Match((& $read $native 'Hosted node address:'), '[0-9a-f]{32}').Value
            }
            else {
                $native = & $start 'native' $env:DOTRETICULUM_NOMADNET_BINARY $nativeArgs
                $nativeNode = [regex]::Match((& $read $native 'Hosted node address:'), '[0-9a-f]{32}').Value
                $python = & $start 'python' $env:DOTRETICULUM_PYTHON $pythonArgs
                $ready = ((& $read $python '"event"\s*:\s*"ready"').Substring(2) | ConvertFrom-Json)
            }
            $ready.revision | Should -Be 'f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2'
            if ($env:DOTRETICULUM_NOMADNET_HOST_POLICY_LIVE -eq '1') {
                & $send $python 'wait-network'
                $null = & $read $python '"event"\s*:\s*"network-ready"'
                Start-Sleep -Seconds 7
                if ($env:DOTRETICULUM_NOMADNET_CLIENT_ANNOUNCE_LIVE -eq '1') {
                    & $send $native 'status'
                    $nativeDelivery = [regex]::Match((& $read $native 'LXMF delivery address:'), '[0-9a-f]{32}').Value
                    & $send $python "client-announces $nativeDelivery"
                    $clientObservation = ((& $read $python '"event"\s*:\s*"client-announces"').Substring(2) | ConvertFrom-Json)
                    $clientObservation.count | Should -Be 0 -Because 'disabled client startup must not broadcast, independently of hosted startup'
                }
                & $send $python "node-announces $nativeNode"
                $observation = ((& $read $python '"event"\s*:\s*"node-announces"').Substring(2) | ConvertFrom-Json)
                $observation.count | Should -Be 0 -Because 'disabled host startup must not broadcast its destination'
                & $send $native 'node announce'
                $null = & $read $native 'Hosted node announced'
                & $send $python "node-announces $nativeNode 1"
                $observation = ((& $read $python '"event"\s*:\s*"node-announces"').Substring(2) | ConvertFrom-Json)
                $observation.count | Should -BeGreaterOrEqual 1
                [IO.File]::WriteAllText((Join-Path $customPages 'scheduled.mu'), "#!c=0`nAUTO_REFRESH_PAGE", [Text.UTF8Encoding]::new($false))
                [IO.File]::WriteAllText((Join-Path $customFiles 'manual.txt'), 'MANUAL_REFRESH_FILE', [Text.UTF8Encoding]::new($false))
                $deadline = [DateTime]::UtcNow.AddSeconds(80)
                do {
                    & $send $native 'node paths'
                    $paths = [Collections.Generic.List[string]]::new()
                    do {
                        $line = & $read $native 'Hosted path |Hosted paths complete'
                        $paths.Add($line)
                    } while ($line -notmatch 'Hosted paths complete')
                    @($paths | Where-Object { $_ -match '/file/manual.txt$' }).Count | Should -Be 0 -Because 'file rescanning is independently disabled'
                    $pageReady = @($paths | Where-Object { $_ -match '/page/scheduled.mu$' }).Count -eq 1
                    if (-not $pageReady) { Start-Sleep -Milliseconds 500 }
                } while (-not $pageReady -and [DateTime]::UtcNow -lt $deadline)
                $pageReady | Should -BeTrue -Because 'configured one-minute page scan must register the newly created path'
                & $send $python "request $nativeNode /page/scheduled.mu"
                $response = ((& $read $python '"event"\s*:\s*"response"').Substring(2) | ConvertFrom-Json)
                $response.text | Should -Match 'AUTO_REFRESH_PAGE'
                & $send $native 'node refresh'
                $null = & $read $native 'Hosted content refreshed'
                & $send $python "request $nativeNode /file/manual.txt"
                $response = ((& $read $python '"event"\s*:\s*"response"').Substring(2) | ConvertFrom-Json)
                $response.text | Should -Be 'MANUAL_REFRESH_FILE'
            }
            if ($env:DOTRETICULUM_NOMADNET_HISTORY_LIVE -eq '1') {
                foreach ($step in @(
                    @("browse $($ready.node)", 'Image application contract'),
                    @("browse $($ready.node) /page/history.mu", 'HISTORY_BRANCH'),
                    @('back', 'Image application contract'), @('forward', 'HISTORY_BRANCH'),
                    @('reload', 'HISTORY_BRANCH'), @('history', 'History complete: 2/2\.'),
                    @('back', 'Image application contract'), @("browse $($ready.node)", 'Image application contract'),
                    @('forward', 'No forward history\.'), @('history', 'History complete: 2/2\.'))) {
                    & $send $native $step[0]
                    $null = & $read $native $step[1]
                }
            }
            if ($env:DOTRETICULUM_NOMADNET_CONFIG_LIVE -eq '1') {
                & $send $python "request $nativeNode /file/public.txt"
                $response = ((& $read $python '"event"\s*:\s*"response"').Substring(2) | ConvertFrom-Json)
                $response.text | Should -Be 'CONFIGURED_FILE_ROOT'
            }
            if ($env:DOTRETICULUM_NOMADNET_DIRECTORY_LIVE -eq '1') {
                & $send $native 'status'
                $identity = [regex]::Match((& $read $native 'Reticulum identity:'), '[0-9a-f]{32}').Value
                $identity | Should -Match '^[0-9a-f]{32}$'
                [IO.File]::WriteAllText((Join-Path $work 'python\storage\pages\private.mu.allowed'), $identity, [Text.UTF8Encoding]::new($false))
                $nativePages = if ($env:DOTRETICULUM_NOMADNET_CONFIG_LIVE -eq '1') { $customPages } else { Join-Path $work 'native\storage\pages' }
                [IO.File]::WriteAllText((Join-Path $nativePages 'private.mu.allowed'), $ready.identity, [Text.UTF8Encoding]::new($false))
                & $send $python 'observe-browser-links'
                $null = & $read $python '"event"\s*:\s*"observing-browser-links"'
                foreach ($command in @("contact add $($ready.node) Saved primary node",
                    "contact node $($ready.node) on", "contact rank $($ready.node) -5")) {
                    & $send $native $command
                    $null = & $read $native 'Contact (policy )?saved'
                }
                & $send $native 'nodes'
                $null = & $read $native "Node 1: $($ready.node).*trust=2 rank=-5 identify=False"
                $null = & $read $native 'Nodes complete'
                & $send $native "browse $($ready.node) /page/private.mu"
                $null = & $read $native 'Request Not Allowed'
                & $send $python 'browser-links 1'
                $observed = ((& $read $python '"event"\s*:\s*"browser-links"').Substring(2) | ConvertFrom-Json)
                $observed.identities.Count | Should -Be 1
                $observed.identities[0] | Should -BeNullOrEmpty
                & $send $native "contact identify $($ready.node) on"
                $null = & $read $native 'Contact policy saved'
                & $send $native 'reload'
                # Windows primary executes every .allowed file due to os.access(X_OK);
                # the plain-text list fails as an executable. Observe identification
                # separately instead of disguising that upstream authorization defect.
                $expected = if ([OperatingSystem]::IsWindows()) { 'Request Not Allowed' } else { 'DIRECTORY_PRIVATE' }
                $null = & $read $native $expected
                & $send $python 'browser-links 1'
                $observed = ((& $read $python '"event"\s*:\s*"browser-links"').Substring(2) | ConvertFrom-Json)
                @($observed.identities) | Should -Be @($identity)
                & $send $native "contact identify $($ready.node) off"
                $null = & $read $native 'Contact policy saved'
                & $send $native 'reload'
                $null = & $read $native 'Request Not Allowed'
                & $send $python 'browser-links 1'
                $observed = ((& $read $python '"event"\s*:\s*"browser-links"').Substring(2) | ConvertFrom-Json)
                $observed.identities.Count | Should -Be 1
                $observed.identities[0] | Should -BeNullOrEmpty
                & $send $native 'open-node 1'
                $null = & $read $native 'Image application contract'
                & $send $python "request $nativeNode /page/private.mu"
                $response = ((& $read $python '"event"\s*:\s*"response"').Substring(2) | ConvertFrom-Json)
                $response.text | Should -Match 'DIRECTORY_PRIVATE'
            }
            if ($env:DOTRETICULUM_NOMADNET_CONVERSATION_LIVE -eq '1') {
                $nativeDelivery = [regex]::Match([IO.File]::ReadAllText((Join-Path $work 'native.stdout')),
                    'LXMF delivery address:\s*([0-9a-f]{32})').Groups[1].Value
                $nativeDelivery | Should -Match '^[0-9a-f]{32}$'
                foreach ($command in @("contact add $($ready.delivery) Actual primary",
                    "contact trust $($ready.delivery) trusted", "contact delivery $($ready.delivery) opportunistic",
                    "contact notes $($ready.delivery) generated acceptance note")) {
                    & $send $native $command
                    $null = & $read $native 'Contact (policy )?saved'
                }
                & $send $native "send $($ready.delivery) CONVERSATION_OPPORTUNISTIC_NATIVE"
                $received = ((& $read $python '"event"\s*:\s*"received"').Substring(2) | ConvertFrom-Json)
                $received.content | Should -Be 'CONVERSATION_OPPORTUNISTIC_NATIVE'
                $received.method | Should -Be 1
                & $send $python 'conversation-stats'
                $stats = ((& $read $python '"event"\s*:\s*"conversations"').Substring(2) | ConvertFrom-Json)
                @($stats.entries | Where-Object hash -EQ $nativeDelivery)[0].unread | Should -Be 1
                & $send $python "conversation-read $nativeDelivery"
                $null = & $read $python '"event"\s*:\s*"conversation-read"'
                & $send $python "send $nativeDelivery CONVERSATION_PRIMARY_INBOUND"
                $null = & $read $python '"event"\s*:\s*"delivered"'
                $stateFile = Join-Path $work 'native\state.json'
                $deadline = [DateTime]::UtcNow.AddSeconds(15)
                do {
                    $state = [IO.File]::ReadAllText($stateFile) | ConvertFrom-Json
                    if (@($state.messages | Where-Object content -EQ 'CONVERSATION_PRIMARY_INBOUND').Count -gt 0) { break }
                    Start-Sleep -Milliseconds 50
                } while ([DateTime]::UtcNow -lt $deadline)
                @($state.conversationAlerts | Where-Object peerHash -EQ $ready.delivery)[0].unread | Should -Be 1
                if ($env:DOTRETICULUM_NOMADNET_DIRECTORY_LIVE -eq '1') {
                    & $send $native "contact trust $($ready.delivery) trusted"
                    $null = & $read $native 'Contact policy saved'
                    & $send $native 'nodes'
                    $null = & $read $native "Node 1: $($ready.node).*trust=255 rank=-5 identify=False"
                    $null = & $read $native 'Nodes complete'
                }
                & $send $native 'conversations'
                $null = & $read $native "Conversation $($ready.delivery) unread=1 failed=0"
                $null = & $read $native 'Conversations complete'
                & $send $native "conversation active $($ready.delivery)"
                $null = & $read $native 'Conversation command complete'
                & $send $python "send $nativeDelivery CONVERSATION_ACTIVE_PRIMARY"
                $null = & $read $native 'Conversation received:.*CONVERSATION_ACTIVE_PRIMARY'
                $null = & $read $python '"event"\s*:\s*"delivered"'
                if ($env:DOTRETICULUM_NOMADNET_PRESENTATION_LIVE -eq '1') {
                    $presentationFixture = [IO.File]::ReadAllText((Join-Path $root 'tests\NomadNet.Tests\NomadNetPresentationVectors.json')) | ConvertFrom-Json
                    $presentationVector = @($presentationFixture.conversation | Where-Object name -EQ live-formatted)[0]
                    $formatted = $presentationVector.input
                    $payload = @{ text = $formatted; renderer = 2 } | ConvertTo-Json -Compress
                    & $send $python "send-formatted $nativeDelivery $payload"
                    $null = & $read $python '"event"\s*:\s*"queued"'
                    $visible = & $read $native ([regex]::Escape($presentationVector.visible))
                    ($visible -replace '^.*Conversation received:\s*', '') | Should -BeExactly $presentationVector.visible
                    $visible | Should -Not -Match '\*\*'
                    $null = & $read $python '"event"\s*:\s*"delivered"'
                    $state = & $readState
                    $stored = @($state.messages | Where-Object content -EQ $formatted)
                    $stored.Count | Should -Be 1
                    $stored[0].renderer | Should -Be 2
                    & $send $native "send $($ready.delivery) **NATIVE_MARKDOWN_COMPOSE**"
                    $received = ((& $read $python '"event"\s*:\s*"received".*NATIVE_MARKDOWN_COMPOSE').Substring(2) | ConvertFrom-Json)
                    $received.content | Should -Be '**NATIVE_MARKDOWN_COMPOSE**'
                    $received.renderer | Should -Be 2
                    & $send $native 'client markdown off'
                    $null = & $read $native 'Outgoing Markdown policy saved'
                    & $send $native "send $($ready.delivery) **NATIVE_LITERAL_COMPOSE**"
                    $received = ((& $read $python '"event"\s*:\s*"received".*NATIVE_LITERAL_COMPOSE').Substring(2) | ConvertFrom-Json)
                    $received.content | Should -Be '**NATIVE_LITERAL_COMPOSE**'
                    $received.renderer | Should -BeNullOrEmpty
                    & $send $native 'rrc render markdown off'
                    $null = & $read $native 'RRC rendering policy saved'
                    & $send $native 'rrc render micron off'
                    $null = & $read $native 'RRC rendering policy saved'
                    & $send $native 'client markdown on'
                    $null = & $read $native 'Outgoing Markdown policy saved'
                    & $send $native "paper-qr $($ready.delivery) NATIVE_QR_PAPER"
                    $null = & $read $native 'Paper conversation saved'
                    $downloadRoot = if ($env:DOTRETICULUM_NOMADNET_CONFIG_LIVE -eq '1') { $customDownloads } else { Join-Path $work 'native\downloads' }
                    $pngs = @(Get-ChildItem $downloadRoot -Filter 'LXM_*.png')
                    $pngs.Count | Should -Be 1
                    $pngs[0].Name | Should -Match '^LXM_[0-9a-f]{64}\.png$'
                    & $send $python "decode-qr $($pngs[0].FullName)"
                    $qrDecoded = ((& $read $python '"event"\s*:\s*"qr-decoded"').Substring(2) | ConvertFrom-Json)
                    & $send $python "ingest $($qrDecoded.uri)"
                    $received = ((& $read $python '"event"\s*:\s*"received".*NATIVE_QR_PAPER').Substring(2) | ConvertFrom-Json)
                    $qrFixture = [IO.File]::ReadAllText((Join-Path $root 'tests\NomadNet.Tests\NomadNetQrVectors.json')) | ConvertFrom-Json
                    $received.method | Should -Be $qrFixture.paperIngressMethod
                    $received.renderer | Should -BeNullOrEmpty
                    $ingested = ((& $read $python '"event"\s*:\s*"ingested"').Substring(2) | ConvertFrom-Json)
                    $ingested.accepted | Should -BeTrue
                    & $send $native "qr-show $($qrDecoded.uri)"
                    $null = & $read $native 'QR display complete'
                    [IO.File]::ReadAllText((Join-Path $work 'native.stdout')) | Should -Match ([regex]::Escape("`e[30;47m"))
                    & $send $native "qr $($qrDecoded.uri) exported.png"
                    $null = & $read $native 'Saved .*exported.png'
                    & $send $python "decode-qr $(Join-Path $downloadRoot 'exported.png')"
                    $exported = ((& $read $python '"event"\s*:\s*"qr-decoded"').Substring(2) | ConvertFrom-Json)
                    $exported.uri | Should -BeExactly $qrDecoded.uri
                    & $send $native "qr $($qrDecoded.uri) ../escape.png"
                    $null = & $read $native 'Error: Output name must be a plain file name' 60 $true
                    & $send $native "paper-save $($ready.delivery) NATIVE_URI_PAPER"
                    $null = & $read $native 'Paper conversation saved'
                    $uris = @(Get-ChildItem $downloadRoot -Filter 'LXM_*.txt')
                    $uris.Count | Should -Be 1
                    [IO.File]::ReadAllText($uris[0].FullName) | Should -Match '^lxm://[A-Za-z0-9_-]+\n$'
                    @((& $readState).messages | Where-Object status -EQ paper).Count | Should -Be 2
                    & $send $native 'client markdown off'
                    $null = & $read $native 'Outgoing Markdown policy saved'
                }
                & $send $native "conversation active off"
                $null = & $read $native 'Conversation command complete'
                & $send $native "conversation export $($ready.delivery) conversation.txt"
                $null = & $read $native 'Conversation command complete'
                $export = if ($env:DOTRETICULUM_NOMADNET_CONFIG_LIVE -eq '1') { Join-Path $customDownloads 'conversation.txt' }
                    else { Join-Path $work 'native\downloads\conversation.txt' }
                [IO.File]::ReadAllText($export) |
                    Should -Match 'CONVERSATION_ACTIVE_PRIMARY'
                & $send $native "contact ignore $($ready.delivery) on"
                $null = & $read $native 'Contact policy saved'
                & $send $python "paper $nativeDelivery CONVERSATION_IGNORED_PAPER"
                $paper = ((& $read $python '"event"\s*:\s*"paper"').Substring(2) | ConvertFrom-Json).uri
                & $send $native "ingest $paper"
                $null = & $read $native 'Paper ingest: Rejected'
            }
            if ($env:DOTRETICULUM_NOMADNET_INLINE_MEDIA_LIVE -eq '1') {
                & $send $native 'images never'
                $null = & $read $native 'Inline images: never\.'
            }
            & $send $native "browse $($ready.node)"
            $null = & $read $native 'Image application contract'
            if ($env:DOTRETICULUM_NOMADNET_INLINE_MEDIA_LIVE -eq '1') {
                $null = & $read $native 'INLINE_PARTIAL'
                & $send $native 'images load'
                $null = & $read $native 'Error: Inline images are disabled' 60 $true
                & $send $native 'images manual'
                $null = & $read $native 'Inline images: manual\.'
                & $send $native 'images load'
                $null = & $read $native '--- inline images updated ---'
                $null = & $read $native 'Inline image loading complete'
                $remoteImages = Join-Path $work 'native\storage\cache\remote-images'
                @(Get-ChildItem $remoteImages -Filter '*.webp').Count | Should -Be 1
                & $send $native 'images reload'
                $null = & $read $native 'Inline image loading complete'
                & $send $native 'images always'
                $null = & $read $native 'Inline images: always\.'
                $null = & $read $native '--- inline images updated ---'
                & $send $native 'images load'
                $null = & $read $native 'Inline image loading complete'
                & $send $native 'images manual'
                $null = & $read $native 'Inline images: manual\.'
                ([IO.File]::ReadAllText($settingsPath) | ConvertFrom-Json).imageLoading | Should -Be 1
            }
            & $send $native 'show-media 1'
            $null = & $read $native 'Image preview complete: 1200x600'
            & $send $native 'show-media 1 color'
            $null = & $read $native 'Image preview complete: 1200x600'
            & $send $native 'media 1 fetched.webp'
            $null = & $read $native 'Saved .*fetched.webp'
            if ($env:DOTRETICULUM_NOMADNET_CONFIG_LIVE -eq '1') {
                Test-Path (Join-Path $customDownloads 'fetched.webp') | Should -BeTrue
                Test-Path (Join-Path $work 'native\downloads\fetched.webp') | Should -BeFalse
            }
            & $send $python "request-media $nativeNode /media/picture.png"
            $image = ((& $read $python '"event"\s*:\s*"image-response"').Substring(2) | ConvertFrom-Json)
            $image.width | Should -Be 1200
            $image.height | Should -Be 600
            $image.source | Should -Be 'WebP'
            $image.format | Should -Be 'PNG'
            if ($env:DOTRETICULUM_NOMADNET_CLIENT_ANNOUNCE_LIVE -eq '1') {
                $deadline = $clientStarted.AddSeconds(125)
                do {
                    & $send $python "client-announces $nativeDelivery"
                    $clientObservation = ((& $read $python '"event"\s*:\s*"client-announces"').Substring(2) | ConvertFrom-Json)
                    if ($clientObservation.count -eq 0) { Start-Sleep -Milliseconds 500 }
                } while ($clientObservation.count -eq 0 -and [DateTimeOffset]::UtcNow -lt $deadline)
                $clientObservation.count | Should -Be 1
                ($clientObservation.utc - $clientStarted.ToUnixTimeMilliseconds() / 1000) |
                    Should -BeGreaterOrEqual 89 -Because 'the source-required 90-second maintenance deferral is real elapsed time'
                $clientObservation.stamp_cost | Should -Be 13
                & $send $native 'client interval 29'
                $null = & $read $native 'Error: Client announce interval must be 30' 60 $true
                & $send $native 'client stamp-cost 255'
                $null = & $read $native 'Error: Inbound stamp cost must be null or 1' 60 $true
                & $send $native 'client stamp-cost 14'
                $null = & $read $native 'Client stamp cost saved'
                # RNS requires a strictly newer whole-second emission for the same-hop route.
                Start-Sleep -Milliseconds 1100
                & $send $native 'client announce'
                $null = & $read $native 'Client destination announced'
                & $send $python "client-announces $nativeDelivery 2"
                $clientObservation = ((& $read $python '"event"\s*:\s*"client-announces"').Substring(2) | ConvertFrom-Json)
                $clientObservation.count | Should -Be 2
                $clientObservation.stamp_cost | Should -Be 14
                if ($env:DOTRETICULUM_NOMADNET_PRESENTATION_LIVE -eq '1') {
                    $beforeName = ([IO.File]::ReadAllText($settingsPath) | ConvertFrom-Json).lastClientAnnounce
                    & $send $native 'client name Native café 雨'
                    $null = & $read $native 'Client display name saved without announce: Native café 雨'
                    & $send $native ('client name ' + ('x' * 81))
                    $null = & $read $native 'Error: Client display name must contain 1' 60 $true
                    & $send $native "client name invalid`ename"
                    $null = & $read $native 'Error: Client display name must contain 1' 60 $true
                    $namedSettings = [IO.File]::ReadAllText($settingsPath) | ConvertFrom-Json
                    $namedSettings.displayName | Should -BeExactly 'Native café 雨'
                    $namedSettings.lastClientAnnounce | Should -Be $beforeName
                    & $send $python "client-announces $nativeDelivery"
                    $unchanged = ((& $read $python '"event"\s*:\s*"client-announces"').Substring(2) | ConvertFrom-Json)
                    $unchanged.count | Should -Be 2
                    Start-Sleep -Milliseconds 1100
                    & $send $native 'client announce'
                    $null = & $read $native 'Client destination announced'
                    & $send $python "client-announces $nativeDelivery 3"
                    $renamed = ((& $read $python '"event"\s*:\s*"client-announces"').Substring(2) | ConvertFrom-Json)
                    $renamed.count | Should -Be 3
                    $renamed.name | Should -BeExactly 'Native café 雨'
                    $renamed.stamp_cost | Should -Be 14
                }
                $savedClientSettings = [IO.File]::ReadAllText($settingsPath) | ConvertFrom-Json
                $savedClientSettings.clientAnnounceIntervalMinutes | Should -Be 30
                $savedClientSettings.inboundStampCost | Should -Be 14
                $savedClientSettings.lastClientAnnounce | Should -Not -BeNullOrEmpty
                $successfulClientTimestamp = $savedClientSettings.lastClientAnnounce
            }
            & $send $python 'quit'
            $python.WaitForExit(20000) | Should -BeTrue
            $python = & $start 'python-restarted-cache-only' $env:DOTRETICULUM_PYTHON $pythonArgs 'missing'
            $restarted = ((& $read $python '"event"\s*:\s*"ready"').Substring(2) | ConvertFrom-Json)
            $restarted.node | Should -Be $ready.node
            & $send $native "browse $($ready.node)"
            $null = & $read $native 'Image application contract'
            & $send $native 'show-media 1'
            $null = & $read $native 'Image preview complete: 1200x600'
            & $send $native 'quit'
            $native.WaitForExit(20000) | Should -BeTrue
            $settings = [IO.File]::ReadAllText($settingsPath) | ConvertFrom-Json
            $settings.mediaConverter = 'missing-image-converter'
            $settings | ConvertTo-Json | Set-Content $settingsPath -Encoding utf8
            $native = & $start 'native-restarted-cache-only' $env:DOTRETICULUM_NOMADNET_BINARY $nativeArgs
            $restoredNode = [regex]::Match((& $read $native 'Hosted node address:'), '[0-9a-f]{32}').Value
            $restoredNode | Should -Be $nativeNode
            if ($env:DOTRETICULUM_NOMADNET_CLIENT_ANNOUNCE_LIVE -eq '1') {
                & $send $native 'client status'
                $null = & $read $native 'Client startup announce: False; interval minutes: 30; last successful announce:'
                $null = & $read $native 'enable_client metadata: False.*stamp cost: 14'
                ([IO.File]::ReadAllText($settingsPath) | ConvertFrom-Json).lastClientAnnounce | Should -Be $successfulClientTimestamp
                if ($env:DOTRETICULUM_NOMADNET_PRESENTATION_LIVE -eq '1') {
                    $null = & $read $native 'Client display name: Native café 雨'
                    $restored = [IO.File]::ReadAllText($settingsPath) | ConvertFrom-Json
                    $restored.composeInMarkdown | Should -BeFalse
                    $restored.rrcRenderMarkdown | Should -BeFalse
                    $restored.rrcRenderMicron | Should -BeFalse
                    $formattedState = & $readState
                    @($formattedState.messages | Where-Object renderer -EQ 2).Count | Should -BeGreaterThan 0
                    @($formattedState.messages | Where-Object content -EQ $formatted)[0].content | Should -BeExactly $formatted
                    @($formattedState.messages | Where-Object status -EQ paper).Count | Should -Be 2
                }
            }
            if ($env:DOTRETICULUM_NOMADNET_HISTORY_LIVE -eq '1') {
                & $send $native 'history'
                $null = & $read $native 'History complete: 0/0\.'
            }
            if ($env:DOTRETICULUM_NOMADNET_DIRECTORY_LIVE -eq '1') {
                & $send $native 'nodes'
                $null = & $read $native "Node 1: $($ready.node).*trust=255 rank=-5 identify=False"
                $null = & $read $native 'Nodes complete'
                & $send $native "contact identify $($ready.node) on"
                $null = & $read $native 'Contact policy saved'
                & $send $native "browse $($ready.node) /page/private.mu"
                $null = & $read $native 'Request Not Allowed'
                & $send $python 'wait-network'
                $null = & $read $python '"event"\s*:\s*"network-ready"'
                & $send $native 'reload'
                $expected = if ([OperatingSystem]::IsWindows()) { 'Request Not Allowed' } else { 'DIRECTORY_PRIVATE' }
                $null = & $read $native $expected
            }
            if ($env:DOTRETICULUM_NOMADNET_CONVERSATION_LIVE -eq '1') {
                & $send $native 'contacts'
                $line = & $read $native "$($ready.delivery).*trust=255 ignored=True delivery=3"
                $line | Should -Match 'generated acceptance note'
                $null = & $read $native 'Contacts complete'
                & $send $native 'conversations'
                $null = & $read $native "Conversation $($ready.delivery) unread=0 failed=0"
                $null = & $read $native 'Conversations complete'
                & $send $native "contact remove $($ready.delivery)"
                $null = & $read $native 'Contact removed'
                $state = & $readState
                $state.ignoredDestinations | Should -Contain $ready.delivery
                & $send $python "paper $nativeDelivery CONVERSATION_IGNORED_AFTER_RESTART"
                $paper = ((& $read $python '"event"\s*:\s*"paper"').Substring(2) | ConvertFrom-Json).uri
                & $send $native "ingest $paper"
                $null = & $read $native 'Paper ingest: Rejected'
                & $send $native "contact ignore $($ready.delivery) off"
                $null = & $read $native 'Contact policy saved'
                & $send $python "paper $nativeDelivery CONVERSATION_ALLOWED_AFTER_UNIGNORE"
                $paper = ((& $read $python '"event"\s*:\s*"paper"').Substring(2) | ConvertFrom-Json).uri
                & $send $native "ingest $paper"
                $null = & $read $native 'Paper ingest: LocalDelivery'
                $deadline = [DateTime]::UtcNow.AddSeconds(15)
                do {
                    $state = & $readState
                    if (@($state.messages | Where-Object content -EQ 'CONVERSATION_ALLOWED_AFTER_UNIGNORE').Count -gt 0) { break }
                    Start-Sleep -Milliseconds 50
                } while ([DateTime]::UtcNow -lt $deadline)
                @($state.messages | Where-Object content -EQ 'CONVERSATION_ALLOWED_AFTER_UNIGNORE').Count | Should -Be 1
                @($state.messages | Where-Object content -Match 'CONVERSATION_IGNORED').Count | Should -Be 0
                & $send $native "conversation delete $($ready.delivery)"
                $null = & $read $native 'Conversation command complete'
                $state = & $readState
                @($state.messages | Where-Object peerHash -EQ $ready.delivery).Count | Should -Be 0
                $state.ignoredDestinations | Should -Not -Contain $ready.delivery
            }
            & $send $python 'wait-network'
            $null = & $read $python '"event"\s*:\s*"network-ready"'
            & $send $native 'announce'
            $null = & $read $native 'Destinations announced'
            if ($env:DOTRETICULUM_NOMADNET_FILE_PICKER_LIVE -eq '1') {
                $pickerRoot = Join-Path $work 'Picker Files'
                $null = New-Item (Join-Path $pickerRoot 'Alpha Dir') -ItemType Directory -Force
                $first = Join-Path $pickerRoot 'first.bin'
                $second = Join-Path $pickerRoot 'z-note.txt'
                $payload = [Security.Cryptography.RandomNumberGenerator]::GetBytes(65536)
                [IO.File]::WriteAllBytes($first, $payload)
                [IO.File]::WriteAllText($second, 'GENERATED_PICKER_NOTE', [Text.UTF8Encoding]::new($false))
                $pickerCommand = {
                    param([string] $command)
                    & $send $native $command
                    $rows = [Collections.Generic.List[string]]::new()
                    do {
                        $row = & $read $native 'File entry |Files listed:'
                        $rows.Add($row)
                    } while ($row -notmatch 'Files listed:')
                    return ,$rows.ToArray()
                }.GetNewClosure()
                $rows = & $pickerCommand "files browse $pickerRoot"
                $firstIndex = [regex]::Match(($rows -join "`n"), 'File entry (\d+): file first.bin').Groups[1].Value
                $secondIndex = [regex]::Match(($rows -join "`n"), 'File entry (\d+): file z-note.txt').Groups[1].Value
                $directoryIndex = [regex]::Match(($rows -join "`n"), 'File entry (\d+): directory Alpha Dir').Groups[1].Value
                $firstIndex | Should -Not -BeNullOrEmpty
                $secondIndex | Should -Not -BeNullOrEmpty
                $directoryIndex | Should -Not -BeNullOrEmpty
                (& $pickerCommand "files open $firstIndex")[-1] | Should -Match 'selected: 1\.'
                (& $pickerCommand "files open $directoryIndex")[-1] | Should -Match 'selected: 1\.'
                (& $pickerCommand 'files up')[-1] | Should -Match 'selected: 1\.'
                (& $pickerCommand "files open $secondIndex")[-1] | Should -Match 'selected: 2\.'
                & $send $native 'files done'
                $null = & $read $native 'File selection saved: 2\.'
                $null = & $pickerCommand "files browse $pickerRoot"
                (& $pickerCommand "files open $firstIndex")[-1] | Should -Match 'selected: 1\.'
                & $send $native 'files cancel'
                $null = & $read $native 'File selection cancelled; saved: 2\.'
                & $send $native 'files selected'
                $null = & $read $native 'Selected file: first.bin'
                $null = & $read $native 'Selected file: z-note.txt'
                $null = & $read $native 'Selected files complete: 2\.'
                & $send $native "send-files $($ready.delivery) NATIVE_PICKER_MULTIPLE"
                $null = & $read $native 'Message queued:'
                $received = ((& $read $python '"event"\s*:\s*"received".*NATIVE_PICKER_MULTIPLE').Substring(2) | ConvertFrom-Json)
                @($received.files).Count | Should -Be 2
                for ($index = 0; $index -lt 2; $index++) {
                    $file = @($first, $second)[$index]
                    $received.files[$index].name | Should -Be ([IO.Path]::GetFileName($file))
                    $received.files[$index].size | Should -Be ([IO.FileInfo]::new($file).Length)
                    $received.files[$index].sha256 | Should -Be ((Get-FileHash $file).Hash.ToLowerInvariant())
                }
                & $send $native 'files selected'
                $null = & $read $native 'Selected files complete: 0\.'
                $nativeDelivery = [regex]::Match([IO.File]::ReadAllText((Join-Path $work 'native.stdout')),
                    'LXMF delivery address: ([0-9a-f]{32})').Groups[1].Value
                $nativeDelivery | Should -Match '^[0-9a-f]{32}$'
                $encodedPaths = ConvertTo-Json -InputObject @($first, $second) -Compress
                & $send $python "send-files $nativeDelivery $encodedPaths"
                $null = & $read $python '"event"\s*:\s*"delivered"'
                $deadline = [DateTime]::UtcNow.AddSeconds(15)
                do {
                    $state = & $readState
                    $incoming = @($state.messages | Where-Object content -Match '^upstream-multiple-attachments')
                    if ($incoming.Count -eq 1) { break }
                    Start-Sleep -Milliseconds 50
                } while ([DateTime]::UtcNow -lt $deadline)
                $incoming.Count | Should -Be 1
                for ($index = 0; $index -lt 2; $index++) {
                    $file = @($first, $second)[$index]
                    $stored = Join-Path $work "native/attachments/$($incoming[0].id)/$index.bin"
                    (Get-FileHash $stored).Hash | Should -Be (Get-FileHash $file).Hash
                    [IO.File]::ReadAllText("$stored.name") | Should -Be ([IO.Path]::GetFileName($file))
                }
            }
            & $send $python "request-media $nativeNode /media/picture.png"
            $image = ((& $read $python '"event"\s*:\s*"image-response"').Substring(2) | ConvertFrom-Json)
            $image.width | Should -Be 1200
            $image.height | Should -Be 600
            $image.format | Should -Be 'PNG'
            if ($env:DOTRETICULUM_NOMADNET_INLINE_MEDIA_LIVE -eq '1') {
                & $send $python 'quit'
                $python.WaitForExit(20000) | Should -BeTrue
                $python.ExitCode | Should -Be 0
                & $send $native 'quit'
                $native.WaitForExit(20000) | Should -BeTrue
                $native.ExitCode | Should -Be 0
                $cachedImage = @(Get-ChildItem $remoteImages -Filter '*.webp')
                $cachedImage.Count | Should -Be 1
                $cachedHash = (Get-FileHash $cachedImage[0].FullName).Hash
                $settings = [IO.File]::ReadAllText($settingsPath) | ConvertFrom-Json
                $settings.mediaConverter = $env:DOTRETICULUM_NOMADNET_IMAGE_CONVERTER
                $settings.imageLoading = 1
                $settings | ConvertTo-Json | Set-Content $settingsPath -Encoding utf8
                $native = & $start 'native-offline-inline-cache' $env:DOTRETICULUM_NOMADNET_BINARY $nativeArgs
                $null = & $read $native 'Hosted node address:'
                & $send $native "browse $($ready.node)"
                $null = & $read $native '\(cached page\)'
                & $send $native 'images load'
                $null = & $read $native '--- inline images updated ---'
                $null = & $read $native 'Inline image loading complete'
                (Get-FileHash $cachedImage[0].FullName).Hash | Should -Be $cachedHash
                & $send $native 'quit'
                $native.WaitForExit(20000) | Should -BeTrue
                $native.ExitCode | Should -Be 0
            }
            @{ binary = (Get-FileHash $env:DOTRETICULUM_NOMADNET_BINARY -Algorithm SHA256).Hash;
                converter = (Get-FileHash $env:DOTRETICULUM_NOMADNET_IMAGE_CONVERTER -Algorithm SHA256).Hash;
                primary = $ready.revision; width = $image.width; height = $image.height;
                nativeRestartCache = $true; primaryRestartCache = $true;
                conversations = ($env:DOTRETICULUM_NOMADNET_CONVERSATION_LIVE -eq '1');
                coreConfiguration = ($env:DOTRETICULUM_NOMADNET_CONFIG_LIVE -eq '1');
                navigation = ($env:DOTRETICULUM_NOMADNET_HISTORY_LIVE -eq '1');
                directory = ($env:DOTRETICULUM_NOMADNET_DIRECTORY_LIVE -eq '1');
                hostPolicy = ($env:DOTRETICULUM_NOMADNET_HOST_POLICY_LIVE -eq '1');
                filePicker = ($env:DOTRETICULUM_NOMADNET_FILE_PICKER_LIVE -eq '1');
                clientAnnounce = ($env:DOTRETICULUM_NOMADNET_CLIENT_ANNOUNCE_LIVE -eq '1');
                presentation = ($env:DOTRETICULUM_NOMADNET_PRESENTATION_LIVE -eq '1');
                inlineMedia = ($env:DOTRETICULUM_NOMADNET_INLINE_MEDIA_LIVE -eq '1');
                primaryStaticAuthorization = if ([OperatingSystem]::IsWindows()) { 'upstream Windows X_OK defect; actual identity observed instead' } else { 'allowed and denied' } } |
                ConvertTo-Json | Set-Content (Join-Path $work 'acceptance.json') -Encoding utf8
        }
        finally {
            foreach ($process in $processes) {
                if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
                if ($errors.ContainsKey($process.Id)) {
                    try { $errors[$process.Id].WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult() |
                        Set-Content (Join-Path $work "$($names[$process.Id]).stderr") -Encoding utf8 }
                    catch [TimeoutException] { }
                }
                $process.Dispose()
            }
            if ($env:DOTRETICULUM_NOMADNET_KEEP_WORK -ne '1' -and (Test-Path $work)) { Remove-Item $work -Recurse -Force }
        }
    }
}

Describe 'NomadNet mixed propagation and impaired application workflows' -Tag 'Live', 'Propagation' {
    It 'retrieves through both implementations across node restarts, schedules and cancels sync, and recovers a dropped Resource part' {
        if (-not [OperatingSystem]::IsLinux() -or $env:DOTRETICULUM_NOMADNET_PROPAGATION_LIVE -ne '1') {
            Set-ItResult -Skipped -Because 'Enable Linux actual-app propagation acceptance with DOTRETICULUM_NOMADNET_PROPAGATION_LIVE=1.'
            return
        }
        foreach ($name in @('DOTRETICULUM_PYTHON', 'DOTRETICULUM_UPSTREAM', 'DOTRETICULUM_LXMF_UPSTREAM',
            'DOTRETICULUM_NOMADNET_SOURCE', 'DOTRETICULUM_NOMADNET_BINARY')) {
            if (-not [Environment]::GetEnvironmentVariable($name)) { throw "$name is required." }
        }
        $root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $base = if ($env:DOTRETICULUM_NOMADNET_WORK) { $env:DOTRETICULUM_NOMADNET_WORK } else { Join-Path (Get-Location) 'artifacts/tests/nomadnet-propagation' }
        $work = "$base-$([Guid]::NewGuid().ToString('N'))"
        $null = New-Item $work -ItemType Directory -Force
        $processes = [Collections.Generic.List[Diagnostics.Process]]::new()
        $pending = @{}
        $errors = @{}
        $names = @{}
        $start = {
            param([string] $name, [string] $binary, [string[]] $arguments)
            $info = [Diagnostics.ProcessStartInfo]::new($binary)
            $info.WorkingDirectory = $root
            $info.UseShellExecute = $false
            $info.RedirectStandardInput = $true
            $info.RedirectStandardOutput = $true
            $info.RedirectStandardError = $true
            $info.Environment['PYTHONPATH'] = "$env:DOTRETICULUM_NOMADNET_SOURCE`:$env:DOTRETICULUM_UPSTREAM`:$env:DOTRETICULUM_LXMF_UPSTREAM`:$root/tests/Interop"
            $info.Environment['PYTHON_CPU_COUNT'] = '2'
            foreach ($argument in $arguments) { $info.ArgumentList.Add($argument) }
            $process = [Diagnostics.Process]::new()
            $process.StartInfo = $info
            $null = $process.Start()
            $processes.Add($process)
            $errors[$process.Id] = $process.StandardError.ReadToEndAsync()
            $names[$process.Id] = $name
            return $process
        }.GetNewClosure()
        $read = {
            param($process, [string] $pattern, [int] $seconds = 120)
            $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
            while ([DateTime]::UtcNow -lt $deadline) {
                if (-not $pending.ContainsKey($process.Id)) { $pending[$process.Id] = $process.StandardOutput.ReadLineAsync() }
                try { $line = $pending[$process.Id].WaitAsync([TimeSpan]::FromMilliseconds(500)).GetAwaiter().GetResult() }
                catch [TimeoutException] { continue }
                $pending.Remove($process.Id)
                if ($null -eq $line) { throw "$($names[$process.Id]) closed stdout before '$pattern'; diagnostics $work" }
                Add-Content (Join-Path $work "$($names[$process.Id]).stdout") $line
                if ($line -match '"event"\s*:\s*"error"' -or $line -match '(^|nomadnet> )Error:') { throw $line }
                if ($line -match $pattern) { return $line }
            }
            throw "$($names[$process.Id]) timed out waiting for '$pattern'; diagnostics $work"
        }.GetNewClosure()
        $json = { param([string] $line) $line.Substring($line.IndexOf('@@') + 2) | ConvertFrom-Json }
        $send = {
            param($process, [string] $text)
            $process.StandardInput.WriteLine($text)
            $process.StandardInput.Flush()
        }
        $waitState = {
            param([string] $profile, [string] $content, [string] $status, [int] $seconds = 120)
            $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
            while ([DateTime]::UtcNow -lt $deadline) {
                try {
                    $state = [IO.File]::ReadAllText((Join-Path $work "$profile/state.json")) | ConvertFrom-Json
                    if (@($state.messages | Where-Object { $_.content -eq $content -and $_.status -eq $status }).Count -gt 0) { return }
                }
                catch [IO.IOException] { }
                Start-Sleep -Milliseconds 100
            }
            throw "$profile did not persist '$content' as $status; diagnostics $work"
        }.GetNewClosure()
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $port = $listener.LocalEndpoint.Port
        $listener.Stop()
        if (-not ('NomadNetAcceptance.TcpImpairmentProxy' -as [type])) {
            Add-Type -Path (Join-Path $root 'tests/NomadNet.Tests/TcpImpairmentProxy.cs')
        }
        $proxy = [NomadNetAcceptance.TcpImpairmentProxy]::new($port)
        foreach ($name in @('python-node', 'python-client', 'native-node', 'native-client')) {
            $null = New-Item (Join-Path $work $name) -ItemType Directory -Force
            $null = New-Item (Join-Path $work "$name-rns") -ItemType Directory -Force
            $lines = @('[reticulum]', '  share_instance = No', '  enable_transport = No', '[logging]', '  loglevel = 2', '[interfaces]')
            if ($name -eq 'python-node') {
                $lines[2] = '  enable_transport = Yes'
                $lines += @('  [[Server]]', '    type = TCPServerInterface', '    enabled = Yes',
                    '    listen_ip = 127.0.0.1', "    listen_port = $port")
            }
            else {
                $target = if ($name -eq 'native-client') { $proxy.Port } else { $port }
                $lines += @('  [[Client]]', '    type = TCPClientInterface', '    enabled = Yes',
                    '    target_host = 127.0.0.1', "    target_port = $target")
            }
            $lines | Set-Content (Join-Path $work "$name-rns/config") -Encoding utf8
        }
        $pythonArgs = {
            param([string] $name)
            @('-u', (Join-Path $root 'tests/Interop/nomadnet_live_node.py'), '--config', (Join-Path $work $name),
                '--rnsconfig', (Join-Path $work "$name-rns"))
        }.GetNewClosure()
        $nativeArgs = {
            param([string] $name)
            @('--config', (Join-Path $work $name), '--rnsconfig', (Join-Path $work "$name-rns"))
        }.GetNewClosure()
        try {
            foreach ($name in @('python-node', 'python-client')) {
                $bootstrap = & $start "$name-bootstrap" $env:DOTRETICULUM_PYTHON @('-u', '-m', 'nomadnet.nomadnet',
                    '--daemon', '--console', '--config', (Join-Path $work $name), '--rnsconfig', (Join-Path $work "$name-rns"))
                $configuration = Join-Path $work "$name/config"
                $deadline = [DateTime]::UtcNow.AddSeconds(60)
                while (-not (Test-Path $configuration) -and [DateTime]::UtcNow -lt $deadline) {
                    if ($bootstrap.HasExited) { throw "Primary bootstrap exited: $($errors[$bootstrap.Id].GetAwaiter().GetResult())" }
                    Start-Sleep -Milliseconds 100
                }
                (Test-Path $configuration) | Should -BeTrue
                Start-Sleep -Seconds 1
                if (-not $bootstrap.HasExited) { Stop-Process -Id $bootstrap.Id -Force }
                $bootstrap.WaitForExit()
                $text = [IO.File]::ReadAllText($configuration).Replace('enable_node = no', 'enable_node = yes').
                    Replace('max_accepted_size = 500', 'max_accepted_size = 4000').
                    Replace('periodic_lxmf_sync = yes', 'periodic_lxmf_sync = no')
                if ($name -eq 'python-node') { $text = $text -replace '(?im)^([ \t]*disable_propagation[ \t]*=[ \t]*)yes[ \t]*$', '${1}no' }
                [IO.File]::WriteAllText($configuration, $text, [Text.UTF8Encoding]::new($false))
            }
            '{"schemaVersion":1,"displayName":"Native PN","enableNode":true,"enablePropagation":true,"periodicLxmfSync":false,"deliveryTransferLimitBytes":4000000}' |
                Set-Content (Join-Path $work 'native-node/settings.json')
            '{"schemaVersion":1,"displayName":"Native client","periodicLxmfSync":false,"deliveryTransferLimitBytes":4000000}' |
                Set-Content (Join-Path $work 'native-client/settings.json')
            $pythonNode = & $start 'python-node' $env:DOTRETICULUM_PYTHON (& $pythonArgs 'python-node')
            $pythonNodeReady = & $json (& $read $pythonNode '"event"\s*:\s*"ready"')
            $pythonNodeReady.propagation | Should -Match '^[0-9a-f]{32}$'
            $pythonClient = & $start 'python-client' $env:DOTRETICULUM_PYTHON (& $pythonArgs 'python-client')
            $pythonClientReady = & $json (& $read $pythonClient '"event"\s*:\s*"ready"')
            & $send $pythonClient 'observe-propagation-announces'
            $null = & $read $pythonClient '"event"\s*:\s*"observing-node-announces"'
            $nativeNode = & $start 'native-node' $env:DOTRETICULUM_NOMADNET_BINARY (& $nativeArgs 'native-node')
            $nativePn = [regex]::Match((& $read $nativeNode 'Propagation node address:'), '[0-9a-f]{32}').Value
            $native = & $start 'native-client' $env:DOTRETICULUM_NOMADNET_BINARY (& $nativeArgs 'native-client')
            $nativeDelivery = [regex]::Match((& $read $native 'LXMF delivery address:'), '[0-9a-f]{32}').Value
            & $send $pythonClient 'announce'
            $null = & $read $pythonClient '"event"\s*:\s*"announced"'
            & $send $native "trust-node $($pythonNodeReady.node) on"
            $null = & $read $native 'Node trust saved.'
            & $send $native 'propagation auto'
            $null = & $read $native 'Propagation selection saved.'
            & $send $pythonNode 'announce'
            $null = & $read $pythonNode '"event"\s*:\s*"announced"'
            $deadline = [DateTime]::UtcNow.AddSeconds(30)
            do {
                & $send $native 'sync status'
                $selection = & $read $native 'Propagation sync:'
                if ($selection -match $pythonNodeReady.propagation) { break }
                Start-Sleep -Seconds 2
            } while ([DateTime]::UtcNow -lt $deadline)
            $selection | Should -Match $pythonNodeReady.propagation

            & $send $native "propagation $nativePn"
            $null = & $read $native 'Propagation selection saved.'
            & $send $native "send-propagated $($pythonClientReady.delivery) native-pn-persisted"
            $null = & $read $native 'Message queued:'
            & $waitState 'native-client' 'native-pn-persisted' 'sent'
            & $send $pythonClient "node-announces $nativePn"
            $beforeRestart = (& $json (& $read $pythonClient '"event"\s*:\s*"node-announces"')).count
            foreach ($observer in @($pythonClient, $pythonNode)) {
                & $send $observer "path-stats $nativePn"
                $null = & $read $observer '"event"\s*:\s*"path-stats"'
            }
            & $send $nativeNode 'quit'
            $nativeNode.WaitForExit(15000) | Should -BeTrue
            $nativeNode = & $start 'native-node-restart' $env:DOTRETICULUM_NOMADNET_BINARY (& $nativeArgs 'native-node')
            [regex]::Match((& $read $nativeNode 'Propagation node address:'), '[0-9a-f]{32}').Value | Should -Be $nativePn
            $deadline = [DateTime]::UtcNow.AddSeconds(30)
            $restartRouteReady = $false
            do {
                & $send $pythonClient "node-announces $nativePn"
                $afterRestart = & $json (& $read $pythonClient '"event"\s*:\s*"node-announces"')
                if ($afterRestart.count -gt $beforeRestart) {
                    & $send $pythonNode "path-stats $nativePn"
                    $restartRoute = & $json (& $read $pythonNode '"event"\s*:\s*"path-stats"')
                    $restartRouteReady = $restartRoute.online -eq $true -and $restartRoute.active -eq $true
                }
                if (-not $restartRouteReady) { Start-Sleep -Milliseconds 250 }
            } while (-not $restartRouteReady -and [DateTime]::UtcNow -lt $deadline)
            $afterRestart.count | Should -BeGreaterThan $beforeRestart -Because 'a startup banner is not a refreshed live Reticulum path after restart'
            $restartRouteReady | Should -BeTrue -Because 'a delayed announce can leave the transport path bound to the stopped TCP interface'
            foreach ($observer in @($pythonClient, $pythonNode)) {
                & $send $observer "path-stats $nativePn"
                $null = & $read $observer '"event"\s*:\s*"path-stats"'
            }
            & $send $pythonClient "propagation $nativePn"
            $null = & $read $pythonClient '"event"\s*:\s*"propagation-selected"'
            & $send $pythonClient 'sync'
            $received = & $json (& $read $pythonClient '"event"\s*:\s*"received".*native-pn-persisted')
            $received.method | Should -Be 3
            $synced = & $json (& $read $pythonClient '"event"\s*:\s*"synced"')
            $synced.state | Should -Be 7
            & $send $pythonClient 'sync'
            (& $json (& $read $pythonClient '"event"\s*:\s*"synced"')).result | Should -Be 0

            & $send $pythonClient "propagation $($pythonNodeReady.propagation)"
            $null = & $read $pythonClient '"event"\s*:\s*"propagation-selected"'
            & $send $pythonClient "send-propagated $nativeDelivery python-pn-persisted"
            $null = & $read $pythonClient '"event"\s*:\s*"delivered"'
            & $send $pythonNode 'stats'
            (& $json (& $read $pythonNode '"event"\s*:\s*"stats"')).propagation_messages | Should -BeGreaterOrEqual 1
            & $send $pythonNode 'quit'
            $pythonNode.WaitForExit(15000) | Should -BeTrue
            $pythonNode = & $start 'python-node-restart' $env:DOTRETICULUM_PYTHON (& $pythonArgs 'python-node')
            (& $json (& $read $pythonNode '"event"\s*:\s*"ready"')).propagation | Should -Be $pythonNodeReady.propagation
            Start-Sleep -Seconds 8
            & $send $pythonClient 'announce'
            $null = & $read $pythonClient '"event"\s*:\s*"announced"'
            & $send $native 'announce'
            $null = & $read $native 'Destinations announced.'
            & $send $nativeNode 'announce'
            $null = & $read $nativeNode 'Destinations announced.'
            & $send $native "propagation $($pythonNodeReady.propagation)"
            $null = & $read $native 'Propagation selection saved.'
            & $send $native 'sync'
            $null = & $read $native 'Propagation sync: 1 messages \(Complete\)'
            & $waitState 'native-client' 'python-pn-persisted' 'received'
            & $send $native 'sync'
            $null = & $read $native 'Propagation sync: 0 messages \(Complete\)'

            & $send $native "propagation $('f' * 32)"
            $null = & $read $native 'Propagation selection saved.'
            & $send $native 'sync'
            $null = & $read $native 'Propagation sync started.'
            & $send $native 'sync cancel'
            $null = & $read $native 'Propagation sync cancelled.'
            & $send $native "propagation $($pythonNodeReady.propagation)"
            $null = & $read $native 'Propagation selection saved.'
            & $send $pythonClient "send-propagated $nativeDelivery scheduled-propagation"
            $null = & $read $pythonClient '"event"\s*:\s*"delivered"'
            & $send $native 'sync schedule on 1 1'
            $null = & $read $native 'Propagation sync: 1 messages \(Complete\)' 150
            & $waitState 'native-client' 'scheduled-propagation' 'received'
            & $send $native 'sync schedule off'

            $bytes = [byte[]]::new(2200000)
            [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
            $payload = Join-Path $work 'impaired.bin'
            [IO.File]::WriteAllBytes($payload, $bytes)
            $expected = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
            $proxy.ArmResourceDrop()
            & $send $native "attach $($pythonClientReady.delivery) $payload"
            $null = & $read $native 'Message queued:' 120
            $attachment = & $json (& $read $pythonClient '"event"\s*:\s*"received".*"files".*"size"\s*:\s*2200000' 180)
            $attachment.files[0].sha256 | Should -Be $expected
            $proxy.Dropped | Should -Be 1
            $proxy.ForwardedBytes | Should -BeGreaterThan $bytes.Length
            & $waitState 'native-client' '' 'delivered'
            & $send $pythonClient "send $nativeDelivery after-impaired-resource"
            $null = & $read $pythonClient '"event"\s*:\s*"delivered"'
            & $waitState 'native-client' 'after-impaired-resource' 'received'
            & $send $pythonClient 'quit'
            $pythonClient.WaitForExit(15000) | Should -BeTrue
            & $send $native "send $($pythonClientReady.delivery) direct-fallback-after-peer-exit"
            $null = & $read $native 'Message queued:'
            & $waitState 'native-client' 'direct-fallback-after-peer-exit' 'sent' 300
            $pythonClient = & $start 'python-client-restart' $env:DOTRETICULUM_PYTHON (& $pythonArgs 'python-client')
            (& $json (& $read $pythonClient '"event"\s*:\s*"ready"')).delivery | Should -Be $pythonClientReady.delivery
            & $send $pythonClient "propagation $($pythonNodeReady.propagation)"
            $null = & $read $pythonClient '"event"\s*:\s*"propagation-selected"'
            & $send $pythonClient 'sync'
            $null = & $read $pythonClient '"event"\s*:\s*"received".*direct-fallback-after-peer-exit'
            (& $json (& $read $pythonClient '"event"\s*:\s*"synced"')).state | Should -Be 7
            @{ droppedResourceParts = $proxy.Dropped; delayedFrames = $proxy.Forwarded; delayMilliseconds = 5;
                forwardedBytes = $proxy.ForwardedBytes;
                nativeBinary = $env:DOTRETICULUM_NOMADNET_BINARY; nativeSha256 = (Get-FileHash $env:DOTRETICULUM_NOMADNET_BINARY).Hash;
                primaryRevision = $pythonNodeReady.revision; payloadBytes = 2200000 } |
                ConvertTo-Json | Set-Content (Join-Path $work 'acceptance.json')
        }
        finally {
            foreach ($process in $processes) {
                if (-not $process.HasExited) { $process.Kill($true) }
                $null = $process.WaitForExit(10000)
                try { $errors[$process.Id].WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult() |
                    Set-Content (Join-Path $work "$($names[$process.Id]).stderr") } catch { }
                if ($names[$process.Id] -like '*-bootstrap') {
                    try { $process.StandardOutput.ReadToEndAsync().WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult() |
                        Set-Content (Join-Path $work "$($names[$process.Id]).stdout") } catch { }
                }
                $process.Dispose()
            }
            $proxy.DisposeAsync().AsTask().GetAwaiter().GetResult()
            if ($env:DOTRETICULUM_NOMADNET_KEEP_WORK -ne '1') { Remove-Item $work -Recurse -Force }
        }
    }
}

Describe 'NomadNet RRC primary-client interoperability' -Tag 'Live', 'Rrc' {
    It 'exchanges group messages and actions, restores room history, and submits Micron forms to primary executable pages' {
        if (-not [OperatingSystem]::IsLinux() -or $env:DOTRETICULUM_NOMADNET_RRC_LIVE -ne '1') {
            Set-ItResult -Skipped -Because 'Enable Linux actual-app RRC acceptance with DOTRETICULUM_NOMADNET_RRC_LIVE=1.'
            return
        }
        foreach ($name in @('DOTRETICULUM_PYTHON', 'DOTRETICULUM_UPSTREAM', 'DOTRETICULUM_LXMF_UPSTREAM',
            'DOTRETICULUM_NOMADNET_SOURCE', 'DOTRETICULUM_NOMADNET_RRCD_SOURCE', 'DOTRETICULUM_NOMADNET_BINARY')) {
            if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) { throw "$name is required." }
        }
        $root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $base = if ($env:DOTRETICULUM_NOMADNET_WORK) { $env:DOTRETICULUM_NOMADNET_WORK } else { Join-Path $root 'artifacts\tests\nomadnet-rrc' }
        $work = "$base-$([Guid]::NewGuid().ToString('N'))"
        $null = New-Item $work -ItemType Directory -Force
        $processes = [Collections.Generic.List[Diagnostics.Process]]::new()
        $pending = @{}
        $errors = @{}
        $names = @{}
        $environment = @{
            PYTHONPATH = "$env:DOTRETICULUM_NOMADNET_SOURCE`:$env:DOTRETICULUM_UPSTREAM`:$env:DOTRETICULUM_LXMF_UPSTREAM`:$root/tests/Interop"
        }
        $start = {
            param([string] $name, [string] $binary, [string[]] $arguments)
            $info = [Diagnostics.ProcessStartInfo]::new($binary)
            $info.WorkingDirectory = $root
            $info.UseShellExecute = $false
            $info.RedirectStandardInput = $true
            $info.RedirectStandardOutput = $true
            $info.RedirectStandardError = $true
            foreach ($argument in $arguments) { $info.ArgumentList.Add($argument) }
            foreach ($entry in $environment.GetEnumerator()) { $info.Environment[$entry.Key] = $entry.Value }
            $process = [Diagnostics.Process]::new()
            $process.StartInfo = $info
            $null = $process.Start()
            $processes.Add($process)
            $errors[$process.Id] = $process.StandardError.ReadToEndAsync()
            $names[$process.Id] = $name
            return $process
        }.GetNewClosure()
        $read = {
            param($process, [string] $pattern, [int] $seconds = 60)
            $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
            while ([DateTime]::UtcNow -lt $deadline) {
                if (-not $pending.ContainsKey($process.Id)) { $pending[$process.Id] = $process.StandardOutput.ReadLineAsync() }
                try { $line = $pending[$process.Id].WaitAsync([TimeSpan]::FromMilliseconds(500)).GetAwaiter().GetResult() }
                catch [TimeoutException] { continue }
                $pending.Remove($process.Id)
                if ($null -eq $line) { throw "$($names[$process.Id]) closed stdout before '$pattern'." }
                Add-Content (Join-Path $work "$($names[$process.Id]).stdout") $line
                if ($line -match '"event"\s*:\s*"error"') { throw $line }
                if ($line -match $pattern) { return $line }
            }
            throw "$($names[$process.Id]) timed out waiting for '$pattern'; diagnostics: $work"
        }.GetNewClosure()
        $send = {
            param($process, [string] $text)
            $process.StandardInput.WriteLine($text)
            $process.StandardInput.Flush()
        }
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $port = $listener.LocalEndpoint.Port
        $listener.Stop()
        foreach ($name in @('hub', 'python', 'native')) {
            $null = New-Item (Join-Path $work "$name-rns") -ItemType Directory -Force
            $null = New-Item (Join-Path $work $name) -ItemType Directory -Force
            $lines = @('[reticulum]', '  share_instance = No', '  enable_transport = No', '[logging]', '  loglevel = 2', '[interfaces]')
            if ($name -eq 'hub') {
                $lines[2] = '  enable_transport = Yes'
                $lines += @('  [[Server]]', '    type = TCPServerInterface', '    enabled = Yes',
                    '    listen_ip = 127.0.0.1', "    listen_port = $port")
            }
            else {
                $lines += @('  [[Client]]', '    type = TCPClientInterface', '    enabled = Yes',
                    '    target_host = 127.0.0.1', "    target_port = $port")
            }
            $lines | Set-Content (Join-Path $work "$name-rns\config") -Encoding utf8
        }
        try {
            $bootstrap = & $start 'bootstrap' $env:DOTRETICULUM_PYTHON @('-u', '-m', 'nomadnet.nomadnet',
                '--daemon', '--console', '--config', (Join-Path $work 'python'), '--rnsconfig', (Join-Path $work 'python-rns'))
            $configuration = Join-Path $work 'python\config'
            $deadline = [DateTime]::UtcNow.AddSeconds(60)
            while (-not (Test-Path $configuration) -and [DateTime]::UtcNow -lt $deadline) {
                if ($bootstrap.HasExited) { throw 'Primary app bootstrap failed.' }
                Start-Sleep -Milliseconds 100
            }
            (Test-Path $configuration) | Should -BeTrue
            Start-Sleep -Seconds 1
            Stop-Process -Id $bootstrap.Id -Force
            $bootstrap.WaitForExit()
            $text = [IO.File]::ReadAllText($configuration).Replace('enable_node = no', 'enable_node = yes')
            [IO.File]::WriteAllText($configuration, $text, [Text.UTF8Encoding]::new($false))
            $pages = Join-Path $work 'python\storage\pages'
            $null = New-Item $pages -ItemType Directory -Force
            $form = @'
#!c=0
>Application form
`<user`Alice>
`<!|password`not-a-real-secret>
`<?|flags|a|*`Alpha>
`<?|flags|b`Beta>
`<^|choice|one|*`One>
`<^|choice|two`Two>
`[submit`:/page/submit.mu`*|mode=accepted]
`{/page/partial.mu`2`*|pid=summary}
`->Folded
HIDDEN_FORM_SECTION
>Visible
VISIBLE_FORM_SECTION
`[refresh partial`p:summary]
'@
            [IO.File]::WriteAllText((Join-Path $pages 'form.mu'), $form.Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
            $submit = @'
#!/bin/sh
printf '#!c=0\nFORM_RESULT user=%s flags=%s choice=%s mode=%s extra=%s\n' "$field_user" "$field_flags" "$field_choice" "$var_mode" "$field_extra"
'@
            $partial = @'
#!/bin/sh
printf '#!c=0\nPARTIAL_RESULT user=%s flags=%s choice=%s pid=%s extra=%s\n`<extra`initial>\n`[child anchor`#child-anchor]\n>`:child-anchor Child content\n' "$field_user" "$field_flags" "$field_choice" "$var_pid" "$field_extra"
'@
            foreach ($entry in @{ 'submit.mu' = $submit; 'partial.mu' = $partial }.GetEnumerator()) {
                $path = Join-Path $pages $entry.Key
                [IO.File]::WriteAllText($path, $entry.Value.Replace("`r`n", "`n") + "`n", [Text.UTF8Encoding]::new($false))
                [IO.File]::SetUnixFileMode($path, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute)
            }
            $hubArgs = @('-u', (Join-Path $root 'tests\Interop\nomadnet_rrc_hub.py'),
                '--config', (Join-Path $work 'hub'), '--rnsconfig', (Join-Path $work 'hub-rns'))
            $hub = & $start 'hub' $env:DOTRETICULUM_PYTHON $hubArgs
            $ready = ((& $read $hub '"event"\s*:\s*"hub-ready"').Substring(2) | ConvertFrom-Json)
            $ready.revision | Should -Be 'f6d7e9d72bf83c70d7a9373ae8c5edaa052a7bf2'
            $python = & $start 'python' $env:DOTRETICULUM_PYTHON @('-u', (Join-Path $root 'tests\Interop\nomadnet_live_node.py'),
                '--config', (Join-Path $work 'python'), '--rnsconfig', (Join-Path $work 'python-rns'))
            $appReady = ((& $read $python '"event"\s*:\s*"ready"').Substring(2) | ConvertFrom-Json)
            & $send $python "rrc-connect $($ready.hash) Python"
            $null = & $read $python '"event"\s*:\s*"rrc-connected"'
            & $send $python 'rrc-join lobby'
            $null = & $read $python '"event"\s*:\s*"rrc-joined"'
            $nativeArgs = @('--config', (Join-Path $work 'native'), '--rnsconfig', (Join-Path $work 'native-rns'))
            $native = & $start 'native' $env:DOTRETICULUM_NOMADNET_BINARY $nativeArgs
            $null = & $read $native 'LXMF delivery address:'
            & $send $native "rrc connect $($ready.hash) Native"
            $null = & $read $native 'RRC command complete'
            & $send $native "browse $($appReady.node) /page/form.mu"
            $null = & $read $native 'VISIBLE_FORM_SECTION'
            $null = & $read $native 'PARTIAL_RESULT user=Alice flags=a choice=one pid=summary'
            & $send $native 'fields'
            $passwordLine = & $read $native 'Field 2: password'
            $passwordLine | Should -Not -Match 'not-a-real-secret'
            $null = & $read $native 'Fields complete'
            foreach ($edit in @('field 1 Bob', 'field 4 on', 'field 6 on')) {
                & $send $native $edit
                $null = & $read $native 'Field updated'
            }
            $null = & $read $native 'PARTIAL_RESULT user=Bob flags=a,b choice=two pid=summary'
            & $send $native 'field 7 child-edited'
            $null = & $read $native 'Field updated'
            & $send $native 'open 3'
            $null = & $read $native 'PARTIAL_RESULT user=Bob flags=a,b choice=two pid=summary extra=child-edited'
            & $send $native 'field 7 final-value'
            $null = & $read $native 'Field updated'
            & $send $native 'open 2'
            $null = & $read $native 'Child content'
            & $send $native 'open 1'
            $null = & $read $native 'FORM_RESULT user=Bob flags=a,b choice=two mode=accepted extra=final-value'
            if ($env:DOTRETICULUM_NOMADNET_HISTORY_LIVE -eq '1') {
                & $send $native 'back'
                $null = & $read $native 'Application form'
                $null = & $read $native 'PARTIAL_RESULT user=Alice flags=a choice=one pid=summary'
                & $send $native 'forward'
                $null = & $read $native 'FORM_RESULT user=Bob flags=a,b choice=two mode=accepted extra=final-value'
                & $send $native 'reload'
                $null = & $read $native 'FORM_RESULT user=Bob flags=a,b choice=two mode=accepted extra=final-value'
                & $send $native 'history'
                $null = & $read $native 'History complete: 2/2\.'
            }
            foreach ($setting in @('auto-list on', 'auto-who on', 'history-limit 100', 'active lobby')) {
                & $send $native "rrc $setting"
                $null = & $read $native 'RRC command complete'
            }
            & $send $native 'rrc join lobby'
            $null = & $read $native 'RRC command complete'
            $foundPython = $false
            for ($attempt = 0; $attempt -lt 40 -and -not $foundPython; $attempt++) {
                & $send $native 'rrc members lobby'
                $line = & $read $native 'RRC member [0-9a-f]+: Python|RRC command complete'
                if ($line -match 'RRC member [0-9a-f]+: Python') {
                    $foundPython = $true
                    $null = & $read $native 'RRC command complete'
                }
                else { Start-Sleep -Milliseconds 250 }
            }
            $foundPython | Should -BeTrue -Because 'automatic /who must populate the actual hub roster without a manual query'
            & $send $native 'rrc send lobby native-group-message'
            $null = & $read $native 'RRC command complete'
            $line = & $read $python '"text"\s*:\s*"native-group-message"'
            ($line.Substring(2) | ConvertFrom-Json).nick | Should -Be 'Native'
            & $send $python 'rrc-send lobby python-group-message'
            $null = & $read $native 'Python: python-group-message'
            & $send $native 'rrc action lobby native-group-action'
            $null = & $read $python '"kind"\s*:\s*"action".*"text"\s*:\s*"native-group-action"'
            & $send $python 'rrc-action lobby python-group-action'
            $null = & $read $native 'Python: python-group-action'
            & $send $native 'rrc status'
            $null = & $read $native 'active: lobby; unread: ; mentions: '
            $null = & $read $native 'RRC command complete'
            & $send $native 'rrc active off'
            $null = & $read $native 'RRC command complete'
            & $send $python 'rrc-send lobby hello @Native'
            $null = & $read $native 'Python: hello @Native'
            & $send $native 'rrc status'
            $null = & $read $native 'active: none; unread: lobby; mentions: lobby'
            $null = & $read $native 'RRC command complete'
            & $send $native 'rrc read lobby'
            $null = & $read $native 'RRC command complete'
            & $send $native 'rrc status'
            $null = & $read $native 'active: none; unread: ; mentions: '
            $null = & $read $native 'RRC command complete'
            & $send $native 'rrc ping'
            $null = & $read $native 'RRC pong:'
            & $send $native 'rrc history'
            $null = & $read $native 'RRC_RESOURCE_MOTD_'
            $null = & $read $native 'RRC command complete'
            & $send $native ('rrc send lobby ' + ('x' * 351))
            $null = & $read $native "Error: Message is empty or exceeds the hub's UTF-8 byte limit"
            & $send $native 'rrc disconnect'
            $null = & $read $native 'RRC command complete'
            & $send $native 'rrc reconnect'
            $null = & $read $native 'RRC command complete'
            & $send $python 'rrc-send lobby after-link-reconnect'
            $null = & $read $native 'Python: after-link-reconnect'
            & $send $native 'quit'
            $native.WaitForExit(20000) | Should -BeTrue
            $native.ExitCode | Should -Be 0
            $native = & $start 'native-restarted' $env:DOTRETICULUM_NOMADNET_BINARY $nativeArgs
            $null = & $read $native 'LXMF delivery address:'
            & $send $native "rrc connect $($ready.hash)"
            $null = & $read $native 'RRC command complete'
            & $send $native 'rrc status'
            $null = & $read $native 'nick: Native; rooms: lobby'
            $null = & $read $native 'RRC command complete'
            & $send $native 'rrc status'
            $null = & $read $native 'active: none;.*per-room history: 100; auto-list: True; auto-who: True'
            $null = & $read $native 'RRC command complete'
            & $send $native 'rrc history lobby'
            $null = & $read $native 'python-group-message'
            $null = & $read $native 'after-link-reconnect'
            $null = & $read $native 'RRC command complete'
            & $send $python 'rrc-send lobby after-process-restart'
            $null = & $read $native 'Python: after-process-restart'
            & $send $native 'rrc auto-reconnect on'
            $null = & $read $native 'RRC command complete'
            & $send $hub 'quit'
            $hub.WaitForExit(10000) | Should -BeTrue
            $hub = & $start 'hub-restarted' $env:DOTRETICULUM_PYTHON $hubArgs
            $restartedHub = ((& $read $hub '"event"\s*:\s*"hub-ready"').Substring(2) | ConvertFrom-Json)
            $restartedHub.hash | Should -Be $ready.hash
            $null = & $read $native 'RRC connected:' 90
            & $send $python "rrc-connect $($ready.hash) Python"
            $null = & $read $python '"event"\s*:\s*"rrc-connected"'
            & $send $python 'rrc-join lobby'
            $null = & $read $python '"event"\s*:\s*"rrc-joined"'
            & $send $python 'rrc-send lobby after-hub-restart'
            $null = & $read $native 'Python: after-hub-restart'
        }
        finally {
            foreach ($process in $processes) {
                if (-not $process.HasExited) {
                    try { & $send $process 'quit' } catch {}
                    if (-not $process.WaitForExit(5000)) { Stop-Process -Id $process.Id -Force; $process.WaitForExit() }
                }
                [IO.File]::WriteAllText((Join-Path $work "$($names[$process.Id]).stderr"), $errors[$process.Id].GetAwaiter().GetResult())
                $process.Dispose()
            }
            if ($env:DOTRETICULUM_NOMADNET_KEEP_WORK -ne '1') { Remove-Item $work -Recurse -Force }
        }
    }
}

Describe 'NomadNet native executable' -Tag 'Native' {
    BeforeAll {
        $script:NomadNetBinary = $env:DOTRETICULUM_NOMADNET_BINARY
        if ([string]::IsNullOrWhiteSpace($script:NomadNetBinary)) {
            $script:NomadNetBinary = (Get-Command nomadnet -ErrorAction SilentlyContinue)?.Source
        }
        if (-not [string]::IsNullOrWhiteSpace($script:NomadNetBinary)) {
            $script:NomadNetBinary = [IO.Path]::GetFullPath($script:NomadNetBinary)
        }
    }

    It 'prints help and version without starting a Reticulum node' {
        if ([string]::IsNullOrWhiteSpace($script:NomadNetBinary) -or -not (Test-Path $script:NomadNetBinary)) {
            Set-ItResult -Skipped -Because 'Set DOTRETICULUM_NOMADNET_BINARY to the published executable.'
            return
        }

        $help = & $script:NomadNetBinary --help 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 0
        $help | Should -Match 'NomadNet'
        $help | Should -Match 'browse'

        $version = & $script:NomadNetBinary --version 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 0
        $version | Should -Match '1\.4\.4'
    }

    It 'initializes and reloads bounded local state without network access' {
        if ([string]::IsNullOrWhiteSpace($script:NomadNetBinary) -or -not (Test-Path $script:NomadNetBinary)) {
            Set-ItResult -Skipped -Because 'Set DOTRETICULUM_NOMADNET_BINARY to the published executable.'
            return
        }

        $directory = Join-Path (Get-Location) 'artifacts\tests\nomadnet-pester'
        if (Test-Path $directory) { Remove-Item $directory -Recurse -Force }
        try {
            & $script:NomadNetBinary --init --config $directory
            $LASTEXITCODE | Should -Be 0
            Test-Path (Join-Path $directory 'settings.json') | Should -BeTrue
            Test-Path (Join-Path $directory 'state.json') | Should -BeTrue

            $status = & $script:NomadNetBinary --status --config $directory 2>&1 | Out-String
            $LASTEXITCODE | Should -Be 0
            $status | Should -Match 'Initialized: True'
            $status | Should -Match 'Contacts: 0'
        }
        finally {
            if (Test-Path $directory) { Remove-Item $directory -Recurse -Force }
        }
    }

    It 'starts the real terminal loop, reports status, exits cleanly and retains its LXMF identity' {
        if ([string]::IsNullOrWhiteSpace($script:NomadNetBinary) -or -not (Test-Path $script:NomadNetBinary)) {
            Set-ItResult -Skipped -Because 'Set DOTRETICULUM_NOMADNET_BINARY to the published executable.'
            return
        }

        $work = Join-Path (Get-Location) 'artifacts\tests\nomadnet-terminal-pester'
        if ((Test-Path $work) -and $env:DOTRETICULUM_NOMADNET_KEEP_WORK -ne '1') { Remove-Item $work -Recurse -Force }
        $data = Join-Path $work 'data'
        $rns = Join-Path $work 'rns'
        $null = New-Item $data -ItemType Directory -Force
        $null = New-Item $rns -ItemType Directory -Force
        @(
            '[reticulum]'
            '  enable_transport = No'
            '  share_instance = No'
            '  instance_name = nomadnet_native_acceptance'
            ''
            '[logging]'
            '  loglevel = 4'
            ''
            '[interfaces]'
        ) | Set-Content -Path (Join-Path $rns 'config') -Encoding utf8

        $process = $null
        $firstAddress = $null
        try {
            foreach ($run in 1..2) {
                $start = [Diagnostics.ProcessStartInfo]::new()
                $start.FileName = $script:NomadNetBinary
                $start.WorkingDirectory = (Get-Location).Path
                $start.UseShellExecute = $false
                $start.RedirectStandardInput = $true
                $start.RedirectStandardOutput = $true
                $start.RedirectStandardError = $true
                foreach ($argument in @('--config', $data, '--rnsconfig', $rns)) { $start.ArgumentList.Add($argument) }
                $process = [Diagnostics.Process]::new()
                $process.StartInfo = $start
                $null = $process.Start()
                $process.BeginErrorReadLine()
                $banner = $process.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(20)).GetAwaiter().GetResult()
                $addressLine = $process.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(20)).GetAwaiter().GetResult()
                $banner | Should -Match 'NomadNet'
                $addressLine | Should -Match 'LXMF delivery address:\s*([0-9a-f]{32})'
                $match = [regex]::Match($addressLine, 'LXMF delivery address:\s*([0-9a-f]{32})')
                $address = $match.Groups[1].Value
                if ($run -eq 1) { $firstAddress = $address } else { $address | Should -Be $firstAddress }

                $process.StandardInput.WriteLine('status')
                $process.StandardInput.WriteLine('contacts')
                $process.StandardInput.WriteLine('quit')
                $process.StandardInput.Flush()
                $process.WaitForExit(20000) | Should -BeTrue
                $process.ExitCode | Should -Be 0
                $process.Dispose()
                $process = $null
            }
        }
        finally {
            if ($null -ne $process) {
                if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
                $process.Dispose()
            }
            if (Test-Path $work) { Remove-Item $work -Recurse -Force }
        }
    }
}

Describe 'NomadNet native hosted workflows' -Tag 'Native' {
    It 'hosts pages and resources, caches pages, transfers attachments, ingests paper and reloads state' {
        if ([string]::IsNullOrWhiteSpace($env:DOTRETICULUM_NOMADNET_BINARY)) {
            Set-ItResult -Skipped -Because 'Select an exact published binary with DOTRETICULUM_NOMADNET_BINARY.'
            return
        }
        $binary = [IO.Path]::GetFullPath($env:DOTRETICULUM_NOMADNET_BINARY)
        if (-not (Test-Path $binary)) { throw "Selected binary does not exist: $binary" }
        $work = Join-Path (Get-Location) "artifacts\tests\nomadnet-functional-$([Guid]::NewGuid().ToString('N'))"
        $server = $null
        $client = $null
        $processes = [Collections.Generic.List[Diagnostics.Process]]::new()
        $readUntil = {
            param($process, [string] $pattern, [int] $seconds = 60)
            $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
            while ([DateTime]::UtcNow -lt $deadline) {
                $line = $process.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds($seconds)).GetAwaiter().GetResult()
                if ($null -eq $line) { throw "Process ended before '$pattern'." }
                if ($line -match $pattern) { return $line }
            }
            throw "Expected output was not observed: $pattern"
        }
        $start = {
            param([string] $name, [string[]] $extra = @())
            $info = [Diagnostics.ProcessStartInfo]::new($binary)
            $info.UseShellExecute = $false
            $info.RedirectStandardInput = $true
            $info.RedirectStandardOutput = $true
            $info.RedirectStandardError = $true
            foreach ($argument in @('--config', (Join-Path $work $name), '--rnsconfig', (Join-Path $work "$name-rns")) + $extra) {
                $info.ArgumentList.Add($argument)
            }
            $process = [Diagnostics.Process]::new()
            $process.StartInfo = $info
            $null = $process.Start()
            $process.BeginErrorReadLine()
            $processes.Add($process)
            return $process
        }.GetNewClosure()
        $send = {
            param($process, [string] $command)
            $process.StandardInput.WriteLine($command)
            $process.StandardInput.Flush()
        }
        try {
            $null = New-Item (Join-Path $work 'server-rns') -ItemType Directory -Force
            $null = New-Item (Join-Path $work 'client-rns') -ItemType Directory -Force
            $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
            $listener.Start()
            $port = $listener.LocalEndpoint.Port
            $listener.Stop()
            @('[reticulum]', '  share_instance = No', '  enable_transport = No', '[interfaces]',
                '  [[Server]]', '    type = TCPServerInterface', '    enabled = Yes',
                '    listen_ip = 127.0.0.1', "    listen_port = $port") |
                Set-Content (Join-Path $work 'server-rns\config') -Encoding utf8
            @('[reticulum]', '  share_instance = No', '  enable_transport = No', '[interfaces]',
                '  [[Client]]', '    type = TCPClientInterface', '    enabled = Yes',
                '    target_host = 127.0.0.1', "    target_port = $port") |
                Set-Content (Join-Path $work 'client-rns\config') -Encoding utf8
            $null = New-Item (Join-Path $work 'server\storage\pages') -ItemType Directory -Force
            $null = New-Item (Join-Path $work 'server\storage\files') -ItemType Directory -Force
            '{"schemaVersion":1,"displayName":"Native host","enableNode":true,"enablePropagation":true,"deliveryTransferLimitBytes":4000000}' |
                Set-Content (Join-Path $work 'server\settings.json') -Encoding utf8
            [IO.File]::WriteAllText((Join-Path $work 'server\storage\pages\index.mu'), "#!c=60`n>Hosted native page`nFIRST_PAGE", [Text.UTF8Encoding]::new($false))
            $partRoot = @'
#!c=0
`<root`outer>
`{/page/child.mu`1`*|pid=live}
'@
            [IO.File]::WriteAllText((Join-Path $work 'server\storage\pages\parts.mu'), $partRoot.Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
            [IO.File]::WriteAllText((Join-Path $work 'server\storage\pages\child.mu'), 'FIRST_PARTIAL' + "`n" + '`<child`initial>' + "`n" + '`[child anchor`#child-anchor]' + "`n" + '>`:child-anchor Child heading', [Text.UTF8Encoding]::new($false))
            if (-not [OperatingSystem]::IsWindows()) {
                [IO.File]::SetUnixFileMode((Join-Path $work 'server\storage\pages\index.mu'),
                    [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)
                foreach ($name in 'parts.mu', 'child.mu') {
                    [IO.File]::SetUnixFileMode((Join-Path $work "server\storage\pages\$name"),
                        [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)
                }
            }
            $bytes = [byte[]]::new(2200000)
            [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
            [IO.File]::WriteAllBytes((Join-Path $work 'server\storage\files\large.bin'), $bytes)
            $server = & $start 'server'
            $serverDelivery = [regex]::Match((& $readUntil $server 'LXMF delivery address:\s*([0-9a-f]{32})'), '[0-9a-f]{32}').Value
            $serverNode = [regex]::Match((& $readUntil $server 'Hosted node address:\s*([0-9a-f]{32})'), '[0-9a-f]{32}').Value
            $propagationNode = [regex]::Match((& $readUntil $server 'Propagation node address:\s*([0-9a-f]{32})'), '[0-9a-f]{32}').Value
            $client = & $start 'client'
            $clientDelivery = [regex]::Match((& $readUntil $client 'LXMF delivery address:\s*([0-9a-f]{32})'), '[0-9a-f]{32}').Value
            & $send $client "browse $serverNode"
            $null = & $readUntil $client 'FIRST_PAGE'
            [IO.File]::WriteAllText((Join-Path $work 'server\storage\pages\index.mu'), "#!c=60`nSECOND_PAGE", [Text.UTF8Encoding]::new($false))
            & $send $client "browse $serverNode"
            $null = & $readUntil $client '\(cached page\)'
            $null = & $readUntil $client 'FIRST_PAGE'
            & $send $client 'reload'
            $null = & $readUntil $client 'SECOND_PAGE'
            & $send $client "browse $serverNode /page/parts.mu"
            $null = & $readUntil $client 'FIRST_PARTIAL'
            & $send $client 'field 1 edited-root'
            $null = & $readUntil $client 'Field updated'
            [IO.File]::WriteAllText((Join-Path $work 'server\storage\pages\child.mu'), 'SECOND_PARTIAL' + "`n" + '`<child`replacement>' + "`n" + '`[child anchor`#child-anchor]' + "`n" + '>`:child-anchor Child heading', [Text.UTF8Encoding]::new($false))
            $null = & $readUntil $client 'SECOND_PARTIAL'
            & $send $client 'fields'
            $null = & $readUntil $client 'Field 1: root \(field\) = edited-root'
            $null = & $readUntil $client 'Field 2: child \(field\) = replacement'
            $null = & $readUntil $client 'Fields complete'
            & $send $client 'open 1'
            $null = & $readUntil $client 'Child heading'
            & $send $client "browse $serverNode"
            $null = & $readUntil $client 'SECOND_PAGE'
            & $send $client "fetch $serverNode /file/large.bin downloaded.bin"
            $null = & $readUntil $client 'Saved 2200000 bytes' 120
            $expected = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
            (Get-FileHash (Join-Path $work 'client\downloads\downloaded.bin') -Algorithm SHA256).Hash | Should -Be $expected
            & $send $client "attach $serverDelivery $(Join-Path $work 'client\downloads\downloaded.bin')"
            $null = & $readUntil $client 'Message queued:'
            $deadline = [DateTime]::UtcNow.AddSeconds(120)
            $attachment = $null
            while ([DateTime]::UtcNow -lt $deadline -and $null -eq $attachment) {
                $attachment = Get-ChildItem (Join-Path $work 'server\attachments') -Filter '0.bin' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($null -eq $attachment) { Start-Sleep -Milliseconds 100 }
            }
            $attachment | Should -Not -BeNullOrEmpty
            (Get-FileHash $attachment.FullName -Algorithm SHA256).Hash | Should -Be $expected
            & $send $client "paper $serverDelivery native-paper-content"
            $paperLine = & $readUntil $client 'lxm://[A-Za-z0-9_-]+'
            $uri = [regex]::Match($paperLine, 'lxm://[A-Za-z0-9_-]+').Value
            & $send $server "ingest $uri"
            $null = & $readUntil $server 'Paper ingest: LocalDelivery'
            & $send $server "ingest $uri"
            $null = & $readUntil $server 'Paper ingest: Duplicate'
            & $send $client "propagation $propagationNode"
            $null = & $readUntil $client 'Propagation selection saved.'
            & $send $client "send-propagated $clientDelivery propagated-native-content"
            $null = & $readUntil $client 'Message queued:'
            $deadline = [DateTime]::UtcNow.AddSeconds(90)
            $sent = $false
            while ([DateTime]::UtcNow -lt $deadline -and -not $sent) {
                try { $state = [IO.File]::ReadAllText((Join-Path $work 'client\state.json')) | ConvertFrom-Json }
                catch [IO.IOException] { Start-Sleep -Milliseconds 100; continue }
                $sent = @($state.messages | Where-Object { $_.content -eq 'propagated-native-content' -and $_.status -eq 'sent' }).Count -gt 0
                if (-not $sent) { Start-Sleep -Milliseconds 100 }
            }
            $sent | Should -BeTrue
            & $send $client 'sync'
            $null = & $readUntil $client 'Propagation sync: 1 messages \(Complete\)' 90
            & $send $client 'sync'
            $null = & $readUntil $client 'Propagation sync: 0 messages \(Complete\)' 30
            & $send $server 'quit'
            $server.WaitForExit(30000) | Should -BeTrue
            $server.ExitCode | Should -Be 0
            $server = & $start 'server'
            $restarted = [regex]::Match((& $readUntil $server 'LXMF delivery address:\s*([0-9a-f]{32})'), '[0-9a-f]{32}').Value
            $restarted | Should -Be $serverDelivery
            & $send $server 'messages'
            $null = & $readUntil $server 'native-paper-content'
            & $send $server 'quit'
            & $send $client 'quit'
            $server.WaitForExit(30000) | Should -BeTrue
            $client.WaitForExit(30000) | Should -BeTrue
            $server.ExitCode | Should -Be 0
            $client.ExitCode | Should -Be 0
        }
        finally {
            foreach ($process in $processes) {
                if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
                $process.Dispose()
            }
            if (Test-Path $work) { Remove-Item $work -Recurse -Force }
        }
    }
}

Describe 'NomadNet live LXMF interoperability' -Tag 'Live' {
    It 'exchanges direct messages with the pinned Python LXMF peer' {
        if ($env:DOTRETICULUM_NOMADNET_LIVE -ne '1' -or
            [string]::IsNullOrWhiteSpace($env:DOTRETICULUM_PYTHON) -or
            [string]::IsNullOrWhiteSpace($env:DOTRETICULUM_UPSTREAM) -or
            [string]::IsNullOrWhiteSpace($env:DOTRETICULUM_NOMADNET_BINARY)) {
            Set-ItResult -Skipped -Because 'Set DOTRETICULUM_NOMADNET_LIVE=1, DOTRETICULUM_PYTHON, DOTRETICULUM_UPSTREAM, and DOTRETICULUM_NOMADNET_BINARY.'
            return
        }

        $root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $interop = Join-Path $root 'tests\Interop\nomadnet_live_peer.py'
        $python = [IO.Path]::GetFullPath($env:DOTRETICULUM_PYTHON)
        $binary = [IO.Path]::GetFullPath($env:DOTRETICULUM_NOMADNET_BINARY)
        if (-not (Test-Path $interop) -or -not (Test-Path $binary)) {
            throw 'The NomadNet live adapter or native executable is missing.'
        }

        $work = Join-Path (Get-Location) "artifacts\tests\nomadnet-live-$([Guid]::NewGuid().ToString('N'))"
        if (Test-Path $work) { Remove-Item $work -Recurse -Force }
        $pythonConfig = Join-Path $work 'python'
        $appConfig = Join-Path $work 'app-rns'
        $appData = Join-Path $work 'app'
        $null = New-Item $pythonConfig -ItemType Directory -Force
        $null = New-Item $appConfig -ItemType Directory -Force
        $null = New-Item $appData -ItemType Directory -Force
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $port = $listener.LocalEndpoint.Port
        $listener.Stop()

        $appProcess = $null
        $peerProcess = $null
        try {
            $configLines = @(
                '[reticulum]'
                '  enable_transport = No'
                '  share_instance = No'
                '  instance_name = nomadnet_native_live'
                ''
                '[logging]'
                '  loglevel = 4'
                ''
                '[interfaces]'
                '  [[PythonReference]]'
                '    type = TCPClientInterface'
                '    enabled = Yes'
                '    target_host = 127.0.0.1'
                "    target_port = $port"
            )
            $configLines | Set-Content -Path (Join-Path $appConfig 'config') -Encoding utf8

            $peerStart = [Diagnostics.ProcessStartInfo]::new()
            $peerStart.FileName = $python
            $peerStart.WorkingDirectory = $root
            $peerStart.UseShellExecute = $false
            $peerStart.RedirectStandardInput = $true
            $peerStart.RedirectStandardOutput = $true
            $peerStart.RedirectStandardError = $true
            $peerStart.ArgumentList.Add($interop)
            $peerStart.ArgumentList.Add('--config')
            $peerStart.ArgumentList.Add($pythonConfig)
            $peerStart.ArgumentList.Add('--tcp-server-port')
            $peerStart.ArgumentList.Add("$port")
            $peerProcess = [Diagnostics.Process]::new()
            $peerProcess.StartInfo = $peerStart
            $null = $peerProcess.Start()
            $peerProcess.BeginErrorReadLine()
            $readyLine = $peerProcess.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(60)).GetAwaiter().GetResult()
            while ($readyLine -notmatch '"event"\s*:\s*"ready"') {
                if ($null -eq $readyLine) { throw 'Python LXMF peer exited before it became ready.' }
                $readyLine = $peerProcess.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(60)).GetAwaiter().GetResult()
            }
            $peerHash = ($readyLine.Substring(2) | ConvertFrom-Json).destination

            $appStart = [Diagnostics.ProcessStartInfo]::new()
            $appStart.FileName = $binary
            $appStart.WorkingDirectory = $root
            $appStart.UseShellExecute = $false
            $appStart.RedirectStandardInput = $true
            $appStart.RedirectStandardOutput = $true
            $appStart.RedirectStandardError = $true
            $appStart.ArgumentList.Add('--config')
            $appStart.ArgumentList.Add($appData)
            $appStart.ArgumentList.Add('--rnsconfig')
            $appStart.ArgumentList.Add($appConfig)
            $appProcess = [Diagnostics.Process]::new()
            $appProcess.StartInfo = $appStart
            $null = $appProcess.Start()
            $appProcess.BeginErrorReadLine()
            $appHash = $null
            $deadline = [DateTime]::UtcNow.AddSeconds(60)
            while ([DateTime]::UtcNow -lt $deadline -and $null -eq $appHash) {
                $line = $appProcess.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult()
                if ($null -eq $line) { throw 'NomadNet exited before announcing its LXMF destination.' }
                if ($line -match 'LXMF delivery address:\s*([0-9a-f]{32})') { $appHash = $Matches[1] }
            }
            if ($null -eq $appHash) { throw 'NomadNet did not report its delivery destination.' }

            $peerProcess.StandardInput.WriteLine('announce')
            $peerProcess.StandardInput.Flush()
            $peerProcess.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult() |
                Should -Match '"event"\s*:\s*"announced"'

            $appProcess.StandardInput.WriteLine("send $peerHash native-to-python")
            $appProcess.StandardInput.Flush()
            $deadline = [DateTime]::UtcNow.AddSeconds(90)
            $received = $null
            while ([DateTime]::UtcNow -lt $deadline -and $null -eq $received) {
                $line = $peerProcess.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(30)).GetAwaiter().GetResult()
                if ($null -eq $line) { throw 'Python LXMF peer exited while receiving.' }
                if ($line -match '"event"\s*:\s*"received"') { $received = $line.Substring(2) | ConvertFrom-Json }
            }
            $received.content | Should -Be 'native-to-python'

            $peerProcess.StandardInput.WriteLine("send $appHash python-to-native")
            $peerProcess.StandardInput.Flush()
            $deadline = [DateTime]::UtcNow.AddSeconds(90)
            $replySeen = $false
            $pendingOutputLine = $null
            while ([DateTime]::UtcNow -lt $deadline -and -not $replySeen) {
                $appProcess.StandardInput.WriteLine("messages $peerHash")
                $appProcess.StandardInput.Flush()
                $pollDeadline = [DateTime]::UtcNow.AddSeconds(2)
                while ([DateTime]::UtcNow -lt $pollDeadline -and -not $replySeen) {
                    if ($null -eq $pendingOutputLine) {
                        $pendingOutputLine = $appProcess.StandardOutput.ReadLineAsync()
                    }
                    try {
                        $line = $pendingOutputLine.WaitAsync([TimeSpan]::FromSeconds(1)).GetAwaiter().GetResult()
                        $pendingOutputLine = $null
                    }
                    catch [TimeoutException] {
                        $line = ''
                    }
                    if ($null -eq $line) { throw 'NomadNet exited while receiving the Python message.' }
                    if ($line.Contains('python-to-native')) { $replySeen = $true }
                    elseif ($line.Contains('No stored messages.')) { break }
                }
            }

            $replySeen | Should -BeTrue
        }
        finally {
            foreach ($process in @($appProcess, $peerProcess)) {
                if ($null -ne $process) {
                    if (-not $process.HasExited) {
                        try { $process.StandardInput.WriteLine('quit'); $process.StandardInput.Flush() } catch {}
                        if (-not $process.WaitForExit(5000)) { Stop-Process -Id $process.Id -Force }
                    }
                    $process.Dispose()
                }
            }
            if (Test-Path $work) { Remove-Item $work -Recurse -Force }
        }
    }
}

Describe 'NomadNet upstream node interoperability' -Tag 'Live' {
    It 'browses and downloads from upstream NomadNet, exchanges LXMF, and reloads durable history' {
        $readNomadNetLine = {
            param([System.IO.StreamReader] $Reader, [ref] $PendingLine, [int] $WaitMilliseconds)
            if ($null -eq $PendingLine.Value) {
                $PendingLine.Value = $Reader.ReadLineAsync()
            }
            try {
                $line = $PendingLine.Value.WaitAsync([TimeSpan]::FromMilliseconds($WaitMilliseconds)).GetAwaiter().GetResult()
                $PendingLine.Value = $null
                return [PSCustomObject]@{ Completed = $true; Line = $line }
            }
            catch [TimeoutException] {
                return [PSCustomObject]@{ Completed = $false; Line = $null }
            }
        }.GetNewClosure()
        $waitNomadNetOutput = {
            param([System.IO.StreamReader] $Reader, [ref] $PendingLine, [string] $Pattern, [int] $TimeoutSeconds, [string] $Description)
            $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
            while ([DateTime]::UtcNow -lt $deadline) {
                $result = & $readNomadNetLine $Reader $PendingLine 500
                if (-not $result.Completed) { continue }
                if ($null -eq $result.Line) { throw "${Description}: process closed its output before the expected event." }
                if ($result.Line -match '"event"\s*:\s*"(error|failed|request-failed)"') { throw "${Description}: $($result.Line)" }
                if ($result.Line -match $Pattern) { return $result.Line }
            }
            throw "${Description}: timed out waiting for '$Pattern'."
        }.GetNewClosure()

        if (-not [OperatingSystem]::IsLinux() -or
            $env:DOTRETICULUM_NOMADNET_NODE_LIVE -ne '1' -or
            [string]::IsNullOrWhiteSpace($env:DOTRETICULUM_PYTHON) -or
            [string]::IsNullOrWhiteSpace($env:DOTRETICULUM_UPSTREAM) -or
            [string]::IsNullOrWhiteSpace($env:DOTRETICULUM_LXMF_UPSTREAM) -or
            [string]::IsNullOrWhiteSpace($env:DOTRETICULUM_NOMADNET_BINARY)) {
            Set-ItResult -Skipped -Because 'Enable the Linux upstream test with DOTRETICULUM_NOMADNET_NODE_LIVE=1 and the pinned Python and native binary paths.'
            return
        }

        $root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        $source = if ([string]::IsNullOrWhiteSpace($env:DOTRETICULUM_NOMADNET_SOURCE)) {
            Join-Path $root 'artifacts\builds\nomadnet\nomadnet-source'
        } else {
            $env:DOTRETICULUM_NOMADNET_SOURCE
        }
        $interop = Join-Path $root 'tests\Interop\nomadnet_live_node.py'
        $python = [IO.Path]::GetFullPath($env:DOTRETICULUM_PYTHON)
        $binary = [IO.Path]::GetFullPath($env:DOTRETICULUM_NOMADNET_BINARY)
        if (-not (Test-Path $source) -or -not (Test-Path $interop) -or -not (Test-Path $binary)) {
            throw 'The primary NomadNet source, adapter, or Linux NativeAOT executable is missing.'
        }

        $work = if ([string]::IsNullOrWhiteSpace($env:DOTRETICULUM_NOMADNET_WORK)) {
            Join-Path $root 'artifacts\tests\nomadnet-upstream-live'
        } else {
            [IO.Path]::GetFullPath($env:DOTRETICULUM_NOMADNET_WORK)
        }
        if (Test-Path $work) { Remove-Item $work -Recurse -Force }
        $nodeConfig = Join-Path $work 'upstream'
        $nodeRns = Join-Path $work 'upstream-rns'
        $nativeData = Join-Path $work 'native'
        $nativeRns = Join-Path $work 'native-rns'
        $null = New-Item $nodeConfig -ItemType Directory -Force
        $null = New-Item $nodeRns -ItemType Directory -Force
        $null = New-Item $nativeData -ItemType Directory -Force
        $null = New-Item $nativeRns -ItemType Directory -Force

        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $port = $listener.LocalEndpoint.Port
        $listener.Stop()
        $topology = if ([string]::IsNullOrWhiteSpace($env:DOTRETICULUM_NOMADNET_TRANSPORT)) { 'direct' } else { $env:DOTRETICULUM_NOMADNET_TRANSPORT }
        if ($topology -notin @('direct', 'python', 'native')) { throw "Unsupported NomadNet topology: $topology" }
        $nativeTargetPort = $port
        $transport = $null
        $transportConfig = Join-Path $work 'transport-rns'
        if ($topology -ne 'direct') {
            $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
            $listener.Start()
            $nativeTargetPort = $listener.LocalEndpoint.Port
            $listener.Stop()
            $null = New-Item $transportConfig -ItemType Directory -Force
            @('[reticulum]', '  share_instance = No', '  enable_transport = Yes', '[interfaces]',
                '  [[Upstream]]', '    type = TCPClientInterface', '    enabled = Yes',
                '    target_host = 127.0.0.1', "    target_port = $port",
                '  [[Native]]', '    type = TCPServerInterface', '    enabled = Yes',
                '    listen_ip = 127.0.0.1', "    listen_port = $nativeTargetPort") |
                Set-Content (Join-Path $transportConfig 'config') -Encoding utf8
        }
        @(
            '[reticulum]'
            '  enable_transport = No'
            '  share_instance = No'
            ''
            '[interfaces]'
            '  [[NomadNetUpstreamServer]]'
            '    type = TCPServerInterface'
            '    enabled = Yes'
            '    listen_ip = 127.0.0.1'
            "    listen_port = $port"
        ) | Set-Content -Path (Join-Path $nodeRns 'config') -Encoding utf8
        @(
            '[reticulum]'
            '  enable_transport = No'
            '  share_instance = No'
            ''
            '[interfaces]'
            '  [[NomadNetUpstreamClient]]'
            '    type = TCPClientInterface'
            '    enabled = Yes'
            '    target_host = 127.0.0.1'
            "    target_port = $nativeTargetPort"
        ) | Set-Content -Path (Join-Path $nativeRns 'config') -Encoding utf8

        $pythonPath = "$source`:$($env:DOTRETICULUM_UPSTREAM)`:$($env:DOTRETICULUM_LXMF_UPSTREAM)`:$root/tests/Interop"
        $environment = @{
            DOTRETICULUM_UPSTREAM = $env:DOTRETICULUM_UPSTREAM
            DOTRETICULUM_LXMF_UPSTREAM = $env:DOTRETICULUM_LXMF_UPSTREAM
            DOTRETICULUM_NOMADNET_SOURCE = $source
            PYTHONPATH = $pythonPath
        }
        $bootstrap = $null
        $upstream = $null
        $client = $null
        try {
            $bootstrapStart = [Diagnostics.ProcessStartInfo]::new()
            $bootstrapStart.FileName = $python
            $bootstrapStart.WorkingDirectory = $root
            $bootstrapStart.UseShellExecute = $false
            foreach ($argument in @('-u', '-m', 'nomadnet.nomadnet', '--daemon', '--console',
                '--config', $nodeConfig, '--rnsconfig', $nodeRns)) {
                $bootstrapStart.ArgumentList.Add($argument)
            }
            foreach ($entry in $environment.GetEnumerator()) { $bootstrapStart.Environment[$entry.Key] = $entry.Value }
            $bootstrap = [Diagnostics.Process]::new()
            $bootstrap.StartInfo = $bootstrapStart
            $null = $bootstrap.Start()
            $identityPath = Join-Path $nodeConfig 'storage\identity'
            $nodeAppConfig = Join-Path $nodeConfig 'config'
            $deadline = [DateTime]::UtcNow.AddSeconds(60)
            while ([DateTime]::UtcNow -lt $deadline -and
                (-not (Test-Path $identityPath) -or -not (Test-Path $nodeAppConfig))) {
                if ($bootstrap.HasExited) { throw 'Upstream NomadNet exited before creating its initial configuration.' }
                Start-Sleep -Milliseconds 100
            }
            (Test-Path $identityPath) | Should -BeTrue
            (Test-Path $nodeAppConfig) | Should -BeTrue
            Start-Sleep -Seconds 1
            $bootstrap.Kill()
            $bootstrap.WaitForExit()
            $bootstrap.Dispose()
            $bootstrap = $null

            $configuration = [IO.File]::ReadAllText($nodeAppConfig)
            if (-not $configuration.Contains('enable_node = no')) {
                throw 'Expected the unmodified upstream default node setting was not found.'
            }
            $configuration = $configuration.Replace('enable_node = no', 'enable_node = yes')
            $configuration = $configuration.Replace('max_accepted_size = 500', 'max_accepted_size = 4000')
            [IO.File]::WriteAllText($nodeAppConfig, $configuration, [Text.UTF8Encoding]::new($false))
            $pages = Join-Path $nodeConfig 'storage\pages'
            $files = Join-Path $nodeConfig 'storage\files'
            $null = New-Item $pages -ItemType Directory -Force
            $null = New-Item $files -ItemType Directory -Force
            [IO.File]::WriteAllText((Join-Path $pages 'readme.mu'), "# Live page`n`nNOMADNET_UPSTREAM_PAGE_OK", [Text.UTF8Encoding]::new($false))
            [IO.File]::WriteAllText((Join-Path $files 'live.txt'), 'UPSTREAM_FILE_012345', [Text.UTF8Encoding]::new($false))
            $large = [byte[]]::new(2200000)
            [Security.Cryptography.RandomNumberGenerator]::Fill($large)
            [IO.File]::WriteAllBytes((Join-Path $files 'large.bin'), $large)
            $expectedLargeHash = (Get-FileHash (Join-Path $files 'large.bin') -Algorithm SHA256).Hash
            '{"schemaVersion":1,"displayName":"Native app","enableNode":true,"deliveryTransferLimitBytes":4000000}' |
                Set-Content (Join-Path $nativeData 'settings.json') -Encoding utf8
            $null = New-Item (Join-Path $nativeData 'storage\pages') -ItemType Directory -Force
            $null = New-Item (Join-Path $nativeData 'storage\files') -ItemType Directory -Force
            [IO.File]::WriteAllText((Join-Path $nativeData 'storage\pages\index.mu'), "#!c=0`nNATIVE_HOST_PAGE_OK", [Text.UTF8Encoding]::new($false))
            [IO.File]::WriteAllBytes((Join-Path $nativeData 'storage\files\large.bin'), $large)

            $upstreamStart = [Diagnostics.ProcessStartInfo]::new()
            $upstreamStart.FileName = $python
            $upstreamStart.WorkingDirectory = $root
            $upstreamStart.UseShellExecute = $false
            $upstreamStart.RedirectStandardInput = $true
            $upstreamStart.RedirectStandardOutput = $true
            foreach ($argument in @('-u', $interop, '--config', $nodeConfig, '--rnsconfig', $nodeRns)) {
                $upstreamStart.ArgumentList.Add($argument)
            }
            foreach ($entry in $environment.GetEnumerator()) { $upstreamStart.Environment[$entry.Key] = $entry.Value }
            $upstream = [Diagnostics.Process]::new()
            $upstream.StartInfo = $upstreamStart
            $null = $upstream.Start()
            $upstreamPending = $null
            $readyLine = & $waitNomadNetOutput $upstream.StandardOutput ([ref]$upstreamPending) `
                '"event"\s*:\s*"ready"' 60 'Upstream NomadNet'
            $ready = $readyLine.Substring(2) | ConvertFrom-Json
            $ready.version | Should -Be '1.4.4'
            $ready.module | Should -Match ([regex]::Escape($source))
            $ready.node | Should -Match '^[0-9a-f]{32}$'
            $ready.delivery | Should -Match '^[0-9a-f]{32}$'
            if ($topology -ne 'direct') {
                $transportStart = [Diagnostics.ProcessStartInfo]::new()
                $transportStart.FileName = if ($topology -eq 'python') { $python } else { $binary }
                $transportStart.UseShellExecute = $false
                $transportStart.RedirectStandardInput = $true
                $transportStart.RedirectStandardOutput = $true
                $transportStart.RedirectStandardError = $true
                $arguments = if ($topology -eq 'python') {
                    @('-u', (Join-Path $root 'tests\Interop\nomadnet_transport_peer.py'), '--config', $transportConfig)
                } else {
                    @('--daemon', '--config', (Join-Path $work 'transport-app'), '--rnsconfig', $transportConfig)
                }
                foreach ($argument in $arguments) { $transportStart.ArgumentList.Add($argument) }
                foreach ($entry in $environment.GetEnumerator()) { $transportStart.Environment[$entry.Key] = $entry.Value }
                $transport = [Diagnostics.Process]::new()
                $transport.StartInfo = $transportStart
                $null = $transport.Start()
                $transport.BeginErrorReadLine()
                $transportPending = $null
                $pattern = if ($topology -eq 'python') { '"event"\s*:\s*"transport-ready"' } else { 'Daemon ready.' }
                $null = & $waitNomadNetOutput $transport.StandardOutput ([ref]$transportPending) $pattern 30 'Transport node readiness'
            }
            Start-Sleep -Seconds 8

            $startNative = {
                param([string[]] $commands)
                $start = [Diagnostics.ProcessStartInfo]::new()
                $start.FileName = $binary
                $start.WorkingDirectory = $root
                $start.UseShellExecute = $false
                $start.RedirectStandardInput = $true
                $start.RedirectStandardOutput = $true
                foreach ($argument in @('--config', $nativeData, '--rnsconfig', $nativeRns)) {
                    $start.ArgumentList.Add($argument)
                }
                $process = [Diagnostics.Process]::new()
                $process.StartInfo = $start
                $null = $process.Start()
                try {
                    $pending = $null
                    $addressLine = & $waitNomadNetOutput $process.StandardOutput ([ref]$pending) `
                        'LXMF delivery address:\s*([0-9a-f]{32})' 60 'Native NomadNet'
                    $address = [regex]::Match($addressLine, 'LXMF delivery address:\s*([0-9a-f]{32})').Groups[1].Value
                    $transcript = [Text.StringBuilder]::new()
                    foreach ($command in $commands | Where-Object { $_ -ne 'quit' }) {
                        $process.StandardInput.WriteLine($command)
                        $process.StandardInput.WriteLine('status')
                        $process.StandardInput.Flush()
                        do {
                            $line = $process.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(120)).GetAwaiter().GetResult()
                            if ($null -eq $line) { throw "Native process exited during '$command'." }
                            $null = $transcript.AppendLine($line)
                            if ($line -match 'Error:') { throw "Native command '$command' failed: $line" }
                        } while ($line -notmatch 'Reticulum interfaces:')
                    }
                    if (@($commands | Where-Object { $_ -like 'send *' }).Count -gt 0) {
                        $deadline = [DateTime]::UtcNow.AddSeconds(90)
                        $delivered = $false
                        while ([DateTime]::UtcNow -lt $deadline -and -not $delivered) {
                            try { $state = [IO.File]::ReadAllText((Join-Path $nativeData 'state.json')) | ConvertFrom-Json }
                            catch [IO.IOException] { Start-Sleep -Milliseconds 100; continue }
                            $delivered = @($state.messages | Where-Object {
                                $_.content -eq 'native-to-upstream-nomadnet' -and $_.status -eq 'delivered'
                            }).Count -gt 0
                            if (-not $delivered) { Start-Sleep -Milliseconds 100 }
                        }
                        if (-not $delivered) {
                            $statuses = @($state.messages | ForEach-Object { "$($_.direction):$($_.status)" }) -join ','
                            throw "Native direct message did not reach delivered state before shutdown. Statuses: $statuses. Transcript: $transcript"
                        }
                    }
                    $outputTask = $process.StandardOutput.ReadToEndAsync()
                    $process.StandardInput.WriteLine('quit')
                    $process.StandardInput.Flush()
                    if (-not $process.WaitForExit(180000)) { throw 'Native NomadNet did not exit after its terminal commands.' }
                    if ($process.ExitCode -ne 0) { throw "Native NomadNet exited with $($process.ExitCode)." }
                    $output = $transcript.ToString() + $outputTask.GetAwaiter().GetResult()
                    return [PSCustomObject]@{ Address = $address; Output = $output }
                }
                finally {
                    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
                    $process.Dispose()
                }
            }.GetNewClosure()

            $firstRun = & $startNative @(
                "browse $($ready.node) /page/readme.mu",
                "fetch $($ready.node) /file/live.txt live.txt",
                "fetch $($ready.node) /file/large.bin large.bin",
                "send $($ready.delivery) native-to-upstream-nomadnet",
                'quit'
            )
            $upstream.StandardInput.WriteLine('stats')
            $upstream.StandardInput.Flush()
            $statsLine = & $waitNomadNetOutput $upstream.StandardOutput ([ref]$upstreamPending) `
                '"event"\s*:\s*"stats"' 10 'Upstream NomadNet request counters'
            $stats = $statsLine.Substring(2) | ConvertFrom-Json
            $stats.served_pages | Should -Be 1
            $stats.served_files | Should -Be 2
            if ($firstRun.Output -notmatch 'NOMADNET_UPSTREAM_PAGE_OK') {
                $logPath = Join-Path $nodeConfig 'logfile'
                $log = if (Test-Path $logPath) { Get-Content $logPath -Tail 30 | Out-String } else { '(no upstream log file)' }
                throw "The upstream page response was not rendered. Node state: $($stats | ConvertTo-Json -Compress). Log: $log Client output: $($firstRun.Output)"
            }
            $firstRun.Output | Should -Match 'NOMADNET_UPSTREAM_PAGE_OK'
            $firstRun.Output | Should -Match 'Saved 20 bytes'
            [IO.File]::ReadAllText((Join-Path $nativeData 'downloads\live.txt')) | Should -Be 'UPSTREAM_FILE_012345'
            (Get-FileHash (Join-Path $nativeData 'downloads\large.bin') -Algorithm SHA256).Hash | Should -Be $expectedLargeHash

            $upstreamConversation = Join-Path (Join-Path $nodeConfig 'storage\conversations') $firstRun.Address
            $deadline = [DateTime]::UtcNow.AddSeconds(60)
            while ([DateTime]::UtcNow -lt $deadline -and
                (-not (Test-Path $upstreamConversation) -or
                    (Get-ChildItem $upstreamConversation -File | Where-Object { $_.Name -notin @('unread', '.index') }).Count -eq 0)) {
                Start-Sleep -Milliseconds 250
            }
            (Get-ChildItem $upstreamConversation -File | Where-Object { $_.Name -notin @('unread', '.index') }).Count | Should -BeGreaterThan 0

            $clientStart = [Diagnostics.ProcessStartInfo]::new()
            $clientStart.FileName = $binary
            $clientStart.WorkingDirectory = $root
            $clientStart.UseShellExecute = $false
            $clientStart.RedirectStandardInput = $true
            $clientStart.RedirectStandardOutput = $true
            foreach ($argument in @('--config', $nativeData, '--rnsconfig', $nativeRns)) { $clientStart.ArgumentList.Add($argument) }
            $client = [Diagnostics.Process]::new()
            $client.StartInfo = $clientStart
            $null = $client.Start()
            $clientPending = $null
            $secondAddressLine = & $waitNomadNetOutput $client.StandardOutput ([ref]$clientPending) `
                'LXMF delivery address:\s*([0-9a-f]{32})' 60 'Restarted native NomadNet'
            $secondAddress = [regex]::Match($secondAddressLine, 'LXMF delivery address:\s*([0-9a-f]{32})').Groups[1].Value
            $secondAddress | Should -Be $firstRun.Address
            $hostLine = & $waitNomadNetOutput $client.StandardOutput ([ref]$clientPending) `
                'Hosted node address:\s*([0-9a-f]{32})' 30 'Native hosted node'
            $hostAddress = [regex]::Match($hostLine, '[0-9a-f]{32}').Value

            $upstream.StandardInput.WriteLine("request $hostAddress /page/index.mu")
            $upstream.StandardInput.Flush()
            $responseLine = & $waitNomadNetOutput $upstream.StandardOutput ([ref]$upstreamPending) `
                '"event"\s*:\s*"response"' 90 'Reference request to native page'
            (($responseLine.Substring(2) | ConvertFrom-Json).text) | Should -Match 'NATIVE_HOST_PAGE_OK'
            $upstream.StandardInput.WriteLine("request $hostAddress /file/large.bin")
            $upstream.StandardInput.Flush()
            $responseLine = & $waitNomadNetOutput $upstream.StandardOutput ([ref]$upstreamPending) `
                '"event"\s*:\s*"response"' 120 'Reference request to native large file'
            $response = $responseLine.Substring(2) | ConvertFrom-Json
            $response.size | Should -Be 2200000
            $response.sha256 | Should -Be $expectedLargeHash.ToLowerInvariant()

            $upstream.StandardInput.WriteLine("send-file $secondAddress $(Join-Path $files 'large.bin')")
            $upstream.StandardInput.Flush()
            $null = & $waitNomadNetOutput $upstream.StandardOutput ([ref]$upstreamPending) `
                '"event"\s*:\s*"delivered"' 120 'Reference attachment delivery'
            $deadline = [DateTime]::UtcNow.AddSeconds(30)
            $nativeAttachment = $null
            while ([DateTime]::UtcNow -lt $deadline -and $null -eq $nativeAttachment) {
                $nativeAttachment = Get-ChildItem (Join-Path $nativeData 'attachments') -Filter '0.bin' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($null -eq $nativeAttachment) { Start-Sleep -Milliseconds 100 }
            }
            $nativeAttachment | Should -Not -BeNullOrEmpty
            (Get-FileHash $nativeAttachment.FullName -Algorithm SHA256).Hash | Should -Be $expectedLargeHash
            $client.StandardInput.WriteLine("attach $($ready.delivery) $(Join-Path $nativeData 'downloads\large.bin')")
            $client.StandardInput.Flush()
            $null = & $waitNomadNetOutput $client.StandardOutput ([ref]$clientPending) 'Message queued:' 30 'Native attachment send'
            $deadline = [DateTime]::UtcNow.AddSeconds(120)
            $upstreamAttachment = $null
            while ([DateTime]::UtcNow -lt $deadline -and $null -eq $upstreamAttachment) {
                $upstreamAttachment = Get-ChildItem (Join-Path $nodeConfig 'storage\attachments') -Filter 'file_0' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($null -eq $upstreamAttachment) { Start-Sleep -Milliseconds 100 }
            }
            $upstreamAttachment | Should -Not -BeNullOrEmpty
            (Get-FileHash $upstreamAttachment.FullName -Algorithm SHA256).Hash | Should -Be $expectedLargeHash
            $client.StandardInput.WriteLine("paper $($ready.delivery) native-to-upstream-paper")
            $client.StandardInput.Flush()
            $paperLine = & $waitNomadNetOutput $client.StandardOutput ([ref]$clientPending) 'lxm://[A-Za-z0-9_-]+' 30 'Native paper generation'
            $uri = [regex]::Match($paperLine, 'lxm://[A-Za-z0-9_-]+').Value
            $upstream.StandardInput.WriteLine("ingest $uri")
            $upstream.StandardInput.Flush()
            $ingestedLine = & $waitNomadNetOutput $upstream.StandardOutput ([ref]$upstreamPending) '"event"\s*:\s*"ingested"' 30 'Upstream paper ingestion'
            (($ingestedLine.Substring(2) | ConvertFrom-Json).accepted) | Should -BeTrue
            $upstream.StandardInput.WriteLine("paper $secondAddress upstream-to-native-paper")
            $upstream.StandardInput.Flush()
            $paperLine = & $waitNomadNetOutput $upstream.StandardOutput ([ref]$upstreamPending) '"event"\s*:\s*"paper"' 30 'Upstream paper generation'
            $uri = ($paperLine.Substring(2) | ConvertFrom-Json).uri
            $client.StandardInput.WriteLine("ingest $uri")
            $client.StandardInput.Flush()
            $null = & $waitNomadNetOutput $client.StandardOutput ([ref]$clientPending) 'Paper ingest: LocalDelivery' 30 'Native paper ingestion'

            $upstream.StandardInput.WriteLine("send $secondAddress upstream-to-native-durable")
            $upstream.StandardInput.Flush()
            $queuedLine = & $waitNomadNetOutput $upstream.StandardOutput ([ref]$upstreamPending) `
                '"event"\s*:\s*"queued"' 30 'Upstream LXMF send'
            (($queuedLine.Substring(2) | ConvertFrom-Json).event) | Should -Be 'queued'
            $null = & $waitNomadNetOutput $upstream.StandardOutput ([ref]$upstreamPending) `
                '"event"\s*:\s*"delivered"' 90 'Upstream LXMF delivery'
            $messagePath = Join-Path $nativeData 'state.json'
            $deadline = [DateTime]::UtcNow.AddSeconds(60)
            $persisted = $false
            while ([DateTime]::UtcNow -lt $deadline -and -not $persisted) {
                if (Test-Path $messagePath) {
                    $state = [IO.File]::ReadAllText($messagePath) | ConvertFrom-Json
                    $persisted = @($state.messages | Where-Object { $_.content -eq 'upstream-to-native-durable' }).Count -gt 0
                }
                if (-not $persisted) { Start-Sleep -Milliseconds 250 }
            }
            $persisted | Should -BeTrue

            $client.StandardInput.WriteLine('quit')
            $client.StandardInput.Flush()
            $clientOutputTask = $client.StandardOutput.ReadToEndAsync()
            $client.WaitForExit(30000) | Should -BeTrue
            $client.Dispose()
            $client = $null
            $clientOutputTask.GetAwaiter().GetResult() | Out-Null

            $reloaded = & $startNative @("messages $($ready.delivery)", 'quit')
            $reloaded.Address | Should -Be $firstRun.Address
            $reloaded.Output | Should -Match 'upstream-to-native-durable'
            $upstream.StandardInput.WriteLine('quit')
            $upstream.StandardInput.Flush()
            $upstream.WaitForExit(10000) | Should -BeTrue
            $upstream.Dispose()
            $upstream = $null
        }
        finally {
            foreach ($process in @($client, $transport, $upstream, $bootstrap)) {
                if ($null -ne $process) {
                    if (-not $process.HasExited) {
                        try { $process.Kill(); $process.WaitForExit(5000) } catch {}
                    }
                    $process.Dispose()
                }
            }
            if ((Test-Path $work) -and $env:DOTRETICULUM_NOMADNET_KEEP_WORK -ne '1') { Remove-Item $work -Recurse -Force }
        }
    }
}
