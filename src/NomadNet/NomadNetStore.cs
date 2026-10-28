using System.Text;
using System.Text.Json;

namespace NomadNet;

public sealed class NomadNetStore
{
    public const int MaximumMessages = 2_000;
    public const int MaximumMessageCharacters = 64 * 1024;
    public const int MaximumStateBytes = 5 * 1024 * 1024;

    private readonly string _statePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NomadNetState _state;

    private NomadNetStore(string statePath, NomadNetState state)
    {
        _statePath = statePath;
        _state = state;
    }

    public static async Task<NomadNetStore> OpenAsync(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "state.json");
        NomadNetState state;
        if (!File.Exists(path))
        {
            state = new NomadNetState();
        }
        else
        {
            var info = new FileInfo(path);
            if (info.Length > MaximumStateBytes) throw new InvalidDataException("NomadNet state exceeds the configured size limit.");
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            state = await JsonSerializer.DeserializeAsync(stream, NomadNetJsonContext.Default.NomadNetState, cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidDataException("NomadNet state file is empty.");
            if (state.SchemaVersion != 1) throw new InvalidDataException($"Unsupported NomadNet state version {state.SchemaVersion}.");
            if (state.Contacts is null || state.Messages is null || state.Bookmarks is null ||
                state.Contacts.Count > 10_000 || state.Messages.Count > MaximumMessages || state.Bookmarks.Count > 2_000)
                throw new InvalidDataException("NomadNet state exceeds a record limit.");
        }

        var store = new NomadNetStore(path, state);
        if (!File.Exists(path)) await store.PersistAsync(cancellationToken).ConfigureAwait(false);
        return store;
    }

    public async Task<IReadOnlyList<NomadNetContact>> ContactsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return _state.Contacts.OrderBy(contact => contact.Name, StringComparer.OrdinalIgnoreCase).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task AddContactAsync(string hash, string name, CancellationToken cancellationToken = default)
    {
        hash = NomadNetAddress.NormalizeHash(hash);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 80 || name.Any(char.IsControl)) throw new ArgumentException("Contact name must be 1-80 printable characters.", nameof(name));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = _state.Contacts.FindIndex(contact => contact.DestinationHash == hash);
            if (index >= 0) _state.Contacts[index] = new NomadNetContact(hash, name.Trim());
            else
            {
                if (_state.Contacts.Count >= 10_000) throw new InvalidOperationException("Contact limit reached.");
                _state.Contacts.Add(new NomadNetContact(hash, name.Trim()));
            }
            await PersistAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> RemoveContactAsync(string hash, CancellationToken cancellationToken = default)
    {
        hash = NomadNetAddress.NormalizeHash(hash);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var removed = _state.Contacts.RemoveAll(contact => contact.DestinationHash == hash) > 0;
            if (removed) await PersistAsync(cancellationToken).ConfigureAwait(false);
            return removed;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<NomadNetMessage>> MessagesAsync(string? peerHash = null, CancellationToken cancellationToken = default)
    {
        if (peerHash is not null) peerHash = NomadNetAddress.NormalizeHash(peerHash);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _state.Messages.Where(message => peerHash is null || message.PeerHash == peerHash)
                .OrderBy(message => message.Timestamp).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task SaveMessageAsync(NomadNetMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var normalized = message with
        {
            PeerHash = NomadNetAddress.NormalizeHash(message.PeerHash),
            Content = Bound(message.Content, MaximumMessageCharacters),
            Title = Bound(message.Title, 512),
            Status = Bound(message.Status, 40),
        };
        if (normalized.Direction is not ("inbound" or "outbound")) throw new ArgumentException("Invalid message direction.", nameof(message));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = _state.Messages.FindIndex(existing => existing.Id == normalized.Id);
            if (index >= 0) _state.Messages[index] = normalized;
            else
            {
                _state.Messages.Add(normalized);
                if (_state.Messages.Count > MaximumMessages)
                    _state.Messages.RemoveRange(0, _state.Messages.Count - MaximumMessages);
            }
            await PersistAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<NomadNetBookmark>> BookmarksAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return _state.Bookmarks.ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task AddBookmarkAsync(NomadNetBookmark bookmark, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bookmark.Title);
        var normalized = bookmark with
        {
            DestinationHash = NomadNetAddress.NormalizeHash(bookmark.DestinationHash),
            Path = NomadNetAddress.NormalizePagePath(bookmark.Path),
            Title = Bound(bookmark.Title.Trim(), 100),
        };
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state.Bookmarks.Count >= 2_000) throw new InvalidOperationException("Bookmark limit reached.");
            _state.Bookmarks.Add(normalized);
            await PersistAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(_state, NomadNetJsonContext.Default.NomadNetState);
        while (data.Length > MaximumStateBytes && _state.Messages.Count > 0)
        {
            _state.Messages.RemoveAt(0);
            data = JsonSerializer.SerializeToUtf8Bytes(_state, NomadNetJsonContext.Default.NomadNetState);
        }
        if (data.Length > MaximumStateBytes) throw new InvalidDataException("NomadNet state would exceed the configured size limit.");
        var temporary = _statePath + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, _statePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string Bound(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];
}

public static class NomadNetTerminalText
{
    public static string Safe(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
            if (!char.IsControl(character) || character == '\t')
                result.Append(character);
        return result.ToString();
    }
}

public static class NomadNetAddress
{
    public const string DefaultPagePath = "/page/index.mu";

    public static string NormalizeHash(string hash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        if (hash.Length != 32 || hash.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Destination hash must be exactly 32 hexadecimal characters.", nameof(hash));
        return hash.ToLowerInvariant();
    }

    public static string NormalizePagePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Length > 512 || path.Contains('\\') || path.Any(char.IsControl) ||
            path.Split('/').Any(segment => segment is "." or ".."))
            throw new ArgumentException("Invalid Nomad Network request path.", nameof(path));
        if (!path.StartsWith('/')) path = "/" + path;
        if (path == "/page" || path == "/page/") return DefaultPagePath;
        if (!path.StartsWith("/page/", StringComparison.Ordinal))
            throw new ArgumentException("Only Nomad Network /page paths are supported for browsing.", nameof(path));
        return path;
    }

    public static (string DestinationHash, string Path) ParseUrl(string target, string? currentHash = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        var separator = target.IndexOf(':');
        string hash;
        string path;
        if (separator < 0)
        {
            hash = NormalizeHash(target);
            path = DefaultPagePath;
        }
        else
        {
            var prefix = target[..separator];
            path = target[(separator + 1)..];
            if (prefix.Length == 0)
                hash = currentHash is null ? throw new ArgumentException("Relative Nomad Network link has no current node.", nameof(target))
                    : NormalizeHash(currentHash);
            else
                hash = NormalizeHash(prefix);
            if (path.Length == 0) path = DefaultPagePath;
        }
        return (hash, NormalizePagePath(path));
    }
}
