namespace Sideband;

public static class SidebandPropagationPolicy
{
    public static bool ShouldSelect(bool manuallyConfigured, int? previousHops, int candidateHops,
        long emitted, double now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(candidateHops);
        if (previousHops is < 0) throw new ArgumentOutOfRangeException(nameof(previousHops));
        if (!double.IsFinite(now)) throw new ArgumentOutOfRangeException(nameof(now));
        return !manuallyConfigured && emitted - now <= 300 &&
            (previousHops is null || candidateHops <= previousHops);
    }
}
