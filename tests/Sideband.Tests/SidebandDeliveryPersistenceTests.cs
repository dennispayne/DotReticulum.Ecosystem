using System.Reflection;
using System.Text.Json;
using LXMF;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandDeliveryPersistenceTests
{
    [Fact]
    public async Task PartialImportExportRestartMalformedRollbackAndCancellationPreservePolicy()
    {
        var root = Path.Combine(Path.GetTempPath(), "delivery-settings-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        try
        {
            var store = new SidebandStateStore(root);
            await store.ConfigureDeliveryAsync(new(false), token);
            var input = Path.Combine(root, "settings.json");
            await File.WriteAllTextAsync(input, """{"display_name":"Delivery peer"}""", token);
            Assert.Empty(await store.ImportSettingsAsync(input, token));
            Assert.False(SidebandDeliveryConfiguration.FromState(await new SidebandStateStore(root).LoadAsync(cancellationToken: token))
                .LimitIncomingToOneMegabyte);
            var output = Path.Combine(root, "export.json");
            await store.ExportSettingsAsync(output, token);
            using (var exported = JsonDocument.Parse(await File.ReadAllBytesAsync(output, token)))
                Assert.False(exported.RootElement.GetProperty("lxm_limit_1mb").GetBoolean());
            var before = await File.ReadAllBytesAsync(store.StatePath, token);
            await File.WriteAllTextAsync(input, """{"display_name":"Rejected edit","lxm_limit_1mb":"false"}""", token);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportSettingsAsync(input, token));
            Assert.Equal(before, await File.ReadAllBytesAsync(store.StatePath, token));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ConfigureDeliveryAsync(new(true), cancelled.Token));
            Assert.Equal(before, await File.ReadAllBytesAsync(store.StatePath, token));
            await File.WriteAllTextAsync(input, """{"lxm_limit_1mb":true}""", token);
            Assert.Empty(await store.ImportSettingsAsync(input, token));
            Assert.True(SidebandDeliveryConfiguration.FromState(await store.LoadAsync(cancellationToken: token)).LimitIncomingToOneMegabyte);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DisposalCancelsQueuedLimitChangeWithoutChangingPersistedOrActivePolicy()
    {
        var root = Path.Combine(Path.GetTempPath(), "delivery-disposal-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        var config = Path.Combine(root, "rns");
        Directory.CreateDirectory(config);
        await File.WriteAllTextAsync(Path.Combine(config, "config"),
            "[reticulum]\n share_instance = no\n enable_transport = no\n\n[logging]\n loglevel = 0\n\n[interfaces]\n", token);
        var store = new SidebandStateStore(Path.Combine(root, "data"));
        await using var client = new SidebandClient(store);
        try
        {
            await client.ConnectAsync(config, token);
            var before = await File.ReadAllBytesAsync(store.StatePath, token);
            var gate = Assert.IsType<SemaphoreSlim>(typeof(SidebandClient)
                .GetField("_lifecycle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client));
            await gate.WaitAsync(token);
            Task disposal;
            try
            {
                var update = client.ConfigureDeliveryAsync(new(false), token);
                disposal = client.DisposeAsync().AsTask();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update.WaitAsync(TimeSpan.FromSeconds(5), token));
                Assert.Equal(before, await File.ReadAllBytesAsync(store.StatePath, token));
            }
            finally { gate.Release(); }
            await disposal.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.True(SidebandDeliveryConfiguration.FromState(await store.LoadAsync(cancellationToken: token)).LimitIncomingToOneMegabyte);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ActiveLimitChangesPersistAcrossIdentityRestartAndIsolateObservers()
    {
        var root = Path.Combine(Path.GetTempPath(), "delivery-active-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        var config = Path.Combine(root, "rns");
        Directory.CreateDirectory(config);
        await File.WriteAllTextAsync(Path.Combine(config, "config"),
            "[reticulum]\n share_instance = no\n enable_transport = no\n\n[logging]\n loglevel = 0\n\n[interfaces]\n", token);
        try
        {
            await using var client = new SidebandClient(new(Path.Combine(root, "data")));
            var errors = new List<string>();
            client.ErrorReported += errors.Add;
            client.StateChanged += _ => throw new InvalidOperationException("Delivery observer failed");
            await client.ConnectAsync(config, token);
            Assert.Equal(1_000_000, Router().DeliveryPerTransferLimit);
            var identity = client.ConnectionInfo!.DeliveryHash;
            await client.ConfigureDeliveryAsync(new(false), token);
            Assert.Equal(128_000_000, Router().DeliveryPerTransferLimit);
            Assert.Contains(errors, error => error.Contains("Delivery observer failed", StringComparison.Ordinal));
            await client.DisconnectAsync();
            await client.ConnectAsync(config, token);
            Assert.Equal(identity, client.ConnectionInfo!.DeliveryHash);
            Assert.Equal(128_000_000, Router().DeliveryPerTransferLimit);
            await client.ConfigureDeliveryAsync(new(true), token);
            Assert.Equal(1_000_000, Router().DeliveryPerTransferLimit);
            await client.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ConfigureDeliveryAsync(new(false), token));

            LxmRouter Router() => Assert.IsType<LxmRouter>(typeof(SidebandClient)
                .GetField("_router", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
