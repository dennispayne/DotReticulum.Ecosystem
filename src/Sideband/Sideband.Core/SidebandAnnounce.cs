using System.Text;
using LXMF;

namespace Sideband;

public sealed record SidebandAnnounceSignal(double? Rssi = null, double? Snr = null, double? Quality = null);

public sealed record SidebandAnnounce(
    string DestinationHash,
    string DestinationType,
    DateTimeOffset ReceivedAt,
    string? Name = null,
    int? StampCost = null,
    string? NodeVersion = null,
    bool? NodeEnabled = null,
    SidebandAnnounceSignal? Signal = null)
{
    public const string Delivery = "lxmf.delivery";
    public const string Propagation = "lxmf.propagation";
    public const int MaximumHistory = 48;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static SidebandAnnounce FromAppData(string destinationHash, string destinationType,
        DateTimeOffset receivedAt, byte[]? appData, int? cachedStampCost = null)
    {
        if (!SidebandContact.TryNormalizeDeliveryHash(destinationHash, out var hash))
            throw new ArgumentException("An announce address must contain 32 hexadecimal characters.", nameof(destinationHash));
        if (appData is { Length: > 4_096 })
            throw new InvalidDataException("Announce application data exceeds the 4 KiB safety limit.");
        SidebandAnnounce result;
        if (destinationType == Delivery)
            result = new(hash, destinationType, receivedAt, LxmfAppData.DisplayName(appData), cachedStampCost);
        else if (destinationType == Propagation)
        {
            if (!LxmfPropagationAnnounce.TryParse(appData, out var node) || node is null)
                throw new InvalidDataException("Malformed propagation-node announce.");
            var name = node.Metadata.GetValueOrDefault(1L) is byte[] bytes ? StrictUtf8.GetString(bytes) : null;
            var implementation = node.Metadata.GetValueOrDefault(254L) as string;
            var version = node.Metadata.GetValueOrDefault(0L) as string;
            var combined = string.Join(" ", new[] { implementation, version }.Where(value => !string.IsNullOrEmpty(value)));
            result = new(hash, destinationType, receivedAt, name, node.StampCost,
                combined.Length == 0 ? null : combined, node.Enabled);
        }
        else throw new ArgumentException("Unsupported announce destination type.", nameof(destinationType));
        Validate(result);
        return result;
    }

    public static IReadOnlyList<SidebandAnnounce> Observe(IReadOnlyList<SidebandAnnounce> history, SidebandAnnounce announce)
    {
        ValidateHistory(history);
        Validate(announce);
        return Array.AsReadOnly(history.Where(item => item.DestinationHash != announce.DestinationHash)
            .Append(announce).OrderBy(item => item.ReceivedAt).TakeLast(MaximumHistory).ToArray());
    }

    internal static void ValidateHistory(IReadOnlyList<SidebandAnnounce> history)
    {
        if (history.Count > MaximumHistory)
            throw new InvalidDataException("Announce history exceeds the 48-entry safety limit.");
        foreach (var item in history) Validate(item);
        if (history.Select(item => item.DestinationHash).Distinct(StringComparer.Ordinal).Count() != history.Count)
            throw new InvalidDataException("Duplicate announce sources in saved history.");
        if (history.Zip(history.Skip(1)).Any(pair => pair.First.ReceivedAt > pair.Second.ReceivedAt))
            throw new InvalidDataException("Saved announce history is not ordered by receipt time.");
    }

    internal static void Validate(SidebandAnnounce announce)
    {
        if (announce is null || !SidebandContact.TryNormalizeDeliveryHash(announce.DestinationHash, out var hash) ||
            hash != announce.DestinationHash || announce.DestinationType is not (Delivery or Propagation))
            throw new InvalidDataException("Invalid saved announce source or destination type.");
        if (announce.StampCost is < 0 or > 256 ||
            announce.DestinationType == Delivery && (announce.StampCost is 0 or > 254 ||
                announce.NodeVersion is not null || announce.NodeEnabled is not null))
            throw new InvalidDataException("Invalid saved announce cost or node metadata.");
        ValidateText(announce.Name);
        ValidateText(announce.NodeVersion);
        if (announce.Signal is { } signal &&
            (new[] { signal.Rssi, signal.Snr, signal.Quality }.Any(value => value.HasValue && !double.IsFinite(value.Value)) ||
                signal.Quality is < 0 or > 100))
            throw new InvalidDataException("Invalid announce physical-layer measurements.");
    }

    private static void ValidateText(string? text)
    {
        if (text is null) return;
        try
        {
            if (StrictUtf8.GetByteCount(text) > 1_024)
                throw new InvalidDataException("Announce text exceeds the 1 KiB safety limit.");
        }
        catch (EncoderFallbackException ex) { throw new InvalidDataException("Invalid announce Unicode text.", ex); }
    }
}
