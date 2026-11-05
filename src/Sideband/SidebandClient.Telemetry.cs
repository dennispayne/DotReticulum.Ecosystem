using System.Collections.Concurrent;
using System.Threading.Channels;
using DotReticulum.Applications;
using DotReticulum.Applications.Runtime;
using DotReticulum.Core;
using DotReticulum.Transport;
using LXMF;
using V = Sideband.SidebandTelemetryValue;
using S = Sideband.SidebandTelemetrySensorId;

namespace Sideband;

public sealed partial class SidebandClient
{
    private readonly SemaphoreSlim _telemetryStoreGate = new(1, 1);
    private readonly SemaphoreSlim _telemetryRefreshGate = new(1, 1);
    private SidebandRuntimeTelemetry? _runtimeTelemetry;
    private readonly ConcurrentDictionary<string, byte> _telemetryPending = new(StringComparer.Ordinal);
    private SidebandTelemetryStore? _telemetryStore;
    private Channel<TelemetryWork>? _telemetryOutbound;
    private Task? _telemetryOutboundTask;
    private Task? _telemetryScheduleTask;
    private SidebandTelemetryReport? _localTelemetry;

    public async Task ConfigureTelemetryAsync(SidebandTelemetryConfiguration config, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _telemetryRefreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await _store.ConfigureTelemetryAsync(config, cancellationToken).ConfigureAwait(false);
            _mqtt?.Configure(config.Mqtt);
            await RefreshLocalTelemetryCoreAsync(cancellationToken).ConfigureAwait(false);
            PublishState(await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
        }
        finally { _telemetryRefreshGate.Release(); }
    }

    public async Task SetConversationTelemetryAsync(string deliveryHash, bool sendTelemetry, bool allowRequests,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _store.SetConversationTelemetryAsync(deliveryHash, sendTelemetry, allowRequests, cancellationToken).ConfigureAwait(false);
        PublishState(await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<SidebandTelemetryStoredReport>> GetTelemetryAsync(SidebandTelemetryQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        var state = await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var storage = await TelemetryStoreAsync(cancellationToken).ConfigureAwait(false);
        var limit = query?.Limit ?? 256;
        if (limit is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(query), "Application telemetry queries are limited to 256 reports.");
        var reports = await storage.QueryAsync(state.Telemetry.DisplayTrustedOnly
            ? (query ?? new()) with { Limit = 4096 } : query, cancellationToken).ConfigureAwait(false);
        return state.Telemetry.DisplayTrustedOnly
            ? reports.Where(r => r.SourceHash == state.OwnDeliveryHash || state.Contacts.Any(c => c.DeliveryHash == r.SourceHash && c.Trusted)).Take(limit).ToArray()
            : reports;
    }

    public async Task<SidebandTelemetryReport?> RefreshLocalTelemetryAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _telemetryRefreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return await RefreshLocalTelemetryCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _telemetryRefreshGate.Release(); }
    }

    private async Task<SidebandTelemetryReport?> RefreshLocalTelemetryCoreAsync(CancellationToken cancellationToken)
    {
        using var operation = RuntimeOperationCancellation(cancellationToken);
        cancellationToken = operation.Token;
        var state = await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var config = state.Telemetry;
        if ((!config.Enabled || !config.RnsTransportEnabled) && _runtimeTelemetry is { } inactive)
            await inactive.ResetAsync(cancellationToken).ConfigureAwait(false);
        if (!config.Enabled) { _localTelemetry = null; return null; }
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var readings = config.ManualSensors.ToDictionary(s => s.SensorId,
            s => SidebandTelemetryCodec.FromWireValue(s.SensorId, V.Decode(Convert.FromHexString(s.WireHex))));
        if (config.InformationEnabled)
            readings[(byte)S.Information] = SidebandTelemetryCodec.FromData(S.Information, V.Fields(("contents", V.From(config.Information))));
        if (config.FixedLocationEnabled)
            readings[(byte)S.Location] = SidebandTelemetryCodec.FromLocation(new(config.FixedLocation.Latitude,
                config.FixedLocation.Longitude, config.FixedLocation.Altitude, 0, 0, 0, now));
        if (config.RnsTransportEnabled)
            readings[(byte)S.RnsTransport] = _runtimeTelemetry is { } runtime
                ? SidebandTelemetryCodec.FromData(S.RnsTransport,
                    await runtime.ReadAsync(config.RnsTransportIncludeTables, cancellationToken).ConfigureAwait(false))
                : new((byte)S.RnsTransport, "rns_transport", SidebandTelemetryStatus.Unavailable,
                    V.Nil, V.Nil, [], [], error: "Connect before collecting Reticulum runtime telemetry.");
        var report = SidebandTelemetryCodec.Create(now, readings.Values.ToArray());
        _localTelemetry = report;
        if ((_router?.DeliveryHashHex ?? state.OwnDeliveryHash) is { } own)
        {
            var storage = await TelemetryStoreAsync(cancellationToken).ConfigureAwait(false);
            await storage.AppendAsync(own, SidebandTelemetryCodec.Encode(report), TelemetryNow, cancellationToken).ConfigureAwait(false);
            QueueTelemetryToMqtt(own, report);
        }
        return report;
    }

    public async Task ClearTelemetryAsync(string sourceHash, CancellationToken cancellationToken = default)
    {
        var storage = await TelemetryStoreAsync(cancellationToken).ConfigureAwait(false);
        await storage.ClearSourceAsync(sourceHash, cancellationToken).ConfigureAwait(false);
        PublishState(await _store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    public Task<SidebandTelemetryQueueResult> SendLatestTelemetryAsync(string deliveryHash, CancellationToken cancellationToken = default) =>
        QueueTelemetryAsync(deliveryHash, request: false, cancellationToken);

    public Task<SidebandTelemetryQueueResult> RequestTelemetryAsync(string deliveryHash, CancellationToken cancellationToken = default) =>
        QueueTelemetryAsync(deliveryHash, request: true, cancellationToken);

    private async Task<SidebandTelemetryQueueResult> QueueTelemetryAsync(string deliveryHash, bool request, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        token.ThrowIfCancellationRequested();
        if (!SidebandContact.TryNormalizeDeliveryHash(deliveryHash, out var hash))
            return new(SidebandTelemetryQueueStatus.NoAddress);
        if (_router is not { } router) return new(SidebandTelemetryQueueStatus.NotConnected);
        if (hash == router.DeliveryHashHex) return new(SidebandTelemetryQueueStatus.NoAddress);
        var state = await _store.LoadAsync(cancellationToken: token).ConfigureAwait(false);
        if (!request)
        {
            var report = await RefreshLocalTelemetryAsync(token).ConfigureAwait(false);
            if (report is null || !(hash == state.Telemetry.Collector ||
                SidebandTelemetryPolicy.ShouldSendTelemetry(state.Telemetry.Policy, ConversationPolicy(state, hash))))
                return new(SidebandTelemetryQueueStatus.NothingToSend);
            if (report.TimestampUtcSeconds <= ValidPeer(state, hash).LastSendSuccessTimebase)
                return new(SidebandTelemetryQueueStatus.AlreadySent);
        }
        return EnqueueTelemetry(new(hash, request, CollectorRequest: request && hash == state.Telemetry.Collector, CallerToken: token));
    }

    private SidebandTelemetryQueueResult EnqueueTelemetry(TelemetryWork work)
    {
        var channel = _telemetryOutbound;
        if (channel is null || _router is null) return new(SidebandTelemetryQueueStatus.NotConnected);
        if (work.Remote is not null && !_commandAdmission.Wait(0))
        {
            Report("Remote command admission is full (64 pending operations).");
            return new(SidebandTelemetryQueueStatus.QueueFull);
        }
        if (!_telemetryPending.TryAdd(work.Key, 0))
        {
            if (work.Remote is not null) _commandAdmission.Release();
            return new(SidebandTelemetryQueueStatus.InProgress);
        }
        if (channel.Writer.TryWrite(work)) return new(SidebandTelemetryQueueStatus.Queued);
        ReleaseTelemetryPending(work);
        Report("Telemetry outbound queue is full or closed.");
        return new(SidebandTelemetryQueueStatus.QueueFull);
    }

    private async Task<SidebandTelemetryStore> TelemetryStoreAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _telemetryStoreGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _telemetryStore ??= await SidebandTelemetryStore.OpenAsync(Path.Combine(_store.DataDirectory, "telemetry"), new() { MaxQueryRecords = 4096 },
                cancellationToken: token).ConfigureAwait(false);
        }
        finally { _telemetryStoreGate.Release(); }
    }

    private async Task StartTelemetryAsync(LxmRouter router, ReticulumNode node, Identity identity, CancellationToken token)
    {
        await _telemetryRefreshGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var state = await _store.LoadAsync(cancellationToken: token).ConfigureAwait(false);
            StartMqtt(state.Telemetry.Mqtt, token);
            _runtimeTelemetry = SidebandRuntimeTelemetry.ForNode(node);
            var outbound = System.Threading.Channels.Channel.CreateBounded<TelemetryWork>(new BoundedChannelOptions(64)
            { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
            _telemetryOutbound = outbound;
            _telemetryOutboundTask = Task.Run(() => ProcessTelemetryOutboundAsync(outbound.Reader, router, node, identity, token), CancellationToken.None);
            _telemetryScheduleTask = Task.Run(() => ProcessTelemetryScheduleAsync(token), CancellationToken.None);
        }
        finally { _telemetryRefreshGate.Release(); }
    }

    private async Task StopTelemetryAsync()
    {
        try { await StopMqttAsync().ConfigureAwait(false); }
        finally
        {
            await StopTelemetryWorkersAsync().ConfigureAwait(false);
        }
    }

    private async Task StopTelemetryWorkersAsync()
    {
        _telemetryOutbound?.Writer.TryComplete();
        _telemetryOutbound = null;
        var runtime = Interlocked.Exchange(ref _runtimeTelemetry, null);
        if (runtime is not null) await runtime.DisposeAsync().ConfigureAwait(false);
        if (_telemetryOutboundTask is { } work) await work.ConfigureAwait(false);
        if (_telemetryScheduleTask is { } schedule) await schedule.ConfigureAwait(false);
        _telemetryOutboundTask = null;
        _telemetryScheduleTask = null;
        await _telemetryRefreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _localTelemetry = null;
        }
        finally { _telemetryRefreshGate.Release(); }
        foreach (var key in _telemetryPending.Keys)
            if (_telemetryPending.TryRemove(key, out _) && key.StartsWith("command:", StringComparison.Ordinal)) _commandAdmission.Release();
    }

    private async Task ProcessTelemetryOutboundAsync(ChannelReader<TelemetryWork> reader, LxmRouter router,
        ReticulumNode node, Identity identity, CancellationToken token)
    {
        try
        {
            await foreach (var work in reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                var registered = false;
                try
                {
                    using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, work.CallerToken);
                    var ct = operation.Token;
                    ct.ThrowIfCancellationRequested();
                    if (work.Remote is not null && work.Retry is null)
                    {
                        await SendRemoteCommandCoreAsync(work, router, node, identity, ct).ConfigureAwait(false);
                        registered = true;
                        continue;
                    }
                    var state = await _store.LoadAsync(cancellationToken: ct).ConfigureAwait(false);
                    var peer = ValidPeer(state, work.Hash);
                    var config = state.Telemetry;
                    if (work.Retry is null && work.Remote is null)
                        await _store.RecordTelemetryAttemptAsync(work.Hash, work.Request, work.Live, work.Scheduled, TelemetryNow, ct).ConfigureAwait(false);
                    var address = Convert.FromHexString(work.Hash);
                    using var remote = await node.ResolveIdentityAsync(address, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                    if (remote is null) throw new InvalidOperationException("No identity/path for telemetry peer.");
                    if (work.Retry is not null)
                    {
                        var retry = SidebandDeliveryRetry.CreatePropagatedRetry(work.Retry);
                        ObserveRemoteCommand(retry, work);
                        RegisterTelemetryOutcome(retry, work with { Retry = null }, work.Timebase, false);
                        await router.SendAsync(retry, ct).ConfigureAwait(false);
                        registered = true;
                        continue;
                    }
                    var linkAvailable = node.Router.Links.Links.Any(l => l.IsInitiator && l.Status == LinkStatus.Active &&
                        l.DestinationHash.AsSpan().SequenceEqual(address));
                    var ratchet = node.Router.Engine.Known.GetRatchet(address) is not null;
                    var fields = new Dictionary<object, object?>();
                    SidebandTelemetryOutboundDecision decision;
                    if (work.Request)
                    {
                        decision = SidebandTelemetryPolicy.EvaluateRequest(config.Policy, new(TelemetryNow)
                        {
                            LastReceivedTimebase = peer.LastReceivedTimebase, IsCollectorRequest = work.CollectorRequest,
                            IsLiveTrack = work.Live, LinkAvailable = linkAvailable, RatchetAvailable = ratchet,
                            PropagationNodeAvailable = router.OutboundPropagationNode is not null
                        });
                        fields[LxmField.Commands] = new object?[] { new Dictionary<object, object?>
                        { [(long)SidebandTelemetryCommand.TelemetryRequest] = new object?[] { decision.Timebase, work.CollectorRequest } } };
                    }
                    else
                    {
                        var own = await RefreshLocalTelemetryAsync(ct).ConfigureAwait(false);
                        var streams = work.After is { } after
                            ? await CreateTelemetryStreamAsync(state, work.Hash, after, ct).ConfigureAwait(false) : null;
                        decision = SidebandTelemetryPolicy.EvaluateSend(config.Policy, ConversationPolicy(state, work.Hash), new(TelemetryNow)
                        {
                            HasPackedTelemetry = own is not null, HasReadings = own is not null, OwnTimestamp = own?.TimestampUtcSeconds ?? 0,
                            LastSuccessTimestamp = peer.LastSendSuccessTimebase, TargetIsCollector = config.Collector == work.Hash,
                            AuthorizedRequest = work.Authorized, CollectorResponse = work.CollectorResponse,
                            OwnSourceExcluded = config.ExcludedSources.Contains(router.DeliveryHashHex, StringComparer.Ordinal),
                            StreamTimebases = streams?.Select(s => Convert.ToDouble(s[1], System.Globalization.CultureInfo.InvariantCulture)).ToArray(),
                            LinkAvailable = linkAvailable, RatchetAvailable = ratchet, PropagationNodeAvailable = router.OutboundPropagationNode is not null
                        });
                        if (decision.Status != SidebandTelemetryAdmission.Sent) continue;
                        if (decision.IncludeOwnTelemetry) fields[LxmField.Telemetry] = SidebandTelemetryCodec.Encode(own!);
                        if (decision.IncludeStream) fields[LxmField.TelemetryStream] = streams!.ToArray();
                        if (decision.IncludeAppearance) fields[LxmField.IconAppearance] = AppearanceField(config.Appearance);
                    }
                    var message = LxMessage.Create(address, identity, "", fields: fields, method: decision.Method);
                    message.IncludeTicket = decision.IncludeTicket;
                    RegisterTelemetryOutcome(message, work, decision.Timebase ?? 0, decision.TryPropagationOnFail);
                    await router.SendAsync(message, ct).ConfigureAwait(false);
                    registered = true;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested || work.CallerToken.IsCancellationRequested)
                { await FailRemoteCommandAsync(work, "Cancelled").ConfigureAwait(false); }
                catch (Exception ex)
                {
                    await FailRemoteCommandAsync(work, "Failed").ConfigureAwait(false);
                    Report($"{(work.Remote is null ? "Telemetry" : "Remote command")} {(work.Request ? "request" : "send")} to {work.Hash} failed: {ex.Message}");
                }
                finally { if (!registered) ReleaseTelemetryPending(work); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void RegisterTelemetryOutcome(LxMessage message, TelemetryWork work, double timebase, bool retry)
    {
        var updates = _inbound;
        var concluded = 0;
        message.StateChanged += changed =>
        {
            if (changed.State == LxmState.Sent && changed.DesiredMethod != LxmMethod.Propagated) return;
            if (changed.State is not (LxmState.Delivered or LxmState.Sent or LxmState.Failed or LxmState.Cancelled or LxmState.Rejected) ||
                Interlocked.Exchange(ref concluded, 1) != 0) return;
            if (!updates.Writer.TryWrite(new ClientUpdate(TelemetryDelivery: new(work, changed, timebase, retry))))
            {
                if (work.OwnsPending) ReleaseTelemetryPending(work);
                Report("Telemetry delivery-state queue is full or closed; the timebase was not advanced.");
            }
        };
    }

    private async Task CompleteTelemetryAsync(TelemetryDelivery delivery)
    {
        var work = delivery.Work;
        if (work.OwnsPending) ReleaseTelemetryPending(work);
        if (work.Remote is { Reply: null } command)
            await _store.RecordCommandMethodAsync(command.Id, delivery.Message.Method).ConfigureAwait(false);
        if (delivery.Message.State == LxmState.Failed && delivery.Retry && _router?.OutboundPropagationNode is not null)
        {
            EnqueueTelemetry(work with { Retry = delivery.Message, Timebase = delivery.Timebase });
            return;
        }
        if (work.Remote is not null)
        {
            if (delivery.Message.State is LxmState.Failed or LxmState.Rejected or LxmState.Cancelled)
                Report($"Remote command to {work.Hash} ended as {delivery.Message.State}.");
            return;
        }
        if (delivery.Message.State == LxmState.Delivered)
            await _store.UpdateTelemetryPeerAsync(work.Hash, p => work.Request
                ? p with { LastRequestSuccessTimebase = Math.Truncate(TelemetryNow) }
                : p with
                {
                    LastSendSuccessTimebase = Math.Max(SidebandTelemetryCodec.IsValidTimebase(p.LastSendSuccessTimebase, TelemetryNow)
                        ? p.LastSendSuccessTimebase : 0, delivery.Timebase),
                    ScheduledSendAttempts = 0, ScheduledSendBlockedUntil = 0
                }).ConfigureAwait(false);
        else if (delivery.Message.State is LxmState.Failed or LxmState.Rejected or LxmState.Cancelled)
            Report($"Telemetry {(work.Request ? "request" : "send")} to {work.Hash} ended as {delivery.Message.State}.");
    }

    private async Task<Dictionary<object, object?>> OrdinaryTelemetryFieldsAsync(SidebandState state, string hash, CancellationToken token)
    {
        var report = await RefreshLocalTelemetryAsync(token).ConfigureAwait(false);
        var decision = SidebandTelemetryPolicy.EvaluateFields(state.Telemetry.Policy, ConversationPolicy(state, hash),
            new(report is not null, report?.TimestampUtcSeconds ?? 0, ValidPeer(state, hash).LastSendSuccessTimebase));
        var fields = new Dictionary<object, object?>();
        if (decision.IncludeTelemetry) fields[LxmField.Telemetry] = SidebandTelemetryCodec.Encode(report!);
        if (decision.IncludeAppearance) fields[LxmField.IconAppearance] = AppearanceField(state.Telemetry.Appearance);
        return fields;
    }

    private async Task<List<object?[]>> CreateTelemetryStreamAsync(SidebandState state, string recipient, double after, CancellationToken token)
    {
        var storage = await TelemetryStoreAsync(token).ConfigureAwait(false);
        var records = await storage.QueryAsync(new(AfterUtcSeconds: after, Limit: SidebandTelemetryPolicy.MaximumStreamEntries), token).ConfigureAwait(false);
        var added = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<object?[]>();
        long bytes = 0;
        foreach (var record in records)
        {
            if (!SidebandTelemetryPolicy.IncludeCollectorSource(record.SourceHash == recipient,
                state.Telemetry.ExcludedSources.Contains(record.SourceHash, StringComparer.Ordinal),
                state.Telemetry.RequestsOnlySendLatest, added.Contains(record.SourceHash))) continue;
            var packed = record.GetPackedTelemetry();
            if (bytes + packed.Length + 256 > SidebandTelemetryCodec.MaxEncodedBytes) break;
            bytes += packed.Length + 256;
            added.Add(record.SourceHash);
            var appearance = state.Contacts.FirstOrDefault(c => c.DeliveryHash == record.SourceHash)?.Appearance;
            if (record.SourceHash == state.OwnDeliveryHash) appearance = state.Telemetry.Appearance;
            result.Add([Convert.FromHexString(record.SourceHash), MsgPack.Unpack(record.TimestampValue.Encode()), packed,
                appearance is null ? null : AppearanceField(appearance)]);
        }
        return result;
    }

    private async Task<bool> ReceiveTelemetryAsync(LxMessage message, CancellationToken token)
    {
        var hasTelemetry = message.TryGetField(LxmField.Telemetry, out var packed);
        var hasStream = message.TryGetField(LxmField.TelemetryStream, out var stream);
        var hasCommands = message.TryGetField(LxmField.Commands, out var commands);
        var hasAppearance = message.TryGetField(LxmField.IconAppearance, out var appearance);
        if (!(hasTelemetry || hasStream || hasCommands || hasAppearance)) return false;
        if (hasCommands)
        {
            await ReceiveRemoteCommandsAsync(message, commands, token).ConfigureAwait(false);
            return true;
        }
        // Stricter than primary ordinary-message ingestion: never act on unsigned/unverified metadata.
        if (!message.SignatureValidated) { Report("Unverified telemetry/commands/appearance were ignored."); return true; }
        var hash = Convert.ToHexStringLower(message.SourceHash);
        var state = await _store.LoadAsync(cancellationToken: token).ConfigureAwait(false);
        var config = state.Telemetry;
        var conversation = ConversationPolicy(state, hash);
        if (SidebandTelemetryPolicy.AcceptReceivedTelemetry(config.Policy, conversation))
        {
            if (hasAppearance && TryAppearance(appearance, out var peerAppearance))
                await _store.SetTelemetryAppearanceAsync(hash, peerAppearance!, false, token).ConfigureAwait(false);
            var own = _localTelemetry?.Get(S.Location)?.Location;
            if (own is null && config.Enabled && config.FixedLocationEnabled)
                own = new(config.FixedLocation.Latitude, config.FixedLocation.Longitude, config.FixedLocation.Altitude, 0, 0, 0, TelemetryNow);
            var receiver = _router?.DeliveryHashHex ?? state.OwnDeliveryHash ?? throw new InvalidOperationException("No receiver delivery identity.");
            if (hasTelemetry)
            {
                if (packed is not byte[] data) throw new InvalidDataException("Telemetry field must be packed binary.");
                var report = SidebandTelemetryCodec.Decode(data);
                var enriched = SidebandTelemetryObservations.Apply(report, receiver, hash, false, own, message.Rssi, message.Snr, message.Quality);
                var storage = await TelemetryStoreAsync(token).ConfigureAwait(false);
                var accepted = await storage.AppendAsync(hash, SidebandTelemetryCodec.Encode(enriched), TelemetryNow, token).ConfigureAwait(false);
                if (accepted == SidebandTelemetryAppendResult.Added) QueueTelemetryToMqtt(hash, enriched);
            }
            if (hasStream)
            {
                var value = SafeField(stream);
                if (value.Kind != SidebandTelemetryValueKind.Array || value.Items.Count > SidebandTelemetryPolicy.MaximumStreamEntries)
                    throw new InvalidDataException("Telemetry stream must be a bounded array.");
                var storage = await TelemetryStoreAsync(token).ConfigureAwait(false);
                var cursor = ValidPeer(state, hash).LastReceivedTimebase;
                foreach (var entry in value.Items)
                {
                    try
                    {
                        if (entry.Items.Count != 4 || entry.Items[0].GetBinary() is not { Length: 16 } source ||
                            entry.Items[1].Number is not { } outer || !SidebandTelemetryCodec.IsValidTimebase(outer, TelemetryNow) ||
                            entry.Items[2].GetBinary() is not { } payload)
                            throw new InvalidDataException("Malformed/future stream entry.");
                        var sourceHash = Convert.ToHexStringLower(source);
                        if (sourceHash == receiver) continue;
                        var report = SidebandTelemetryCodec.Decode(payload);
                        if (report.TimestampUtcSeconds != outer)
                            throw new InvalidDataException("Stream outer timebase does not match the source report.");
                        var enriched = SidebandTelemetryObservations.Apply(report, receiver, hash, true);
                        var accepted = await storage.AppendAsync(sourceHash, SidebandTelemetryCodec.Encode(enriched), TelemetryNow, token).ConfigureAwait(false);
                        if (accepted != SidebandTelemetryAppendResult.InvalidTimestamp)
                        {
                            if (accepted == SidebandTelemetryAppendResult.Added) QueueTelemetryToMqtt(sourceHash, enriched);
                            cursor = Math.Max(cursor, outer);
                            if (TryAppearance(MsgPack.Unpack(entry.Items[3].Encode()), out var sourceAppearance))
                                await _store.SetTelemetryAppearanceAsync(sourceHash, sourceAppearance!, true, token).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidDataException or OverflowException)
                    { Report("Telemetry stream entry rejected: " + ex.Message); }
                }
                await _store.UpdateTelemetryPeerAsync(hash, p => p with { LastReceivedTimebase =
                    Math.Max(SidebandTelemetryCodec.IsValidTimebase(p.LastReceivedTimebase, TelemetryNow) ? p.LastReceivedTimebase : 0, cursor) }, token).ConfigureAwait(false);
            }
        }
        return hasTelemetry || hasStream || hasCommands;
    }

    private async Task ProcessTelemetryScheduleAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var lastClean = 0d;
        long? lastLocalUpdate = null;
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try
                {
                    var state = await _store.LoadAsync(cancellationToken: token).ConfigureAwait(false);
                    var config = state.Telemetry;
                    var now = TelemetryNow;
                    var refreshInterval = config.Mqtt.Enabled ? MqttLocalRefreshSeconds : SidebandRuntimeTelemetry.StaleSeconds;
                    if (config.Enabled && (config.RnsTransportEnabled || config.Mqtt.Enabled) &&
                        (lastLocalUpdate is null ||
                         TimeProvider.System.GetElapsedTime(lastLocalUpdate.Value) >= TimeSpan.FromSeconds(refreshInterval)))
                    {
                        await RefreshLocalTelemetryAsync(token).ConfigureAwait(false);
                        lastLocalUpdate = TimeProvider.System.GetTimestamp();
                    }
                    if (config.Enabled && now - lastClean >= SidebandTelemetryRetention.PrimaryCleanIntervalSeconds)
                    {
                        var storage = await TelemetryStoreAsync(token).ConfigureAwait(false);
                        // Deliberate safety correction: primary uses now-240 and deletes valid recent reports.
                        await storage.PruneAsync(new(now - SidebandTelemetryRetention.PrimaryKeepSeconds,
                            now + SidebandTelemetryRetention.PrimaryFutureGraceSeconds), token).ConfigureAwait(false);
                        lastClean = now;
                    }
                    if (config.Enabled && config.Collector is { } collector && collector != _router?.DeliveryHashHex)
                    {
                        var peer = ValidPeer(state, collector);
                        var sendPending = _telemetryPending.ContainsKey("send:" + collector);
                        var decision = SidebandTelemetrySchedule.Evaluate(now, config.SendToCollector && !sendPending,
                            config.RequestFromCollector, config.SendIntervalSeconds, config.RequestIntervalSeconds,
                            peer.LastSendSuccessTimebase, peer.LastRequestSuccessTimebase, peer.ScheduledSendAttempts,
                            peer.ScheduledSendBlockedUntil);
                        if (decision.SendBlockedUntil != peer.ScheduledSendBlockedUntil)
                            await _store.UpdateTelemetryPeerAsync(collector, p => p with
                            { ScheduledSendAttempts = decision.SendAttempts, ScheduledSendBlockedUntil = decision.SendBlockedUntil }, token).ConfigureAwait(false);
                        if (decision.Send)
                            EnqueueTelemetry(new(collector, false, Authorized: config.SendAllToCollector,
                                After: config.SendAllToCollector ? peer.LastSendSuccessTimebase : null, Scheduled: true));
                        if (decision.Request && now > peer.LastRequestAttemptUtcSeconds + Math.Min(60, config.RequestIntervalSeconds))
                            EnqueueTelemetry(new(collector, true, CollectorRequest: true));
                    }
                    foreach (var track in config.Tracking.Where(t => t.UntilUtcSeconds > now))
                    {
                        var peer = ValidPeer(state, track.DeliveryHash);
                        var last = SidebandTelemetryCodec.IsValidTimebase(track.LastRequestUtcSeconds, now) ? track.LastRequestUtcSeconds : 0;
                        if (now > Math.Max(last, peer.LastRequestAttemptUtcSeconds) + track.IntervalSeconds)
                            EnqueueTelemetry(new(track.DeliveryHash, true, Live: true));
                    }
                    if (config.Tracking.Any(t => t.UntilUtcSeconds <= now))
                    {
                        await _store.PruneTelemetryTrackingAsync(now, token).ConfigureAwait(false);
                        PublishState(await _store.LoadAsync(cancellationToken: token).ConfigureAwait(false));
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex) { Report("Scheduled telemetry failed: " + ex.Message); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static double TelemetryNow => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
    private static SidebandTelemetryConversationPolicy ConversationPolicy(SidebandState state, string hash) =>
        state.Contacts.FirstOrDefault(c => c.DeliveryHash == hash && c.ConversationKnown) is { } contact
            ? new(true, contact.Trusted ? 1 : 0, contact.SendTelemetry, contact.AllowTelemetryRequests) : new();
    private static SidebandTelemetryPeerState ValidPeer(SidebandState state, string hash)
    {
        var peer = state.TelemetryPeers?.FirstOrDefault(p => p.DeliveryHash == hash) ?? new(hash);
        return peer with
        {
            LastReceivedTimebase = SidebandTelemetryCodec.IsValidTimebase(peer.LastReceivedTimebase, TelemetryNow) ? peer.LastReceivedTimebase : 0,
            LastSendSuccessTimebase = SidebandTelemetryCodec.IsValidTimebase(peer.LastSendSuccessTimebase, TelemetryNow) ? peer.LastSendSuccessTimebase : 0,
            LastSendAttemptUtcSeconds = SidebandTelemetryCodec.IsValidTimebase(peer.LastSendAttemptUtcSeconds, TelemetryNow) ? peer.LastSendAttemptUtcSeconds : 0,
            LastRequestAttemptUtcSeconds = SidebandTelemetryCodec.IsValidTimebase(peer.LastRequestAttemptUtcSeconds, TelemetryNow) ? peer.LastRequestAttemptUtcSeconds : 0,
            LastRequestSuccessTimebase = SidebandTelemetryCodec.IsValidTimebase(peer.LastRequestSuccessTimebase, TelemetryNow) ? peer.LastRequestSuccessTimebase : 0,
            ScheduledSendBlockedUntil = peer.ScheduledSendBlockedUntil <= TelemetryNow + 31_536_000 ? peer.ScheduledSendBlockedUntil : 0
        };
    }
    private static object?[] AppearanceField(SidebandTelemetryAppearance appearance) =>
        [appearance.Icon, Convert.FromHexString(appearance.Foreground), Convert.FromHexString(appearance.Background)];
    private static V SafeField(object? field) => V.Decode(MsgPack.Pack(field, SidebandTelemetryCodec.Limits));
    private static bool TryAppearance(object? field, out SidebandTelemetryAppearance? appearance)
    {
        appearance = null;
        if (field is not object?[] { Length: 3 } parts || parts[0] is not string icon ||
            parts[1] is not byte[] { Length: 3 } foreground || parts[2] is not byte[] { Length: 3 } background) return false;
        var candidate = new SidebandTelemetryAppearance(icon, Convert.ToHexStringLower(foreground), Convert.ToHexStringLower(background));
        try { SidebandTelemetryConfiguration.ValidateAppearance(candidate); appearance = candidate; return true; }
        catch (InvalidDataException) { return false; }
    }
    private sealed record TelemetryWork(string Hash, bool Request, bool Authorized = false, bool CollectorResponse = false,
        bool CollectorRequest = false, double? After = null, bool Live = false, CancellationToken CallerToken = default,
        LxMessage? Retry = null, double Timebase = 0, bool Scheduled = false, bool OwnsPending = true, RemoteCommandWork? Remote = null)
    {
        public string Key => Remote is { } remote ? "command:" + remote.Id : (Request ? "request:" : "send:") + Hash;
    }
    private sealed record TelemetryDelivery(TelemetryWork Work, LxMessage Message, double Timebase, bool Retry);
}
