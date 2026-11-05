using System.Text.Json;

namespace NomadNet.Tests;

[Trait("Category", "Unit")]
public sealed class NomadNetPeerControlTests
{
    [Fact]
    [Trait("TestId", "T1050")]
    public void StrictSynchronizationGraceMatchesExecutedPrimaryCallbacks()
    {
        // Actual Network.py callbacks at f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2.
        // SHA-256 7566c413f1d7b298dec6c764e0e8a14f7f7d6c2ec7825e93c595db2137063949.
        // GeneratePeerControlVectors.py discloses the synthetic selection/clock/thread boundary.
        using var fixture = JsonDocument.Parse(NomadNetPeerControlVectors.Json);
        Assert.Equal("f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2", fixture.RootElement.GetProperty("revision").GetString());
        Assert.Equal("7566c413f1d7b298dec6c764e0e8a14f7f7d6c2ec7825e93c595db2137063949",
            fixture.RootElement.GetProperty("sourceSha256").GetString());
        var cases = fixture.RootElement.GetProperty("synchronization").EnumerateArray().ToArray();
        Assert.Equal(7, cases.Length);
        foreach (var item in cases)
        {
            if (!item.GetProperty("selected").GetBoolean() || !item.GetProperty("exists").GetBoolean())
            {
                Assert.False(item.GetProperty("started").GetBoolean());
                continue;
            }
            var allowed = NomadNetPeerControls.CanSynchronize(item.GetProperty("now").GetDouble(),
                item.GetProperty("lastAttempt").GetDouble());
            Assert.Equal(item.GetProperty("started").GetBoolean(), allowed);
            if (allowed)
                Assert.Equal(item.GetProperty("now").GetDouble() - 1, item.GetProperty("nextAttempt").GetDouble());
        }
        var removals = fixture.RootElement.GetProperty("removal").EnumerateArray().ToArray();
        Assert.Equal(2, removals.Length);
        Assert.Empty(removals[0].GetProperty("events").EnumerateArray());
        Assert.Equal(["unpeer:00112233445566778899aabbccddeeff", "rebuild", "show"],
            removals[1].GetProperty("events").EnumerateArray().Select(value => value.GetString()));
    }

    [Theory]
    [Trait("TestId", "T1051")]
    [InlineData(double.NaN, 0)]
    [InlineData(0, double.NaN)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(0, double.NegativeInfinity)]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void InvalidTimestampsFailExplicitly(double now, double previous) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => NomadNetPeerControls.CanSynchronize(now, previous));
}
