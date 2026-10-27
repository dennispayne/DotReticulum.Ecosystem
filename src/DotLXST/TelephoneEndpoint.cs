using DotReticulum.Core;
using DotReticulum.Transport;
using System.Diagnostics;
using DotLXST.Audio;

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
    public TelephoneCallerPolicy CallerPolicy { get; init; } = new();
    public float ReceiveGainDb { get; init; }
    public float TransmitGainDb { get; init; }
    public bool UseAutomaticGainControl { get; init; } = true;
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
        public BandPassFilter TransmitFilter { get; } = new(250, 8500);
        public AutomaticGainControl TransmitAgc { get; } = new(targetLevelDb: -15);
        public bool UseAutomaticGainControl { get; set; }
    }

    private sealed class ReceiveSink(TelephoneEndpoint endpoint) : IAudioSink
    {
        public Call Call { get; set; } = null!;
        public ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken = default) =>
            endpoint.ReceiveAudioAsync(Call, frame, cancellationToken);
    }

    private readonly TransportRouter _router;
    private readonly Identity _identity;
    private readonly TelephoneOptions _options;
    private readonly LocalDestination _destination;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _cleanup = [];
    private TelephoneCallerPolicy _callerPolicy;
    private Call? _call;
    private CancellationTokenSource? _pendingDial;
    private bool _started;
    private bool _disposed;
    private bool _externalBusy;
    private float _receiveGainDb;
    private float _transmitGainDb;
    private bool _receiveMuted;
    private bool _transmitMuted;
    private bool _useAutomaticGainControl;
    private TimeSpan _connectTimeout;
    private TaskCompletionSource? _disposeCompletion;
    private Task _announcements = Task.CompletedTask;
    private CallState _state = CallState.Available;

    public TelephoneEndpoint(TransportRouter router, Identity identity, TelephoneOptions? options = null)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _options = options ?? new();
        ArgumentNullException.ThrowIfNull(_options.CodecFactory);
        ArgumentNullException.ThrowIfNull(_options.CallerPolicy);
        _callerPolicy = _options.CallerPolicy;
        if (!float.IsFinite(_options.ReceiveGainDb) || !float.IsFinite(_options.TransmitGainDb))
            throw new ArgumentOutOfRangeException(nameof(options));
        _receiveGainDb = _options.ReceiveGainDb;
        _transmitGainDb = _options.TransmitGainDb;
        _useAutomaticGainControl = _options.UseAutomaticGainControl;
        _connectTimeout = _options.ConnectTimeout;
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
    public bool IsIncomingCall { get { lock (_gate) return _call is { Outgoing: false }; } }
    /// <summary>Changes future outgoing establishment deadlines; an already scheduled deadline is not rewritten.</summary>
    public TimeSpan ConnectTimeout
    {
        get { lock (_gate) return _connectTimeout; }
        set { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _connectTimeout = value; } }
    }
    public float ReceiveGainDb
    {
        get { lock (_gate) return _receiveGainDb; }
        set { SetGain(value, receive: true); }
    }
    public float TransmitGainDb
    {
        get { lock (_gate) return _transmitGainDb; }
        set { SetGain(value, receive: false); }
    }
    public bool ReceiveMuted
    {
        get { lock (_gate) return _call is not null && _receiveMuted; }
        set { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _receiveMuted = value; } }
    }
    public bool TransmitMuted
    {
        get { lock (_gate) return _call is not null && _transmitMuted; }
        set { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _transmitMuted = value; } }
    }
    /// <summary>Controls filter construction for subsequent calls, like the reference setter.</summary>
    public bool UseAutomaticGainControl
    {
        get { lock (_gate) return _useAutomaticGainControl; }
        set { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _useAutomaticGainControl = value; } }
    }
    public event Action<CallState>? StateChanged;
    public event Action<AudioFrame>? AudioReceived;
    public Action<string, Exception?>? Error { get; set; }

    public void SetCallerPolicy(TelephoneCallerPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _callerPolicy = policy;
        }
    }

    private void SetGain(float value, bool receive)
    {
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (receive) _receiveGainDb = value;
            else _transmitGainDb = value;
        }
    }

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
        var started = Stopwatch.GetTimestamp();
        TimeSpan connectTimeout;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        timeout.CancelAfter(_options.WaitTimeout);
        lock (_gate)
        {
            if (_externalBusy || _call is not null || _state != CallState.Available) throw new InvalidOperationException("Telephone is busy.");
            _pendingDial = timeout;
            _state = CallState.Calling;
            connectTimeout = _connectTimeout;
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
                var link = _router.Links.Connect(remote, destination.Hash,
                    establishmentTimeout: Math.Max(.001, connectTimeout.TotalSeconds));
                var call = _call = Attach(link, true, profile);
                var elapsed = Stopwatch.GetElapsedTime(started);
                call.Deadline = Task.WhenAll(
                    WatchTimeoutAsync(call, Remaining(_options.WaitTimeout, elapsed)),
                    WatchTimeoutAsync(call, Remaining(connectTimeout, elapsed), CallState.Ringing));
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
        var sink = new ReceiveSink(this);
        var session = new StreamSession(transport, _options.CodecFactory, sink) { Error = Report };
        var call = new Call(link, transport, session, outgoing, profile) { UseAutomaticGainControl = _useAutomaticGainControl };
        sink.Call = call;
        session.SignalReceived += signal => ReceiveSignal(call, signal);
        link.Closed += _ => TrackCleanup(CloseAfterIngressAsync(call));
        session.Start();
        return call;
    }

    private async ValueTask ReceiveAudioAsync(Call call, AudioFrame frame, CancellationToken cancellationToken)
    {
        float gain;
        bool muted;
        lock (_gate)
        {
            if (!ReferenceEquals(_call, call)) return;
            gain = _receiveGainDb;
            muted = _receiveMuted;
        }
        var output = ApplyMixerGain(frame, gain, muted);
        if (_options.Sink is not null)
        {
            try { await _options.Sink.WriteAsync(output, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { Report("LXST audio sink failed.", ex); }
        }
        Notify(AudioReceived, output);
    }

    private static AudioFrame ApplyMixerGain(AudioFrame frame, float gain, bool muted)
    {
        var amplified = AudioMixer.ApplyGain(frame, gain, muted);
        var samples = amplified.Samples.ToArray();
        for (var index = 0; index < samples.Length; index++) samples[index] = Math.Clamp(samples[index], -1, 1);
        return new(samples, frame.SampleRate, frame.Channels);
    }

    private async Task CloseAfterIngressAsync(Call call)
    {
        await call.Session.Completion.ConfigureAwait(false);
        CloseCall(call);
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
                call.Deadline = WatchTimeoutAsync(call, _options.ConnectTimeout, CallState.Ringing);
            }
        }
        if (call is null)
        {
            try { SendFinalSignal(link, CallState.Busy); }
            finally { link.Teardown(); }
        }
        else SendSignal(call, (long)CallState.Available);
    }

    private void CallerIdentified(Call call, Identity identity)
    {
        var allowed = false;
        try { allowed = Volatile.Read(ref _callerPolicy).IsAllowed(identity) && (_options.AuthorizeCaller?.Invoke(identity) ?? true); }
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
            if (_options.AutoAnswerDelay is { } delay && delay > TimeSpan.Zero) TrackCleanup(AutoAnswerAsync(call, delay));
            else call.Deadline = Task.WhenAll(call.Deadline, WatchTimeoutAsync(call, _options.RingTimeout));
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
            float gain;
            bool muted;
            lock (_gate)
            {
                profile = call.Profile;
                gain = _transmitGainDb;
                muted = _transmitMuted;
            }
            var filtered = call.TransmitFilter.Process(frame);
            if (call.UseAutomaticGainControl) filtered = call.TransmitAgc.Process(filtered);
            var processed = ApplyMixerGain(filtered, gain, muted);
            if (call.Encoder is null || call.EncoderProfile != profile)
            {
                var replacement = CreateEncoder(profile);
                var previous = call.Encoder;
                call.Encoder = replacement;
                call.EncoderProfile = profile;
                try { previous?.Dispose(); }
                catch (Exception ex) { Report("LXST previous encoder cleanup failed.", ex); }
            }
            await call.Session.SendFrameAsync(new(call.Encoder.Kind, call.Encoder.Encode(processed)), linked.Token).ConfigureAwait(false);
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

    private static TimeSpan Remaining(TimeSpan limit, TimeSpan elapsed) =>
        elapsed >= limit ? TimeSpan.Zero : limit - elapsed;

    private async Task WatchTimeoutAsync(Call call, TimeSpan timeout, CallState before = CallState.Established)
    {
        try
        {
            await Task.Delay(timeout <= TimeSpan.Zero ? TimeSpan.Zero : timeout, call.Stop.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (ReferenceEquals(_call, call) && _state < before)
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
                if (!call.Outgoing && _state == CallState.Ringing)
                    SendFinalSignal(call.Link, CallState.Rejected);
                call.Link.Teardown();
                CloseCall(call);
            }
            else if (_state != CallState.Available) SetState(CallState.Available);
        }
    }

    private void SendFinalSignal(Link link, CallState signal)
    {
        if (!link.TrySend(new StreamPacket { Signals = [(long)signal] }.Encode()))
            Report("LXST final status could not be enqueued.", null);
    }

    private void CloseCall(Call call)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_call, call)) return;
            _call = null;
            _receiveMuted = false;
            _transmitMuted = false;
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
        if (state == CallState.Established && _state != CallState.Established && _call is { } call)
            call.UseAutomaticGainControl = _useAutomaticGainControl;
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
