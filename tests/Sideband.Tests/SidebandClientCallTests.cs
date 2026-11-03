using System.Net;
using System.Net.Sockets;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandClientCallTests
{
    [Fact]
    public async Task SharedClientIdentityCallsPersistAndDisconnectReleasesOwnedDevices()
    {
        var root = Path.Combine(Path.GetTempPath(), "sideband-client-call-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        try
        {
            var serverConfig = Path.Combine(root, "server-rns");
            var peerConfig = Path.Combine(root, "peer-rns");
            Directory.CreateDirectory(serverConfig);
            Directory.CreateDirectory(peerConfig);
            var common = "[reticulum]\n  enable_transport = no\n  share_instance = no\n\n[logging]\n  loglevel = 4\n\n[interfaces]\n";
            await File.WriteAllTextAsync(Path.Combine(serverConfig, "config"), common +
                $"  [[Calls]]\n    type = TCPServerInterface\n    enabled = yes\n    listen_ip = 127.0.0.1\n    listen_port = {port}\n", token);
            await File.WriteAllTextAsync(Path.Combine(peerConfig, "config"), common +
                $"  [[Calls]]\n    type = TCPClientInterface\n    enabled = yes\n    target_host = 127.0.0.1\n    target_port = {port}\n", token);
            var devices = new SidebandVoiceLifecycleTests.Devices();
            var peerDevices = new SidebandVoiceLifecycleTests.Devices();
            var store = new SidebandStateStore(Path.Combine(root, "server"));
            var peerStore = new SidebandStateStore(Path.Combine(root, "peer"));
            await store.ConfigureVoiceAsync(true, new(), token);
            await peerStore.ConfigureVoiceAsync(true, new(), token);
            await using var client = new SidebandClient(store, devices);
            await using var peer = new SidebandClient(peerStore, peerDevices);
            var observerErrors = new List<string>();
            client.ErrorReported += message => { lock (observerErrors) observerErrors.Add(message); };
            client.VoiceStateChanged += _ => throw new InvalidOperationException("injected observer failure");
            await client.ConnectAsync(serverConfig, token);
            await peer.ConnectAsync(peerConfig, token);
            Assert.Equal(0, devices.Captures);
            Assert.Equal(0, peerDevices.Captures);
            var own = client.ConnectionInfo!.IdentityHash;
            var peerIdentity = peer.ConnectionInfo!.IdentityHash;
            var dial = client.CallIdentityAsync(peerIdentity, cancellationToken: token);
            await SidebandVoiceLifecycleTests.UntilAsync(() => peer.VoiceSnapshot?.State == SidebandVoiceState.Ringing);
            Assert.Equal(0, peerDevices.Captures);
            await peer.AnswerCallAsync(token);
            await dial;
            await SidebandVoiceLifecycleTests.UntilAsync(() => devices.Captures == 1 && peerDevices.Captures == 1);
            Assert.Equal(SidebandVoiceState.InCall, client.VoiceSnapshot!.State);
            Assert.Equal(peerIdentity, client.VoiceSnapshot.RemoteIdentityHash);
            await client.HangupCallAsync(token);
            await SidebandVoiceLifecycleTests.UntilAsync(() =>
                client.VoiceSnapshot?.State == SidebandVoiceState.Available &&
                peer.VoiceSnapshot?.State == SidebandVoiceState.Available &&
                devices.CaptureDisposals == devices.Captures && peerDevices.CaptureDisposals == peerDevices.Captures);
            await client.DisconnectAsync();
            Assert.Null(client.VoiceSnapshot);
            Assert.Null(client.VoiceDestinationHash);
            Assert.NotEmpty((await new SidebandStateStore(store.DataDirectory).LoadAsync(cancellationToken: token)).Calls!);
            lock (observerErrors)
                Assert.Contains(observerErrors, message => message.Contains("voice observer failed", StringComparison.Ordinal));
            await client.ConnectAsync(serverConfig, token);
            Assert.Equal(own, client.ConnectionInfo!.IdentityHash);
            Assert.Equal(SidebandVoiceState.Available, client.VoiceSnapshot!.State);
            await client.ClearCallHistoryAsync(token);
            Assert.Empty((await store.LoadAsync(cancellationToken: token)).Calls!);
            await client.ConfigureVoiceAsync(false, new(), token);
            Assert.Null(client.VoiceSnapshot);
            Assert.True(client.IsConnected);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.CallIdentityAsync(peerIdentity, cancellationToken: token));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
