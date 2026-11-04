using System.Diagnostics;
using System.Runtime.CompilerServices;
using LXST;

namespace Sideband.Tests;

// Timed playback uses real cancellable timers, but all device leases are fakes.
[Trait("Category", "Integration")]
public sealed class SidebandMediaDeviceTests
{
    [Fact]
    public async Task ValidationAndPreCancellationNeverOpenHardwareOrEnumerateDevices()
    {
        var backend = new Backend();
        Assert.Empty(backend.Inputs);
        Assert.Empty(backend.Outputs);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => SidebandMediaDevices.RecordAsync(
            backend, null, TimeSpan.FromMilliseconds(999), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => SidebandMediaDevices.RecordAsync(
            backend, null, TimeSpan.FromSeconds(11), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => SidebandMediaDevices.PlayAsync(
            backend, null, new(new float[0], 12000, 1), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => SidebandMediaDevices.PlayAsync(
            backend, null, new(new float[80_001], 8000, 1), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => SidebandMediaDevices.PlayAsync(
            backend, null, new(new float[] { float.NaN }, 12000, 1), TestContext.Current.CancellationToken));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SidebandMediaDevices.RecordAsync(
            backend, null, TimeSpan.FromSeconds(1), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SidebandMediaDevices.PlayAsync(
            backend, null, new(new float[240], 12000, 1), cancelled.Token));
        Assert.Empty(backend.Inputs);
        Assert.Empty(backend.Outputs);
        Assert.Equal(0, backend.Enumerations);
    }

    [Fact]
    public async Task CaptureHasAnExplicitLeaseAndConvertsTheFirstChannelWithoutAnObserver()
    {
        var backend = new Backend { CaptureRate = 8000, CaptureChannels = 2 };
        var frame = await SidebandMediaDevices.RecordAsync(backend, "microphone", TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
        Assert.Equal(12000, frame.SampleRate);
        Assert.Equal(1, frame.Channels);
        Assert.Equal(12000, frame.Samples.Length);
        Assert.All(frame.Samples.ToArray(), sample => Assert.InRange(sample, .2499f, .2501f));
        var lease = Assert.Single(backend.Inputs);
        Assert.Equal("microphone", lease.Device);
        Assert.Equal(new AudioDeviceFormat(48000, 1, TimeSpan.FromMilliseconds(20)), lease.Format);
        Assert.Equal(50, lease.FramesRead);
        Assert.Equal(1, lease.Disposals);
        Assert.Equal(0, backend.Enumerations);
    }

    [Fact]
    public async Task PlaybackIsPacedConvertsFormatAndKeepsOneLeaseForItsPartialFinalChunk()
    {
        var backend = new Backend();
        var interleaved = Enumerable.Range(0, 401).SelectMany(_ => new[] { .25f, -.75f }).ToArray();
        var clock = Stopwatch.StartNew();
        await SidebandMediaDevices.PlayAsync(backend, "speaker", new(interleaved, 8000, 2),
            TestContext.Current.CancellationToken);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(50));
        var lease = Assert.Single(backend.Outputs);
        Assert.Equal("speaker", lease.Device);
        Assert.Equal(3, lease.Frames.Count);
        Assert.Equal(1, lease.Disposals);
        Assert.All(lease.Frames, frame =>
        {
            Assert.Equal(48000, frame.SampleRate);
            Assert.Equal(1, frame.Channels);
            Assert.Equal(960, frame.Samples.Length);
        });
        Assert.All(lease.Frames[0].Samples.ToArray(), sample => Assert.InRange(sample, .2499f, .2501f));
        Assert.Equal(0, lease.Frames[^1].Samples.Span[^1]);
        Assert.True(lease.WriteTimes[^1] - lease.WriteTimes[0] >= TimeSpan.FromMilliseconds(30));
        Assert.Equal(0, backend.Enumerations);
    }

    [Fact]
    public async Task SlowDeviceOpenDoesNotConsumeTheFirstPlaybackFramePacingInterval()
    {
        var backend = new Backend { OpenDelay = TimeSpan.FromMilliseconds(80) };
        await SidebandMediaDevices.PlayAsync(backend, "speaker", new(new float[2400], 48000, 1),
            TestContext.Current.CancellationToken);
        var lease = Assert.Single(backend.Outputs);
        Assert.Equal(3, lease.Frames.Count);
        Assert.Equal(1, lease.Disposals);
        Assert.True(lease.WriteTimes[^1] - lease.WriteTimes[0] >= TimeSpan.FromMilliseconds(30));
    }

    [Fact]
    public async Task CancellationDisposesBlockedCaptureAndFreshRecordingSucceeds()
    {
        var backend = new Backend { BlockCapture = true };
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var capture = SidebandMediaDevices.RecordAsync(backend, null, TimeSpan.FromSeconds(1), cancelled.Token);
        await backend.CaptureBlocked.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        Assert.Equal(1, Assert.Single(backend.Inputs).Disposals);
        backend.BlockCapture = false;
        var next = await SidebandMediaDevices.RecordAsync(backend, null, TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
        Assert.Equal(12000, next.Samples.Length);
        Assert.Equal(1, backend.Inputs[^1].Disposals);
    }

    [Fact]
    public async Task CancellationDisposesBlockedPlaybackAndFreshPlaybackSucceeds()
    {
        var backend = new Backend { BlockPlayback = true };
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var play = SidebandMediaDevices.PlayAsync(backend, null, new(new float[2400], 12000, 1), cancelled.Token);
        await backend.PlaybackBlocked.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => play);
        Assert.Equal(1, Assert.Single(backend.Outputs).Disposals);
        backend.BlockPlayback = false;
        await SidebandMediaDevices.PlayAsync(backend, null, new(new float[] { .25f }, 8000, 1),
            TestContext.Current.CancellationToken);
        Assert.Equal(1, backend.Outputs[^1].Disposals);
        Assert.Single(backend.Outputs[^1].Frames);
    }

    [Fact]
    public async Task CancellationDuringPlaybackPacingClosesTheLeaseAndStopsFurtherWrites()
    {
        var backend = new Backend();
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var play = SidebandMediaDevices.PlayAsync(backend, null, new(new float[120000], 12000, 1), cancelled.Token);
        await backend.FirstPlaybackFrame.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => play);
        var lease = Assert.Single(backend.Outputs);
        Assert.InRange(lease.Frames.Count, 1, 499);
        Assert.Equal(1, lease.Disposals);
    }

    [Fact]
    public async Task ProviderReadWriteAndOpenFailuresDoNotPoisonBackendReuse()
    {
        var backend = new Backend { FailCapture = true };
        await Assert.ThrowsAsync<IOException>(() => SidebandMediaDevices.RecordAsync(backend, null,
            TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        Assert.Equal(1, Assert.Single(backend.Inputs).Disposals);
        backend.FailCapture = false;
        backend.FailPlayback = true;
        await Assert.ThrowsAsync<IOException>(() => SidebandMediaDevices.PlayAsync(backend, null,
            new(new float[240], 12000, 1), TestContext.Current.CancellationToken));
        Assert.Equal(1, Assert.Single(backend.Outputs).Disposals);
        backend.FailPlayback = false;
        backend.FailOpen = true;
        await Assert.ThrowsAsync<IOException>(() => SidebandMediaDevices.RecordAsync(backend, null,
            TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<IOException>(() => SidebandMediaDevices.PlayAsync(backend, null,
            new(new float[240], 12000, 1), TestContext.Current.CancellationToken));
        backend.FailOpen = false;
        await SidebandMediaDevices.RecordAsync(backend, null, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        await SidebandMediaDevices.PlayAsync(backend, null, new(new float[240], 12000, 1),
            TestContext.Current.CancellationToken);
        Assert.All(backend.Inputs, lease => Assert.Equal(1, lease.Disposals));
        Assert.All(backend.Outputs, lease => Assert.Equal(1, lease.Disposals));
    }

    [Fact]
    public async Task PrematureCaptureEndIsReportedAndLongRecordingsStillHaveBoundedOutput()
    {
        var backend = new Backend { EndCaptureEarly = true };
        await Assert.ThrowsAsync<EndOfStreamException>(() => SidebandMediaDevices.RecordAsync(backend, null,
            TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        Assert.Equal(1, Assert.Single(backend.Inputs).Disposals);
        backend.EndCaptureEarly = false;
        var frame = await SidebandMediaDevices.RecordAsync(backend, null, TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        Assert.Equal(120_000, frame.Samples.Length);
        Assert.Equal(500, backend.Inputs[^1].FramesRead);
        Assert.Equal(1, backend.Inputs[^1].Disposals);
    }

    [Fact]
    public async Task StalledCaptureHasADeadlineAndItsLeaseIsClosedBeforeReturn()
    {
        var backend = new Backend { BlockCapture = true };
        await Assert.ThrowsAsync<TimeoutException>(() => SidebandMediaDevices.RecordAsync(backend, null,
            TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        Assert.Equal(1, Assert.Single(backend.Inputs).Disposals);
        backend.BlockCapture = false;
        var recovered = await SidebandMediaDevices.RecordAsync(backend, null, TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
        Assert.Equal(12000, recovered.Samples.Length);
    }

    [Fact]
    public async Task MalformedCaptureAndMidStreamRateChangesReleaseTheirLease()
    {
        var backend = new Backend { NonfiniteCapture = true };
        await Assert.ThrowsAsync<InvalidDataException>(() => SidebandMediaDevices.RecordAsync(backend, null,
            TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        Assert.Equal(1, Assert.Single(backend.Inputs).Disposals);
        backend.NonfiniteCapture = false;
        backend.ChangeCaptureRate = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => SidebandMediaDevices.RecordAsync(backend, null,
            TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        Assert.All(backend.Inputs, lease => Assert.Equal(1, lease.Disposals));
        backend.ChangeCaptureRate = false;
        var recovered = await SidebandMediaDevices.RecordAsync(backend, null, TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
        Assert.Equal(12000, recovered.Samples.Length);
    }

    private sealed class Backend : IAudioDeviceBackend
    {
        internal readonly List<Capture> Inputs = [];
        internal readonly List<Playback> Outputs = [];
        internal readonly TaskCompletionSource CaptureBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource PlaybackBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource FirstPlaybackFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int CaptureRate = 48000;
        internal int CaptureChannels = 1;
        internal int Enumerations;
        internal bool BlockCapture;
        internal bool BlockPlayback;
        internal bool FailCapture;
        internal bool FailPlayback;
        internal bool FailOpen;
        internal bool EndCaptureEarly;
        internal bool NonfiniteCapture;
        internal bool ChangeCaptureRate;
        internal TimeSpan OpenDelay;

        public ValueTask<IReadOnlyList<AudioDeviceInfo>> ListInputsAsync(CancellationToken cancellationToken = default)
        {
            Enumerations++;
            throw new InvalidOperationException("Device enumeration was not requested.");
        }

        public ValueTask<IReadOnlyList<AudioDeviceInfo>> ListOutputsAsync(CancellationToken cancellationToken = default) =>
            ListInputsAsync(cancellationToken);

        public ValueTask<IAudioCapture> OpenCaptureAsync(string? device, AudioDeviceFormat format,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailOpen) throw new IOException("Synthetic capture open failure.");
            var capture = new Capture(this, device, format);
            Inputs.Add(capture);
            return ValueTask.FromResult<IAudioCapture>(capture);
        }

        public async ValueTask<IAudioPlayback> OpenPlaybackAsync(string? device, AudioDeviceFormat format,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailOpen) throw new IOException("Synthetic playback open failure.");
            if (OpenDelay > TimeSpan.Zero) await Task.Delay(OpenDelay, cancellationToken);
            var playback = new Playback(this, device);
            Outputs.Add(playback);
            return playback;
        }
    }

    private sealed class Capture(Backend backend, string? device, AudioDeviceFormat format) : IAudioCapture
    {
        internal string? Device => device;
        internal AudioDeviceFormat Format => format;
        internal int Disposals;
        internal int FramesRead;

        public async IAsyncEnumerable<AudioFrame> CaptureAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (backend.BlockCapture)
            {
                backend.CaptureBlocked.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (backend.FailCapture) throw new IOException("Synthetic capture read failure.");
                var samples = new float[backend.CaptureRate / 50 * backend.CaptureChannels];
                for (var index = 0; index < samples.Length; index++)
                    samples[index] = backend.CaptureChannels == 1 || index % backend.CaptureChannels == 0 ? .25f : -.75f;
                if (backend.NonfiniteCapture) samples[0] = float.PositiveInfinity;
                FramesRead++;
                yield return new(samples, backend.ChangeCaptureRate && FramesRead > 1 ? 24000 : backend.CaptureRate,
                    backend.CaptureChannels);
                if (backend.EndCaptureEarly) yield break;
            }
        }

        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    private sealed class Playback(Backend backend, string? device) : IAudioPlayback
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        internal string? Device => device;
        internal readonly List<AudioFrame> Frames = [];
        internal readonly List<TimeSpan> WriteTimes = [];
        internal int Disposals;

        public async ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken = default)
        {
            if (backend.BlockPlayback)
            {
                backend.PlaybackBlocked.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (backend.FailPlayback) throw new IOException("Synthetic playback write failure.");
            Frames.Add(frame);
            WriteTimes.Add(_clock.Elapsed);
            backend.FirstPlaybackFrame.TrySetResult();
        }

        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
