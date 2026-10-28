using System.Security.Cryptography;
using System.Text.Json;

namespace Sideband;

public sealed class SidebandStateStore
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
        var validated = SidebandContact.Create(contact.DisplayName, contact.DeliveryHash);
        await MutateAsync(state =>
        {
            var contacts = state.Contacts.Where(item => item.DeliveryHash != validated.DeliveryHash).Append(validated).ToArray();
            if (contacts.Length > 1_000) throw new InvalidOperationException("The contact list is full.");
            return state with { Contacts = contacts };
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SidebandChatMessage> AddMessageAsync(SidebandChatMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        try { ValidateMessage(message); }
        catch (InvalidDataException ex) { throw new ArgumentException("The message contains invalid or oversized fields.", nameof(message), ex); }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadUnderLockAsync(cancellationToken).ConfigureAwait(false);
            var messages = state.Messages.Append(message).TakeLast(MaximumMessages).ToArray();
            var next = state with { Messages = messages };
            await WriteAtomicallyAsync(next, cancellationToken).ConfigureAwait(false);
            _state = next;
            return message;
        }
        finally { _gate.Release(); }
    }

    public async Task UpdateMessageStateAsync(Guid id, string deliveryState, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryState);
        if (deliveryState.Length > 32) throw new ArgumentException("Delivery state is too long.", nameof(deliveryState));
        await MutateAsync(state => state with
        {
            Messages = state.Messages.Select(message => message.Id == id
                ? message with { DeliveryState = deliveryState }
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
            ValidateState(next);
            await WriteAtomicallyAsync(next, cancellationToken).ConfigureAwait(false);
            _state = next;
        }
        finally { _gate.Release(); }
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
        if (state.OwnDeliveryHash is not null && !SidebandContact.TryNormalizeDeliveryHash(state.OwnDeliveryHash, out _))
            throw new InvalidDataException("The saved local LXMF delivery address is invalid.");
        foreach (var contact in state.Contacts)
        {
            if (contact is null) throw new InvalidDataException("Sideband state contains a null contact.");
            try { _ = SidebandContact.Create(contact.DisplayName, contact.DeliveryHash); }
            catch (ArgumentException ex) { throw new InvalidDataException("Sideband state contains an invalid contact.", ex); }
        }
        foreach (var message in state.Messages) ValidateMessage(message);
    }

    private static void ValidateMessage(SidebandChatMessage message)
    {
        if (message is null) throw new InvalidDataException("Sideband state contains an empty message.");
        if (!SidebandContact.TryNormalizeDeliveryHash(message.PeerDeliveryHash, out _))
            throw new InvalidDataException("A message has an invalid LXMF delivery address.");
        if (message.Title is null || message.Content is null || message.DeliveryState is null ||
            message.Title.Length > 512 || message.Content.Length > 65_536 || message.DeliveryState.Length > 32)
            throw new InvalidDataException("A saved message exceeds a field size limit.");
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
