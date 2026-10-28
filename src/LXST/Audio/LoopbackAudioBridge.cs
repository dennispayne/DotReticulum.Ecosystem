using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace LXST.Audio;

public sealed class LoopbackAudioBridge : IAudioSource, IAudioSink, IAsyncDisposable
{
    public const int MaximumBufferedFrames = 128;

    private sealed class CaptureSession
    {
        public readonly Channel<AudioFrame> Frames = Channel.CreateBounded<AudioFrame>(
            new BoundedChannelOptions(MaximumBufferedFrames)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
        public readonly CancellationTokenSource Stop = new();
        public bool ReaderClaimed;
        public bool Stopping;
    }

    private readonly object _gate = new();
    private readonly TimeSpan _frameInterval;
    private CaptureSession? _session;
    private Task? _stopTask;
    private Task? _disposeTask;
    private bool _disposed;

    public LoopbackAudioBridge(TimeSpan? frameInterval = null)
    {
        _frameInterval = frameInterval ?? TimeSpan.FromMilliseconds(70);
        if (_frameInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(frameInterval));
    }

    public bool IsRunning
    {
        get { lock (_gate) return _session is not null && !_session.Stopping; }
    }

    public TimeSpan FrameInterval => _frameInterval;

    public int BufferedFrames
    {
        get
        {
            lock (_gate) return _session?.Frames.Reader.Count ?? 0;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopTask is not null) throw new InvalidOperationException("The loopback bridge is stopping.");
            if (_session is not null) return;
            _session = new();
        }
    }

    public bool CanReceive
    {
        get
        {
            lock (_gate)
                return _session is { Stopping: false } session && session.Frames.Reader.Count < MaximumBufferedFrames;
        }
    }

    public bool TryWrite(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not { Stopping: false } session) return false;
            foreach (var sample in frame.Samples.Span)
                if (!float.IsFinite(sample)) throw new ArgumentException("Audio samples must be finite.", nameof(frame));

            return session.Frames.Writer.TryWrite(new AudioFrame(frame.Samples.ToArray(), frame.SampleRate, frame.Channels));
        }
    }

    public ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryWrite(frame)) throw new InvalidOperationException("The loopback bridge is not running.");
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<AudioFrame> CaptureAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CaptureSession session;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            session = _session ?? throw new InvalidOperationException("Start the loopback bridge before capturing.");
            if (session.Stopping) throw new InvalidOperationException("The loopback bridge is stopping.");
            if (session.ReaderClaimed) throw new InvalidOperationException("The loopback bridge supports one capture reader.");
            session.ReaderClaimed = true;
        }

        var disposeStop = false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(session.Stop.Token, cancellationToken);
            using var timer = new PeriodicTimer(_frameInterval);
            while (await timer.WaitForNextTickAsync(linked.Token).ConfigureAwait(false))
            {
                if (session.Frames.Reader.TryRead(out var frame)) yield return frame;
                else if (session.Frames.Reader.Completion.IsCompleted) yield break;
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_session, session))
                {
                    _session = null;
                    if (!session.Stopping)
                    {
                        session.Stopping = true;
                        session.Frames.Writer.TryComplete();
                        disposeStop = true;
                    }
                }
            }

            if (disposeStop) session.Stop.Dispose();
        }
    }

    public ValueTask StopAsync()
    {
        lock (_gate)
        {
            if (_stopTask is not null) return new(_stopTask);
            var session = _session;
            if (session is null) return ValueTask.CompletedTask;
            session.Stopping = true;
            _stopTask = StopCoreAsync(session);
            return new(_stopTask);
        }
    }

    private async Task StopCoreAsync(CaptureSession session)
    {
        await Task.Yield();
        try
        {
            session.Frames.Writer.TryComplete();
            await session.Stop.CancelAsync().ConfigureAwait(false);
            lock (_gate)
                if (ReferenceEquals(_session, session)) _session = null;
        }
        finally
        {
            lock (_gate) _stopTask = null;
            session.Stop.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _disposed = true;
            _disposeTask = DisposeCoreAsync();
            return new(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        await StopAsync().ConfigureAwait(false);
    }
}
