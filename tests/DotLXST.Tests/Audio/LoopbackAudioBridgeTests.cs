using System.Diagnostics;
using System.Text.Json;
using DotLXST.Audio;

namespace DotLXST.Tests.Audio;

[Trait("Category", "Unit")]
public sealed class LoopbackAudioBridgeTests
{
    private static readonly JsonDocument Vectors = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxst-audio-processing.json")));

    [Fact]
    public async Task LoopbackBuffersAtMost128FramesAndKeepsNewestFrames()
    {
        await using var loopback = new LoopbackAudioBridge(TimeSpan.FromMilliseconds(10));
        loopback.Start();
        for (var i = 0; i < LoopbackAudioBridge.MaximumBufferedFrames + 2; i++)
        {
            Assert.True(loopback.TryWrite(new AudioFrame(new[] { (float)i }, 8_000, 1)));
        }

        Assert.Equal(LoopbackAudioBridge.MaximumBufferedFrames, loopback.BufferedFrames);
        Assert.False(loopback.CanReceive);

        await using var frames = loopback.CaptureAsync().GetAsyncEnumerator();
        Assert.True(await frames.MoveNextAsync());

        Assert.Equal(2f, frames.Current.Samples.Span[0]);
        Assert.Equal(LoopbackAudioBridge.MaximumBufferedFrames - 1, loopback.BufferedFrames);
        await loopback.StopAsync();
        Assert.False(loopback.IsRunning);
    }

    [Fact]
    public async Task LoopbackPacesFramesAndStopsAnActiveReader()
    {
        var interval = TimeSpan.FromMilliseconds(30);
        await using var loopback = new LoopbackAudioBridge(interval);
        loopback.Start();
        Assert.True(loopback.TryWrite(new AudioFrame(new float[] { 0.25f }, 8_000, 1)));
        await using var frames = loopback.CaptureAsync().GetAsyncEnumerator();
        var timer = Stopwatch.StartNew();

        Assert.True(await frames.MoveNextAsync());
        timer.Stop();
        Assert.True(timer.Elapsed >= TimeSpan.FromMilliseconds(10));
        Assert.Equal(0.25f, frames.Current.Samples.Span[0]);

        var pendingRead = frames.MoveNextAsync().AsTask();
        await loopback.StopAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pendingRead);
        Assert.False(loopback.IsRunning);
    }

    [Fact]
    public async Task LoopbackSupportsRestartCancellationAndDisposalRaces()
    {
        var loopback = new LoopbackAudioBridge(TimeSpan.FromMilliseconds(5));
        loopback.Start();
        using var cancellation = new CancellationTokenSource();
        await using (var frames = loopback.CaptureAsync(cancellation.Token).GetAsyncEnumerator())
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await frames.MoveNextAsync());
        }

        Assert.False(loopback.IsRunning);
        loopback.Start();
        Assert.True(loopback.TryWrite(new AudioFrame(new[] { 1f }, 8_000, 1)));
        await using var restartedFrames = loopback.CaptureAsync().GetAsyncEnumerator();
        Assert.True(await restartedFrames.MoveNextAsync());
        Assert.Equal(1f, restartedFrames.Current.Samples.Span[0]);

        var stop1 = loopback.StopAsync();
        var stop2 = loopback.StopAsync();
        var dispose1 = loopback.DisposeAsync();
        var dispose2 = loopback.DisposeAsync();
        await Task.WhenAll(stop1.AsTask(), stop2.AsTask(), dispose1.AsTask(), dispose2.AsTask());
        Assert.False(loopback.IsRunning);
        Assert.Throws<ObjectDisposedException>(() => loopback.Start());
    }

    [Fact]
    public async Task LoopbackRejectsInvalidLifecycleAndMalformedFrames()
    {
        await using var loopback = new LoopbackAudioBridge(TimeSpan.FromMilliseconds(5));
        Assert.False(loopback.TryWrite(new AudioFrame(new[] { 0f }, 8_000, 1)));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await loopback.WriteAsync(new AudioFrame(new[] { 0f }, 8_000, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoopbackAudioBridge(TimeSpan.Zero));

        loopback.Start();
        Assert.Throws<ArgumentException>(() =>
            loopback.TryWrite(new AudioFrame(new[] { float.NaN }, 8_000, 1)));
        Assert.Throws<ArgumentException>(() =>
            loopback.TryWrite(new AudioFrame(new[] { float.PositiveInfinity }, 8_000, 1)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await loopback.WriteAsync(new AudioFrame(new[] { 0f }, 8_000, 1), cancellation.Token));
    }

    [Fact]
    public void NullCodecPreservesReferenceIdentityWithoutAStreamDiscriminator()
    {
        var source = Vectors.RootElement.GetProperty("nullCodec");
        var codec = new NullAudioCodec();
        var marker = new object();

        var encoded = codec.Encode(marker);
        var decoded = codec.Decode(encoded);

        Assert.True(source.GetProperty("encodeReturnsSameObject").GetBoolean());
        Assert.True(source.GetProperty("decodeReturnsSameObject").GetBoolean());
        Assert.Same(marker, encoded);
        Assert.Same(marker, decoded);
        Assert.Null(codec.Encode<object?>(null));
        Assert.Null(codec.Decode<object?>(null));
    }

    [Fact]
    public void LoopbackMatchesPinnedSourceRelayAndLifecycleObservations()
    {
        var source = Vectors.RootElement.GetProperty("loopback");
        var loopback = new LoopbackAudioBridge();

        Assert.Equal(128, source.GetProperty("maximumFrameBuffer").GetInt32());
        Assert.Equal(0, source.GetProperty("initialQueuedFrames").GetInt32());
        Assert.True(source.GetProperty("startSetsRunning").GetBoolean());
        Assert.True(source.GetProperty("sourceSetterPreservesReference").GetBoolean());
        Assert.True(source.GetProperty("canReceiveWhenDownstreamAccepts").GetBoolean());
        Assert.False(source.GetProperty("canReceiveWhenDownstreamRejects").GetBoolean());
        Assert.True(source.GetProperty("forwardsSameFrame").GetBoolean());
        Assert.True(source.GetProperty("forwardsLoopbackAsSource").GetBoolean());
        Assert.True(source.GetProperty("stopClearsRunning").GetBoolean());
        Assert.False(source.GetProperty("targetFrameMsUsedByLoopback").GetBoolean());
        Assert.False(source.GetProperty("upstreamClockedOutput").GetBoolean());
        Assert.Equal(70, source.GetProperty("constructorTargetFrameMs").GetInt32());
        Assert.Equal(128, LoopbackAudioBridge.MaximumBufferedFrames);
        Assert.Equal(TimeSpan.FromMilliseconds(70), loopback.FrameInterval);
        Assert.False(loopback.IsRunning);
    }
}
