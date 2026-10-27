using System.Threading.Channels;

namespace DotLXST.Tests;

[Trait("Category", "Unit")]
public sealed class StreamSessionTests
{
    private sealed class MemoryTransport(int capacity = 8) : IStreamTransport
    {
        public TaskCompletionSource? BlockSend { get; set; }
        public TaskCompletionSource SendEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Channel<ReadOnlyMemory<byte>> Incoming { get; } = Channel.CreateBounded<ReadOnlyMemory<byte>>(capacity);
        public List<byte[]> Sent { get; } = [];
        public int MaximumPacketSize => 400;
        public async ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SendEntered.TrySetResult();
            if (BlockSend is not null) await BlockSend.Task;
            Sent.Add(packet.ToArray());
        }
        public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default) =>
            Incoming.Reader.ReadAllAsync(cancellationToken);
    }

    private sealed class RawFactory : IAudioCodecFactory
    {
        public IAudioCodec Create(CodecKind kind) => kind == CodecKind.Raw ? new RawAudioCodec() :
            throw new NotSupportedException();
    }

    private sealed class ObservedCodec(CodecKind kind, bool failDisposal = false) : IAudioCodec
    {
        private readonly RawAudioCodec _raw = new();
        public CodecKind Kind => kind;
        public bool Disposed { get; private set; }
        public byte[] Encode(AudioFrame frame) => _raw.Encode(frame);
        public AudioFrame Decode(ReadOnlySpan<byte> data) => _raw.Decode(data);
        public void Dispose()
        {
            Disposed = true;
            _raw.Dispose();
            if (failDisposal) throw new InvalidOperationException("test decoder cleanup");
        }
    }

    private sealed class SwitchingFactory : IAudioCodecFactory
    {
        public ObservedCodec First { get; } = new(CodecKind.Raw, failDisposal: true);
        public ObservedCodec Second { get; } = new(CodecKind.Opus);
        public IAudioCodec Create(CodecKind kind) => kind == CodecKind.Raw ? First : Second;
    }

    [Fact]
    public async Task ArbitrarySignalsSurviveObserverFailureAndPreserveTelephoneIntegerFiltering()
    {
        var transport = new MemoryTransport();
        var values = new List<object?>();
        var integers = new List<long>();
        var errors = new List<string>();
        await using var session = new StreamSession(transport, new RawFactory());
        session.Error = (context, _) => errors.Add(context);
        session.SignalValueReceived += _ => throw new InvalidOperationException("test observer");
        session.SignalValueReceived += values.Add;
        session.SignalReceived += integers.Add;
        session.Start();
        await session.SendSignalAsync(new Dictionary<object, object?> { ["mute"] = true });
        await transport.Incoming.Writer.WriteAsync(new StreamPacket
        {
            Signals = [null, "remote", new object?[] { 1L, false }, ulong.MaxValue, 4L],
        }.Encode());
        transport.Incoming.Writer.Complete();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(5, values.Count);
        Assert.Null(values[0]);
        Assert.Equal("remote", values[1]);
        Assert.Equal(new object?[] { 1L, false }, Assert.IsType<object?[]>(values[2]));
        Assert.Equal(ulong.MaxValue, Assert.IsType<ulong>(values[3]));
        Assert.Equal(new long[] { 4 }, integers);
        Assert.Equal(5, errors.Count);
        var sent = Assert.IsType<Dictionary<object, object?>>(Assert.Single(
            StreamPacket.Decode(Assert.Single(transport.Sent)).Signals));
        Assert.Equal(true, sent["mute"]);
    }

    [Fact]
    public async Task CoreDecodingRunsWithoutObserversAndRecoversAfterMalformedPacket()
    {
        var transport = new MemoryTransport();
        var errors = new List<string>();
        await using var session = new StreamSession(transport, new RawFactory());
        session.Error = (context, _) => errors.Add(context);
        session.Start();
        await transport.Incoming.Writer.WriteAsync(new byte[] { 0xc1 });
        await transport.Incoming.Writer.WriteAsync(new StreamPacket { Frames = [new(CodecKind.Raw, new byte[] { 0, 0, 0 })] }.Encode());
        transport.Incoming.Writer.Complete();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, session.DecodedFrames);
        Assert.Single(errors);
    }

    [Fact]
    public async Task ThrowingObserversCannotDisableOtherObserversOrSignals()
    {
        var transport = new MemoryTransport();
        var errors = new List<string>();
        var signals = new List<long>();
        var observed = 0;
        await using var session = new StreamSession(transport, new RawFactory());
        session.Error = (context, _) => errors.Add(context);
        session.FrameReceived += _ => throw new InvalidOperationException("observer failed");
        session.FrameReceived += _ => observed++;
        session.SignalReceived += signal => signals.Add(signal);
        session.Start();
        await transport.Incoming.Writer.WriteAsync(new StreamPacket
        {
            Frames = [new(CodecKind.Raw, new byte[] { 0, 0, 0 })],
            Signals = [4, 6],
        }.Encode());
        transport.Incoming.Writer.Complete();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, observed);
        Assert.Equal(new long[] { 4, 6 }, signals);
        Assert.Single(errors);
    }

    [Fact]
    public async Task SendHonorsCancellationMduAndExplicitStart()
    {
        var transport = new MemoryTransport();
        await using var session = new StreamSession(transport, new RawFactory());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendSignalAsync(3).AsTask());
        session.Start();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SendSignalAsync(3, cancelled.Token).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => session.SendFrameAsync(new(CodecKind.Raw, new byte[400])).AsTask());
        await session.SendSignalAsync(3);
        Assert.Equal(3, Assert.IsType<long>(Assert.Single(StreamPacket.Decode(Assert.Single(transport.Sent)).Signals)));
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.SendSignalAsync(3).AsTask());
    }

    [Fact]
    public async Task IdleReceiveAndConcurrentDisposeTerminateWithoutTraffic()
    {
        var session = new StreamSession(new MemoryTransport(1), new RawFactory());
        session.Start();
        await Task.WhenAll(session.DisposeAsync().AsTask(), session.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.Completion.IsCompleted);
    }

    [Fact]
    public async Task EveryConcurrentDisposerWaitsForOutstandingSendCleanup()
    {
        var transport = new MemoryTransport { BlockSend = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var session = new StreamSession(transport, new RawFactory());
        session.Start();
        var sending = session.SendSignalAsync(3).AsTask();
        await transport.SendEntered.Task;
        var first = session.DisposeAsync().AsTask();
        var second = session.DisposeAsync().AsTask();
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        transport.BlockSend.TrySetResult();
        await Task.WhenAll(first, second, sending).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FailingPreviousDecoderCleanupDoesNotLeakReplacementOrDisableReceiving()
    {
        var factory = new SwitchingFactory();
        var transport = new MemoryTransport();
        var errors = new List<string>();
        await using var session = new StreamSession(transport, factory);
        session.Error = (context, _) => errors.Add(context);
        session.Start();
        foreach (var kind in new[] { CodecKind.Raw, CodecKind.Opus })
            await transport.Incoming.Writer.WriteAsync(new StreamPacket { Frames = [new(kind, new byte[] { 0, 0, 0 })] }.Encode());
        transport.Incoming.Writer.Complete();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, session.DecodedFrames);
        Assert.True(factory.First.Disposed);
        Assert.Single(errors);
        await session.DisposeAsync();
        Assert.True(factory.Second.Disposed);
    }
}
