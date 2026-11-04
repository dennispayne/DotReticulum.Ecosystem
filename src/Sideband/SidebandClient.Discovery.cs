using System.Security.Cryptography;
using DotReticulum.Applications.Runtime;

namespace Sideband;

public sealed partial class SidebandClient
{
    private readonly SemaphoreSlim _discoveryAdmission = new(1, 1);

    /// <summary>
    /// Inspects the selected connected configuration's cache, not a remote shared owner's cache.
    /// The public stack cache reader also prunes expired/unauthorized entries; no listener or autoconnect is started.
    /// </summary>
    public async Task<SidebandMapDiscoverySnapshot> GetMapDiscoveryAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!await _discoveryAdmission.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Another discovery cache refresh is pending.");
        try
        {
            using var owned = RuntimeOperationCancellation(cancellationToken);
            var token = owned.Token;
            await _lifecycle.WaitAsync(token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                var node = _node ?? throw new InvalidOperationException("Connect before inspecting the selected Reticulum discovery cache.");
                var value = await node.CallManagementAsync(
                    new Dictionary<object, object?> { ["get"] = "blackholed_identities" }, token).ConfigureAwait(false);
                if (value is not IEnumerable<KeyValuePair<object, object?>> entries)
                    throw new InvalidDataException("Reticulum returned an invalid blackhole snapshot.");
                var blocked = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in entries)
                {
                    if (entry.Key is not byte[] { Length: 16 } hash)
                        throw new InvalidDataException("Reticulum returned an invalid blackhole identity.");
                    blocked.Add(Convert.ToHexStringLower(hash));
                }
                var options = node.Config.Discovery with
                {
                    Enabled = false,
                    MaxAutoConnected = 0,
                    Capacity = Math.Min(node.Config.Discovery.Capacity, SidebandMapDiscovery.MaximumRecords),
                };
                return await Task.Run(async () =>
                {
                    token.ThrowIfCancellationRequested();
                    await using var cache = new DiscoveryService(Path.Combine(node.StorageDir, "discovery"), options,
                        isBlocked: hash => blocked.Contains(Convert.ToHexStringLower(hash.Span)),
                        diagnostic: message => Report("Discovery cache: " + message));
                    var records = await cache.SnapshotAsync(onlyTransport: true, cancellationToken: token).ConfigureAwait(false);
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        var interfaces = records.Select(record =>
                        {
                            SidebandGeoCoordinate? location = null;
                            if (record.Latitude is { } latitude && record.Longitude is { } longitude)
                            {
                                if (latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180)
                                    location = new(latitude, longitude, record.Height ?? 0);
                                else
                                    Report($"Discovery {record.DiscoveryHash} has out-of-range coordinates; no map marker was created.");
                            }
                            return new SidebandDiscoveredInterface(
                                record.DiscoveryHash, record.InterfaceType, record.Name, record.Transport,
                                record.NetworkId, record.TransportId, location,
                                record.Status, record.StampValue, record.Hops, record.LastHeard,
                                record.ReachableOn, record.Port);
                        }).ToArray();
                        return new SidebandMapDiscoverySnapshot(interfaces, options.Capacity * 2, options.Capacity);
                    }
                    finally
                    {
                        foreach (var record in records) CryptographicOperations.ZeroMemory(record.Packed);
                    }
                }, token).ConfigureAwait(false);
            }
            finally { _lifecycle.Release(); }
        }
        finally { _discoveryAdmission.Release(); }
    }
}
