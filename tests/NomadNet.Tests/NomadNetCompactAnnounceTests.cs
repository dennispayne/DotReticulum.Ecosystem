using System.Text;
using System.Text.Json;
using DotReticulum.Core;
using DotReticulum.Transport;
using LXMF;
using NomadNet;

namespace NomadNet.Tests;

// Executed primary f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2; generator and source hashes in fixture.
[Trait("Category", "Unit")]
public sealed class NomadNetCompactAnnounceTests
{
    internal static JsonDocument Fixture() => JsonDocument.Parse(typeof(NomadNetCompactAnnounceTests).Assembly
        .GetManifestResourceStream("NomadNet.Tests.NomadNetCompactAnnounceVectors.json")!);

    [Fact]
    [Trait("TestId", "T230")]
    public void ConfigurationMatchesExecutedPrimary()
    {
        using var fixture = Fixture();
        Assert.Equal("f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2", fixture.RootElement.GetProperty("revision").GetString());
        Assert.Equal("29467f537a64feda32a1c6fe075b3836bccbeeecee1a84628075e0e8fa3c723a",
            fixture.RootElement.GetProperty("sources").GetProperty("nomadnet/Directory.py").GetString());
        foreach (var item in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var input = item.GetProperty("input").GetString();
            var result = NomadNetConfigImport.Parse(input is null ? "" : "[client]\ncompact_announce_stream=" + input);
            Assert.Empty(result.UnsupportedKeys);
            Assert.Equal(item.GetProperty("observed").GetProperty("compact").GetBoolean(),
                item.GetProperty("name").GetString() == "generated" ? new NomadNetSettings().CompactAnnounceStream :
                result.Settings.CompactAnnounceStream);
        }
        Assert.True(new NomadNetSettings().CompactAnnounceStream);
        Assert.Throws<InvalidDataException>(() => NomadNetConfigImport.Parse("[client]\ncompact_announce_stream=maybe"));
        Assert.Throws<InvalidDataException>(() => NomadNetConfigImport.Parse("[client]\ncompact_announce_stream=yes,no"));
        foreach (var item in fixture.RootElement.GetProperty("invalidBooleans").EnumerateArray())
            Assert.Throws<InvalidDataException>(() => NomadNetConfigImport.Parse(
                "[client]\ncompact_announce_stream=" + item.GetProperty("input").GetString()));
    }

    [Fact]
    [Trait("TestId", "T231")]
    public void PresentationAndRawSearchMatchExecutedPrimary()
    {
        using var fixture = Fixture();
        var now = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        foreach (var item in fixture.RootElement.GetProperty("entries").EnumerateArray())
        {
            var entry = new NomadNetAnnounceEntry(now.ToUnixTimeSeconds(), new('1', 32),
                Convert.ToHexStringLower(Encoding.UTF8.GetBytes(item.GetProperty("input").GetString()!)),
                item.GetProperty("kind").GetString()!);
            Assert.Equal(item.GetProperty("labelSuffix").GetString(),
                NomadNetAnnounceStream.Label(entry, item.GetProperty("destination").GetBoolean(), now)[8..]);
        }
        var sample = new NomadNetAnnounceEntry(now.ToUnixTimeSeconds(), new('1', 32), "416263", "peer");
        Assert.True(NomadNetAnnounceStream.Matches(sample, "BC"));
        Assert.False(NomadNetAnnounceStream.Matches(sample, sample.DestinationHash));
        Assert.False(NomadNetAnnounceStream.Matches(sample with { DataHex = "ff" }, "x"));
        Assert.StartsWith(now.ToLocalTime().ToString("yyyy-MM-dd"),
            NomadNetAnnounceStream.Label(sample, false, now.AddDays(1)));
        Assert.Throws<ArgumentException>(() => NomadNetAnnounceStream.Matches(sample, new('x', 4097)));
    }

    [Fact]
    [Trait("TestId", "T232")]
    public void MalformedPrimaryAndBoundsAreRejected()
    {
        static byte[] Pack(object?[] rows) => MsgPack.Pack(new Dictionary<object, object?>
        { ["entry_list"] = Array.Empty<object>(), ["announce_stream"] = rows });
        foreach (var bad in new object?[]
        {
            new object?[] { double.NaN, new byte[16], Array.Empty<byte>(), "peer" },
            new object?[] { -1d, new byte[16], Array.Empty<byte>(), "peer" },
            new object?[] { 1d, new byte[15], Array.Empty<byte>(), "peer" },
            new object?[] { 1d, new byte[16], Array.Empty<byte>(), true },
            new object?[] { 1d, new byte[16], Array.Empty<byte>(), "unknown" },
            new object?[] { 1d, new byte[16], new byte[4097], "peer" },
            new object?[] { "1", new byte[16], Array.Empty<byte>(), "peer" },
            new object?[] { 1d, new byte[16], null, "peer" },
        })
            Assert.Throws<InvalidDataException>(() => NomadNetAnnounceStream.ParsePrimary(Pack([bad])));
        Assert.Throws<InvalidDataException>(() => NomadNetAnnounceStream.ParsePrimary([0xc1]));
        var row = new object?[] { 1d, new byte[16], Array.Empty<byte>(), "peer" };
        Assert.Throws<InvalidDataException>(() => NomadNetAnnounceStream.ParsePrimary(
            Pack(Enumerable.Repeat<object?>(row, 257).ToArray())));
        Assert.Single(NomadNetAnnounceStream.ParsePrimary(Pack([row])));
        Assert.Throws<InvalidDataException>(() => NomadNetAnnounceStream.ParsePrimary(new byte[5 * 1024 * 1024 + 1]));
    }
}

[Trait("Category", "Integration")]
public sealed class NomadNetCompactAnnouncePersistenceTests
{
    internal static string Root()
    {
        var path = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomad-compact", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(path);
        return path;
    }

    private static NomadNetAnnounceEntry Entry(int number, string kind = "peer", double timestamp = 1) =>
        new(timestamp, number.ToString("x32"), Convert.ToHexStringLower("public"u8), kind);

    [Fact]
    [Trait("TestId", "T233")]
    public async Task ReceiveSequencesCapsAndRestartMatchExecutedPrimary()
    {
        using var fixture = NomadNetCompactAnnounceTests.Fixture();
        foreach (var item in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var root = Root();
            try
            {
                var compact = item.GetProperty("observed").GetProperty("compact").GetBoolean();
                var settings = new NomadNetSettings { CompactAnnounceStream = compact };
                await settings.SaveAsync(root);
                Assert.Equal(compact, (await NomadNetSettings.OpenAsync(root)).CompactAnnounceStream);
                var service = await NomadNetAnnounceStream.OpenAsync(root, () => compact);
                var rows = new[] { (1, "peer", "First"), (2, "node", "Node"), (1, "peer", "Updated"),
                    (3, "pn", "Relay"), (2, "node", "Node again"), (4, "peer", "") };
                for (var index = 0; index < rows.Length; index++)
                {
                    var (number, kind, name) = rows[index];
                    var hash = string.Concat(Enumerable.Repeat(number.ToString("x2"), 16));
                    await service.AddAsync(new(index + 1, hash, Convert.ToHexStringLower(Encoding.UTF8.GetBytes(name)), kind));
                    var observed = (await service.SnapshotAsync()).Select(e => $"{e.DestinationHash}:{e.DataHex}:{e.Kind}");
                    var expected = item.GetProperty("observed").GetProperty("rows")[index].EnumerateArray()
                        .Select(e => $"{e.GetProperty("hash").GetString()}:{e.GetProperty("data").GetString()}:{e.GetProperty("kind").GetString()}");
                    Assert.Equal(expected, observed);
                }
                await service.DisposeAsync();
                await using var reloaded = await NomadNetAnnounceStream.OpenAsync(root, () => compact);
                Assert.Equal(item.GetProperty("observed").GetProperty("reloaded").GetArrayLength(),
                    (await reloaded.SnapshotAsync()).Count);
                for (var index = 0; index < 260; index++) await reloaded.AddAsync(Entry(index));
                Assert.Equal(item.GetProperty("observed").GetProperty("peerBound").GetInt32(),
                    (await reloaded.SnapshotAsync()).Count(e => e.Kind == "peer"));
            }
            finally { Directory.Delete(root, true); }
        }
    }

    [Fact]
    [Trait("TestId", "T234")]
    public async Task PrimaryImportIsAtomicIdempotentAndDoesNotCompactHistory()
    {
        var root = Root();
        try
        {
            var source = Path.Combine(root, "primary", "storage");
            Directory.CreateDirectory(source);
            var path = Path.Combine(source, "directory");
            await File.WriteAllBytesAsync(path, MsgPack.Pack(new Dictionary<object, object?>
            { ["entry_list"] = Array.Empty<object>(), ["announce_stream"] = new object?[]
                { new object?[] { 2d, new byte[16], "second"u8.ToArray(), "peer" },
                  new object?[] { 1d, new byte[16], "first"u8.ToArray(), "peer" } } }));
            await using var service = await NomadNetAnnounceStream.OpenAsync(root, () => true);
            Assert.Equal(2, await service.ImportAsync(Path.Combine(root, "primary")));
            Assert.Equal(0, await service.ImportAsync(Path.Combine(root, "primary")));
            var before = await File.ReadAllBytesAsync(Path.Combine(root, "storage", "announce-stream.json"));
            await File.WriteAllBytesAsync(path, [0xc1]);
            await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(Path.Combine(root, "primary")));
            Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(root, "storage", "announce-stream.json")));
            Assert.Equal(2, (await service.SnapshotAsync()).Count);
            await service.AddAsync(new(3, new('0', 32), "", "peer"));
            Assert.Single(await service.SnapshotAsync());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("TestId", "T235")]
    public async Task CancellationObserverFaultsDisposalAndFailedWriteRecover()
    {
        var root = Root();
        try
        {
            var compact = false;
            var service = await NomadNetAnnounceStream.OpenAsync(root, () => compact);
            var errors = new List<string>();
            var healthyObserver = 0;
            service.Log = _ => throw new InvalidOperationException("bad logger");
            service.Log += errors.Add;
            service.Changed = () => throw new InvalidOperationException("bad observer");
            service.Changed += () => healthyObserver++;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.AddAsync(Entry(1), new(true)));
            Assert.Empty(await service.SnapshotAsync());
            await File.WriteAllTextAsync(Path.Combine(root, "storage"), "public blocker");
            await Assert.ThrowsAsync<IOException>(() => service.AddAsync(Entry(1)));
            Assert.Empty(await service.SnapshotAsync());
            File.Delete(Path.Combine(root, "storage"));
            await service.AddAsync(Entry(1));
            await service.AddAsync(Entry(1, timestamp: 2));
            compact = true;
            Assert.Equal(2, (await service.SnapshotAsync()).Count);
            await service.AddAsync(Entry(1, timestamp: 3));
            Assert.Single(await service.SnapshotAsync());
            Assert.Equal(3, healthyObserver);
            Assert.Equal(3, errors.Count);
            var writes = Enumerable.Range(2, 16).Select(index => service.AddAsync(Entry(index))).ToArray();
            var disposal = service.DisposeAsync().AsTask();
            await Task.WhenAll(writes);
            await disposal;
            await service.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => service.AddAsync(Entry(22)));
            await using var reloaded = await NomadNetAnnounceStream.OpenAsync(root, () => true);
            Assert.Equal(17, (await reloaded.SnapshotAsync()).Count);
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "storage"), "*.new"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("TestId", "T239")]
    public async Task RegisteredHandlersBoundQueueFilterPathsAndRecoverWithoutObservers()
    {
        var root = Root();
        using var identity = Identity.Generate();
        try
        {
            var engine = new AnnounceEngine();
            var destination = Destination.Create(DestinationType.Single, "lxmf", ["delivery"], identity);
            var node = Destination.Create(DestinationType.Single, "nomadnetwork", ["node"], identity);
            var pn = Destination.Create(DestinationType.Single, "lxmf", ["propagation"], identity);
            var service = await NomadNetAnnounceStream.OpenAsync(root, () => false);
            var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reports = new System.Collections.Concurrent.ConcurrentQueue<string>();
            service.Log = reports.Enqueue;
            service.Accept = async (_, _) => { entered.TrySetResult(); await blocked.Task; return true; };
            service.Start(engine);
            Assert.Throws<InvalidOperationException>(() => service.Start(engine));
            var emitted = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            engine.Process(Announce.Create(identity, destination, LxmfAppData.Build("Public"), unixSeconds: emitted), null);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            for (var index = 0; index < NomadNetAnnounceStream.MaximumPending + 5; index++)
                engine.Process(Announce.Create(identity, destination, LxmfAppData.Build("Public"), unixSeconds: ++emitted), null);
            Assert.Contains(reports, message => message.Contains("queue full", StringComparison.Ordinal));
            blocked.TrySetResult();
            await service.DisposeAsync();
            await using var reloaded = await NomadNetAnnounceStream.OpenAsync(root, () => false);
            var entries = await reloaded.SnapshotAsync();
            Assert.InRange(entries.Count, 1, NomadNetAnnounceStream.MaximumPending + 1);
            Assert.All(entries, entry => Assert.Equal("Public", Encoding.UTF8.GetString(Convert.FromHexString(entry.DataHex))));

            var next = await NomadNetAnnounceStream.OpenAsync(root, () => true);
            next.Start(engine);
            engine.Process(Announce.Create(identity, node, "Hidden path"u8.ToArray(), pathResponse: true, unixSeconds: ++emitted), null);
            engine.Process(Announce.Create(identity, pn, [0xc1]), null);
            engine.Process(Announce.Create(identity, node, "Healthy node"u8.ToArray(), unixSeconds: ++emitted), null);
            await next.DisposeAsync();
            await using var final = await NomadNetAnnounceStream.OpenAsync(root, () => true);
            var healthy = await final.SnapshotAsync();
            Assert.Single(healthy, e => e.Kind == "node");
            Assert.DoesNotContain(healthy, e => e.Kind == "pn");
            Assert.Contains(healthy, e => Encoding.UTF8.GetString(Convert.FromHexString(e.DataHex)) == "Healthy node");
        }
        finally { Directory.Delete(root, true); }
    }
}
