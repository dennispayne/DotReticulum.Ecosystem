using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotLXMF.Tests.Interop;
using DotReticulum.Transport;

namespace DotLXMF.Tests;

/// <summary>.NET LxmRouter against the real upstream Python LXMF 1.2.1 LXMRouter (tests/Interop/live_lxmf_node.py). DOTRETICULUM_LIVE=1.</summary>
public class LiveLxmfTests
{
    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly PacketInterfaceManager _manager;
        private readonly Task _run;
        public PyLinkNode Py = null!;
        public TransportRouter Router { get; }
        public Identity Identity { get; } = Identity.Generate();
        public LxmRouter Lxmf { get; }
        public List<ReceivedMessage> Inbox { get; } = [];

        private Rig(int port)
        {
            _manager = new PacketInterfaceManager([new TcpClientPacketInterface("127.0.0.1", port)]);
            Router = new TransportRouter(_manager, new TransportRouterOptions());
            Lxmf = new LxmRouter(Router, Identity, "DotNetLxmf", new LxmRouterOptions { ProcessingInterval = TimeSpan.FromMilliseconds(500) });
            Lxmf.MessageReceived = m => { lock (Inbox) Inbox.Add(m); };
            _run = Task.Run(() => Router.RunAsync(_cts.Token));
        }

        public static async Task<Rig> StartAsync()
        {
            var port = FreePort();
            var py = await PyLinkNode.StartAsync("lxmf", tcpServerPort: port, harness: "live_lxmf_node.py");
            var rig = new Rig(port) { Py = py };
            await rig._manager.StartAsync(rig._cts.Token);
            rig.Lxmf.Start();
            await Task.Delay(1000);
            await rig.Lxmf.AnnounceAsync();
            await py.Command("announce", "announced", TimeSpan.FromSeconds(10));
            var seen = await py.WaitForAnnounce(rig.Lxmf.DeliveryHash, TimeSpan.FromSeconds(20));
            Assert.Equal("DotNetLxmf", seen.GetProperty("display_name").GetString());
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!rig.Router.HasPath(py.Hash))
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("No path to the Python LXMF node.");
                await Task.Delay(50);
            }

            return rig;
        }

        public async ValueTask DisposeAsync()
        {
            await Lxmf.DisposeAsync();
            await _cts.CancelAsync();
            try { await _run.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
            await _manager.DisposeAsync();
            await Py.DisposeAsync();
        }
    }

    private static string HexContent(int size, int seed) => Convert.ToHexStringLower(new Random(seed).GetItems(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(), size / 2));

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static async Task DotNetToPython(LxmMethod method, int size)
    {
        await using var rig = await Rig.StartAsync();
        var content = HexContent(size, 7);
        var fields = new Dictionary<object, object?> { [1L] = new object?[] { "net-field" } };
        var message = LxMessage.Create(rig.Py.Hash, rig.Identity, content, "from dotnet", fields, method);
        await rig.Lxmf.SendAsync(message);
        var received = await rig.Py.NextAsync("received", 0, TimeSpan.FromSeconds(90));
        Assert.Equal("from dotnet", received.GetProperty("title").GetString());
        Assert.Equal(Sha(System.Text.Encoding.UTF8.GetBytes(content)), received.GetProperty("sha256").GetString());
        Assert.True(received.GetProperty("signature_validated").GetBoolean());
        Assert.Equal(Convert.ToHexStringLower(message.Hash!), received.GetProperty("hash").GetString());
        Assert.Equal(LxmState.Delivered, await message.Completion.WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.Equal(method == LxmMethod.Opportunistic ? LxmMethod.Opportunistic : LxmMethod.Direct, message.Method);
    }

    private static async Task PythonToDotNet(string method, int size)
    {
        await using var rig = await Rig.StartAsync();
        var queued = await rig.Py.Command($"send {rig.Lxmf.DeliveryHashHex} {method} {size} seed1 from-python", "sent_queued", TimeSpan.FromSeconds(30));
        await WaitFor(() => { lock (rig.Inbox) return rig.Inbox.Count == 1; }, 90);
        var m = rig.Inbox[0].Message;
        Assert.Equal("from-python", m.Title);
        Assert.Equal(size, m.ContentBytes.Length);
        Assert.Equal(queued.GetProperty("sha256").GetString(), Sha(m.ContentBytes));
        Assert.True(m.SignatureValidated);
        Assert.Equal(queued.GetProperty("hash").GetString(), Convert.ToHexStringLower(m.Hash!));
        await rig.Py.NextAsync("delivered", 0, TimeSpan.FromSeconds(60));
    }

    private static async Task WaitFor(Func<bool> condition, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(50);
        }
    }

    [LiveFact]
    public Task DotNet_ToPython_Opportunistic() => DotNetToPython(LxmMethod.Opportunistic, 120);

    [LiveFact]
    public Task DotNet_ToPython_DirectLink_SinglePacket() => DotNetToPython(LxmMethod.Direct, 200);

    [LiveFact]
    public Task DotNet_ToPython_DirectLink_LargeResource() => DotNetToPython(LxmMethod.Direct, 600_000);

    [LiveFact]
    public Task Python_ToDotNet_Opportunistic() => PythonToDotNet("opportunistic", 120);

    [LiveFact]
    public Task Python_ToDotNet_DirectLink_SinglePacket() => PythonToDotNet("direct", 200);

    [LiveFact]
    public Task Python_ToDotNet_DirectLink_LargeResource() => PythonToDotNet("direct", 600_000);
}