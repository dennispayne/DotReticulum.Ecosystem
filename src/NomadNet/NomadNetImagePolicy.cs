namespace NomadNet;

public enum NomadNetImageLoading { Never, Manual, Auto, Always }

public sealed record NomadNetImageLinkMetrics(double? RttSeconds, double? ExpectedBitsPerSecond,
    long? ResponseTransferBytes, double? ResponseSeconds)
{
    public double? ResponseBitsPerSecond =>
        ResponseTransferBytes is > 0 && ResponseSeconds is double seconds && seconds > 0 && double.IsFinite(seconds)
            ? ResponseTransferBytes.Value / Math.Max(seconds, 0.0001) * 8 : null;
}

public static class NomadNetImagePolicy
{
    public static NomadNetImageLoading Parse(string value) => value.ToLowerInvariant() switch
    {
        "never" => NomadNetImageLoading.Never,
        "manual" => NomadNetImageLoading.Manual,
        "auto" => NomadNetImageLoading.Auto,
        "always" => NomadNetImageLoading.Always,
        _ => throw new InvalidDataException("Image loading must be never, manual, auto or always."),
    };

    public static bool ShouldLoad(NomadNetImageLoading mode, bool local, NomadNetImageLinkMetrics? metrics)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode is NomadNetImageLoading.Never or NomadNetImageLoading.Manual) return false;
        if (mode == NomadNetImageLoading.Always || local) return true;
        if (metrics?.RttSeconds is not (> 0 and < 1.5)) return false;
        var rate = metrics.ExpectedBitsPerSecond is null or 0
            ? metrics.ResponseBitsPerSecond : metrics.ExpectedBitsPerSecond;
        return rate is double measured && measured > 10000 && double.IsFinite(measured);
    }
}
