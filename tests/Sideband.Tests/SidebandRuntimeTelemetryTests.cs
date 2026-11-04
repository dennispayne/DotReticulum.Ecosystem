using System.Text.Json;
using V = Sideband.SidebandTelemetryValue;

namespace Sideband.Tests;

[Trait("Category", "Unit")]
public sealed class SidebandRuntimeTelemetryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed class Clock : TimeProvider
    {
        internal double Now = 1_700_000_000;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(Now * 1000);
    }

    // GenerateSidebandRuntimeTelemetryVectors.py executes actual RNSTransport/Sensor methods.
    // Primary d069122af14880e43607aed0b71add4c7806fbe0; exact sense.py SHA-256 is in the fixture.
    [Fact]
    public async Task CollectionCacheRatesSparseDenseAndDisabledTransportMatchExecutedPrimary()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "sideband-runtime-telemetry.json")));
        var clock = new Clock();
        JsonElement row = default;
        var calls = new List<string>();
        await using var sensor = new SidebandRuntimeTelemetry((path, token) =>
        {
            token.ThrowIfCancellationRequested();
            calls.Add(path);
            return ValueTask.FromResult(path switch
            {
                "interface_stats" => V.Decode(Convert.FromHexString(row.GetProperty("input_stats_hex").GetString()!)),
                "path_table" => V.Decode(Convert.FromHexString(row.GetProperty("input_paths_hex").GetString()!)),
                "link_count" => V.From(row.GetProperty("link_count").GetInt64()),
                _ => throw new InvalidOperationException(path)
            });
        }, Enumerable.Range(0, 16).Select(n => (byte)n).ToArray(), clock);
        foreach (var observation in fixture.RootElement.GetProperty("observations").EnumerateArray())
        {
            row = observation;
            clock.Now = row.GetProperty("now").GetDouble();
            calls.Clear();
            var data = await sensor.ReadAsync(true, TestContext.Current.CancellationToken,
                row.GetProperty("explicit").GetBoolean());
            Assert.Equal(Convert.FromHexString(row.GetProperty("data_hex").GetString()!), data.Encode());
            var packed = await sensor.ReadAsync(!row.GetProperty("sparse").GetBoolean(), TestContext.Current.CancellationToken);
            Assert.Equal(Convert.FromHexString(row.GetProperty("packed_hex").GetString()!), packed.Encode());
            Assert.Equal(row.GetProperty("calls").EnumerateArray().Select(call =>
                call.ValueKind == JsonValueKind.String ? call.GetString()! : call[0].GetString()!), calls);
        }
    }

    private static V Statistics(V? received = null) => V.Fields(
        ("rxb", received ?? V.From(100L)), ("txb", V.From(200L)),
        ("rxs", V.Nil), ("txs", V.Nil), ("interfaces", V.Array()));

    private static V Query(string path, V statistics) => path switch
    {
        "interface_stats" => statistics, "path_table" => V.Array(), "link_count" => V.From(0L),
        _ => throw new InvalidOperationException(path)
    };

    [Fact]
    public void NativeCodecRetainsDenseDataWhileExecutedPrimaryDefaultRepackingDropsTables()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "sideband-runtime-telemetry.json")));
        var receiver = fixture.RootElement.GetProperty("receiver");
        var input = Convert.FromHexString(receiver.GetProperty("input_sensor_hex").GetString()!);
        var native = SidebandTelemetryCodec.FromWireValue((byte)SidebandTelemetrySensorId.RnsTransport, V.Decode(input));
        Assert.Equal(input, native.WireValue.Encode());
        var primary = V.Decode(Convert.FromHexString(receiver.GetProperty("repacked_sensor_hex").GetString()!));
        Assert.True(receiver.GetProperty("default_sparse").GetBoolean());
        Assert.True(primary.Get("ifstats")!.IsNil);
        Assert.True(primary.Get("path_table")!.IsNil);
        Assert.Equal(["Synthetic TCP"], receiver.GetProperty("rendered_interface_names").EnumerateArray().Select(value => value.GetString()));
        foreach (var pair in native.Data.Members.Where(pair => pair.Key.Text is not ("ifstats" or "path_table")))
            Assert.Equal(pair.Value.Encode(), primary.Get(pair.Key.Text!)!.Encode());
    }

    [Fact]
    public async Task ConcurrentReadersShareOneObservationAndResetRestoresInitialRates()
    {
        var clock = new Clock();
        var queries = 0;
        await using var sensor = new SidebandRuntimeTelemetry(async (path, token) =>
        {
            Interlocked.Increment(ref queries);
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            return Query(path, Statistics());
        }, new byte[16], clock);
        var reports = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => sensor.ReadAsync(false, Token)));
        Assert.Equal(3, queries);
        Assert.All(reports, report => Assert.True(report.Get("speed_rx")!.IsNil));
        clock.Now += 61;
        reports = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => sensor.ReadAsync(false, Token)));
        Assert.Equal(6, queries);
        Assert.All(reports, report => Assert.Equal(0d, report.Get("speed_rx")!.Number));
        await sensor.ResetAsync(TestContext.Current.CancellationToken);
        Assert.True((await sensor.ReadAsync(false, Token)).Get("speed_rx")!.IsNil);
        Assert.Equal(9, queries);
    }

    [Theory]
    [InlineData("missing-counter")]
    [InlineData("bad-counter")]
    [InlineData("bad-path")]
    [InlineData("bad-link-count")]
    [InlineData("bad-interfaces")]
    public async Task MalformedQueriesFailExplicitlyAndDoNotPoisonHealthyRecovery(string mutation)
    {
        var bad = true;
        var clock = new Clock();
        await using var sensor = new SidebandRuntimeTelemetry((path, _) =>
        {
            var value = Query(path, Statistics());
            if (bad)
                value = (mutation, path) switch
                {
                    ("missing-counter", "interface_stats") => V.Fields(("interfaces", V.Array())),
                    ("bad-counter", "interface_stats") => Statistics(V.From("not a counter")),
                    ("bad-path", "path_table") => V.Array(V.Fields(("hops", V.From(1L)))),
                    ("bad-link-count", "link_count") => V.From(-1L),
                    ("bad-interfaces", "interface_stats") => V.Fields(("interfaces", V.Nil)),
                    _ => value
                };
            return ValueTask.FromResult(value);
        }, new byte[16], clock);
        await Assert.ThrowsAsync<InvalidDataException>(() => sensor.ReadAsync(false, Token));
        bad = false;
        Assert.Equal(100d, (await sensor.ReadAsync(false, Token)).Get("traffic_rxb")!.Number);
    }

    [Fact]
    public async Task UnknownCountersRemainUnavailableRatherThanBecomingZero()
    {
        var clock = new Clock();
        await using var sensor = new SidebandRuntimeTelemetry((path, _) =>
            ValueTask.FromResult(Query(path, Statistics(V.Nil))), new byte[16], clock);
        var first = await sensor.ReadAsync(false, Token);
        clock.Now += 61;
        var second = await sensor.ReadAsync(false, Token);
        Assert.True(first.Get("traffic_rxb")!.IsNil);
        Assert.True(second.Get("speed_rx")!.IsNil);
        Assert.Equal(0d, second.Get("speed_tx")!.Number);
        Assert.True(second.Get("memory_used")!.IsNil);
        Assert.False(second.Get("transport_enabled")!.Boolean);
    }

    [Fact]
    public async Task CancellationAndConcurrentDisposalCancelOwnedQueryAndCannotResurrectCollection()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sensor = new SidebandRuntimeTelemetry(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("The query must have been cancelled.");
        }, new byte[16]);
        var reading = sensor.ReadAsync(false, Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Task.WhenAll(sensor.DisposeAsync().AsTask(), sensor.DisposeAsync().AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => sensor.ReadAsync(false, Token));
        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await stopped.CancelAsync();
        await using var fresh = new SidebandRuntimeTelemetry((path, token) =>
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Query(path, Statistics()));
        }, new byte[16]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fresh.ReadAsync(false, stopped.Token));
        Assert.NotNull(await fresh.ReadAsync(false, Token));
    }
}
