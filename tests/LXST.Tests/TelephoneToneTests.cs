using System.Text.Json;

namespace LXST.Tests;

[Trait("Category", "Unit")]
public sealed class TelephoneToneTests
{
    [Fact]
    public void RingbackAndBusyCadenceMatchExecutedActualPrimaryJobs()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxst-telephone-tones.json")));
        foreach (var (kind, property) in new[] { (TelephoneTone.Ringback, "ringback"), (TelephoneTone.Busy, "busy") })
            foreach (var vector in fixture.RootElement.GetProperty(property).EnumerateArray())
                Assert.True(vector.GetProperty("audible").GetBoolean() ==
                    TelephoneToneSource.IsAudible(kind, TimeSpan.FromTicks((long)Math.Round(vector.GetProperty("elapsed").GetDouble() * TimeSpan.TicksPerSecond))),
                    $"{kind} at {vector.GetProperty("elapsed").GetDouble()}");
        foreach (var example in fixture.RootElement.GetProperty("timing").EnumerateArray())
            foreach (var observation in example.GetProperty("observations").EnumerateArray())
                Assert.True(observation.GetProperty("audible").GetBoolean() ==
                    TelephoneToneSource.IsAudible(example.GetProperty("kind").GetString() == "ringback" ?
                        TelephoneTone.Ringback : TelephoneTone.Busy,
                        TimeSpan.FromTicks((long)Math.Round(observation.GetProperty("elapsed").GetDouble() * TimeSpan.TicksPerSecond)),
                        TimeSpan.FromTicks((long)Math.Round(example.GetProperty("busy_duration").GetDouble() * TimeSpan.TicksPerSecond))),
                    $"{example.GetProperty("kind").GetString()} at {observation.GetProperty("elapsed").GetDouble()}");
    }

    [Trait("Category", "Integration")]
    public sealed class TelephoneToneLifecycleTests
    {
        [Fact]
        public async Task ToneEnumerationHonorsCancellationAndZeroBusyDurationProducesNoAudio()
        {
            using var stop = new CancellationTokenSource();
            await using var ringback = new TelephoneToneSource(TelephoneTone.Ringback).CaptureAsync(stop.Token).GetAsyncEnumerator();
            Assert.True(await ringback.MoveNextAsync());
            Assert.All(ringback.Current.Samples.ToArray(), sample => Assert.Equal(0, sample));
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await ringback.MoveNextAsync());
            await using var silent = new TelephoneToneSource(TelephoneTone.Busy, TimeSpan.Zero).CaptureAsync().GetAsyncEnumerator();
            Assert.False(await silent.MoveNextAsync());
        }
    }
}
