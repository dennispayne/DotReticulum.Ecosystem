namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandRemoteCommandApplicationTests
{
    [Fact]
    public async Task AuthorizationIsSharedPersistentAtomicAndNeverEnablesTelemetryOrPythonPlugins()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "rc-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        try
        {
            await using (var client = new SidebandClient(new(root)))
            {
                Assert.Equal(new(), await client.GetCommandAuthorizationAsync(token));
                await client.ConfigureCommandAuthorizationAsync(new(true, true, true), token);
                Assert.False((await client.LoadAsync(token)).Telemetry.Enabled);
                Assert.True((await client.LoadAsync(token)).Telemetry.AllowRequestsFromAnyone);
                using var cancelled = new CancellationTokenSource();
                await cancelled.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    client.ConfigureCommandAuthorizationAsync(new(), cancelled.Token));
                Assert.Equal(new(true, true, true), await client.GetCommandAuthorizationAsync(token));
                Assert.Equal(SidebandCommandQueueStatus.NotConnected, (await client.SendCommandAsync(new string('1', 32), "ping", cancellationToken: token)).Status);
                Assert.Equal(SidebandCommandQueueStatus.Empty, (await client.SendCommandAsync(new string('1', 32), "", cancellationToken: token)).Status);
                Assert.Equal(SidebandCommandQueueStatus.NoAddress, (await client.SendCommandAsync("bad", "ping", cancellationToken: token)).Status);
            }
            await using var reopened = new SidebandClient(new(root));
            Assert.Equal(new(true, true, true), await reopened.GetCommandAuthorizationAsync(token));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task BoundedQueueCancellationConcurrentDisposalAndReconnectDoNotReplayCommands()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "rq-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        var rns = Path.Combine(root, "rns");
        Directory.CreateDirectory(rns);
        await File.WriteAllTextAsync(Path.Combine(rns, "config"),
            "[reticulum]\n share_instance = no\n enable_transport = no\n\n[logging]\n loglevel = 0\n\n[interfaces]\n", token);
        try
        {
            var client = new SidebandClient(new(Path.Combine(root, "data")));
            client.StateChanged += _ => throw new InvalidOperationException("Synthetic command state observer");
            client.ErrorReported += _ => throw new InvalidOperationException("Synthetic command error observer");
            await client.ConnectAsync(rns, token);
            using var commands = CancellationTokenSource.CreateLinkedTokenSource(token);
            var hash = new string('2', 32);
            for (var index = 0; index < SidebandClient.MaximumPendingCommands; index++)
                Assert.True((await client.SendCommandAsync(hash, "ping", cancellationToken: commands.Token)).Queued);
            Assert.Equal(SidebandCommandQueueStatus.QueueFull, (await client.SendCommandAsync(hash, "ping", cancellationToken: commands.Token)).Status);
            await commands.CancelAsync();
            await Task.WhenAll(client.DisposeAsync().AsTask(), client.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(10), token);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.SendCommandAsync(hash, "ping", cancellationToken: token));
            await using var reopened = new SidebandClient(new(Path.Combine(root, "data")));
            await reopened.ConnectAsync(rns, token);
            Assert.DoesNotContain((await reopened.LoadAsync(token)).Messages, message => message.DeliveryState == "Delivered");
            Assert.True((await reopened.SendCommandAsync(hash, "ping", cancellationToken: token)).Queued);
            await reopened.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.DoesNotContain((await reopened.LoadAsync(token)).Messages, message => message.DeliveryState == "Delivered");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
