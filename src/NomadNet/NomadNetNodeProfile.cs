using System.Security.Cryptography;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;
using LXMF;

namespace NomadNet;

/// <summary>Activates primary node persistence only inside the private absent-target profile transaction.</summary>
internal static class NomadNetNodeProfile
{
    internal const int MaximumPeers = 4096;
    internal const int MaximumPeerBytes = 8 * 1024 * 1024;
    private const string Prefix = "storage/lxmf/";

    internal static async Task ImportAsync(string source, string staging, Identity identity,
        IReadOnlyDictionary<string, string> snapshot, NomadNetSettings settings, ISet<string> activated,
        ICollection<string> warnings, CancellationToken token)
    {
        try { await ImportCoreAsync(source, staging, identity, snapshot, settings, activated, warnings, token).ConfigureAwait(false); }
        catch (Exception error) when (error is FormatException or OverflowException or ArgumentException)
        { throw new InvalidDataException("Malformed or unsupported primary propagation node state.", error); }
    }

    private static async Task ImportCoreAsync(string source, string staging, Identity identity,
        IReadOnlyDictionary<string, string> snapshot, NomadNetSettings settings, ISet<string> activated,
        ICollection<string> warnings, CancellationToken token)
    {
        if (!settings.EnablePropagation)
        {
            warnings.Add("Propagation is disabled in imported configuration; node_stats, peers and messagestore are not activated.");
            return;
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        long storedBytes = 0;
        foreach (var relative in snapshot.Keys.Where(path => path.StartsWith(Prefix + "messagestore/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var name = relative[(Prefix + "messagestore/").Length..];
            if (name.EndsWith(".out", StringComparison.Ordinal) || name.Contains(".tmp.", StringComparison.Ordinal))
            {
                warnings.Add("Interrupted propagation store file not imported: " + relative);
                continue;
            }
            if (name.Contains('/'))
            {
                warnings.Add("Unknown nested propagation store file not imported: " + relative);
                continue;
            }
            var bytes = await ReadStableAsync(source, relative, NomadNetProfileImport.MaximumFileBytes, snapshot, token).ConfigureAwait(false);
            try
            {
                var entry = LxmPropagationNode.InspectPersistedMessage(name, bytes,
                    Math.Max(settings.PropagationTransferLimitBytes, settings.PropagationSyncLimitBytes),
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
                if (entry.Expired)
                    throw new InvalidDataException("Primary propagation store contains expired entries; clean it in the primary and retry. Nothing was silently evicted.");
                if (!ids.Add(Convert.ToHexStringLower(entry.TransientId)))
                    throw new InvalidDataException("Duplicate transient ID in primary propagation store.");
                storedBytes += bytes.Length;
                if (storedBytes > settings.PropagationStorageLimitBytes)
                    throw new InvalidDataException("Primary propagation store exceeds imported storage policy; nothing was silently evicted.");
                await WriteAsync(staging, relative, bytes, token).ConfigureAwait(false);
                activated.Add(relative);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        var peerCount = 0;
        if (snapshot.ContainsKey(Prefix + "peers"))
        {
            var bytes = await ReadStableAsync(source, Prefix + "peers", MaximumPeerBytes, snapshot, token).ConfigureAwait(false);
            try
            {
                if (MsgPack.UnpackApplicationValue(bytes, new MsgPackLimits
                {
                    MaxEncodedBytes = MaximumPeerBytes, MaxValueBytes = LxmPeer.MaximumPersistedBytes,
                    MaxContainerEntries = MaximumPeers, MaxValues = MaximumPeers + 1, MaxDepth = 2,
                }) is not object?[] peers || peers.Length > MaximumPeers)
                    throw new InvalidDataException("Invalid or oversized primary node peer array.");
                var destinations = new HashSet<string>(StringComparer.Ordinal);
                foreach (var value in peers)
                {
                    token.ThrowIfCancellationRequested();
                    if (value is not byte[] encoded)
                        throw new InvalidDataException("Primary node peer is not a binary serialized peer record.");
                    var peer = LxmPeer.FromBytes(encoded, id => ids.Contains(Convert.ToHexStringLower(id)) ? true :
                        throw new InvalidDataException("Primary peer refers to an absent propagation message; nothing was silently removed."));
                    if (!destinations.Add(Convert.ToHexStringLower(peer.DestinationHash)))
                        throw new InvalidDataException("Duplicate primary propagation peer.");
                    peerCount++;
                }
                await WriteAsync(staging, Prefix + "peers", bytes, token).ConfigureAwait(false);
                activated.Add(Prefix + "peers");
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        if (snapshot.ContainsKey(Prefix + "node_stats"))
        {
            var bytes = await ReadStableAsync(source, Prefix + "node_stats", 4096, snapshot, token).ConfigureAwait(false);
            try
            {
                LxmPropagationNode.ValidatePersistedStatistics(bytes);
                await WriteAsync(staging, Prefix + "node_stats", bytes, token).ConfigureAwait(false);
                activated.Add(Prefix + "node_stats");
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        await using (var interfaces = new PacketInterfaceManager([]))
        await using (var transport = new TransportRouter(interfaces))
        await using (var node = new LxmPropagationNode(transport, identity, Path.Combine(staging, "storage"),
            NomadNetNodeConfiguration.CreateOptions(settings)))
        {
            if (node.MessageCount != ids.Count || node.StoredBytes != storedBytes ||
                node.UnresolvedPeers.Count != peerCount || node.Peers.Count != settings.PropagationStaticPeers
                    .Distinct(StringComparer.Ordinal).Count(hash => !node.UnresolvedPeers.Any(peer => Convert.ToHexStringLower(peer.DestinationHash) == hash)))
                throw new InvalidDataException("Public LXMF node preflight did not preserve the selected persisted node state.");
        }
        foreach (var relative in activated.Where(path => path.StartsWith(Prefix, StringComparison.Ordinal)))
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)),
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
        token.ThrowIfCancellationRequested();
        warnings.Add($"Activated {ids.Count} stamped propagation messages ({storedBytes} bytes), {peerCount} unresolved persisted peers and available node counters using shared LXMF APIs. Public peer identities are not imported; unresolved peers survive restart but are never scheduled or marked synchronized until a matching identity is genuinely discovered.");
        warnings.Add("Node migration bounds: 4096 peers/8 MiB peer array, 4096-byte complete nonnegative counters, existing 20 MiB per file/512 MiB whole-profile bounds and configured sync/storage quotas. Expired, stale-index, duplicate, malformed and stamp/filename mismatch records fail atomically rather than being repaired or evicted. Historical stamp values are verified, not retroactively charged at today's admission cost; opaque encrypted content does not authenticate message authorship.");
    }

    private static async Task<byte[]> ReadStableAsync(string source, string relative, int maximum,
        IReadOnlyDictionary<string, string> snapshot, CancellationToken token)
    {
        var bytes = await NomadNetProfileImport.ReadAsync(source, relative, maximum, token).ConfigureAwait(false);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) == snapshot[relative]) return bytes;
        CryptographicOperations.ZeroMemory(bytes);
        throw new IOException("Primary node state changed during import; stop the primary and retry.");
    }

    private static async Task WriteAsync(string staging, string relative, byte[] bytes, CancellationToken token)
    {
        var path = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
            Share = FileShare.None, Options = FileOptions.Asynchronous | FileOptions.WriteThrough };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var stream = new FileStream(path, options);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
        stream.Flush(true);
    }
}
