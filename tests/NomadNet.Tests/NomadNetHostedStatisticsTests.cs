using System.Text;
using System.Text.Json;
using DotReticulum.Applications;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace NomadNet.Tests;

[Trait("Category", "Integration")]
public sealed class NomadNetHostedStatisticsTests
{
    [Fact, Trait("TestId", "T961")]
    public async Task HandlerAttemptCountsAndResetMatchExecutedPrimary()
    {
        // GenerateHostedStatisticsVectors.py executes primary f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2.
        // The fixture records actual Node/App/Network source SHA-256 and disclosed synthetic boundaries.
        using var fixture = JsonDocument.Parse(typeof(NomadNetHostedStatisticsTests).Assembly
            .GetManifestResourceStream("NomadNet.Tests.NomadNetHostedStatisticsVectors.json")!);
        Assert.Equal("f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2", fixture.RootElement.GetProperty("revision").GetString());
        using var workspace = new Workspace();
        await using var node = ReticulumNode.Open(workspace.Root);
        using var identity = Identity.Generate();
        await using var host = new NomadNetHost(node.Router, identity, workspace.Root, new());
        foreach (var name in new[] { "ok.mu", "denied.mu", "ok.webp", "denied.webp" })
            await File.WriteAllTextAsync(Path.Combine(workspace.Root, "storage", "pages", name), "synthetic");
        foreach (var name in new[] { "ok.bin", "denied.bin" })
            await File.WriteAllTextAsync(Path.Combine(workspace.Root, "storage", "files", name), "synthetic");
        foreach (var relative in new[] { "pages/denied.mu", "pages/denied.webp", "files/denied.bin" })
            await File.WriteAllTextAsync(Path.Combine(workspace.Root, "storage", relative.Replace('/', Path.DirectorySeparatorChar)) + ".allowed", new string('0', 32));
        host.Refresh();
        foreach (var item in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            object? data = item.GetProperty("data").ValueKind == JsonValueKind.Object
                ? item.GetProperty("data").EnumerateObject().ToDictionary(property => (object)property.Name,
                    property => property.Value.ValueKind == JsonValueKind.Null ? null : (object?)property.Value.GetString()) : null;
            var before = host.Statistics.Snapshot;
            await host.RespondAsync(Context(item.GetProperty("path").GetString()!, data), default,
                item.GetProperty("handler").GetString() == "serve_default_index");
            var after = host.Statistics.Snapshot;
            var delta = item.GetProperty("delta");
            Assert.Equal(delta.GetProperty("served_page_requests").GetInt64(), after.ServedPageRequests - before.ServedPageRequests);
            Assert.Equal(delta.GetProperty("served_file_requests").GetInt64(), after.ServedFileRequests - before.ServedFileRequests);
            Assert.Equal(delta.GetProperty("served_media_requests").GetInt64(), after.ServedMediaRequests - before.ServedMediaRequests);
        }
        host.Statistics.Record(NomadNetHostedRequestKind.Connection);
        AssertSnapshot(fixture.RootElement.GetProperty("afterConnection"), host.Statistics.Snapshot);
        await host.Statistics.FlushAsync();
        Assert.False(host.Statistics.IsDirty);
        Assert.Equal(5, NomadNetHostedStatistics.JobInterval.TotalSeconds);
        Assert.Equal(60, NomadNetHostedStatistics.SettingsFlushInterval.TotalSeconds);
        await host.Statistics.ResetAsync();
        AssertSnapshot(fixture.RootElement.GetProperty("reset"), host.Statistics.Snapshot);
        await using var reopened = await NomadNetHostedStatistics.OpenAsync(workspace.Root);
        AssertSnapshot(fixture.RootElement.GetProperty("reloaded"), reopened.Snapshot);
    }

    [Fact, Trait("TestId", "T962")]
    public async Task RegisteredDefaultDoesNotBecomeAFileHandlerUntilRescanAndDeletedIndexIsCounted()
    {
        using var workspace = new Workspace();
        await using var node = ReticulumNode.Open(workspace.Root);
        using var identity = Identity.Generate();
        await using var host = new NomadNetHost(node.Router, identity, workspace.Root, new());
        var index = Path.Combine(workspace.Root, "storage", "pages", "index.mu");
        await File.WriteAllTextAsync(index, "published");
        Assert.Contains("Create storage", Encoding.UTF8.GetString(Assert.IsType<byte[]>(
            await host.RespondAsync(Context("/page/index.mu"), default, defaultIndex: true))));
        Assert.Equal(0, host.Statistics.Snapshot.ServedPageRequests);
        host.Refresh();
        Assert.Equal("published", Encoding.UTF8.GetString(Assert.IsType<byte[]>(
            await host.RespondAsync(Context("/page/index.mu"), default, defaultIndex: false))));
        File.Delete(index);
        Assert.Null(await host.RespondAsync(Context("/page/index.mu"), default, defaultIndex: false));
        Assert.Equal(2, host.Statistics.Snapshot.ServedPageRequests);
        host.Refresh();
        Assert.IsType<byte[]>(await host.RespondAsync(Context("/page/index.mu"), default, defaultIndex: true));
        Assert.Equal(2, host.Statistics.Snapshot.ServedPageRequests);
    }

    [Fact, Trait("TestId", "T963")]
    public async Task ConcurrentCountersAndFlushesSurviveCleanRestartWithoutSubscribers()
    {
        using var workspace = new Workspace();
        var stats = await NomadNetHostedStatistics.OpenAsync(workspace.Root);
        stats.Start();
        Assert.Throws<InvalidOperationException>(stats.Start);
        var writers = Enumerable.Range(0, 8).Select(async _ =>
        {
            for (var index = 0; index < 1000; index++)
            {
                stats.Record(NomadNetHostedRequestKind.Page);
                stats.Record(NomadNetHostedRequestKind.Media);
                if (index % 100 == 0) await stats.FlushAsync();
            }
        });
        await Task.WhenAll(writers);
        var disposals = Enumerable.Range(0, 8).Select(_ => stats.DisposeAsync().AsTask()).ToArray();
        await Task.WhenAll(disposals).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Throws<ObjectDisposedException>(() => stats.Record(NomadNetHostedRequestKind.Page));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => stats.FlushAsync());
        await using var reopened = await NomadNetHostedStatistics.OpenAsync(workspace.Root);
        Assert.Equal(new(ServedPageRequests: 8000, ServedMediaRequests: 8000), reopened.Snapshot);
        Assert.False(reopened.IsDirty);
    }

    [Fact, Trait("TestId", "T964")]
    public async Task PersistenceFailureReportsAllObserversAndRetainsDirtyCountersForRetry()
    {
        using var workspace = new Workspace();
        await using var stats = await NomadNetHostedStatistics.OpenAsync(workspace.Root);
        stats.Record(NomadNetHostedRequestKind.File);
        var path = Path.Combine(workspace.Root, "storage", "hosted-statistics.json");
        Directory.CreateDirectory(path);
        var messages = new List<string>();
        stats.Log = _ => throw new InvalidOperationException("synthetic failing observer");
        stats.Log += messages.Add;
        var error = await Assert.ThrowsAnyAsync<Exception>(() => stats.FlushAsync());
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Contains(messages, message => message.Contains("persistence failed", StringComparison.Ordinal));
        Assert.True(stats.IsDirty);
        Directory.Delete(path);
        stats.Record(NomadNetHostedRequestKind.File);
        await stats.FlushAsync();
        await using var reopened = await NomadNetHostedStatistics.OpenAsync(workspace.Root);
        Assert.Equal(2, reopened.Snapshot.ServedFileRequests);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.new"));
    }

    [Fact, Trait("TestId", "T965")]
    public async Task CanceledFlushAndResetDoNotLoseCountersAndFutureDueFlushWorks()
    {
        using var workspace = new Workspace();
        await using var stats = await NomadNetHostedStatistics.OpenAsync(workspace.Root);
        stats.Record(NomadNetHostedRequestKind.Page);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stats.FlushAsync(canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stats.ResetAsync(canceled.Token));
        Assert.Equal(1, stats.Snapshot.ServedPageRequests);
        Assert.True(stats.IsDirty);
        await stats.FlushIfDueAsync(DateTimeOffset.MinValue);
        Assert.True(stats.IsDirty);
        await stats.FlushIfDueAsync(DateTimeOffset.UtcNow + TimeSpan.FromSeconds(61));
        Assert.False(stats.IsDirty);
    }

    [Fact, Trait("TestId", "T966")]
    public async Task SaturatedCounterReportsOverflowDoesNotWrapAndOtherCountersContinue()
    {
        using var workspace = new Workspace();
        var path = Path.Combine(workspace.Root, "storage", "hosted-statistics.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, NomadNetHostedStatistics.Encode(new(ServedPageRequests: long.MaxValue)));
        await using var stats = await NomadNetHostedStatistics.OpenAsync(workspace.Root);
        var messages = new List<string>();
        stats.Log = messages.Add;
        stats.Record(NomadNetHostedRequestKind.Page);
        stats.Record(NomadNetHostedRequestKind.File);
        Assert.Equal(long.MaxValue, stats.Snapshot.ServedPageRequests);
        Assert.Equal(1, stats.Snapshot.ServedFileRequests);
        Assert.Contains(messages, message => message.Contains("overflow", StringComparison.Ordinal));
        Assert.Throws<ArgumentOutOfRangeException>(() => stats.Record((NomadNetHostedRequestKind)100));
        await stats.ResetAsync();
        Assert.Equal(0, stats.Snapshot.ServedPageRequests);
    }

    [Theory, Trait("TestId", "T967")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{\"schemaVersion\":2,\"nodeConnects\":0,\"servedPageRequests\":0,\"servedFileRequests\":0,\"servedMediaRequests\":0}")]
    [InlineData("{\"schemaVersion\":1,\"nodeConnects\":-1,\"servedPageRequests\":0,\"servedFileRequests\":0,\"servedMediaRequests\":0}")]
    [InlineData("{\"schemaVersion\":1,\"nodeConnects\":0,\"servedPageRequests\":9223372036854775808,\"servedFileRequests\":0,\"servedMediaRequests\":0}")]
    [InlineData("{\"schemaVersion\":1,\"nodeConnects\":0,\"servedPageRequests\":0,\"servedFileRequests\":0,\"servedMediaRequests\":null}")]
    [InlineData("{\"schemaVersion\":1,\"nodeConnects\":0,\"servedPageRequests\":0,\"servedFileRequests\":0,\"servedMediaRequests\":0,\"nodeConnects\":3}")]
    public async Task CorruptCountersFailClosedWithoutResetOrOverwrite(string data)
    {
        using var workspace = new Workspace();
        var path = Path.Combine(workspace.Root, "storage", "hosted-statistics.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, data);
        await Assert.ThrowsAsync<InvalidDataException>(() => NomadNetHostedStatistics.OpenAsync(workspace.Root));
        Assert.Equal(data, await File.ReadAllTextAsync(path));
    }

    [Fact, Trait("TestId", "T968")]
    public async Task OversizeCountersAreRejectedAndDisposedHostFlushesEvenWithoutStart()
    {
        using var workspace = new Workspace();
        var path = Path.Combine(workspace.Root, "storage", "hosted-statistics.json");
        Assert.Equal(new(), NomadNetHostedStatistics.ReadSnapshot(workspace.Root));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        Directory.CreateDirectory(path);
        Assert.Throws<InvalidDataException>(() => NomadNetHostedStatistics.Open(workspace.Root));
        Directory.Delete(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, new byte[NomadNetHostedStatistics.MaximumStateBytes + 1]);
        Assert.Throws<InvalidDataException>(() => NomadNetHostedStatistics.Open(workspace.Root));
        File.Delete(path);
        await using var node = ReticulumNode.Open(workspace.Root);
        using var identity = Identity.Generate();
        var host = new NomadNetHost(node.Router, identity, workspace.Root, new());
        await host.RespondAsync(Context("/page/missing.mu"), default, defaultIndex: false);
        await Task.WhenAll(host.DisposeAsync().AsTask(), host.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(10));
        await using var stats = NomadNetHostedStatistics.Open(workspace.Root);
        Assert.Equal(1, stats.Snapshot.ServedPageRequests);
    }

    [Fact, Trait("TestId", "T969")]
    public async Task PrimaryCountersImportIsBoundedNonoverwritingAndCanceledImportPublishesNothing()
    {
        using var workspace = new Workspace();
        using var fixture = JsonDocument.Parse(typeof(NomadNetHostedStatisticsTests).Assembly
            .GetManifestResourceStream("NomadNet.Tests.NomadNetHostedStatisticsVectors.json")!);
        var peers = fixture.RootElement.GetProperty("persisted").EnumerateObject()
            .ToDictionary(item => (object)item.Name, item => (object?)item.Value.GetInt64());
        var canceledTarget = Path.Combine(workspace.Root, "canceled");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NomadNetHostedStatistics.InitializeFromPrimaryAsync(canceledTarget, peers, canceled.Token));
        Assert.False(Directory.Exists(canceledTarget));
        foreach (var invalid in new object?[] { null, "4", true, -1L, ulong.MaxValue, 1.5 })
        {
            var bad = new Dictionary<object, object?>(peers) { ["served_page_requests"] = invalid };
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                NomadNetHostedStatistics.InitializeFromPrimaryAsync(Path.Combine(workspace.Root, "bad"), bad));
            Assert.False(Directory.Exists(Path.Combine(workspace.Root, "bad")));
        }
        await NomadNetHostedStatistics.InitializeFromPrimaryAsync(workspace.Root, peers);
        await Assert.ThrowsAsync<IOException>(() => NomadNetHostedStatistics.InitializeFromPrimaryAsync(workspace.Root, peers));
        await using var opened = NomadNetHostedStatistics.Open(workspace.Root);
        AssertSnapshot(fixture.RootElement.GetProperty("persisted"), opened.Snapshot);
    }

    [Fact, Trait("TestId", "T970")]
    public async Task DisposalRacingRequestsRetainsEveryAcceptedIncrementAndRejectsLateRequests()
    {
        using var workspace = new Workspace();
        var stats = NomadNetHostedStatistics.Open(workspace.Root);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted = 0;
        var writer = Task.Run(() =>
        {
            for (var index = 0; index < 100_000; index++)
            {
                try { stats.Record(NomadNetHostedRequestKind.Page); }
                catch (ObjectDisposedException) { return; }
                Interlocked.Increment(ref accepted);
                if (index == 100) started.TrySetResult();
            }
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stats.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await writer.WaitAsync(TimeSpan.FromSeconds(5));
        await using var reopened = NomadNetHostedStatistics.Open(workspace.Root);
        Assert.Equal(accepted, reopened.Snapshot.ServedPageRequests);
        Assert.Throws<ObjectDisposedException>(() => stats.Record(NomadNetHostedRequestKind.Page));
    }

    private static void AssertSnapshot(JsonElement expected, NomadNetHostedStatisticsSnapshot actual)
    {
        Assert.Equal(expected.GetProperty("node_connects").GetInt64(), actual.NodeConnects);
        Assert.Equal(expected.GetProperty("served_page_requests").GetInt64(), actual.ServedPageRequests);
        Assert.Equal(expected.GetProperty("served_file_requests").GetInt64(), actual.ServedFileRequests);
        Assert.Equal(expected.GetProperty("served_media_requests").GetInt64(), actual.ServedMediaRequests);
    }

    private static RequestContext Context(string path, object? data = null) => new(path, data, new byte[16], new byte[16], null, 0);

    private sealed class Workspace : IDisposable
    {
        public string Root { get; } = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomad-host-statistics", "test-" + Guid.NewGuid().ToString("N")));
        public Workspace()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "config"), "[reticulum]\n share_instance = No\n[interfaces]\n");
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
