using System.Net;
using System.Net.Sockets;
using DotReticulum.Applications;
using DotReticulum.Applications.Rnsd;

namespace Telemetry.Tests;

internal static class TestNodes
{
    public static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    public static string TempDir() => Path.Combine(Path.GetTempPath(), "dottelemetry-" + Guid.NewGuid().ToString("N"));

    public static (ReticulumNode Server, ReticulumNode Client, string Dir) Pair()
    {
        var port = FreePort();
        var dir = TempDir();
        var server = ReticulumNode.Create(RnsdConfigParser.Parse($"""
            [reticulum]
              share_instance = no
              enable_transport = no
            [interfaces]
              [[S]]
                type = TCPServerInterface
                enabled = yes
                listen_ip = 127.0.0.1
                listen_port = {port}
            """), Path.Combine(dir, "server"));
        var client = ReticulumNode.Create(RnsdConfigParser.Parse($"""
            [reticulum]
              share_instance = no
            [interfaces]
              [[C]]
                type = TCPClientInterface
                enabled = yes
                target_host = 127.0.0.1
                target_port = {port}
            """), Path.Combine(dir, "client"));
        return (server, client, dir);
    }
}
