using Sideband;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandLifecycleTests
{
    [Fact]
    public async Task DisposalCancelsPathLookupAndAllDisposersWaitForShutdown()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "sideband-lifecycle-" + Guid.NewGuid().ToString("N"));
        var rns = Path.Combine(root, "rns");
        Directory.CreateDirectory(rns);
        await File.WriteAllTextAsync(Path.Combine(rns, "config"),
            "[reticulum]\n  enable_transport = no\n  share_instance = no\n\n[logging]\n  loglevel = 4\n", TestContext.Current.CancellationToken);
        try
        {
            var client = new SidebandClient(new SidebandStateStore(Path.Combine(root, "data")));
            await client.ConnectAsync(rns, TestContext.Current.CancellationToken);
            var sending = client.SendTextAsync("00112233445566778899aabbccddeeff", "cancelled lookup", TestContext.Current.CancellationToken);
            var first = client.DisposeAsync().AsTask();
            var second = client.DisposeAsync().AsTask();
            Assert.Same(first, second);
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
            Assert.False(client.IsConnected);
            Assert.Null(client.ConnectionInfo);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ConnectAsync(rns, TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task FailedSyncAndThrowingObserversDoNotPreventDisconnectAndReconnect()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "sideband-lifecycle-" + Guid.NewGuid().ToString("N"));
        var rns = Path.Combine(root, "rns");
        Directory.CreateDirectory(rns);
        await File.WriteAllTextAsync(Path.Combine(rns, "config"),
            "[reticulum]\n  enable_transport = no\n  share_instance = no\n", TestContext.Current.CancellationToken);
        try
        {
            await using var client = new SidebandClient(new SidebandStateStore(Path.Combine(root, "data")));
            var errors = new List<string>();
            var observations = 0;
            client.ErrorReported += _ => throw new InvalidOperationException("test error observer");
            client.ErrorReported += errors.Add;
            client.StateChanged += _ => throw new InvalidOperationException("test state observer");
            client.StateChanged += _ => observations++;
            await client.ConnectAsync(rns, TestContext.Current.CancellationToken);
            var hash = (await client.LoadAsync(TestContext.Current.CancellationToken)).OwnDeliveryHash;
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.SynchronizeAsync(cancellationToken: TestContext.Current.CancellationToken));
            await client.DisconnectAsync();
            await client.ConnectAsync(rns, TestContext.Current.CancellationToken);
            Assert.Equal(hash, (await client.LoadAsync(TestContext.Current.CancellationToken)).OwnDeliveryHash);
            Assert.True(observations >= 2);
            Assert.Contains(errors, error => error.Contains("state observer", StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
