using System.Security.Cryptography;
using System.Text.Json;

namespace Sideband;

public sealed partial class SidebandStateStore
{
    private const int MaximumStateBytes = 8 * 1024 * 1024;
    private const int MaximumMessages = 10_000;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SidebandState? _state;

    public SidebandStateStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        DataDirectory = Path.GetFullPath(dataDirectory);
        StatePath = Path.Combine(DataDirectory, "state.json");
    }

    public string DataDirectory { get; }
    public string StatePath { get; }

    public async Task<SidebandState> LoadAsync(string? defaultReticulumConfig = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state is not null) return _state;
            Directory.CreateDirectory(DataDirectory);
            RestrictDirectoryOnUnix();
            if (!File.Exists(StatePath))
            {
                var config = Path.GetFullPath(defaultReticulumConfig ?? DefaultReticulumConfigDirectory());
                _state = SidebandState.Empty(config);
                await WriteAtomicallyAsync(_state, cancellationToken).ConfigureAwait(false);
                return _state;
            }

            var info = new FileInfo(StatePath);
            if (info.Length > MaximumStateBytes) throw new InvalidDataException("Sideband state exceeds the 8 MiB safety limit.");
            await using var stream = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.Read, 16_384, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var state = await JsonSerializer.DeserializeAsync(stream, SidebandStateJsonContext.Default.SidebandState, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Sideband state is empty or invalid.");
            ValidateState(state);
            _state = state;
            return state;
        }
        finally { _gate.Release(); }
    }

    public async Task AddContactAsync(SidebandContact contact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contact);
        var validated = SidebandContact.Create(contact.DisplayName, contact.DeliveryHash, contact.Trusted, contact.Unread, contact.DirectPushToTalk)
            with { SendTelemetry = contact.SendTelemetry, AllowTelemetryRequests = contact.AllowTelemetryRequests, Appearance = contact.Appearance };
        await MutateAsync(state =>
        {
            var existing = state.Contacts.FirstOrDefault(item => item.DeliveryHash == validated.DeliveryHash);
            var updated = existing is null ? validated : validated with
            {
                Trusted = existing.Trusted, Unread = existing.Unread, DirectPushToTalk = existing.DirectPushToTalk,
                SendTelemetry = existing.SendTelemetry, AllowTelemetryRequests = existing.AllowTelemetryRequests, Appearance = existing.Appearance,
            };
            var contacts = state.Contacts.Where(item => item.DeliveryHash != validated.DeliveryHash).Append(updated).ToArray();
            if (contacts.Length > 1_000) throw new InvalidOperationException("The contact list is full.");
            return state with { Contacts = contacts };
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task SetConversationTrustAsync(string deliveryHash, bool trusted, CancellationToken cancellationToken = default) =>
        MutateConversationAsync(deliveryHash, contact => contact with { Trusted = trusted }, cancellationToken);

    public Task SetConversationReadAsync(string deliveryHash, bool read, CancellationToken cancellationToken = default) =>
        MutateConversationAsync(deliveryHash, contact => contact with { Unread = !read }, cancellationToken);

    public Task SetConversationPushToTalkAsync(string deliveryHash, bool enabled, CancellationToken cancellationToken = default) =>
        MutateConversationAsync(deliveryHash, contact => contact with { DirectPushToTalk = enabled }, cancellationToken);

    public Task ConfigureVoiceAsync(bool enabled, SidebandVoiceConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return MutateAsync(state => state with { VoiceEnabled = enabled, Voice = configuration }, cancellationToken);
    }

    public Task SetVoicePeerTrustAsync(string identityHash, bool trusted, CancellationToken cancellationToken = default)
    {
        if (!SidebandContact.TryNormalizeDeliveryHash(identityHash, out var normalized))
            throw new ArgumentException("A voice peer must have a 32-character hexadecimal identity hash.", nameof(identityHash));
        return MutateAsync(state =>
        {
            var peers = state.VoicePeers ?? [];
            var peer = peers.SingleOrDefault(item => item.IdentityHash == normalized) ?? new(normalized);
            return state with
            {
                VoicePeers = peers.Where(item => item.IdentityHash != normalized)
                    .Append(peer with { VoiceTrusted = trusted }).OrderBy(item => item.IdentityHash, StringComparer.Ordinal).ToArray(),
            };
        }, cancellationToken);
    }

    public Task AddCallEventAsync(SidebandVoiceCallEvent call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        ValidateCallEvent(call);
        return MutateAsync(state => state with
        {
            Calls = (state.Calls ?? []).Append(call)
                .Where(item => item.Time >= DateTimeOffset.UtcNow - SidebandVoiceService.CallLogRetention)
                .OrderBy(item => item.Time).TakeLast(1_000).ToArray(),
        }, cancellationToken);
    }

    public Task ClearCallHistoryAsync(CancellationToken cancellationToken = default) =>
        MutateAsync(state => state with { Calls = [] }, cancellationToken);

    public Task PruneCallHistoryAsync(CancellationToken cancellationToken = default) =>
        MutateAsync(state =>
        {
            var cutoff = DateTimeOffset.UtcNow - SidebandVoiceService.CallLogRetention;
            return state.Calls is { } calls && calls.Any(call => call.Time < cutoff)
                ? state with { Calls = calls.Where(call => call.Time >= cutoff).ToArray() }
                : state;
        }, cancellationToken);

    public async Task<SidebandChatMessage> AddMessageAsync(SidebandChatMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        try { ValidateMessage(message); }
        catch (InvalidDataException ex) { throw new ArgumentException("The message contains invalid or oversized fields.", nameof(message), ex); }
        _ = SidebandContact.TryNormalizeDeliveryHash(message.PeerDeliveryHash, out var peerHash);
        message = message with { PeerDeliveryHash = peerHash };
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadUnderLockAsync(cancellationToken).ConfigureAwait(false);
            var messages = state.Messages.Append(message).TakeLast(MaximumMessages).ToArray();
            var contacts = state.Contacts.Any(contact => contact.DeliveryHash == peerHash)
                ? state.Contacts.Select(contact => contact.DeliveryHash == peerHash ? contact with { Unread = true } : contact).ToArray()
                : state.Contacts.Append(SidebandContact.Create(peerHash, peerHash, unread: true)).ToArray();
            var next = state with { Contacts = contacts, Messages = messages };
            await WriteAtomicallyAsync(next, cancellationToken).ConfigureAwait(false);
            _state = next;
            return message;
        }
        finally { _gate.Release(); }
    }

    public async Task UpdateMessageStateAsync(Guid id, string deliveryState, CancellationToken cancellationToken = default, string? deliveryMethod = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryState);
        if (deliveryState.Length > 32) throw new ArgumentException("Delivery state is too long.", nameof(deliveryState));
        await MutateAsync(state => state with
        {
            Messages = state.Messages.Select(message => message.Id == id
                ? message with { DeliveryState = deliveryState, DeliveryMethod = deliveryMethod ?? message.DeliveryMethod }
                : message).ToArray(),
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetOwnDeliveryHashAsync(string hash, CancellationToken cancellationToken = default)
    {
        if (!SidebandContact.TryNormalizeDeliveryHash(hash, out var normalized))
            throw new ArgumentException("An LXMF delivery address must be exactly 32 hexadecimal characters.", nameof(hash));
        await MutateAsync(state => state with { OwnDeliveryHash = normalized }, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetReticulumConfigDirectoryAsync(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var fullPath = Path.GetFullPath(directory);
        await MutateAsync(state => state with { ReticulumConfigDirectory = fullPath }, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetPreferencesAsync(string displayName, string theme, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        var normalizedName = displayName.Trim();
        if (normalizedName.Length > 64 || normalizedName.Any(char.IsControl))
            throw new ArgumentException("Display names must contain at most 64 non-control characters.", nameof(displayName));
        if (theme is not ("Dark" or "Light"))
            throw new ArgumentException("Theme must be Dark or Light.", nameof(theme));
        await MutateAsync(state => state with { DisplayName = normalizedName, Theme = theme }, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetPropagationAsync(string? node, bool byDefault, CancellationToken cancellationToken = default)
    {
        string? normalized = null;
        if (!string.IsNullOrWhiteSpace(node) && !SidebandContact.TryNormalizeDeliveryHash(node, out normalized))
            throw new ArgumentException("A propagation address must be exactly 32 hexadecimal characters.", nameof(node));
        await MutateAsync(state =>
        {
            var lastNode = normalized ?? state.LastPropagationNode ?? state.PropagationNode;
            if (byDefault && normalized is null && lastNode is null)
                throw new ArgumentException("Select or discover a propagation node before enabling propagated delivery.", nameof(node));
            return state with
            {
                PropagationNode = normalized,
                LastPropagationNode = lastNode,
                PropagationByDefault = byDefault,
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task RememberPropagationNodeAsync(string node, CancellationToken cancellationToken = default)
    {
        if (!SidebandContact.TryNormalizeDeliveryHash(node, out var normalized))
            throw new ArgumentException("A propagation address must be exactly 32 hexadecimal characters.", nameof(node));
        await MutateAsync(state => state with { LastPropagationNode = normalized }, cancellationToken).ConfigureAwait(false);
    }

    public Task SetPeriodicSynchronizationAsync(bool enabled, int intervalSeconds, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intervalSeconds);
        return MutateAsync(state => state with
        {
            PeriodicSynchronization = enabled,
            SynchronizationIntervalSeconds = intervalSeconds,
        }, cancellationToken);
    }

    public Task SetSynchronizationStateAsync(double? lastSync, bool retrying, CancellationToken cancellationToken = default) =>
        MutateAsync(state => state with { LastSynchronization = lastSync, SynchronizationRetrying = retrying }, cancellationToken);

    public Task SetPropagationFallbackAsync(bool enabled, CancellationToken cancellationToken = default) =>
        MutateAsync(state => state with { TryPropagationOnFail = enabled }, cancellationToken);

    internal Task SaveImportedStateAsync(SidebandState state, CancellationToken cancellationToken) =>
        MutateAsync(_ => state, cancellationToken);

    public async Task<IReadOnlyList<string>> ImportSettingsAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16_384, FileOptions.Asynchronous);
        if (stream.Length > MaximumStateBytes) throw new InvalidDataException("Settings exceed the 8 MiB safety limit.");
        var settings = await JsonSerializer.DeserializeAsync(stream,
            SidebandStateJsonContext.Default.DictionaryStringJsonElement, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Settings must be a JSON object.");
        foreach (var key in new[] { "lxmf_propagation_node", "last_lxmf_propagation_node", "nn_home_node", "telemetry_collector" })
        {
            if (settings.TryGetValue(key, out var address) && address.ValueKind != JsonValueKind.Null &&
                (address.ValueKind != JsonValueKind.String || !SidebandContact.TryNormalizeDeliveryHash(address.GetString(), out _)))
                throw new InvalidDataException($"Setting '{key}' must be null or a 32-character hexadecimal address.");
        }

        await MutateAsync(state =>
        {
            var name = settings.TryGetValue("display_name", out var display) ? display.GetString() : state.DisplayName;
            var theme = settings.TryGetValue("dark_ui", out var dark) ? dark.GetBoolean() ? "Dark" : "Light" : state.Theme;
            var node = settings.TryGetValue("lxmf_propagation_node", out var configured) ? configured.GetString() : state.PropagationNode;
            var lastNode = settings.TryGetValue("last_lxmf_propagation_node", out var previous)
                ? previous.GetString() : state.LastPropagationNode ?? state.PropagationNode;
            var byDefault = settings.TryGetValue("propagation_by_default", out var propagation) ? propagation.GetBoolean() : state.PropagationByDefault;
            var periodic = settings.TryGetValue("lxmf_periodic_sync", out var sync) ? sync.GetBoolean() : state.PeriodicSynchronization;
            var fallback = settings.TryGetValue("lxmf_try_propagation_on_fail", out var retry) ? retry.GetBoolean() : state.TryPropagationOnFail;
            var voice = state.Voice ?? new();
            var voiceEnabled = settings.TryGetValue("voice_enabled", out var enabled) ? enabled.GetBoolean() : state.VoiceEnabled;
            voice = voice with
            {
                TrustedOnly = settings.TryGetValue("voice_trusted_only", out var trustedOnly) ? trustedOnly.GetBoolean() : voice.TrustedOnly,
                DirectPushToTalk = settings.TryGetValue("voice_hdx_direct_ptt", out var directPtt) ? directPtt.GetBoolean() : voice.DirectPushToTalk,
                HalfDuplexLoudspeaker = settings.TryGetValue("voice_hdx_loudspeaker", out var hdxSpeaker) ? hdxSpeaker.GetBoolean() : voice.HalfDuplexLoudspeaker,
                Microphone = settings.TryGetValue("voice_input", out var microphone) ? microphone.GetString() : voice.Microphone,
                Speaker = settings.TryGetValue("voice_speaker", out var speaker) ? speaker.GetString() : voice.Speaker,
                Loudspeaker = settings.TryGetValue("voice_loudspeaker", out var loudspeaker) ? loudspeaker.GetString() : voice.Loudspeaker,
                Ringer = settings.TryGetValue("voice_ringer", out var ringer) ? ringer.GetString() : voice.Ringer,
            };
            var interval = state.SynchronizationIntervalSeconds;
            if (settings.TryGetValue("lxmf_sync_interval", out var syncInterval) &&
                (syncInterval.ValueKind != JsonValueKind.Number || !syncInterval.TryGetInt32(out interval)))
                throw new InvalidDataException("The synchronization interval must be a positive whole number of seconds.");
            if (node is not null) node = node.ToLowerInvariant();
            return state with
            {
                DisplayName = name ?? throw new InvalidDataException("Display name cannot be null."),
                Theme = theme,
                PropagationNode = node,
                LastPropagationNode = node ?? lastNode?.ToLowerInvariant(),
                PropagationByDefault = byDefault,
                PeriodicSynchronization = periodic,
                TryPropagationOnFail = fallback,
                SynchronizationIntervalSeconds = interval,
                ImportedSettings = settings,
                VoiceEnabled = voiceEnabled,
                Voice = voice,
                Telemetry = SidebandTelemetrySettings.Import(settings, state.Telemetry),
            };
        }, cancellationToken).ConfigureAwait(false);
        return settings.Keys.Where(key => !SidebandTelemetrySettings.IsSupported(key) && key is not ("display_name" or "dark_ui" or "lxmf_propagation_node" or
            "last_lxmf_propagation_node" or "propagation_by_default" or "lxmf_periodic_sync" or "lxmf_sync_interval" or
            "lxmf_try_propagation_on_fail" or "voice_enabled" or "voice_trusted_only" or "voice_hdx_direct_ptt" or
            "voice_hdx_loudspeaker" or "voice_input" or "voice_speaker" or "voice_loudspeaker" or "voice_ringer")).Order(StringComparer.Ordinal).ToArray();
    }

    public async Task ExportSettingsAsync(string path, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var settings = state.ImportedSettings is null
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : state.ImportedSettings.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        settings["display_name"] = JsonSerializer.SerializeToElement(state.DisplayName, SidebandStateJsonContext.Default.String);
        settings["dark_ui"] = JsonSerializer.SerializeToElement(state.Theme == "Dark", SidebandStateJsonContext.Default.Boolean);
        settings["lxmf_propagation_node"] = JsonSerializer.SerializeToElement(state.PropagationNode, SidebandStateJsonContext.Default.String);
        settings["last_lxmf_propagation_node"] = JsonSerializer.SerializeToElement(state.LastPropagationNode ?? state.PropagationNode, SidebandStateJsonContext.Default.String);
        settings["propagation_by_default"] = JsonSerializer.SerializeToElement(state.PropagationByDefault, SidebandStateJsonContext.Default.Boolean);
        settings["lxmf_periodic_sync"] = JsonSerializer.SerializeToElement(state.PeriodicSynchronization, SidebandStateJsonContext.Default.Boolean);
        settings["lxmf_try_propagation_on_fail"] = JsonSerializer.SerializeToElement(state.TryPropagationOnFail, SidebandStateJsonContext.Default.Boolean);
        settings["lxmf_sync_interval"] = JsonSerializer.SerializeToElement(state.SynchronizationIntervalSeconds, SidebandStateJsonContext.Default.Int32);
        var voice = state.Voice ?? new();
        settings["voice_enabled"] = JsonSerializer.SerializeToElement(state.VoiceEnabled, SidebandStateJsonContext.Default.Boolean);
        settings["voice_trusted_only"] = JsonSerializer.SerializeToElement(voice.TrustedOnly, SidebandStateJsonContext.Default.Boolean);
        settings["voice_hdx_direct_ptt"] = JsonSerializer.SerializeToElement(voice.DirectPushToTalk, SidebandStateJsonContext.Default.Boolean);
        settings["voice_hdx_loudspeaker"] = JsonSerializer.SerializeToElement(voice.HalfDuplexLoudspeaker, SidebandStateJsonContext.Default.Boolean);
        settings["voice_input"] = JsonSerializer.SerializeToElement(voice.Microphone, SidebandStateJsonContext.Default.String);
        settings["voice_speaker"] = JsonSerializer.SerializeToElement(voice.Speaker, SidebandStateJsonContext.Default.String);
        settings["voice_loudspeaker"] = JsonSerializer.SerializeToElement(voice.Loudspeaker, SidebandStateJsonContext.Default.String);
        settings["voice_ringer"] = JsonSerializer.SerializeToElement(voice.Ringer, SidebandStateJsonContext.Default.String);
        SidebandTelemetrySettings.Export(settings, state.Telemetry);
        var target = Path.GetFullPath(path);
        if (string.Equals(target, StatePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Export cannot overwrite the application's state file.", nameof(path));
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16_384,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await JsonSerializer.SerializeAsync(stream, settings, SidebandStateJsonContext.Default.DictionaryStringJsonElement, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                if (stream.Length > MaximumStateBytes) throw new InvalidDataException("Settings exceed the 8 MiB safety limit.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task<IReadOnlyList<SidebandChatMessage>> GetMessagesAsync(string peerHash, CancellationToken cancellationToken = default)
    {
        if (!SidebandContact.TryNormalizeDeliveryHash(peerHash, out var normalized))
            throw new ArgumentException("An LXMF delivery address must be exactly 32 hexadecimal characters.", nameof(peerHash));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadUnderLockAsync(cancellationToken).ConfigureAwait(false);
            return state.Messages.Where(message => message.PeerDeliveryHash == normalized).OrderBy(message => message.Timestamp).ToArray();
        }
        finally { _gate.Release(); }
    }

    public static string DefaultDataDirectory()
    {
        var root = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        if (string.IsNullOrWhiteSpace(root)) root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(root, "sideband");
    }

    public static string DefaultReticulumConfigDirectory()
    {
        var root = Environment.GetEnvironmentVariable("RNS_CONFIG");
        if (!string.IsNullOrWhiteSpace(root)) return Path.GetFullPath(root);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return OperatingSystem.IsWindows()
            ? Path.Combine(user, ".reticulum")
            : Path.Combine(user, ".config", "reticulum");
    }

    private async Task MutateAsync(Func<SidebandState, SidebandState> mutation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadUnderLockAsync(cancellationToken).ConfigureAwait(false);
            var next = mutation(current);
            if (ReferenceEquals(current, next)) return;
            ValidateState(next);
            await WriteAtomicallyAsync(next, cancellationToken).ConfigureAwait(false);
            _state = next;
        }
        finally { _gate.Release(); }
    }

    private Task MutateConversationAsync(string deliveryHash, Func<SidebandContact, SidebandContact> mutation,
        CancellationToken cancellationToken)
    {
        if (!SidebandContact.TryNormalizeDeliveryHash(deliveryHash, out var normalized))
            throw new ArgumentException("An LXMF delivery address must be exactly 32 hexadecimal characters.", nameof(deliveryHash));
        return MutateAsync(state => state with
        {
            Contacts = state.Contacts.Select(contact => contact.DeliveryHash == normalized ? mutation(contact) : contact).ToArray(),
        }, cancellationToken);
    }

    private async Task<SidebandState> LoadUnderLockAsync(CancellationToken cancellationToken)
    {
        if (_state is not null) return _state;
        if (!File.Exists(StatePath))
        {
            Directory.CreateDirectory(DataDirectory);
            RestrictDirectoryOnUnix();
            _state = SidebandState.Empty(DefaultReticulumConfigDirectory());
            return _state;
        }

        var info = new FileInfo(StatePath);
        if (info.Length > MaximumStateBytes) throw new InvalidDataException("Sideband state exceeds the 8 MiB safety limit.");
        await using var stream = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.Read, 16_384, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var state = await JsonSerializer.DeserializeAsync(stream, SidebandStateJsonContext.Default.SidebandState, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Sideband state is empty or invalid.");
        ValidateState(state);
        _state = state;
        return state;
    }

    private async Task WriteAtomicallyAsync(SidebandState state, CancellationToken cancellationToken)
    {
        ValidateState(state);
        Directory.CreateDirectory(DataDirectory);
        RestrictDirectoryOnUnix();
        var temporary = StatePath + "." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)) + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16_384,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, state, SidebandStateJsonContext.Default.SidebandState, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                if (stream.Length > MaximumStateBytes) throw new InvalidDataException("Sideband state exceeds the 8 MiB safety limit.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, StatePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
        }
    }

    private static void ValidateState(SidebandState state)
    {
        if (state.SchemaVersion != SidebandState.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported Sideband state schema {state.SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(state.ReticulumConfigDirectory) || state.ReticulumConfigDirectory.Length > 4_096)
            throw new InvalidDataException("Reticulum configuration directory is missing or invalid.");
        if (state.Contacts is null || state.Messages is null)
            throw new InvalidDataException("Sideband state is missing its contact or message collections.");
        if (state.Contacts.Count > 1_000 || state.Messages.Count > MaximumMessages)
            throw new InvalidDataException("Sideband state exceeds its contact or message count limit.");
        _ = state.Telemetry.Validate();
        if (state.TelemetryPeers is { Count: > 1_000 }) throw new InvalidDataException("Too many telemetry peers.");
        foreach (var peer in state.TelemetryPeers ?? [])
            if (peer is null || !SidebandContact.TryNormalizeDeliveryHash(peer.DeliveryHash, out _) ||
                !double.IsFinite(peer.LastSendSuccessTimebase) || !double.IsFinite(peer.LastReceivedTimebase) ||
                !double.IsFinite(peer.LastSendAttemptUtcSeconds) || !double.IsFinite(peer.LastRequestAttemptUtcSeconds) ||
                !double.IsFinite(peer.LastRequestSuccessTimebase) || !double.IsFinite(peer.ScheduledSendBlockedUntil) ||
                peer.ScheduledSendAttempts is < 0 or > 2)
                throw new InvalidDataException("Invalid saved telemetry peer state.");
        if (state.TelemetryPeers is { } telemetryPeers &&
            telemetryPeers.Select(p => p.DeliveryHash).Distinct(StringComparer.Ordinal).Count() != telemetryPeers.Count)
            throw new InvalidDataException("Duplicate saved telemetry peers.");
        if (string.IsNullOrWhiteSpace(state.DisplayName) || state.DisplayName.Length > 64 || state.DisplayName.Any(char.IsControl))
            throw new InvalidDataException("Sideband display name is missing or invalid.");
        if (state.Theme is not ("Dark" or "Light"))
            throw new InvalidDataException("Sideband theme is unsupported.");
        if (state.PropagationNode is not null && !SidebandContact.TryNormalizeDeliveryHash(state.PropagationNode, out _))
            throw new InvalidDataException("The saved propagation-node address is invalid.");
        if (state.LastPropagationNode is not null && !SidebandContact.TryNormalizeDeliveryHash(state.LastPropagationNode, out _))
            throw new InvalidDataException("The saved discovered propagation-node address is invalid.");
        if (state.PropagationByDefault && state.PropagationNode is null && state.LastPropagationNode is null)
            throw new InvalidDataException("Propagated delivery requires a selected or discovered propagation node.");
        if (state.SynchronizationIntervalSeconds <= 0 ||
            state.LastSynchronization is { } lastSync && !double.IsFinite(lastSync))
            throw new InvalidDataException("The saved synchronization schedule is invalid.");
        if (state.OwnDeliveryHash is not null && !SidebandContact.TryNormalizeDeliveryHash(state.OwnDeliveryHash, out _))
            throw new InvalidDataException("The saved local LXMF delivery address is invalid.");
        foreach (var contact in state.Contacts)
        {
            if (contact is null) throw new InvalidDataException("Sideband state contains a null contact.");
            try { _ = SidebandContact.Create(contact.DisplayName, contact.DeliveryHash); }
            catch (ArgumentException ex) { throw new InvalidDataException("Sideband state contains an invalid contact.", ex); }
            if (contact.Appearance is not null) SidebandTelemetryConfiguration.ValidateAppearance(contact.Appearance);
        }
        foreach (var message in state.Messages) ValidateMessage(message);
        if (state.Voice is { } voice)
            foreach (var device in new[] { voice.Microphone, voice.Speaker, voice.Loudspeaker, voice.Ringer })
                if (device is not null && (string.IsNullOrWhiteSpace(device) || device.Length > 512 || device.Any(char.IsControl)))
                    throw new InvalidDataException("A saved voice device identifier is invalid.");
        if (state.VoicePeers is { } peers)
        {
            if (peers.Count > 1_000 || peers.Any(peer => peer is null ||
                !SidebandContact.TryNormalizeDeliveryHash(peer.IdentityHash, out var normalized) || normalized != peer.IdentityHash) ||
                peers.Select(peer => peer.IdentityHash).Distinct(StringComparer.Ordinal).Count() != peers.Count)
                throw new InvalidDataException("The saved voice peer collection is invalid or oversized.");
        }
        if (state.Calls is { } calls)
        {
            if (calls.Count > 1_000) throw new InvalidDataException("The saved voice call history is oversized.");
            foreach (var call in calls) ValidateCallEvent(call);
        }
    }

    private static void ValidateCallEvent(SidebandVoiceCallEvent call)
    {
        if (call is null || !SidebandContact.TryNormalizeDeliveryHash(call.IdentityHash, out _) ||
            string.IsNullOrWhiteSpace(call.Event) || call.Event.Length > 32 || call.Event.Any(char.IsControl))
            throw new InvalidDataException("A saved voice call event is invalid.");
    }

    private static void ValidateMessage(SidebandChatMessage message)
    {
        if (message is null) throw new InvalidDataException("Sideband state contains an empty message.");
        if (!SidebandContact.TryNormalizeDeliveryHash(message.PeerDeliveryHash, out _))
            throw new InvalidDataException("A message has an invalid LXMF delivery address.");
        if (message.Title is null || message.Content is null || message.DeliveryState is null ||
            message.Title.Length > 512 || message.Content.Length > 65_536 || message.DeliveryState.Length > 32)
            throw new InvalidDataException("A saved message exceeds a field size limit.");
        if (message.DeliveryMethod is not ("Unknown" or "Opportunistic" or "Direct" or "Propagated" or "Paper"))
            throw new InvalidDataException("A saved message has an unsupported delivery method.");
        if (message.PaperUri is { } uri)
        {
            byte[] destination;
            try { destination = LXMF.LxMessage.GetPaperDestinationHash(uri); }
            catch (FormatException ex) { throw new InvalidDataException("A saved paper URI has an invalid or oversized envelope.", ex); }
            if (message.Incoming || message.DeliveryMethod != "Paper" ||
                !destination.AsSpan().SequenceEqual(Convert.FromHexString(message.PeerDeliveryHash)))
                throw new InvalidDataException("A saved paper URI does not match its outgoing paper conversation.");
        }
        if (message.Attachments is { } attachments)
        {
            foreach (var attachment in attachments) SidebandAttachmentStore.Validate(attachment);
            if (attachments.Count > 8 || attachments.Sum(attachment => (long)attachment.Length) > SidebandAttachmentStore.MaximumMessageBytes)
                throw new InvalidDataException("A saved message exceeds attachment count or size limits.");
        }
        if (message.Image is { } image)
        {
            if (image.Format is not ("webp" or "png" or "jpeg"))
                throw new InvalidDataException("A saved image has an unsupported format.");
            SidebandAttachmentStore.Validate(image.Data);
            if (image.Data.Length == 0) throw new InvalidDataException("A saved image is empty.");
        }
        if (message.Audio is { } audio)
        {
            if (audio.Mode is < 0 or > 255) throw new InvalidDataException("A saved audio mode is invalid.");
            SidebandAttachmentStore.Validate(audio.Data);
            if (audio.Data.Length == 0) throw new InvalidDataException("A saved audio message is empty.");
        }
        if ((message.Attachments?.Sum(item => (long)item.Length) ?? 0) +
            (message.Image?.Data.Length ?? 0) + (message.Audio?.Data.Length ?? 0) > SidebandAttachmentStore.MaximumMessageBytes)
            throw new InvalidDataException("A saved message exceeds its combined 4 MiB media limit.");
    }

    private void RestrictDirectoryOnUnix()
    {
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(DataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch (PlatformNotSupportedException) { }
        }
    }
}
