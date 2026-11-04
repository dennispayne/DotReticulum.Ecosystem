using System.Reflection;
using System.Text.Json;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandSynchronizationPersistenceTests
{
    [Fact]
    public async Task PartialImportsExportRestartAndMalformedRollbackRetainNullablePolicy()
    {
        var root = Path.Combine(Path.GetTempPath(), "sync-settings-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        try
        {
            var store = new SidebandStateStore(root);
            await store.ConfigureSynchronizationAsync(new(true, 2), token);
            var input = Path.Combine(root, "settings.json");
            await File.WriteAllTextAsync(input, """{"display_name":"Sync peer"}""", token);
            Assert.Empty(await store.ImportSettingsAsync(input, token));
            Assert.Equal(new(true, 2), SidebandSynchronizationConfiguration.FromState(
                await new SidebandStateStore(root).LoadAsync(cancellationToken: token)));
            var before = await File.ReadAllBytesAsync(store.StatePath, token);
            foreach (var malformed in new[] { """{"display_name":"Invalid","lxmf_sync_max":0}""",
                """{"lxmf_sync_limit":1}""", """{"lxmf_sync_max":"3"}""" })
            {
                await File.WriteAllTextAsync(input, malformed, token);
                await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportSettingsAsync(input, token));
                Assert.Equal(before, await File.ReadAllBytesAsync(store.StatePath, token));
            }
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ConfigureSynchronizationAsync(new(false), cancelled.Token));
            Assert.Equal(before, await File.ReadAllBytesAsync(store.StatePath, token));
            await File.WriteAllTextAsync(input, """{"lxmf_sync_limit":null}""", token);
            Assert.Empty(await store.ImportSettingsAsync(input, token));
            var export = Path.Combine(root, "export.json");
            await store.ExportSettingsAsync(export, token);
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(export, token));
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("lxmf_sync_limit").ValueKind);
            Assert.Equal(2, document.RootElement.GetProperty("lxmf_sync_max").GetInt32());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DisconnectedDisposedCancelledAndObserverFaultPathsPreserveServiceAndSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "sync-lifecycle-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        var config = Path.Combine(root, "rns");
        Directory.CreateDirectory(config);
        await File.WriteAllTextAsync(Path.Combine(config, "config"),
            "[reticulum]\n share_instance = no\n enable_transport = no\n\n[logging]\n loglevel = 0\n\n[interfaces]\n", token);
        try
        {
            var store = new SidebandStateStore(Path.Combine(root, "data"));
            await using var client = new SidebandClient(store);
            var errors = new List<string>();
            client.ErrorReported += errors.Add;
            await client.ConfigureSynchronizationAsync(new(true, 2), token); // No subscriber is required.
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.SynchronizeConfiguredAsync(token));
            client.StateChanged += _ => throw new InvalidOperationException("Sync observer fault");
            await client.ConfigureSynchronizationAsync(new(false, 4), token);
            Assert.Contains(errors, error => error.Contains("Sync observer fault", StringComparison.Ordinal));
            await client.ConnectAsync(config, token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.SynchronizeConfiguredAsync(token));
            Assert.Null((await client.LoadAsync(token)).LastSynchronization);
            var gate = Assert.IsType<SemaphoreSlim>(typeof(SidebandClient)
                .GetField("_lifecycle", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client));
            var before = await File.ReadAllBytesAsync(store.StatePath, token);
            await gate.WaitAsync(token);
            Task disposal;
            try
            {
                var change = client.ConfigureSynchronizationAsync(new(true, 1), token);
                disposal = client.DisposeAsync().AsTask();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => change.WaitAsync(TimeSpan.FromSeconds(5), token));
                Assert.Equal(before, await File.ReadAllBytesAsync(store.StatePath, token));
            }
            finally { gate.Release(); }
            await disposal.WaitAsync(TimeSpan.FromSeconds(10), token);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ConfigureSynchronizationAsync(new(), token));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.SynchronizeConfiguredAsync(token));
            Assert.Equal(new(false, 4), SidebandSynchronizationConfiguration.FromState(await store.LoadAsync(cancellationToken: token)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
