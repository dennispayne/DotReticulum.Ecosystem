using DotReticulum.Applications;
using DotReticulum.Core;
using NomadNet;

namespace NomadNet.Tests;

[Trait("Category", "Integration")]
public sealed class RrcLifecycleTests
{
    [Fact]
    public async Task NoSubscriberAndFailedObserversCannotPreventDurableCoreReception()
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomad-rrc-migration", "observers-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        try
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            foreach (var name in new[] { "hub", "client" })
            {
                var path = Path.Combine(directory, name);
                Directory.CreateDirectory(path);
                var network = name == "hub"
                    ? $"type=TCPServerInterface\nenabled=Yes\nlisten_ip=127.0.0.1\nlisten_port={port}\n"
                    : $"type=TCPClientInterface\nenabled=Yes\ntarget_host=127.0.0.1\ntarget_port={port}\n";
                await File.WriteAllTextAsync(Path.Combine(path, "config"),
                    "[reticulum]\nshare_instance=No\n[interfaces]\n[[Test]]\n" + network);
            }
            await using var hub = ReticulumNode.Open(Path.Combine(directory, "hub"));
            await using var node = ReticulumNode.Open(Path.Combine(directory, "client"));
            using var hubIdentity = Identity.Generate();
            using var identity = Identity.Generate();
            var accepted = new TaskCompletionSource<DotReticulum.Transport.Link>(TaskCreationOptions.RunContinuationsAsynchronously);
            var destination = new DotReticulum.Transport.LocalDestination(hubIdentity,
                Destination.Create(DestinationType.Single, "rrc", ["hub"], hubIdentity));
            destination.LinkEstablished = link =>
            {
                link.PacketReceived = (data, _) =>
                {
                    if (RrcEnvelope.Decode(data).Type == 1)
                        link.TrySend(RrcEnvelope.Create(2, hubIdentity.Hash, body: new Dictionary<long, object?>
                        { [0] = "test hub", [1] = "1" }).Encode());
                };
                accepted.TrySetResult(link);
            };
            hub.Router.RegisterLocalDestination(destination);
            await hub.StartAsync();
            await node.StartAsync();
            await using (var client = await RrcClient.OpenAsync(node, identity, directory,
                Convert.ToHexStringLower(destination.Destination.Hash)))
            {
                await hub.Router.AnnounceAsync(destination);
                await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(15));
                var link = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var reports = new System.Collections.Concurrent.ConcurrentQueue<string>();
                client.Log = reports.Enqueue;
                async Task SendAndWait(string text)
                {
                    Assert.True(link.TrySend(RrcEnvelope.Create(20, hubIdentity.Hash, "lobby", text, "Peer").Encode()));
                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while (!(await client.HistoryAsync()).Any(item => item.Text == text))
                    {
                        Assert.True(DateTime.UtcNow < deadline);
                        await Task.Delay(20);
                    }
                }
                await SendAndWait("without observer");
                var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                client.MessageReceived += _ => throw new InvalidOperationException("deliberate observer failure");
                client.MessageReceived += _ => observed.TrySetResult();
                await SendAndWait("after failure");
                await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Contains(reports, report => report.Contains("observer failed", StringComparison.Ordinal));
                await client.DisconnectAsync();
            }
            await using var reopened = await RrcClient.OpenAsync(node, identity, directory,
                Convert.ToHexStringLower(destination.Destination.Hash));
            Assert.Contains(await reopened.HistoryAsync(), item => item.Text == "without observer");
            Assert.Contains(await reopened.HistoryAsync(), item => item.Text == "after failure");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task DisposalCancelsAnInProgressPathLookupAndCanBeRepeated()
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomadnet-rrc-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "config"),
                "[reticulum]\n share_instance = No\n[interfaces]\n");
            await using var node = ReticulumNode.Open(directory);
            using var identity = Identity.Generate();
            await node.StartAsync();
            var client = await RrcClient.OpenAsync(node, identity, directory, new string('a', 32));
            var connect = client.ConnectAsync();
            await Task.Delay(30);
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
            await client.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ConnectAsync());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
