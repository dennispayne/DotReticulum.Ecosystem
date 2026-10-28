using System.Text.Json;
using LXST.Audio;

namespace LXST.Tests.Audio;

[Trait("Category", "Unit")]
public sealed class BufferedAudioSinkTests
{
    [Fact]
    public async Task PlaybackQueueBoundsLagDropAndChannelTruncationMatchPrimary()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxst-audio-processing.json")));
        var source = fixture.RootElement.GetProperty("bufferedPlayback");
        var output = new RecordingSink();
        await using var sink = new BufferedAudioSink(output, 8_000, 1, autoStart: false);
        for (var value = 0; value < 8; value++)
        {
            await sink.WriteAsync(new AudioFrame(new float[] { value, -value }, 8_000, 2));
            Assert.Equal(source.GetProperty("canReceiveAfterWrites")[value].GetBoolean(), sink.CanReceive);
        }
        Assert.Equal(source.GetProperty("maximumQueuedFrames").GetInt32(), sink.QueuedFrames);
        sink.Start();
        await sink.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(sink.IsRunning);
        Assert.Equal(
            source.GetProperty("played").EnumerateArray().Select(frame => frame[0][0].GetSingle()).ToArray(),
            output.Frames.Select(frame => frame.Samples.Span[0]).ToArray());
        Assert.All(output.Frames, frame => Assert.Equal(1, frame.Channels));
        Assert.Equal(3, sink.DroppedFrames);
        Assert.Equal(TimeSpan.FromSeconds(source.GetProperty("frameSeconds").GetDouble()) * 3, sink.MaximumLatency);
        Assert.False(sink.RequestLowLatency());

        await sink.WriteAsync(new AudioFrame(new float[] { 10f, -10f }, 8_000, 2));
        sink.Start();
        await sink.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(source.GetProperty("restartPlayed")[0][0][0].GetSingle(), output.Frames[^1].Samples.Span[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FaultedPlaybackAndErrorObserversDoNotDisableSubsequentFrames(bool throwingObserver)
    {
        var output = new RecordingSink { FailFirst = true };
        await using var sink = new BufferedAudioSink(output, 8_000, 1, autoStart: false);
        if (throwingObserver) sink.Error = (_, _) => throw new InvalidOperationException("observer failed");
        await sink.WriteAsync(new AudioFrame(new float[] { 0.1f }, 8_000, 1));
        await sink.WriteAsync(new AudioFrame(new float[] { 0.2f }, 8_000, 1));
        sink.Start();
        await sink.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0.2f, Assert.Single(output.Frames).Samples.Span[0]);
    }

    [Fact]
    public async Task DisposalCancelsBlockedOutputWithoutOwningItAndIsIdempotent()
    {
        var output = new BlockingSink();
        var sink = new BufferedAudioSink(output, 8_000, 1);
        await sink.WriteAsync(new AudioFrame(new float[] { 0.1f }, 8_000, 1));
        await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var first = sink.DisposeAsync().AsTask();
        var second = sink.DisposeAsync().AsTask();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(output.Cancelled);
        Assert.False(sink.IsRunning);
        Assert.False(sink.CanReceive);
        Assert.Equal(0, sink.QueuedFrames);
        Assert.Throws<ObjectDisposedException>(() => sink.Start());
        Assert.Throws<ObjectDisposedException>(() => sink.WriteAsync(
            new AudioFrame(new float[] { 0f }, 8_000, 1)));
    }

    [Fact]
    public async Task StopPreservesQueuedFramesAndRejectsMalformedWrites()
    {
        var output = new RecordingSink();
        await using var sink = new BufferedAudioSink(output, 8_000, 1, autoStart: false);
        Assert.Throws<InvalidOperationException>(() => sink.Start());
        Assert.Throws<ArgumentException>(() => sink.WriteAsync(new AudioFrame(Array.Empty<float>(), 8_000, 1)));
        Assert.Throws<ArgumentException>(() => sink.WriteAsync(new AudioFrame(new float[] { float.NaN }, 8_000, 1)));
        Assert.Throws<ArgumentException>(() => sink.WriteAsync(new AudioFrame(new float[] { 0f }, 16_000, 1)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => sink.WriteAsync(
            new AudioFrame(new float[] { 0f }, 8_000, 1), cancellation.Token));
        await sink.WriteAsync(new AudioFrame(new float[] { 0.2f }, 8_000, 1));
        sink.Start(cancellation.Token);
        await sink.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        await sink.StopAsync();
        Assert.Equal(1, sink.QueuedFrames);
        sink.Start();
        await sink.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Single(output.Frames);
    }

    [Fact]
    public async Task StarvationStopsAfterEightFrameTimesAndAutostartCanRecover()
    {
        var clock = new ManualClock();
        var output = new RecordingSink();
        await using var sink = new BufferedAudioSink(output, 8_000, 1, timeProvider: clock);
        await sink.WriteAsync(new AudioFrame(new float[80], 8_000, 1));
        await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(sink.IsRunning);
        clock.Advance(TimeSpan.FromMilliseconds(79));
        Assert.True(sink.IsRunning);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await sink.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(sink.IsRunning);
        await sink.WriteAsync(new AudioFrame(new float[80], 8_000, 1));
        await sink.StopAsync();
        Assert.False(sink.IsRunning);
    }

    private sealed class RecordingSink : IAudioSink
    {
        public bool FailFirst { get; set; }
        public List<AudioFrame> Frames { get; } = [];
        public ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken = default)
        {
            if (FailFirst)
            {
                FailFirst = false;
                throw new IOException("fake playback failure");
            }
            Frames.Add(frame);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingSink : IAudioSink
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        public async ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled = true;
                throw;
            }
        }

    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private TimeSpan _elapsed;
        public TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                var timer = new ManualTimer(this, callback, state);
                _timers.Add(timer);
                timer.Change(dueTime, period);
                TimerCreated.TrySetResult();
                return timer;
            }
        }

        public void Advance(TimeSpan amount)
        {
            ManualTimer[] ready;
            lock (_gate)
            {
                _elapsed += amount;
                ready = _timers.Where(timer => timer.Due <= _elapsed).ToArray();
                foreach (var timer in ready) timer.Due = TimeSpan.MaxValue;
            }
            foreach (var timer in ready) timer.Callback(timer.State);
        }

        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public TimerCallback Callback { get; } = callback;
            public object? State { get; } = state;
            public TimeSpan Due { get; set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._gate)
                    Due = dueTime == Timeout.InfiniteTimeSpan ? TimeSpan.MaxValue : clock._elapsed + dueTime;
                return true;
            }
            public void Dispose() { lock (clock._gate) Due = TimeSpan.MaxValue; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
