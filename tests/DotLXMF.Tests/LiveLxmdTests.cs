using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;

namespace DotReticulum.Lxmf.Tests;

/// <summary>.NET LxmRouter delivering to a real upstream <c>lxmd</c> (LXMF 1.2.1, LXMF.Utilities.lxmd) delivery destination. DOTRETICULUM_LIVE=1.</summary>
public class LiveLxmdTests
{
    [LiveFact]
    public async Task DotNet_DeliversToLxmd_Opportunistic_Direct_And_LargeResource()
    {
        int port;
        using (var l = new TcpListener(IPAddress.Loopback, 0))
        {
            l.Start();
            port = ((IPEndPoint)l.LocalEndpoint).Port;
        }

        var root = Path.Combine(AppContext.BaseDirectory, "live-lxmd-" + Guid.NewGuid().ToString("N"));
        var lxmdDir = Path.Combine(root, "lxmd");
        var rnsDir = Path.Combine(root, "rns");
        Directory.CreateDirectory(lxmdDir);
        Directory.CreateDirectory(rnsDir);
        await File.WriteAllTextAsync(Path.Combine(rnsDir, "config"),
            $"[reticulum]\n  enable_transport = No\n  share_instance = No\n  instance_name = dotreticulum_lxmd\n\n[logging]\n  loglevel = 4\n\n[interfaces]\n  [[Server]]\n    type = TCPServerInterface\n    enabled = yes\n    listen_ip = 127.0.0.1\n    listen_port = {port}\n");
        await File.WriteAllTextAsync(Path.Combine(lxmdDir, "config"),
            "[lxmf]\ndisplay_name = LxmdLive\nannounce_at_start = yes\ndelivery_transfer_max_accepted_size = 1000\n\n[propagation]\nenable_node = no\n\n[logging]\nloglevel = 4\n");

        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTRETICULUM_PYTHON") ?? "python")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "-m", "LXMF.Utilities.lxmd", "--config", lxmdDir, "--rnsconfig", rnsDir, "-v" }) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONPATH"] = Environment.GetEnvironmentVariable("DOTRETICULUM_UPSTREAM")!;
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        var log = new StringBuilder();
        using var lxmd = Process.Start(psi)!;
        lxmd.OutputDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
        lxmd.ErrorDataReceived += (_, e) => { lock (log) log.AppendLine("[stderr] " + e.Data); };
        lxmd.BeginOutputReadLine();
        lxmd.BeginErrorReadLine();

        using var cts = new CancellationTokenSource();
        var manager = new PacketInterfaceManager([new TcpClientPacketInterface("127.0.0.1", port)]);
        var router = new TransportRouter(manager, new TransportRouterOptions());
        var identity = Identity.Generate();
        var lxmf = new LxmRouter(router, identity, "DotNetToLxmd", new LxmRouterOptions { ProcessingInterval = TimeSpan.FromMilliseconds(500) });
        Task? run = null;
        try
        {
            var identityPath = Path.Combine(lxmdDir, "identity");
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(identityPath))
            {
                Assert.True(DateTime.UtcNow < deadline, "lxmd did not create its identity. Log:\n" + log);
                await Task.Delay(100);
            }

            await Task.Delay(500);
            var lxmdDest = LxMessage.DeliveryDestinationHash(IdentityFile.Load(identityPath));
            await manager.StartAsync(cts.Token);
            run = Task.Run(() => router.RunAsync(cts.Token));
            lxmf.Start();
            await lxmf.AnnounceAsync();

            // lxmd announces ~10 s after start; wait for it so the identity is known.
            deadline = DateTime.UtcNow.AddSeconds(60);
            while (router.Engine.Known.Recall(lxmdDest) is null)
            {
                Assert.True(DateTime.UtcNow < deadline, "No lxmd announce received. Log:\n" + log);
                await Task.Delay(200);
            }

            var messagesDir = Path.Combine(lxmdDir, "storage", "messages");
            foreach (var (method, size) in new[] { (LxmMethod.Opportunistic, 100), (LxmMethod.Direct, 200), (LxmMethod.Direct, 200_000) })
            {
                var content = size < 1000 ? new string('q', size) : Convert.ToHexStringLower(new Random(3).GetItems((byte[])[..Enumerable.Range(0, 256).Select(i => (byte)i)], size / 2));
                var message = LxMessage.Create(lxmdDest, identity, content, "to lxmd", method: method);
                await lxmf.SendAsync(message);
                var state = await message.Completion.WaitAsync(TimeSpan.FromSeconds(90));
                Assert.True(state == LxmState.Delivered, $"{method} {size}: {state}. lxmd log:\n{log}");
                var file = Path.Combine(messagesDir, Convert.ToHexStringLower(message.Hash!));
                deadline = DateTime.UtcNow.AddSeconds(30);
                while (!File.Exists(file))
                {
                    Assert.True(DateTime.UtcNow < deadline, $"lxmd did not store {file}. Log:\n{log}");
                    await Task.Delay(100);
                }

                var stored = await File.ReadAllBytesAsync(file);
                Assert.True(stored.AsSpan().IndexOf(Encoding.UTF8.GetBytes(content.AsSpan(0, Math.Min(size, 100)).ToString())) >= 0);
                Assert.True(stored.AsSpan().IndexOf(Encoding.UTF8.GetBytes("to lxmd")) >= 0);
            }
        }
        finally
        {
            await lxmf.DisposeAsync();
            await cts.CancelAsync();
            if (run is not null)
            {
                try { await run.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
            }

            await manager.DisposeAsync();
            if (!lxmd.HasExited) lxmd.Kill(entireProcessTree: true);
            lxmd.WaitForExit(10_000);
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }
}
