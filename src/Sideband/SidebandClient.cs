using System.Threading.Channels;
using DotReticulum.Applications;
using DotReticulum.Applications.Runtime;
using DotReticulum.Core;
using DotReticulum.Transport;
using LXMF;

namespace Sideband;

public sealed class SidebandClient : IAsyncDisposable
{
    private readonly SidebandStateStore _store;
    private readonly SidebandAttachmentStore _attachments;
    private Channel<ClientUpdate> _inbound = CreateInboundChannel();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _propagationGate = new(1, 1);
    private readonly object _disposeGate = new();
    private readonly object _cancellationGate = new();
    private Task? _disposeTask;
    private CancellationTokenSource? _runCancellation;
    private Task? _inboundTask;
    private Task? _periodicTask;
    private ReticulumNode? _node;
    private Identity? _identity;
    private LxmRouter? _router;
    private CallbackAnnounceHandler? _announceHandler;
    private CallbackAnnounceHandler? _propagationHandler;
    private int _disposed;

    public SidebandClient(SidebandStateStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _attachments = new SidebandAttachmentStore(store.DataDirectory);
    }

    public event Action<SidebandState>? StateChanged;
    public event Action<string>? ErrorReported;

    public bool IsConnected => _router is not null;
    public SidebandConnectionInfo? ConnectionInfo { get; private set; }
    public LxmPropagationTransferState SyncState => _router?.PropagationTransferState ?? LxmPropagationTransferState.Idle;
    public double SyncProgress => _router?.PropagationTransferProgress ?? 0;

    public async Task<SidebandState> LoadAsync(CancellationToken cancellationToken = default) =>
        await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task ConnectAsync(string? reticulumConfigDirectory = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_router is not null) return;
            var state = await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var config = string.IsNullOrWhiteSpace(reticulumConfigDirectory)
                ? state.ReticulumConfigDirectory
                : Path.GetFullPath(reticulumConfigDirectory);
            await _store.SetReticulumConfigDirectoryAsync(config, cancellationToken).ConfigureAwait(false);

            var node = ReticulumNode.Open(config, (_, message) => Report(message));
            var inbound = CreateInboundChannel();
            _inbound = inbound;
            Identity? identity = null;
            LxmRouter? router = null;
            CancellationTokenSource? runCancellation = null;
            try
            {
                identity = node.LoadOrCreateIdentity("sideband");
                runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                await node.StartAsync(runCancellation.Token).ConfigureAwait(false);
                router = new LxmRouter(node.Router, identity, state.DisplayName, new LxmRouterOptions
                {
                    StoragePath = _store.DataDirectory,
                    InboundQueueCapacity = 128,
                    OutboundQueueCapacity = 128,
                    DeliveryPerTransferLimit = 8 * 1024 * 1024,
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
                _inboundTask = Task.Run(() => ProcessIncomingAsync(inbound.Reader), CancellationToken.None);
                var runToken = runCancellation.Token;
                _periodicTask = Task.Run(() => ProcessPeriodicAsync(runToken), CancellationToken.None);
                await router.AnnounceAsync(cancellationToken).ConfigureAwait(false);
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
                if (_announceHandler is not null) node.Router.Engine.DeregisterHandler(_announceHandler);
                if (_propagationHandler is not null) node.Router.Engine.DeregisterHandler(_propagationHandler);
                _announceHandler = null;
                _propagationHandler = null;
                await _propagationGate.WaitAsync().ConfigureAwait(false);
                try { _router = null; _node = null; }
                finally { _propagationGate.Release(); }
                if (router is not null) await router.DisposeAsync().ConfigureAwait(false);
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
                throw;
            }
        }
        finally { _lifecycle.Release(); }
    }

    public async Task AddContactAsync(string name, string deliveryHash, CancellationToken cancellationToken = default)
    {
        var contact = SidebandContact.Create(name, deliveryHash);
        await _store.AddContactAsync(contact, cancellationToken).ConfigureAwait(false);
        PublishState(await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    public async Task SetConversationTrustAsync(string deliveryHash, bool trusted, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _store.SetConversationTrustAsync(deliveryHash, trusted, cancellationToken).ConfigureAwait(false);
        PublishState(await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    public async Task SetConversationReadAsync(string deliveryHash, bool read, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _store.SetConversationReadAsync(deliveryHash, read, cancellationToken).ConfigureAwait(false);
        PublishState(await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
    }

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

    public async Task ExportAttachmentAsync(string peerHash, Guid messageId, int index, string path, CancellationToken cancellationToken = default)
    {
        var messages = await _store.GetMessagesAsync(peerHash, cancellationToken).ConfigureAwait(false);
        var message = messages.SingleOrDefault(item => item.Id == messageId)
            ?? throw new ArgumentException("The selected conversation message was not found.", nameof(messageId));
        if (message.Attachments is not { } attachments || index < 0 || index >= attachments.Count)
            throw new ArgumentOutOfRangeException(nameof(index), "The selected attachment was not found.");
        await _attachments.ExportAsync(attachments[index], path, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendMessageAsync(string deliveryHash, string content, string? attachmentName, byte[]? attachmentData, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        if (content.Length > 65_536) throw new ArgumentException("Messages are limited to 65,536 characters.", nameof(content));
        if (!SidebandContact.TryNormalizeDeliveryHash(deliveryHash, out var normalized))
            throw new ArgumentException("An LXMF delivery address must be exactly 32 hexadecimal characters.", nameof(deliveryHash));

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var router = _router ?? throw new InvalidOperationException("Connect to Reticulum before sending messages.");
            var identity = _identity ?? throw new InvalidOperationException("The local Reticulum identity is unavailable.");
            using var owned = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _runCancellation!.Token);
            var state = await _store.LoadAsync(cancellationToken: owned.Token).ConfigureAwait(false);
            var destination = Convert.FromHexString(normalized);
            if (await _node!.ResolveIdentityAsync(destination, TimeSpan.FromSeconds(15), owned.Token).ConfigureAwait(false) is null)
                throw new InvalidOperationException("No announce or path was found for this LXMF delivery address.");

            var method = state.PropagationByDefault ? LxmMethod.Propagated : LxmMethod.Direct;
            IReadOnlyList<SidebandAttachment>? attachments = null;
            Dictionary<object, object?>? fields = null;
            if (attachmentData is not null && attachmentName is not null)
            {
                attachments = [await _attachments.StoreAsync(attachmentName, attachmentData, owned.Token).ConfigureAwait(false)];
                fields = new Dictionary<object, object?> { [LxmField.FileAttachments] = new object?[] { new object?[] { attachmentName, attachmentData } } };
            }
            var message = LxMessage.Create(destination, identity, content, fields: fields, method: method);
            message.IncludeTicket = state.Contacts.Any(contact => contact.DeliveryHash == normalized && contact.Trusted);
            var local = new SidebandChatMessage(Guid.NewGuid(), normalized, false, string.Empty, content, DateTimeOffset.UtcNow, "Queued", attachments, method.ToString());
            await _store.AddMessageAsync(local, owned.Token).ConfigureAwait(false);
            PublishState(await _store.LoadAsync(cancellationToken: owned.Token).ConfigureAwait(false));
            var updates = _inbound;
            var eligibleForRetry = state.TryPropagationOnFail && router.OutboundPropagationNode is not null;
            message.StateChanged += changed =>
            {
                var failed = SidebandDeliveryRetry.ShouldRetry(changed.State, eligibleForRetry, stampGenerationFailed: false);
                if (!updates.Writer.TryWrite(new ClientUpdate(MessageId: local.Id, DeliveryState: changed.State.ToString(),
                    FailedMessage: failed ? changed : null)))
                    Report("Delivery-state queue is full or closed; a conversation status update could not be saved.");
            };
            try { await router.SendAsync(message, owned.Token).ConfigureAwait(false); }
            catch
            {
                await updates.Writer.WriteAsync(new ClientUpdate(MessageId: local.Id,
                    DeliveryState: owned.IsCancellationRequested ? "Cancelled" : "Failed")).ConfigureAwait(false);
                throw;
            }
        }
        finally { _lifecycle.Release(); }
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

    public async Task<int> SynchronizeAsync(int maxMessages = 0, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxMessages);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var router = _router ?? throw new InvalidOperationException("Connect to Reticulum before synchronizing.");
            using var owned = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _runCancellation!.Token);
            return await router.RequestMessagesFromPropagationNodeAsync(maxMessages: maxMessages, cancellationToken: owned.Token).ConfigureAwait(false);
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
        await CancelRunAsync().ConfigureAwait(false);
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await DisconnectCoreAsync().ConfigureAwait(false); }
        finally
        {
            _lifecycle.Release();
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
        if (router is not null) await router.DisposeAsync().ConfigureAwait(false);
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
        if (LxmfAppData.DisplayName(announce.AppData) is not { Length: > 0 } displayName) return;
        var contact = new SidebandContact(Convert.ToHexStringLower(announce.DestinationHash), displayName);
        if (!_inbound.Writer.TryWrite(new ClientUpdate(Contact: contact)))
            Report("Contact-update queue is full or closed; the discovered contact was not saved.");
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
        if (!_inbound.Writer.TryWrite(new ClientUpdate(Propagation: new PropagationCandidate(
            Convert.ToHexStringLower(announce.DestinationHash), announce.Hops, data.Timebase))))
            Report("Propagation-discovery queue is full or closed; the announce was not processed.");
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
                if (update.Propagation is { } candidate)
                {
                    await ProcessPropagationAsync(candidate).ConfigureAwait(false);
                }
                else if (update.Contact is { } contact)
                {
                    await _store.AddContactAsync(contact).ConfigureAwait(false);
                }
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
                        received.Message.Method.ToString());
                    await _store.AddMessageAsync(stored).ConfigureAwait(false);
                }
                PublishState(await _store.LoadAsync().ConfigureAwait(false));
            }
            catch (Exception ex) { Report("Could not persist a Sideband conversation update: " + ex.Message); }
        }
    }

    private async Task RetryViaPropagationAsync(Guid messageId, LxMessage failed)
    {
        var router = _router;
        var token = _runCancellation?.Token ?? new CancellationToken(canceled: true);
        try
        {
            token.ThrowIfCancellationRequested();
            if (router is null) throw new InvalidOperationException("The LXMF router is unavailable for propagation retry.");
            var retry = SidebandDeliveryRetry.CreatePropagatedRetry(failed);
            await _store.UpdateMessageStateAsync(messageId, "Outbound", token, deliveryMethod: "Propagated").ConfigureAwait(false);
            var updates = _inbound;
            retry.StateChanged += changed =>
            {
                if (!updates.Writer.TryWrite(new ClientUpdate(MessageId: messageId, DeliveryState: changed.State.ToString())))
                    Report("Delivery-state queue is full or closed; a propagation retry status could not be saved.");
            };
            await router.SendAsync(retry, token).ConfigureAwait(false);
        }
        catch (Exception ex) when ((ex is OperationCanceledException or ObjectDisposedException) && token.IsCancellationRequested)
        {
            await _store.UpdateMessageStateAsync(messageId, "Cancelled").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
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
        LxMessage? FailedMessage = null);

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
