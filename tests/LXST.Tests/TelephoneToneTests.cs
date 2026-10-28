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
                Assert.Equal(vector.GetProperty("audible").GetBoolean(),
                    TelephoneToneSource.IsAudible(kind, TimeSpan.FromSeconds(vector.GetProperty("elapsed").GetDouble())));
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
