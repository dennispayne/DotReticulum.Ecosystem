using System.Runtime.CompilerServices;

namespace LXST.Tests;

[Trait("Category", "Unit")]
public sealed class DeviceAudioTests
{
    private sealed class Backend : IAudioDeviceBackend
    {
        public readonly List<(string? Device, AudioDeviceFormat Format)> Opened = [];
        public readonly List<Playback> Outputs = [];
        public Capture? Input;
        public bool FailNextWrite;
        public ValueTask<IReadOnlyList<AudioDeviceInfo>> ListInputsAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AudioDeviceInfo>>([new("in", "Input", true)]);
        public ValueTask<IReadOnlyList<AudioDeviceInfo>> ListOutputsAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AudioDeviceInfo>>([new("out", "Output", true)]);
        public ValueTask<IAudioCapture> OpenCaptureAsync(string? device, AudioDeviceFormat format, CancellationToken cancellationToken = default)
        {
            Opened.Add((device, format));
            Input = new();
            return ValueTask.FromResult<IAudioCapture>(Input);
        }
        public ValueTask<IAudioPlayback> OpenPlaybackAsync(string? device, AudioDeviceFormat format, CancellationToken cancellationToken = default)
        {
            Opened.Add((device, format));
            var output = new Playback { Fail = FailNextWrite };
            FailNextWrite = false;
            Outputs.Add(output);
            return ValueTask.FromResult<IAudioPlayback>(output);
        }
    }

    private sealed class Playback : IAudioPlayback
    {
        public bool Fail;
        public bool Disposed;
        public List<AudioFrame> Frames = [];
        public ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new IOException("injected device failure");
            Frames.Add(frame);
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class Capture : IAudioCapture
    {
        public bool Disposed;
        public TaskCompletionSource Blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<AudioFrame> CaptureAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new(new float[] { .25f, -.5f }, 8000, 1);
            Blocked.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task PlaybackDeviceAndFormatChangesCloseOldLeasesAndFailedOutputRecovers()
    {
        var backend = new Backend();
        await using var sink = new DeviceAudioSink(backend, firstFrame: () => throw new InvalidOperationException("observer"));
        var mono = new AudioFrame(new float[80], 8000, 1);
        await sink.WriteAsync(mono);
        await sink.WriteAsync(mono);
        Assert.Single(backend.Opened);
        sink.SetDevice("other");
        await sink.WriteAsync(mono);
        Assert.True(backend.Outputs[0].Disposed);
        Assert.Equal("other", backend.Opened[^1].Device);
        await sink.WriteAsync(new(new float[160], 8000, 2));
        Assert.True(backend.Outputs[1].Disposed);
        await sink.StopAsync();
        Assert.True(backend.Outputs[2].Disposed);
        backend.FailNextWrite = true;
        await Assert.ThrowsAsync<IOException>(() => sink.WriteAsync(mono).AsTask());
        Assert.True(backend.Outputs[^1].Disposed);
        await sink.WriteAsync(mono);
        Assert.Single(backend.Outputs[^1].Frames);
    }

    [Fact]
    public async Task CaptureGainAndCancellationReleaseBackendBeforeConcurrentDisposersComplete()
    {
        var backend = new Backend();
        var source = new DeviceAudioSource(backend, new(8000, 1, TimeSpan.FromMilliseconds(10)), "in") { LinearGain = 2 };
        await using var enumerator = source.CaptureAsync().GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(new float[] { .5f, -1 }, enumerator.Current.Samples.ToArray());
        var pending = enumerator.MoveNextAsync().AsTask();
        await backend.Input!.Blocked.Task;
        var one = source.DisposeAsync().AsTask();
        var two = source.DisposeAsync().AsTask();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await Task.WhenAll(one, two);
        Assert.True(backend.Input.Disposed);
        Assert.Throws<ObjectDisposedException>(() => source.LinearGain = 1);
    }

    [Fact]
    public async Task CaptureRejectsConcurrentReadersButCanRestartAfterEnumeratorDisposal()
    {
        var backend = new Backend();
        await using var source = new DeviceAudioSource(backend, new(8000, 1, TimeSpan.FromMilliseconds(10)));
        await using (var first = source.CaptureAsync().GetAsyncEnumerator())
        {
            Assert.True(await first.MoveNextAsync());
            await using var competing = source.CaptureAsync().GetAsyncEnumerator();
            await Assert.ThrowsAsync<InvalidOperationException>(() => competing.MoveNextAsync().AsTask());
        }
        Assert.True(backend.Input!.Disposed);
        await using var restarted = source.CaptureAsync().GetAsyncEnumerator();
        Assert.True(await restarted.MoveNextAsync());
        Assert.Equal(2, backend.Opened.Count);
    }
}
