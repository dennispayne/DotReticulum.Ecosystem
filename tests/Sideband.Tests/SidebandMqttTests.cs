using System.Collections.Concurrent;
using Sideband;
using V = Sideband.SidebandTelemetryValue;
using S = Sideband.SidebandTelemetrySensorId;

namespace Sideband.Tests;

[Trait("Category", "Unit")]
public sealed class SidebandMqttTests
{
    private static readonly SidebandMqttConfiguration Enabled = new() { Enabled = true, Host = "example.invalid" };
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static SidebandTelemetryReport Report(long timestamp = 1700000000) => SidebandTelemetryCodec.Create(timestamp);

    [Fact]
    public void DefaultsBoundsAndCredentialDiagnosticsAreExplicit()
    {
        Assert.False(new SidebandMqttConfiguration().Enabled);
        Assert.Equal(1883, new SidebandMqttConfiguration().EffectivePort);
        Assert.Equal(Enabled, Enabled.Validate());
        foreach (var invalid in new[]
        {
            Enabled with { Host = null }, Enabled with { Host = "mqtt://example.invalid" },
            Enabled with { Host = "host name" }, Enabled with { Host = new('h', 254) },
            Enabled with { Port = 0 }, Enabled with { Port = 65536 },
            Enabled with { Username = new('u', 257) }, Enabled with { Username = "user\nname" },
            Enabled with { Password = new('p', 1025) }, Enabled with { Password = "\ud800" },
            Enabled with { Username = "\udc00" }, Enabled with { Host = "host\ud800" }
        }) Assert.Throws<InvalidDataException>(() => invalid.Validate());
        Assert.Equal(1, (Enabled with { Port = 1 }).Validate().EffectivePort);
        Assert.Equal(65535, (Enabled with { Port = 65535 }).Validate().EffectivePort);
        Assert.Equal("valid \ud83c\udf0d", (Enabled with { Password = "valid \ud83c\udf0d" }).Validate().Password);
        var credentials = Enabled with { Username = "synthetic-private-user", Password = "synthetic-private-password" };
        Assert.DoesNotContain(credentials.Username!, credentials.ToString());
        Assert.DoesNotContain(credentials.Password!, credentials.ToString());
    }

    [Fact]
    public void InvalidPayloadsTopicsAndMissingGeoidRemainVisibleWithBoundedRedactedDiagnostics()
    {
        var entries = Enumerable.Range(0, 100).Select(index => KeyValuePair.Create(V.From("synthetic-private-" + index),
            V.Array(V.Fields(("synthetic-private-value", V.From(true))), V.Nil))).ToArray();
        var custom = SidebandTelemetryCodec.FromData(S.Custom, V.Map(entries));
        var projection = SidebandMqtt.Project(SidebandMqttVectorTests.Context, SidebandTelemetryCodec.Create(1700000000L, custom));
        Assert.Equal(65, projection.Errors.Count);
        Assert.Contains("suppressed", projection.Errors[^1]);
        Assert.All(projection.Errors, error => Assert.DoesNotContain("synthetic-private", error));
        Assert.Contains(projection.Messages, message => message.Topic.EndsWith("/time/utc", StringComparison.Ordinal));
        Assert.DoesNotContain(projection.Messages, message => message.Topic.EndsWith("/value", StringComparison.Ordinal));
        var badTopic = SidebandTelemetryCodec.FromData(S.Custom, V.Map(
            KeyValuePair.Create(V.From("wild#card"), V.Array(V.From(1L), V.Nil)),
            KeyValuePair.Create(V.From("healthy"), V.Array(V.From(2L), V.Nil))));
        projection = SidebandMqtt.Project(SidebandMqttVectorTests.Context, SidebandTelemetryCodec.Create(1700000000L, badTopic));
        Assert.NotEmpty(projection.Errors);
        Assert.DoesNotContain(projection.Messages, message => message.Topic.Contains('#'));
        Assert.Contains(projection.Messages, message => message.Topic.EndsWith("/healthy/value", StringComparison.Ordinal));
        var location = SidebandTelemetryCodec.FromLocation(new(51.5, 0, 100, 0, 0, 1, 1700000000));
        projection = SidebandMqtt.Project(SidebandMqttVectorTests.Context, SidebandTelemetryCodec.Create(1700000000L, location));
        Assert.Single(projection.Errors);
        Assert.DoesNotContain(projection.Messages, message => message.Topic.Contains("/location/", StringComparison.Ordinal));
        Assert.Equal(3, projection.Messages.Count);
    }

    [Fact]
    public async Task BrokerFailureUsesOwnedOneSecondRetryAndFailingObserverCannotDisablePublishing()
    {
        var clock = new StepClock();
        var succeeded = Signal();
        var attempts = 0;
        var errors = new ConcurrentQueue<string>();
        await using var bridge = new SidebandMqttBridge(message =>
        {
            errors.Enqueue(message);
            throw new InvalidOperationException("synthetic observer failure");
        }, (_, messages, _) =>
        {
            Assert.Equal(3, messages.Count);
            if (Interlocked.Increment(ref attempts) == 1) throw new IOException("synthetic-private-network-detail");
            succeeded.TrySetResult();
            return Task.CompletedTask;
        }, timeProvider: clock);
        bridge.Start(Enabled, Token);
        Assert.Equal(SidebandMqttAdmission.Queued, bridge.Enqueue(SidebandMqttVectorTests.Context, Report()));
        await clock.Created.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(1, attempts);
        Assert.Equal(TimeSpan.FromSeconds(1), clock.Due);
        clock.Advance();
        await succeeded.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(2, attempts);
        Assert.Single(errors);
        Assert.DoesNotContain("synthetic-private", errors.Single());
    }

    [Fact]
    public async Task ExactQueueCapacityConfigurationCancellationAndConcurrentDisposalAreOwned()
    {
        var started = Signal();
        var cancelled = Signal();
        var released = Signal();
        var bridge = new SidebandMqttBridge(_ => { }, async (_, _, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cancelled.TrySetResult(); await released.Task; }
        });
        Assert.Equal(SidebandMqttAdmission.Stopped, bridge.Enqueue(SidebandMqttVectorTests.Context, Report()));
        bridge.Start(Enabled, Token);
        Assert.Throws<InvalidOperationException>(() => bridge.Start(Enabled, Token));
        Assert.Equal(SidebandMqttAdmission.Queued, bridge.Enqueue(SidebandMqttVectorTests.Context, Report()));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var admissions = new SidebandMqttAdmission[64];
        Parallel.For(0, admissions.Length, index =>
            admissions[index] = bridge.Enqueue(SidebandMqttVectorTests.Context, Report(1700000001 + index)));
        Assert.Equal(SidebandMqttBridge.QueueCapacity, admissions.Count(admission => admission == SidebandMqttAdmission.Queued));
        Assert.Equal(56, admissions.Count(admission => admission == SidebandMqttAdmission.Full));
        bridge.Configure(Enabled with { Enabled = false });
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(SidebandMqttAdmission.Disabled, bridge.Enqueue(SidebandMqttVectorTests.Context, Report()));
        var first = bridge.DisposeAsync().AsTask();
        var second = bridge.DisposeAsync().AsTask();
        Assert.Same(first, second);
        Assert.False(first.IsCompleted);
        released.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(SidebandMqttAdmission.Stopped, bridge.Enqueue(SidebandMqttVectorTests.Context, Report()));
        Assert.Throws<ObjectDisposedException>(() => bridge.Start(Enabled, Token));
    }

    [Fact]
    public async Task MalformedReportAndThrowingCancellationObserverPreserveSubsequentHealthyGeneration()
    {
        var errors = new ConcurrentQueue<string>();
        var first = Signal();
        var next = Signal();
        var attempts = 0;
        await using var bridge = new SidebandMqttBridge(errors.Enqueue, async (configuration, _, token) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                using var callback = token.Register(() => throw new InvalidOperationException("synthetic-private-callback"));
                first.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            else
            {
                Assert.Equal("second.invalid", configuration.Host);
                next.SetResult();
            }
        });
        bridge.Start(Enabled, Token);
        var malformed = SidebandTelemetryCodec.FromSensors(SidebandTelemetryCodec.FromWireValue((byte)S.Time, V.Nil));
        Assert.Equal(SidebandMqttAdmission.Queued, bridge.Enqueue(SidebandMqttVectorTests.Context, malformed));
        Assert.Equal(SidebandMqttAdmission.Queued, bridge.Enqueue(SidebandMqttVectorTests.Context, Report()));
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        bridge.Configure(Enabled with { Host = "second.invalid" });
        Assert.Equal(SidebandMqttAdmission.Queued, bridge.Enqueue(SidebandMqttVectorTests.Context, Report(1700000001)));
        await next.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Contains(errors, error => error.Contains("report rejected", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("cancellation callback failed", StringComparison.Ordinal));
        Assert.All(errors, error => Assert.DoesNotContain("synthetic-private", error));
    }

    [Fact]
    public async Task UnexpectedWorkerFaultIsReportedSurfacedByDisposalAndRefusesFurtherAdmission()
    {
        var faulted = Signal();
        var bridge = new SidebandMqttBridge(message =>
        {
            if (message.Contains("unexpected failure", StringComparison.Ordinal)) faulted.TrySetResult();
        }, (_, _, _) => throw new InvalidOperationException("synthetic worker fault"));
        bridge.Start(Enabled, Token);
        Assert.Equal(SidebandMqttAdmission.Queued, bridge.Enqueue(SidebandMqttVectorTests.Context, Report()));
        await faulted.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.DisposeAsync().AsTask());
        Assert.Equal(SidebandMqttAdmission.Stopped, bridge.Enqueue(SidebandMqttVectorTests.Context, Report()));
    }

    private sealed class StepClock : TimeProvider
    {
        private StepTimer? _timer;
        internal TaskCompletionSource Created { get; } = Signal();
        internal TimeSpan Due { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            Due = dueTime;
            _timer = new(callback, state);
            Created.TrySetResult();
            return _timer;
        }
        internal void Advance() => _timer!.Fire();

        private sealed class StepTimer(TimerCallback callback, object? state) : ITimer
        {
            private int _disposed;
            internal void Fire() { if (Volatile.Read(ref _disposed) == 0) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
