using System.Reflection;
using System.Text.Json;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandStartupAnnouncePersistenceTests
{
    [Fact]
    public async Task PartialImportsAndExportRetainPolicyAndMalformedCancellationNeverReplaceState()
    {
        var root = Path.Combine(Path.GetTempPath(), "startup-settings-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        try
        {
            var store = new SidebandStateStore(root);
            await store.ConfigureAnnouncementAsync(new(false), token);
            var input = Path.Combine(root, "settings.json");
            await File.WriteAllTextAsync(input, """{"display_name":"Startup peer"}""", token);
            Assert.Empty(await store.ImportSettingsAsync(input, token));
            Assert.False(SidebandAnnounceConfiguration.FromState(await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).AnnounceOnStartup);
            var output = Path.Combine(root, "export.json");
            await store.ExportSettingsAsync(output, token);
            using (var json = JsonDocument.Parse(await File.ReadAllBytesAsync(output, token)))
                Assert.False(json.RootElement.GetProperty("start_announce").GetBoolean());
            var before = await File.ReadAllBytesAsync(store.StatePath, token);
            await File.WriteAllTextAsync(input, """{"display_name":"Rejected edit","start_announce":null}""", token);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportSettingsAsync(input, token));
            Assert.Equal(before, await File.ReadAllBytesAsync(store.StatePath, token));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ConfigureAnnouncementAsync(new(true), cancelled.Token));
            Assert.Equal(before, await File.ReadAllBytesAsync(store.StatePath, token));
            await store.ConfigureAnnouncementAsync(new(true), token);
            Assert.True(SidebandAnnounceConfiguration.FromState(await store.LoadAsync(cancellationToken: token)).AnnounceOnStartup);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DisconnectCancelsAndJoinsEachPendingAnnouncementBeforeIdentityRestartAndDisposal()
    {
        var root = Path.Combine(Path.GetTempPath(), "startup-lifecycle-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        var config = Path.Combine(root, "rns");
        Directory.CreateDirectory(config);
        await File.WriteAllTextAsync(Path.Combine(config, "config"),
            "[reticulum]\n share_instance = no\n enable_transport = no\n\n[logging]\n loglevel = 0\n\n[interfaces]\n", token);
        try
        {
            await using var client = new SidebandClient(new(Path.Combine(root, "data")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.AnnounceAsync(token));
            await client.ConnectAsync(config, token); // No subscriber is required.
            var identity = client.ConnectionInfo!.DeliveryHash;
            var first = Pending();
            Assert.False(first.IsCompleted);
            await client.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.True(first.IsCompletedSuccessfully);
            await client.ConnectAsync(config, token);
            Assert.Equal(identity, client.ConnectionInfo!.DeliveryHash);
            var second = Pending();
            Assert.NotSame(first, second);
            var errors = new List<string>();
            client.ErrorReported += errors.Add;
            client.StateChanged += _ => throw new InvalidOperationException("Startup observer failed");
            await client.ConfigureAnnouncementAsync(new(false), token);
            Assert.Contains(errors, error => error.Contains("Startup observer failed", StringComparison.Ordinal));
            await client.AnnounceAsync(token); // Explicit announcements remain available with startup disabled.
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.True(second.IsCompletedSuccessfully);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.AnnounceAsync(token));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ConfigureAnnouncementAsync(new(true), token));

            Task Pending() => Assert.IsAssignableFrom<Task>(typeof(SidebandClient)
                .GetField("_startupAnnounceTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
