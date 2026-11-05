using System.Collections.Concurrent;
using System.Text.Json;
using DotReticulum.Core;
using LXMF;
using V = Sideband.SidebandTelemetryValue;

namespace Sideband.Tests;

// GeneratePropagationTelemetryVectors.py executes primary sense.py at
// d069122af14880e43607aed0b71add4c7806fbe0 and LXMF e8ba02483217af43be284a72f1c059c730b7d047.
// Exact file SHA-256s/import paths and disclosed clock/query/worker seams are in the fixture.
[Trait("Category", "Integration")]
public sealed class SidebandPropagationTelemetryVectorTests
{
    [Fact]
    [Trait("TestId", "T900")]
    public async Task ExecutedPrimaryStatusCadenceFailuresAndWireAreExact()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "sideband-propagation-telemetry.json")));
        var fixture = document.RootElement;
        Assert.Equal("d069122af14880e43607aed0b71add4c7806fbe0", fixture.GetProperty("revision").GetString());
        Assert.Equal("e8ba02483217af43be284a72f1c059c730b7d047", fixture.GetProperty("lxmf_revision").GetString());
        Assert.Equal("a29bfb51c468945c96a1ee22cae0114e993e2391", fixture.GetProperty("rns_revision").GetString());
        Assert.Equal("80418b1aa4de638f2c65f1bd64e5ea0466378a6f0bc682781672ef89b93a7dab",
            fixture.GetProperty("source_sha256").GetProperty("sbapp/sideband/sense.py").GetString());
        Assert.Equal(SidebandPropagationTelemetry.StaleInterval.TotalSeconds, fixture.GetProperty("stale_seconds").GetDouble());
        Assert.Equal(SidebandPropagationTelemetry.WakeInterval.TotalSeconds, fixture.GetProperty("wake_seconds").GetDouble());
        Assert.Equal(LxmPropagationStatusClient.DefaultTimeout.TotalSeconds, fixture.GetProperty("query_timeout_seconds").GetDouble());
        using var identity = Identity.Generate();
        var clock = new SidebandPropagationTelemetryTests.Clock();
        var status = V.Decode(Convert.FromHexString(fixture.GetProperty("status_hex").GetString()!));
        var responses = new Queue<V>([status, V.From(0xf1L), V.Nil, V.From(0xf0L), V.From(0xfeL), status]);
        var source = new SidebandPropagationTelemetryTests.Source((actual, _) =>
        {
            Assert.Same(identity, actual);
            return Task.FromResult(responses.Dequeue());
        });
        await using var provider = new SidebandPropagationTelemetry(identity, source, clock);
        foreach (var row in fixture.GetProperty("cases").EnumerateArray())
        {
            if (row.GetProperty("name").GetString() is "no-identity" or "synthesized") continue;
            clock.Now = row.GetProperty("time").GetDouble();
            var actual = await provider.CollectAsync(TestContext.Current.CancellationToken);
            Assert.Equal(row.GetProperty("queries").GetInt32(), source.Calls);
            Assert.Equal(row.GetProperty("last_attempt").GetDouble(), actual.LastAttemptUtcSeconds);
            Assert.Equal(Convert.FromHexString(row.GetProperty("wire_hex").GetString()!),
                SidebandTelemetryCodec.Encode(SidebandTelemetryCodec.FromSensors(actual.Reading)));
        }
        Assert.Equal(SidebandPropagationTelemetryFailure.None, provider.Snapshot.Failure);
        Assert.All(fixture.GetProperty("queries").EnumerateArray(), query => Assert.True(query.GetProperty("explicit_same_identity").GetBoolean()));
        Assert.Equal(2, fixture.GetProperty("plugin_calls").GetArrayLength());
        Assert.Equal("~/.lxmd/identity", fixture.GetProperty("plugin_calls")[1][1].GetString());
        await provider.DisposeAsync();
        Assert.Equal(Convert.FromHexString(fixture.GetProperty("teardown").GetProperty("wire_hex").GetString()!),
            SidebandTelemetryCodec.Encode(SidebandTelemetryCodec.FromSensors(provider.Snapshot.Reading)));
    }
}

[Trait("Category", "Unit")]
public sealed class SidebandPropagationTelemetryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    internal sealed class Clock : TimeProvider
    {
        internal double Now = 1000;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(Now * 1000);
    }

    internal sealed class Source(Func<Identity, CancellationToken, Task<V>> query) : ISidebandPropagationTelemetrySource
    {
        internal int Calls;
        public Task<V> QueryAsync(Identity identity, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return query(identity, cancellationToken);
        }
    }

    // Synthetic input uses the factual compile_stats fields; exact upstream maps are independently tested above.
    private static V Status() => V.Fields(
        ("destination_hash", V.From(new byte[16])), ("identity_hash", V.From(new byte[16])),
        ("uptime", V.From(1L)), ("delivery_limit", V.From(1000L)), ("propagation_limit", V.From(256L)),
        ("autopeer_maxdepth", V.From(4L)), ("from_static_only", V.From(false)),
        ("messagestore", V.Fields(("count", V.From(0L)), ("bytes", V.From(0L)), ("limit", V.From(500_000_000L)))),
        ("clients", V.Fields(("client_propagation_messages_received", V.From(0L)), ("client_propagation_messages_served", V.From(0L)))),
        ("unpeered_propagation_incoming", V.From(0L)), ("unpeered_propagation_rx_bytes", V.From(0L)),
        ("static_peers", V.From(0L)), ("total_peers", V.From(0L)), ("max_peers", V.From(20L)), ("peers", V.Map()));

    [Fact]
    [Trait("TestId", "T901")]
    public async Task NoImplicitStartSingleFlightAndCallerOwnership()
    {
        using var identity = Identity.Generate();
        var release = new TaskCompletionSource<V>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source((actual, _) =>
        {
            Assert.Same(identity, actual);
            entered.TrySetResult();
            return release.Task;
        });
        var provider = new SidebandPropagationTelemetry(identity, source);
        Assert.Equal(0, source.Calls);
        Assert.Equal(SidebandTelemetryStatus.Unavailable, provider.Snapshot.Reading.Status);
        var first = provider.CollectAsync(Token);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var pending = Enumerable.Range(0, 64).Select(_ => provider.CollectAsync(Token)).ToArray();
        release.SetResult(Status());
        await first;
        Assert.All(await Task.WhenAll(pending), snapshot => Assert.Equal(SidebandTelemetryStatus.Available, snapshot.Reading.Status));
        Assert.Equal(1, source.Calls);
        await provider.DisposeAsync();
        await provider.DisposeAsync();
        Assert.True(identity.HasPrivateKey);
        Assert.Equal(64, identity.Sign([1, 2, 3]).Length);
        Assert.Throws<ObjectDisposedException>(() => provider.Start(Token));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => provider.CollectAsync(Token));
    }

    [Theory]
    [InlineData(0xf0L, SidebandPropagationTelemetryFailure.NoIdentity)]
    [InlineData(0xf1L, SidebandPropagationTelemetryFailure.AccessDenied)]
    [InlineData(0xfeL, SidebandPropagationTelemetryFailure.Timeout)]
    [InlineData(99L, SidebandPropagationTelemetryFailure.InvalidResponse)]
    [Trait("TestId", "T902")]
    public async Task FailuresAreThrottledReportedIsolatedAndRecover(long code, SidebandPropagationTelemetryFailure expected)
    {
        using var identity = Identity.Generate();
        var clock = new Clock();
        var response = V.From(code);
        var source = new Source((_, _) => Task.FromResult(response));
        await using var provider = new SidebandPropagationTelemetry(identity, source, clock);
        var errors = new ConcurrentQueue<string>();
        provider.ErrorReported += _ => throw new IOException("observer private details must not escape");
        provider.ErrorReported += errors.Enqueue;
        Assert.Equal(expected, (await provider.CollectAsync(Token)).Failure);
        Assert.Equal(1, source.Calls);
        Assert.Single(errors);
        clock.Now += 299;
        response = Status();
        Assert.Equal(expected, (await provider.CollectAsync(Token)).Failure);
        Assert.Equal(1, source.Calls);
        clock.Now += 1;
        var success = await provider.CollectAsync(Token);
        Assert.Equal(SidebandPropagationTelemetryFailure.None, success.Failure);
        Assert.Equal(SidebandTelemetryStatus.Available, success.Reading.Status);
        clock.Now += 300;
        response = V.From(code);
        var failed = await provider.CollectAsync(Token);
        Assert.Same(success.Reading, failed.Reading);
        Assert.Equal(success.LastSuccessUtcSeconds, failed.LastSuccessUtcSeconds);
        Assert.Equal(expected, failed.Failure);
    }

    [Theory]
    [InlineData("nil", SidebandPropagationTelemetryFailure.EmptyResponse)]
    [InlineData("partial", SidebandPropagationTelemetryFailure.InvalidResponse)]
    [InlineData("malformed", SidebandPropagationTelemetryFailure.InvalidResponse)]
    [InlineData("oversized", SidebandPropagationTelemetryFailure.SourceFailed)]
    [InlineData("exception", SidebandPropagationTelemetryFailure.SourceFailed)]
    [InlineData("timeout", SidebandPropagationTelemetryFailure.Timeout)]
    [Trait("TestId", "T903")]
    public async Task BadSourcesCannotPoisonLastSuccessOrLeakPrivateFailureMessages(string mode, SidebandPropagationTelemetryFailure expected)
    {
        using var identity = Identity.Generate();
        var clock = new Clock();
        var bad = false;
        var source = new Source((_, _) => Task.FromResult(!bad ? Status() : mode switch
        {
            "nil" => V.Nil,
            "partial" => V.Map(),
            "malformed" => V.Fields(("uptime", V.From("not a number"))),
            "oversized" => V.From(new byte[SidebandTelemetryCodec.MaxValueBytes + 1]),
            "timeout" => throw new OperationCanceledException(),
            _ => throw new IOException("PRIVATE IDENTITY FILE PATH"),
        }));
        await using var provider = new SidebandPropagationTelemetry(identity, source, clock);
        var success = await provider.CollectAsync(Token);
        var diagnostics = new List<string>();
        provider.ErrorReported += diagnostics.Add;
        bad = true;
        clock.Now += 300;
        var failed = await provider.CollectAsync(Token);
        Assert.Same(success.Reading, failed.Reading);
        Assert.Equal(expected, failed.Failure);
        Assert.DoesNotContain("PRIVATE", Assert.Single(diagnostics), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("TestId", "T904")]
    public async Task DisposeCancelsAndJoinsTheExactPendingQueryWithoutDisposingIdentity()
    {
        using var identity = Identity.Generate();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leaving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source(async (_, token) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { leaving.SetResult(); await release.Task; }
            return Status();
        });
        var provider = new SidebandPropagationTelemetry(identity, source);
        provider.Start(Token);
        provider.Start(Token);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var dispose = provider.DisposeAsync().AsTask();
        await leaving.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(dispose.IsCompleted);
        release.SetResult();
        await dispose.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, source.Calls);
        Assert.Null(provider.Snapshot.LastAttemptUtcSeconds);
        Assert.Equal(SidebandTelemetryStatus.Unavailable, provider.Snapshot.Reading.Status);
        Assert.True(identity.HasPrivateKey);
    }

    [Fact]
    [Trait("TestId", "T905")]
    public async Task CancellationIsNotAnAuthorizationOrSourceFailureAndCanceledJoinerDoesNotCancelOwner()
    {
        using var identity = Identity.Generate();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<V>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source((_, _) => { entered.TrySetResult(); return release.Task; });
        await using var provider = new SidebandPropagationTelemetry(identity, source);
        Assert.ThrowsAny<OperationCanceledException>(() => provider.Start(canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CollectAsync(canceled.Token));
        Assert.Equal(0, source.Calls);
        var owner = provider.CollectAsync(Token);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        using var joiner = new CancellationTokenSource();
        var joined = provider.CollectAsync(joiner.Token);
        joiner.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => joined);
        release.SetResult(Status());
        Assert.Equal(SidebandTelemetryStatus.Available, (await owner).Reading.Status);
    }

    [Fact]
    [Trait("TestId", "T906")]
    public void IdentityAndSourceAreAlwaysExplicit()
    {
        using var identity = Identity.Generate();
        using var publicOnly = Identity.FromPublicKey(identity.ExportPublicKey());
        var source = new Source((_, _) => Task.FromResult(V.Nil));
        Assert.Throws<ArgumentNullException>(() => new SidebandPropagationTelemetry(null!, source));
        Assert.Throws<ArgumentNullException>(() => new SidebandPropagationTelemetry(identity, null!));
        Assert.Throws<ArgumentException>(() => new SidebandPropagationTelemetry(publicOnly, source));
        Assert.Throws<ArgumentNullException>(() => new SidebandPropagationTelemetrySource(null!));
        Assert.Throws<ArgumentNullException>(() => new LxmPropagationStatusClient(null!));
    }

    [Fact]
    [Trait("TestId", "T907")]
    public async Task AutomaticWorkerWakesEveryEighteenSecondsAndQueriesOnlyWhenThreeHundredSecondsOld()
    {
        using var identity = Identity.Generate();
        var clock = new WakeClock();
        var source = new Source((_, _) => Task.FromResult(Status()));
        await using var provider = new SidebandPropagationTelemetry(identity, source, clock);
        provider.Start(Token);
        await clock.WaitForTimerAsync(1, Token);
        Assert.Equal(1, source.Calls);
        for (var index = 1; index <= 16; index++)
        {
            clock.Advance();
            await clock.WaitForTimerAsync(index + 1, Token);
            Assert.Equal(1, source.Calls);
        }
        clock.Advance();
        await clock.WaitForTimerAsync(18, Token);
        Assert.Equal(2, source.Calls);
        Assert.Equal(1306, provider.Snapshot.LastAttemptUtcSeconds);
        Assert.Equal(SidebandTelemetryStatus.Available, provider.Snapshot.Reading.Status);
    }

    private sealed class WakeClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<Timer> _timers = [];
        private readonly Dictionary<int, TaskCompletionSource> _waiters = [];
        private int _created;
        private double _now = 1000;
        public override DateTimeOffset GetUtcNow() { lock (_gate) return DateTimeOffset.UnixEpoch.AddSeconds(_now); }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(SidebandPropagationTelemetry.WakeInterval, dueTime);
            lock (_gate)
            {
                var timer = new Timer(callback, state);
                _timers.Add(timer);
                _created++;
                if (_waiters.Remove(_created, out var waiter)) waiter.SetResult();
                return timer;
            }
        }
        internal Task WaitForTimerAsync(int count, CancellationToken token)
        {
            lock (_gate)
            {
                if (_created >= count) return Task.CompletedTask;
                var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add(count, waiter);
                return waiter.Task.WaitAsync(token);
            }
        }
        internal void Advance()
        {
            Timer[] timers;
            lock (_gate) { _now += 18; timers = _timers.ToArray(); _timers.Clear(); }
            foreach (var timer in timers) timer.Fire();
        }
        private sealed class Timer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            internal void Fire() { if (!_disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
