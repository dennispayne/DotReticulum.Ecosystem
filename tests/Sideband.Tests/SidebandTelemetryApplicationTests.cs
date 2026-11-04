using System.Text.Json;
using V = Sideband.SidebandTelemetryValue;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandTelemetryApplicationTests
{
    [Fact]
    public async Task ManualConfigurationReportsPermissionsAppearanceAndOwnedStoreSurviveRestart()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(AppContext.BaseDirectory, "telemetry-app-" + Guid.NewGuid().ToString("N"));
        var hash = new string('1', 32);
        var peer = new string('2', 32);
        try
        {
            var store = new SidebandStateStore(root);
            await store.SetOwnDeliveryHashAsync(hash, token);
            await using (var client = new SidebandClient(store))
            {
                Assert.False((await client.LoadAsync(token)).Telemetry.Enabled);
                await client.AddContactAsync("Peer", peer, token);
                await client.SetConversationTelemetryAsync(peer, true, true, token);
                await client.ConfigureTelemetryAsync(new()
                {
                    Enabled = true, InformationEnabled = true, Information = "Explicit manual information",
                    FixedLocationEnabled = true, FixedLocation = new(51.5, -0.125, 10),
                    Appearance = new("map-marker", "112233", "aabbcc"),
                    ManualSensors = [new(3, Convert.ToHexString(V.From(1012.3).Encode()))],
                    Tracking = [new(peer, 15, DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds())]
                }, token);
                var report = await client.RefreshLocalTelemetryAsync(token);
                Assert.Equal("Explicit manual information", report!.Get(SidebandTelemetrySensorId.Information)!.Data.Get("contents")!.Text);
                Assert.Equal(51.5, report.Get(SidebandTelemetrySensorId.Location)!.Location!.Latitude);
                Assert.Equal(1012.3, report.Get(SidebandTelemetrySensorId.Pressure)!.Data.Get("mbar")!.Number);
                await client.AddContactAsync("Renamed", peer, token);
                var contact = Assert.Single((await client.LoadAsync(token)).Contacts);
                Assert.True(contact.SendTelemetry);
                Assert.True(contact.AllowTelemetryRequests);
                Assert.NotEmpty(await client.GetTelemetryAsync(cancellationToken: token));
                Assert.Equal(SidebandTelemetryQueueStatus.NotConnected, (await client.SendLatestTelemetryAsync(peer, token)).Status);
            }
            await using var reopened = new SidebandClient(new(root));
            var restored = await reopened.LoadAsync(token);
            Assert.True(restored.Telemetry.Enabled);
            Assert.Equal("112233", restored.Telemetry.Appearance.Foreground);
            Assert.Single(restored.Telemetry.Tracking);
            Assert.True(Assert.Single(restored.Contacts).SendTelemetry);
            Assert.NotEmpty(await reopened.GetTelemetryAsync(cancellationToken: token));
            await reopened.ClearTelemetryAsync(hash, token);
            Assert.Empty(await reopened.GetTelemetryAsync(cancellationToken: token));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SettingsActivatePrimaryKeysAndExportWithoutEnablingHardware()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(AppContext.BaseDirectory, "telemetry-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "import.json");
            await File.WriteAllTextAsync(path, """
                {"telemetry_enabled":true,"telemetry_s_information":true,"telemetry_s_information_text":"Manual",
                 "telemetry_s_fixed_location":true,"telemetry_s_fixed_latlon":[51.5,-0.125],"telemetry_s_fixed_altitude":10,
                 "telemetry_send_to_trusted":true,"telemetry_allow_requests_from_anyone":true,
                 "telemetry_send_interval":15,"telemetry_request_interval":30,"telemetry_receive_trusted_only":true,
                 "telemetry_collector":"22222222222222222222222222222222","telemetry_icon":"map-marker",
                 "telemetry_fg":[0,0.2,0.4,1],"telemetry_bg":[1,1,1,1],"telemetry_s_battery":true,
                 "telemetry_s_rns_transport":true,"dotreticulum_runtime_telemetry_tables":true}
                """, token);
            var store = new SidebandStateStore(Path.Combine(root, "profile"));
            Assert.Equal(["telemetry_s_battery"], await store.ImportSettingsAsync(path, token));
            var config = (await store.LoadAsync(cancellationToken: token)).Telemetry;
            Assert.True(config.Enabled);
            Assert.True(config.RnsTransportEnabled);
            Assert.True(config.RnsTransportIncludeTables);
            Assert.True(config.FixedLocationEnabled);
            Assert.True(config.AllowRequestsFromAnyone);
            Assert.Equal(51.5, config.FixedLocation.Latitude);
            Assert.Equal("003366", config.Appearance.Foreground);
            Assert.Empty(config.ManualSensors);
            var export = Path.Combine(root, "export.json");
            await store.ExportSettingsAsync(export, token);
            using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(export, token));
            Assert.Equal(15, json.RootElement.GetProperty("telemetry_send_interval").GetInt32());
            Assert.True(json.RootElement.GetProperty("telemetry_s_fixed_location").GetBoolean());
            Assert.True(json.RootElement.GetProperty("telemetry_s_rns_transport").GetBoolean());
            Assert.True(json.RootElement.GetProperty("dotreticulum_runtime_telemetry_tables").GetBoolean());
            Assert.Equal("Manual", json.RootElement.GetProperty("telemetry_s_information_text").GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CancellationInvalidConfigurationAndConcurrentDisposeKeepCommittedStateAndReleaseLease()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(AppContext.BaseDirectory, "telemetry-lifecycle-" + Guid.NewGuid().ToString("N"));
        try
        {
            var client = new SidebandClient(new(root));
            await client.ConfigureTelemetryAsync(new() { Enabled = true }, token);
            await client.GetTelemetryAsync(cancellationToken: token);
            await Assert.ThrowsAsync<InvalidDataException>(() => client.ConfigureTelemetryAsync(new() { SendIntervalSeconds = 0 }, token));
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ConfigureTelemetryAsync(new(), cancelled.Token));
            await Task.WhenAll(client.DisposeAsync().AsTask(), client.DisposeAsync().AsTask());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetTelemetryAsync(cancellationToken: token));
            await using var reopened = new SidebandClient(new(root));
            Assert.True((await reopened.LoadAsync(token)).Telemetry.Enabled);
            await reopened.GetTelemetryAsync(cancellationToken: token);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DisplayPrivacyFiltersBeforeLimitAndIncludesOwnSourceWithoutTrust()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(AppContext.BaseDirectory, "telemetry-display-" + Guid.NewGuid().ToString("N"));
        var own = new string('1', 32);
        var trusted = new string('2', 32);
        var untrusted = new string('3', 32);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try
        {
            await using (var seed = await SidebandTelemetryStore.OpenAsync(Path.Combine(root, "telemetry"), cancellationToken: token))
            {
                for (var i = 0; i < 257; i++)
                    await seed.AppendAsync(untrusted, SidebandTelemetryCodec.Encode(SidebandTelemetryCodec.Create(now - i)), now, token);
                await seed.AppendAsync(own, SidebandTelemetryCodec.Encode(SidebandTelemetryCodec.Create(now - 300)), now, token);
                await seed.AppendAsync(trusted, SidebandTelemetryCodec.Encode(SidebandTelemetryCodec.Create(now - 301)), now, token);
            }
            var state = new SidebandStateStore(root);
            await state.SetOwnDeliveryHashAsync(own, token);
            await using var client = new SidebandClient(state);
            await client.AddContactAsync("Trusted", trusted, token);
            await client.SetConversationTrustAsync(trusted, true, token);
            await client.ConfigureTelemetryAsync(new() { DisplayTrustedOnly = true }, token);
            Assert.Equal(new[] { own, trusted }, (await client.GetTelemetryAsync(cancellationToken: token)).Select(r => r.SourceHash));
            Assert.Equal(own, Assert.Single(await client.GetTelemetryAsync(new(Limit: 1), token)).SourceHash);
            Assert.Empty(await client.GetTelemetryAsync(new(untrusted), token));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetTelemetryAsync(new(Limit: 257), token));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InvalidManualInputsDoNotActivateCollectionOrOverwriteSettings()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(AppContext.BaseDirectory, "telemetry-config-invalid-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var client = new SidebandClient(new(root));
            var invalid = new SidebandTelemetryConfiguration[]
            {
                new() { Appearance = null! }, new() { ManualSensors = null! },
                new() { Tracking = [new(new string('1', 32), 0, 100)] },
                new() { FixedLocationEnabled = true, FixedLocation = new(double.NaN, 0) },
                new() { ManualSensors = [new(1, "00")] }, new() { ManualSensors = [new(16, "c0")] },
                new() { ManualSensors = [new(3, "c1")] }, new() { ManualSensors = [new(3, "01"), new(3, "02")] },
                new() { ExcludedSources = ["not a delivery hash"] }
            };
            foreach (var configuration in invalid)
            {
                Assert.NotNull(await Record.ExceptionAsync(() => client.ConfigureTelemetryAsync(configuration, token)));
                Assert.False((await client.LoadAsync(token)).Telemetry.Enabled);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task UnreachablePeerWorkCancelsDuringConcurrentDisconnectAndDisposeThenRestartsWithoutSubscribers()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(AppContext.BaseDirectory, "tc-" + Guid.NewGuid().ToString("N"));
        var rns = Path.Combine(root, "rns");
        var data = Path.Combine(root, "data");
        var peerHash = new string('2', 32);
        Directory.CreateDirectory(rns);
        await File.WriteAllTextAsync(Path.Combine(rns, "config"),
            "[reticulum]\n share_instance = no\n enable_transport = no\n\n[logging]\n loglevel = 0\n\n[interfaces]\n", token);
        try
        {
            var client = new SidebandClient(new(data));
            client.StateChanged += _ => throw new InvalidOperationException("Synthetic observer failure");
            client.ErrorReported += _ => throw new InvalidOperationException("Synthetic error observer failure");
            await client.ConfigureTelemetryAsync(new() { Enabled = true }, token);
            await client.ConnectAsync(rns, token);
            using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
            Assert.True((await client.RequestTelemetryAsync(peerHash, caller.Token)).Queued);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            while ((await client.LoadAsync(deadline.Token)).TelemetryPeers?.Any(p => p.DeliveryHash == peerHash && p.LastRequestAttemptUtcSeconds > 0) != true)
                await Task.Delay(20, deadline.Token);
            await caller.CancelAsync();
            var disconnect = client.DisconnectAsync();
            await Task.WhenAll(disconnect, client.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5), token);
            await using var reopened = new SidebandClient(new(data));
            await reopened.ConnectAsync(rns, token);
            Assert.True((await reopened.LoadAsync(token)).TelemetryPeers!.Single().LastRequestAttemptUtcSeconds > 0);
            Assert.NotNull(await reopened.RefreshLocalTelemetryAsync(token));
            Assert.NotEmpty(await reopened.GetTelemetryAsync(cancellationToken: token));
            await reopened.DisconnectAsync();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
