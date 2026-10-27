using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotLXST;

public enum CallState
{
    Busy = 0,
    Rejected = 1,
    Calling = 2,
    Available = 3,
    Ringing = 4,
    Connecting = 5,
    Established = 6,
}

public enum TelephoneProfile
{
    UltraLowBandwidth = 0x10,
    VeryLowBandwidth = 0x20,
    LowBandwidth = 0x30,
    MediumQuality = 0x40,
    HighQuality = 0x50,
    MaximumQuality = 0x60,
    UltraLowLatency = 0x70,
    LowLatency = 0x80,
}

public sealed record TelephoneOptions
{
    public TimeSpan RingTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan WaitTimeout { get; init; } = TimeSpan.FromSeconds(70);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan? AutoAnswerDelay { get; init; }
    public TimeSpan AnnounceInterval { get; init; } = TimeSpan.FromHours(3);
    public int ReceiveCapacity { get; init; } = 32;
    public Func<Identity, bool>? AuthorizeCaller { get; init; }
    public IAudioSink? Sink { get; init; }
    public IAudioCodecFactory CodecFactory { get; init; } = new NativeAudioCodecFactory();
    public string? OpusLibraryPath { get; init; }
    public string? Codec2LibraryPath { get; init; }
}

/// <summary>Original, device-independent single-call telephony endpoint over real Reticulum links.</summary>
public sealed class TelephoneEndpoint : IAsyncDisposable
{
    private sealed class Call(Link link, ReticulumStreamTransport transport, StreamSession session, bool outgoing,
        TelephoneProfile profile)
    {
        public Link Link { get; } = link;
        public ReticulumStreamTransport Transport { get; } = transport;
        public StreamSession Session { get; } = session;
        public bool Outgoing { get; } = outgoing;
        public TelephoneProfile Profile { get; set; } = profile;
        public CancellationTokenSource Stop { get; } = new();
        public Task Deadline { get; set; } = Task.CompletedTask;
        public IAudioCodec? Encoder { get; set; }
        public TelephoneProfile? EncoderProfile { get; set; }
        public SemaphoreSlim AudioLock { get; } = new(1, 1);
    }

    private readonly TransportRouter _router;
    private readonly Identity _identity;
    private readonly TelephoneOptions _options;
    private readonly LocalDestination _destination;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _cleanup = [];
    private Call? _call;
    private CancellationTokenSource? _pendingDial;
    private bool _started;
    private bool _disposed;
    private bool _externalBusy;
    private TaskCompletionSource? _disposeCompletion;
    private Task _announcements = Task.CompletedTask;
    private CallState _state = CallState.Available;

    public TelephoneEndpoint(TransportRouter router, Identity identity, TelephoneOptions? options = null)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _options = options ?? new();
        ArgumentNullException.ThrowIfNull(_options.CodecFactory);
        if (_options.RingTimeout <= TimeSpan.Zero || _options.WaitTimeout <= TimeSpan.Zero ||
            _options.ConnectTimeout <= TimeSpan.Zero || _options.AnnounceInterval <= TimeSpan.Zero ||
            _options.AutoAnswerDelay < TimeSpan.Zero || _options.ReceiveCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        _destination = new LocalDestination(identity, Destination.Create(DestinationType.Single, "lxst", ["telephony"], identity))
        {
            AcceptLinks = true,
            ProofStrategy = ProofStrategy.None,
            LinkEstablished = AcceptLink,
        };
    }

    public byte[] DestinationHash => _destination.Destination.Hash.ToArray();
    public CallState State { get { lock (_gate) return _state; } }
    public bool IsBusy { get { lock (_gate) return _externalBusy || _state != CallState.Available; } }
    public TelephoneProfile? ActiveProfile { get { lock (_gate) return _call?.Profile; } }
    public Identity? RemoteIdentity { get { lock (_gate) return _call?.Link.RemoteIdentity; } }
    public event Action<CallState>? StateChanged;
    public event Action<AudioFrame>? AudioReceived;
    public Action<string, Exception?>? Error { get; set; }

    public void SetBusy(bool busy = true)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _externalBusy = busy;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            _router.RegisterLocalDestination(_destination);
            _started = true;
            _announcements = Task.Run(AnnounceLoopAsync);
        }
    }

    public ValueTask AnnounceAsync(CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        return _router.AnnounceAsync(_destination, cancellationToken: cancellationToken);
    }

    public async Task CallAsync(Identity remote, TelephoneProfile profile = TelephoneProfile.MediumQuality,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remote);
        EnsureStarted();
        if (!Enum.IsDefined(profile)) throw new ArgumentOutOfRangeException(nameof(profile));
        var destination = Destination.Create(DestinationType.Single, "lxst", ["telephony"], remote);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        timeout.CancelAfter(_options.WaitTimeout);
        lock (_gate)
        {
            if (_externalBusy || _call is not null || _state != CallState.Available) throw new InvalidOperationException("Telephone is busy.");
            _pendingDial = timeout;
            _state = CallState.Calling;
        }
        Notify(StateChanged, CallState.Calling);
        try
        {
            if (!_router.HasPath(destination.Hash))
                await _router.RequestPathAsync(destination.Hash, cancellationToken: timeout.Token).ConfigureAwait(false);
            while (!_router.HasPath(destination.Hash)) await Task.Delay(25, timeout.Token).ConfigureAwait(false);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                timeout.Token.ThrowIfCancellationRequested();
                if (_state != CallState.Calling || !ReferenceEquals(_pendingDial, timeout))
                    throw new OperationCanceledException("Call was cancelled.");
                var link = _router.Links.Connect(remote, destination.Hash, establishmentTimeout: _options.ConnectTimeout.TotalSeconds);
                _call = Attach(link, true, profile);
                _call.Deadline = WatchTimeoutAsync(_call, _options.WaitTimeout);
                _pendingDial = null;
            }
        }
        catch
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pendingDial, timeout)) Hangup();
            }
            throw;
        }
    }

    private Call Attach(Link link, bool outgoing, TelephoneProfile profile)
    {
        var transport = new ReticulumStreamTransport(link, _options.ReceiveCapacity) { Error = message => Report(message, null) };
        var session = new StreamSession(transport, _options.CodecFactory, _options.Sink) { Error = Report };
        var call = new Call(link, transport, session, outgoing, profile);
        session.SignalReceived += signal => ReceiveSignal(call, signal);
        session.FrameReceived += frame => Notify(AudioReceived, frame);
        link.Closed += _ => CloseCall(call);
        session.Start();
        return call;
    }

    private void AcceptLink(Link link)
    {
        Call? call = null;
        lock (_gate)
        {
            if (!_disposed && _started && !_externalBusy && _call is null && _state == CallState.Available)
            {
                call = Attach(link, false, TelephoneProfile.MediumQuality);
                _call = call;
                link.RemoteIdentified += (_, identity) => CallerIdentified(call, identity);
                call.Deadline = WatchTimeoutAsync(call, _options.RingTimeout);
            }
        }
        if (call is null)
        {
            try { link.Send(new StreamPacket { Signals = [(long)CallState.Busy] }.Encode(), createReceipt: false); }
            finally { link.Teardown(); }
        }
        else SendSignal(call, (long)CallState.Available);
    }

    private void CallerIdentified(Call call, Identity identity)
    {
        var allowed = false;
        try { allowed = _options.AuthorizeCaller?.Invoke(identity) ?? true; }
        catch (Exception ex) { Report("LXST caller authorization failed.", ex); }
        lock (_gate)
        {
            if (!ReferenceEquals(_call, call)) return;
            if (!allowed)
            {
                SendSignal(call, (long)CallState.Busy);
                call.Link.Teardown();
                CloseCall(call);
                return;
            }
            SetState(CallState.Ringing);
            SendSignal(call, (long)CallState.Ringing);
            if (_options.AutoAnswerDelay is { } delay) TrackCleanup(AutoAnswerAsync(call, delay));
        }
    }

    private async Task AutoAnswerAsync(Call call, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, call.Stop.Token).ConfigureAwait(false);
            await AnswerAsync(call.Stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (call.Stop.IsCancellationRequested) { }
        catch (Exception ex) { Report("LXST automatic answer failed.", ex); }
    }

    public async ValueTask AnswerAsync(CancellationToken cancellationToken = default)
    {
        Call call;
        lock (_gate)
        {
            EnsureStarted();
            call = _call ?? throw new InvalidOperationException("No incoming call.");
            if (call.Outgoing || call.Link.RemoteIdentity is null || _state != CallState.Ringing)
                throw new InvalidOperationException("No identified ringing incoming call.");
            SetState(CallState.Connecting);
        }
        await call.Session.SendSignalAsync((long)CallState.Connecting, cancellationToken).ConfigureAwait(false);
        await call.Session.SendSignalAsync((long)CallState.Established, cancellationToken).ConfigureAwait(false);
        lock (_gate) if (ReferenceEquals(_call, call)) SetState(CallState.Established);
    }

    private void ReceiveSignal(Call call, long signal)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_call, call)) return;
            if (signal >= 255)
            {
                var profile = (TelephoneProfile)(signal - 255);
                if (Enum.IsDefined(profile)) call.Profile = profile;
                return;
            }
            if (!call.Outgoing && _state < CallState.Connecting) return;
            switch ((CallState)signal)
            {
                case CallState.Busy:
                case CallState.Rejected:
                    SetState((CallState)signal);
                    call.Link.Teardown();
                    CloseCall(call);
                    break;
                case CallState.Available when call.Outgoing:
                    call.Link.Identify(_identity);
                    break;
                case CallState.Ringing when call.Outgoing:
                    SetState(CallState.Ringing);
                    SendSignal(call, 255 + (long)call.Profile);
                    break;
                case CallState.Connecting when call.Outgoing:
                    SetState(CallState.Connecting);
                    break;
                case CallState.Established when call.Outgoing:
                    SetState(CallState.Established);
                    break;
            }
        }
    }

    public ValueTask SwitchProfileAsync(TelephoneProfile profile, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(profile)) throw new ArgumentOutOfRangeException(nameof(profile));
        lock (_gate)
        {
            EnsureStarted();
            var call = _call ?? throw new InvalidOperationException("No active call.");
            call.Profile = profile;
            return call.Session.SendSignalAsync(255 + (long)profile, cancellationToken);
        }
    }

    public async ValueTask SendAudioAsync(AudioFrame frame, CancellationToken cancellationToken = default)
    {
        Call call;
        lock (_gate)
        {
            EnsureStarted();
            call = _call ?? throw new InvalidOperationException("No active call.");
            if (_state != CallState.Established) throw new InvalidOperationException("Call is not established.");
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, call.Stop.Token);
        await call.AudioLock.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            TelephoneProfile profile;
            lock (_gate) profile = call.Profile;
            if (call.Encoder is null || call.EncoderProfile != profile)
            {
                var replacement = CreateEncoder(profile);
                call.Encoder?.Dispose();
                call.Encoder = replacement;
                call.EncoderProfile = profile;
            }
            await call.Session.SendFrameAsync(new(call.Encoder.Kind, call.Encoder.Encode(frame)), linked.Token).ConfigureAwait(false);
        }
        finally { call.AudioLock.Release(); }
    }

    private IAudioCodec CreateEncoder(TelephoneProfile profile) => profile switch
    {
        TelephoneProfile.UltraLowBandwidth => new NativeCodec2(700, _options.Codec2LibraryPath),
        TelephoneProfile.VeryLowBandwidth => new NativeCodec2(1600, _options.Codec2LibraryPath),
        TelephoneProfile.LowBandwidth => new NativeCodec2(3200, _options.Codec2LibraryPath),
        TelephoneProfile.HighQuality => new NativeOpusCodec(OpusProfile.VoiceHigh, _options.OpusLibraryPath),
        TelephoneProfile.MaximumQuality => new NativeOpusCodec(OpusProfile.VoiceMaximum, _options.OpusLibraryPath),
        _ => new NativeOpusCodec(OpusProfile.VoiceMedium, _options.OpusLibraryPath),
    };

    private void SendSignal(Call call, long signal)
    {
        TrackCleanup(SendSignalObservedAsync(call, signal));
    }

    private async Task SendSignalObservedAsync(Call call, long signal)
    {
        try { await call.Session.SendSignalAsync(signal, call.Stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (call.Stop.IsCancellationRequested) { }
        catch (Exception ex) { Report("LXST signalling send failed.", ex); CloseCall(call); }
    }

    private async Task WatchTimeoutAsync(Call call, TimeSpan timeout)
    {
        try
        {
            await Task.Delay(timeout, call.Stop.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (ReferenceEquals(_call, call) && _state != CallState.Established)
                {
                    call.Link.Teardown();
                    CloseCall(call);
                }
            }
        }
        catch (OperationCanceledException) when (call.Stop.IsCancellationRequested) { }
    }

    public void Hangup()
    {
        lock (_gate)
        {
            var pending = _pendingDial;
            _pendingDial = null;
            pending?.Cancel();
            if (_call is { } call)
            {
                call.Link.Teardown();
                CloseCall(call);
            }
            else if (_state != CallState.Available) SetState(CallState.Available);
        }
    }

    private void CloseCall(Call call)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_call, call)) return;
            _call = null;
            call.Stop.Cancel();
            SetState(CallState.Available);
            TrackCleanup(CleanupCallAsync(call));
        }
    }

    private void TrackCleanup(Task task)
    {
        var observed = ObserveCleanupAsync(task);
        lock (_gate)
        {
            _cleanup.Add(observed);
            _cleanup.RemoveAll(value => value.IsCompleted);
        }
    }

    private async Task ObserveCleanupAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception ex) { Report("LXST call cleanup failed.", ex); }
    }

    private static async Task CleanupCallAsync(Call call)
    {
        await call.Session.DisposeAsync().ConfigureAwait(false);
        call.Transport.Dispose();
        await call.AudioLock.WaitAsync().ConfigureAwait(false);
        try { call.Encoder?.Dispose(); }
        finally { call.AudioLock.Release(); }
        await call.Deadline.ConfigureAwait(false);
    }

    private void SetState(CallState state)
    {
        _state = state;
        Notify(StateChanged, state);
    }

    private void Notify<T>(Action<T>? handlers, T value)
    {
        if (handlers is null) return;
        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            try { handler(value); }
            catch (Exception ex) { Report("LXST telephone observer failed.", ex); }
        }
    }

    private void Report(string context, Exception? error)
    {
        try
        {
            if (Error is { } report) report(context, error);
            else System.Diagnostics.Trace.TraceError($"{context} {error}");
        }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError($"LXST error observer failed: {ex}; {context} {error}"); }
    }

    private void EnsureStarted()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_started) throw new InvalidOperationException("Start the telephone first.");
        }
    }

    private async Task AnnounceLoopAsync()
    {
        try
        {
            await _router.AnnounceAsync(_destination, cancellationToken: _stop.Token).ConfigureAwait(false);
            while (true)
            {
                await Task.Delay(_options.AnnounceInterval, _stop.Token).ConfigureAwait(false);
                await _router.AnnounceAsync(_destination, cancellationToken: _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { Report("LXST periodic announcement failed.", ex); }
    }

    public ValueTask DisposeAsync()
    {
        Task[] cleanup;
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_disposeCompletion is not null) return new(_disposeCompletion.Task);
            completion = _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposed = true;
            Hangup();
            if (_started) _router.DeregisterLocalDestination(_destination);
            _stop.Cancel();
            cleanup = _cleanup.ToArray();
        }
        _ = CompleteDisposalAsync(cleanup, completion);
        return new(completion.Task);
    }

    private async Task CompleteDisposalAsync(Task[] cleanup, TaskCompletionSource completion)
    {
        try
        {
            await _announcements.ConfigureAwait(false);
            await Task.WhenAll(cleanup).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception ex) { completion.TrySetException(ex); }
    }
}
