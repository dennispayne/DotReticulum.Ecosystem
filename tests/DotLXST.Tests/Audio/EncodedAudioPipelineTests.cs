using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using DotLXST.Audio;

namespace DotLXST.Tests.Audio;

[Trait("Category", "Unit")]
public sealed class EncodedAudioPipelineTests
{
    private static readonly JsonDocument Vectors = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxst-audio-processing.json")));

    [Fact]
    public async Task EncodedPipelineWritesReferenceRawFrameAndCodecMetadata()
    {
        var source = Vectors.RootElement.GetProperty("pipeline");
        var input = source.GetProperty("rawFrame").GetProperty("input")
            .EnumerateArray().Select(value => value.GetSingle()).ToArray();
        var sink = new CapturingEncodedSink();
        await using var pipeline = AudioPipeline.CreateEncoded(
            new SingleFrameSource(new AudioFrame(input, 8_000, source.GetProperty("rawFrame").GetProperty("channels").GetInt32())),
            new RawAudioCodec(bitDepth: 16, channels: 1, sampleRate: 8_000),
            sink);

        pipeline.Start();
        await pipeline.Completion;

        var output = Assert.IsType<EncodedFrame>(sink.Frames.Single());
        Assert.Equal(CodecKind.Raw, output.Codec);
        Assert.Equal(Convert.FromHexString(source.GetProperty("rawFrame").GetProperty("encoded").GetString()!), output.Data.ToArray());
        var packet = StreamPacket.Decode(new StreamPacket { Frames = [output] }.Encode());
        var recovered = Assert.Single(packet.Frames);
        Assert.Equal(output.Codec, recovered.Codec);
        Assert.Equal(output.Data.ToArray(), recovered.Data.ToArray());
    }

    [Fact]
    public async Task EncodedPipelineProcessesAudioBeforeEncoding()
    {
        var sink = new CapturingEncodedSink();
        await using var pipeline = AudioPipeline.CreateEncoded(
            new SingleFrameSource(new AudioFrame(new float[] { 0.25f, -0.5f }, 8_000, 1)),
            new RawAudioCodec(bitDepth: 32, channels: 1, sampleRate: 8_000),
            sink,
            new MultiplyFilter(2f));

        pipeline.Start();
        await pipeline.Completion;

        var frame = Assert.Single(sink.Frames);
        var decoded = new RawAudioCodec(bitDepth: 32, channels: 1, sampleRate: 8_000).Decode(frame.Data.Span);
        Assert.Equal(new float[] { 0.5f, -1f }, decoded.Samples.ToArray());
    }

    [Fact]
    public async Task EncodedPipelinePassesCancellationToItsSink()
    {
        var sink = new BlockingEncodedSink();
        await using var pipeline = AudioPipeline.CreateEncoded(
            new RepeatingSource(),
            new RawAudioCodec(bitDepth: 16, channels: 1, sampleRate: 8_000),
            sink);

        pipeline.Start();
        await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await pipeline.StopAsync();

        Assert.True(sink.WasCancelled);
        Assert.False(pipeline.IsRunning);
    }

    [Fact]
    public async Task EncodedSinkFailureRemainsVisibleThroughCompletion()
    {
        var pipeline = AudioPipeline.CreateEncoded(
            new SingleFrameSource(new AudioFrame(new float[] { 0f }, 8_000, 1)),
            new RawAudioCodec(bitDepth: 16, channels: 1, sampleRate: 8_000),
            new FaultingEncodedSink());

        pipeline.Start();
        var error = await Assert.ThrowsAsync<IOException>(async () => await pipeline.Completion);
        Assert.Equal("encoded sink failed", error.Message);
        await Assert.ThrowsAsync<IOException>(async () => await pipeline.StopAsync().AsTask());
        await pipeline.DisposeAsync();
    }

    [Fact]
    public async Task StreamSessionAdapterTransmitsTheEncodedPipelineFrameWithoutTakingOwnership()
    {
        var transport = new InMemoryTransport();
        await using var session = new StreamSession(transport, new UnusedCodecFactory());
        session.Start();
        await using (var pipeline = AudioPipeline.CreateEncoded(
            new SingleFrameSource(new AudioFrame(new float[] { 0.25f }, 8_000, 1)),
            new RawAudioCodec(bitDepth: 16, channels: 1, sampleRate: 8_000),
            new StreamSessionAudioSink(session)))
        {
            pipeline.Start();
            await pipeline.Completion;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var transmitted = await transport.Sent.Reader.ReadAsync(timeout.Token);
        var frame = Assert.Single(StreamPacket.Decode(transmitted.Span).Frames);
        Assert.Equal(CodecKind.Raw, frame.Codec);
        Assert.False(session.Completion.IsCompleted);
    }

    [Fact]
    public void ExecutedReferenceRecordsPipelineWiringAndPacketizerEnvelope()
    {
        var root = Vectors.RootElement.GetProperty("pipeline");
        var sourceHashes = Vectors.RootElement.GetProperty("reference").GetProperty("sourceSha256");
        Assert.Equal(
            "313a8abbe27735966e47b30fe55b8634e95157430e8658cf58b1f25665937293",
            sourceHashes.GetProperty("LXST/Pipeline.py").GetString());
        Assert.Equal(
            "3da8422a5c2c73f6e9c7d8e784b1e9cb307d079ebb5bf99183fc5daadfbd9701",
            sourceHashes.GetProperty("LXST/Network.py").GetString());
        Assert.True(root.GetProperty("pipelineSetsSourceBackReference").GetBoolean());
        Assert.True(root.GetProperty("pipelineAssignsSinkToSource").GetBoolean());
        Assert.True(root.GetProperty("pipelineAssignsCodecToSource").GetBoolean());
        Assert.True(root.GetProperty("pipelineAssignsSourceAndSinkToCodec").GetBoolean());
        Assert.True(root.GetProperty("startStartsSource").GetBoolean());
        Assert.True(root.GetProperty("stopStopsSource").GetBoolean());
        Assert.False(root.GetProperty("processorArgumentObserved").GetBoolean());
        Assert.Equal("00", root.GetProperty("codecMarkers").GetProperty("Raw").GetString());
        Assert.Equal("01", root.GetProperty("codecMarkers").GetProperty("Opus").GetString());
        Assert.Equal("02", root.GetProperty("codecMarkers").GetProperty("Codec2").GetString());
        Assert.True(root.GetProperty("packetizerUsesCodecMarkerAndFrameField").GetBoolean());
        Assert.False(root.GetProperty("packetCreateReceipt").GetBoolean());
    }

    private sealed class CapturingEncodedSink : IEncodedAudioSink
    {
        public List<EncodedFrame> Frames { get; } = [];

        public ValueTask WriteAsync(EncodedFrame frame, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Frames.Add(frame);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingEncodedSink : IEncodedAudioSink
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WasCancelled { get; private set; }

        public async ValueTask WriteAsync(EncodedFrame frame, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                WasCancelled = true;
                throw;
            }
        }
    }

    private sealed class FaultingEncodedSink : IEncodedAudioSink
    {
        public ValueTask WriteAsync(EncodedFrame frame, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("encoded sink failed"));
    }

    private sealed class SingleFrameSource(AudioFrame frame) : IAudioSource
    {
        public async IAsyncEnumerable<AudioFrame> CaptureAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return frame;
            await Task.CompletedTask;
        }
    }

    private sealed class RepeatingSource : IAudioSource
    {
        public async IAsyncEnumerable<AudioFrame> CaptureAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new(new float[] { 0.25f }, 8_000, 1);
                await Task.Delay(1, cancellationToken);
            }
        }
    }

    private sealed class MultiplyFilter(float gain) : AudioFilter
    {
        public override AudioFrame Process(AudioFrame frame) =>
            new(frame.Samples.ToArray().Select(sample => sample * gain).ToArray(), frame.SampleRate, frame.Channels);
    }

    private sealed class UnusedCodecFactory : IAudioCodecFactory
    {
        public IAudioCodec Create(CodecKind kind) => throw new InvalidOperationException("No inbound frames expected.");
    }

    private sealed class InMemoryTransport : IStreamTransport
    {
        private readonly Channel<ReadOnlyMemory<byte>> _incoming = Channel.CreateBounded<ReadOnlyMemory<byte>>(1);
        public Channel<ReadOnlyMemory<byte>> Sent { get; } = Channel.CreateBounded<ReadOnlyMemory<byte>>(1);
        public int MaximumPacketSize => StreamPacket.MaximumEncodedBytes;

        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default) =>
            Sent.Writer.WriteAsync(packet.ToArray(), cancellationToken);

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var packet in _incoming.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return packet;
        }
    }
}
