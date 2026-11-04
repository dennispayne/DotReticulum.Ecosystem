using System.Text.Json;

namespace Sideband.Tests;

// Actual primary _periodic_jobs observations at d069122af14880e43607aed0b71add4c7806fbe0.
// core.py SHA-256 8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25.
[Trait("Category", "Integration")]
public sealed class SidebandTelemetryScheduleTests
{
    [Fact]
    public async Task CollectorSlotsMatchEightExecutedPrimaryObservations()
    {
        using var fixture = JsonDocument.Parse(await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-telemetry-schedule.json"), TestContext.Current.CancellationToken));
        foreach (var vector in fixture.RootElement.GetProperty("vectors").EnumerateArray())
        {
            double N(string name) => vector.GetProperty(name).GetDouble();
            bool B(string name) => vector.GetProperty(name).GetBoolean();
            var decision = SidebandTelemetrySchedule.Evaluate(N("now"), B("send_enabled"), B("request_enabled"),
                60, 60, N("last_send_success"), N("last_request_success"), (int)N("attempts"), N("blocked_until"));
            Assert.Equal(B("send"), decision.Send);
            Assert.Equal(B("request"), decision.Request);
            Assert.Equal(N("next_attempts"), decision.SendAttempts);
            Assert.Equal(N("next_blocked_until"), decision.SendBlockedUntil);
            Assert.True(B("track"));
        }
    }

    [Fact]
    public void FailedSendSlotBlocksAndReopensWithoutLosingSuccessTimebase()
    {
        var exhausted = SidebandTelemetrySchedule.Evaluate(1000, true, true, 60, 60, 900, 999, 2, 0);
        Assert.False(exhausted.Send);
        Assert.False(exhausted.Request);
        Assert.Equal(1060, exhausted.SendBlockedUntil);
        Assert.False(SidebandTelemetrySchedule.Evaluate(1060, true, false, 60, 60, 900, 999, 0, 1060).Send);
        Assert.True(SidebandTelemetrySchedule.Evaluate(1061, true, false, 60, 60, 900, 999, 0, 1060).Send);
        Assert.Throws<ArgumentOutOfRangeException>(() => SidebandTelemetrySchedule.Evaluate(double.NaN, true, true, 60, 60, 0, 0, 0, 0));
    }
}
