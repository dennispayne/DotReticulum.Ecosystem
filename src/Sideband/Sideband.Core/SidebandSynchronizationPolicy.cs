namespace Sideband;

public readonly record struct SidebandSynchronizationDecision(double? LastSync, bool Retrying, bool Attempted);

public static class SidebandSynchronizationPolicy
{
    public static SidebandSynchronizationDecision Advance(bool enabled, int intervalSeconds, double? lastSync,
        bool retrying, double now, bool requestAccepted)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intervalSeconds);
        if (!double.IsFinite(now) || lastSync is { } last && !double.IsFinite(last))
            throw new ArgumentOutOfRangeException(nameof(now));
        if (!enabled) return new(lastSync, retrying, false);
        if (lastSync is null) return new(now, retrying, false);
        if (now <= lastSync + intervalSeconds) return new(lastSync, retrying, false);
        if (requestAccepted || retrying) return new(now, false, true);
        return new(lastSync + 360, true, true);
    }
}
