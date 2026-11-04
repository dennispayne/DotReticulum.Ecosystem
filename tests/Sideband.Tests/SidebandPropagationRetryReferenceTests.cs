using System.Text.Json;
using LXMF;

namespace Sideband.Tests;

/// <summary>Actual primary d069122 message_notification; generator records source SHA and observer seams.</summary>
[Trait("Category", "Unit")]
public sealed class SidebandPropagationRetryReferenceTests
{
    [Fact]
    public void ActualPrimaryPolicyDistinguishesFailedRejectedCancelledAndStampFailure()
    {
        using var fixture = Load();
        var cases = fixture.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(24, cases.Length);
        foreach (var item in cases)
        {
            var retry = SidebandDeliveryRetry.ShouldRetry((LxmState)item.GetProperty("state").GetInt32(),
                item.GetProperty("eligible").GetBoolean(), item.GetProperty("stamp_failure").GetBoolean());
            Assert.Equal(retry, item.GetProperty("queued").GetArrayLength() == 1);
            if (!retry) continue;
            Assert.Equal((int)LxmMethod.Propagated, item.GetProperty("queued")[0].GetInt32());
            Assert.False(item.GetProperty("eligible_after").GetBoolean());
            Assert.Equal(0, item.GetProperty("attempts_after").GetInt32());
            Assert.False(item.GetProperty("next_attempt_present").GetBoolean());
            Assert.True(item.GetProperty("packed_cleared").GetBoolean());
            Assert.Empty(item.GetProperty("history").EnumerateArray());
        }
    }

    [Fact]
    public void ActualPrimaryInitiallyPropagatedFailureConsumesEligibilityExactlyOnce()
    {
        using var fixture = Load();
        var oneShot = fixture.RootElement.GetProperty("one_shot");
        Assert.Equal((int)LxmMethod.Propagated, Assert.Single(oneShot.GetProperty("queued").EnumerateArray()).GetInt32());
        Assert.False(oneShot.GetProperty("eligible_after").GetBoolean());
        Assert.Single(oneShot.GetProperty("history").EnumerateArray());
        Assert.False(SidebandDeliveryRetry.ShouldRetry(LxmState.Failed, false, false));
    }

    private static JsonDocument Load() => JsonDocument.Parse(File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-propagation-retry.json")));
}
