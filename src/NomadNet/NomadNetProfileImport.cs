using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using DotReticulum.Core;
using LXMF;

namespace NomadNet;

public sealed record NomadNetProfileImportReport(int Messages, int Attachments, int Contacts,
    IReadOnlyList<string> Warnings, IReadOnlyList<string> ArchivedFiles, IReadOnlyList<string> NotImportedFiles);

/// <summary>Offline, non-overwriting primary profile migration. Imported history is never queued for delivery.</summary>
public static class NomadNetProfileImport
{
    public const long MaximumProfileBytes = 512L * 1024 * 1024;
    public const int MaximumFiles = 12_000;
    public const int MaximumFileBytes = 20 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task<NomadNetProfileImportReport> ImportAsync(string primaryProfile, string targetDirectory,
        string rnsConfigDirectory, CancellationToken cancellationToken = default)
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(primaryProfile));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
        var rns = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rnsConfigDirectory));
        if (Within(source, target) || Within(target, source) || Within(source, rns) || Within(target, rns))
            throw new InvalidDataException("Source, absent target and explicitly selected RNS configuration must be separate roots.");
        EnsureNoLinks(target);
        EnsureNoLinks(rns);
        if (Path.Exists(target)) throw new IOException("Profile import requires an absent target; nothing was overwritten.");
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        var lockPath = target + ".profile-import.lock";
        EnsureNoLinks(lockPath);
        using var admission = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (Path.Exists(target)) throw new IOException("Profile import requires an absent target.");
        var snapshot = await SnapshotAsync(source, cancellationToken).ConfigureAwait(false);
        var staging = target + ".profile-import-" + Guid.NewGuid().ToString("N");
        var archived = new HashSet<string>(StringComparer.Ordinal);
        var activated = new HashSet<string>(StringComparer.Ordinal) { "storage/identity" };
        var warnings = new List<string>();
        try
        {
            Directory.CreateDirectory(staging);
            if (OperatingSystem.IsWindows())
            {
                using var owner = WindowsIdentity.GetCurrent();
                var user = owner.User ?? throw new IOException("Cannot determine native profile owner.");
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(staging).SetAccessControl(security);
            }
            else
                File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var lease = NomadNetProfileLease.Acquire(staging);
            var identityBytes = await ReadAsync(source, "storage/identity", 64, cancellationToken).ConfigureAwait(false);
            Identity identity;
            try
            {
                identity = Identity.FromPrivateKey(identityBytes);
                await WritePrivateAsync(Path.Combine(staging, "identity"), identityBytes, cancellationToken).ConfigureAwait(false);
            }
            finally { CryptographicOperations.ZeroMemory(identityBytes); }
            using (identity)
            {
                var ownHash = Destination.ComputeHashFromNameAndIdentity("lxmf.delivery", identity.Hash);
                await NomadNetRouterProfile.ImportAsync(source, staging, identity, snapshot, activated,
                    warnings, cancellationToken).ConfigureAwait(false);
                var configText = StrictUtf8.GetString(
                    await ReadAsync(source, "config", NomadNetConfigImport.MaximumConfigBytes, cancellationToken).ConfigureAwait(false));
                var config = NomadNetConfigImport.Parse(configText);
                warnings.AddRange(config.UnsupportedKeys.Select(key => "Unsupported setting: " + key));
                warnings.AddRange(config.Warnings);
                var settings = config.Settings with { RnsConfigDirectory = rns, PagesPath = null, FilesPath = null,
                    DownloadsPath = null, EnableExecutablePages = false };
                warnings.Add("External page/file/download paths and executable pages were not activated; network/RNS configuration was not copied.");
                var peers = await ReadPeerSettingsAsync(source, snapshot, warnings, cancellationToken).ConfigureAwait(false);
                if (peers is not null)
                {
                    if (peers.GetValueOrDefault("display_name") is not string display)
                        throw new InvalidDataException("Primary peer settings lack a display_name.");
                    settings = settings with { DisplayName = display,
                        LastClientAnnounce = Timestamp(peers.GetValueOrDefault("last_announce")),
                        LastLxmfSync = Timestamp(peers.GetValueOrDefault("last_lxmf_sync")),
                        PropagationNode = peers.GetValueOrDefault("propagation_node") is byte[] { Length: 16 } pn
                            ? Convert.ToHexStringLower(pn) : peers.GetValueOrDefault("propagation_node") is null ? null
                            : throw new InvalidDataException("Invalid saved propagation node.") };
                    warnings.Add("Peer settings counters, node announce timestamp, quit-confirm and unknown peer keys are archived, not active native settings.");
                }
                await settings.SaveAsync(staging, cancellationToken).ConfigureAwait(false);
                await NomadNetNodeProfile.ImportAsync(source, staging, identity, snapshot, settings, activated,
                    warnings, cancellationToken).ConfigureAwait(false);
                var ignoredBytes = snapshot.ContainsKey("ignored") ? await ReadAsync(source, "ignored",
                    NomadNetDirectoryImport.MaximumIgnoredBytes, cancellationToken).ConfigureAwait(false) : [];
                var directory = snapshot.ContainsKey("storage/directory")
                    ? await NomadNetDirectoryImport.ReadAsync(source, cancellationToken).ConfigureAwait(false)
                    : NomadNetDirectoryImport.Parse(MsgPack.Pack(new Dictionary<object, object?>
                    { ["entry_list"] = Array.Empty<object>(), ["announce_stream"] = Array.Empty<object>() }),
                        ignoredBytes);
                warnings.AddRange(directory.Warnings);
                if (directory.SkippedAnnounces > 0) warnings.Add($"Archived {directory.SkippedAnnounces} transient directory announces; not activated as saved contacts.");
                var state = new NomadNetState
                {
                    Contacts = directory.Contacts.ToList(),
                    IgnoredDestinations = directory.IgnoredDestinations.ToList(),
                };
                var indexClaims = new Dictionary<string, bool?>(StringComparer.Ordinal);
                var conversationsRoot = Path.Combine(source, "storage", "conversations");
                if (Directory.Exists(conversationsRoot))
                    foreach (var conversation in Directory.EnumerateDirectories(conversationsRoot))
                    {
                        EnsureNoLinks(conversation);
                        var peer = Path.GetFileName(conversation);
                        if (!IsHash(peer, 32)) { warnings.Add("Unrecognised conversation directory archived, not activated: " + peer); continue; }
                        if (state.ConversationShells.Count >= NomadNetStore.MaximumMessages)
                            throw new InvalidDataException("Primary conversation count exceeds native bounds.");
                        state.ConversationShells.Add(new(peer, Directory.GetLastWriteTimeUtc(conversation)));
                        var indexPath = $"storage/conversations/{peer}/.index";
                        if (!snapshot.ContainsKey(indexPath)) continue;
                        try
                        {
                            var index = DecodeMap(await ReadAsync(source, indexPath, NomadNetStore.MaximumStateBytes, cancellationToken).ConfigureAwait(false));
                            var stale = 0;
                            foreach (var (key, value) in index)
                            {
                                if (key is not string id || !IsHash(id, 64) ||
                                    !snapshot.ContainsKey($"storage/conversations/{peer}/{id}")) { stale++; continue; }
                                if (value is Dictionary<object, object?> entry)
                                    indexClaims[peer + "/" + id] = entry.GetValueOrDefault("signature_validated") as bool?;
                            }
                            if (stale > 0) warnings.Add($"Archived {stale} stale/non-message index entries for {peer}; no missing container was invented from cached text.");
                        }
                        catch (Exception error) when (error is InvalidDataException or FormatException or ArgumentException or OverflowException)
                        { warnings.Add("Corrupt conversation index archived, not trusted; history loaded from containers for " + peer); }
                    }
                var attachments = 0;
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var relative in snapshot.Keys.Order(StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var parts = relative.Split('/');
                    if (parts.Length != 4 || parts[0] != "storage" || parts[1] != "conversations" ||
                        !IsHash(parts[2], 32) || !IsHash(parts[3], 64)) continue;
                    if (state.Messages.Count >= NomadNetStore.MaximumMessages)
                        throw new InvalidDataException("Profile exceeds the native message limit; no history was evicted.");
                    var id = parts[3];
                    if (!ids.Add(id)) throw new InvalidDataException("Duplicate message ID across primary conversations.");
                    var bytes = await ReadAsync(source, relative, MaximumFileBytes, cancellationToken).ConfigureAwait(false);
                    var message = LxMessage.UnpackContainer(bytes, hash => hash.AsSpan().SequenceEqual(ownHash) ? identity : null);
                    var outgoing = message.SourceHash.AsSpan().SequenceEqual(ownHash);
                    if (!(outgoing ? message.DestinationHash : message.SourceHash).AsSpan().SequenceEqual(Convert.FromHexString(parts[2])) ||
                        !outgoing && !message.DestinationHash.AsSpan().SequenceEqual(ownHash))
                        throw new InvalidDataException("Conversation directory does not match message participants.");
                    if (!Enum.IsDefined(message.State) || !Enum.IsDefined(message.Method))
                        throw new InvalidDataException("Unknown historical LXMF state or method.");
                    var fields = message.Fields;
                    var restored = await ReadAttachmentsAsync(source, snapshot, id, cancellationToken).ConfigureAwait(false);
                    if (restored.Count > 0 && NomadNetAttachments.Extract(fields).Count == 0)
                        fields[LxmField.FileAttachments] = restored.Select(item => (object?)new object?[] { item.Name, item.Data }).ToArray();
                    var extracted = NomadNetAttachments.Extract(fields);
                    var imageIndex = NomadNetAttachments.ImageIndex(fields);
                    if (imageIndex is null && restored.Count > 0)
                        imageIndex = extracted.Select((item, index) => (item, index))
                            .Where(item => NomadNetAttachments.MediaExtension(null, item.item.Data, false) != ".bin")
                            .Select(item => (int?)item.index).FirstOrDefault();
                    attachments += extracted.Count;
                    await NomadNetAttachments.SaveAsync(staging, id, fields, cancellationToken).ConfigureAwait(false);
                    var hashMatches = Convert.ToHexStringLower(message.Hash!) == id;
                    var auth = !hashMatches ? "container-hash-mismatch-not-authenticated" :
                        message.SignatureValidated ? "verified-local-signature" :
                        outgoing ? "invalid-local-signature" : "remote-public-key-unavailable";
                    var text = message.Content ?? throw new InvalidDataException("Binary history content cannot be represented as native text.");
                    if (text.Length > NomadNetStore.MaximumMessageCharacters || message.Title.Length > 512)
                        throw new InvalidDataException("Historical text exceeds native bounds; nothing was truncated.");
                    var status = message.State is LxmState.Outbound or LxmState.Sending or LxmState.Generating ? "failed" :
                        message.State.ToString().ToLowerInvariant();
                    state.Messages.Add(new(id, parts[2], outgoing ? "outbound" : "inbound",
                        Timestamp(message.Timestamp) ?? throw new InvalidDataException("Missing history timestamp."),
                        message.Title, text, status)
                    {
                        Renderer = NomadNetPresentation.Renderer(fields),
                        ImageAttachmentIndex = imageIndex,
                        ImportedAttachmentNames = string.Join('\n', extracted.Select(item => item.Name)),
                        HistoricalAuthentication = auth,
                        HistoricalSignatureValidated = indexClaims.GetValueOrDefault(parts[2] + "/" + id),
                        HistoricalState = (int)message.State,
                        HistoricalMethod = (int)message.Method,
                    });
                    var archive = Path.Combine(staging, "import-archive", relative.Replace('/', Path.DirectorySeparatorChar) + ".bin");
                    Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
                    await File.WriteAllBytesAsync(archive, bytes, cancellationToken).ConfigureAwait(false);
                    archived.Add(relative);
                }
                foreach (var peer in state.ConversationShells.Select(shell => shell.PeerHash))
                {
                    state.ConversationAlerts.Add(new(peer,
                        await CounterAsync(source, snapshot, peer, "unread", cancellationToken).ConfigureAwait(false),
                        await CounterAsync(source, snapshot, peer, "failed", cancellationToken).ConfigureAwait(false)));
                }
                var stateBytes = JsonSerializer.SerializeToUtf8Bytes(state, NomadNetJsonContext.Default.NomadNetState);
                if (stateBytes.Length > NomadNetStore.MaximumStateBytes)
                    throw new InvalidDataException("Imported history exceeds native state bounds; no records were evicted.");
                await File.WriteAllBytesAsync(Path.Combine(staging, "state.json"), stateBytes, cancellationToken).ConfigureAwait(false);
                await NomadNetRrcImport.ImportAsync(source, staging, snapshot, settings.DisplayName, configText,
                    activated, warnings, cancellationToken).ConfigureAwait(false);
                foreach (var relative in snapshot.Keys)
                {
                    if (relative == "config" || relative == "ignored" ||
                        relative is "storage/directory" or "storage/peersettings" or "storage/peersettings.bak" ||
                        relative.StartsWith("storage/attachments/", StringComparison.Ordinal) ||
                        relative.StartsWith("storage/conversations/", StringComparison.Ordinal) ||
                        activated.Contains(relative) && relative.StartsWith("storage/rrc", StringComparison.Ordinal))
                    {
                        if (!archived.Add(relative)) continue;
                        var archive = Path.Combine(staging, "import-archive", relative.Replace('/', Path.DirectorySeparatorChar) + ".bin");
                        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
                        await File.WriteAllBytesAsync(archive,
                            await ReadAsync(source, relative, MaximumFileBytes, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                    }
                }
                warnings.Add("Historical .index claims, attachment manifests and original containers are inert archives, not authentication. Pending historical sends are failed, never resumed; paper remains a saved offline document, never resent. Stripped media loses original field typing; recovered image previews are detected from encoded bytes, not authenticated FIELD_IMAGE provenance.");
                var notImported = snapshot.Keys.Where(path => !activated.Contains(path) && !archived.Contains(path)).Order(StringComparer.Ordinal).ToArray();
                var report = new NomadNetProfileImportReport(state.Messages.Count, attachments, state.Contacts.Count,
                    warnings, archived.Order(StringComparer.Ordinal).ToArray(), notImported);
                await File.WriteAllBytesAsync(Path.Combine(staging, "profile-import.json"),
                    JsonSerializer.SerializeToUtf8Bytes(report, NomadNetJsonContext.Default.NomadNetProfileImportReport), cancellationToken).ConfigureAwait(false);
                _ = await NomadNetStore.OpenAsync(staging, cancellationToken).ConfigureAwait(false);
                _ = await NomadNetSettings.OpenAsync(staging, cancellationToken).ConfigureAwait(false);
                var after = await SnapshotAsync(source, cancellationToken).ConfigureAwait(false);
                if (snapshot.Count != after.Count || snapshot.Any(item => after.GetValueOrDefault(item.Key) != item.Value))
                    throw new IOException("Primary profile changed during import; stop the primary process and retry.");
                cancellationToken.ThrowIfCancellationRequested();
                // Windows cannot rename the directory while its exclusive lease handle is open.
                // The sibling admission lock still excludes other importers; Move refuses any target created by a native owner.
                lease.Dispose();
                Directory.Move(staging, target);
                return report;
            }
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }

    internal static async Task<Identity> ReadIdentityAsync(string directory, CancellationToken cancellationToken)
    {
        var bytes = await ReadAsync(directory, "identity", 64, cancellationToken).ConfigureAwait(false);
        try { return Identity.FromPrivateKey(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static async Task WritePrivateAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(path, options);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(true);
    }

    private static async Task<Dictionary<object, object?>?> ReadPeerSettingsAsync(string root,
        Dictionary<string, string> snapshot, List<string> warnings, CancellationToken token)
    {
        foreach (var path in new[] { "storage/peersettings", "storage/peersettings.bak" })
        {
            if (!snapshot.ContainsKey(path)) continue;
            try
            {
                var peers = DecodeMap(await ReadAsync(root, path, NomadNetSettings.MaximumSettingsBytes, token).ConfigureAwait(false));
                if (peers.GetValueOrDefault("display_name") is not string)
                    throw new InvalidDataException("Missing peer display name.");
                if (path.EndsWith(".bak", StringComparison.Ordinal)) warnings.Add("Recovered peer settings from primary backup.");
                return peers;
            }
            catch (Exception error) when (error is InvalidDataException or FormatException or ArgumentException or OverflowException)
            { warnings.Add("Invalid peer settings archived: " + path); }
        }
        throw new InvalidDataException("No valid primary peer settings or backup; refusing to silently replace peer settings.");
    }

    private static async Task<List<NomadNetAttachment>> ReadAttachmentsAsync(string root, Dictionary<string, string> snapshot,
        string id, CancellationToken token)
    {
        var manifestPath = $"storage/attachments/{id}/manifest";
        if (!snapshot.ContainsKey(manifestPath)) return [];
        var manifest = DecodeMap(await ReadAsync(root, manifestPath, 64 * 1024, token).ConfigureAwait(false));
        if (manifest.GetValueOrDefault("files") is not object?[] files || files.Length > NomadNetAttachments.MaximumAttachments)
            throw new InvalidDataException("Invalid primary attachment manifest.");
        var result = new List<NomadNetAttachment>();
        long total = 0;
        foreach (var item in files)
        {
            if (item is not Dictionary<object, object?> entry || entry.GetValueOrDefault("stored_name") is not string stored ||
                !stored.StartsWith("file_", StringComparison.Ordinal) || stored.Length > 16 ||
                stored.Length <= 5 || stored.AsSpan(5).ContainsAnyExceptInRange('0', '9') ||
                entry.GetValueOrDefault("name") is not string name)
                throw new InvalidDataException("Invalid primary attachment metadata.");
            var bytes = await ReadAsync(root, $"storage/attachments/{id}/{stored}", NomadNetAttachments.MaximumBytes, token).ConfigureAwait(false);
            if (entry.GetValueOrDefault("size") is not long size || size != bytes.Length ||
                (total += bytes.Length) > NomadNetAttachments.MaximumBytes)
                throw new InvalidDataException("Primary attachment size or aggregate bound is invalid.");
            result.Add(new(NomadNetAttachments.SafeName(name), bytes));
        }
        return result;
    }

    private static async Task<int> CounterAsync(string root, Dictionary<string, string> snapshot, string peer, string name, CancellationToken token)
    {
        var path = $"storage/conversations/{peer}/{name}";
        if (!snapshot.ContainsKey(path)) return 0;
        var text = StrictUtf8.GetString(await ReadAsync(root, path, 32, token).ConfigureAwait(false)).Trim();
        if (text.Length == 0) return 1;
        if (!int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture,
                out var count) || count is < 0 or > NomadNetStore.MaximumMessages)
            throw new InvalidDataException("Conversation alert count exceeds native bounds.");
        return count;
    }

    private static Dictionary<object, object?> DecodeMap(byte[] bytes) =>
        MsgPack.UnpackApplicationValue(bytes, new MsgPackLimits { MaxEncodedBytes = MaximumFileBytes,
            MaxValueBytes = MaximumFileBytes, MaxContainerEntries = 10_000, MaxValues = 150_000, MaxDepth = 16 })
            as Dictionary<object, object?> ?? throw new InvalidDataException("Expected a primary MessagePack map.");

    private static DateTimeOffset? Timestamp(object? value)
    {
        if (value is null) return null;
        var seconds = value switch { double number => number, long number => number, _ => throw new InvalidDataException("Invalid timestamp.") };
        if (!double.IsFinite(seconds) || seconds is < 0 or > 253402300799)
            throw new InvalidDataException("Historical timestamp is out of range.");
        return DateTimeOffset.FromUnixTimeMilliseconds(checked((long)(seconds * 1000)));
    }

    private static bool IsHash(string text, int length) =>
        text.Length == length && text.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool Within(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void EnsureNoLinks(string path)
    {
        for (var item = new FileInfo(Path.GetFullPath(path)); item is not null; item = item.Directory is { } parent ? new FileInfo(parent.FullName) : null)
            if (Path.Exists(item.FullName) && (File.GetAttributes(item.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Profile import refuses links/reparse points.");
    }

    internal static async Task<byte[]> ReadAsync(string root, string relative, int maximum, CancellationToken token)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        EnsureNoLinks(path);
        return await NomadNetContent.ReadBoundedAsync(path, maximum, token).ConfigureAwait(false);
    }

    private static async Task<Dictionary<string, string>> SnapshotAsync(string root, CancellationToken token)
    {
        EnsureNoLinks(root);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(root);
        var entries = 0;
        long total = 0;
        while (pending.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                if (++entries > MaximumFiles) throw new InvalidDataException("Primary profile exceeds entry bound.");
                EnsureNoLinks(path);
                if (Directory.Exists(path)) { pending.Push(path); continue; }
                var info = new FileInfo(path);
                if (!NomadNetLocalFiles.IsRegularFile(path)) throw new InvalidDataException("Profile import requires regular files, never pipes or devices.");
                if ((total += info.Length) > MaximumProfileBytes || info.Length > MaximumFileBytes)
                    throw new InvalidDataException("Primary profile exceeds byte bounds.");
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var digest = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
                result.Add(Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'), Convert.ToHexStringLower(digest));
            }
        }
        return result;
    }
}
