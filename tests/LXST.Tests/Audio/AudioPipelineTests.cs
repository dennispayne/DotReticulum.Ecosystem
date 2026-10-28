using System.Runtime.CompilerServices;
using System.Threading.Channels;
using LXST.Audio;

namespace LXST.Tests.Audio;

[Trait("Category", "Unit")]
public sealed class AudioPipelineTests
{
    [Fact]
    public async Task PipelineStartsReplacesCodecAndStopsWithoutOwningSourceOrSink()
    {
        var source = new RepeatingSource();
        var sink = new FrameSink();
        var originalCodec = new LoopbackCodec();
        var replacement = new LoopbackCodec();
        var pipeline = new AudioPipeline(source, originalCodec, sink);

        pipeline.Start();
        _ = await sink.ReadAsync();
        pipeline.ReplaceCodec(replacement);
        _ = await sink.ReadAsync();
        await pipeline.StopAsync();
        Assert.False(pipeline.IsRunning);
        pipeline.Start();
        _ = await sink.ReadAsync();
        var firstDispose = pipeline.DisposeAsync();
        var secondDispose = pipeline.DisposeAsync();
        await Task.WhenAll(firstDispose.AsTask(), secondDispose.AsTask());

        Assert.True(originalCodec.IsDisposed);
        Assert.True(replacement.IsDisposed);
        Assert.False(pipeline.IsRunning);
        Assert.False(source.IsDisposed);
        Assert.False(sink.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => pipeline.Start());
    }

    [Fact]
    public async Task PipelinePropagatesSinkFailureThroughCompletion()
    {
        var pipeline = new AudioPipeline(new RepeatingSource(), new LoopbackCodec(), new FaultingSink());
        pipeline.Start();

        var error = await Assert.ThrowsAsync<IOException>(async () => await pipeline.Completion);
        Assert.Equal("sink failed", error.Message);
        await Assert.ThrowsAsync<IOException>(async () => await pipeline.StopAsync().AsTask());
        await pipeline.DisposeAsync();
    }

    private sealed class RepeatingSource : IAudioSource
    {
        public bool IsDisposed { get; private set; }

        public async IAsyncEnumerable<AudioFrame> CaptureAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var sample = 0f;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new(new[] { sample += 0.01f }, 8_000, 1);
                await Task.Delay(1, cancellationToken);
            }
        }
    }

    private sealed class LoopbackCodec : IAudioCodec
    {
        private AudioFrame? _frame;
        public CodecKind Kind => CodecKind.Raw;
        public bool IsDisposed { get; private set; }

        public byte[] Encode(AudioFrame frame)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            _frame = frame;
            return [1];
        }

        public AudioFrame Decode(ReadOnlySpan<byte> data)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (data.Length != 1 || _frame is null) throw new FormatException("Invalid test packet.");
            return _frame;
        }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class FrameSink : IAudioSink
    {
        private readonly Channel<AudioFrame> _frames = Channel.CreateUnbounded<AudioFrame>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        public bool IsDisposed { get; private set; }

        public ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken = default) =>
            _frames.Writer.WriteAsync(frame, cancellationToken);

        public async Task<AudioFrame> ReadAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            return await _frames.Reader.ReadAsync(timeout.Token);
        }
    }

    private sealed class FaultingSink : IAudioSink
    {
        public ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("sink failed"));
    }
}
