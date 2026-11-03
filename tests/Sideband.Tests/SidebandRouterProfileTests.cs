using System.Security.Cryptography;
using System.Text.Json;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;
using LXMF;
using Sideband;

namespace Sideband.Tests;

// GenerateSidebandRouterProfile.py executes LXMF e8ba02483217af43be284a72f1c059c730b7d047
// and RNS a29bfb51c468945c96a1ee22cae0114e993e2391, without shims. Public fixture source
// hashes include LXMRouter.py 56b6a8e6b9f79256c4ae297340791eebcc1b6cda3cec04ab2dbe2f1efb53caa9
// and Destination.py e1117e5ff9c4baeda53f9117ae52c4cd0e1e2a4f4167de628e4afb8b4506b8ae.
[Trait("Category", "Integration")]
public sealed class SidebandRouterProfileTests
{
    public static bool ReferenceUnavailable => string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_ROUTER_PROFILE"));

    [Fact]
    public async Task ActualPublicCacheFilesLoadAcrossNativeRouterRestartsWithoutCopyingOtherFiles()
    {
        using var profile = new Profile();
        await File.WriteAllTextAsync(Path.Combine(profile.Source, "unknown-secret"), "must not migrate", TestContext.Current.CancellationToken);
        var before = profile.Fingerprint();
        var result = await SidebandRouterProfile.ImportAsync(profile.Source, profile.Target, profile.Identity, TestContext.Current.CancellationToken);
        Assert.Equal(4, result.ImportedFiles);
        Assert.Equal(before, profile.Fingerprint());
        Assert.False(File.Exists(Path.Combine(profile.Target, "lxmf", "node_stats")));
        Assert.False(File.Exists(Path.Combine(profile.Target, "unknown-secret")));
        Assert.Contains(result.Notices, notice => notice.Contains("memory-only", StringComparison.Ordinal));
        Assert.Equal("unrelated staged state", File.ReadAllText(Path.Combine(profile.Target, "state.json")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.Combine(profile.Target, "lxmf")));
            foreach (var file in Directory.GetFiles(Path.Combine(profile.Target, "lxmf")))
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }
        for (var restart = 0; restart < 2; restart++)
        {
            await using var interfaces = new PacketInterfaceManager([]);
            await using var transport = new TransportRouter(interfaces, new TransportRouterOptions { TimeProvider = profile.Clock });
            await using var router = new LxmRouter(transport, profile.Identity, options: new LxmRouterOptions
            {
                StoragePath = profile.Target, EnableRatchets = false,
            });
            Assert.Equal(5, router.GetOutboundStampCost(profile.Peer));
            Assert.True(router.HasMessage(Convert.FromHexString(profile.Fixture.RootElement.GetProperty("messageHash").GetString()!)));
        }
        Assert.Equal(before, profile.Fingerprint());
    }

    [Fact]
    public async Task NativeTicketsAndOwnRatchetsRemainActiveAndForeignRatchetsAreNotCopied()
    {
        using var profile = new Profile();
        byte[] ciphertext;
        var ownPath = profile.OwnRatchet;
        Directory.CreateDirectory(Path.GetDirectoryName(ownPath)!);
        using (var ratchets = SignedDestinationRatchetFile.Open(ownPath, profile.Identity))
            ciphertext = profile.Identity.Encrypt("ratchet persistence"u8, ratchets.Rotate(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        using var foreign = Identity.Generate();
        var foreignPath = Path.Combine(Path.GetDirectoryName(ownPath)!, Convert.ToHexStringLower(LxMessage.DeliveryDestinationHash(foreign)) + ".ratchets");
        using (var ring = SignedDestinationRatchetFile.Open(foreignPath, foreign)) ring.Rotate(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        string inboundHash;
        string outboundHash;
        using (var tickets = new LxmTicketStore(Path.Combine(profile.Source, "lxmf", "available_tickets")))
        {
            var issued = tickets.Generate(profile.Peer)!;
            try { inboundHash = Digest(issued.Token); }
            finally { CryptographicOperations.ZeroMemory(issued.Token); }
            var outbound = RandomNumberGenerator.GetBytes(16);
            try
            {
                outboundHash = Digest(outbound);
                tickets.Remember(profile.Peer, new LxmTicket(DateTimeOffset.UtcNow.AddDays(21).ToUnixTimeSeconds(), outbound));
            }
            finally { CryptographicOperations.ZeroMemory(outbound); }
            tickets.MarkDelivered(profile.Peer);
        }
        var before = profile.Fingerprint();
        Assert.Equal(5, (await SidebandRouterProfile.ImportAsync(profile.Source, profile.Target, profile.Identity, TestContext.Current.CancellationToken)).ImportedFiles);
        Assert.Equal(before, profile.Fingerprint());
        Assert.False(File.Exists(Path.Combine(profile.Target, "lxmf", "ratchets", Path.GetFileName(foreignPath))));
        for (var restart = 0; restart < 2; restart++)
        {
            using var ring = SignedDestinationRatchetFile.Open(Path.Combine(profile.Target, "lxmf", "ratchets", Path.GetFileName(ownPath)), profile.Identity);
            var plaintext = ring.Decrypt(profile.Identity, ciphertext)!;
            try { Assert.Equal("ratchet persistence"u8.ToArray(), plaintext); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
            using var tickets = new LxmTicketStore(Path.Combine(profile.Target, "lxmf", "available_tickets"));
            var outbound = tickets.GetOutbound(profile.Peer)!;
            try
            {
                Assert.Equal(outboundHash, Digest(outbound.Token));
                var message = LxMessage.Create(profile.Peer, profile.Identity, "ticket proof",
                    timestamp: DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                message.Pack(message.CreateTicketStamp(outbound.Token));
                Assert.True(message.ValidateStamp(254, [outbound.Token]));
            }
            finally { CryptographicOperations.ZeroMemory(outbound.Token); }
            var inbound = tickets.GetInbound(profile.Peer);
            try { Assert.Equal(inboundHash, Digest(Assert.Single(inbound))); }
            finally { foreach (var token in inbound) CryptographicOperations.ZeroMemory(token); }
            Assert.Null(tickets.Generate(profile.Peer));
        }
    }

    [Theory]
    [InlineData("tickets-schema")]
    [InlineData("tickets-value")]
    [InlineData("tickets-count")]
    [InlineData("history-hash")]
    [InlineData("history-time")]
    [InlineData("cost")]
    [InlineData("truncated")]
    [InlineData("oversized")]
    [InlineData("ratchet-signature")]
    [InlineData("ratchet-count")]
    [InlineData("identity")]
    public async Task MalformedOrOversizedStateRollsBackWithoutChangingSourceOrOtherStagedFiles(string scenario)
    {
        using var profile = new Profile();
        var name = scenario.StartsWith("tickets", StringComparison.Ordinal) ? "available_tickets" :
            scenario.StartsWith("history", StringComparison.Ordinal) ? "local_deliveries" : "outbound_stamp_costs";
        var path = Path.Combine(profile.Source, "lxmf", name);
        switch (scenario)
        {
            case "tickets-schema": File.WriteAllBytes(path, MsgPack.Pack(new Dictionary<object, object?> { ["unknown"] = new Dictionary<object, object?>() })); break;
            case "tickets-value": File.WriteAllBytes(path, MsgPack.Pack(new Dictionary<object, object?>
                { ["inbound"] = new Dictionary<object, object?>(), ["last_deliveries"] = new Dictionary<object, object?>(),
                    ["outbound"] = new Dictionary<object, object?> { [profile.Peer] = new object?[] { double.PositiveInfinity, new byte[16] } } })); break;
            case "tickets-count":
                var map = Enumerable.Range(0, 4_097).ToDictionary(index => (object)Convert.FromHexString(index.ToString("x32")), _ => (object?)new object?[] { 1d, new byte[16] });
                File.WriteAllBytes(path, MsgPack.Pack(new Dictionary<object, object?>
                    { ["inbound"] = new Dictionary<object, object?>(), ["last_deliveries"] = new Dictionary<object, object?>(), ["outbound"] = map }));
                break;
            case "history-hash": File.WriteAllBytes(path, MsgPack.Pack(new Dictionary<object, object?> { [new byte[16]] = 1d })); break;
            case "history-time": File.WriteAllBytes(path, MsgPack.Pack(new Dictionary<object, object?> { [new byte[32]] = double.NaN })); break;
            case "cost": File.WriteAllBytes(path, MsgPack.Pack(new Dictionary<object, object?> { [profile.Peer] = new object?[] { 1d, 255L } })); break;
            case "truncated": File.WriteAllBytes(path, [0x81]); break;
            case "oversized": using (var file = File.OpenWrite(path)) file.SetLength(8 * 1024 * 1024 + 1); break;
            case "identity":
                File.Delete(Path.Combine(profile.Source, "primary_identity"));
                using (var other = Identity.Generate()) IdentityFile.SaveNew(other, Path.Combine(profile.Source, "primary_identity"));
                break;
            default:
                Directory.CreateDirectory(Path.GetDirectoryName(profile.OwnRatchet)!);
                var keys = Enumerable.Range(0, scenario == "ratchet-count" ? 513 : 1).Select(_ => RandomNumberGenerator.GetBytes(32)).ToArray();
                byte[] encoded;
                try { encoded = SignedDestinationRatchetFile.Encode(profile.Identity, keys); }
                finally { foreach (var key in keys) CryptographicOperations.ZeroMemory(key); }
                try
                {
                    if (scenario == "ratchet-signature") encoded[^1] ^= 1;
                    File.WriteAllBytes(profile.OwnRatchet, encoded);
                }
                finally { CryptographicOperations.ZeroMemory(encoded); }
                break;
        }
        var before = profile.Fingerprint();
        Assert.NotNull(await Record.ExceptionAsync(() =>
            SidebandRouterProfile.ImportAsync(profile.Source, profile.Target, profile.Identity, TestContext.Current.CancellationToken)));
        Assert.Equal(before, profile.Fingerprint());
        Assert.False(Directory.Exists(Path.Combine(profile.Target, "lxmf")));
        Assert.Empty(Directory.GetDirectories(profile.Target, ".router-profile-*"));
        Assert.Equal("unrelated staged state", File.ReadAllText(Path.Combine(profile.Target, "state.json")));
    }

    [Fact]
    public async Task MissingFilesAreOptionalExistingTargetsAreRefusedAndCancellationRollsBack()
    {
        using var profile = new Profile();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SidebandRouterProfile.ImportAsync(profile.Source, profile.Target, profile.Identity, cancelled.Token));
        Assert.False(Directory.Exists(Path.Combine(profile.Target, "lxmf")));
        using (var stream = File.OpenWrite(Path.Combine(profile.Source, "lxmf", "local_deliveries"))) stream.SetLength(8 * 1024 * 1024);
        using var midRead = new CancellationTokenSource();
        var importing = SidebandRouterProfile.ImportAsync(profile.Source, profile.Target, profile.Identity, midRead.Token);
        await midRead.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importing);
        Assert.False(Directory.Exists(Path.Combine(profile.Target, "lxmf")));
        Directory.Delete(Path.Combine(profile.Source, "lxmf"), true);
        Assert.Equal(0, (await SidebandRouterProfile.ImportAsync(profile.Source, profile.Target, profile.Identity, TestContext.Current.CancellationToken)).ImportedFiles);
        Directory.CreateDirectory(Path.Combine(profile.Target, "lxmf"));
        await Assert.ThrowsAsync<IOException>(() =>
            SidebandRouterProfile.ImportAsync(profile.Source, profile.Target, profile.Identity, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentImportCommitsOneRouterDirectoryWithoutOverwritingStagedState()
    {
        using var profile = new Profile();
        var first = SidebandRouterProfile.ImportAsync(profile.Source, profile.Target, profile.Identity, TestContext.Current.CancellationToken);
        var second = SidebandRouterProfile.ImportAsync(profile.Source, profile.Target, profile.Identity, TestContext.Current.CancellationToken);
        var outcomes = await Task.WhenAll(Record.ExceptionAsync(() => first).AsTask(), Record.ExceptionAsync(() => second).AsTask());
        Assert.Single(outcomes, outcome => outcome is null);
        Assert.IsType<IOException>(Assert.Single(outcomes, outcome => outcome is not null));
        Assert.True(File.Exists(Path.Combine(profile.Target, "lxmf", "available_tickets")));
        Assert.Empty(Directory.GetDirectories(profile.Target, ".router-profile-*"));
        Assert.Equal("unrelated staged state", File.ReadAllText(Path.Combine(profile.Target, "state.json")));
    }

    [Fact]
    public async Task SourceMutationDuringSnapshotIsRejectedWithoutCommittingMixedState()
    {
        using var profile = new Profile();
        var path = Path.Combine(profile.Source, "lxmf", "local_deliveries");
        using (var file = File.OpenWrite(path)) file.SetLength(8 * 1024 * 1024);
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        var import = SidebandRouterProfile.ImportAsync(profile.Source, profile.Target, profile.Identity, TestContext.Current.CancellationToken);
        writer.WriteByte(0xC1);
        await writer.FlushAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(await Record.ExceptionAsync(() => import));
        Assert.False(Directory.Exists(Path.Combine(profile.Target, "lxmf")));
    }

    [Fact(SkipWhen = nameof(ReferenceUnavailable), Skip = "Set DOTRETICULUM_SIDEBAND_ROUTER_PROFILE to GenerateSidebandRouterProfile.py output.")]
    public async Task ActualUpstreamTicketsRatchetsCostsAndDuplicateHistoriesAreActiveAfterNativeRestart()
    {
        using var profile = new Profile();
        var reference = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_ROUTER_PROFILE")!;
        Directory.Delete(profile.Source, true);
        CopyDirectory(Path.Combine(reference, "app_storage"), profile.Source);
        using var identity = IdentityFile.Load(Path.Combine(profile.Source, "primary_identity"));
        using var observed = JsonDocument.Parse(File.ReadAllText(Path.Combine(reference, "observations.json")));
        var expected = observed.RootElement;
        var peer = Convert.FromHexString(expected.GetProperty("peer").GetString()!);
        var before = profile.Fingerprint();
        var target = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_ROUTER_NATIVE") ?? profile.Target;
        if (target != profile.Target)
        {
            Assert.False(Path.Exists(target));
            Directory.CreateDirectory(target);
        }
        Assert.Equal(5, (await SidebandRouterProfile.ImportAsync(profile.Source, target, identity, TestContext.Current.CancellationToken)).ImportedFiles);
        for (var restart = 0; restart < 2; restart++)
        {
            await using var interfaces = new PacketInterfaceManager([]);
            await using var transport = new TransportRouter(interfaces);
            await using var router = new LxmRouter(transport, identity, options: new LxmRouterOptions { StoragePath = target });
            Assert.Equal(5, router.GetOutboundStampCost(peer));
            Assert.True(router.HasMessage(Convert.FromHexString(expected.GetProperty("messageHash").GetString()!)));
            Assert.Equal(LxmPaperIngestResult.Duplicate, await router.IngestPaperUriAsync(expected.GetProperty("uri").GetString()!,
                cancellationToken: TestContext.Current.CancellationToken));
            var outbound = router.Tickets.GetOutbound(peer)!;
            try { Assert.Equal(expected.GetProperty("outboundTicketSha256").GetString(), Digest(outbound.Token)); }
            finally { CryptographicOperations.ZeroMemory(outbound.Token); }
            var inbound = router.Tickets.GetInbound(peer);
            try { Assert.Equal(expected.GetProperty("inboundTicketSha256").GetString(), Digest(Assert.Single(inbound))); }
            finally { foreach (var token in inbound) CryptographicOperations.ZeroMemory(token); }
        }
        var ratchet = Path.Combine(target, "lxmf", "ratchets", expected.GetProperty("delivery").GetString() + ".ratchets");
        using (var ring = SignedDestinationRatchetFile.Open(ratchet, identity))
        {
            var plaintext = ring.Decrypt(identity, Convert.FromHexString(expected.GetProperty("ratchetCiphertext").GetString()!))!;
            try { Assert.Equal(expected.GetProperty("ratchetPlaintext").GetString(), System.Text.Encoding.UTF8.GetString(plaintext)); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        Assert.Equal(before, profile.Fingerprint());
    }

    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var directory in Directory.GetDirectories(source)) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class Profile : IDisposable
    {
        internal string Root { get; } = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "router-tests-" + Guid.NewGuid().ToString("N"));
        internal string Source => Path.Combine(Root, "source");
        internal string Target => Path.Combine(Root, "native-staging");
        internal Identity Identity { get; } = Identity.Generate();
        internal JsonDocument Fixture { get; } = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-router-profile.json")));
        internal TimeProvider Clock => new Clock(DateTimeOffset.UnixEpoch.AddSeconds(Fixture.RootElement.GetProperty("timestamp").GetDouble()));
        internal byte[] Peer => Convert.FromHexString(Fixture.RootElement.GetProperty("peer").GetString()!);
        internal string OwnRatchet => Path.Combine(Source, "lxmf", "ratchets", Convert.ToHexStringLower(LxMessage.DeliveryDestinationHash(Identity)) + ".ratchets");
        internal Profile()
        {
            Directory.CreateDirectory(Path.Combine(Source, "lxmf"));
            Directory.CreateDirectory(Target);
            File.WriteAllText(Path.Combine(Target, "state.json"), "unrelated staged state");
            IdentityFile.SaveNew(Identity, Path.Combine(Source, "primary_identity"));
            File.WriteAllBytes(Path.Combine(Source, "lxmf", "available_tickets"), Convert.FromBase64String(Fixture.RootElement.GetProperty("emptyTickets").GetString()!));
            foreach (var entry in Fixture.RootElement.GetProperty("files").EnumerateObject())
                File.WriteAllBytes(Path.Combine(Source, "lxmf", entry.Name), Convert.FromBase64String(entry.Value.GetString()!));
        }
        internal string Fingerprint() => string.Join("|", Directory.GetFiles(Source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(Source, path) + ":" + Digest(File.ReadAllBytes(path))));
        public void Dispose() { Identity.Dispose(); Fixture.Dispose(); Directory.Delete(Root, true); }
    }
}
