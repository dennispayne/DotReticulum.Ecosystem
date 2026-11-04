using DotReticulum.Applications;
using DotReticulum.Core;
using DotReticulum.Transport;
using LXMF;
using V = Sideband.SidebandTelemetryValue;

namespace Sideband;

public sealed partial class SidebandClient
{
    public const int MaximumPendingCommands = 64;
    private readonly SemaphoreSlim _commandAdmission = new(MaximumPendingCommands, MaximumPendingCommands);

    public async Task<SidebandCommandAuthorization> GetCommandAuthorizationAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return SidebandRemoteCommands.Authorization(await LoadAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task ConfigureCommandAuthorizationAsync(SidebandCommandAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _store.ConfigureCommandAuthorizationAsync(authorization, cancellationToken).ConfigureAwait(false);
        PublishState(await LoadAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Queued is bounded in-memory admission, not execution or delivery. Unsent work is never replayed after restart.</summary>
    public Task<SidebandCommandQueueResult> SendCommandAsync(string deliveryHash, string content, bool propagation = false,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(QueueRemoteCommand(deliveryHash, content, propagation, cancellationToken));

    private SidebandCommandQueueResult QueueRemoteCommand(string deliveryHash, string content, bool propagation,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var commands = SidebandRemoteCommands.CreateCommandField(content);
        if (commands is null) return new(SidebandCommandQueueStatus.Empty);
        if (!SidebandContact.TryNormalizeDeliveryHash(deliveryHash, out var hash)) return new(SidebandCommandQueueStatus.NoAddress);
        if (_router is not { } router) return new(SidebandCommandQueueStatus.NotConnected);
        if (hash == router.DeliveryHashHex) return new(SidebandCommandQueueStatus.NoAddress);
        if (propagation && router.OutboundPropagationNode is null)
            throw new InvalidOperationException("Select a propagation node before sending a propagated command.");
        var remote = new RemoteCommandWork(Guid.NewGuid(), content, commands, null, propagation);
        var queued = EnqueueTelemetry(new(hash, false, CallerToken: cancellationToken, Remote: remote));
        return new(queued.Status switch
        {
            SidebandTelemetryQueueStatus.Queued => SidebandCommandQueueStatus.Queued,
            SidebandTelemetryQueueStatus.NotConnected => SidebandCommandQueueStatus.NotConnected,
            _ => SidebandCommandQueueStatus.QueueFull
        }, remote.Id);
    }

    private async Task ReceiveRemoteCommandsAsync(LxMessage message, object? commands, CancellationToken token)
    {
        var hash = Convert.ToHexStringLower(message.SourceHash);
        if (!message.SignatureValidated)
        {
            Report($"Remote commands from {hash} rejected: signature not validated.");
            return;
        }
        var state = await _store.LoadAsync(cancellationToken: token).ConfigureAwait(false);
        var conversation = ConversationPolicy(state, hash);
        var authorization = SidebandRemoteCommands.Authorization(state);
        if (authorization.IgnoreUnknown && !conversation.Exists ||
            !SidebandTelemetryPolicy.AllowRequest(state.Telemetry.Policy, conversation))
        {
            Report($"Remote commands from {hash} rejected: sender not authorized.");
            return;
        }
        var value = SafeField(commands);
        if (value.Kind != SidebandTelemetryValueKind.Array || value.Items.Count > SidebandRemoteCommands.MaximumCommands)
            throw new InvalidDataException("Commands must be a bounded array of at most 64 maps.");
        foreach (var command in value.Items)
        {
            if (command.Kind != SidebandTelemetryValueKind.Map) throw new InvalidDataException("A command must be a map.");
            if (command.Get((long)SidebandTelemetryCommand.TelemetryRequest) is { } request)
            {
                var time = request.Kind == SidebandTelemetryValueKind.Array && request.Items.Count == 2 ? request.Items[0] : request;
                var collector = request.Kind == SidebandTelemetryValueKind.Array && request.Items.Count == 2
                    ? request.Items[1].Boolean ?? throw new InvalidDataException("Invalid collector-request flag.") : true;
                if (time.Number is not { } after || !SidebandTelemetryCodec.IsValidTimebase(after, TelemetryNow))
                    throw new InvalidDataException("Invalid/future telemetry request cursor.");
                var response = collector && state.Telemetry.CollectorEnabled;
                EnqueueTelemetry(new(hash, false, Authorized: response, CollectorResponse: response,
                    After: response ? Math.Truncate(after) : null));
            }
            else if (SidebandRemoteCommands.CreateBuiltinReply(command, message.Quality, message.Rssi, message.Snr) is { } reply)
                EnqueueTelemetry(new(hash, false, Remote: new(Guid.NewGuid(), null, null, reply, false)));
            else
                Report($"Remote command from {hash} ignored: unknown command or unsupported Python plugin request.");
        }
    }

    private async Task SendRemoteCommandCoreAsync(TelemetryWork work, LxmRouter router, ReticulumNode node,
        Identity identity, CancellationToken token)
    {
        var remote = work.Remote!;
        var state = await LoadAsync(token).ConfigureAwait(false);
        if (remote.Reply is not null)
        {
            var authorization = SidebandRemoteCommands.Authorization(state);
            var conversation = ConversationPolicy(state, work.Hash);
            if (authorization.IgnoreUnknown && !conversation.Exists ||
                !SidebandTelemetryPolicy.AllowRequest(state.Telemetry.Policy, conversation))
                throw new UnauthorizedAccessException("Remote reply authorization was revoked before transmission.");
        }
        var address = Convert.FromHexString(work.Hash);
        if (remote.Reply is null)
        {
            await _store.AddMessageAsync(new(remote.Id, work.Hash, false, "Command", remote.Input!,
                DateTimeOffset.UtcNow, "Queued", DeliveryMethod: remote.Propagation ? "Propagated" : "Direct"), token).ConfigureAwait(false);
            PublishState(await LoadAsync(token).ConfigureAwait(false));
        }
        using var recipient = await node.ResolveIdentityAsync(address, TimeSpan.FromSeconds(15), token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No identity/path for remote-command peer.");
        var linked = node.Router.Links.Links.Any(link => link.IsInitiator && link.Status == LinkStatus.Active &&
            link.DestinationHash.AsSpan().SequenceEqual(address));
        var method = remote.Propagation ? LxmMethod.Propagated :
            !linked && node.Router.Engine.Known.GetRatchet(address) is not null ? LxmMethod.Opportunistic : LxmMethod.Direct;
        var fields = remote.Commands is { } commands
            ? new Dictionary<object, object?> { [LxmField.Commands] = MsgPack.Unpack(commands.Encode()) } : [];
        var message = LxMessage.Create(address, identity, remote.Reply ?? "", fields: fields, method: method);
        message.IncludeTicket = state.Contacts.Any(contact => contact.DeliveryHash == work.Hash && contact.Trusted);
        ObserveRemoteCommand(message, work);
        RegisterTelemetryOutcome(message, work, 0, state.TryPropagationOnFail && router.OutboundPropagationNode is not null && !remote.Propagation);
        await router.SendAsync(message, token).ConfigureAwait(false);
    }

    private void ObserveRemoteCommand(LxMessage message, TelemetryWork work)
    {
        if (work.Remote is not { Reply: null } remote) return;
        var updates = _inbound;
        message.StateChanged += changed =>
        {
            if (!updates.Writer.TryWrite(new ClientUpdate(MessageId: remote.Id, DeliveryState: changed.State.ToString())))
                Report("Remote-command history update queue is full or closed.");
        };
    }

    private void ReleaseTelemetryPending(TelemetryWork work)
    {
        if (_telemetryPending.TryRemove(work.Key, out _) && work.Remote is not null) _commandAdmission.Release();
    }

    private async Task FailRemoteCommandAsync(TelemetryWork work, string outcome)
    {
        if (work.Remote is not { Reply: null } remote) return;
        await _store.UpdateMessageStateAsync(remote.Id, outcome).ConfigureAwait(false);
        PublishState(await LoadAsync().ConfigureAwait(false));
    }

    private sealed record RemoteCommandWork(Guid Id, string? Input, V? Commands, string? Reply, bool Propagation);
}
