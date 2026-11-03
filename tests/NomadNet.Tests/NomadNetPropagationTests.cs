using System.Text.Json;
using DotReticulum.Core;
using DotReticulum.Applications;
using LXMF;
using NomadNet;

namespace NomadNet.Tests;

[Trait("Category", "Unit")]
public sealed class NomadNetPropagationTests
{
    // Executed primary f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2; generator and per-file hashes accompany fixture.
    [Fact]
    public void SelectionMatchesObservedTrustDistanceTieAndManualDecisions()
    {
        using var fixture = Fixture();
        foreach (var test in fixture.RootElement.GetProperty("selection").EnumerateArray())
        {
            var trusted = new List<string>();
            var candidates = new List<NomadNetPropagationCandidate>();
            foreach (var node in test.GetProperty("nodes").EnumerateArray())
            {
                var hash = Hash(node[0].GetInt32());
                if (node[1].GetInt32() == 255) trusted.Add(hash);
                candidates.Add(new(hash, hash, node[2].GetInt32()));
            }
            var manual = test.GetProperty("manual");
            var selected = NomadNetPropagation.SelectNode(manual.ValueKind == JsonValueKind.Null ? null : Hash(manual.GetInt32()),
                trusted, candidates);
            var expected = test.GetProperty("selected");
            // Primary retains a previous selection after no trusted candidate; native deliberately clears it on revoked trust.
            if (test.GetProperty("previous").ValueKind != JsonValueKind.Null) Assert.Null(selected);
            else Assert.Equal(expected.ValueKind == JsonValueKind.Null ? null : Hash(expected.GetInt32()), selected);
        }
        Assert.Equal(Hash(2), NomadNetPropagation.SelectNode(null, [Hash(1)],
            [new(Hash(1), Hash(2), 3)]));
        Assert.Null(NomadNetPropagation.SelectNode(null, [Hash(1)],
            [new(Hash(1), Hash(2), -1)]));
    }

    [Fact]
    public void FallbackMatchesPrimaryDecisionsAndNeverRepeatsPropagation()
    {
        using var fixture = Fixture();
        foreach (var test in fixture.RootElement.GetProperty("fallback").EnumerateArray())
            Assert.Equal(test.GetProperty("retry").GetBoolean(), NomadNetPropagation.ShouldFallback(
                test.GetProperty("failed").GetBoolean() ? LxmState.Failed : LxmState.Delivered,
                LxmMethod.Direct, test.GetProperty("enabled").GetBoolean(), true,
                test.GetProperty("stampFailure").GetBoolean()));
        Assert.False(NomadNetPropagation.ShouldFallback(LxmState.Failed, LxmMethod.Propagated, true, true));
        Assert.False(NomadNetPropagation.ShouldFallback(LxmState.Failed, LxmMethod.Direct, true, false));
        Assert.False(NomadNetPropagation.ShouldFallback(LxmState.Rejected, LxmMethod.Direct, true, true));
        Assert.False(NomadNetPropagation.ShouldFallback(LxmState.Cancelled, LxmMethod.Direct, true, true));
        Assert.False(NomadNetPropagation.ShouldFallback(LxmState.Failed, LxmMethod.Paper, true, true));
    }

    [Fact]
    public void FallbackPreservesWireMessageHashTimestampBinaryAndFields()
    {
        using var identity = Identity.Generate();
        var message = LxMessage.CreateBinary(Convert.FromHexString(Hash(2)), identity, new byte[] { 0, 0xff, 5 },
            new byte[] { 0xff, 0 }, new() { [LxmField.FileAttachments] = new object?[] { new object?[] { "a.bin", new byte[] { 1, 2 } } } },
            timestamp: 1700000000.125);
        message.Pack();
        var fallback = NomadNetDelivery.CreateFallback(message, identity);
        Assert.Equal(message.Hash, fallback.ComputeHash());
        Assert.Equal(message.Timestamp, fallback.Timestamp);
        Assert.Equal(message.ContentBytes, fallback.ContentBytes);
        Assert.Equal(message.TitleBytes, fallback.TitleBytes);
        Assert.Equal(LxmMethod.Propagated, fallback.DesiredMethod);
        Assert.NotSame(message.Fields, fallback.Fields);
    }

    [Fact]
    public async Task ScheduleHonoursStartupDelayStrictBoundaryAndDisabledPolicy()
    {
        var now = DateTimeOffset.UtcNow;
        await using var policy = new NomadNetPropagation("", new(), () => [], _ => { }, (_, _) => Task.FromResult(0), () => { });
        Assert.False(policy.IsDue(now.AddSeconds(89)));
        Assert.True(policy.IsDue(now.AddSeconds(91)));
        await using var disabled = new NomadNetPropagation("", new() { PeriodicLxmfSync = false }, () => [], _ => { },
            (_, _) => Task.FromResult(0), () => { });
        Assert.False(disabled.IsDue(now.AddDays(1)));
        await using var future = new NomadNetPropagation("", new() { LastLxmfSync = now }, () => [], _ => { },
            (_, _) => Task.FromResult(0), () => { });
        Assert.False(future.IsDue(now.AddMinutes(360)));
        Assert.True(future.IsDue(now.AddMinutes(360).AddTicks(1)));
    }

    private static JsonDocument Fixture() => JsonDocument.Parse(typeof(NomadNetPropagationTests).Assembly
        .GetManifestResourceStream("NomadNet.Tests.NomadNetPropagationVectors.json")!);
    private static string Hash(int value) => Convert.ToHexStringLower(Enumerable.Repeat((byte)value, 16).ToArray());
}

[Trait("Category", "Integration")]
public sealed class NomadNetPropagationLifecycleTests
{
    [Fact]
    public async Task FailedPeerDoesNotStopDeliveryAndDisposalCancelsQueuedWork()
    {
        var directory = Workspace();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "config"), "[reticulum]\n share_instance = No\n[interfaces]\n");
            await using var node = ReticulumNode.Open(directory);
            await node.StartAsync();
            using var identity = Identity.Generate();
            await using var router = new LxmRouter(node.Router, identity);
            var store = await NomadNetStore.OpenAsync(directory);
            var delivery = new NomadNetDelivery(router, identity, store, () => true);
            var failed = LxMessage.Create(new byte[16], identity, "failed");
            await delivery.QueueAsync(failed, new("one", new string('0', 32), "outbound", DateTimeOffset.UtcNow, "", "failed", "queued"));
            Assert.True(router.CancelOutbound(failed.Hash!, LxmState.Failed));
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while ((await store.MessagesAsync()).Single().Status != "failed" && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.Equal("failed", (await store.MessagesAsync()).Single().Status);
            var next = LxMessage.Create(new byte[16], identity, "next");
            await delivery.QueueAsync(next, new("two", new string('0', 32), "outbound", DateTimeOffset.UtcNow, "", "next", "queued"));
            await delivery.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("cancelled", (await store.MessagesAsync()).Single(item => item.Id == "two").Status);
            await delivery.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => delivery.QueueAsync(next,
                new("three", new string('0', 32), "outbound", DateTimeOffset.UtcNow, "", "", "queued")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task SyncPersistsAttemptBeforeIoSerializesCancelsAndRecoversAfterFailure()
    {
        var directory = Workspace();
        try
        {
            var attempts = 0;
            var observedLimit = -1;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var selected = new string('a', 32);
            await using var policy = new NomadNetPropagation(directory, new() { PropagationNode = selected }, () => [], _ => { },
                async (limit, token) =>
                {
                    observedLimit = limit;
                    Assert.NotNull((await NomadNetSettings.OpenAsync(directory, token)).LastLxmfSync);
                    if (Interlocked.Increment(ref attempts) == 1)
                    {
                        entered.TrySetResult();
                        await Task.Delay(Timeout.Infinite, token);
                    }
                    if (attempts == 2) throw new IOException("peer disconnected");
                    return 2;
                }, () => { });
            policy.Log = _ => throw new InvalidOperationException("failing observer");
            Assert.True(await policy.BeginSyncAsync(8));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(8, observedLimit);
            Assert.False(await policy.BeginSyncAsync());
            await policy.CancelSyncAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("Cancelled", policy.Status);
            Assert.True(await policy.BeginSyncAsync());
            await policy.WaitForSyncAsync();
            Assert.StartsWith("Failed:", policy.Status);
            Assert.True(await policy.BeginSyncAsync(1));
            await policy.WaitForSyncAsync();
            Assert.Equal("Complete: 2 messages", policy.Status);
            Assert.Equal(1, observedLimit);
            Assert.Equal(selected, policy.Selected);
            await policy.UpdateSettingsAsync(current => current with { UsePropagation = false });
            Assert.Null(policy.Selected);
            await Assert.ThrowsAsync<InvalidOperationException>(() => policy.BeginSyncAsync());
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => policy.BeginSyncAsync(-1));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task DisposalCancelsActiveSyncAndSettingsRemainMigrationSafe()
    {
        var directory = Workspace();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"), "{\"schemaVersion\":1,\"displayName\":\"Old profile\"}");
            var settings = await NomadNetSettings.OpenAsync(directory);
            Assert.True(settings.PeriodicLxmfSync);
            Assert.True(settings.TryPropagationOnSendFail);
            Assert.Equal(360, settings.LxmfSyncIntervalMinutes);
            Assert.Equal(8, settings.LxmfSyncLimit);
            Assert.Empty(settings.TrustedNodes);
            var cancelled = 0;
            var policy = new NomadNetPropagation(directory, settings with { PropagationNode = new string('a', 32) },
                () => [], _ => { }, async (_, token) => { await Task.Delay(Timeout.Infinite, token); return 0; },
                () => Interlocked.Increment(ref cancelled));
            await policy.BeginSyncAsync();
            await policy.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(policy.IsSyncing);
            Assert.Equal(1, cancelled);
            await policy.DisposeAsync();
            Assert.Throws<ObjectDisposedException>(policy.Start);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => policy.BeginSyncAsync());
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                (settings with { LxmfSyncIntervalMinutes = 0 }).SaveAsync(directory));
        }

        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task StartingAndDisposingTheSchedulerCanRaceWithoutLeakingWork()
    {
        var directory = Workspace();
        try
        {
            var policy = new NomadNetPropagation(directory, new(), () => [], _ => { }, (_, _) => Task.FromResult(0), () => { });
            var start = Task.Run(() =>
            {
                try { policy.Start(); }
                catch (ObjectDisposedException) { }
            });
            await Task.WhenAll(start, policy.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Throws<ObjectDisposedException>(policy.Start);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string Workspace()
    {
        var path = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomadnet-propagation-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(path);
        return path;
    }
}
