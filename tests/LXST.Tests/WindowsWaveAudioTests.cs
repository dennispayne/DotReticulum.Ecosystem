using System.Runtime.InteropServices;
using System.Threading.Channels;
using LXST.Devices;

namespace LXST.Tests;

[Trait("Category", "Unit")]
public sealed class WindowsWaveAudioTests
{
    private static readonly AudioDeviceFormat Format = new(8000, 1, TimeSpan.FromMilliseconds(20));

    [Fact]
    public async Task CaptureUsesSelectedDeviceAndDisposalCancelsBlockedRead()
    {
        var driver = new Driver();
        var backend = new WindowsWaveAudioBackend(driver);
        var capture = await backend.OpenCaptureAsync("7", Format);
        await using var frames = capture.CaptureAsync().GetAsyncEnumerator();
        driver.Session.Input.Writer.TryWrite([0.25f, -0.5f]);
        Assert.True(await frames.MoveNextAsync());
        Assert.Equal(new float[] { 0.25f, -0.5f }, frames.Current.Samples.ToArray());
        Assert.Equal("7", driver.Device);
        Assert.True(driver.Capture);
        var pending = frames.MoveNextAsync().AsTask();
        await capture.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, driver.Session.Disposals);
        await capture.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await using var another = capture.CaptureAsync().GetAsyncEnumerator();
            await another.MoveNextAsync();
        });
    }

    [Fact]
    public async Task CaptureDisposalUnblocksFullQueueAndPrecancelledCaptureDoesNotOpenDevice()
    {
        var driver = new Driver();
        var backend = new WindowsWaveAudioBackend(driver);
        var capture = await backend.OpenCaptureAsync(null, Format);
        await using var frames = capture.CaptureAsync().GetAsyncEnumerator();
        for (var index = 0; index < 10; index++) driver.Session.Input.Writer.TryWrite([0.25f]);
        Assert.True(await frames.MoveNextAsync());
        await driver.Session.SixthRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await capture.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await frames.MoveNextAsync());

        var unused = new Driver();
        await using var canceledCapture = await new WindowsWaveAudioBackend(unused).OpenCaptureAsync(null, Format);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await using var canceledFrames = canceledCapture.CaptureAsync(stop.Token).GetAsyncEnumerator();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await canceledFrames.MoveNextAsync());
        Assert.Equal(0, unused.Opens);
    }

    [Fact]
    public async Task CapturePropagatesOpenFailureInsteadOfCompletingSuccessfully()
    {
        var driver = new Driver { OpenFailure = new IOException("device unavailable") };
        var capture = await new WindowsWaveAudioBackend(driver).OpenCaptureAsync(null, Format);
        await using var frames = capture.CaptureAsync().GetAsyncEnumerator();
        var failure = await Assert.ThrowsAsync<IOException>(async () => await frames.MoveNextAsync());
        Assert.Equal("device unavailable", failure.Message);
        await Assert.ThrowsAsync<IOException>(() => capture.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task PlaybackSerializesCopiesAndCancelsInFlightWorkOnDispose()
    {
        var driver = new Driver();
        var backend = new WindowsWaveAudioBackend(driver);
        var playback = await backend.OpenPlaybackAsync("2", Format);
        var samples = new[] { 0.125f, -1f };
        driver.Session.BlockWrite = true;
        var pending = playback.WriteAsync(new(samples, 8000, 1)).AsTask();
        await driver.Session.Writing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        samples[0] = 0.75f;
        Assert.Equal(0.125f, driver.Session.LastWritten![0]);
        await playback.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(driver.Capture);
        Assert.Equal("2", driver.Device);
        Assert.Equal(1, driver.Session.Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => playback.WriteAsync(new(new float[1], 8000, 1)).AsTask());
    }

    [Fact]
    public async Task PlaybackRejectsFormatAndNonfiniteSamplesBeforeOpeningDevice()
    {
        var driver = new Driver();
        await using var playback = await new WindowsWaveAudioBackend(driver).OpenPlaybackAsync(null, Format);
        await Assert.ThrowsAsync<ArgumentException>(() => playback.WriteAsync(new(new float[1], 16000, 1)).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => playback.WriteAsync(new(new[] { float.NaN }, 8000, 1)).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => playback.WriteAsync(new(new float[8001], 8000, 1)).AsTask());
        Assert.Equal(0, driver.Opens);
    }

    [Fact]
    public async Task PlaybackPropagatesFailureAndSupportsSubsequentWrite()
    {
        var driver = new Driver();
        await using var playback = await new WindowsWaveAudioBackend(driver).OpenPlaybackAsync(null, Format);
        driver.Session.WriteFailure = new IOException("playback lost");
        await Assert.ThrowsAsync<IOException>(() => playback.WriteAsync(new(new float[1], 8000, 1)).AsTask());
        driver.Session.WriteFailure = null;
        await playback.WriteAsync(new(new float[] { 0.5f }, 8000, 1));
        Assert.Equal(new float[] { 0.5f }, driver.Session.LastWritten);
        Assert.Equal(1, driver.Opens);
    }

    [Fact]
    public async Task EnumerationAndOpenRespectCancellationAndFormatBounds()
    {
        var driver = new Driver();
        var backend = new WindowsWaveAudioBackend(driver);
        Assert.Single(await backend.ListInputsAsync());
        Assert.Single(await backend.ListOutputsAsync());
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.ListInputsAsync(stop.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.OpenCaptureAsync(null, Format, stop.Token).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => backend.OpenPlaybackAsync(null, Format with { Channels = 3 }).AsTask());
        Assert.Equal(0, driver.Opens);
    }

    [Fact]
    public void NativeLayoutsMatchWindowsMultimediaAbi()
    {
        Assert.Equal(18, Marshal.SizeOf<WindowsWaveDriver.WaveFormat>());
        Assert.Equal(IntPtr.Size == 8 ? 48 : 32, Marshal.SizeOf<WindowsWaveDriver.WaveHeader>());
        Assert.Equal(IntPtr.Size == 8 ? 24 : 16, Marshal.OffsetOf<WindowsWaveDriver.WaveHeader>("Flags").ToInt32());
    }

    private sealed class Driver : IWindowsWaveDriver
    {
        internal Session Session { get; } = new();
        internal int Opens;
        internal bool Capture;
        internal string? Device;
        internal Exception? OpenFailure;
        public IReadOnlyList<AudioDeviceInfo> List(bool capture) => [new("default", capture ? "Fake input" : "Fake output", true)];
        public IWindowsWaveSession Open(bool capture, string? device, AudioDeviceFormat format)
        {
            Opens++;
            Capture = capture;
            Device = device;
            if (OpenFailure is not null) throw OpenFailure;
            return Session;
        }
    }

    private sealed class Session : IWindowsWaveSession
    {
        internal Channel<float[]> Input { get; } = Channel.CreateUnbounded<float[]>();
        internal TaskCompletionSource Writing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SixthRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;
        internal float[]? LastWritten;
        internal bool BlockWrite;
        internal Exception? WriteFailure;
        internal int Disposals;
        public float[] Read(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _reads) == 6) SixthRead.TrySetResult();
            return Input.Reader.ReadAsync(cancellationToken).AsTask().GetAwaiter().GetResult();
        }
        public void Write(ReadOnlySpan<float> samples, CancellationToken cancellationToken)
        {
            LastWritten = samples.ToArray();
            Writing.TrySetResult();
            if (WriteFailure is not null) throw WriteFailure;
            if (BlockWrite)
            {
                cancellationToken.WaitHandle.WaitOne();
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        public void Dispose() => Disposals++;
    }
}

[Trait("Category", "Integration")]
public sealed class WindowsWaveEnumerationTests
{
    [WindowsWaveFact]
    public async Task EnumeratesActualWindowsEndpointsWithoutOpeningThem()
    {
        var backend = new WindowsWaveAudioBackend();
        foreach (var devices in new[] { await backend.ListInputsAsync(), await backend.ListOutputsAsync() })
        {
            Assert.Equal(devices.Count, devices.Select(device => device.Id).Distinct().Count());
            Assert.All(devices, device => Assert.False(string.IsNullOrWhiteSpace(device.Name)));
            if (devices.Count != 0) Assert.Single(devices, device => device.IsDefault);
        }
    }

    [WindowsWavePlaybackFact]
    [Trait("Category", "Live")]
    public async Task SilentNativePlaybackCancelsAndThenReusesReturnedBuffer()
    {
        var backend = new WindowsWaveAudioBackend();
        await using var playback = await backend.OpenPlaybackAsync(null, new(48000, 2, TimeSpan.FromMilliseconds(20)));
        await playback.WriteAsync(new(new float[1920], 48000, 2));
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            playback.WriteAsync(new(new float[96000], 48000, 2), stop.Token).AsTask());
        await playback.WriteAsync(new(new float[1920], 48000, 2));
    }
}

public sealed class WindowsWaveFactAttribute : FactAttribute
{
    public WindowsWaveFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows waveform device enumeration requires Windows.";
    }

}

public sealed class WindowsWavePlaybackFactAttribute : FactAttribute
{
    public WindowsWavePlaybackFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows waveform playback requires Windows.";
        else if (Environment.GetEnvironmentVariable("DOTLXST_WINDOWS_AUDIO") != "1")
            Skip = "Set DOTLXST_WINDOWS_AUDIO=1 to open the default Windows output and play synthetic silence.";
    }
}
