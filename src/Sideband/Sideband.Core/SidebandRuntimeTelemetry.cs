using DotReticulum.Applications;
using DotReticulum.Core;
using DotReticulum.Transport;
using V = Sideband.SidebandTelemetryValue;
using K = Sideband.SidebandTelemetryValueKind;

namespace Sideband;

internal sealed class SidebandRuntimeTelemetry : IAsyncDisposable
{
    internal const int StaleSeconds = 60;
    private readonly Func<string, CancellationToken, ValueTask<V>> _query;
    private readonly V _identity;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private int _disposed;
    private V? _data;
    private double _lastUpdate;
    private V? _received;
    private V? _sent;

    internal SidebandRuntimeTelemetry(Func<string, CancellationToken, ValueTask<V>> query,
        ReadOnlySpan<byte> identity, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (identity.Length != 16) throw new ArgumentException("A transport identity must be 16 bytes.", nameof(identity));
        _query = query;
        _identity = V.From(identity);
        _time = timeProvider ?? TimeProvider.System;
    }

    internal static SidebandRuntimeTelemetry ForNode(ReticulumNode node) => new(async (path, token) =>
    {
        var call = new Dictionary<object, object?> { ["get"] = path };
        if (path == "path_table") call["max_hops"] = (long)AnnounceEngine.MaxHops - 1;
        return V.Decode(MsgPack.Pack(await node.CallManagementAsync(call, token).ConfigureAwait(false),
            SidebandTelemetryCodec.Limits));
    }, node.Router.TransportId);

    internal async Task<V> ReadAsync(bool includeTables, CancellationToken cancellationToken = default,
        bool explicitUpdate = false)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var token = linked.Token;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            token.ThrowIfCancellationRequested();
            var now = _time.GetUtcNow().ToUnixTimeMilliseconds() / 1000d;
            var elapsed = now - _lastUpdate;
            if (_data is null || (explicitUpdate ? elapsed >= StaleSeconds : elapsed > StaleSeconds))
            {
                var stats = await _query("interface_stats", token).ConfigureAwait(false);
                var paths = await _query("path_table", token).ConfigureAwait(false);
                var links = await _query("link_count", token).ConfigureAwait(false);
                if (stats.Kind != K.Map || stats.Get("interfaces") is not { Kind: K.Array } interfaces ||
                    paths.Kind != K.Array || links.Integer is not >= 0)
                    throw new InvalidDataException("Reticulum returned an invalid telemetry snapshot.");
                var ordered = paths.Items.Select(path =>
                {
                    if (path.Kind != K.Map || path.Get("interface")?.Text is not { } name ||
                        path.Get("hops")?.Integer is not >= 0)
                        throw new InvalidDataException("Reticulum returned an invalid telemetry path.");
                    return (Path: path, Name: name, Hops: path.Get("hops")!.Integer!.Value);
                }).OrderBy(path => path.Name, StringComparer.Ordinal).ThenBy(path => path.Hops).Select(path => path.Path).ToArray();
                var rx = Number(stats, "rxb");
                var tx = Number(stats, "txb");
                var rxInstant = Number(stats, "rxs");
                var txInstant = Number(stats, "txs");
                V Average(V current, V? previous, V first)
                {
                    if (_data is null) return first;
                    if (current.Number is not { } c || previous?.Number is not { } p) return V.Nil;
                    // Subtract integral counters before conversion so a one-byte delta survives above 2^53.
                    var delta = Integral(current) is { } ci && Integral(previous) is { } pi
                        ? (double)(ci - pi) : c - p;
                    return V.From(delta / elapsed * 8);
                }
                var withoutMemory = V.Map(stats.Members.Where(pair => pair.Key.Text != "rss").ToArray());
                var data = V.Fields(
                    ("transport_enabled", V.From(stats.Get("transport_uptime") is not null)),
                    ("transport_identity", _identity),
                    ("transport_uptime", stats.Get("transport_uptime") ?? V.From(0L)),
                    ("traffic_rxb", rx), ("traffic_txb", tx),
                    ("speed_rx", Average(rx, _received, rxInstant)),
                    ("speed_tx", Average(tx, _sent, txInstant)),
                    ("speed_rx_inst", rxInstant), ("speed_tx_inst", txInstant),
                    ("memory_used", stats.Get("rss") ?? V.Nil),
                    ("ifstats", withoutMemory), ("path_table", V.Array(ordered)),
                    ("interface_count", V.From((long)interfaces.Items.Count)),
                    ("link_count", links), ("path_count", V.From((long)ordered.Length)));
                _ = SidebandTelemetryCodec.FromData(SidebandTelemetrySensorId.RnsTransport, data);
                token.ThrowIfCancellationRequested();
                _data = data;
                _lastUpdate = now;
                _received = rx;
                _sent = tx;
            }
            return includeTables ? _data : V.Map(_data.Members.Select(pair =>
                pair.Key.Text is "ifstats" or "path_table" ? KeyValuePair.Create(pair.Key, V.Nil) : pair).ToArray());
        }
        finally { _gate.Release(); }
    }

    private static Int128? Integral(V value) => value.Integer is { } signed ? signed :
        value.UnsignedInteger is { } unsigned ? unsigned : null;

    private static V Number(V stats, string key)
    {
        var value = stats.Get(key) ?? throw new InvalidDataException($"Reticulum telemetry lacks '{key}'.");
        if (!value.IsNil && value.Number is null) throw new InvalidDataException($"Reticulum telemetry '{key}' must be numeric or unavailable.");
        return value;
    }

    internal async Task ResetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { Clear(); }
        finally { _gate.Release(); }
    }

    private void Clear()
    {
        _data = null;
        _lastUpdate = 0;
        _received = null;
        _sent = null;
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        await _stop.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try { Clear(); }
        finally { _gate.Release(); _stop.Dispose(); }
    }
}
