using System.Text.Json;

namespace LXST.Tests;

[Trait("Category", "Unit")]
public sealed class TelephoneAudioOutputTests
{
    private sealed class Backend : IAudioDeviceBackend
    {
        public readonly List<string[]> Operations = [];
        public readonly List<Playback> Leases = [];
        public bool BlockWrites;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<IReadOnlyList<AudioDeviceInfo>> ListInputsAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AudioDeviceInfo>>([]);
        public ValueTask<IReadOnlyList<AudioDeviceInfo>> ListOutputsAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AudioDeviceInfo>>([]);
        public ValueTask<IAudioCapture> OpenCaptureAsync(string? device, AudioDeviceFormat format, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No microphone may be opened.");
        public ValueTask<IAudioPlayback> OpenPlaybackAsync(string? device, AudioDeviceFormat format, CancellationToken cancellationToken = default)
        {
            if (device == "blocked") throw new IOException("injected replacement failure");
            Operations.Add(["open", device ?? "default"]);
            var lease = new Playback(this, device ?? "default");
            Leases.Add(lease);
            return ValueTask.FromResult<IAudioPlayback>(lease);
        }
    }

    private sealed class Playback(Backend backend, string device) : IAudioPlayback
    {
        public bool Disposed;
        public readonly List<AudioFrame> Frames = [];
        public async ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken = default)
        {
            backend.Entered.TrySetResult();
            if (backend.BlockWrites) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            Frames.Add(frame);
            backend.Written.TrySetResult();
        }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            backend.Operations.Add(["close", device]);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task RoutesAreLazyIdempotentMakeBeforeBreakAndResetWithoutOpeningAnotherDevice()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "lxst-telephone-controls.json")));
        var backend = new Backend();
        await using var output = new TelephoneAudioOutput(backend, "speaker-A", "handsfree-B");
        Assert.Empty(backend.Operations);
        var frame = new AudioFrame(new float[80], 8000, 1);
        await output.WriteAsync(frame);
        foreach (var row in fixture.RootElement.GetProperty("routes").EnumerateArray())
        {
            backend.Operations.Clear();
            switch (row.GetProperty("name").GetString())
            {
                case "enable":
                case "enable-again":
                case "disable-false":
                case "enable-before-hangup": await output.SelectLoudspeakerAsync(true); break;
                case "change-speaker": output.SpeakerDevice = "speaker-C"; break;
                case "hangup": await output.ResetAsync(); break;
                default: await output.SelectLoudspeakerAsync(false); break;
            }
            Assert.Equal(row.GetProperty("enabled").GetBoolean(), output.LoudspeakerEnabled);
            if (row.GetProperty("device").ValueKind == JsonValueKind.Null)
            {
                Assert.All(backend.Leases, lease => Assert.True(lease.Disposed));
                Assert.DoesNotContain(backend.Operations, operation => operation[0] == "open");
            }
            else
            {
                var expected = row.GetProperty("actions").EnumerateArray()
                    .Where(action => action[0].GetString() is "open" or "close")
                    .Select(action => action.EnumerateArray().Select(value => value.GetString()!).ToArray()).ToArray();
                Assert.Equal(expected, backend.Operations);
            }
        }
        await output.WriteAsync(frame);
        Assert.Equal("speaker-C", backend.Operations[^1][1]);
    }

    [Fact]
    public async Task FailedReplacementRetainsOldLeaseAndCancellationDoesNotSelectADevice()
    {
        var backend = new Backend();
        await using var output = new TelephoneAudioOutput(backend, "speaker", "blocked");
        await output.WriteAsync(new(new float[80], 8000, 1));
        var original = Assert.Single(backend.Leases);
        await Assert.ThrowsAsync<IOException>(() => output.SelectLoudspeakerAsync(true).AsTask());
        Assert.False(original.Disposed);
        await output.SelectLoudspeakerAsync(false);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        Assert.Throws<OperationCanceledException>(() => output.SelectLoudspeakerAsync(true, canceled.Token));
        Assert.False(output.LoudspeakerEnabled);
        Assert.Single(backend.Leases);
    }

    [Fact]
    public async Task BufferedResetCancelsBlockedOutputDropsOldFramesAndSupportsNextCall()
    {
        var backend = new Backend { BlockWrites = true };
        var output = new TelephoneAudioOutput(backend, "speaker", "handsfree", bufferOutput: true);
        output.ConfigureProfile(TelephoneProfile.MediumQuality);
        var old = new AudioFrame(Enumerable.Repeat(.1f, 80).ToArray(), 8000, 1);
        for (var index = 0; index < 3; index++) await output.WriteAsync(old);
        await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(output.ResetAsync().AsTask(), output.ResetAsync().AsTask());
        Assert.All(backend.Leases, lease => Assert.True(lease.Disposed));
        backend.BlockWrites = false;
        await output.WriteAsync(new(Enumerable.Repeat(.9f, 80).ToArray(), 8000, 1));
        await backend.Written.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(backend.Leases[^1].Frames, frame => Assert.All(frame.Samples.ToArray(), sample => Assert.InRange(sample, .89, .91)));
        await Task.WhenAll(output.DisposeAsync().AsTask(), output.DisposeAsync().AsTask());
        Assert.All(backend.Leases, lease => Assert.True(lease.Disposed));
        Assert.Throws<ObjectDisposedException>(() => output.WriteAsync(old));
    }
}
