using System.Text.Json;
using System.Threading.Channels;
using Rnphone;

namespace LXST.Tests;

[Trait("Category", "Unit")]
public sealed class CallProgressTests
{
    [Fact]
    public void RoundingFormattingAndDisplayIntervalsMatchExecutedPrimary()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "lxst-telephone-controls.json")));
        foreach (var row in fixture.RootElement.GetProperty("durations").EnumerateArray())
            Assert.Equal(row.GetProperty("text").GetString(),
                CallProgress.FormatElapsed(TimeSpan.FromSeconds(row.GetProperty("elapsed").GetDouble())));
        foreach (var row in fixture.RootElement.GetProperty("display").EnumerateArray())
            foreach (var sleep in row.GetProperty("sleeps").EnumerateArray())
                Assert.Equal(sleep.GetDouble(), CallProgress.UpdateInterval(row.GetProperty("has_display").GetBoolean()).TotalSeconds);
        Assert.Throws<ArgumentOutOfRangeException>(() => CallProgress.FormatElapsed(TimeSpan.FromTicks(-1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProgressUsesObservedRefreshCadenceAndCancellationStopsFurtherWrites(bool hasDisplay)
    {
        var clock = new ManualAudioClock();
        using var output = new Writer();
        using var stop = new CancellationTokenSource();
        var started = clock.GetUtcNow();
        var progress = CallProgress.DisplayAsync(() => clock.GetUtcNow() - started, output, stop.Token, clock, hasDisplay);
        Assert.Contains("0s", await output.Updates.Reader.ReadAsync());
        foreach (var expected in hasDisplay ? new[] { "1s", "2s", "3s", "4s" } : new[] { "0s", "0s", "1s", "1s" })
        {
            clock.Advance(CallProgress.UpdateInterval(hasDisplay));
            Assert.Contains(expected, await output.Updates.Reader.ReadAsync());
        }
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => progress);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(output.Updates.Reader.TryRead(out _));
    }

    private sealed class Writer : StringWriter
    {
        public Channel<string> Updates { get; } = Channel.CreateBounded<string>(16);
        public override Task WriteAsync(string? value)
        {
            Updates.Writer.TryWrite(value!);
            return base.WriteAsync(value);
        }
    }
}
