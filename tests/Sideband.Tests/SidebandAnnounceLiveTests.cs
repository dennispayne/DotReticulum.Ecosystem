using System.Net;
using System.Net.Sockets;
using DotReticulum.Applications;
using LXMF.Tests.Interop;

namespace Sideband.Tests;

public sealed partial class SidebandLiveInteropTests
{
    [Theory(SkipWhen = nameof(ApplicationInteropIsDisabled), Skip = "Announcement application interop requires DOTSIDEBAND_LIVE=1, DOTRETICULUM_LIVE=1 and pinned Python/RNS/LXMF/LXST/Sideband paths.")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [Trait("Category", "Live")]
    public async Task ActualPrimaryAnnouncementStreamsBothDirectionsEmptyReplacementDeleteAndRestart(int topology)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(4));
        var token = deadline.Token;
        var root = Path.Combine(AppContext.BaseDirectory, "announce-live-" + Guid.NewGuid().ToString("N"));
        var source = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_UPSTREAM")!;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        try
        {
            var config = Path.Combine(root, "native-rns");
            Directory.CreateDirectory(config);
            await File.WriteAllTextAsync(Path.Combine(config, "config"),
                $"[reticulum]\n share_instance = no\n enable_transport = no\n\n[logging]\n loglevel = 0\n\n[interfaces]\n [[Announces]]\n type = TCPClientInterface\n enabled = yes\n target_host = 127.0.0.1\n target_port = {port}\n", token);
            await using var pythonTransport = topology == 1
                ? await PyLinkNode.StartAsync("AnnounceTransport", tcpServerPort: port, harness: "sideband_live_node.py", extraArguments: ["--transport"])
                : null;
            var transportConfig = Path.Combine(root, "transport");
            Directory.CreateDirectory(transportConfig);
            await File.WriteAllTextAsync(Path.Combine(transportConfig, "config"),
                $"[reticulum]\n share_instance = no\n enable_transport = yes\n\n[logging]\n loglevel = 0\n\n[interfaces]\n [[Announces]]\n type = TCPServerInterface\n enabled = yes\n listen_ip = 127.0.0.1\n listen_port = {port}\n", token);
            await using var nativeTransport = topology == 2 ? ReticulumNode.Open(transportConfig) : null;
            if (nativeTransport is not null) await nativeTransport.StartAsync(token);
            await using var primary = await PyLinkNode.StartAsync("ActualAnnounceCore",
                tcpServerPort: topology == 0 ? port : 0, rnsdPort: topology == 0 ? 0 : port,
                harness: "sideband_application_node.py", extraArguments: ["--source", source]);
            var data = Path.Combine(root, "data");
            await using var client = new SidebandClient(new(data));
            await client.ConnectAsync(config, token);
            var local = client.ConnectionInfo!.DeliveryHash;
            await primary.WaitForAnnounce(Convert.FromHexString(local), TimeSpan.FromSeconds(30));
            await CheckPrimaryRecord();
            await primary.Command("announce", "announced", TimeSpan.FromSeconds(10));
            var first = await WaitNative(primary.HashHex, item => item.Name == "Anonymous Peer");
            Assert.False((await client.LoadAsync(token)).Contacts.Single(contact => contact.DeliveryHash == primary.HashHex).ConversationKnown);
            Assert.Empty((await client.LoadAsync(token)).Messages);
            // No state subscriber is installed for initial ingress; a failing observer is added for later ingress.
            var failures = 0;
            client.StateChanged += _ => throw new InvalidOperationException("Synthetic announcement observer");
            client.ErrorReported += _ => throw new InvalidOperationException("Synthetic announcement error observer");
            client.ErrorReported += message =>
            {
                if (message.Contains("Synthetic announcement observer", StringComparison.Ordinal)) Interlocked.Increment(ref failures);
            };
            // RNS path freshness uses whole-second emission timestamps; same-second replacements need not dispatch.
            await Task.Delay(TimeSpan.FromMilliseconds(1_100), token);
            await primary.Command("announce-empty", "announced-empty", TimeSpan.FromSeconds(10));
            var empty = await WaitNative(primary.HashHex, item => item.ReceivedAt > first.ReceivedAt && item.Name is null);
            Assert.Equal(1, (await client.GetAnnouncesAsync(token)).Count(item => item.DestinationHash == primary.HashHex));
            var pn = await primary.Command("enable-announcement-node", "announce-node", TimeSpan.FromSeconds(30));
            var nodeHash = pn.GetProperty("dest").GetString()!;
            var node = await WaitNative(nodeHash, item => item.DestinationType == SidebandAnnounce.Propagation);
            Assert.Equal("lxmd 1.2.1", node.NodeVersion);
            Assert.Equal(16, node.StampCost);
            Assert.True(node.NodeEnabled);
            await client.DeleteAnnounceAsync(primary.HashHex, token);
            Assert.DoesNotContain(await client.GetAnnouncesAsync(token), item => item.DestinationHash == primary.HashHex);
            Assert.False((await client.LoadAsync(token)).Contacts.Single(contact => contact.DeliveryHash == primary.HashHex).ConversationKnown);
            await Task.Delay(TimeSpan.FromMilliseconds(1_100), token);
            await primary.Command("announce", "announced", TimeSpan.FromSeconds(10));
            await WaitNative(primary.HashHex, item => item.Name == "Anonymous Peer" && item.ReceivedAt > empty.ReceivedAt);
            await primary.Command("delete-announce " + local, "announce-deleted", TimeSpan.FromSeconds(10));
            await client.AnnounceAsync(token);
            await CheckPrimaryRecord();
            await client.DisconnectAsync().WaitAsync(token);
            var persisted = await new SidebandStateStore(data).LoadAsync(cancellationToken: token);
            Assert.Contains(persisted.Announces!, item => item.DestinationHash == nodeHash);
            Assert.Contains(persisted.Announces!, item => item.DestinationHash == primary.HashHex && item.Name == "Anonymous Peer");
            Assert.All(persisted.Contacts, contact => Assert.False(contact.Trusted || contact.Unread || contact.ConversationKnown));
            Assert.True(Volatile.Read(ref failures) > 0);
            await client.ConnectAsync(config, token);
            Assert.Equal(local, client.ConnectionInfo!.DeliveryHash);
            Assert.Contains(await client.GetAnnouncesAsync(token), item => item.DestinationHash == primary.HashHex);

            async Task<SidebandAnnounce> WaitNative(string hash, Func<SidebandAnnounce, bool> predicate)
            {
                var until = DateTime.UtcNow.AddSeconds(35);
                while (DateTime.UtcNow < until)
                {
                    if ((await client.GetAnnouncesAsync(token)).SingleOrDefault(item => item.DestinationHash == hash) is { } item && predicate(item))
                        return item;
                    await Task.Delay(50, token);
                }
                throw new TimeoutException("Native announcement stream did not contain the asserted primary record: " + hash + "\n" + primary.Log);
            }

            async Task CheckPrimaryRecord()
            {
                var until = DateTime.UtcNow.AddSeconds(35);
                while (DateTime.UtcNow < until)
                {
                    var snapshot = await primary.Command("announces", "announces", TimeSpan.FromSeconds(10));
                    var entries = snapshot.GetProperty("records").EnumerateArray().Where(item => item.GetProperty("dest").GetString() == local).ToArray();
                    if (entries.Length == 1)
                    {
                        Assert.Equal("Sideband", entries[0].GetProperty("name").GetString());
                        Assert.Equal(SidebandAnnounce.Delivery, entries[0].GetProperty("type").GetString());
                        return;
                    }
                    await Task.Delay(50, token);
                }
                throw new TimeoutException("Actual primary list_announces did not expose the native announcement.\n" + primary.Log);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
