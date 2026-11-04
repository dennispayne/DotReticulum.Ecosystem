namespace Sideband;

public sealed record SidebandDiscoveredInterface(
    string DiscoveryHash, string InterfaceType, string Name, bool Transport,
    string NetworkIdentityHash, string TransportIdentityHash,
    SidebandGeoCoordinate? Location, string Status, int StampValue, int Hops,
    double LastHeardUtcSeconds, string? ReachableOn, int? Port);

public sealed record SidebandMapDiscoverySnapshot(IReadOnlyList<SidebandDiscoveredInterface> Interfaces,
    int ScanCandidateLimit, int RecordLimit);

public static class SidebandMapDiscovery
{
    public const int MaximumRecords = 256;

    public static IReadOnlyList<SidebandMapOverlayMarker> Project(
        IReadOnlyList<SidebandDiscoveredInterface> interfaces)
    {
        ArgumentNullException.ThrowIfNull(interfaces);
        if (interfaces.Count > MaximumRecords)
            throw new ArgumentException($"At most {MaximumRecords} discovery records may be projected.", nameof(interfaces));
        var result = new List<SidebandMapOverlayMarker>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in interfaces)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (item.DiscoveryHash.Length != 64 || !item.DiscoveryHash.All(Uri.IsHexDigit))
                throw new ArgumentException("Discovery hashes must contain 64 hexadecimal characters.", nameof(interfaces));
            if (!item.Transport || item.Location is not { } coordinate) continue;
            if (!double.IsFinite(coordinate.Latitude) || coordinate.Latitude is < -90 or > 90 ||
                !double.IsFinite(coordinate.Longitude) || coordinate.Longitude is < -180 or > 180 ||
                !double.IsFinite(coordinate.Altitude))
                throw new ArgumentException("Discovery coordinates must be finite geographic coordinates.", nameof(interfaces));
            var hash = item.DiscoveryHash.ToLowerInvariant();
            if (!seen.Add(hash))
                throw new ArgumentException("Discovery hashes must be unique.", nameof(interfaces));
            result.Add(new("discovery:" + hash, hash, coordinate,
                SidebandMapOverlays.InterfaceAppearance(item.InterfaceType),
                SidebandMapOverlayKind.Discovery, Name: item.Name));
        }
        return result;
    }
}
