using System.Security.Cryptography;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;
using LXMF;

namespace NomadNet;

/// <summary>Activates only primary client-router persistence inside the profile importer's private transaction.</summary>
internal static class NomadNetRouterProfile
{
    private const int TicketBytes = 4 * 1024 * 1024;
    private const int CacheBytes = 8 * 1024 * 1024;
    private static readonly MsgPackLimits Limits = new()
    {
        MaxEncodedBytes = CacheBytes, MaxValueBytes = CacheBytes,
        MaxContainerEntries = 65_536, MaxValues = 600_001, MaxDepth = 6,
    };

    internal static async Task ImportAsync(string source, string staging, Identity identity,
        IReadOnlyDictionary<string, string> snapshot, ISet<string> activated, ICollection<string> warnings,
        CancellationToken token)
    {
        var ratchets = "storage/lxmf/ratchets/" + Convert.ToHexStringLower(LxMessage.DeliveryDestinationHash(identity)) + ".ratchets";
        var selected = new[] { "storage/lxmf/available_tickets", "storage/lxmf/outbound_stamp_costs",
            "storage/lxmf/local_deliveries", "storage/lxmf/locally_processed", ratchets };
        foreach (var relative in selected)
        {
            token.ThrowIfCancellationRequested();
            if (!snapshot.TryGetValue(relative, out var digest)) continue;
            var maximum = relative == ratchets ? SignedDestinationRatchetFile.MaximumBytes :
                relative.EndsWith("/available_tickets", StringComparison.Ordinal) ? TicketBytes : CacheBytes;
            var bytes = await NomadNetProfileImport.ReadAsync(source, relative, maximum, token).ConfigureAwait(false);
            try
            {
                if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != digest)
                    throw new IOException("Primary router state changed during profile import; stop the primary process and retry.");
                if (relative == ratchets)
                {
                    var keys = SignedDestinationRatchetFile.Decode(identity, bytes);
                    try
                    {
                        if (keys.Count > DestinationRatchets.DefaultRetained)
                            throw new InvalidDataException("Own ratchet history exceeds the native retention limit; nothing was truncated.");
                    }
                    finally { foreach (var key in keys) CryptographicOperations.ZeroMemory(key); }
                }
                else Validate(Path.GetFileName(relative), bytes);
                var target = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                    Share = FileShare.None, Options = FileOptions.Asynchronous | FileOptions.WriteThrough };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using var stream = new FileStream(target, options);
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(true);
                activated.Add(relative);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        if (selected.Any(activated.Contains))
        {
            // Exercise the same public stores that the application will load, without starting
            // transport, workers or callbacks. Expiry policy remains LXMF's responsibility.
            await using var interfaces = new PacketInterfaceManager([]);
            await using var transport = new TransportRouter(interfaces);
            await using var router = new LxmRouter(transport, identity, options: new LxmRouterOptions
            {
                StoragePath = Path.Combine(staging, "storage"),
                EnableRatchets = activated.Contains(ratchets),
            });
            if (activated.Contains(selected[0])) router.Tickets.Save();
        }
        if (!OperatingSystem.IsWindows())
            foreach (var relative in selected.Where(activated.Contains))
                File.SetUnixFileMode(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)),
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
        token.ThrowIfCancellationRequested();
        warnings.Add("Activated only primary storage/lxmf client tickets, outbound stamp costs, delivered/processed duplicate histories and this identity's signed delivery ratchets when present; shared LXMF expiry/grace rules apply on load.");
        warnings.Add("Client queues/links are memory-only and are not resumed. node_stats is written by a primary client but reloaded only when enabling propagation; node_stats/peers/messagestore are handled separately only when imported configuration enables propagation. Other identities' ratchets, interrupted writes, unknown files and RNS global caches/interfaces are not imported.");
        warnings.Add("Router bounds: 4 MiB tickets, 8 MiB each cache, 4096 destinations per ticket map, 16 inbound tickets per destination, 65536 cache entries and 512 own signed ratchets. Malformed or unsupported stores fail the entire profile transaction, never silently reset, truncate or replace the identity.");
    }

    private static void Validate(string name, byte[] bytes)
    {
        object? decoded = null;
        try
        {
            decoded = MsgPack.UnpackApplicationValue(bytes, Limits);
            var map = Map(decoded, 65_536);
            switch (name)
            {
                case "available_tickets":
                    if (map.Count != 3 || !map.ContainsKey("outbound") || !map.ContainsKey("inbound") || !map.ContainsKey("last_deliveries"))
                        throw new InvalidDataException("Unsupported primary ticket schema.");
                    foreach (var (peer, value) in Map(map["outbound"], 4096))
                    {
                        Hash(peer, 16);
                        var entry = Array(value, 2);
                        Timestamp(entry[0]);
                        Hash(entry[1], 16);
                    }
                    foreach (var (peer, value) in Map(map["inbound"], 4096))
                    {
                        Hash(peer, 16);
                        foreach (var (ticket, expiry) in Map(value, 16))
                        {
                            Hash(ticket, 16);
                            Timestamp(Array(expiry, 1)[0]);
                        }
                    }
                    foreach (var (peer, timestamp) in Map(map["last_deliveries"], 4096))
                    { Hash(peer, 16); Timestamp(timestamp); }
                    break;
                case "outbound_stamp_costs":
                    foreach (var (peer, value) in map)
                    {
                        Hash(peer, 16);
                        var entry = Array(value, 2);
                        Timestamp(entry[0]);
                        if (entry[1] is not null && entry[1] is not (long and >= 1 and <= 254) and not (ulong and >= 1 and <= 254))
                            throw new InvalidDataException("Invalid primary stamp cost.");
                    }
                    break;
                default:
                    foreach (var (hash, timestamp) in map) { Hash(hash, 32); Timestamp(timestamp); }
                    break;
            }
        }
        catch (FormatException error) { throw new InvalidDataException("Malformed primary router persistence.", error); }
        finally { Zero(decoded); }
    }

    private static Dictionary<object, object?> Map(object? value, int maximum) =>
        value is Dictionary<object, object?> map && map.Count <= maximum ? map :
            throw new InvalidDataException("Invalid or oversized primary router map.");
    private static object?[] Array(object? value, int length) =>
        value is object?[] array && array.Length == length ? array :
            throw new InvalidDataException("Invalid primary router entry.");
    private static void Hash(object? value, int length)
    {
        if (value is not byte[] hash || hash.Length != length)
            throw new InvalidDataException("Invalid primary router hash or ticket length.");
    }
    private static void Timestamp(object? value)
    {
        if (value is not (long or ulong or float or double) || !double.IsFinite(MsgPack.ToDouble(value)))
            throw new InvalidDataException("Invalid primary router timestamp.");
    }
    private static void Zero(object? value)
    {
        if (value is byte[] bytes) CryptographicOperations.ZeroMemory(bytes);
        else if (value is object?[] array) foreach (var item in array) Zero(item);
        else if (value is Dictionary<object, object?> map)
            foreach (var (key, item) in map) { Zero(key); Zero(item); }
    }
}
