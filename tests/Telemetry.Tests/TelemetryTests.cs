using DotReticulum.Applications.Rnsd;
using DotReticulum.Applications;

namespace Telemetry.Tests;

public class TelemetryTests
{
    [Fact]
    public void Report_RoundTripsThroughMsgPack_AndRejectsUnknownVersions()
    {
        var (server, client, dir) = TestNodes.Pair();
        try
        {
            var report = TelemetryReport.Collect(7, server.Router, [("S", "TcpServerInterface")], dir);
            var parsed = TelemetryReport.TryUnpack(report.Pack());
            Assert.NotNull(parsed);
            Assert.Equal(7, parsed.Sequence);
            Assert.Equal(Environment.MachineName, parsed.Host);
            Assert.Contains("1 interface(s)", parsed.Format());

            Assert.Null(TelemetryReport.TryUnpack(DotReticulum.Core.MsgPack.Pack(new Dictionary<object, object?> { ["v"] = 99L })));
            Assert.Null(TelemetryReport.TryUnpack([0xc1]));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DaemonPublishes_ToReceiver_Directly()
    {
        var (server, client, dir) = TestNodes.Pair();
        try
        {
            await server.StartAsync();
            await client.StartAsync();
            var daemon = new TelemetryDaemon(server, server.LoadOrCreateIdentity("telemetry"));
            await Task.Delay(500);
            await daemon.AnnounceAsync();
            var reports = await Receive(client, daemon, 3);
            Assert.True(reports.Count >= 3);
            Assert.True(reports[^1].Sequence > reports[0].Sequence);
            Assert.All(reports, r => Assert.Equal(Environment.MachineName, r.Host));
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DaemonPublishes_ToReceiver_OverRnsdHop()
    {
        var port = TestNodes.FreePort();
        var dir = TestNodes.TempDir();
        var cts = new CancellationTokenSource();
        var hub = RnsdHost.Create(RnsdConfigParser.Parse($"""
            [reticulum]
              share_instance = no
              enable_transport = yes
            [interfaces]
              [[Hub]]
                type = TCPServerInterface
                enabled = yes
                listen_ip = 127.0.0.1
                listen_port = {port}
            """), Path.Combine(dir, "hub", "transport_identity"), (_, _) => { });
        ReticulumNode Edge(string name) => ReticulumNode.Create(RnsdConfigParser.Parse($"""
            [reticulum]
              share_instance = no
            [interfaces]
              [[Hub]]
                type = TCPClientInterface
                enabled = yes
                target_host = 127.0.0.1
                target_port = {port}
            """), Path.Combine(dir, name));

        var publisher = Edge("publisher");
        var viewer = Edge("viewer");
        Task run = Task.CompletedTask;
        try
        {
            await hub.StartAsync(cts.Token);
            run = hub.RunAsync(cts.Token);
            await publisher.StartAsync();
            await viewer.StartAsync();
            var daemon = new TelemetryDaemon(publisher, publisher.LoadOrCreateIdentity("telemetry"));
            await Task.Delay(1000);
            await daemon.AnnounceAsync();

            var reports = await Receive(viewer, daemon, 2);
            Assert.True(reports.Count >= 2);
            Assert.True(viewer.Router.HopsTo(daemon.DestinationHash) >= 1);
        }
        finally
        {
            await viewer.DisposeAsync();
            await publisher.DisposeAsync();
            await cts.CancelAsync();
            try { await run.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
            await hub.DisposeAsync();
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task<List<TelemetryReport>> Receive(ReticulumNode viewer, TelemetryDaemon daemon, int count)
    {
        var reports = new List<TelemetryReport>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = daemon.RunAsync(TimeSpan.FromMilliseconds(300), cts.Token);
        var failure = await TelemetryReceiver.WatchAsync(viewer, daemon.DestinationHash, TimeSpan.FromSeconds(30), report =>
        {
            bool complete;
            lock (reports)
            {
                reports.Add(report);
                complete = reports.Count == count;
            }
            if (complete) cts.Cancel();
        }, cts.Token);
        Assert.Null(failure);
        await run;
        lock (reports) return reports.ToList();
    }
}
