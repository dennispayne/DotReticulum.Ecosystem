using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotReticulum.Core;
using LXMF;

namespace Sideband;

public sealed record SidebandProfileImportResult(
    string DataDirectory, string DeliveryHash, int Conversations, int Messages, IReadOnlyList<string> Notices);

/// <summary>
/// Imports a stable upstream desktop profile into a new native data directory. No source file is opened for writing.
/// The destination must not exist; callers must stop both applications before migration.
/// </summary>
public static class SidebandProfileImporter
{
    private const int MaximumConfigBytes = 1024 * 1024;
    private const long MaximumDatabaseBytes = 128L * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static async Task<SidebandProfileImportResult> ImportAsync(
        string sourceProfileDirectory, string destinationDataDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceProfileDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDataDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        var source = Path.GetFullPath(sourceProfileDirectory);
        if (Directory.Exists(Path.Combine(source, "app_storage"))) source = Path.Combine(source, "app_storage");
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDataDirectory));
        if (Path.Exists(target)) throw new IOException("Profile import requires a destination directory that does not exist. Existing state is never merged or replaced.");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if ((target + Path.DirectorySeparatorChar).StartsWith(source + Path.DirectorySeparatorChar, comparison) ||
            (source + Path.DirectorySeparatorChar).StartsWith(target + Path.DirectorySeparatorChar, comparison))
            throw new IOException("Source and destination profile directories must be separate.");
        var parent = Path.GetDirectoryName(target) ?? throw new IOException("The destination must have a parent directory.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, "." + Path.GetFileName(target) + ".import-" + Guid.NewGuid().ToString("N"));
        CreatePrivateDirectory(staging);
        try
        {
            var archive = Path.Combine(staging, "imported-profile");
            CreatePrivateDirectory(archive);
            // A stable copy includes committed frames still in WAL. Two complete fingerprints detect
            // concurrent writes on platforms where FileShare.Read does not exclude other writers.
            var files = new[] { "primary_identity", "sideband_config", "sideband.db", "sideband.db-wal", "sideband.db-shm" };
            var before = await FingerprintAsync(source, files, cancellationToken).ConfigureAwait(false);
            foreach (var pair in before)
            {
                var name = pair.Key;
                await CopyPrivateAsync(Path.Combine(source, name), Path.Combine(archive, name),
                    LimitFor(name), cancellationToken).ConfigureAwait(false);
            }
            var after = await FingerprintAsync(source, files, cancellationToken).ConfigureAwait(false);
            if (before.Count != after.Count || before.Any(pair => !after.TryGetValue(pair.Key, out var value) || pair.Value != value))
                throw new IOException("The source profile changed during snapshot creation. Stop upstream Sideband and retry.");
            foreach (var pair in before)
            {
                var copiedHash = await HashFileAsync(Path.Combine(archive, pair.Key), LimitFor(pair.Key), cancellationToken).ConfigureAwait(false);
                if (copiedHash != pair.Value) throw new IOException("The source profile changed while it was copied. Stop upstream Sideband and retry.");
            }

            var key = await ReadBoundedAsync(Path.Combine(archive, "primary_identity"), 64, cancellationToken).ConfigureAwait(false);
            Identity identity;
            try
            {
                if (key.Length != 64) throw new InvalidDataException("The upstream identity must contain exactly 64 bytes.");
                identity = Identity.FromPrivateKey(key);
            }
            finally { CryptographicOperations.ZeroMemory(key); }
            using (identity)
            {
                var ownHash = LxMessage.DeliveryDestinationHash(identity);
                var own = Convert.ToHexStringLower(ownHash);
                var finalConfig = Path.Combine(target, "reticulum");
                var store = new SidebandStateStore(staging);
                var configBytes = await ReadBoundedAsync(Path.Combine(archive, "sideband_config"), MaximumConfigBytes, cancellationToken).ConfigureAwait(false);
                byte[] configJson;
                try { configJson = ConfigToJson(configBytes); }
                finally { CryptographicOperations.ZeroMemory(configBytes); }
                var settingsPath = Path.Combine(staging, "import-settings.json");
                try { await WritePrivateAsync(settingsPath, configJson, cancellationToken).ConfigureAwait(false); }
                finally { CryptographicOperations.ZeroMemory(configJson); }
                IReadOnlyList<string> unsupported;
                try { unsupported = await store.ImportSettingsAsync(settingsPath, cancellationToken).ConfigureAwait(false); }
                finally { File.Delete(settingsPath); }
                var notices = new List<string>
                {
                    "LXMF conversation names, addresses, trust and unread flags, text, file attachments and delivery states are activated. Conversation activity timestamps/data, transport/stamp extras, other LXMF fields, voice objects, announces, telemetry and persistent state remain archive-only.",
                    "The original binary configuration and SQLite snapshot are retained privately in imported-profile. Unknown binary configuration values use hexadecimal JSON projections; the original MessagePack remains authoritative.",
                    "Reticulum interfaces, LXMF router tickets/ratchets/queues, plugins and other app_storage files are not imported. Configure interfaces in the new private reticulum directory before connecting.",
                    "Historical signatures are verified only when the source is the imported identity; remote sender identities are not available in this profile snapshot. Import is not live interoperability evidence.",
                };
                if (unsupported.Count > 0) notices.Add("Configuration keys retained without native behavior: " + string.Join(", ", unsupported));
                var contacts = new List<SidebandContact>();
                var messages = new List<SidebandChatMessage>();
                using (var database = new ProfileSqlite(Path.Combine(archive, "sideband.db"), cancellationToken))
                {
                    ValidateSchema(database, "conv", ["dest_context", "last_tx", "last_rx", "unread", "type", "trust", "name", "data"]);
                    ValidateSchema(database, "lxm", ["lxm_hash", "dest", "source", "title", "tx_ts", "rx_ts", "state", "method", "t_encrypted", "t_encryption", "data", "extra"]);
                    foreach (var row in database.Rows("SELECT dest_context, type, name, trust, unread FROM conv LIMIT 1001", 1_000))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var hash = Bytes(row[0], 16, "conversation address");
                        if (row[1] is not long type) throw new InvalidDataException("Invalid conversation type.");
                        if (type != 1)
                        {
                            notices.Add("A non-LXMF conversation is retained in the archive only.");
                            continue;
                        }
                        var address = Convert.ToHexStringLower(hash);
                        var name = row[2] switch
                        {
                            byte[] bytes => Utf8.GetString(bytes),
                            string text => text,
                            _ => throw new InvalidDataException("Invalid conversation name."),
                        };
                        contacts.Add(SidebandContact.Create(string.IsNullOrWhiteSpace(name) ? address : name, address,
                            ReadFlag(row[3], "conversation trust"), ReadFlag(row[4], "conversation unread")));
                    }
                    var attachments = new SidebandAttachmentStore(staging);
                    foreach (var row in database.Rows("SELECT lxm_hash, dest, source, state, method, data FROM lxm LIMIT 10001", 10_000))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var hash = Bytes(row[0], 32, "message hash");
                        var destination = Bytes(row[1], 16, "message destination");
                        var sender = Bytes(row[2], 16, "message source");
                        if (row[3] is not long state || row[4] is not long method || !Enum.IsDefined((LxmMethod)checked((byte)method)))
                            throw new InvalidDataException("Invalid message state or method.");
                        var raw = row[5] as byte[] ?? throw new InvalidDataException("A message is missing its packed LXMF data.");
                        if (method == (long)LxmMethod.Paper)
                        {
                            if (MsgPack.Unpack(raw) is not object?[] { Length: 2 } paper || paper[0] is not byte[] wire || paper[1] is not byte[])
                                throw new InvalidDataException("Invalid stored paper message.");
                            raw = wire;
                        }
                        var message = LxMessage.Unpack(raw,
                            address => address.AsSpan().SequenceEqual(ownHash) ? identity : null, (LxmMethod)method);
                        if (message.Hash is null || !CryptographicOperations.FixedTimeEquals(hash, message.Hash) ||
                            !destination.AsSpan().SequenceEqual(message.DestinationHash) || !sender.AsSpan().SequenceEqual(message.SourceHash) ||
                            message.UnverifiedReason == LxmUnverifiedReason.SignatureInvalid)
                            throw new InvalidDataException("Stored LXMF message integrity does not match its database record.");
                        var incoming = destination.AsSpan().SequenceEqual(ownHash);
                        if (!incoming && !sender.AsSpan().SequenceEqual(ownHash))
                            throw new InvalidDataException("The database contains a message unrelated to the imported identity.");
                        var peer = Convert.ToHexStringLower(incoming ? sender : destination);
                        if (!contacts.Any(contact => contact.DeliveryHash == peer))
                            throw new InvalidDataException("A message has no matching LXMF conversation. The import was not committed.");
                        var importedAttachments = new List<SidebandAttachment>();
                        if (message.TryGetField(LxmField.FileAttachments, out var field))
                        {
                            if (field is not object?[] entries || entries.Length > 8)
                                throw new InvalidDataException("Unsupported or oversized file attachment list.");
                            foreach (var entry in entries)
                            {
                                if (entry is not object?[] { Length: 2 } item || item[0] is not string name || item[1] is not byte[] content)
                                    throw new InvalidDataException("Invalid LXMF file attachment.");
                                importedAttachments.Add(await attachments.StoreAsync(name, content, cancellationToken).ConfigureAwait(false));
                            }
                        }
                        if (!Enum.IsDefined((LxmState)checked((byte)state))) throw new InvalidDataException("Unknown LXMF delivery state.");
                        messages.Add(new SidebandChatMessage(new Guid(hash.AsSpan(0, 16)), peer, incoming,
                            message.Title, message.Content ?? throw new InvalidDataException("Binary message content is not supported."),
                            DateTimeOffset.UnixEpoch.AddSeconds(message.Timestamp),
                            ((LxmState)state).ToString(), importedAttachments.Count == 0 ? null : importedAttachments,
                            DeliveryMethod: ((LxmMethod)method).ToString()));
                    }
                    var tables = database.Rows("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name", 64);
                    foreach (var table in tables)
                        if (table[0] is string name && name is not ("conv" or "lxm"))
                            notices.Add("Database table retained without native behavior: " + name);
                }
                var settings = await store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                await store.SaveImportedStateAsync(settings with
                {
                    ReticulumConfigDirectory = finalConfig,
                    OwnDeliveryHash = own,
                    Contacts = contacts.OrderBy(contact => contact.DeliveryHash, StringComparer.Ordinal).ToArray(),
                    Messages = messages.OrderBy(message => message.Timestamp).ThenBy(message => message.Id).ToArray(),
                }, cancellationToken).ConfigureAwait(false);
                var storage = Path.Combine(staging, "reticulum", "storage");
                CreatePrivateDirectory(Path.GetDirectoryName(storage)!);
                CreatePrivateDirectory(storage);
                IdentityFile.SaveNew(identity, Path.Combine(storage, "sideband"));
                // There must be no second identity copy in the retained reference archive.
                File.Delete(Path.Combine(archive, "primary_identity"));
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Move(staging, target);
                return new SidebandProfileImportResult(target, own, contacts.Count, messages.Count, notices.Distinct(StringComparer.Ordinal).ToArray());
            }
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }

    private static byte[] Bytes(object? value, int length, string description) =>
        value is byte[] bytes && bytes.Length == length ? bytes : throw new InvalidDataException("Invalid " + description + ".");

    private static bool ReadFlag(object? value, string description) =>
        value is long flag && flag is 0 or 1 ? flag == 1 : throw new InvalidDataException("Invalid " + description + " flag; expected integer 0 or 1.");

    private static void ValidateSchema(ProfileSqlite database, string table, string[] columns)
    {
        var schema = database.Rows("SELECT name, type, sql FROM sqlite_master WHERE name='" + table + "'", 1).SingleOrDefault();
        if (schema is null || schema[1] is not "table" || schema[2] is not string sql ||
            !sql.StartsWith("CREATE TABLE ", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Required profile table is missing or is not an ordinary table.");
        var actual = database.Rows("PRAGMA table_info(" + table + ")", 32);
        if (!actual.Select(row => row[1] as string).SequenceEqual(columns))
            throw new InvalidDataException("Unsupported upstream profile database schema.");
    }

    private static byte[] ConfigToJson(byte[] bytes)
    {
        if (MsgPack.Unpack(bytes) is not Dictionary<object, object?> map || map.Count > 2_048)
            throw new InvalidDataException("The upstream configuration must be a bounded MessagePack dictionary.");
        using var stream = new MemoryStream();
        try
        {
            using (var writer = new Utf8JsonWriter(stream)) WriteValue(writer, map);
            return stream.ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(stream.GetBuffer()); }
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case string text: writer.WriteStringValue(text); break;
            case byte[] bytes: writer.WriteStringValue(Convert.ToHexStringLower(bytes)); break;
            case bool flag: writer.WriteBooleanValue(flag); break;
            case long integer: writer.WriteNumberValue(integer); break;
            case ulong integer: writer.WriteNumberValue(integer); break;
            case double number when double.IsFinite(number): writer.WriteNumberValue(number); break;
            case float number when float.IsFinite(number): writer.WriteNumberValue(number); break;
            case object?[] values:
                writer.WriteStartArray();
                foreach (var item in values) WriteValue(writer, item);
                writer.WriteEndArray();
                break;
            case Dictionary<object, object?> map:
                writer.WriteStartObject();
                foreach (var pair in map)
                {
                    if (pair.Key is not string key) throw new InvalidDataException("Configuration maps require string keys; the import was not committed.");
                    writer.WritePropertyName(key);
                    WriteValue(writer, pair.Value);
                }
                writer.WriteEndObject();
                break;
            default: throw new InvalidDataException("Unsupported upstream configuration value; the import was not committed.");
        }
    }

    private static int LimitFor(string name) => name switch
    {
        "primary_identity" => 64,
        "sideband_config" => MaximumConfigBytes,
        "sideband.db-shm" => 8 * 1024 * 1024,
        _ => (int)MaximumDatabaseBytes,
    };

    private static async Task<Dictionary<string, string>> FingerprintAsync(string source, string[] files, CancellationToken token)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in files)
        {
            var path = Path.Combine(source, name);
            if (!File.Exists(path) && name is "sideband.db-wal" or "sideband.db-shm") continue;
            result[name] = await HashFileAsync(path, LimitFor(name), token).ConfigureAwait(false);
        }
        return result;
    }

    private static async Task<string> HashFileAsync(string path, int maximumBytes, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16_384, FileOptions.Asynchronous);
        if (stream.Length > maximumBytes) throw new InvalidDataException("A profile file exceeds its byte safety limit.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[16_384];
        long length = 0;
        try
        {
            int count;
            while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                length += count;
                if (length > maximumBytes) throw new InvalidDataException("A profile file exceeds its byte safety limit.");
                hash.AppendData(buffer.AsSpan(0, count));
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static async Task CopyPrivateAsync(string source, string target, int maximumBytes, CancellationToken token)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 16_384, FileOptions.Asynchronous);
        if (input.Length > maximumBytes) throw new InvalidDataException("A profile file exceeds its byte safety limit.");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var output = new FileStream(target, options);
        var buffer = new byte[16_384];
        long total = 0;
        try
        {
            int count;
            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                total += count;
                if (total > maximumBytes) throw new InvalidDataException("A profile file exceeds its byte safety limit.");
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            }
            await output.FlushAsync(token).ConfigureAwait(false);
            output.Flush(true);
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int maximumBytes, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16_384, FileOptions.Asynchronous);
        if (input.Length > maximumBytes) throw new InvalidDataException("A profile file exceeds its byte safety limit.");
        var result = new byte[(int)input.Length];
        try
        {
            await input.ReadExactlyAsync(result, token).ConfigureAwait(false);
            if (await input.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0)
                throw new IOException("A profile file changed while being read.");
            return result;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(result);
            throw;
        }
    }

    private static async Task WritePrivateAsync(string path, byte[] bytes, CancellationToken token)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(path, options);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
