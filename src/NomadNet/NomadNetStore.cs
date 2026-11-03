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
    private string? _activePeer;

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
            if (state.Contacts is null || state.Messages is null || state.Bookmarks is null || state.ConversationAlerts is null || state.IgnoredDestinations is null ||
                state.Contacts.Count > 10_000 || state.Messages.Count > MaximumMessages || state.Bookmarks.Count > 2_000 ||
                state.ConversationAlerts.Count > MaximumMessages || state.IgnoredDestinations.Count > 10_000)
                throw new InvalidDataException("NomadNet state exceeds a record limit.");
            foreach (var contact in state.Contacts) _ = NormalizeContact(contact);
            foreach (var hash in state.IgnoredDestinations) _ = NomadNetAddress.NormalizeHash(hash);
            foreach (var alert in state.ConversationAlerts)
                if (alert.PeerHash != NomadNetAddress.NormalizeHash(alert.PeerHash) ||
                    alert.Unread is < 0 or > MaximumMessages || alert.Failures is < 0 or > MaximumMessages)
                    throw new InvalidDataException("Invalid conversation alert state.");
            if (state.ConversationAlerts.Select(alert => alert.PeerHash).Distinct(StringComparer.Ordinal).Count() != state.ConversationAlerts.Count)
                throw new InvalidDataException("Duplicate conversation alert state.");
        }

        var store = new NomadNetStore(path, state);
        if (!File.Exists(path)) await store.PersistAsync(cancellationToken).ConfigureAwait(false);
        return store;
    }

    public async Task<IReadOnlyList<NomadNetContact>> ContactsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return _state.Contacts.Select(contact => contact with { Ignored = _state.IgnoredDestinations.Contains(contact.DestinationHash) })
            .OrderBy(contact => contact.Name, StringComparer.OrdinalIgnoreCase).ToArray(); }
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
            if (index >= 0) _state.Contacts[index] = _state.Contacts[index] with { Name = name.Trim() };
            else
            {
                if (_state.Contacts.Count >= 10_000) throw new InvalidOperationException("Contact limit reached.");
                _state.Contacts.Add(new NomadNetContact(hash, name.Trim()));
            }
            await PersistAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<NomadNetContact?> FindContactAsync(string hash, CancellationToken cancellationToken = default)
    {
        hash = NomadNetAddress.NormalizeHash(hash);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var contact = _state.Contacts.Find(contact => contact.DestinationHash == hash);
            return contact is null ? null : contact with { Ignored = _state.IgnoredDestinations.Contains(hash) };
        }
        finally { _gate.Release(); }
    }

    public async Task SaveContactAsync(NomadNetContact contact, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeContact(contact);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var contacts = _state.Contacts;
        var ignored = _state.IgnoredDestinations;
        try
        {
            _state.Contacts = [.. contacts];
            _state.IgnoredDestinations = [.. ignored];
            var index = _state.Contacts.FindIndex(item => item.DestinationHash == normalized.DestinationHash);
            if (index < 0 && _state.Contacts.Count >= 10_000) throw new InvalidOperationException("Contact limit reached.");
            if (normalized.Ignored && !_state.IgnoredDestinations.Contains(normalized.DestinationHash))
            {
                if (_state.IgnoredDestinations.Count >= 10_000) throw new InvalidOperationException("Ignore list limit reached.");
                _state.IgnoredDestinations.Add(normalized.DestinationHash);
            }
            else if (!normalized.Ignored) _state.IgnoredDestinations.Remove(normalized.DestinationHash);
            if (index >= 0) _state.Contacts[index] = normalized;
            else _state.Contacts.Add(normalized);
            if (JsonSerializer.SerializeToUtf8Bytes(_state, NomadNetJsonContext.Default.NomadNetState).Length > MaximumStateBytes)
                throw new InvalidDataException("Contact policy exceeds the state size limit; history was not evicted.");
            await PersistAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _state.Contacts = contacts;
            _state.IgnoredDestinations = ignored;
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<string>> IgnoredDestinationsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return _state.IgnoredDestinations.ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<bool> IsIgnoredAsync(string hash, CancellationToken cancellationToken = default)
    {
        hash = NomadNetAddress.NormalizeHash(hash);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return _state.IgnoredDestinations.Contains(hash); }
        finally { _gate.Release(); }
    }

    public async Task<int> TrustLevelAsync(string hash, string? announcedName = null, CancellationToken cancellationToken = default)
    {
        hash = NomadNetAddress.NormalizeHash(hash);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var contact = _state.Contacts.Find(item => item.DestinationHash == hash);
            if (contact is null) return 2;
            if (contact.TrustLevel != 255 && announcedName is not null &&
                _state.Contacts.Any(item => item.DestinationHash != hash && item.Name == announcedName)) return 0;
            return contact.TrustLevel;
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
                AddAlert(normalized, normalized.Direction == "inbound", normalized.Direction == "outbound" &&
                    normalized.Status is "failed" or "rejected");
                if (_state.Messages.Count > MaximumMessages)
                    _state.Messages.RemoveRange(0, _state.Messages.Count - MaximumMessages);
            }
            await PersistAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task UpdateMessageStatusAsync(string id, string status, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = _state.Messages.FindIndex(message => message.Id == id);
            if (index < 0 || _state.Messages[index].Status is "delivered" or "failed" or "rejected" or "cancelled" or "paper") return;
            _state.Messages[index] = _state.Messages[index] with { Status = Bound(status, 40) };
            if (_state.Messages[index].Direction == "outbound" && status is "failed" or "rejected")
                AddAlert(_state.Messages[index], false, true);
            await PersistAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<NomadNetConversation>> ConversationsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _state.Messages.GroupBy(message => message.PeerHash).Select(messages =>
            {
                var alert = _state.ConversationAlerts.Find(item => item.PeerHash == messages.Key);
                return new NomadNetConversation(messages.Key, _state.Contacts.Find(item => item.DestinationHash == messages.Key)?.Name,
                    messages.Max(message => message.Timestamp), messages.Count(), alert?.Unread ?? 0, alert?.Failures ?? 0);
            }).OrderByDescending(item => item.LastActivity).ThenBy(item => item.PeerHash, StringComparer.Ordinal).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task MarkReadAsync(string hash, CancellationToken cancellationToken = default)
    {
        hash = NomadNetAddress.NormalizeHash(hash);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state.ConversationAlerts.RemoveAll(item => item.PeerHash == hash) > 0)
                await PersistAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task SetActiveConversationAsync(string? hash, CancellationToken cancellationToken = default)
    {
        if (hash is not null) hash = NomadNetAddress.NormalizeHash(hash);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _activePeer = hash;
            if (hash is not null && _state.ConversationAlerts.RemoveAll(item => item.PeerHash == hash) > 0)
                await PersistAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<string?> ActiveConversationAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return _activePeer; }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<string>> DeleteConversationAsync(string hash, bool failedOnly = false,
        CancellationToken cancellationToken = default)
    {
        hash = NomadNetAddress.NormalizeHash(hash);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var removed = _state.Messages.Where(message => message.PeerHash == hash &&
                (!failedOnly || message.Direction == "outbound" && message.Status is "failed" or "rejected")).ToArray();
            var ids = removed.Select(message => message.Id).ToHashSet(StringComparer.Ordinal);
            _state.Messages.RemoveAll(message => ids.Contains(message.Id));
            if (!failedOnly)
            {
                _state.ConversationAlerts.RemoveAll(item => item.PeerHash == hash);
                if (_activePeer == hash) _activePeer = null;
            }
            else
            {
                var index = _state.ConversationAlerts.FindIndex(item => item.PeerHash == hash);
                if (index >= 0) _state.ConversationAlerts[index] = _state.ConversationAlerts[index] with { Failures = 0 };
            }
            await PersistAsync(cancellationToken).ConfigureAwait(false);
            return removed.Select(message => message.Id).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<string> ExportConversationAsync(string hash, CancellationToken cancellationToken = default)
    {
        var messages = await MessagesAsync(hash, cancellationToken).ConfigureAwait(false);
        var output = new StringBuilder();
        foreach (var message in messages)
            output.AppendLine($"{message.Timestamp:O} {message.Direction} [{message.Status}] {message.Id}")
                .AppendLine(message.Title).AppendLine(message.Content).AppendLine();
        return output.ToString();
    }

    private void AddAlert(NomadNetMessage message, bool unread, bool failure)
    {
        if (_activePeer == message.PeerHash || !unread && !failure) return;
        var index = _state.ConversationAlerts.FindIndex(item => item.PeerHash == message.PeerHash);
        var current = index < 0 ? new NomadNetConversationAlert(message.PeerHash, 0, 0) : _state.ConversationAlerts[index];
        var updated = current with { Unread = Math.Min(MaximumMessages, current.Unread + (unread ? 1 : 0)),
            Failures = Math.Min(MaximumMessages, current.Failures + (failure ? 1 : 0)) };
        if (index < 0) _state.ConversationAlerts.Add(updated);
        else _state.ConversationAlerts[index] = updated;
    }

    private static NomadNetContact NormalizeContact(NomadNetContact contact)
    {
        ArgumentNullException.ThrowIfNull(contact);
        if (string.IsNullOrWhiteSpace(contact.Name) || contact.Name.Length > 80 || contact.Name.Any(char.IsControl) ||
            contact.TrustLevel is not (0 or 1 or 2 or 255) || contact.PreferredDelivery is < 1 or > 3 ||
            contact.Notes is null || contact.Notes.Length > 4096 ||
            contact.Notes.Any(character => char.IsControl(character) && character is not ('\n' or '\r' or '\t')))
            throw new InvalidDataException("Invalid contact name, trust, delivery or notes.");
        return contact with { DestinationHash = NomadNetAddress.NormalizeHash(contact.DestinationHash), Name = contact.Name.Trim() };
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
        var peers = _state.Messages.Select(message => message.PeerHash).ToHashSet(StringComparer.Ordinal);
        _state.ConversationAlerts.RemoveAll(item => !peers.Contains(item.PeerHash) || item.Unread == 0 && item.Failures == 0);
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

    public static (string DestinationHash, string Path) ParseUrl(string target, string? currentHash = null, bool media = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        var separator = target.IndexOf(':');
        string hash;
        string path;
        if (separator < 0 && target.StartsWith('/'))
        {
            hash = NormalizeHash(currentHash ?? throw new ArgumentException("Relative link has no current node.", nameof(target)));
            path = target;
        }
        else if (separator < 0)
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
        if (media)
        {
            if (!path.StartsWith("/media/", StringComparison.Ordinal) || path.Length > 512 || path.Contains('\\') ||
                path.Any(char.IsControl) || path.Split('/').Any(part => part is "." or ".."))
                throw new ArgumentException("Invalid Nomad Network media path.", nameof(target));
            return (hash, path);
        }
        return (hash, NormalizePagePath(path));
    }
}
