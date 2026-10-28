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
    private Channel<ReceivedMessage> _inbound = CreateInboundChannel();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _runCancellation;
    private Task? _inboundTask;
    private ReticulumNode? _node;
    private Identity? _identity;
    private LxmRouter? _router;
    private CallbackAnnounceHandler? _announceHandler;
    private int _disposed;

    public SidebandClient(SidebandStateStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public event Action<SidebandState>? StateChanged;
    public event Action<string>? ErrorReported;

    public bool IsConnected => _router is not null;
    public SidebandConnectionInfo? ConnectionInfo { get; private set; }

    public async Task<SidebandState> LoadAsync(CancellationToken cancellationToken = default) =>
        await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task ConnectAsync(string? reticulumConfigDirectory = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
                });
                router.Log = Report;
                router.MessageReceived = message =>
                {
                    if (!inbound.Writer.TryWrite(message))
                        Report("Incoming message queue is full; the message was not added to the conversation list.");
                };
                _announceHandler = new CallbackAnnounceHandler("lxmf.delivery", ReceivedDeliveryAnnounce);
                node.Router.Engine.RegisterHandler(_announceHandler);
                router.Start();
                _node = node;
                _identity = identity;
                _router = router;
                _runCancellation = runCancellation;
                _inboundTask = Task.Run(() => ProcessIncomingAsync(inbound.Reader, runCancellation.Token), CancellationToken.None);
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
                if (_announceHandler is not null) node.Router.Engine.DeregisterHandler(_announceHandler);
                _announceHandler = null;
                if (router is not null) await router.DisposeAsync().ConfigureAwait(false);
                if (runCancellation is not null) await runCancellation.CancelAsync().ConfigureAwait(false);
                if (identity is not null) identity.Dispose();
                await node.DisposeAsync().ConfigureAwait(false);
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

    public async Task SendTextAsync(string deliveryHash, string content, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        if (content.Length > 65_536) throw new ArgumentException("Messages are limited to 65,536 characters.", nameof(content));
        if (!SidebandContact.TryNormalizeDeliveryHash(deliveryHash, out var normalized))
            throw new ArgumentException("An LXMF delivery address must be exactly 32 hexadecimal characters.", nameof(deliveryHash));

        var router = _router ?? throw new InvalidOperationException("Connect to Reticulum before sending messages.");
        var identity = _identity ?? throw new InvalidOperationException("The local Reticulum identity is unavailable.");
        var destination = Convert.FromHexString(normalized);
        if (await _node!.ResolveIdentityAsync(destination, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false) is null)
            throw new InvalidOperationException("No announce or path was found for this LXMF delivery address.");

        var message = LxMessage.Create(destination, identity, content);
        var local = new SidebandChatMessage(Guid.NewGuid(), normalized, false, string.Empty, content, DateTimeOffset.UtcNow, "Queued");
        await _store.AddMessageAsync(local, cancellationToken).ConfigureAwait(false);
        PublishState(await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
        message.StateChanged += changed => _ = PersistDeliveryStateAsync(local.Id, changed.State.ToString());
        await router.SendAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<SidebandChatMessage>> GetMessagesAsync(string peerHash, CancellationToken cancellationToken = default) =>
        _store.GetMessagesAsync(peerHash, cancellationToken);

    public async Task DisconnectAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await DisconnectCoreAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await DisconnectCoreAsync().ConfigureAwait(false); }
        finally
        {
            _lifecycle.Release();
            _lifecycle.Dispose();
        }
    }

    private async Task DisconnectCoreAsync()
    {
        var node = _node;
        var router = _router;
        var identity = _identity;
        var cancellation = _runCancellation;
        if (node is not null && _announceHandler is not null)
            node.Router.Engine.DeregisterHandler(_announceHandler);
        _announceHandler = null;
        _router = null;
        _node = null;
        _identity = null;
        _runCancellation = null;
        ConnectionInfo = null;
        if (router is not null) await router.DisposeAsync().ConfigureAwait(false);
        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            _inbound.Writer.TryComplete();
            if (_inboundTask is not null)
            {
                try { await _inboundTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
                catch (TimeoutException) { Report("Timed out while draining the incoming message queue."); }
            }
            cancellation.Dispose();
        }

        _inboundTask = null;
        if (node is not null) await node.DisposeAsync().ConfigureAwait(false);
        identity?.Dispose();
    }

    private void ReceivedDeliveryAnnounce(AnnounceReceived announce)
    {
        if (LxmfAppData.DisplayName(announce.AppData) is not { Length: > 0 } displayName) return;
        var contact = new SidebandContact(Convert.ToHexStringLower(announce.DestinationHash), displayName);
        _ = PersistAnnouncedContactAsync(contact);
    }

    private async Task ProcessIncomingAsync(ChannelReader<ReceivedMessage> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var received in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var content = received.Message.Content ?? "[Non-text LXMF content]";
                var stored = new SidebandChatMessage(
                    Guid.NewGuid(),
                    Convert.ToHexStringLower(received.Message.SourceHash),
                    true,
                    received.Message.Title,
                    content,
                    DateTimeOffset.FromUnixTimeMilliseconds((long)(received.Message.Timestamp * 1000)),
                    "Received");
                try
                {
                    await _store.AddMessageAsync(stored, cancellationToken).ConfigureAwait(false);
                    PublishState(await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception ex) { Report("Could not save an incoming LXMF message: " + ex.Message); }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task PersistAnnouncedContactAsync(SidebandContact contact)
    {
        try
        {
            await _store.AddContactAsync(contact).ConfigureAwait(false);
            PublishState(await _store.LoadAsync().ConfigureAwait(false));
        }
        catch (Exception ex) { Report("Could not save a discovered LXMF contact: " + ex.Message); }
    }

    private async Task PersistDeliveryStateAsync(Guid id, string state)
    {
        try
        {
            await _store.UpdateMessageStateAsync(id, state).ConfigureAwait(false);
            PublishState(await _store.LoadAsync().ConfigureAwait(false));
        }
        catch (Exception ex) { Report("Could not update message delivery state: " + ex.Message); }
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

    private static Channel<ReceivedMessage> CreateInboundChannel() =>
        System.Threading.Channels.Channel.CreateBounded<ReceivedMessage>(new BoundedChannelOptions(128)
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
