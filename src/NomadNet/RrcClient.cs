using System.Security.Cryptography;
using System.Threading.Channels;
using DotReticulum.Applications;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace NomadNet;

/// <summary>One identified RRC hub link with bounded processing, durable rooms/history, and reconnect.</summary>
public sealed class RrcClient : IAsyncDisposable
{
    private readonly ReticulumNode _node;
    private readonly Identity _identity;
    private readonly byte[] _hash;
    private readonly string _path;
    private readonly RrcSessionState _state;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _connection = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<Incoming> _incoming = System.Threading.Channels.Channel.CreateBounded<Incoming>(new BoundedChannelOptions(128)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private Task? _receiver;
    private Task? _reconnect;
    private Link? _link;
    private TaskCompletionSource _welcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _manualDisconnect = true;
    private volatile bool _autoReconnect;
    private int _disposed;
    private readonly Queue<long> _sentTimes = new();
    private readonly Dictionary<string, long> _pings = new(StringComparer.Ordinal);

    private RrcClient(ReticulumNode node, Identity identity, byte[] hash, string path)
    {
        _node = node;
        _identity = identity;
        _hash = hash;
        _path = path;
        _state = new(identity.Hash);
    }

    public Action<string>? Log { get; set; }
    public event Action<RrcChatMessage>? MessageReceived;
    public string HubHash => Convert.ToHexStringLower(_hash);

    public static async Task<RrcClient> OpenAsync(ReticulumNode node, Identity identity, string directory,
        string hubHash, CancellationToken cancellationToken = default)
    {
        hubHash = NomadNetAddress.NormalizeHash(hubHash);
        var path = Path.Combine(directory, "rrc", hubHash + ".cbor");
        var client = new RrcClient(node, identity, Convert.FromHexString(hubHash), path);
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > RrcCbor.MaximumBytes) throw new InvalidDataException("RRC saved profile is too large.");
            var data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            client.Restore(data);
        }
        return client;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await _connection.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (_link?.Status == LinkStatus.Active && _welcome.Task.IsCompletedSuccessfully) return;
            _receiver ??= ReceiveAsync();
            _manualDisconnect = false;
            _reconnect ??= ReconnectAsync();
            await EstablishAsync(linked.Token).ConfigureAwait(false);
        }
        finally { _connection.Release(); }
    }

    private async Task EstablishAsync(CancellationToken cancellationToken)
    {
        var result = await ClientLinks.ConnectAsync(_node, _hash, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (result.Link is not { } link) throw new IOException(result.Error);
        if (link.RemoteIdentity is not { } remote ||
            !CryptographicOperations.FixedTimeEquals(Destination.Create(DestinationType.Single, "rrc", ["hub"], remote).Hash, _hash))
        {
            link.Teardown();
            throw new InvalidDataException("Destination is not rrc.hub for its announced identity.");
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _link?.Teardown();
            _link = link;
            _state.ResetConnection();
            _welcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
            link.PacketReceived = (data, _) => Enqueue(link, data, false);
            link.ResourcePolicy = new ResourcePolicy { MaxDataSize = RrcSessionState.MaximumResourceBytes, MaxIncomingResources = 2,
                MaxQueuedResources = 2, MaxCompletedFileResources = 2, MemoryThreshold = RrcSessionState.MaximumResourceBytes };
            link.ResourceStrategy = ResourceStrategy.AcceptApp;
            link.ResourceAdvertised = advertisement => advertisement.DataSize is > 0 and <= RrcSessionState.MaximumResourceBytes;
            link.ResourceReceived = resource =>
            {
                if (resource.Status == ResourceStatus.Complete && resource.Data is { } data) Enqueue(link, data, true);
            };
            link.Identify(_identity);
        }
        finally { _gate.Release(); }
        try
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try { SendCore(_state.MakeHello()); }
                finally { _gate.Release(); }
                try
                {
                    await _welcome.Task.WaitAsync(TimeSpan.FromSeconds(Math.Max(3, (link.Rtt ?? 0) * Link.TrafficTimeoutFactor)),
                        cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (TimeoutException) when (attempt < 4) { }
            }
        }
        catch
        {
            link.Teardown();
            throw;
        }
    }

    private void Enqueue(Link link, byte[] data, bool resource)
    {
        if (data.Length > (resource ? RrcSessionState.MaximumResourceBytes : link.Mdu))
        { Report("RRC rejected oversized input."); return; }
        if (!_incoming.Writer.TryWrite(new(link, data.ToArray(), resource)) && !_stop.IsCancellationRequested)
            Report("RRC incoming queue is full; packet rejected.");
    }

    private async Task ReceiveAsync()
    {
        try
        {
            await foreach (var incoming in _incoming.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                await _gate.WaitAsync(_stop.Token).ConfigureAwait(false);
                RrcChatMessage? notification = null;
                try
                {
                    if (!ReferenceEquals(incoming.Link, _link)) continue;
                    var previous = _state.History.LastOrDefault();
                    if (incoming.Resource) _state.ApplyResource(incoming.Data);
                    else
                    {
                        var envelope = RrcEnvelope.Decode(incoming.Data);
                        var response = _state.Apply(envelope);
                        if (response is not null) SendCore(response);
                        if (envelope.Type == 2)
                        {
                            foreach (var room in _state.Rooms.ToArray()) SendCore(_state.MakeJoin(room));
                            _welcome.TrySetResult();
                            Report("RRC connected: " + NomadNetTerminalText.Safe(_state.HubName));
                        }
                        if (envelope.Type == 31 && envelope.Body is byte[] ping &&
                            _pings.Remove(Convert.ToHexStringLower(ping), out var sent))
                            Report($"RRC pong: {Math.Max(0, Environment.TickCount64 - sent)} ms");
                    }
                    foreach (var automatic in _state.DrainAutomaticCommands()) SendCore(automatic);
                    var latest = _state.History.LastOrDefault();
                    if (!ReferenceEquals(previous, latest)) notification = latest;
                    await PersistAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                { Report("RRC input processing failed: " + exception.Message); }
                finally { _gate.Release(); }
                if (notification is not null) Notify(notification);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task ReconnectAsync()
    {
        var attempts = 0;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _stop.Token).ConfigureAwait(false);
                if (_manualDisconnect || !_autoReconnect || _link is { Status: not LinkStatus.Closed }) { attempts = 0; continue; }
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(attempts++, 6)))), _stop.Token).ConfigureAwait(false);
                await _connection.WaitAsync(_stop.Token).ConfigureAwait(false);
                try
                {
                    if (!_manualDisconnect && _autoReconnect && (_link is null || _link.Status == LinkStatus.Closed))
                        await EstablishAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                { Report("RRC reconnect failed: " + exception.Message); }
                finally { _connection.Release(); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _manualDisconnect = true;
        await _connection.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { _link?.Teardown(); _state.ResetConnection(); await PersistAsync(cancellationToken).ConfigureAwait(false); }
            finally { _gate.Release(); }
        }
        finally { _connection.Release(); }
    }

    public async Task SetNickAsync(string nick, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (System.Text.Encoding.UTF8.GetByteCount(nick) > _state.MaximumNickBytes || nick.Any(char.IsControl))
                throw new ArgumentException("Nickname exceeds the hub's UTF-8 byte limit or contains controls.");
            _state.Nick = nick;
            await PersistAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task SetAutoReconnectAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { _autoReconnect = enabled; await PersistAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public Task JoinAsync(string room, string? key = null, CancellationToken cancellationToken = default) =>
        SendAsync(() => _state.MakeJoin(room, key), false, cancellationToken);
    public Task PartAsync(string room, CancellationToken cancellationToken = default) =>
        SendAsync(() => _state.MakePart(room), false, cancellationToken);
    public Task SendMessageAsync(string? room, string text, bool action = false, CancellationToken cancellationToken = default) =>
        SendAsync(() => _state.MakeMessage(room, text, action), true, cancellationToken);
    public async Task SetActiveRoomAsync(string? room, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _state.SetActiveRoom(room);
        }
        finally { _gate.Release(); }
    }

    public async Task MarkReadAsync(string room, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _state.MarkRead(room);
        }
        finally { _gate.Release(); }
    }

    public async Task ClearHistoryAsync(string room, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _state.ClearMessages(room);
            await PersistAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task SetHistoryPerRoomCapAsync(int cap, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _state.SetHistoryPerRoomCap(cap);
            await PersistAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task SetAutomaticDirectoryAsync(bool? list = null, bool? who = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (list.HasValue) _state.AutoList = list.Value;
            if (who.HasValue) _state.AutoWho = who.Value;
            await PersistAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    public Task PingAsync(CancellationToken cancellationToken = default) => SendAsync(() =>
    {
        var ping = RandomNumberGenerator.GetBytes(8);
        foreach (var key in _pings.Where(item => Environment.TickCount64 - item.Value > 15000).Select(item => item.Key).ToArray())
            _pings.Remove(key);
        if (_pings.Count >= 16) throw new InvalidOperationException("Too many pending RRC pings.");
        _pings[Convert.ToHexStringLower(ping)] = Environment.TickCount64;
        return RrcEnvelope.Create(30, _identity.Hash, body: ping);
    }, false, cancellationToken);

    private async Task SendAsync(Func<RrcEnvelope> create, bool rateLimited, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_state.Welcomed || _link?.Status != LinkStatus.Active) throw new InvalidOperationException("RRC hub is not connected.");
            while (_sentTimes.TryPeek(out var time) && Environment.TickCount64 - time >= 60000) _sentTimes.Dequeue();
            if (rateLimited && _sentTimes.Count >= _state.RatePerMinute) throw new InvalidOperationException("Hub message rate limit reached.");
            SendCore(create());
            if (rateLimited) _sentTimes.Enqueue(Environment.TickCount64);
            await PersistAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private void SendCore(RrcEnvelope envelope)
    {
        var link = _link ?? throw new InvalidOperationException("No RRC link.");
        var bytes = envelope.Encode();
        if (bytes.Length > link.Mdu) throw new InvalidDataException("RRC envelope exceeds link MDU; shorten the message, room or nickname.");
        if (!link.TrySend(bytes)) throw new IOException("RRC link could not send the packet.");
        _state.Sent(envelope);
    }

    public async Task<string> StatusAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return $"RRC hub: {HubHash}; connected: {_state.Welcomed && _link?.Status == LinkStatus.Active}; " +
            $"nick: {NomadNetTerminalText.Safe(_state.Nick)}; rooms: {string.Join(", ", _state.Rooms)}; " +
            $"message limit: {_state.MaximumMessageBytes} bytes; history: {_state.History.Count}; auto-reconnect: {_autoReconnect}; " +
            $"active: {_state.ActiveRoom ?? "none"}; unread: {string.Join(",", _state.UnreadRooms.Order())}; " +
            $"mentions: {string.Join(",", _state.MentionRooms.Order())}; per-room history: {_state.HistoryPerRoomCap}; " +
            $"auto-list: {_state.AutoList}; auto-who: {_state.AutoWho}"; }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<RrcChatMessage>> HistoryAsync(string? room = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return _state.History.Where(item => room is null || item.Room == RrcEnvelope.NormalizeRoom(room)).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyDictionary<string, string?>> RoomsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return new Dictionary<string, string?>(_state.AvailableRooms); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<RrcWhoEntry>> MembersAsync(string room, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (_state.Members.GetValueOrDefault(RrcEnvelope.NormalizeRoom(room)) ?? [])
                .Select(hash => new RrcWhoEntry(_state.Nicks.GetValueOrDefault(hash), hash)).ToArray();
        }
        finally { _gate.Release(); }
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        var map = new Dictionary<long, object?>
        {
            [0] = 1L, [1] = _state.Nick, [2] = _state.Rooms.Cast<object?>().ToArray(),
            [3] = _state.History.Select(item => (object?)new object?[] { item.Kind, item.Room, item.Source,
                item.Nick, item.Text, item.Timestamp, item.Mention }).ToArray(),
            [4] = _autoReconnect,
            [7] = (long)_state.HistoryPerRoomCap,
            [5] = _state.AutoList, [6] = _state.AutoWho,
        };
        var bytes = RrcCbor.Encode(map);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var staging = _path + ".new";
        try
        {
            await File.WriteAllBytesAsync(staging, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(staging, _path, true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    private void Restore(byte[] bytes)
    {
        if (RrcCbor.Decode(bytes) is not Dictionary<long, object?> map || map.GetValueOrDefault(0) is not 1L ||
            map.GetValueOrDefault(1) is not string nick || System.Text.Encoding.UTF8.GetByteCount(nick) > 4096 ||
            map.GetValueOrDefault(2) is not List<object?> rooms || rooms.Count > 256 ||
            map.GetValueOrDefault(3) is not List<object?> history || history.Count > RrcSessionState.MaximumHistory)
            throw new InvalidDataException("Malformed RRC saved profile.");
        if (map.GetValueOrDefault(4) is { } reconnectValue && reconnectValue is not bool)
            throw new InvalidDataException("Malformed RRC reconnect policy.");
        _state.Nick = nick;
        if (map.GetValueOrDefault(4) is bool reconnect) _autoReconnect = reconnect;
        if ((map.GetValueOrDefault(5) is { } listValue && listValue is not bool) ||
            (map.GetValueOrDefault(6) is { } whoValue && whoValue is not bool))
            throw new InvalidDataException("Malformed RRC automatic directory policy.");
        if (map.GetValueOrDefault(5) is bool autoList) _state.AutoList = autoList;
        if (map.GetValueOrDefault(6) is bool autoWho) _state.AutoWho = autoWho;
        if (map.GetValueOrDefault(7) is { } historyLimit)
        {
            if (historyLimit is not long cap || cap is < 0 or > RrcSessionState.MaximumHistory)
                throw new InvalidDataException("Malformed RRC history cap.");
            _state.SetHistoryPerRoomCap((int)cap);
        }
        foreach (var room in rooms)
            _state.Rooms.Add(RrcEnvelope.NormalizeRoom(room as string ?? throw new InvalidDataException("Invalid saved room."), 4096));
        foreach (var item in history)
        {
            if (item is not List<object?> { Count: 7 } record || record[0] is not string kind || record[2] is not string source ||
                record[4] is not string text || record[5] is not long timestamp || record[6] is not bool mention ||
                (record[1] is not null && record[1] is not string) || (record[3] is not null && record[3] is not string))
                throw new InvalidDataException("Malformed RRC saved history.");
            _state.Record(new(kind, record[1] as string, source, record[3] as string, text, timestamp, mention));
        }
    }

    private void Report(string text)
    {
        try { Log?.Invoke(text); }
        catch { /* Logging must not interrupt link/session processing. */ }
    }

    private void Notify(RrcChatMessage message)
    {
        foreach (var callback in MessageReceived?.GetInvocationList() ?? [])
        {
            try { ((Action<RrcChatMessage>)callback)(message); }
            catch (Exception exception) { Report("RRC message observer failed: " + exception.Message); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _manualDisconnect = true;
        _stop.Cancel();
        _incoming.Writer.TryComplete();
        await _connection.WaitAsync().ConfigureAwait(false);
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try { _link?.Teardown(); }
            finally { _gate.Release(); }
        }
        finally { _connection.Release(); }
        if (_receiver is not null) await _receiver.ConfigureAwait(false);
        if (_reconnect is not null) await _reconnect.ConfigureAwait(false);
        _stop.Dispose();
    }

    private sealed record Incoming(Link Link, byte[] Data, bool Resource);
}
