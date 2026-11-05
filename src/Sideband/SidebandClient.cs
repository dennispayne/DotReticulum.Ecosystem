using System.Threading.Channels;
using DotReticulum.Applications;
using DotReticulum.Applications.Runtime;
using DotReticulum.Core;
using DotReticulum.Transport;
using LXMF;
using LXST;
using LXST.Devices;

namespace Sideband;

public sealed partial class SidebandClient : IAsyncDisposable
{
    private readonly SidebandStateStore _store;
    private readonly SidebandAttachmentStore _attachments;
    private Channel<ClientUpdate> _inbound = CreateInboundChannel();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _propagationGate = new(1, 1);
    private readonly object _disposeGate = new();
    private readonly object _cancellationGate = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _voiceDialGate = new();
    private CancellationTokenSource? _voiceDialCancellation;
    private Task _voiceDialTask = Task.CompletedTask;
    private Task? _disposeTask;
    private CancellationTokenSource? _runCancellation;
    private Task? _inboundTask;
    private Task? _periodicTask;
    private ReticulumNode? _node;
    private Identity? _identity;
    private LxmRouter? _router;
    private readonly IAudioDeviceBackend? _voiceDevices;
    private SidebandVoiceService? _voice;
    private Identity? _voiceRemoteIdentity;
    private CallbackAnnounceHandler? _announceHandler;
    private CallbackAnnounceHandler? _propagationHandler;
    private int _disposed;

    public SidebandClient(SidebandStateStore store, IAudioDeviceBackend? voiceDevices = null)
        : this(store, voiceDevices, new SidebandBatterySource()) { }

    public SidebandClient(SidebandStateStore store, IAudioDeviceBackend? voiceDevices, ISidebandBatterySource batterySource,
        Func<ReticulumNode, ISidebandPropagationTelemetrySource>? propagationTelemetrySourceFactory = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _attachments = new SidebandAttachmentStore(store.DataDirectory);
        _voiceDevices = voiceDevices;
        _batterySource = batterySource ?? throw new ArgumentNullException(nameof(batterySource));
        _propagationTelemetrySourceFactory = propagationTelemetrySourceFactory;
    }

    public event Action<SidebandState>? StateChanged;
    public event Action<string>? ErrorReported;
    public event Action<SidebandVoiceSnapshot>? VoiceStateChanged;

    public bool IsConnected => _router is not null;
    public SidebandConnectionInfo? ConnectionInfo { get; private set; }
    public LxmPropagationTransferState SyncState => _router?.PropagationTransferState ?? LxmPropagationTransferState.Idle;
    public double SyncProgress => _router?.PropagationTransferProgress ?? 0;
    public SidebandVoiceSnapshot? VoiceSnapshot => _voice?.Snapshot;
    public string? VoiceDestinationHash => _voice is { } voice ? Convert.ToHexStringLower(voice.DestinationHash) : null;

    public async Task<SidebandState> LoadAsync(CancellationToken cancellationToken = default) =>
        await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

    private CancellationTokenSource RuntimeOperationCancellation(CancellationToken token)
    {
        lock (_cancellationGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _runCancellation is { } run
                ? CancellationTokenSource.CreateLinkedTokenSource(token, run.Token, _lifetimeCancellation.Token)
                : CancellationTokenSource.CreateLinkedTokenSource(token, _lifetimeCancellation.Token);
        }
    }

    public async Task ConnectAsync(string? reticulumConfigDirectory = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_router is not null) return;
            await _store.PruneCallHistoryAsync(cancellationToken).ConfigureAwait(false);
            var state = await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var stampPolicy = SidebandStampConfiguration.FromState(state);
            var config = string.IsNullOrWhiteSpace(reticulumConfigDirectory)
                ? state.ReticulumConfigDirectory
                : Path.GetFullPath(reticulumConfigDirectory);
            await _store.SetReticulumConfigDirectoryAsync(config, cancellationToken).ConfigureAwait(false);

            var node = ReticulumNode.Open(config, ReportRuntimeLog);
            var inbound = CreateInboundChannel();
            _inbound = inbound;
            Identity? identity = null;
            LxmRouter? router = null;
            CancellationTokenSource? runCancellation = null;
            try
            {
                identity = node.LoadOrCreateIdentity("sideband");
                runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                // Cancelling application workers must not stop transport before LXMF/voice link teardown.
                await node.StartAsync(cancellationToken).ConfigureAwait(false);
                router = new LxmRouter(node.Router, identity, state.DisplayName, new LxmRouterOptions
                {
                    StoragePath = _store.DataDirectory,
                    InboundQueueCapacity = 128,
                    OutboundQueueCapacity = 128,
                    DeliveryPerTransferLimit = SidebandDeliveryConfiguration.FromState(state).MaximumTransferBytes,
                    InboundStampCost = stampPolicy.EffectiveCost,
                    EnforceStamps = stampPolicy.RejectInvalidStamps,
                });
                router.Log = Report;
                router.OutboundPropagationNode = (state.PropagationNode ?? state.LastPropagationNode) is { } propagationNode
                    ? Convert.FromHexString(propagationNode) : null;
                router.MessageReceived = message =>
                {
                    if (!inbound.Writer.TryWrite(new ClientUpdate(Received: message)))
                        Report("Incoming message queue is full; the message was not added to the conversation list.");
                };
                _announceHandler = new CallbackAnnounceHandler("lxmf.delivery", ReceivedDeliveryAnnounce);
                node.Router.Engine.RegisterHandler(_announceHandler);
                _propagationHandler = new CallbackAnnounceHandler("lxmf.propagation", ReceivedPropagationAnnounce);
                node.Router.Engine.RegisterHandler(_propagationHandler);
                router.Start();
                _node = node;
                _identity = identity;
                _router = router;
                lock (_cancellationGate) _runCancellation = runCancellation;
                await StartTelemetryAsync(router, node, identity, runCancellation.Token).ConfigureAwait(false);
                _inboundTask = Task.Run(() => ProcessIncomingAsync(inbound.Reader), CancellationToken.None);
                await ApplyVoiceConfigurationAsync(state, cancellationToken).ConfigureAwait(false);
                var runToken = runCancellation.Token;
                _periodicTask = Task.Run(() => ProcessPeriodicAsync(runToken), CancellationToken.None);
                _startupAnnounceTask = SidebandAnnounceConfiguration.FromState(state).AnnounceOnStartup
                    ? AnnounceAfterStartupAsync(router, runToken) : Task.CompletedTask;
                await _store.SetOwnDeliveryHashAsync(router.DeliveryHashHex, cancellationToken).ConfigureAwait(false);
                ConnectionInfo = new SidebandConnectionInfo(
                    node.ConfigDir,
                    Path.Combine(node.ConfigDir, "config"),
                    node.SharedRole,
                    node.Config.SharedInstance.Enabled,
                    identity.HexHash,
                    router.DeliveryHashHex);
                PublishState(await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
            }
            catch
            {
                lock (_cancellationGate) _runCancellation = null;
                if (runCancellation is not null) await runCancellation.CancelAsync().ConfigureAwait(false);
                if (_startupAnnounceTask is not null) await _startupAnnounceTask.ConfigureAwait(false);
                _startupAnnounceTask = null;
                try { await StopTelemetryAsync().ConfigureAwait(false); }
                finally
                {
                    if (_announceHandler is not null) node.Router.Engine.DeregisterHandler(_announceHandler);
                    if (_propagationHandler is not null) node.Router.Engine.DeregisterHandler(_propagationHandler);
                    _announceHandler = null;
                    _propagationHandler = null;
                    await _propagationGate.WaitAsync().ConfigureAwait(false);
                    try { _router = null; _node = null; }
                    finally { _propagationGate.Release(); }
                    if (_voice is { } voice) await voice.DisposeAsync().ConfigureAwait(false);
                    _voice = null;
                    _voiceRemoteIdentity?.Dispose();
                    _voiceRemoteIdentity = null;
                    if (router is not null) await router.DisposeAsync().ConfigureAwait(false);
                    ClearOutgoingMessages();
                    inbound.Writer.TryComplete();
                    if (_inboundTask is not null) await _inboundTask.ConfigureAwait(false);
                    if (_periodicTask is not null) await _periodicTask.ConfigureAwait(false);
                    runCancellation?.Dispose();
                    if (identity is not null) identity.Dispose();
                    await node.DisposeAsync().ConfigureAwait(false);
                    _router = null;
                    _node = null;
                    _identity = null;
                    _inboundTask = null;
                    _periodicTask = null;
                    ConnectionInfo = null;
                }
                throw;
            }
        }
        finally { _lifecycle.Release(); }
    }

    public async Task AddContactAsync(string name, string deliveryHash, CancellationToken cancellationToken = default)
    {
        var contact = SidebandContact.Create(name, deliveryHash);
        await _store.AddContactAsync(contact, cancellationToken).ConfigureAwait(false);
        await RefreshVoicePolicyAsync(cancellationToken).ConfigureAwait(false);
        PublishState(await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    public async Task SetConversationTrustAsync(string deliveryHash, bool trusted, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _store.SetConversationTrustAsync(deliveryHash, trusted, cancellationToken).ConfigureAwait(false);
        await RefreshVoicePolicyAsync(cancellationToken).ConfigureAwait(false);
        PublishState(await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    public async Task SetConversationReadAsync(string deliveryHash, bool read, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _store.SetConversationReadAsync(deliveryHash, read, cancellationToken).ConfigureAwait(false);
        PublishState(await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    public async Task ConfigureVoiceAsync(bool enabled, SidebandVoiceConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _store.ConfigureVoiceAsync(enabled, configuration, cancellationToken).ConfigureAwait(false);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var state = await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (_node is not null) await ApplyVoiceConfigurationAsync(state, cancellationToken).ConfigureAwait(false);
            PublishState(state);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task SetConversationPushToTalkAsync(string deliveryHash, bool enabled, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _store.SetConversationPushToTalkAsync(deliveryHash, enabled, cancellationToken).ConfigureAwait(false);
        await RefreshVoicePolicyAsync(cancellationToken).ConfigureAwait(false);
        PublishState(await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    public async Task SetVoicePeerTrustAsync(string identityHash, bool trusted, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _store.SetVoicePeerTrustAsync(identityHash, trusted, cancellationToken).ConfigureAwait(false);
        await RefreshVoicePolicyAsync(cancellationToken).ConfigureAwait(false);
        PublishState(await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    public async Task ConfigureVoicePeerAsync(string identityHash, string? displayName, bool trusted, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _store.ConfigureVoicePeerAsync(identityHash, displayName, trusted, cancellationToken).ConfigureAwait(false);
        await RefreshVoicePolicyAsync(cancellationToken).ConfigureAwait(false);
        PublishState(await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    public async Task DeleteVoicePeerAsync(string identityHash, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _store.DeleteVoicePeerAsync(identityHash, cancellationToken).ConfigureAwait(false);
        await RefreshVoicePolicyAsync(cancellationToken).ConfigureAwait(false);
        PublishState(await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    public Task CallIdentityAsync(string identityHash, TelephoneProfile profile = TelephoneProfile.MediumQuality,
        TelephoneMode mode = TelephoneMode.FullDuplex, CancellationToken cancellationToken = default)
    {
        if (!SidebandContact.TryNormalizeDeliveryHash(identityHash, out var normalized))
            throw new ArgumentException("Enter a 32-character hexadecimal identity hash, not an LXMF destination.", nameof(identityHash));
        return CallDestinationAsync(Destination.ComputeHashFromNameAndIdentity("lxst.telephony", Convert.FromHexString(normalized)),
            profile, mode, cancellationToken);
    }

    public Task CallContactAsync(string deliveryHash, TelephoneProfile profile = TelephoneProfile.MediumQuality,
        TelephoneMode mode = TelephoneMode.FullDuplex, CancellationToken cancellationToken = default)
    {
        if (!SidebandContact.TryNormalizeDeliveryHash(deliveryHash, out var normalized))
            throw new ArgumentException("Enter a 32-character hexadecimal LXMF delivery address.", nameof(deliveryHash));
        return CallDestinationAsync(Convert.FromHexString(normalized), profile, mode, cancellationToken);
    }

    private Task CallDestinationAsync(byte[] destination, TelephoneProfile profile, TelephoneMode mode, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_voiceDialGate)
        {
            if (_voiceDialCancellation is not null) throw new InvalidOperationException("A voice call is already being placed.");
            CancellationTokenSource request;
            lock (_cancellationGate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                var run = _runCancellation ?? throw new InvalidOperationException("Connect to Reticulum and enable voice first.");
                request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, run.Token);
            }
            _voiceDialCancellation = request;
            // Publish the owned task before callbacks can re-enter hangup from call setup.
            return _voiceDialTask = Task.Run(() => CallDestinationCoreAsync(destination, profile, mode, request));
        }
    }

    private async Task CallDestinationCoreAsync(byte[] destination, TelephoneProfile profile, TelephoneMode mode,
        CancellationTokenSource cancellation)
    {
        using var request = cancellation;
        var acquired = false;
        try
        {
            await _lifecycle.WaitAsync(request.Token).ConfigureAwait(false);
            acquired = true;
            var voice = _voice ?? throw new InvalidOperationException("Connect to Reticulum and enable voice first.");
            if (voice.IsBusy) throw new InvalidOperationException("The voice service is already busy.");
            var remote = await _node!.ResolveIdentityAsync(destination, SidebandVoiceService.PathTimeout, request.Token).ConfigureAwait(false)
                ?? throw new TimeoutException("No announced identity was found for the requested peer.");
            _voiceRemoteIdentity?.Dispose();
            _voiceRemoteIdentity = remote;
            await voice.CallAsync(remote, profile, mode, request.Token).ConfigureAwait(false);
        }
        finally
        {
            if (acquired) _lifecycle.Release();
            lock (_voiceDialGate)
            {
                _voiceDialCancellation = null;
            }
        }
    }

    public async Task ClearCallHistoryAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_inboundTask is null)
            {
                await _store.ClearCallHistoryAsync(cancellationToken).ConfigureAwait(false);
                PublishState(await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
            }
            else
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await _inbound.Writer.WriteAsync(new ClientUpdate(ClearCalls: completion), cancellationToken).ConfigureAwait(false);
                await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally { _lifecycle.Release(); }
    }

    public ValueTask AnnounceVoiceAsync(CancellationToken cancellationToken = default) => RequireVoice().AnnounceAsync(cancellationToken);
    public ValueTask AnswerCallAsync(CancellationToken cancellationToken = default) => RequireVoice().AnswerAsync(cancellationToken);
    public ValueTask RejectCallAsync(CancellationToken cancellationToken = default) => RequireVoice().RejectAsync(cancellationToken);
    public async ValueTask HangupCallAsync(CancellationToken cancellationToken = default)
    {
        var voice = RequireVoice();
        Task pending;
        lock (_voiceDialGate)
        {
            pending = _voiceDialCancellation is null ? Task.CompletedTask : _voiceDialTask;
            _voiceDialCancellation?.Cancel();
        }
        await voice.HangupAsync(cancellationToken).ConfigureAwait(false);
        try { await pending.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (pending.IsCanceled && !cancellationToken.IsCancellationRequested) { }
    }
    public void MuteVoiceMicrophone(bool muted) => RequireVoice().MuteMicrophone(muted);
    public void MuteVoiceSpeaker(bool muted) => RequireVoice().MuteSpeaker(muted);
    public void SetPushToTalk(bool pressed) => RequireVoice().SetPushToTalk(pressed);
    public ValueTask SetVoiceLoudspeakerAsync(bool enabled, CancellationToken cancellationToken = default) =>
        RequireVoice().EnableLoudspeakerAsync(enabled, cancellationToken);
    public ValueTask SetVoiceProfileAsync(TelephoneProfile profile, CancellationToken cancellationToken = default) =>
        RequireVoice().SwitchProfileAsync(profile, cancellationToken);
    public ValueTask SetVoiceModeAsync(TelephoneMode mode, CancellationToken cancellationToken = default) =>
        RequireVoice().SwitchModeAsync(mode, cancellationToken);
    public ValueTask<IReadOnlyList<AudioDeviceInfo>> ListVoiceInputsAsync(CancellationToken cancellationToken = default) =>
        RequireVoice().ListInputsAsync(cancellationToken);
    public ValueTask<IReadOnlyList<AudioDeviceInfo>> ListVoiceOutputsAsync(CancellationToken cancellationToken = default) =>
        RequireVoice().ListOutputsAsync(cancellationToken);

    private SidebandVoiceService RequireVoice()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _voice ?? throw new InvalidOperationException("Connect to Reticulum and enable voice first.");
    }

    private async Task ApplyVoiceConfigurationAsync(SidebandState state, CancellationToken token)
    {
        if (!state.VoiceEnabled)
        {
            if (_voice is { } previous) await previous.DisposeAsync().ConfigureAwait(false);
            _voice = null;
            _voiceRemoteIdentity?.Dispose();
            _voiceRemoteIdentity = null;
            return;
        }
        var policy = VoicePolicy(state);
        if (_voice is { } existing)
        {
            await existing.ConfigureAsync(policy, token).ConfigureAwait(false);
            return;
        }
        var backend = _voiceDevices ?? (OperatingSystem.IsWindows()
            ? new WindowsWasapiAudioBackend()
            : OperatingSystem.IsLinux() ? new PulseAudioDeviceBackend()
            : throw new PlatformNotSupportedException("Sideband voice devices require Windows or Linux."));
        var voice = new SidebandVoiceService(_node!.Router, _identity!, backend, policy);
        voice.StateChanged += PublishVoice;
        voice.Error += error => Report(error.Context + (error.Exception is null ? string.Empty : " " + error.Exception.Message));
        var updates = _inbound;
        voice.CallHistory += call =>
        {
            if (!updates.Writer.TryWrite(new ClientUpdate(Call: call)))
                Report("Voice history queue is full or closed; the call event was not persisted.");
        };
        try
        {
            voice.Start();
            await voice.AnnounceAsync(token).ConfigureAwait(false);
            _voice = voice;
        }
        catch { await voice.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async Task RefreshVoicePolicyAsync(CancellationToken token)
    {
        await _lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await RefreshVoicePolicyCoreAsync(token).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task RefreshVoicePolicyCoreAsync(CancellationToken token)
    {
        if (_voice is not { } voice) return;
        var state = await _store.LoadAsync(cancellationToken: token).ConfigureAwait(false);
        if (ReferenceEquals(voice, _voice))
            await voice.ConfigureAsync(VoicePolicy(state), token).ConfigureAwait(false);
    }

    private static SidebandVoicePolicy VoicePolicy(SidebandState state) => SidebandVoicePolicy.FromState(state);

    public Task SendTextAsync(string deliveryHash, string content, CancellationToken cancellationToken = default) =>
        SendMessageAsync(deliveryHash, content, null, null, cancellationToken);

    public async Task SendFileAsync(string deliveryHash, string content, string path, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var name = Path.GetFileName(path);
        SidebandAttachmentStore.ValidateName(name);
        var data = await SidebandAttachmentStore.ReadBoundedAsync(path, cancellationToken).ConfigureAwait(false);
        await SendMessageAsync(deliveryHash, content, name, data, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendImageAsync(string deliveryHash, string content, string path,
        SidebandImageQuality quality = SidebandImageQuality.Medium, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var input = await SidebandAttachmentStore.ReadBoundedAsync(path, cancellationToken).ConfigureAwait(false);
        var image = await Task.Run(() => SidebandMediaCodec.PrepareImage(input, quality, cancellationToken), cancellationToken).ConfigureAwait(false);
        await SendMessageAsync(deliveryHash, content, null, null, cancellationToken, image: image).ConfigureAwait(false);
    }

    public async Task SendAudioFileAsync(string deliveryHash, string content, string path, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var input = await SidebandAttachmentStore.ReadBoundedAsync(path, cancellationToken).ConfigureAwait(false);
        await SendAudioAsync(deliveryHash, content, new(SidebandMediaCodec.OpusOggMode, input), cancellationToken).ConfigureAwait(false);
    }

    public async Task SendAudioAsync(string deliveryHash, string content, SidebandEncodedAudio audio, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(audio);
        if (audio.EncodedBytes is not { Length: > 0 } || audio.EncodedBytes.Length > SidebandMediaCodec.MaximumEncodedBytes)
            throw new InvalidDataException("Outgoing audio must contain at most 2 MiB of nonempty encoded data.");
        audio = audio with { EncodedBytes = audio.EncodedBytes.ToArray() };
        // Validate outgoing audio before queueing. Receipt never opens a device or requires a native codec.
        _ = await SidebandMediaCodec.DecodeAudioAsync(audio.Mode, audio.EncodedBytes,
            Path.Combine(_store.DataDirectory, "media-work"), cancellationToken).ConfigureAwait(false);
        await SendMessageAsync(deliveryHash, content, null, null, cancellationToken, audio: audio).ConfigureAwait(false);
    }

    public async Task<byte[]> LoadImageAsync(string peerHash, Guid messageId, CancellationToken cancellationToken = default)
    {
        var message = await FindMessageAsync(peerHash, messageId, cancellationToken).ConfigureAwait(false);
        var image = message.Image ?? throw new InvalidOperationException("The selected message has no image.");
        var input = await _attachments.LoadAsync(image.Data, cancellationToken).ConfigureAwait(false);
        return (await Task.Run(() => SidebandMediaCodec.ValidateImage(image.Format, input, cancellationToken),
            cancellationToken).ConfigureAwait(false)).EncodedBytes;
    }

    public async Task<LXST.AudioFrame> LoadAudioAsync(string peerHash, Guid messageId, CancellationToken cancellationToken = default)
    {
        var message = await FindMessageAsync(peerHash, messageId, cancellationToken).ConfigureAwait(false);
        var audio = message.Audio ?? throw new InvalidOperationException("The selected message has no audio.");
        var data = await _attachments.LoadAsync(audio.Data, cancellationToken).ConfigureAwait(false);
        return await SidebandMediaCodec.DecodeAudioAsync(audio.Mode, data,
            Path.Combine(_store.DataDirectory, "media-work"), cancellationToken).ConfigureAwait(false);
    }

    public async Task ExportMediaAsync(string peerHash, Guid messageId, bool audio, string path, CancellationToken cancellationToken = default)
    {
        var message = await FindMessageAsync(peerHash, messageId, cancellationToken).ConfigureAwait(false);
        var data = audio ? message.Audio?.Data : message.Image?.Data;
        await _attachments.ExportAsync(data ?? throw new InvalidOperationException("The selected message has no requested media."),
            path, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SidebandChatMessage> FindMessageAsync(string peerHash, Guid messageId, CancellationToken cancellationToken) =>
        (await _store.GetMessagesAsync(peerHash, cancellationToken).ConfigureAwait(false)).SingleOrDefault(item => item.Id == messageId)
        ?? throw new ArgumentException("The selected conversation message was not found.", nameof(messageId));

    public async Task ExportAttachmentAsync(string peerHash, Guid messageId, int index, string path, CancellationToken cancellationToken = default)
    {
        var messages = await _store.GetMessagesAsync(peerHash, cancellationToken).ConfigureAwait(false);
        var message = messages.SingleOrDefault(item => item.Id == messageId)
            ?? throw new ArgumentException("The selected conversation message was not found.", nameof(messageId));
        if (message.Attachments is not { } attachments || index < 0 || index >= attachments.Count)
            throw new ArgumentOutOfRangeException(nameof(index), "The selected attachment was not found.");
        await _attachments.ExportAsync(attachments[index], path, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendMessageAsync(string deliveryHash, string content, string? attachmentName, byte[]? attachmentData,
        CancellationToken cancellationToken, SidebandEncodedImage? image = null, SidebandEncodedAudio? audio = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0 && (attachmentData is not null || image is not null || audio is not null))
            content = " ";
        else
            ArgumentException.ThrowIfNullOrWhiteSpace(content);
        if (content.Length > 65_536) throw new ArgumentException("Messages are limited to 65,536 characters.", nameof(content));
        if (!SidebandContact.TryNormalizeDeliveryHash(deliveryHash, out var normalized))
            throw new ArgumentException("An LXMF delivery address must be exactly 32 hexadecimal characters.", nameof(deliveryHash));

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await SendMessageCoreAsync(normalized, content, attachmentName, attachmentData, cancellationToken, image, audio).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task<SidebandChatMessage> SendMessageCoreAsync(string normalized, string content, string? attachmentName,
        byte[]? attachmentData, CancellationToken cancellationToken, SidebandEncodedImage? image = null,
        SidebandEncodedAudio? audio = null, Guid? replacedFailedMessageId = null)
    {
        var router = _router ?? throw new InvalidOperationException("Connect to Reticulum before sending messages.");
        var identity = _identity ?? throw new InvalidOperationException("The local Reticulum identity is unavailable.");
        using var owned = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _runCancellation!.Token);
        var state = await _store.LoadAsync(cancellationToken: owned.Token).ConfigureAwait(false);
        var formatted = SidebandMessageFormatting.FromState(state).PrepareOutgoing(content);
        content = formatted.Content;
        var destination = Convert.FromHexString(normalized);
        using var recipient = await _node!.ResolveIdentityAsync(destination, TimeSpan.FromSeconds(15), owned.Token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No announce or path was found for this LXMF delivery address.");
        var method = state.PropagationByDefault ? LxmMethod.Propagated : LxmMethod.Direct;
        IReadOnlyList<SidebandAttachment>? attachments = null;
        SidebandImage? storedImage = null;
        SidebandAudio? storedAudio = null;
        Dictionary<object, object?>? fields = await OrdinaryTelemetryFieldsAsync(state, normalized, owned.Token).ConfigureAwait(false);
        if (formatted.Renderer is { } renderer) fields[LxmField.Renderer] = renderer;
        if (attachmentData is not null && attachmentName is not null)
        {
            attachments = [await _attachments.StoreAsync(attachmentName, attachmentData, owned.Token).ConfigureAwait(false)];
            fields[LxmField.FileAttachments] = new object?[] { new object?[] { attachmentName, attachmentData } };
        }
        if (image is not null)
        {
            storedImage = new(image.Format, await _attachments.StoreAsync("image." + image.Format, image.EncodedBytes, owned.Token).ConfigureAwait(false));
            (fields ??= [])[LxmField.Image] = new object?[] { image.Format, image.EncodedBytes };
        }
        if (audio is not null)
        {
            storedAudio = new(audio.Mode, await _attachments.StoreAsync(audio.Mode == SidebandMediaCodec.OpusOggMode ? "audio.ogg" : "audio.c2",
                audio.EncodedBytes, owned.Token).ConfigureAwait(false));
            (fields ??= [])[LxmField.Audio] = new object?[] { audio.Mode, audio.EncodedBytes };
        }
        var message = LxMessage.Create(destination, identity, content, fields: fields, method: method);
        if (fields.TryGetValue(LxmField.Telemetry, out var telemetryBytes) && telemetryBytes is byte[] reportBytes)
            RegisterTelemetryOutcome(message, new(normalized, false, OwnsPending: false), SidebandTelemetryCodec.Decode(reportBytes).TimestampUtcSeconds ?? 0, false);
        message.IncludeTicket = state.Contacts.Any(contact => contact.DeliveryHash == normalized && contact.Trusted);
        var local = new SidebandChatMessage(Guid.NewGuid(), normalized, false, string.Empty, content, DateTimeOffset.UtcNow, "Queued",
            attachments, method.ToString(), storedImage, storedAudio, Renderer: formatted.Renderer,
            TelemetryHex: SidebandMessageTelemetry.Capture(message));
        var updates = _inbound;
        var eligibleForRetry = state.TryPropagationOnFail && router.OutboundPropagationNode is not null;
        message.StateChanged += changed =>
        {
            if (OutgoingMessageIsTerminal(changed)) UntrackOutgoingMessage(local.Id, changed);
            var failed = SidebandDeliveryRetry.ShouldRetry(changed.State, eligibleForRetry, stampGenerationFailed: false);
            if (!updates.Writer.TryWrite(new ClientUpdate(MessageId: local.Id, DeliveryState: changed.State.ToString(),
                FailedMessage: failed ? changed : null)))
                Report("Delivery-state queue is full or closed; a conversation status update could not be saved.");
        };
        TrackOutgoingMessage(local.Id, normalized, router, message);
        try
        {
            await _store.AddMessageAsync(local, true, owned.Token, replacedFailedMessageId).ConfigureAwait(false);
            PublishState(await _store.LoadAsync(cancellationToken: owned.Token).ConfigureAwait(false));
            await router.SendAsync(message, owned.Token).ConfigureAwait(false);
        }
        catch
        {
            UntrackOutgoingMessage(local.Id, message);
            await updates.Writer.WriteAsync(new ClientUpdate(MessageId: local.Id,
                DeliveryState: owned.IsCancellationRequested || message.State == LxmState.Cancelled ? "Cancelled" : "Failed")).ConfigureAwait(false);
            throw;
        }
        return local;
    }

    public async Task ConfigurePropagationAsync(string? node, bool byDefault, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await _propagationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _store.SetPropagationAsync(node, byDefault, cancellationToken).ConfigureAwait(false);
                var state = await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                if (_router is { } router)
                    router.OutboundPropagationNode = (state.PropagationNode ?? state.LastPropagationNode) is { } selected
                        ? Convert.FromHexString(selected) : null;
                PublishState(state);
            }
            finally { _propagationGate.Release(); }
        }
        finally { _lifecycle.Release(); }
    }

    public Task<int> SynchronizeAsync(int maxMessages = 0, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxMessages);
        return SynchronizeCoreAsync(maxMessages, cancellationToken);
    }

    private async Task<int> SynchronizeCoreAsync(int? maxMessages, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var router = _router ?? throw new InvalidOperationException("Connect to Reticulum before synchronizing.");
            using var owned = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _runCancellation!.Token);
            if (maxMessages is null)
            {
                if (router.OutboundPropagationNode is null)
                    throw new InvalidOperationException("Select or discover a propagation node before synchronizing.");
                var state = await _store.LoadAsync(cancellationToken: owned.Token).ConfigureAwait(false);
                maxMessages = SidebandSynchronizationConfiguration.FromState(state).ManualMaximumMessages;
                await _store.SetSynchronizationStateAsync(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d,
                    false, owned.Token).ConfigureAwait(false);
                PublishState(await _store.LoadAsync(cancellationToken: owned.Token).ConfigureAwait(false));
            }
            return await router.RequestMessagesFromPropagationNodeAsync(maxMessages: maxMessages.Value, cancellationToken: owned.Token).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task ConfigurePeriodicSynchronizationAsync(bool enabled, int intervalSeconds = 43_200, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _store.SetPeriodicSynchronizationAsync(enabled, intervalSeconds, cancellationToken).ConfigureAwait(false);
        PublishState(await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    public async Task ConfigurePropagationFallbackAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _store.SetPropagationFallbackAsync(enabled, cancellationToken).ConfigureAwait(false);
        PublishState(await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    public void CancelSynchronization() => _router?.CancelPropagationNodeRequests();

    public Task<IReadOnlyList<SidebandChatMessage>> GetMessagesAsync(string peerHash, CancellationToken cancellationToken = default) =>
        _store.GetMessagesAsync(peerHash, cancellationToken);

    public async Task DisconnectAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await CancelRunAsync().ConfigureAwait(false);
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await DisconnectCoreAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lifetimeCancellation.CancelAsync().ConfigureAwait(false);
        await CancelRunAsync().ConfigureAwait(false);
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await DisconnectCoreAsync().ConfigureAwait(false); }
        finally
        {
            await _telemetryStoreGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_telemetryStore is { } storage) await storage.DisposeAsync().ConfigureAwait(false);
                _telemetryStore = null;
            }
            finally { _telemetryStoreGate.Release(); _lifecycle.Release(); _lifetimeCancellation.Dispose(); }
        }
    }

    private Task CancelRunAsync()
    {
        lock (_cancellationGate) return _runCancellation?.CancelAsync() ?? Task.CompletedTask;
    }

    private async Task DisconnectCoreAsync()
    {
        await _propagationGate.WaitAsync().ConfigureAwait(false);
        var node = _node;
        var router = _router;
        var identity = _identity;
        CancellationTokenSource? cancellation = null;
        try
        {
            lock (_cancellationGate)
            {
                cancellation = _runCancellation;
                _runCancellation = null;
            }
            if (node is not null && _announceHandler is not null)
                node.Router.Engine.DeregisterHandler(_announceHandler);
            if (node is not null && _propagationHandler is not null)
                node.Router.Engine.DeregisterHandler(_propagationHandler);
            _announceHandler = null;
            _propagationHandler = null;
            _router = null;
            _node = null;
            _identity = null;
            ConnectionInfo = null;
        }
        finally { _propagationGate.Release(); }
        if (cancellation is not null) await cancellation.CancelAsync().ConfigureAwait(false);
        if (_startupAnnounceTask is not null) await _startupAnnounceTask.ConfigureAwait(false);
        _startupAnnounceTask = null;
        try { await StopTelemetryAsync().ConfigureAwait(false); }
        finally { await StopTransportAsync(node, router, identity, cancellation).ConfigureAwait(false); }
    }

    private async Task StopTransportAsync(ReticulumNode? node, LxmRouter? router, Identity? identity,
        CancellationTokenSource? cancellation)
    {
        var voice = _voice;
        _voice = null;
        if (voice is not null) await voice.DisposeAsync().ConfigureAwait(false);
        _voiceRemoteIdentity?.Dispose();
        _voiceRemoteIdentity = null;
        if (router is not null) await router.DisposeAsync().ConfigureAwait(false);
        ClearOutgoingMessages();
        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            _inbound.Writer.TryComplete();
            if (_inboundTask is not null) await _inboundTask.ConfigureAwait(false);
            if (_periodicTask is not null) await _periodicTask.ConfigureAwait(false);
            cancellation.Dispose();
        }

        _inboundTask = null;
        _periodicTask = null;
        if (node is not null) await node.DisposeAsync().ConfigureAwait(false);
        identity?.Dispose();
    }

    private void ReceivedDeliveryAnnounce(AnnounceReceived announce)
    {
        try
        {
            var hash = Convert.ToHexStringLower(announce.DestinationHash);
            var observed = SidebandAnnounce.FromAppData(hash, SidebandAnnounce.Delivery, DateTimeOffset.UtcNow,
                announce.AppData, _router?.GetOutboundStampCost(announce.DestinationHash));
            SidebandContact? contact = null;
            if (!string.IsNullOrWhiteSpace(observed.Name))
            {
                try { contact = SidebandContact.Create(observed.Name, hash); }
                catch (ArgumentException ex) { Report("Announce name is not a valid contact name; history is retained: " + ex.Message); }
            }
            if (!_inbound.Writer.TryWrite(new ClientUpdate(Contact: contact, Announce: observed,
                AnnouncePacketHash: announce.PacketHash?.ToArray())))
                Report("Announce queue is full or closed; the delivery announce was not saved.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
        { Report("Ignored invalid delivery announce: " + ex.Message); }
    }

    private async Task ProcessPeriodicAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await _store.PruneCallHistoryAsync(cancellationToken).ConfigureAwait(false);
                    var state = await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                    var router = _router;
                    if (router is null) continue;
                    var transferState = router.PropagationTransferState;
                    var accepted = transferState == LxmPropagationTransferState.Idle ||
                        (int)transferState >= (int)LxmPropagationTransferState.Complete;
                    var decision = SidebandSynchronizationPolicy.Advance(state.PeriodicSynchronization,
                        state.SynchronizationIntervalSeconds, state.LastSynchronization, state.SynchronizationRetrying,
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d, accepted);
                    if (decision.LastSync != state.LastSynchronization || decision.Retrying != state.SynchronizationRetrying)
                    {
                        await _store.SetSynchronizationStateAsync(decision.LastSync, decision.Retrying, cancellationToken).ConfigureAwait(false);
                        PublishState(await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
                    }
                    if (!decision.Attempted || !accepted) continue;
                    await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (ReferenceEquals(router, _router))
                            await router.RequestMessagesFromPropagationNodeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    finally { _lifecycle.Release(); }
                    PublishState(await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception ex) { Report("Scheduled propagation synchronization failed: " + ex.Message); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void ReceivedPropagationAnnounce(AnnounceReceived announce)
    {
        if (!LxmfPropagationAnnounce.TryParse(announce.AppData, out var data) || data is null)
        {
            Report("Ignored malformed propagation-node announce.");
            return;
        }
        if (data.Timebase - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d > 300)
        {
            Report("Ignored propagation-node announce timestamp more than 300 seconds in the future.");
            return;
        }
        try
        {
            var hash = Convert.ToHexStringLower(announce.DestinationHash);
            var observed = SidebandAnnounce.FromAppData(hash, SidebandAnnounce.Propagation, DateTimeOffset.UtcNow, announce.AppData);
            if (!_inbound.Writer.TryWrite(new ClientUpdate(Propagation: new PropagationCandidate(hash, announce.Hops, data.Timebase),
                Announce: observed, AnnouncePacketHash: announce.PacketHash?.ToArray())))
                Report("Propagation-discovery queue is full or closed; the announce was not processed.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
        { Report("Ignored invalid propagation announce metadata: " + ex.Message); }
    }

    private async Task ProcessPropagationAsync(PropagationCandidate candidate)
    {
        await _propagationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_node is not { } node || _router is not { } router) return;
            var state = await _store.LoadAsync().ConfigureAwait(false);
            var active = state.PropagationNode ?? state.LastPropagationNode;
            var previousHops = active is null ? null : node.Router.HopsTo(Convert.FromHexString(active));
            if (!SidebandPropagationPolicy.ShouldSelect(state.PropagationNode is not null, previousHops,
                candidate.Hops, candidate.Emitted, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d)) return;
            await _store.RememberPropagationNodeAsync(candidate.Hash).ConfigureAwait(false);
            state = await _store.LoadAsync().ConfigureAwait(false);
            router.OutboundPropagationNode = Convert.FromHexString(state.PropagationNode ?? state.LastPropagationNode!);
        }
        finally { _propagationGate.Release(); }
    }

    private async Task ProcessIncomingAsync(ChannelReader<ClientUpdate> reader)
    {
        await foreach (var update in reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (update.Announce is { } observed)
                    await ProcessAnnounceAsync(observed, update.AnnouncePacketHash).ConfigureAwait(false);
                if (update.TelemetryDelivery is { } telemetryDelivery)
                    await CompleteTelemetryAsync(telemetryDelivery).ConfigureAwait(false);
                if (update.ClearCalls is not null)
                    await _store.ClearCallHistoryAsync().ConfigureAwait(false);
                else if (update.Propagation is { } candidate)
                {
                    await ProcessPropagationAsync(candidate).ConfigureAwait(false);
                }
                else if (update.Contact is { } contact)
                {
                    await _store.ObserveDeliveryAnnounceAsync(contact).ConfigureAwait(false);
                    await RefreshVoicePolicyCoreAsync(CancellationToken.None).ConfigureAwait(false);
                }
                else if (update.Call is { } call)
                    await _store.AddCallEventAsync(call).ConfigureAwait(false);
                else if (update.FailedMessage is { } failed)
                {
                    await RetryViaPropagationAsync(update.MessageId, failed).ConfigureAwait(false);
                }
                else if (update.DeliveryState is { } deliveryState)
                {
                    await _store.UpdateMessageStateAsync(update.MessageId, deliveryState).ConfigureAwait(false);
                }
                else if (update.Received is { } received)
                {
                    var state = await _store.LoadAsync().ConfigureAwait(false);
                    var hash = Convert.ToHexStringLower(received.Message.SourceHash);
                    if (SidebandTelemetryPolicy.DeliveryDisposition(state.Telemetry.Policy, ConversationPolicy(state, hash),
                        received.Message.SignatureValidated, received.Message.TryGetField(LxmField.Commands, out _),
                        SidebandRemoteCommands.Authorization(state).IgnoreUnknown) == SidebandTelemetryDeliveryDisposition.Drop)
                    {
                        Report($"LXMF message from {hash} rejected: sender not authorized (unknown conversation or command policy).");
                        continue;
                    }
                    string? messageTelemetry = null;
                    try { messageTelemetry = await ReceiveTelemetryAsync(received.Message, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { Report("Received telemetry metadata was rejected; text/media are retained: " + ex.Message); }
                    if (received.Message.TryGetField(LxmField.Commands, out _)) continue;
                    var telemetryOnly = string.IsNullOrEmpty(received.Message.Content) && string.IsNullOrEmpty(received.Message.Title) &&
                        (received.Message.TryGetField(LxmField.Telemetry, out _) || received.Message.TryGetField(LxmField.TelemetryStream, out _));
                    if (string.IsNullOrEmpty(received.Message.Content) &&
                        string.IsNullOrEmpty(received.Message.Title) &&
                        !received.Message.TryGetField(LxmField.FileAttachments, out _) &&
                        !received.Message.TryGetField(LxmField.Image, out _) && !received.Message.TryGetField(LxmField.Audio, out _))
                    {
                        if (!telemetryOnly) await _store.PromoteConversationAsync(hash).ConfigureAwait(false);
                        PublishState(await _store.LoadAsync().ConfigureAwait(false));
                        continue;
                    }
                    IReadOnlyList<SidebandAttachment>? attachments = null;
                    if (received.Message.TryGetField(LxmField.FileAttachments, out var field))
                    {
                        if (field is not object?[] entries || entries.Length > 8)
                            throw new InvalidDataException("Malformed file attachment list.");
                        var accepted = new List<SidebandAttachment>();
                        long total = 0;
                        foreach (var entry in entries)
                        {
                            if (entry is not object?[] { Length: 2 } pair || pair[0] is not string name || pair[1] is not byte[] data)
                                throw new InvalidDataException("Malformed file attachment entry.");
                            total += data.Length;
                            if (total > SidebandAttachmentStore.MaximumMessageBytes)
                                throw new InvalidDataException("Message attachments exceed the 4 MiB safety limit.");
                            accepted.Add(await _attachments.StoreAsync(name, data, CancellationToken.None).ConfigureAwait(false));
                        }
                        attachments = accepted;
                    }
                    SidebandImage? image = null;
                    SidebandAudio? audio = null;
                    var mediaBytes = attachments?.Sum(item => (long)item.Length) ?? 0;
                    if (received.Message.TryGetField(LxmField.Image, out var imageField))
                    {
                        try
                        {
                            if (imageField is not object?[] { Length: 2 } pair || pair[0] is not string format || pair[1] is not byte[] data)
                                throw new InvalidDataException("Malformed LXMF image field.");
                            if (mediaBytes + data.Length > SidebandAttachmentStore.MaximumMessageBytes)
                                throw new InvalidDataException("Message media exceed the combined 4 MiB limit.");
                            var validated = SidebandMediaCodec.ValidateImage(format, data);
                            image = new(validated.Format, await _attachments.StoreAsync("image." + validated.Format, data, CancellationToken.None).ConfigureAwait(false));
                            mediaBytes += data.Length;
                        }
                        catch (Exception ex) { Report("The received image was not stored; text is retained: " + ex.Message); }
                    }
                    if (received.Message.TryGetField(LxmField.Audio, out var audioField))
                    {
                        try
                        {
                            if (audioField is not object?[] { Length: 2 } pair || pair[0] is not long mode || mode is < 0 or > 255 ||
                                pair[1] is not byte[] { Length: > 0 } data)
                                throw new InvalidDataException("Malformed LXMF audio field.");
                            if (mediaBytes + data.Length > SidebandAttachmentStore.MaximumMessageBytes)
                                throw new InvalidDataException("Message media exceed the combined 4 MiB limit.");
                            audio = new((int)mode, await _attachments.StoreAsync(mode == SidebandMediaCodec.OpusOggMode ? "audio.ogg" : "audio.c2",
                                data, CancellationToken.None).ConfigureAwait(false));
                        }
                        catch (Exception ex) { Report("The received audio was not stored; text is retained: " + ex.Message); }
                    }
                    var content = received.Message.Content ?? "[Non-text LXMF content]";
                    var stored = new SidebandChatMessage(
                        Guid.NewGuid(),
                        Convert.ToHexStringLower(received.Message.SourceHash),
                        true,
                        received.Message.Title,
                        content,
                        DateTimeOffset.FromUnixTimeMilliseconds((long)(received.Message.Timestamp * 1000)),
                        "Received",
                        attachments,
                        received.Message.Method.ToString(),
                        image,
                        audio,
                        Renderer: SidebandMessageFormatting.ReadRenderer(received.Message),
                        TelemetryHex: messageTelemetry);
                    await _store.AddMessageAsync(stored, !telemetryOnly).ConfigureAwait(false);
                    await RefreshVoicePolicyCoreAsync(CancellationToken.None).ConfigureAwait(false);
                }
                PublishState(await _store.LoadAsync().ConfigureAwait(false));
                update.ClearCalls?.TrySetResult();
            }
            catch (Exception ex)
            {
                update.ClearCalls?.TrySetException(ex);
                Report("Could not persist a Sideband conversation update: " + ex.Message);
            }
        }
    }

    private async Task RetryViaPropagationAsync(Guid messageId, LxMessage failed)
    {
        var router = _router;
        var token = _runCancellation?.Token ?? new CancellationToken(canceled: true);
        LxMessage? retry = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (router is null) throw new InvalidOperationException("The LXMF router is unavailable for propagation retry.");
            retry = SidebandDeliveryRetry.CreatePropagatedRetry(failed);
            TrackOutgoingMessage(messageId, Convert.ToHexStringLower(retry.DestinationHash), router, retry);
            await _store.UpdateMessageStateAsync(messageId, "Outbound", token, deliveryMethod: "Propagated").ConfigureAwait(false);
            var updates = _inbound;
            retry.StateChanged += changed =>
            {
                if (OutgoingMessageIsTerminal(changed)) UntrackOutgoingMessage(messageId, changed);
                if (!updates.Writer.TryWrite(new ClientUpdate(MessageId: messageId, DeliveryState: changed.State.ToString())))
                    Report("Delivery-state queue is full or closed; a propagation retry status could not be saved.");
            };
            await router.SendAsync(retry, token).ConfigureAwait(false);
        }
        catch (Exception ex) when ((ex is OperationCanceledException or ObjectDisposedException) &&
            (token.IsCancellationRequested || retry?.State == LxmState.Cancelled))
        {
            if (retry is not null) UntrackOutgoingMessage(messageId, retry);
            await _store.UpdateMessageStateAsync(messageId, "Cancelled").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (retry is not null) UntrackOutgoingMessage(messageId, retry);
            await _store.UpdateMessageStateAsync(messageId, "Failed").ConfigureAwait(false);
            Report("The one-shot propagation retry failed: " + ex.Message);
        }
    }

    private void PublishState(SidebandState state)
    {
        if (StateChanged is not { } callbacks) return;
        foreach (Action<SidebandState> callback in callbacks.GetInvocationList())
        {
            try { callback(state); }
            catch (Exception ex) { Report("A Sideband state observer failed: " + ex.Message); }
        }
    }

    private void PublishVoice(SidebandVoiceSnapshot snapshot)
    {
        if (VoiceStateChanged is not { } callbacks) return;
        foreach (Action<SidebandVoiceSnapshot> callback in callbacks.GetInvocationList())
        {
            try { callback(snapshot); }
            catch (Exception ex) { Report("A Sideband voice observer failed: " + ex.Message); }
        }
    }

    private void Report(string message)
    {
        if (ErrorReported is not { } callbacks)
        {
            System.Diagnostics.Trace.TraceError("Sideband: {0}", message);
            return;
        }

        foreach (Action<string> callback in callbacks.GetInvocationList())
        {
            try { callback(message); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError("Sideband error observer failed: {0}", ex.Message); }
        }
    }

    private sealed record PropagationCandidate(string Hash, int Hops, long Emitted);
    private sealed record ClientUpdate(ReceivedMessage? Received = null, SidebandContact? Contact = null,
        Guid MessageId = default, string? DeliveryState = null, PropagationCandidate? Propagation = null,
        LxMessage? FailedMessage = null, SidebandVoiceCallEvent? Call = null, TaskCompletionSource? ClearCalls = null,
        TelemetryDelivery? TelemetryDelivery = null, SidebandAnnounce? Announce = null, byte[]? AnnouncePacketHash = null);

    private static Channel<ClientUpdate> CreateInboundChannel() =>
        System.Threading.Channels.Channel.CreateBounded<ClientUpdate>(new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    public sealed record SidebandConnectionInfo(
        string ConfigDirectory,
        string ConfigFilePath,
        SharedInstanceRole SharedRole,
        bool SharedInstanceEnabled,
        string IdentityHash,
        string DeliveryHash);
}
