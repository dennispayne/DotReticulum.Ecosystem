using System.Text.Json;
using System.Threading.Channels;

namespace NomadNet.Tests;

// GenerateNomadNetClientAnnounceVectors.py executes primary f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2.
// The fixture records the actual source hash, disclosed clock and observed failures.
[Trait("Category", "Unit")]
public sealed class NomadNetClientAnnounceTests
{
    [Fact]
    public void StrictPeriodicBoundaryMatchesActualPrimaryJobsWithExplicitNullSafetyDifference()
    {
        using var fixture = JsonDocument.Parse(typeof(NomadNetClientAnnounceTests).Assembly
            .GetManifestResourceStream("NomadNet.Tests.NomadNetClientAnnounceVectors.json")!);
        Assert.Equal("f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2", fixture.RootElement.GetProperty("commit").GetString());
        Assert.Equal(3, fixture.RootElement.GetProperty("startupDelaySeconds").GetInt32());
        foreach (var vector in fixture.RootElement.GetProperty("observed").EnumerateArray())
        {
            var last = vector.GetProperty("lastBefore");
            var settings = new NomadNetSettings
            {
                ClientAnnounceIntervalMinutes = 30,
                LastClientAnnounce = last.ValueKind == JsonValueKind.Null ? null :
                    DateTimeOffset.FromUnixTimeSeconds(checked((long)last.GetDouble())),
            };
            var announced = vector.GetProperty("calls").EnumerateArray()
                .Any(call => call[0].GetString() == "announce");
            Assert.Equal(announced, NomadNetClientAnnounces.PeriodicDue(settings, DateTimeOffset.FromUnixTimeSeconds(100000)));
            if (last.ValueKind == JsonValueKind.Null)
                Assert.Equal("TypeError", vector.GetProperty("error").GetString());
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => NomadNetClientAnnounces.PeriodicDue(
            new NomadNetSettings { ClientAnnounceIntervalMinutes = 29 }, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ManualSuccessPersistsAfterNetworkFailureAndWorksWithoutObserversOrEnabledMetadata()
    {
        var settings = new NomadNetSettings { EnableClient = false, LastClientAnnounce = DateTimeOffset.UnixEpoch };
        var clock = new FixedClock();
        var fail = true;
        var saves = 0;
        await using var service = new NomadNetClientAnnounces(() => settings, _ =>
        {
            Assert.Equal(saves == 0 ? DateTimeOffset.UnixEpoch : clock.GetUtcNow(), settings.LastClientAnnounce);
            return fail ? ValueTask.FromException(new IOException("generated failure")) : ValueTask.CompletedTask;
        }, (timestamp, _) =>
        {
            saves++;
            settings = settings with { LastClientAnnounce = timestamp };
            return Task.CompletedTask;
        }, clock);
        await Assert.ThrowsAsync<IOException>(() => service.AnnounceNowAsync().AsTask());
        Assert.Equal(0, saves);
        Assert.Equal(DateTimeOffset.UnixEpoch, settings.LastClientAnnounce);
        fail = false;
        await service.AnnounceNowAsync();
        Assert.Equal(1, saves);
        Assert.Equal(clock.GetUtcNow(), settings.LastClientAnnounce);
        Assert.False(NomadNetClientAnnounces.PeriodicDue(settings, clock.GetUtcNow()));
    }

    [Fact]
    public async Task RequestsAreBoundedCancellationJoinsActiveWorkAndConcurrentDisposalIsShared()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saves = 0;
        var service = new NomadNetClientAnnounces(() => new(), async token =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { await finish.Task; }
        }, (_, _) => { saves++; return Task.CompletedTask; });
        var active = service.AnnounceNowAsync().AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waiting = Enumerable.Range(1, NomadNetClientAnnounces.MaximumPending - 1)
            .Select(_ => service.AnnounceNowAsync().AsTask()).ToArray();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AnnounceNowAsync().AsTask());
        var first = service.DisposeAsync().AsTask();
        var second = service.DisposeAsync().AsTask();
        Assert.Same(first, second);
        Assert.False(first.IsCompleted);
        finish.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        foreach (var request in waiting) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, saves);
        Assert.Throws<ObjectDisposedException>(service.Start);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.AnnounceNowAsync().AsTask());
    }

    [Fact]
    public async Task StartIsExplicitSingleUseAndDisposalCancelsStartupDelay()
    {
        var calls = 0;
        var service = new NomadNetClientAnnounces(() => new(), _ =>
        {
            calls++;
            return ValueTask.CompletedTask;
        }, (_, _) => Task.CompletedTask, new SteppedClock());
        service.Start();
        Assert.Throws<InvalidOperationException>(service.Start);
        await service.DisposeAsync();
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RealServiceUsesThreeAndNinetySecondDeadlinesAndSurvivesFailedObserverAndNetwork()
    {
        var clock = new SteppedClock();
        var settings = new NomadNetSettings { LastClientAnnounce = DateTimeOffset.UnixEpoch };
        var calls = 0;
        var logged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new NomadNetClientAnnounces(() => settings, _ =>
        {
            calls++;
            return calls == 1 ? ValueTask.FromException(new IOException("generated startup failure")) : ValueTask.CompletedTask;
        }, (timestamp, _) =>
        {
            settings = settings with { LastClientAnnounce = timestamp };
            saved.TrySetResult();
            return Task.CompletedTask;
        }, clock);
        service.Log += _ => throw new InvalidOperationException("generated observer failure");
        service.Log += _ => logged.TrySetResult();
        service.Start();
        await clock.AdvanceNextAsync(TimeSpan.FromSeconds(3));
        await logged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DateTimeOffset.UnixEpoch, settings.LastClientAnnounce);
        await clock.AdvanceNextAsync(TimeSpan.FromSeconds(87));
        await saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(100090), settings.LastClientAnnounce);
        await clock.AdvanceNextAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(5), (await clock.NextAsync()).Delay);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task DisabledStartupWithNoTimestampRemainsQuietUntilManualSuccess()
    {
        var clock = new SteppedClock();
        var settings = new NomadNetSettings { ClientAnnounceAtStart = false };
        var calls = 0;
        await using var service = new NomadNetClientAnnounces(() => settings,
            _ => { calls++; return ValueTask.CompletedTask; },
            (timestamp, _) => { settings = settings with { LastClientAnnounce = timestamp }; return Task.CompletedTask; }, clock);
        service.Start();
        await clock.AdvanceNextAsync(TimeSpan.FromSeconds(3));
        await clock.AdvanceNextAsync(TimeSpan.FromSeconds(87));
        await clock.AdvanceNextAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(5), (await clock.NextAsync()).Delay);
        Assert.Equal(0, calls);
        await service.AnnounceNowAsync();
        Assert.Equal(1, calls);
        Assert.Equal(clock.GetUtcNow(), settings.LastClientAnnounce);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(100000);
    }

    private sealed class SteppedClock : TimeProvider
    {
        private readonly Channel<(TimeSpan Delay, StepTimer Timer)> _timers =
            Channel.CreateBounded<(TimeSpan, StepTimer)>(16);
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(100000).AddTicks(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            var timer = new StepTimer(callback, state);
            Assert.True(_timers.Writer.TryWrite((dueTime, timer)));
            return timer;
        }
        public async Task<(TimeSpan Delay, StepTimer Timer)> NextAsync() =>
            await _timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public async Task AdvanceNextAsync(TimeSpan expected)
        {
            var next = await NextAsync();
            Assert.Equal(expected, next.Delay);
            Interlocked.Add(ref _ticks, expected.Ticks);
            next.Timer.Fire();
        }
        public sealed class StepTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Fire() { if (!_disposed) callback(state); }
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
