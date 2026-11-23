using System.Diagnostics;
using DotReticulum.Core;
using DotReticulum.Transport;
using LXST;
using LXST.Devices;

namespace Sideband;

/// <summary>
/// Original Sideband voice coordinator. Router, identity and device backend remain caller-owned.
/// Start registers lxst.telephony; call audio is acceptance-gated. Incoming notifications
/// use an original synthesized ringtone, not upstream audio assets.
/// </summary>
public sealed class SidebandVoiceService : IAsyncDisposable
{
    public static readonly TimeSpan RingTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PathTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan CallLogRetention = TimeSpan.FromDays(14);
    public static readonly TimeSpan RingerDelay = TimeSpan.FromMilliseconds(500);

    private sealed class AcceptedSink(SidebandVoiceService owner) : IAudioSink
    {
        public async ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken = default)
        {
            if (owner._telephone.State != CallState.Established) return;
            try { await owner._output.WriteAsync(frame, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                owner.Report("Voice playback failed.", exception);
                owner._telephone.Hangup();
            }
        }
    }

    private readonly object _gate = new();
    private readonly TransportRouter _router;
    private readonly IAudioDeviceBackend _devices;
    private readonly TelephoneEndpoint _telephone;
    private readonly TelephoneAudioOutput _output;
    private readonly SidebandVoiceRinger _ringer;
    private readonly CancellationTokenSource _stop = new();
    private SidebandVoicePolicy _policy;
    private SidebandVoiceState _state;
    private string? _remote;
    private bool _incoming;
    private bool _started;
    private bool _disposed;
    private bool _callActive;
    private long _generation;
    private CancellationTokenSource? _callStop;
    private Task _capture = Task.CompletedTask;
    private Task _autoAnswer = Task.CompletedTask;
    private Task _ending = Task.CompletedTask;
    private long _endingGeneration;
    private Task _dial = Task.CompletedTask;
    private Task? _disposal;

    public SidebandVoiceService(TransportRouter router, Identity identity, IAudioDeviceBackend devices,
        SidebandVoicePolicy? policy = null)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        ArgumentNullException.ThrowIfNull(identity);
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _policy = policy ?? new();
        var config = _policy.Configuration;
        _output = new(devices, config.Speaker, config.Loudspeaker, bufferOutput: true) { Error = Report };
        _ringer = new(devices, Report);
        _telephone = new(router, identity, new()
        {
            RingTimeout = RingTimeout,
            WaitTimeout = WaitTimeout,
            AuthorizeCaller = AuthorizeCaller,
            Sink = new AcceptedSink(this),
            OutputControl = _output,
        }) { Error = Report };
        _telephone.StateChanged += OnTelephoneState;
        _telephone.CallEnded += OnCallEnded;
        _telephone.ProfileChanged += OnProfileChanged;
        _telephone.ModeChanged += _ => Publish();
        _telephone.LoudspeakerChanged += _ => Publish();
    }

    public byte[] DestinationHash => _telephone.DestinationHash;
    public SidebandVoicePolicy Policy => Volatile.Read(ref _policy);
    public bool IsBusy
    {
        get { var telephoneBusy = _telephone.IsBusy;         lock (_gate) return telephoneBusy || _callActive || !_dial.IsCompleted; }
    }

    public SidebandVoiceSnapshot Snapshot
    {
        get
        {
            // Never take the endpoint lock while holding _gate: endpoint callbacks take _gate.
            var profile = _telephone.ActiveProfile;
            var mode = _telephone.ActiveMode;
            var transmitMuted = _telephone.TransmitMuted;
            var receiveMuted = _telephone.ReceiveMuted;
            var squelched = _telephone.TransmitSquelched;
            var loudspeaker = _telephone.LoudspeakerEnabled;
            var established = _telephone.EstablishedAt;
            var received = _telephone.ReceivedFrames;
            var transmitted = _telephone.TransmittedFrames;
            lock (_gate)
                return new(_state, _started && !_disposed, _incoming, _remote, profile, mode,
                    transmitMuted, receiveMuted, squelched, loudspeaker, established, received, transmitted);
        }
    }

    public event Action<SidebandVoiceSnapshot>? StateChanged;
    public event Action<SidebandVoiceCallEvent>? CallHistory;
    public event Action<SidebandVoiceError>? Error;

    private bool AuthorizeCaller(Identity peer)
    {
        if (!Policy.IsAllowed(peer.HexHash)) return false;
        lock (_gate) return !_disposed && !_callActive && _dial.IsCompleted;
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            _started = true;
        }
        try { _telephone.Start(); }
        catch { lock (_gate) _started = false; throw; }
        Publish();
    }

    public ValueTask AnnounceAsync(CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        return _telephone.AnnounceAsync(cancellationToken);
    }

    public ValueTask<IReadOnlyList<AudioDeviceInfo>> ListInputsAsync(CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return _devices.ListInputsAsync(cancellationToken);
    }

    public ValueTask<IReadOnlyList<AudioDeviceInfo>> ListOutputsAsync(CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return _devices.ListOutputsAsync(cancellationToken);
    }

    /// <summary>Replaces authorization immediately; existing accepted calls are not retroactively rejected.</summary>
    public async ValueTask ConfigureAsync(SidebandVoicePolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        EnsureNotDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(ref _policy, policy);
        await _ringer.StopAsync().ConfigureAwait(false);
        _output.SpeakerDevice = policy.Configuration.Speaker;
        _output.LoudspeakerDevice = policy.Configuration.Loudspeaker;
        await _output.RefreshDeviceAsync(cancellationToken).ConfigureAwait(false);
        var ringing = _telephone.IsIncomingCall && _telephone.State == CallState.Ringing;
        lock (_gate)
            if (ringing && !_disposed && _callActive && _state == SidebandVoiceState.Ringing &&
                _endingGeneration != _generation)
                _ringer.Start(Policy.Configuration.Ringer, _callStop!.Token);
        Publish();
    }

    public Task CallAsync(Identity remote, TelephoneProfile profile = TelephoneProfile.MediumQuality,
        TelephoneMode mode = TelephoneMode.FullDuplex, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remote);
        EnsureStarted();
        if (!Enum.IsDefined(profile)) throw new ArgumentOutOfRangeException(nameof(profile));
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_callActive || !_dial.IsCompleted) throw new InvalidOperationException("Voice is busy.");
            var generation = BeginCall(remote.HexHash, incoming: false);
            var token = _callStop!.Token;
            // Register the exact public task before endpoint work can take its own lock.
            return _dial = Task.Run(() => CallCoreAsync(remote, profile, mode, generation, token, cancellationToken));
        }
    }

    private async Task CallCoreAsync(Identity remote, TelephoneProfile profile, TelephoneMode mode,
        long generation, CancellationToken token, CancellationToken cancellationToken)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
        try
        {
            // The upstream UI requests a path before ReticulumTelephone.dial; no call log is
            // produced for a path timeout. Keep that separate from the telephone wait budget.
            var destination = Destination.Create(DestinationType.Single, "lxst", ["telephony"], remote);
            if (!_router.HasPath(destination.Hash))
            {
                _telephone.SetBusy();
                try
                {
                    using var path = CancellationTokenSource.CreateLinkedTokenSource(request.Token);
                    path.CancelAfter(PathTimeout);
                    await _router.RequestPathAsync(destination.Hash, cancellationToken: path.Token).ConfigureAwait(false);
                    while (!_router.HasPath(destination.Hash))
                        await Task.Delay(25, path.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!request.IsCancellationRequested)
                {
                    throw new TimeoutException("Voice path request timed out.");
                }
                finally { _telephone.SetBusy(false); }
            }
            request.Token.ThrowIfCancellationRequested();
            if (mode == TelephoneMode.HalfDuplex && Policy.Configuration.HalfDuplexLoudspeaker)
                await _telephone.EnableLoudspeakerAsync(cancellationToken: request.Token).ConfigureAwait(false);
            await _telephone.CallAsync(remote, profile, request.Token, mode).ConfigureAwait(false);
        }
        catch
        {
            await _telephone.HangupAsync().ConfigureAwait(false);
            Task ending;
            lock (_gate) ending = _ending;
            await ending.ConfigureAwait(false);
            ResetUnattachedCall(generation);
            throw;
        }
    }

    public ValueTask AnswerAsync(CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        return AnswerCoreAsync(cancellationToken);
    }

    private async ValueTask AnswerCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _ringer.StopAsync().ConfigureAwait(false);
        if (_telephone.ActiveMode == TelephoneMode.HalfDuplex && Policy.Configuration.HalfDuplexLoudspeaker)
            await _telephone.EnableLoudspeakerAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await _telephone.AnswerAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Explicit rejection uses the LXST endpoint's status-before-teardown behavior.</summary>
    public ValueTask RejectAsync(CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        if (!_telephone.IsIncomingCall || _telephone.State != CallState.Ringing)
            throw new InvalidOperationException("No incoming ringing call.");
        return HangupAsync(cancellationToken);
    }

    public async ValueTask HangupAsync(CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        long generation;
        lock (_gate) { _callStop?.Cancel(); generation = _generation; }
        _ringer.Stop();
        await _telephone.HangupAsync(cancellationToken).ConfigureAwait(false);
        Task capture;
        Task autoAnswer;
        Task dial;
        Task ending;
        lock (_gate) { capture = _capture; autoAnswer = _autoAnswer; dial = _dial; ending = _ending; }
        // A cancelled dial must finish its endpoint cleanup before the next caller can reuse it.
        await Task.WhenAll(capture, autoAnswer, Task.WhenAny(dial), ending, _ringer.StopAsync())
            .WaitAsync(cancellationToken).ConfigureAwait(false);
        ResetUnattachedCall(generation);
    }

    public void SetBusy(bool busy)
    {
        EnsureStarted();
        _telephone.SetBusy(busy);
    }

    public void MuteMicrophone(bool muted)
    {
        EnsureStarted();
        _telephone.TransmitMuted = muted;
        Publish();
    }

    public void MuteSpeaker(bool muted)
    {
        EnsureStarted();
        _telephone.ReceiveMuted = muted;
        Publish();
    }

    public void SetPushToTalk(bool pressed)
    {
        EnsureStarted();
        if (_telephone.State != CallState.Established || _telephone.ActiveMode != TelephoneMode.HalfDuplex)
            throw new InvalidOperationException("Push-to-talk requires an established half-duplex call.");
        _telephone.SetTransmitSquelched(!pressed);
        Publish();
    }

    public ValueTask EnableLoudspeakerAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        return _telephone.EnableLoudspeakerAsync(enabled, cancellationToken);
    }

    public ValueTask SwitchProfileAsync(TelephoneProfile profile, CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        return _telephone.SwitchProfileAsync(profile, cancellationToken);
    }

    public ValueTask SwitchModeAsync(TelephoneMode mode, CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        return _telephone.SwitchModeAsync(mode, cancellationToken);
    }

    private long BeginCall(string remote, bool incoming)
    {
        _callStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _callActive = true;
        _incoming = incoming;
        _remote = remote;
        return ++_generation;
    }

    private void OnTelephoneState(CallState state)
    {
        var incoming = _telephone.IsIncomingCall;
        var remote = _telephone.RemoteIdentity?.HexHash;
        var profile = _telephone.ActiveProfile;
        SidebandVoiceCallEvent? history = null;
        lock (_gate)
        {
            if (_disposed) { _callStop?.Cancel(); _ringer.Stop(); return; }
            if (state == CallState.Ringing && incoming && !_callActive && remote is not null)
            {
                BeginCall(remote, incoming: true);
                var previous = _autoAnswer;
                var generation = _generation;
                var token = _callStop!.Token;
                _autoAnswer = Task.Run(() => AutoAnswerAsync(previous, generation, remote, token));
            }
            if (state is CallState.Calling || state == CallState.Ringing && !incoming)
                _state = SidebandVoiceState.Connecting;
            else if (state == CallState.Ringing && incoming)
            {
                _state = SidebandVoiceState.Ringing;
                if (_callStop is not null) _ringer.Start(Policy.Configuration.Ringer, _callStop.Token);
            }
            else if (state == CallState.Established && _callActive)
            {
                if (_state != SidebandVoiceState.InCall && _remote is not null)
                    history = new(DateTimeOffset.UtcNow, _incoming ? "incoming-success" : "outgoing-success", _remote);
                _state = SidebandVoiceState.InCall;
                if (profile is { } selected) _output.ConfigureProfile(selected);
                var previous = _capture;
                var token = _callStop!.Token;
                _capture = Task.Run(() => CaptureAsync(previous, token));
            }
            else if (state == CallState.Available)
                _callStop?.Cancel();
            if (state != CallState.Ringing || !incoming) _ringer.Stop();
        }
        if (history is not null) Notify(CallHistory, history, "call history");
        Publish();
    }

    private async Task CaptureAsync(Task previous, CancellationToken token)
    {
        await previous.ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            await TelephoneAudioCapture.CaptureAsync(_devices, _telephone,
                () => Policy.Configuration.Microphone, token).ConfigureAwait(false);
        }
        catch (Exception exception) when ((token.IsCancellationRequested ||
            _telephone.ActiveProfile is null || _telephone.State != CallState.Established) &&
            exception is OperationCanceledException or ObjectDisposedException or InvalidOperationException or IOException) { }
        catch (Exception exception)
        {
            Report("Voice capture failed.", exception);
            _telephone.Hangup();
        }
    }

    private async Task AutoAnswerAsync(Task previous, long generation, string remote, CancellationToken token)
    {
        await previous.ConfigureAwait(false);
        try
        {
            if (!Policy.AllowsDirectPushToTalk(remote)) return;
            for (var index = 0; index < 5; index++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), token).ConfigureAwait(false);
                lock (_gate) if (_disposed || _generation != generation || _state != SidebandVoiceState.Ringing) return;
                if (!Policy.AllowsDirectPushToTalk(remote)) return;
                if (_telephone.ActiveMode != TelephoneMode.HalfDuplex) continue;
                if (_telephone.DisableRemoteModeFollow())
                    await AnswerCoreAsync(token).ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { Report("Voice direct PTT answer failed.", exception); }
    }

    private void OnProfileChanged(TelephoneProfile profile)
    {
        _output.ConfigureProfile(profile);
        Publish();
    }

    private void OnCallEnded(TelephoneCallEnded ended)
    {
        lock (_gate)
        {
            if (!_callActive || _endingGeneration == _generation) return;
            var generation = _endingGeneration = _generation;
            _ending = Task.Run(() => CompleteCallEndAsync(ended, generation));
        }
    }

    private async Task CompleteCallEndAsync(TelephoneCallEnded ended, long generation)
    {
        await _ringer.StopAsync().ConfigureAwait(false);
        SidebandVoiceCallEvent? history;
        CancellationTokenSource? stop;
        lock (_gate)
        {
            if (_generation != generation || !_callActive) return;
            var remote = ended.RemoteIdentityHash ?? _remote;
            history = !_callActive || remote is null ? null : new(DateTimeOffset.UtcNow, _state switch
            {
                SidebandVoiceState.InCall => "ongoing-ended",
                SidebandVoiceState.Ringing => "incoming-missed",
                _ => "outgoing-failure",
            }, remote);
            stop = ClearCall();
        }
        stop?.Cancel();
        // Jobs retain their tokens, not the source; disposal after cancellation does not invalidate them.
        stop?.Dispose();
        if (history is not null) Notify(CallHistory, history, "call history");
        Publish();
    }

    private CancellationTokenSource? ClearCall()
    {
        var previous = _callStop;
        _callStop = null;
        _callActive = false;
        _state = SidebandVoiceState.Available;
        _remote = null;
        _incoming = false;
        return previous;
    }

    private void ResetUnattachedCall(long generation)
    {
        CancellationTokenSource? stop;
        lock (_gate)
        {
            if (!_callActive || _generation != generation) return;
            stop = ClearCall();
        }
        stop?.Cancel();
        stop?.Dispose();
        Publish();
    }

    private void EnsureStarted()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_started) throw new InvalidOperationException("Voice service has not been started.");
        }
    }

    private void EnsureNotDisposed()
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void Publish() => Notify(StateChanged, Snapshot, "state");

    private void Notify<T>(Action<T>? observers, T value, string context)
    {
        if (observers is null) return;
        foreach (Action<T> observer in observers.GetInvocationList())
        {
            try { observer(value); }
            catch (Exception exception) { Report("Voice " + context + " observer failed.", exception); }
        }
    }

    private void Report(string context, Exception? exception)
    {
        if (Error is not { } handlers)
        {
            Trace.TraceError($"{context} {exception}");
            return;
        }
        foreach (Action<SidebandVoiceError> handler in handlers.GetInvocationList())
        {
            try { handler(new(context, exception)); }
            catch (Exception observer) { Trace.TraceError($"{context} {exception}; voice error observer: {observer}"); }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposal is not null) return new(_disposal);
            _disposed = true;
            _callStop?.Cancel();
            _ringer.Stop();
            _stop.Cancel();
            return new(_disposal = Task.Run(DisposeCoreAsync));
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await _telephone.DisposeAsync().ConfigureAwait(false);
            Task capture;
            Task autoAnswer;
            Task dial;
            Task ending;
            lock (_gate) { capture = _capture; autoAnswer = _autoAnswer; dial = _dial; ending = _ending; }
            // Dial errors belong to the caller; disposal joins the exact public task without rethrowing them.
            await Task.WhenAll(capture, autoAnswer, Task.WhenAny(dial), ending).ConfigureAwait(false);
        }
        finally
        {
            await _ringer.DisposeAsync().ConfigureAwait(false);
            await _output.DisposeAsync().ConfigureAwait(false);
            lock (_gate) ClearCall()?.Dispose();
            _stop.Dispose();
        }
    }
}
