using System.Runtime.InteropServices;
using System.Text.Json;
using LXST.Devices;

namespace LXST.Tests;

[Trait("Category", "Unit")]
public sealed class WindowsWasapiTests
{
    [Fact]
    public void ChannelMapsMatchExecutedPrimaryPlaybackAndCapture()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxst-wasapi.json")));
        foreach (var row in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var map = row.GetProperty("map").EnumerateArray().Select(value => value.GetInt32()).ToArray();
            var input = row.GetProperty("input").EnumerateArray().Select(value => value.GetSingle()).ToArray();
            var capture = new WasapiChannelMap(map.Length, map, true);
            Assert.Equal(row.GetProperty("capture").EnumerateArray().Select(value => value.GetSingle()), capture.Capture(input));
            if (row.TryGetProperty("playback_error", out _))
                Assert.Throws<ArgumentException>(() => new WasapiChannelMap(map.Length, map, false));
            else
            {
                var playback = new WasapiChannelMap(map.Length, map, false);
                var output = new float[input.Length];
                playback.Playback(input, output);
                Assert.Equal(row.GetProperty("playback").EnumerateArray().Select(value => value.GetSingle()), output);
            }
        }
        Assert.Throws<ArgumentException>(() => new WasapiChannelMap(2, [0, 2], true));
        Assert.Throws<ArgumentException>(() => new WasapiChannelMap(2, [-1, 0], true));
        Assert.Throws<ArgumentException>(() => new WasapiChannelMap(1, [0, 1], true));
        Assert.Throws<ArgumentException>(() => new WasapiChannelMap(1, [32], true));
        Assert.Equal(40, Marshal.SizeOf<WindowsWasapiDriver.WaveFormat>());
        Assert.Equal(24, Marshal.OffsetOf<WindowsWasapiDriver.WaveFormat>("SubFormat").ToInt32());
    }

    [Fact]
    public async Task NativeSessionOwnershipIsConfinedToOneThreadAndBlockedWorkCancels()
    {
        var threads = new List<int>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var native = new Session(threads, entered);
        var host = new WasapiSessionHost(() => { threads.Add(Environment.CurrentManagedThreadId); return native; });
        host.Write([.25f, -.5f], CancellationToken.None);
        var read = Task.Run(() => host.Read(CancellationToken.None));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Run(host.Dispose).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        host.Dispose();
        Assert.Single(threads.Distinct());
        Assert.Equal(1, native.Disposals);
        Assert.Throws<ObjectDisposedException>(() => host.Read(CancellationToken.None));
        Assert.Throws<IOException>(() => new WasapiSessionHost(() => throw new IOException("unavailable")));
        using var recovered = new WasapiSessionHost(() => new Session([], new(TaskCreationOptions.RunContinuationsAsynchronously)));
        recovered.Write([0], CancellationToken.None);
    }

    [Fact]
    public async Task TypedLoopbackAndMultichannelCaptureKeepTheSelectedIdentifierAndLease()
    {
        var driver = new Driver();
        var backend = new WindowsWasapiAudioBackend(driver);
        var format = new AudioDeviceFormat(48000, 3, TimeSpan.FromMilliseconds(20));
        await using var capture = await backend.OpenLoopbackCaptureAsync("render-id", format);
        await using var frames = capture.CaptureAsync().GetAsyncEnumerator();
        Assert.True(await frames.MoveNextAsync());
        Assert.Equal("loopback:render-id", driver.Id);
        Assert.Equal(3, frames.Current.Channels);
        Assert.Equal(new float[] { .1f, .2f, .3f }, frames.Current.Samples.ToArray());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            backend.OpenCaptureAsync(null, format with { Channels = 33 }).AsTask());
    }

    [Fact]
    public void ExclusiveLoopbackAndInvalidModeFailBeforeNativeInitialization()
    {
        var driver = new WindowsWasapiDriver(new() { ShareMode = WasapiShareMode.Exclusive });
        Assert.Throws<ArgumentException>(() => driver.Open(true, "loopback:default", new(48000, 2, TimeSpan.FromMilliseconds(20))));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowsWasapiDriver(new() { ShareMode = (WasapiShareMode)99 }));
    }

    private sealed class Driver : IWindowsWaveDriver
    {
        internal string? Id;
        public IReadOnlyList<AudioDeviceInfo> List(bool capture) => [new("render-id", "Synthetic", true)];
        public IWindowsWaveSession Open(bool capture, string? device, AudioDeviceFormat format)
        {
            Id = device;
            return new CaptureSession();
        }
    }
    private sealed class CaptureSession : IWindowsWaveSession
    {
        public float[] Read(CancellationToken token) { token.ThrowIfCancellationRequested(); return [.1f, .2f, .3f]; }
        public void Write(ReadOnlySpan<float> samples, CancellationToken token) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private sealed class Session(List<int> threads, TaskCompletionSource entered) : IWindowsWaveSession
    {
        internal int Disposals;
        public float[] Read(CancellationToken token)
        {
            threads.Add(Environment.CurrentManagedThreadId);
            entered.TrySetResult();
            token.WaitHandle.WaitOne();
            token.ThrowIfCancellationRequested();
            return [];
        }
        public void Write(ReadOnlySpan<float> samples, CancellationToken token) => threads.Add(Environment.CurrentManagedThreadId);
        public void Dispose() { Disposals++; threads.Add(Environment.CurrentManagedThreadId); }
    }
}

[Trait("Category", "Integration")]
public sealed class WindowsWasapiNativeTests
{
    [WindowsWaveFact]
    public async Task EnumeratesActualWasapiEndpointsAndLoopbackIdentifiersWithoutOpeningMicrophones()
    {
        var backend = new WindowsWasapiAudioBackend(new WindowsWasapiOptions { IncludeLoopbackInputs = true });
        var outputs = await backend.ListOutputsAsync();
        var inputs = await backend.ListInputsAsync();
        Assert.Equal(outputs.Count, inputs.Count(device => device.Id.StartsWith("loopback:", StringComparison.Ordinal)));
        Assert.Equal(outputs.Count, outputs.Select(device => device.Id).Distinct().Count());
        Assert.All(inputs.Concat(outputs), device => Assert.False(string.IsNullOrWhiteSpace(device.Name)));
    }

    [WindowsWavePlaybackFact]
    [Trait("Category", "Live")]
    public async Task ExclusiveModeRendersSilenceOrReportsTheIndependentlyProbedUnsupportedFormat()
    {
        var backend = new WindowsWasapiAudioBackend(new WindowsWasapiOptions { ShareMode = WasapiShareMode.Exclusive });
        var format = new AudioDeviceFormat(48000, 2, TimeSpan.FromMilliseconds(20));
        var supported = await backend.SupportsPlaybackFormatAsync(format);
        await using var playback = await backend.OpenPlaybackAsync(null, format);
        if (supported) await playback.WriteAsync(new(new float[1920], 48000, 2));
        else
        {
            var error = await Assert.ThrowsAsync<IOException>(() => playback.WriteAsync(new(new float[1920], 48000, 2)).AsTask());
            Assert.Contains("0x88890008", error.Message);
        }
    }

    [WindowsWavePlaybackFact]
    [Trait("Category", "Live")]
    public async Task SharedSilentRenderingAndLoopbackActivationUseActualWasapiWithoutReadingSystemAudio()
    {
        var backend = new WindowsWasapiAudioBackend(new WindowsWasapiOptions { ChannelMap = [1, 0] });
        var format = new AudioDeviceFormat(48000, 2, TimeSpan.FromMilliseconds(20));
        await using var playback = await backend.OpenPlaybackAsync(null, format);
        await playback.WriteAsync(new(new float[1920], 48000, 2));
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            playback.WriteAsync(new(new float[96000], 48000, 2), stop.Token).AsTask());
        await playback.WriteAsync(new(new float[1920], 48000, 2));
        var driver = new WindowsWasapiDriver(new());
        using var loopback = driver.Open(true, "loopback:default", format);
        // Activation/GetService/Start/Stop only: never retrieve private system audio.
    }
}
