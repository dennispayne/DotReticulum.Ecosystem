using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DotReticulum.Applications;
using DotReticulum.Applications.Rncp;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace NomadNet.Tests;

// GenerateNomadNetUtilityVectors.py executes primary 1.4.4 f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2.
// The fixture records exact Git-blob SHA256 and disclosed synthetic UI/transport owners.
[Trait("Category", "Unit")]
public sealed class NomadNetUtilityTests
{
    [Fact]
    [Trait("TestId", "T350")]
    public void UtilitiesMatchIndependentlyExecutedPrimaryContracts()
    {
        using var fixture = JsonDocument.Parse(typeof(NomadNetUtilityTests).Assembly
            .GetManifestResourceStream("NomadNet.Tests.NomadNetUtilityVectors.json")!);
        var root = fixture.RootElement;
        Assert.Equal("f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2", root.GetProperty("revision").GetString());
        Assert.Equal("61a75d64a244d13e543d7c915d1611bbde87cf28db8450def4424a5634c7c1b1",
            root.GetProperty("sources").GetProperty("nomadnet/ui/textui/Utilities.py").GetString());
        Assert.Equal("57bd1e08e5c68b452f2314626ec67d73871ea2468a5af54cc15a5b54f17323e9",
            root.GetProperty("sources").GetProperty("nomadnet/ui/textui/Transport.py").GetString());
        var defaults = root.GetProperty("defaults");
        Assert.Equal(NomadNetUtilities.DefaultProbeSize, defaults.GetProperty("probeSize").GetInt32());
        Assert.Equal(NomadNetUtilities.DefaultTimeoutSeconds, defaults.GetProperty("probeTimeout").GetDouble());
        Assert.Equal(NomadNetSpeedTest.DestinationName, defaults.GetProperty("speedName").GetString());
        Assert.Equal(defaults.GetProperty("speedSizes").GetArrayLength(), NomadNetSpeedTest.Sizes.Count);
        foreach (var size in defaults.GetProperty("speedSizes").EnumerateArray())
            Assert.Equal(size[1].GetInt64(), NomadNetSpeedTest.Sizes[size[0].GetString()!]);
        foreach (var item in root.GetProperty("probes").EnumerateArray())
            Assert.Equal((item.GetProperty("payload").GetInt32(), item.GetProperty("remaining").GetInt32()),
                NomadNetUtilities.PrepareProbe(item.GetProperty("size").GetString(), item.GetProperty("count").GetString()));
        foreach (var item in root.GetProperty("timeouts").EnumerateArray())
            Assert.Equal(item.GetProperty("seconds").GetDouble(), NomadNetUtilities.TimeoutSeconds(
                item.GetProperty("firstHop").GetDouble(), item.GetProperty("medium").GetDouble()));
        foreach (var item in root.GetProperty("sizes").EnumerateArray())
            Assert.Equal(item.GetProperty("text").GetString(), NomadNetSpeedTest.FormatSize(item.GetProperty("input").GetInt64()));
        foreach (var item in root.GetProperty("rates").EnumerateArray())
            Assert.Equal(item.GetProperty("text").GetString(), NomadNetSpeedTest.FormatRate(item.GetProperty("input").GetDouble()));
        using var identity = Identity.Generate();
        var expected = Destination.ComputeHashFromNameAndIdentity(NomadNetSpeedTest.DestinationName, identity.Hash);
        foreach (var item in root.GetProperty("authorization").EnumerateArray())
            Assert.Equal(item.GetProperty("resources").GetBoolean(), NomadNetSpeedTest.IsAuthorized(
                item.GetProperty("name").GetString() switch { "matching" => expected, "different" => new byte[16], _ => null }, identity));
        Assert.Single(root.GetProperty("resources").EnumerateArray());
        Assert.False(root.GetProperty("resources")[0].GetProperty("autoCompress").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("resources")[0].GetProperty("metadata").ValueKind);
        Assert.Equal(new[] { true, true, false },
            root.GetProperty("pathExpiry").GetProperty("results").EnumerateArray().Select(value => value.GetBoolean()));
        Assert.Equal(0, root.GetProperty("pathExpiry").GetProperty("timestamp").GetInt64());
        Assert.Equal("a29bfb51c468945c96a1ee22cae0114e993e2391",
            root.GetProperty("pathExpiry").GetProperty("rnsRevision").GetString());
        Assert.False(root.GetProperty("received").GetProperty("linkClosed").GetBoolean());
        Assert.True(root.GetProperty("received").GetProperty("activeCleared").GetBoolean());
        Assert.True(root.GetProperty("received").GetProperty("resourceCleared").GetBoolean());
        Assert.Equal(100, root.GetProperty("received").GetProperty("progress").GetInt32());
        foreach (var reception in root.GetProperty("reception").EnumerateArray())
        {
            double? Value(string name) => reception.GetProperty(name).ValueKind == JsonValueKind.Null
                ? null : reception.GetProperty(name).GetDouble();
            Assert.Equal(reception.GetProperty("text").GetString(),
                NomadNetUtilities.FormatReceptionStats(new(true, .1, 1, Value("rssi"), Value("snr"), Value("quality"))));
        }
    }

    [Theory]
    [InlineData("501", "1")]
    [InlineData("16", "1001")]
    [InlineData("9999999999999999999", "1")]
    [InlineData("16", "9999999999999999999")]
    [Trait("TestId", "T351")]
    public void BoundedProbeInputsRejectExcessiveValuesInsteadOfQuietFallback(string size, string count) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => NomadNetUtilities.PrepareProbe(size, count));

    [Fact]
    [Trait("TestId", "T351")]
    public void BoundedTimingFormattingAndSizesRejectInvalidValues()
    {
        foreach (var value in new[] { -1d, double.NaN, double.PositiveInfinity, 3601 })
            Assert.Throws<InvalidDataException>(() => NomadNetUtilities.TimeoutSeconds(value, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => NomadNetSpeedTest.FormatRate(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => NomadNetSpeedTest.FormatRate(double.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => NomadNetSpeedTest.FormatSize(-1));
        Assert.Throws<InvalidDataException>(() =>
            NomadNetUtilities.FormatReceptionStats(new(true, .1, 1, double.NaN)));
        Assert.Equal(0, new NomadNetProbeResult([]).LossPercent);
        Assert.Equal(50, new NomadNetProbeResult([new(true, .1, 1), new(false, null, null)]).LossPercent);
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, long>)NomadNetSpeedTest.Sizes).Add("unbounded", long.MaxValue));
    }

    [Fact]
    [Trait("TestId", "T352")]
    public void ManagementOutputIsAotSafeBoundedAndNeverAnObjectTypeName()
    {
        var output = NomadNetUtilities.RenderManagement(new Dictionary<object, object?>
            { ["hash"] = new byte[] { 1, 0xff }, ["entries"] = new object?[] { null, true, 1L, 2d, "public\ntext" } });
        using var decoded = JsonDocument.Parse(output);
        Assert.Equal("01ff", decoded.RootElement.GetProperty("hash").GetString());
        Assert.Equal(5, decoded.RootElement.GetProperty("entries").GetArrayLength());
        Assert.Throws<InvalidDataException>(() => NomadNetUtilities.RenderManagement(new object()));
        Assert.Throws<InvalidDataException>(() => NomadNetUtilities.RenderManagement(double.NaN));
        Assert.Throws<InvalidDataException>(() => NomadNetUtilities.RenderManagement(new Dictionary<object, object?> { [1L] = "bad" }));
        Assert.Throws<InvalidDataException>(() => NomadNetUtilities.RenderManagement(new string('x', 8193)));
        var recursive = new List<object?>();
        recursive.Add(recursive);
        Assert.Throws<InvalidDataException>(() => NomadNetUtilities.RenderManagement(recursive));
        Assert.Throws<InvalidDataException>(() => NomadNetUtilities.RenderManagement(Enumerable.Repeat<object?>(null, 10001)));
        Assert.Throws<InvalidDataException>(() =>
            NomadNetUtilities.RenderManagement(Enumerable.Repeat<object?>(new string('x', 8188), 512)));
    }
}

[Trait("Category", "Integration")]
public sealed class NomadNetUtilityLifecycleTests
{
    [Fact]
    [Trait("TestId", "T353")]
    public async Task CurrentOwnerManagementResolutionProofsDropCancellationAndRecoveryWork()
    {
        await using var rig = await Rig.CreateAsync();
        var utility = new NomadNetUtilities(rig.Alice, rig.AliceIdentity, rig.Root);
        foreach (var table in new[] { "paths", "rates", "interfaces" })
            using (JsonDocument.Parse(NomadNetUtilities.RenderManagement(await utility.GetTableAsync(table)))) { }
        Assert.Equal(rig.BobIdentity.HexHash, await utility.ResolveIdentityAsync(rig.Probe.Destination.Hash));
        using (var path = JsonDocument.Parse(NomadNetUtilities.RenderManagement(await utility.ResolvePathAsync(rig.Probe.Destination.Hash))))
            Assert.Equal(Convert.ToHexStringLower(rig.Probe.Destination.Hash), path.RootElement.GetProperty("destination").GetString());
        var probes = await utility.ProbeAsync("rnstransport.probe", rig.Probe.Destination.Hash, 16, 2);
        Assert.Equal(2, probes.Sent);
        Assert.Equal(2, probes.Received);
        Assert.Equal(0, probes.LossPercent);
        Assert.True(await utility.DropPathAsync(rig.Probe.Destination.Hash));
        Assert.True(await utility.DropPathAsync(rig.Probe.Destination.Hash));
        Assert.False(await utility.DropPathAsync(new byte[16]));
        await utility.ResolvePathAsync(rig.Probe.Destination.Hash);
        await Assert.ThrowsAsync<ArgumentException>(() => utility.ProbeAsync("wrong.name", rig.Probe.Destination.Hash));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => utility.ProbeAsync("rnstransport.probe", rig.Probe.Destination.Hash, Packet.Mtu));
        using var cancelled = new CancellationTokenSource();
        var missing = utility.ResolvePathAsync(new byte[16], cancelled.Token);
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => missing);
        Assert.Equal(1, (await utility.ProbeAsync("rnstransport.probe", rig.Probe.Destination.Hash)).Received);
    }

    [Fact]
    [Trait("TestId", "T354")]
    public async Task AuthorizedSpeedTestsStreamMultipleSegmentsBothWaysWithoutSubscribersAndReleaseFiles()
    {
        await using var rig = await Rig.CreateAsync();
        await using var alice = new NomadNetSpeedTest(rig.Alice, rig.AliceIdentity, rig.Root);
        await using var bob = new NomadNetSpeedTest(rig.Bob, rig.BobIdentity, rig.Root);
        await alice.StartListeningAsync(bob.DestinationHash);
        await bob.StartListeningAsync(alice.DestinationHash);
        var sent = await alice.SendAsync(bob.DestinationHash, "4 MB");
        await UntilAsync(() => bob.LastReceived?.Sha256 == sent.Sha256);
        Assert.Equal(4 * 1024 * 1024, bob.LastReceived!.Bytes);
        Assert.True(sent.PhysicalBytes > sent.Bytes);
        Assert.Equal(sent.PhysicalBytes, bob.LastReceived.PhysicalBytes);
        var reverse = await bob.SendAsync(alice.DestinationHash, "2 KB");
        await UntilAsync(() => alice.LastReceived?.Sha256 == reverse.Sha256);
        Assert.Equal(reverse.Bytes, alice.LastReceived!.Bytes);
        await alice.StopListeningAsync();
        await bob.StopListeningAsync();
        Assert.False(alice.Listening);
        Assert.False(bob.Listening);
        Assert.Empty(Directory.GetFiles(Path.Combine(rig.Root, "storage", "utility-work"), "speed-*"));
    }

    [Fact]
    [Trait("TestId", "T355")]
    public async Task WrongPeerMalformedInputsAndObserverFailuresDoNotPreventHealthyServiceOrRestart()
    {
        await using var rig = await Rig.CreateAsync();
        await using var alice = new NomadNetSpeedTest(rig.Alice, rig.AliceIdentity, rig.Root);
        var bob = new NomadNetSpeedTest(rig.Bob, rig.BobIdentity, rig.Root) { Log = _ => throw new IOException("observer") };
        try
        {
            await bob.StartListeningAsync(new string('0', 32));
            var failure = await Record.ExceptionAsync(() => alice.SendAsync(bob.DestinationHash, "2 KB"));
            Assert.True(failure is IOException or InvalidOperationException, failure?.ToString());
            Assert.Null(bob.LastReceived);
            await bob.StopListeningAsync();
            await Assert.ThrowsAsync<ArgumentException>(() => bob.StartListeningAsync("invalid"));
            await bob.StartListeningAsync(alice.DestinationHash);
            await Assert.ThrowsAsync<InvalidOperationException>(() => bob.StartListeningAsync(alice.DestinationHash));
            var sent = await alice.SendAsync(bob.DestinationHash, "2 KB");
            await UntilAsync(() => bob.LastReceived?.Sha256 == sent.Sha256);
        }
        finally { await Task.WhenAll(bob.DisposeAsync().AsTask(), bob.DisposeAsync().AsTask()); }
        await Assert.ThrowsAsync<ObjectDisposedException>(() => bob.StartListeningAsync(alice.DestinationHash));
        await using var restarted = new NomadNetSpeedTest(rig.Bob, rig.BobIdentity, rig.Root);
        Assert.Equal(bob.DestinationHash, restarted.DestinationHash);
        await restarted.StartListeningAsync(alice.DestinationHash);
        var recovered = await alice.SendAsync(restarted.DestinationHash, "2 KB");
        await UntilAsync(() => restarted.LastReceived?.Sha256 == recovered.Sha256);
    }

    [Fact]
    [Trait("TestId", "T356")]
    public async Task UnidentifiedPeerExpiresAtReferenceDeadlineAndNextPeerStillWorks()
    {
        await using var rig = await Rig.CreateAsync();
        await using var alice = new NomadNetSpeedTest(rig.Alice, rig.AliceIdentity, rig.Root);
        await using var bob = new NomadNetSpeedTest(rig.Bob, rig.BobIdentity, rig.Root);
        await bob.StartListeningAsync(alice.DestinationHash);
        var connected = await ClientLinks.ConnectAsync(rig.Alice, Convert.FromHexString(bob.DestinationHash), TimeSpan.FromSeconds(12));
        var link = Assert.IsType<Link>(connected.Link);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await UntilAsync(() => link.Status == LinkStatus.Closed);
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(8));
        var sent = await alice.SendAsync(bob.DestinationHash, "2 KB");
        await UntilAsync(() => bob.LastReceived?.Sha256 == sent.Sha256);
    }

    [Fact]
    [Trait("TestId", "T357")]
    public async Task CancellationDisposalAndInactiveStartDoNotLeakDialsOrDiskJobs()
    {
        await using var rig = await Rig.CreateAsync();
        var alice = new NomadNetSpeedTest(rig.Alice, rig.AliceIdentity, rig.Root);
        var sending = alice.SendAsync(new string('0', 32), "16 MB");
        await UntilAsync(() => Directory.Exists(Path.Combine(rig.Root, "storage", "utility-work")) &&
            Directory.GetFiles(Path.Combine(rig.Root, "storage", "utility-work"), "speed-*").Length == 1);
        await Task.WhenAll(alice.DisposeAsync().AsTask(), alice.DisposeAsync().AsTask())
            .WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        Assert.Empty(Directory.GetFiles(Path.Combine(rig.Root, "storage", "utility-work"), "speed-*"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => alice.SendAsync(new string('0', 32), "2 KB"));
        var utility = new NomadNetUtilities(rig.Alice, rig.AliceIdentity, rig.Root);
        await Assert.ThrowsAnyAsync<IOException>(() => utility.SendFileAsync(new byte[16], Path.Combine(rig.Root, "missing")));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => utility.SendFileAsync(new byte[16], "unused", token: cancelled.Token));
    }

    [Fact]
    [Trait("TestId", "T358")]
    public async Task FileUtilityStreamsNamedMultisegmentDataToAuthorizedRncpWithoutRetainingStaging()
    {
        await using var rig = await Rig.CreateAsync();
        var saved = Path.Combine(rig.Root, "received");
        Directory.CreateDirectory(saved);
        var listener = new RncpListener(rig.Bob.Router, rig.BobIdentity,
            new() { SaveDirectory = saved, AllowedIdentities = [rig.AliceIdentity.Hash] });
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.FileReceived = path => received.TrySetResult(path);
        await listener.AnnounceAsync();
        var file = Path.Combine(rig.Root, "public file 'sample'.bin");
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(4 * 1024 * 1024);
        await File.WriteAllBytesAsync(file, bytes);
        var utility = new NomadNetUtilities(rig.Alice, rig.AliceIdentity, rig.Root);
        var sent = await utility.SendFileAsync(listener.DestinationHash, file);
        Assert.True(sent.Success, sent.Message);
        Assert.Equal(bytes.Length, sent.Bytes);
        Assert.True(sent.PhysicalBytes > sent.Bytes);
        var path = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(Path.GetFileName(file), Path.GetFileName(path));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(rig.Root, "storage", "utility-work")));
        using var cancelled = new CancellationTokenSource();
        var unavailable = utility.SendFileAsync(new byte[16], file, token: cancelled.Token);
        await UntilAsync(() => Directory.GetDirectories(Path.Combine(rig.Root, "storage", "utility-work")).Length == 1);
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => unavailable);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(rig.Root, "storage", "utility-work")));
    }

    private static async Task UntilAsync(Func<bool> predicate)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!predicate()) await Task.Delay(20, stop.Token);
    }

    private sealed class Rig : IAsyncDisposable
    {
        public string Root { get; }
        public ReticulumNode Alice { get; }
        public ReticulumNode Bob { get; }
        public Identity AliceIdentity { get; } = Identity.Generate();
        public Identity BobIdentity { get; } = Identity.Generate();
        public LocalDestination Probe { get; }

        private Rig()
        {
            Root = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomad-utilities-" + Guid.NewGuid().ToString("N")));
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            Alice = CreateNode("alice", $"type=TCPServerInterface\nlisten_ip=127.0.0.1\nlisten_port={port}");
            Bob = CreateNode("bob", $"type=TCPClientInterface\ntarget_host=127.0.0.1\ntarget_port={port}");
            Probe = new(BobIdentity, Destination.Create(DestinationType.Single, "rnstransport", ["probe"], BobIdentity))
                { ProofStrategy = ProofStrategy.All };
            Bob.Router.RegisterLocalDestination(Probe);
        }

        private ReticulumNode CreateNode(string name, string network)
        {
            var path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "config"),
                "[reticulum]\nshare_instance=No\n[logging]\nloglevel=0\n[interfaces]\n[[test]]\nenabled=Yes\n" + network + "\n");
            return ReticulumNode.Open(path);
        }

        public static async Task<Rig> CreateAsync()
        {
            var rig = new Rig();
            try
            {
                await rig.Alice.StartAsync();
                await rig.Bob.StartAsync();
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!rig.Alice.Router.HasPath(rig.Probe.Destination.Hash))
                {
                    await rig.Bob.Router.AnnounceAsync(rig.Probe, cancellationToken: stop.Token);
                    await Task.Delay(50, stop.Token);
                }
                return rig;
            }
            catch { await rig.DisposeAsync(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            Bob.Router.DeregisterLocalDestination(Probe);
            Probe.Dispose();
            await Bob.DisposeAsync();
            await Alice.DisposeAsync();
            BobIdentity.Dispose();
            AliceIdentity.Dispose();
            Directory.Delete(Root, true);
        }
    }
}
