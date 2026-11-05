namespace NomadNet;

public static class NomadNetPeerControls
{
    public const double SynchronizationGraceSeconds = 10;

    public static bool CanSynchronize(double nowUtcSeconds, double lastAttemptUtcSeconds)
    {
        if (!double.IsFinite(nowUtcSeconds) || nowUtcSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(nowUtcSeconds));
        if (!double.IsFinite(lastAttemptUtcSeconds) || lastAttemptUtcSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(lastAttemptUtcSeconds));
        return nowUtcSeconds > lastAttemptUtcSeconds + SynchronizationGraceSeconds;
    }
}
