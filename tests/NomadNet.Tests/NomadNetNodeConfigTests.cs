using System.Text.Json;
using LXMF;

namespace NomadNet.Tests;

// Executed primary f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2 with real pinned LXMF 1.2.1.
// GenerateNomadNetNodeConfigVectors.py records actual constructor observations and source hashes.
[Trait("Category", "Unit")]
public sealed class NomadNetNodeConfigTests
{
    private const string First = "00112233445566778899aabbccddeeff";
    private const string Second = "11223344556677889900aabbccddeeff";

    [Fact]
    public void NodeOptionsMatchActualPrimaryConstructorsIncludingRouterOnlySyncClamp()
    {
        using var fixture = JsonDocument.Parse(typeof(NomadNetNodeConfigTests).Assembly
            .GetManifestResourceStream("NomadNet.Tests.NomadNetNodeConfigVectors.json")!);
        Assert.Equal("f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2", fixture.RootElement.GetProperty("commit").GetString());
        foreach (var vector in fixture.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var imported = NomadNetConfigImport.Parse(vector.GetProperty("input").GetString()!);
            Assert.Empty(imported.UnsupportedKeys);
            var actual = imported.Settings;
            var options = NomadNetNodeConfiguration.CreateOptions(actual);
            var expected = vector.GetProperty("observed");
            Assert.Equal((long)(expected.GetProperty("transfer_kb").GetDouble() * 1000), actual.PropagationTransferLimitBytes);
            Assert.Equal((long)(expected.GetProperty("sync_kb").GetDouble() * 1000), actual.PropagationSyncLimitBytes);
            Assert.Equal((long)(expected.GetProperty("storage_mb").GetDouble() * 1_000_000), actual.PropagationStorageLimitBytes);
            Assert.Equal(expected.GetProperty("stamp_cost").GetInt32(), actual.PropagationStampCost);
            var peerCap = expected.GetProperty("max_peers");
            Assert.Equal(peerCap.ValueKind == JsonValueKind.Null ? null : (int?)peerCap.GetInt32(), actual.PropagationMaxPeers);
            Assert.Equal((long)(expected.GetProperty("effective_transfer_kb").GetDouble() * 1000), options.TransferLimit);
            Assert.Equal((long)(expected.GetProperty("effective_sync_kb").GetDouble() * 1000), options.SyncLimit);
            Assert.Equal(expected.GetProperty("effective_storage_bytes").GetInt64(), options.StorageLimit);
            Assert.Equal(expected.GetProperty("effective_max_peers").GetInt32(), options.MaxPeers);
            Assert.Equal(expected.GetProperty("effective_static_peers").EnumerateArray().Select(value => value.GetString()),
                options.StaticPeers.Select(Convert.ToHexStringLower));
            Assert.Equal(expected.GetProperty("effective_priorities").EnumerateArray().Select(value => value.GetString()),
                options.PrioritisedDestinations.Select(Convert.ToHexStringLower));
        }
    }

    [Fact]
    public void ConfigListsHandleQuotedHashesWhitespaceDuplicatesTrailingCommaAndReportInvalidEntries()
    {
        var result = NomadNetConfigImport.Parse($"""
            [node]
            static_peers = '{First.ToUpperInvariant()}', "11 22 33 44 55 66 77 88 99 00 aa bb cc dd ee ff", invalid,
            prioritise_destinations = "{Second}", '{Second.ToUpperInvariant()}'
            """);
        Assert.Equal([First, Second], result.Settings.PropagationStaticPeers);
        Assert.Equal([Second], result.Settings.PrioritisedDestinations);
        Assert.Single(result.Warnings);
        Assert.Contains("node.static_peers", result.Warnings[0]);
        Assert.DoesNotContain("invalid,", result.Warnings[0]);
        var interrupted = NomadNetConfigImport.Parse($"""
            [node]
            static_peers = "0 {First[1..]}"
            prioritise_destinations = "1 {Second[1..]}"
            """);
        Assert.Empty(interrupted.Settings.PropagationStaticPeers);
        Assert.Empty(interrupted.Settings.PrioritisedDestinations);
        Assert.Equal(2, interrupted.Warnings.Count);
    }

    [Fact]
    public void TerminalChangesAreIsolatedBoundedAndCreateOwnedLowerLayerOptions()
    {
        var original = new NomadNetSettings();
        var changed = NomadNetNodeConfiguration.Change(original, "static", First, "on");
        changed = NomadNetNodeConfiguration.Change(changed, "priority", Second, "on");
        changed = NomadNetNodeConfiguration.Change(changed, "transfer", "20000");
        changed = NomadNetNodeConfiguration.Change(changed, "storage", "0.125");
        changed = NomadNetNodeConfiguration.Change(changed, "cost", "1");
        changed = NomadNetNodeConfiguration.Change(changed, "peers", "-5");
        Assert.Empty(original.PropagationStaticPeers);
        Assert.Empty(original.PrioritisedDestinations);
        Assert.Equal(125_000, changed.PropagationStorageLimitBytes);
        Assert.Equal(13, changed.PropagationStampCost);
        Assert.Equal(0, changed.PropagationMaxPeers);
        var options = NomadNetNodeConfiguration.CreateOptions(changed);
        Assert.Equal(20_000_000, options.SyncLimit);
        options.StaticPeers.Single()[0] ^= 0xff;
        options.PrioritisedDestinations.Single()[0] ^= 0xff;
        Assert.Equal(First, changed.PropagationStaticPeers.Single());
        Assert.Equal(Second, changed.PrioritisedDestinations.Single());
        Assert.Empty(NomadNetNodeConfiguration.Change(changed, "static", First.ToUpperInvariant(), "off").PropagationStaticPeers);
        Assert.Null(NomadNetNodeConfiguration.Change(changed, "peers", "default").PropagationMaxPeers);
        Assert.Equal(new LxmPropagationOptions().MaxPeers,
            NomadNetNodeConfiguration.CreateOptions(NomadNetNodeConfiguration.Change(changed, "peers", "default")).MaxPeers);
        Assert.Throws<ArgumentException>(() => NomadNetNodeConfiguration.Change(original, "static", "bad", "on"));
        Assert.Throws<ArgumentException>(() => NomadNetNodeConfiguration.Change(original, "priority", Second, "maybe"));
    }

    [Theory]
    [InlineData("max_transfer_size", "NaN")]
    [InlineData("max_transfer_size", "Infinity")]
    [InlineData("max_transfer_size", "1e300")]
    [InlineData("max_transfer_size", "100000")]
    [InlineData("max_sync_size", "1000000")]
    [InlineData("message_storage_limit", "100000")]
    [InlineData("message_storage_limit", "-Infinity")]
    [InlineData("propagation_cost", "257")]
    [InlineData("propagation_cost", "1.5")]
    [InlineData("max_peers", "65537")]
    [InlineData("max_peers", "2147483648")]
    public void InvalidOrUnsafeNodeConfigurationFailsBeforeStarting(string key, string value) =>
        Assert.Throws<InvalidDataException>(() => NomadNetConfigImport.Parse($"[node]\n{key}={value}"));

    [Fact]
    public void DestinationBoundsAndMalformedNativeStateAreNotSilentlyDropped()
    {
        Assert.Equal(128, NomadNetConfigImport.Parse("[node]\nstatic_peers=" +
            string.Join(',', Enumerable.Repeat(First, 128))).Settings.PropagationStaticPeers.Count);
        Assert.Throws<InvalidDataException>(() => NomadNetConfigImport.Parse("[node]\nstatic_peers=" +
            string.Join(',', Enumerable.Repeat("x", 129))));
        Assert.Throws<InvalidDataException>(() => NomadNetNodeConfiguration.CreateOptions(new()
            { PropagationStaticPeers = Enumerable.Repeat(First, 129).ToList() }));
        Assert.Throws<InvalidDataException>(() => NomadNetNodeConfiguration.CreateOptions(new()
            { PrioritisedDestinations = null! }));
        Assert.Throws<ArgumentException>(() => NomadNetNodeConfiguration.CreateOptions(new()
            { PrioritisedDestinations = ["bad"] }));
    }
}

[Trait("Category", "Integration")]
public sealed class NomadNetNodeConfigPersistenceTests
{
    [Fact]
    public async Task SettingsUpdatesPersistAtomicallySurviveRestartAndDoNotExposePolicyLists()
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomadnet-node-config-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        try
        {
            const string hash = "00112233445566778899aabbccddeeff";
            var initial = new NomadNetSettings { PropagationStaticPeers = [hash], PrioritisedDestinations = [hash] };
            await using var policy = new NomadNetPropagation(directory, initial, () => [], _ => { },
                (_, _) => Task.FromResult(0), () => { });
            initial.PropagationStaticPeers.Clear();
            initial.PrioritisedDestinations.Clear();
            var snapshot = policy.Settings;
            snapshot.PropagationStaticPeers.Clear();
            snapshot.PrioritisedDestinations.Clear();
            Assert.Single(policy.Settings.PropagationStaticPeers);
            Assert.Single(policy.Settings.PrioritisedDestinations);
            NomadNetSettings? retained = null;
            await policy.UpdateSettingsAsync(current => retained = NomadNetNodeConfiguration.Change(current, "storage", "0.125"));
            retained!.PropagationStaticPeers.Clear();
            retained.PrioritisedDestinations.Clear();
            Assert.Single(policy.Settings.PropagationStaticPeers);
            Assert.Single(policy.Settings.PrioritisedDestinations);
            var restored = await NomadNetSettings.OpenAsync(directory);
            Assert.Equal(125_000, restored.PropagationStorageLimitBytes);
            Assert.Single(restored.PropagationStaticPeers);
            Assert.Single(restored.PrioritisedDestinations);
            var persisted = await File.ReadAllBytesAsync(Path.Combine(directory, "settings.json"));
            await Assert.ThrowsAsync<InvalidDataException>(() => policy.UpdateSettingsAsync(current =>
                current with { PropagationStampCost = 257 }));
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => policy.UpdateSettingsAsync(current =>
                current with { PropagationStorageLimitBytes = 5000 }, canceled.Token));
            Assert.Equal(persisted, await File.ReadAllBytesAsync(Path.Combine(directory, "settings.json")));
            Assert.Equal(125_000, policy.Settings.PropagationStorageLimitBytes);
            Assert.Empty(Directory.GetFiles(directory, "*.new"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ExpandedBoundedProfileRoundTripsFreshUsesPrimaryStorageAndLegacyRetainsExistingQuota()
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomadnet-node-config-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.Equal(2_000_000_000, (await NomadNetSettings.OpenAsync(Path.Combine(directory, "fresh"))).PropagationStorageLimitBytes);
            var hashes = Enumerable.Range(0, 128).Select(index => index.ToString("x32")).ToList();
            var settings = new NomadNetSettings
            {
                TrustedNodes = [.. hashes],
                PropagationStaticPeers = [.. hashes],
                PrioritisedDestinations = [.. hashes],
                PagesPath = new string('\u754c', 2048),
                FilesPath = new string('\u754c', 2048),
                DownloadsPath = new string('\u754c', 2048),
            };
            await settings.SaveAsync(directory);
            Assert.True(new FileInfo(Path.Combine(directory, "settings.json")).Length > 16 * 1024);
            var restored = await NomadNetSettings.OpenAsync(directory);
            Assert.Equal(hashes, restored.PropagationStaticPeers);
            Assert.Equal(hashes, restored.PrioritisedDestinations);
            await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"), "{\"schemaVersion\":1,\"displayName\":\"Legacy\"}",
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            restored = await NomadNetSettings.OpenAsync(directory);
            Assert.Equal(500_000_000, restored.PropagationStorageLimitBytes);
            await restored.SaveAsync(directory);
            Assert.Equal(500_000_000, (await NomadNetSettings.OpenAsync(directory)).PropagationStorageLimitBytes);
            await File.WriteAllBytesAsync(Path.Combine(directory, "settings.json"), new byte[NomadNetSettings.MaximumSettingsBytes + 1]);
            await Assert.ThrowsAsync<InvalidDataException>(() => NomadNetSettings.OpenAsync(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
