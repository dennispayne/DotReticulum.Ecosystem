using System.Security.Cryptography;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;
using LXMF;

namespace Sideband;

public sealed record SidebandRouterProfileResult(int ImportedFiles, IReadOnlyList<string> Notices);

/// <summary>Offline client-router migration into caller-owned private staging, not a running profile.
/// Only known LXMF client files and the imported identity's signed delivery ratchets are activated.</summary>
public static class SidebandRouterProfile
{
    private const int SmallLimit = 4 * 1024 * 1024;
    private const int CacheLimit = 8 * 1024 * 1024;
    private static readonly MsgPackLimits Limits = new()
    {
        MaxEncodedBytes = CacheLimit, MaxValueBytes = CacheLimit,
        MaxContainerEntries = 65_536, MaxValues = 600_001, MaxDepth = 6,
    };

    public static async Task<SidebandRouterProfileResult> ImportAsync(string sourceAppStorage,
        string nativeStagingDirectory, Identity importedIdentity, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceAppStorage);
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeStagingDirectory);
        ArgumentNullException.ThrowIfNull(importedIdentity);
        if (!importedIdentity.HasPrivateKey) throw new ArgumentException("Router migration requires the imported private identity.", nameof(importedIdentity));
        cancellationToken.ThrowIfCancellationRequested();
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceAppStorage));
        var staging = Path.TrimEndingDirectorySeparator(Path.GetFullPath(nativeStagingDirectory));
        if (!Directory.Exists(source) || !Directory.Exists(staging)) throw new DirectoryNotFoundException("Source app_storage and native staging directories must exist.");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if ((source + Path.DirectorySeparatorChar).StartsWith(staging + Path.DirectorySeparatorChar, comparison) ||
            (staging + Path.DirectorySeparatorChar).StartsWith(source + Path.DirectorySeparatorChar, comparison))
            throw new IOException("Router source and staging must be separate directories.");
        RejectLink(source);
        RejectLink(staging);
        var target = Path.Combine(staging, "lxmf");
        if (Path.Exists(target)) throw new IOException("Router migration cannot replace existing native LXMF state.");
        var ownRatchet = Path.Combine("lxmf", "ratchets", Convert.ToHexStringLower(LxMessage.DeliveryDestinationHash(importedIdentity)) + ".ratchets");
        var selected = new[]
        {
            Path.Combine("lxmf", "available_tickets"), Path.Combine("lxmf", "outbound_stamp_costs"),
            Path.Combine("lxmf", "local_deliveries"), Path.Combine("lxmf", "locally_processed"), ownRatchet,
        };
        var tracked = selected.Append("primary_identity").ToArray();
        var before = await SnapshotAsync(source, tracked, cancellationToken).ConfigureAwait(false);
        if (before["primary_identity"] is not null)
        {
            var bytes = await ReadAsync(Path.Combine(source, "primary_identity"), 64, cancellationToken).ConfigureAwait(false);
            try
            {
                if (bytes.Length != 64) throw new InvalidDataException("The source identity must be exactly 64 bytes.");
                using var identity = Identity.FromPrivateKey(bytes);
                if (!CryptographicOperations.FixedTimeEquals(identity.ExportPublicKey(), importedIdentity.ExportPublicKey()))
                    throw new InvalidDataException("Router source identity does not match the imported identity.");
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        var notices = new[]
        {
            "Migrated client-router tickets, stamp-cost cache, duplicate histories and only this identity's signed delivery ratchets when present. Native expiry cleanup applies on load.",
            "Other identities' ratchets, unknown files, RNS global caches/interfaces, propagation-node peers/message stores and node_stats are not migrated. Sideband does not enable propagation-node service; node_stats is not restored by the upstream client router.",
            "Upstream client inbound/outbound queues are memory-only; no queued messages are reconstructed.",
            "Migration bounds: 4 MiB tickets/ratchets, 8 MiB per cache, 4,096 ticket destinations per map, 16 inbound tickets per destination, 65,536 cache entries and 512 own ratchet keys. Unsupported schemas fail rather than being repaired or truncated.",
        };
        var count = selected.Count(name => before[name] is not null);
        if (count == 0)
        {
            await VerifySnapshotAsync(source, tracked, before, cancellationToken).ConfigureAwait(false);
            return new SidebandRouterProfileResult(0, notices);
        }
        var work = Path.Combine(staging, ".router-profile-" + Guid.NewGuid().ToString("N"));
        CreatePrivateDirectory(work);
        try
        {
            foreach (var name in selected)
            {
                if (before[name] is null) continue;
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = await ReadAsync(Path.Combine(source, name), Maximum(name), cancellationToken).ConfigureAwait(false);
                try
                {
                    if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != before[name])
                        throw new IOException("Router source changed while its snapshot was copied. Stop upstream Sideband and retry.");
                    Validate(name, bytes, importedIdentity);
                    var destination = Path.Combine(work, name);
                    CreatePrivateDirectory(Path.GetDirectoryName(destination)!);
                    await WritePrivateAsync(destination, bytes, cancellationToken).ConfigureAwait(false);
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            await using (var interfaces = new PacketInterfaceManager([]))
            await using (var transport = new TransportRouter(interfaces))
            await using (var router = new LxmRouter(transport, importedIdentity, options: new LxmRouterOptions
            {
                StoragePath = work,
                EnableRatchets = before[ownRatchet] is not null,
            }))
            {
                // Public stores perform the real load/validation, including signed key ownership,
                // ticket expiry/grace, history expiry and announced stamp-cost policy.
                if (before[selected[0]] is not null) router.Tickets.Save();
            }
            File.Delete(Path.Combine(work, "lxmf", "available_tickets.lock"));
            if (before[ownRatchet] is not null) File.Delete(Path.Combine(work, ownRatchet) + ".lock");
            foreach (var name in selected)
            {
                var path = Path.Combine(work, name);
                if (!OperatingSystem.IsWindows() && File.Exists(path))
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            await VerifySnapshotAsync(source, tracked, before, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(Path.Combine(work, "lxmf"), target);
            return new SidebandRouterProfileResult(count, notices);
        }
        finally { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
    }

    private static void Validate(string name, byte[] bytes, Identity identity)
    {
        object? decoded = null;
        try
        {
            decoded = MsgPack.UnpackApplicationValue(bytes, Limits);
            var root = Map(decoded, 65_536);
            switch (Path.GetFileName(name))
            {
                case "available_tickets":
                    if (root.Count != 3 || root.Keys.Any(key => key is not ("outbound" or "inbound" or "last_deliveries")))
                        throw new InvalidDataException("Unsupported ticket store schema.");
                    foreach (var (key, value) in Map(root.GetValueOrDefault("outbound"), 4_096))
                    {
                        Hash(key, 16);
                        if (value is not object?[] { Length: 2 } entry || entry[1] is not byte[] { Length: 16 })
                            throw new InvalidDataException("Invalid outbound ticket.");
                        Number(entry[0]);
                    }
                    foreach (var (key, value) in Map(root.GetValueOrDefault("inbound"), 4_096))
                    {
                        Hash(key, 16);
                        foreach (var (token, expiry) in Map(value, 16))
                        {
                            Hash(token, 16);
                            if (expiry is not object?[] { Length: 1 } entry) throw new InvalidDataException("Invalid inbound ticket.");
                            Number(entry[0]);
                        }
                    }
                    foreach (var (key, value) in Map(root.GetValueOrDefault("last_deliveries"), 4_096)) { Hash(key, 16); Number(value); }
                    break;
                case "outbound_stamp_costs":
                    foreach (var (key, value) in root)
                    {
                        Hash(key, 16);
                        if (value is not object?[] { Length: 2 } entry ||
                            entry[1] is not null && entry[1] is not (long and >= 1 and <= 254) and not (ulong and >= 1 and <= 254))
                            throw new InvalidDataException("Invalid outbound stamp-cost entry.");
                        Number(entry[0]);
                    }
                    break;
                case "local_deliveries":
                case "locally_processed":
                    foreach (var (key, value) in root) { Hash(key, 32); Number(value); }
                    break;
                default:
                    if (root.Count != 2 || !root.ContainsKey("signature") || !root.ContainsKey("ratchets"))
                        throw new InvalidDataException("Unsupported signed ratchet envelope.");
                    var keys = SignedDestinationRatchetFile.Decode(identity, bytes);
                    try
                    {
                        if (keys.Count > DestinationRatchets.DefaultRetained)
                            throw new InvalidDataException("The source exceeds the native ratchet retention bound.");
                    }
                    finally { foreach (var key in keys) CryptographicOperations.ZeroMemory(key); }
                    break;
            }
        }
        catch (FormatException error) { throw new InvalidDataException("Malformed router persistence file.", error); }
        finally { Zero(decoded); }
    }

    private static Dictionary<object, object?> Map(object? value, int maximum) =>
        value is Dictionary<object, object?> map && map.Count <= maximum ? map : throw new InvalidDataException("Invalid or oversized router map.");
    private static void Hash(object? value, int length)
    {
        if (value is not byte[] bytes || bytes.Length != length) throw new InvalidDataException("Invalid router hash or ticket length.");
    }
    private static void Number(object? value)
    {
        if (value is not (long or ulong or float or double) || !double.IsFinite(MsgPack.ToDouble(value)))
            throw new InvalidDataException("Invalid router persistence timestamp.");
    }
    private static void Zero(object? value)
    {
        if (value is byte[] bytes) CryptographicOperations.ZeroMemory(bytes);
        else if (value is object?[] array) foreach (var item in array) Zero(item);
        else if (value is Dictionary<object, object?> map) foreach (var pair in map) { Zero(pair.Key); Zero(pair.Value); }
    }
    private static int Maximum(string name) => name == "primary_identity" ? 64 :
        Path.GetFileName(name) == "available_tickets" || name.EndsWith(".ratchets", StringComparison.Ordinal) ? SmallLimit : CacheLimit;
    private static async Task<Dictionary<string, string?>> SnapshotAsync(string root, string[] paths, CancellationToken token)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var relative in paths)
        {
            var current = root;
            foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
            {
                current = Path.Combine(current, segment);
                if (Path.Exists(current)) RejectLink(current);
                if (current != Path.Combine(root, relative) && File.Exists(current))
                    throw new InvalidDataException("A router storage directory is not a directory.");
            }
            if (Directory.Exists(current)) throw new InvalidDataException("A selected router file is a directory.");
            if (!File.Exists(current)) { result[relative] = null; continue; }
            var bytes = await ReadAsync(current, Maximum(relative), token).ConfigureAwait(false);
            try { result[relative] = Convert.ToHexStringLower(SHA256.HashData(bytes)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        return result;
    }
    private static async Task VerifySnapshotAsync(string root, string[] paths, Dictionary<string, string?> expected, CancellationToken token)
    {
        var after = await SnapshotAsync(root, paths, token).ConfigureAwait(false);
        if (expected.Any(pair => after[pair.Key] != pair.Value))
            throw new IOException("The router source changed during migration. Stop upstream Sideband and retry.");
    }
    private static async Task<byte[]> ReadAsync(string path, int maximum, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16_384, FileOptions.Asynchronous);
        if (stream.Length > maximum) throw new InvalidDataException("A router file exceeds its byte safety limit.");
        var bytes = new byte[(int)stream.Length];
        try
        {
            await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            if (await stream.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0) throw new IOException("A router source file changed while being read.");
            return bytes;
        }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
    }
    private static async Task WritePrivateAsync(string path, byte[] bytes, CancellationToken token)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(path, options);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
        stream.Flush(true);
    }
    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Router migration does not follow symbolic links or reparse points.");
    }
}
