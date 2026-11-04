using System.Globalization;
using System.Text;

namespace Sideband;

public sealed record SidebandFixedLocation(string DeliveryHash, double Latitude, double Longitude);

public static class SidebandFixedLocations
{
    public const int MaximumLocations = 256;
    public const int MaximumFileBytes = 262_144;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static IReadOnlyList<SidebandFixedLocation> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaximumFileBytes || Utf8.GetByteCount(text) > MaximumFileBytes)
            throw new InvalidDataException("Fixed locations exceed the 256 KiB UTF-8 limit.");
        var locations = new Dictionary<string, SidebandFixedLocation>(StringComparer.Ordinal);
        using var input = new StringReader(text);
        var lineNumber = 0;
        while (input.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var fields = line.Replace(" ", "", StringComparison.Ordinal).Split(',');
            if (fields.Length != 3 || !SidebandContact.TryNormalizeDeliveryHash(fields[0], out var peer) ||
                !double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) ||
                !double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude) ||
                !double.IsFinite(latitude) || latitude is < -90 or > 90 ||
                !double.IsFinite(longitude) || longitude is < -180 or > 180)
                throw new InvalidDataException($"Invalid fixed location at line {lineNumber}; expected LXMF hash, latitude, longitude.");
            locations[peer] = new(peer, latitude, longitude);
            if (locations.Count > MaximumLocations)
                throw new InvalidDataException("Fixed locations exceed the 256 destination limit.");
        }
        return Array.AsReadOnly(locations.Values.OrderBy(location => location.DeliveryHash, StringComparer.Ordinal).ToArray());
    }

    public static async Task<IReadOnlyList<SidebandFixedLocation>> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length > MaximumFileBytes) throw new InvalidDataException("Fixed locations exceed the 256 KiB file limit.");
        var bytes = new byte[MaximumFileBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await input.ReadAsync(bytes.AsMemory(count), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        if (count > MaximumFileBytes) throw new InvalidDataException("Fixed locations exceed the 256 KiB file limit.");
        cancellationToken.ThrowIfCancellationRequested();
        return Parse(Utf8.GetString(bytes, 0, count));
    }

    internal static void Validate(IReadOnlyList<SidebandFixedLocation> locations)
    {
        if (locations.Count > MaximumLocations ||
            locations.Any(location => location is null ||
                !SidebandContact.TryNormalizeDeliveryHash(location.DeliveryHash, out var peer) || peer != location.DeliveryHash ||
                !double.IsFinite(location.Latitude) || location.Latitude is < -90 or > 90 ||
                !double.IsFinite(location.Longitude) || location.Longitude is < -180 or > 180) ||
            locations.Select(location => location.DeliveryHash).Distinct(StringComparer.Ordinal).Count() != locations.Count)
            throw new InvalidDataException("Saved fixed locations contain invalid, duplicate or excessive entries.");
    }
}
