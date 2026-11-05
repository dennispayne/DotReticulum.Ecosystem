using System.Collections.Concurrent;
using Sideband.Desktop;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandConsoleApplicationTests
{
    private const string Peer = "0123456789abcdef0123456789abcdef";

    [Fact]
    [Trait("TestId", "T371")]
    public async Task UnnamedConsoleCreationAndEmptyRenameKeepFollowingAnnouncementsAcrossRestart()
    {
        var root = Root();
        var token = TestContext.Current.CancellationToken;
        try
        {
            var store = new SidebandStateStore(root);
            await using var client = new SidebandClient(store);
            await using var console = new SidebandConsole(client, store, _ => { }, _ => { }, token);
            await Execute("conv create " + Peer + " ''");
            Assert.False(Assert.Single((await client.LoadAsync(token)).Contacts).NameIsUserDefined);
            await store.ObserveDeliveryAnnounceAsync(SidebandContact.Create("First observed", Peer), token);
            Assert.Equal("First observed", Assert.Single((await client.LoadAsync(token)).Contacts).DisplayName);
            await Execute("conv name " + Peer + " 'Alias'");
            await store.ObserveAnnounceAsync(new(Peer, SidebandAnnounce.Delivery, DateTimeOffset.UtcNow, "Cached observed"), token);
            await Execute("conv name " + Peer + " ''");
            Assert.Equal("Cached observed", Assert.Single((await client.LoadAsync(token)).Contacts).DisplayName);
            await new SidebandStateStore(root).ObserveDeliveryAnnounceAsync(SidebandContact.Create("After restart", Peer), token);
            Assert.Equal("After restart", Assert.Single((await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).Contacts).DisplayName);

            async Task Execute(string line) => Assert.True(await console.ExecuteAsync(
                SidebandConsoleSyntax.Resolve(SidebandConsoleSyntax.Parse(line))!, token));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ConversationConfigurationAndTelemetryCommandsActivatePersistedStateWithoutSubscribers()
    {
        var root = Root();
        var token = TestContext.Current.CancellationToken;
        try
        {
            var store = new SidebandStateStore(root);
            await using var client = new SidebandClient(store);
            var output = new ConcurrentQueue<string>();
            await using var console = new SidebandConsole(client, store, output.Enqueue, output.Enqueue, token);
            await Execute("conv create " + Peer + " 'Unicode Ω 雪'");
            await Execute("conv trust 'unicode Ω 雪'");
            await Execute("conv name " + Peer + " 'Renamed peer'");
            await Execute("config set general display_name 'CLI name Ω'");
            await Execute("config set general start_announce false");
            await Execute("config set general lxm_limit_1mb false");
            await Execute("config set lxmf inbound_stamp_cost 8");
            await Execute("config set lxmf require_stamps yes");
            await Execute("config set lxmf sync_max 7");
            await Execute("config set lxmf ignore_unknown on");
            await Execute("config set sensors information_text 'quoted value ; $(no-shell)'");
            await Execute("config set sensors information on");
            await Execute("config set sensors fixed_latlon '[1.5,-2.5]'");
            await Execute("config set sensors fixed_altitude 3");
            await Execute("config set sensors fixed_location true");
            await Execute("telemetry on");
            await Execute("telemetry");
            await Execute("telemetry track 'Renamed peer' 60 120");
            var state = await new SidebandStateStore(root).LoadAsync(cancellationToken: token);
            Assert.Equal("CLI name Ω", state.DisplayName);
            Assert.False(SidebandAnnounceConfiguration.FromState(state).AnnounceOnStartup);
            Assert.False(SidebandDeliveryConfiguration.FromState(state).LimitIncomingToOneMegabyte);
            Assert.Equal(8, SidebandStampConfiguration.FromState(state).EffectiveCost);
            Assert.Equal(7, SidebandSynchronizationConfiguration.FromState(state).MaximumMessages);
            Assert.True((await client.GetCommandAuthorizationAsync(token)).IgnoreUnknown);
            Assert.True(Assert.Single(state.Contacts).Trusted);
            Assert.Equal("Renamed peer", Assert.Single(state.Contacts).DisplayName);
            Assert.Equal(1.5, state.Telemetry.FixedLocation.Latitude);
            Assert.Equal(-2.5, state.Telemetry.FixedLocation.Longitude);
            Assert.Equal(3, state.Telemetry.FixedLocation.Altitude);
            Assert.Single(state.Telemetry.Tracking);
            Assert.Contains(output, line => line.Contains("quoted value ; $(no-shell)", StringComparison.Ordinal));
            await Execute("telemetry untrack 'Renamed peer'");
            await Execute("telemetry off");
            await Execute("status");
            Assert.Contains(output, line => line.StartsWith("Sideband Status", StringComparison.Ordinal) && line.Contains("Conversations: 1", StringComparison.Ordinal));
            Assert.Empty((await client.LoadAsync(token)).Telemetry.Tracking);
            Assert.False((await client.LoadAsync(token)).Telemetry.Enabled);

            async Task Execute(string line) => Assert.True(await console.ExecuteAsync(SidebandConsoleSyntax.Resolve(SidebandConsoleSyntax.Parse(line))!, token));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FailuresDoNotStoreUnsupportedArchiveKeysOrChangePreviousValuesAndServiceContinues()
    {
        var root = Root();
        var token = TestContext.Current.CancellationToken;
        try
        {
            var store = new SidebandStateStore(root);
            await using var client = new SidebandClient(store);
            await using var console = new SidebandConsole(client, store, _ => { }, _ => { }, token);
            var failures = new[]
            {
                "config set connect tcp true", "config set plugins services true", "raw arbitrary-code",
                "plugins reload", "config set sensors battery true", "config set voice output speaker",
                "config set telemetry mqtt_validate_ssl true", "config set lxmf sync_limit 3",
                "config set lxmf sync_interval -1", "config set sensors fixed_latlon '[100,2]'",
                "config set telemetry fg '[2,0,0,1]'", "config set telemetry fg '[[[[]]]]'",
                "config set sensors fixed_altitude NaN", "config set general display_name ''",
                "conv msgs " + Peer + " -1", "telemetry list 257", "telemetry track " + Peer + " 0 1",
                "net propagation set malformed", "log 8"
            };
            foreach (var line in failures)
                await Assert.ThrowsAnyAsync<Exception>(() => console.ExecuteAsync(SidebandConsoleSyntax.Resolve(SidebandConsoleSyntax.Parse(line))!, token));
            var state = await client.LoadAsync(token);
            Assert.Equal("Sideband", state.DisplayName);
            Assert.Equal(43_200, state.SynchronizationIntervalSeconds);
            Assert.DoesNotContain(state.ImportedSettings ?? new Dictionary<string, System.Text.Json.JsonElement>(),
                item => item.Key is "connect_tcp" or "mqtt_validate_ssl" or "service_plugins_enabled");
            Assert.True(await console.ExecuteAsync(SidebandConsoleSyntax.Resolve(["conv", "create", Peer])!, token));
            Assert.Single((await client.LoadAsync(token)).Contacts);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ClearAndDeleteRemoveOnlySelectedHistoryAndDeleteTelemetryWithoutSubscriber()
    {
        var root = Root();
        var token = TestContext.Current.CancellationToken;
        try
        {
            var store = new SidebandStateStore(root);
            await using var client = new SidebandClient(store);
            await client.AddContactAsync("Peer", Peer, token);
            await store.AddMessageAsync(new(Guid.NewGuid(), Peer, true, "", "synthetic history", DateTimeOffset.UtcNow, "Received"), token);
            await client.ClearConversationAsync(Peer, cancellationToken: token);
            Assert.Empty((await client.LoadAsync(token)).Messages);
            Assert.Single((await client.LoadAsync(token)).Contacts);
            await store.AddMessageAsync(new(Guid.NewGuid(), Peer, true, "", "second synthetic history", DateTimeOffset.UtcNow, "Received"), token);
            await client.ClearConversationAsync(Peer, deleteConversation: true, cancellationToken: token);
            var persisted = await new SidebandStateStore(root).LoadAsync(cancellationToken: token);
            Assert.Empty(persisted.Messages);
            Assert.Empty(persisted.Contacts);
            Assert.Empty(await client.GetTelemetryAsync(cancellationToken: token));
            await client.AddContactAsync("Recovered", Peer, token);
            Assert.Single((await client.LoadAsync(token)).Contacts);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ReloadRejectsCorruptStateWithoutReplacingCachedStateAndCancelledMutationsDoNotPersist()
    {
        var root = Root();
        var token = TestContext.Current.CancellationToken;
        try
        {
            var store = new SidebandStateStore(root);
            await using var client = new SidebandClient(store);
            await client.AddContactAsync("Peer", Peer, token);
            var bytes = await File.ReadAllBytesAsync(store.StatePath, token);
            await File.WriteAllTextAsync(store.StatePath, "invalid", token);
            await Assert.ThrowsAnyAsync<Exception>(() => client.ReloadConfigurationAsync(token));
            Assert.Single((await client.LoadAsync(token)).Contacts);
            await File.WriteAllBytesAsync(store.StatePath, bytes, token);
            await client.ReloadConfigurationAsync(token);
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ClearConversationAsync(Peer, true, cancelled.Token));
            Assert.Single((await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).Contacts);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FourOwnedSendsAndSingleSyncRemainCancellableAndServiceSurvivesPeerLookupFailure()
    {
        var root = Root();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(30));
        var token = stop.Token;
        try
        {
            var config = Path.Combine(root, "rns");
            Directory.CreateDirectory(config);
            await File.WriteAllTextAsync(Path.Combine(config, "config"),
                "[reticulum]\n share_instance = no\n enable_transport = no\n\n[logging]\n loglevel = 0\n\n[interfaces]\n", token);
            var store = new SidebandStateStore(Path.Combine(root, "data"));
            await using var client = new SidebandClient(store);
            await client.ConfigureAnnouncementAsync(new(false), token);
            await client.ConnectAsync(config, token);
            var output = new ConcurrentQueue<string>();
            var errors = new ConcurrentQueue<string>();
            await using var console = new SidebandConsole(client, store, output.Enqueue, errors.Enqueue, token);
            for (var index = 0; index < 4; index++) await Execute("conv send " + Peer + " pending");
            await Assert.ThrowsAsync<InvalidOperationException>(() => Execute("conv send " + Peer + " overflow"));
            await Execute("status");
            Assert.Contains(output, line => line.Contains("Operations: 4", StringComparison.Ordinal));
            await Execute("cancel");
            Assert.Equal(4, output.Count(line => line == "CANCELLED conv send"));
            await Execute("net propagation set " + Peer);
            await Execute("conv sync");
            await Assert.ThrowsAsync<InvalidOperationException>(() => Execute("conv sync"));
            await Execute("conv sync status");
            await Execute("conv sync cancel");
            Assert.Contains(output, line => line == "CANCELLED conv sync");
            await Execute("conv create " + Peer + " 'healthy after failed lookup'");
            Assert.Single((await client.LoadAsync(token)).Contacts);
            Assert.Empty(errors);

            async Task Execute(string line) => Assert.True(await console.ExecuteAsync(SidebandConsoleSyntax.Resolve(SidebandConsoleSyntax.Parse(line))!, token));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ActivatedConfigurationAliasesArePrimaryRegistrationsAndMapValuesSurviveRestart()
    {
        var root = Root();
        var token = TestContext.Current.CancellationToken;
        try
        {
            var store = new SidebandStateStore(root);
            await using var client = new SidebandClient(store);
            using var fixture = System.Text.Json.JsonDocument.Parse(await File.ReadAllBytesAsync(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-cli.json"), token));
            var aliases = fixture.RootElement.GetProperty("configuration").EnumerateArray()
                .Select(s => s.GetProperty("category").GetString() + " " + s.GetProperty("alias").GetString()).ToHashSet();
            var configuration = new SidebandConsoleConfiguration(client);
            foreach (var setting in configuration.Settings) Assert.Contains(setting.Category + " " + setting.Alias, aliases);
            Assert.Equal(configuration.Settings.Count, configuration.Settings.Select(s => s.Category + " " + s.Alias).Distinct().Count());
            await using var console = new SidebandConsole(client, store, _ => { }, _ => { }, token);
            foreach (var line in new[] { "config set map lat 51.5", "config set map lon -0.5",
                "config set map zoom 5", "config set map cluster false", "config set map interfaces false", "config set map layer opentopo" })
                Assert.True(await console.ExecuteAsync(SidebandConsoleSyntax.Resolve(SidebandConsoleSyntax.Parse(line))!, token));
            await using var maps = await SidebandMapStore.OpenAsync(Path.Combine(root, "maps"), cancellationToken: token);
            Assert.Equal(51.5, maps.ViewSettings.Latitude);
            Assert.Equal(-0.5, maps.ViewSettings.Longitude);
            Assert.Equal(5, maps.ViewSettings.Zoom);
            Assert.False(maps.ViewSettings.ClusterMarkers);
            Assert.False(maps.ViewSettings.IncludeInterfaces);
            Assert.Equal(SidebandMapPresets.Create("opentopo").Provider, maps.ViewSettings.Provider);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static string Root() => Path.Combine(AppContext.BaseDirectory, "console-tests-" + Guid.NewGuid().ToString("N"));
}
