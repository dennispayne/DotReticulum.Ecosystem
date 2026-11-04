using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using LXMF;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandStampPersistenceTests
{
    [Fact]
    public async Task PartialSettingsPreserveIndependentPolicyAndInactiveCostAcrossExportAndRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "stamp-settings-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        try
        {
            var store = new SidebandStateStore(root);
            await store.ConfigureStampPolicyAsync(new(false, 12, false), token);
            var input = Path.Combine(root, "import.json");
            await File.WriteAllTextAsync(input, """{"display_name":"Stamp peer","lxmf_require_stamps":true}""", token);
            Assert.Empty(await store.ImportSettingsAsync(input, token));
            Assert.Equal(new(true, 12, false), SidebandStampConfiguration.FromState(await store.LoadAsync(cancellationToken: token)));
            await File.WriteAllTextAsync(input, """{"lxmf_ignore_invalid_stamps":true}""", token);
            Assert.Empty(await store.ImportSettingsAsync(input, token));
            var policy = new SidebandStampConfiguration(true, 12, true);
            Assert.Equal(policy, SidebandStampConfiguration.FromState(await new SidebandStateStore(root).LoadAsync(cancellationToken: token)));
            var export = Path.Combine(root, "export.json");
            await store.ExportSettingsAsync(export, token);
            using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(export, token));
            Assert.True(json.RootElement.GetProperty("lxmf_require_stamps").GetBoolean());
            Assert.True(json.RootElement.GetProperty("lxmf_ignore_invalid_stamps").GetBoolean());
            Assert.Equal(12, json.RootElement.GetProperty("lxmf_inbound_stamp_cost").GetInt32());
            await using var reopened = new SidebandClient(new(root));
            Assert.Equal(policy, SidebandStampConfiguration.FromState(await reopened.LoadAsync(token)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("""{"display_name":"Changed","lxmf_inbound_stamp_cost":255}""")]
    [InlineData("""{"lxmf_require_stamps":false,"lxmf_inbound_stamp_cost":true}""")]
    [InlineData("""{"lxmf_ignore_invalid_stamps":0}""")]
    public async Task InvalidImportRollsBackWholeStateAndHealthyRetryRecovers(string invalid)
    {
        var root = Path.Combine(Path.GetTempPath(), "stamp-rollback-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        try
        {
            var store = new SidebandStateStore(root);
            await store.ConfigureStampPolicyAsync(new(true, 8, false), token);
            var before = await File.ReadAllBytesAsync(store.StatePath, token);
            var input = Path.Combine(root, "settings.json");
            await File.WriteAllTextAsync(input, invalid, token);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportSettingsAsync(input, token));
            Assert.Equal(before, await File.ReadAllBytesAsync(store.StatePath, token));
            Assert.Equal(new(true, 8, false), SidebandStampConfiguration.FromState(await store.LoadAsync(cancellationToken: token)));
            await File.WriteAllTextAsync(input, """{"lxmf_inbound_stamp_cost":null,"lxmf_ignore_invalid_stamps":true}""", token);
            Assert.Empty(await store.ImportSettingsAsync(input, token));
            Assert.Equal(new(true, null, true), SidebandStampConfiguration.FromState(await new SidebandStateStore(root).LoadAsync(cancellationToken: token)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InvalidSavedPolicyFailsWithoutReplacementAndCancellationDoesNotMutate()
    {
        var root = Path.Combine(Path.GetTempPath(), "stamp-invalid-state-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        try
        {
            var store = new SidebandStateStore(root);
            await store.ConfigureStampPolicyAsync(new(true, 8), token);
            var before = await File.ReadAllBytesAsync(store.StatePath, token);
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ConfigureStampPolicyAsync(new(), cancelled.Token));
            Assert.Equal(before, await File.ReadAllBytesAsync(store.StatePath, token));
            var json = JsonNode.Parse(before)!;
            json["ImportedSettings"]!["lxmf_ignore_invalid_stamps"] = "bad";
            await File.WriteAllTextAsync(store.StatePath, json.ToJsonString(), token);
            var malformed = await File.ReadAllBytesAsync(store.StatePath, token);
            await Assert.ThrowsAsync<InvalidDataException>(() => new SidebandStateStore(root).LoadAsync(cancellationToken: token));
            Assert.Equal(malformed, await File.ReadAllBytesAsync(store.StatePath, token));
            await File.WriteAllBytesAsync(store.StatePath, before, token);
            Assert.Equal(new(true, 8), SidebandStampConfiguration.FromState(await new SidebandStateStore(root).LoadAsync(cancellationToken: token)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DisposalCancelsQueuedPolicyChangeWithoutMutationAndReleasesLifecycleOwnership()
    {
        var root = Path.Combine(Path.GetTempPath(), "stamp-disposal-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        var config = Path.Combine(root, "rns");
        Directory.CreateDirectory(config);
        await File.WriteAllTextAsync(Path.Combine(config, "config"),
            "[reticulum]\n share_instance = no\n enable_transport = no\n\n[logging]\n loglevel = 0\n\n[interfaces]\n", token);
        var store = new SidebandStateStore(Path.Combine(root, "data"));
        var client = new SidebandClient(store);
        try
        {
            await client.ConfigureStampPolicyAsync(new(true, 8, false), token);
            await client.ConnectAsync(config, token);
            var before = await File.ReadAllBytesAsync(store.StatePath, token);
            var gate = Assert.IsType<SemaphoreSlim>(typeof(SidebandClient)
                .GetField("_lifecycle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client));
            await gate.WaitAsync(token);
            Task disposal;
            try
            {
                var update = client.ConfigureStampPolicyAsync(new(false, 12, true), token);
                disposal = client.DisposeAsync().AsTask();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update.WaitAsync(TimeSpan.FromSeconds(5), token));
            }
            finally { gate.Release(); }
            await disposal.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.Equal(before, await File.ReadAllBytesAsync(store.StatePath, token));
            Assert.Equal(new(true, 8, false), SidebandStampConfiguration.FromState(
                await new SidebandStateStore(store.DataDirectory).LoadAsync(cancellationToken: token)));
        }
        finally
        {
            await client.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ActivePolicyAnnouncementRestartCancellationObserversAndDisposalKeepRouterConsistent()
    {
        var root = Path.Combine(Path.GetTempPath(), "stamp-runtime-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        var config = Path.Combine(root, "rns");
        Directory.CreateDirectory(config);
        await File.WriteAllTextAsync(Path.Combine(config, "config"),
            "[reticulum]\n share_instance = no\n enable_transport = no\n\n[logging]\n loglevel = 0\n\n[interfaces]\n", token);
        var client = new SidebandClient(new(Path.Combine(root, "data")));
        try
        {
            var faults = 0;
            client.StateChanged += _ => throw new InvalidOperationException("Synthetic stamp observer");
            client.ErrorReported += _ => throw new InvalidOperationException("Synthetic stamp error observer");
            client.ErrorReported += message => { if (message.Contains("Synthetic stamp observer", StringComparison.Ordinal)) faults++; };
            await client.ConnectAsync(config, token);
            var hash = client.ConnectionInfo!.DeliveryHash;
            Check(new());
            await client.ConfigureStampPolicyAsync(new(true, 8, false), token);
            Check(new(true, 8, false));
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ConfigureStampPolicyAsync(new(), cancelled.Token));
            Check(new(true, 8, false));
            await Assert.ThrowsAsync<InvalidDataException>(() => client.ConfigureStampPolicyAsync(new(true, 255), token));
            Check(new(true, 8, false));
            await client.ConfigureStampPolicyAsync(new(false, 12, true), token);
            Check(new(false, 12, true));
            await client.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(10), token);
            await client.ConnectAsync(config, token);
            Assert.Equal(hash, client.ConnectionInfo!.DeliveryHash);
            Check(new(false, 12, true));
            await Task.WhenAll(client.DisposeAsync().AsTask(), client.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(10), token);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ConfigureStampPolicyAsync(new(), token));
            Assert.True(faults > 0);

            void Check(SidebandStampConfiguration expected)
            {
                var router = Assert.IsType<LxmRouter>(typeof(SidebandClient)
                    .GetField("_router", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client));
                Assert.Equal(expected.EffectiveCost, router.InboundStampCost);
                Assert.Equal(expected.RejectInvalidStamps, router.StampsEnforced);
                Assert.Equal(expected.EffectiveCost, LxmfAppData.StampCost(router.GetAnnounceAppData()));
            }
        }
        finally
        {
            await client.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
