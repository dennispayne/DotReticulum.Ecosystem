using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DotReticulum.Transport;

namespace DotLXST;

/// <summary>Packet-oriented boundary; callers retain transport ownership.</summary>
public interface IStreamTransport
{
    int MaximumPacketSize { get; }
    ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default);
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default);
}

public sealed class NativeAudioCodecFactory : IAudioCodecFactory
{
    public string? OpusLibraryPath { get; init; }
    public string? Codec2LibraryPath { get; init; }
    public int OutputSampleRate { get; init; } = 48_000;
    public int OutputChannels { get; init; } = 2;

    public IAudioCodec Create(CodecKind kind) => kind switch
    {
        CodecKind.Raw => new RawAudioCodec(sampleRate: OutputSampleRate),
        CodecKind.Opus => new NativeOpusCodec(libraryPath: OpusLibraryPath,
            decodeSampleRate: OutputSampleRate, decodeChannels: OutputChannels),
        CodecKind.Codec2 => new NativeCodec2(libraryPath: Codec2LibraryPath),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

/// <summary>One ordered receive worker; the transport and optional sink are externally owned.</summary>
public sealed class StreamSession : IAsyncDisposable
{
    private readonly IStreamTransport _transport;
    private readonly IAudioCodecFactory _codecs;
    private readonly IAudioSink? _sink;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly object _gate = new();
    private IAudioCodec? _decoder;
    private Task _worker = Task.CompletedTask;
    private bool _started;
    private bool _disposed;
    private TaskCompletionSource? _disposeCompletion;
    private long _decodedFrames;

    public StreamSession(IStreamTransport transport, IAudioCodecFactory codecs, IAudioSink? sink = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _codecs = codecs ?? throw new ArgumentNullException(nameof(codecs));
        _sink = sink;
    }

    public event Action<long>? SignalReceived;
    public event Action<AudioFrame>? FrameReceived;
    public Action<string, Exception?>? Error { get; set; }
    public long DecodedFrames => Interlocked.Read(ref _decodedFrames);
    public Task Completion { get { lock (_gate) return _worker; } }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            _started = true;
            _worker = Task.Run(ReceiveAsync);
        }
    }

    public ValueTask SendSignalAsync(long signal, CancellationToken cancellationToken = default) =>
        SendPacketAsync(new() { Signals = [signal] }, cancellationToken);

    public ValueTask SendFrameAsync(EncodedFrame frame, CancellationToken cancellationToken = default) =>
        SendPacketAsync(new() { Frames = [frame] }, cancellationToken);

    public async ValueTask SendPacketAsync(StreamPacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_started) throw new InvalidOperationException("Start the stream before sending.");
        }
        var data = packet.Encode();
        if (data.Length > _transport.MaximumPacketSize) throw new ArgumentException("Frame exceeds the link packet budget; LXST does not fragment it.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        await _send.WaitAsync(linked.Token).ConfigureAwait(false);
        try { await _transport.SendAsync(data, linked.Token).ConfigureAwait(false); }
        finally { _send.Release(); }
    }

    private async Task ReceiveAsync()
    {
        try
        {
            await foreach (var bytes in _transport.ReceiveAsync(_stop.Token).ConfigureAwait(false))
            {
                try
                {
                    if (bytes.Length > _transport.MaximumPacketSize) throw new FormatException("Incoming stream packet exceeds transport budget.");
                    var packet = StreamPacket.Decode(bytes.Span);
                    foreach (var frame in packet.Frames)
                    {
                        if (_decoder is null || _decoder.Kind != frame.Codec)
                        {
                            var replacement = _codecs.Create(frame.Codec);
                            var previous = _decoder;
                            _decoder = replacement;
                            try { previous?.Dispose(); }
                            catch (Exception ex) { Report("LXST previous decoder cleanup failed.", ex); }
                        }
                        var decoded = _decoder.Decode(frame.Data.Span);
                        Interlocked.Increment(ref _decodedFrames);
                        if (_sink is not null)
                        {
                            try { await _sink.WriteAsync(decoded, _stop.Token).ConfigureAwait(false); }
                            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
                            catch (Exception ex) { Report("LXST audio sink failed.", ex); }
                        }
                        Notify(FrameReceived, decoded, "frame observer");
                    }
                    foreach (var signal in packet.Signals) Notify(SignalReceived, signal, "signalling observer");
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception ex) { Report("LXST packet processing failed.", ex); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { Report("LXST receive transport failed.", ex); }
    }

    private void Notify<T>(Action<T>? handlers, T value, string context)
    {
        if (handlers is null) return;
        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            try { handler(value); }
            catch (Exception ex) { Report($"LXST {context} failed.", ex); }
        }
    }

    private void Report(string context, Exception? error)
    {
        if (Error is { } report)
        {
            try { report(context, error); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError($"{context} {error}; LXST error observer failed: {ex}"); }
        }
        else System.Diagnostics.Trace.TraceError($"{context} {error}");
    }

    public ValueTask DisposeAsync()
    {
        Task worker;
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_disposeCompletion is not null) return new(_disposeCompletion.Task);
            completion = _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposed = true;
            worker = _worker;
        }
        _ = CompleteDisposalAsync(worker, completion);
        return new(completion.Task);
    }

    private async Task CompleteDisposalAsync(Task worker, TaskCompletionSource completion)
    {
        try
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            await worker.ConfigureAwait(false);
            await _send.WaitAsync().ConfigureAwait(false);
            try { _decoder?.Dispose(); }
            finally { _send.Release(); }
            // Synchronization primitives remain usable by senders unwinding cancellation.
            completion.TrySetResult();
        }
        catch (Exception ex) { completion.TrySetException(ex); }
    }
}

/// <summary>A bounded, non-owning adapter over a dedicated Reticulum link with no other core receiver.</summary>
public sealed class ReticulumStreamTransport : IStreamTransport, IDisposable
{
    private static readonly object OwnershipGate = new();
    private static readonly ConditionalWeakTable<Link, ReticulumStreamTransport> Owners = new();
    private readonly Link _link;
    private readonly Channel<ReadOnlyMemory<byte>> _incoming;
    private readonly Action<byte[], DotReticulum.Core.Packet> _handler;
    private readonly Action<Link> _closedHandler;
    private int _disposed;
    private long _dropped;
    private long _unreportedDrops;

    public ReticulumStreamTransport(Link link, int receiveCapacity = 32)
    {
        _link = link ?? throw new ArgumentNullException(nameof(link));
        ArgumentOutOfRangeException.ThrowIfLessThan(receiveCapacity, 1);
        _incoming = System.Threading.Channels.Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(receiveCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _handler = (data, _) =>
        {
            if (!_incoming.Writer.TryWrite(data.ToArray()))
            {
                Interlocked.Increment(ref _dropped);
                Interlocked.Increment(ref _unreportedDrops);
            }
        };
        _closedHandler = _ => _incoming.Writer.TryComplete();
        lock (OwnershipGate)
        {
            if (Owners.TryGetValue(_link, out _))
                throw new InvalidOperationException("The link already has an LXST stream adapter.");
            Owners.Add(_link, this);
            try { _link.SetPacketReceiver(_handler); }
            catch
            {
                Owners.Remove(_link);
                throw;
            }
            _link.Closed += _closedHandler;
        }
    }

    public Action<string>? Error { get; set; }
    public long DroppedPackets => Interlocked.Read(ref _dropped);
    public int MaximumPacketSize => Math.Min(_link.Mdu, StreamPacket.MaximumEncodedBytes);

    public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_link.Status != LinkStatus.Active) throw new IOException("LXST link is not active.");
        if (packet.Length > MaximumPacketSize) throw new ArgumentException("Packet exceeds link MDU.", nameof(packet));
        if (!_link.TrySend(packet.Span))
        {
            Report("LXST stack rejected the outbound packet.");
            throw new IOException("LXST stack rejected the outbound packet.");
        }
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var packet in _incoming.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            ReportDroppedPackets();
            yield return packet;
        }
        ReportDroppedPackets();
    }

    private void ReportDroppedPackets()
    {
        var dropped = Interlocked.Exchange(ref _unreportedDrops, 0);
        if (dropped != 0) Report($"LXST receive queue full or closed; {dropped} packet(s) discarded.");
    }

    private void Report(string message)
    {
        try { Error?.Invoke(message); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError($"{message} LXST error observer failed: {ex}"); }
        if (Error is null) System.Diagnostics.Trace.TraceError(message);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (OwnershipGate)
        {
            _link.SetPacketReceiver(null);
            _link.Closed -= _closedHandler;
            Owners.Remove(_link);
        }
        _incoming.Writer.TryComplete();
        ReportDroppedPackets();
    }
}
