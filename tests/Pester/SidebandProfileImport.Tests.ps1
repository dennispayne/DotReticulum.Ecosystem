Describe 'sideband upstream desktop-profile migration' -Tag 'Native', 'ProfileImport' {
    BeforeAll {
        $binary = $env:DOTRETICULUM_SIDEBAND_BINARY
        $reference = $env:DOTRETICULUM_SIDEBAND_PROFILE
        if ([string]::IsNullOrWhiteSpace($binary) -or -not (Test-Path -LiteralPath $binary -PathType Leaf)) {
            throw 'Set DOTRETICULUM_SIDEBAND_BINARY to the published sideband executable.'
        }
        if ([string]::IsNullOrWhiteSpace($reference) -or -not (Test-Path -LiteralPath (Join-Path $reference 'observations.json'))) {
            throw 'Set DOTRETICULUM_SIDEBAND_PROFILE to an ephemeral GenerateSidebandProfile.py output.'
        }
        $source = Join-Path $reference 'app_storage'
        $root = Join-Path (Join-Path (Get-Location) 'artifacts\tests') ("sideband-profile-pester-" + [guid]::NewGuid().ToString('N'))
        $target = Join-Path $root 'native'
        New-Item -ItemType Directory -Path $root -Force | Out-Null
        $sourceFingerprint = (Get-ChildItem -LiteralPath $source -File -Recurse | Sort-Object FullName |
            ForEach-Object { $_.FullName + ':' + (Get-FileHash -LiteralPath $_.FullName).Hash }) -join '|'
        $observations = Get-Content -LiteralPath (Join-Path $reference 'observations.json') -Raw | ConvertFrom-Json
    }

    It 'imports actual binary config, identity and WAL history in a separate native process' {
        $imported = & $binary --import-profile $reference --data-dir $target 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 0
        $imported | Should -Match 'Imported 1 conversations and 2 messages'
        $imported | Should -Match 'archive-only'
        $state = Get-Content -LiteralPath (Join-Path $target 'state.json') -Raw | ConvertFrom-Json
        $state.ownDeliveryHash | Should -Be $observations.ownDeliveryHash
        $state.displayName | Should -Be 'Python Profile'
        $state.tryPropagationOnFail | Should -BeFalse
        $state.periodicSynchronization | Should -BeTrue
        if ($observations.PSObject.Properties.Name -contains 'lastSynchronization') {
            $state.lastSynchronization | Should -Be $observations.lastSynchronization
            $state.synchronizationRetrying | Should -Be $observations.synchronizationRetrying
        }
        $state.messages.Count | Should -Be 2
        $state.contacts[0].displayName | Should -Be 'Reference contact'
        $state.contacts[0].trusted | Should -Be $observations.conversationTrusted
        $state.contacts[0].unread | Should -Be $observations.conversationUnread
        $observations.mainDatabaseTrusted | Should -BeFalse
        $observations.mainDatabaseUnread | Should -BeFalse
        $observations.conversationTrusted | Should -BeTrue
        $observations.conversationUnread | Should -BeTrue
        ($state.messages | Where-Object incoming).deliveryMethod | Should -Be 'Direct'
        ($state.messages | Where-Object { -not $_.incoming }).deliveryMethod | Should -Be 'Propagated'
        $attachment = ($state.messages | Where-Object incoming).attachments[0]
        [System.IO.File]::ReadAllText((Join-Path (Join-Path $target 'attachments') $attachment.contentHash)) |
            Should -Be "Python attachment`n"
        $identity = Join-Path (Join-Path (Join-Path $target 'reticulum') 'storage') 'sideband'
        (Get-FileHash -LiteralPath $identity).Hash | Should -Be (Get-FileHash -LiteralPath (Join-Path $source 'primary_identity')).Hash
        $identityHash = (Get-FileHash -LiteralPath $identity).Hash
        & $binary --check --data-dir $target 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 0
        (Get-FileHash -LiteralPath $identity).Hash | Should -Be $identityHash
        $after = (Get-ChildItem -LiteralPath $source -File -Recurse | Sort-Object FullName |
            ForEach-Object { $_.FullName + ':' + (Get-FileHash -LiteralPath $_.FullName).Hash }) -join '|'
        $after | Should -Be $sourceFingerprint
    }

    It 'decodes published native media offline and reports corruption without changing history' {
        if ($env:DOTLXST_NATIVE -ne '1' -or
            $observations.PSObject.Properties.Name -notcontains 'media' -or
            $observations.media.PSObject.Properties.Name -notcontains 'image') {
            Set-ItResult -Skipped -Because 'Requires DOTLXST_NATIVE=1, native codecs and the actual generated media profile.'
            return
        }
        $before = (Get-FileHash -LiteralPath (Join-Path $target 'state.json')).Hash
        $check = & $binary --check-media --data-dir $target 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 0
        $check | Should -Match 'Validated 1 images and 2 audio messages without opening audio devices or Reticulum\.'
        $state = Get-Content -LiteralPath (Join-Path $target 'state.json') -Raw | ConvertFrom-Json
        $image = ($state.messages | Where-Object incoming).image.data.contentHash
        $imagePath = Join-Path (Join-Path $target 'attachments') $image
        $original = [System.IO.File]::ReadAllBytes($imagePath)
        try {
            [System.IO.File]::WriteAllText($imagePath, 'corrupted synthetic media')
            $check = & $binary --check-media --data-dir $target 2>&1 | Out-String
            $LASTEXITCODE | Should -Be 1
            $check | Should -Match 'integrity|length'
            (Get-FileHash -LiteralPath (Join-Path $target 'state.json')).Hash | Should -Be $before
        }
        finally { [System.IO.File]::WriteAllBytes($imagePath, $original) }
        & $binary --check-media --data-dir $target 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 0
        foreach ($extra in @('--check', '--init', '--smoke-ui')) {
            & $binary --check-media $extra --data-dir $target 2>&1 | Out-Null
            $LASTEXITCODE | Should -Be 1
        }
        (Get-FileHash -LiteralPath (Join-Path $target 'state.json')).Hash | Should -Be $before
    }

    It 'activates image audio and router persistence and cross-loads it through actual Python APIs' {
        if ($observations.PSObject.Properties.Name -notcontains 'media' -or
            $observations.media.PSObject.Properties.Name -notcontains 'image' -or
            [string]::IsNullOrWhiteSpace($env:DOTRETICULUM_SIDEBAND_ROUTER_PROFILE) -or
            [string]::IsNullOrWhiteSpace($env:DOTRETICULUM_PYTHON) -or
            [string]::IsNullOrWhiteSpace($env:DOTRETICULUM_SIDEBAND_UPSTREAM) -or
            [string]::IsNullOrWhiteSpace($env:DOTRETICULUM_UPSTREAM)) {
            Set-ItResult -Skipped -Because 'Requires a generated media profile with same-identity router files, router observations and pinned Python/source paths.'
            return
        }
        $state = Get-Content -LiteralPath (Join-Path $target 'state.json') -Raw | ConvertFrom-Json
        $message = $state.messages | Where-Object incoming
        $message.image.format | Should -Be $observations.media.image.format
        $message.image.data.contentHash | Should -Be $observations.media.image.sha256
        $message.audio.mode | Should -Be $observations.media.audio.mode
        $message.audio.data.contentHash | Should -Be $observations.media.audio.sha256
        foreach ($media in @($message.image.data, $message.audio.data)) {
            $file = Join-Path (Join-Path $target 'attachments') $media.contentHash
            (Get-Item -LiteralPath $file).Length | Should -Be $media.length
            (Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant() | Should -Be $media.contentHash
        }
        foreach ($name in @('available_tickets', 'outbound_stamp_costs', 'local_deliveries', 'locally_processed')) {
            Test-Path -LiteralPath (Join-Path (Join-Path $target 'lxmf') $name) | Should -BeTrue
        }
        $script = Join-Path (Split-Path $PSScriptRoot -Parent) 'Interop\GenerateSidebandRouterProfile.py'
        $result = & $env:DOTRETICULUM_PYTHON $script --sideband $env:DOTRETICULUM_SIDEBAND_UPSTREAM `
            --rns $env:DOTRETICULUM_UPSTREAM --profile $env:DOTRETICULUM_SIDEBAND_ROUTER_PROFILE --verify-native $target 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 0
        $result | Should -Match '"crossLoad": true'
    }

    It 'rejects repeated imports without changing the existing native state' {
        $statePath = Join-Path $target 'state.json'
        $before = (Get-FileHash -LiteralPath $statePath).Hash
        & $binary --import-profile $reference --data-dir $target 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 1
        (Get-FileHash -LiteralPath $statePath).Hash | Should -Be $before
    }

    It 'rejects conflicting maintenance, network and GUI options before creating a destination' {
        foreach ($extra in @(
            @{ Arguments = @('--check') },
            @{ Arguments = @('--rnsconfig', (Join-Path $root 'unused-rns')) },
            @{ Arguments = @('--smoke-ui') }
        )) {
            $blocked = Join-Path $root ('blocked-' + [guid]::NewGuid().ToString('N'))
            $arguments = @('--import-profile', $reference, '--data-dir', $blocked) + $extra.Arguments
            & $binary @arguments 2>&1 | Out-Null
            $LASTEXITCODE | Should -Be 1
            Test-Path -LiteralPath $blocked | Should -BeFalse
        }
    }

    It 'rolls back malformed binary configuration and can retry the same absent target' {
        $badSource = Join-Path $root 'bad-source'
        $badTarget = Join-Path $root 'recovery-target'
        New-Item -ItemType Directory -Path $badSource | Out-Null
        Get-ChildItem -LiteralPath $source -File | Copy-Item -Destination $badSource
        [System.IO.File]::WriteAllBytes((Join-Path $badSource 'sideband_config'), [byte[]]@(0xC1))
        $failed = & $binary --import-profile $badSource --data-dir $badTarget 2>&1 | Out-String
        $LASTEXITCODE | Should -Be 1
        $failed | Should -Match 'Profile import failed'
        Test-Path -LiteralPath $badTarget | Should -BeFalse
        Copy-Item -LiteralPath (Join-Path $source 'sideband_config') -Destination (Join-Path $badSource 'sideband_config') -Force
        & $binary --import-profile $badSource --data-dir $badTarget 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 0
        Test-Path -LiteralPath (Join-Path $badTarget 'state.json') | Should -BeTrue
    }

    AfterAll {
        if ($root -and (Test-Path -LiteralPath $root)) { Remove-Item -LiteralPath $root -Recurse -Force }
    }
}
