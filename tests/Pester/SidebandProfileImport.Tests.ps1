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
        $sourceFingerprint = (Get-ChildItem -LiteralPath $source -File | Sort-Object Name |
            ForEach-Object { $_.Name + ':' + (Get-FileHash -LiteralPath $_.FullName).Hash }) -join '|'
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
        $after = (Get-ChildItem -LiteralPath $source -File | Sort-Object Name |
            ForEach-Object { $_.Name + ':' + (Get-FileHash -LiteralPath $_.FullName).Hash }) -join '|'
        $after | Should -Be $sourceFingerprint
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
