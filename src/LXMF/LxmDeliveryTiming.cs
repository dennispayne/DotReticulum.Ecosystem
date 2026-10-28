namespace LXMF;

internal readonly record struct LxmAttemptSchedule(double NextAttempt, int AttemptLimit, double? ReceiptTimeout);

internal static class LxmDeliveryTiming
{
    internal static LxmAttemptSchedule Schedule(double now, double roundTrip, bool pathRequest, int attempts,
        double jitterFraction, int maximumAttempts = 5, double retryWait = 10, double pathWait = 7,
        double processingInterval = 4)
    {
        var limit = roundTrip > retryWait
            ? Math.Max(3, (int)Math.Round(maximumAttempts * retryWait / roundTrip, MidpointRounding.ToEven))
            : maximumAttempts;
        var next = now + Math.Max(pathRequest ? pathWait : retryWait, roundTrip);
        var mtuTime = Math.Max(roundTrip - 6, 0) / 2;
        double? receiptTimeout = null;
        if (mtuTime > 0.5)
        {
            next += jitterFraction * mtuTime;
            receiptTimeout = (Math.Max(retryWait, roundTrip) + mtuTime) *
                Math.Max(1, limit - attempts + 1) + processingInterval;
        }
        return new(next, limit, receiptTimeout);
    }

    internal static bool AttemptDue(double now, double nextAttempt, bool awaitingPath, bool hasPath) =>
        now > nextAttempt || awaitingPath && hasPath;

    internal static bool SlowPathInFlight(LxMessage message) =>
        message.SentOnSlowPath && (message.Method == LxmMethod.Opportunistic
            ? message.State == LxmState.Sent : message.State == LxmState.Sending);

    internal static bool Held(LxMessage message, ReadOnlySpan<byte> target, IEnumerable<LxMessage> outbound,
        Func<LxMessage, byte[]> selectedTarget)
    {
        if (message.State != LxmState.Outbound) return false;
        var ahead = true;
        foreach (var other in outbound)
        {
            if (ReferenceEquals(message, other)) { ahead = false; continue; }
            if (other.State is LxmState.Delivered or LxmState.Failed or LxmState.Cancelled or LxmState.Rejected ||
                other.Method == LxmMethod.Propagated && other.State == LxmState.Sent) continue;
            if (ahead && selectedTarget(other).AsSpan().SequenceEqual(target) || SlowPathInFlight(other)) return true;
        }
        return false;
    }
}
