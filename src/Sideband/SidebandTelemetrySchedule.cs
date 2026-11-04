namespace Sideband;

public sealed record SidebandTelemetryScheduleDecision(bool Send, bool Request, int SendAttempts, double SendBlockedUntil);

/// <summary>Primary collector-slot rules. Native callers additionally suppress duplicate in-flight work.</summary>
public static class SidebandTelemetrySchedule
{
    public static SidebandTelemetryScheduleDecision Evaluate(double now, bool sendEnabled, bool requestEnabled,
        int sendInterval, int requestInterval, double lastSendSuccess, double lastRequestSuccess,
        int sendAttempts, double sendBlockedUntil)
    {
        if (!double.IsFinite(now) || !double.IsFinite(lastSendSuccess) || !double.IsFinite(lastRequestSuccess) ||
            !double.IsFinite(sendBlockedUntil) || sendInterval <= 0 || requestInterval <= 0 || sendAttempts is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(now), "Invalid telemetry schedule state.");
        var send = false;
        if (sendEnabled && now > lastSendSuccess + sendInterval && now > sendBlockedUntil)
        {
            if (sendAttempts < 2) { send = true; sendAttempts++; }
            else { sendAttempts = 0; sendBlockedUntil = now + sendInterval; }
        }
        return new(send, requestEnabled && now > lastRequestSuccess + requestInterval, sendAttempts, sendBlockedUntil);
    }
}
