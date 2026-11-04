using System.Net;
using System.Net.Sockets;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;
using LXST;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandClientCallInteropTests
{
    [SidebandVoiceLiveTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Trait("Category", "Live")]
    public async Task SharedApplicationIdentityPolicyTrafficHistoryAndRestart(bool pythonOriginates, bool transportHop)
    {
        var root = Path.Combine(Path.GetTempPath(), "sideband-client-interop-" + Guid.NewGuid().ToString("N"));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(100));
        var token = stop.Token;
        var port = FreePort();
        var relayPort = transportHop ? FreePort() : 0;
        await using var interfaces = transportHop ? new PacketInterfaceManager(
            [new TcpServerPacketInterface(new IPEndPoint(IPAddress.Loopback, relayPort), perClientInterfaces: true)]) : null;
        await using var relay = interfaces is null ? null : new TransportRouter(interfaces, new() { TransportEnabled = true });
        if (interfaces is not null) await interfaces.StartAsync(token);
        var relayRun = relay?.RunAsync(token) ?? Task.CompletedTask;
        try
        {
            await using var python = await SidebandVoiceLiveTests.PythonVoice.StartAsync(port, relayPort, token);
            var config = Path.Combine(root, "rns");
            Directory.CreateDirectory(config);
            await File.WriteAllTextAsync(Path.Combine(config, "config"),
                "[reticulum]\n  enable_transport = no\n  share_instance = no\n\n[logging]\n  loglevel = 0\n\n[interfaces]\n" +
                $"  [[Calls]]\n    type = TCPClientInterface\n    enabled = yes\n    target_host = 127.0.0.1\n    target_port = {(transportHop ? relayPort : port)}\n", token);
            var store = new SidebandStateStore(Path.Combine(root, "profile"));
            await store.ConfigureVoiceAsync(true, new() { TrustedOnly = true, DirectPushToTalk = true }, token);
            await using var client = new SidebandClient(store, new HeadlessAudioDeviceBackend());
            await client.ConnectAsync(config, token);
            var identity = client.ConnectionInfo!.IdentityHash;
            var delivery = python.Ready.GetProperty("lxmf_dest").GetString()!;
            await client.AddContactAsync("Reference", delivery, token);
            await client.SetConversationTrustAsync(delivery, true, token);
            await client.SetConversationPushToTalkAsync(delivery, true, token);
            if (!transportHop) await python.CommandAsync("wait-client", "client-ready", token);
            await python.CommandAsync("announce", "announced", token);
            await client.AnnounceVoiceAsync(token);
            await python.CommandAsync($"trust {identity} true true", "configured", token);
            await python.CommandAsync("policy true true", "configured", token);
            await python.CommandAsync($"wait-path {identity}", "path-ready", token);
            if (pythonOriginates)
                await python.CommandAsync($"call {identity} 64 2", "calling", token);
            else
                await client.CallContactAsync(delivery, mode: TelephoneMode.HalfDuplex, cancellationToken: token);
            await UntilAsync(() => client.VoiceSnapshot?.State == SidebandVoiceState.InCall, token);
            await python.WaitStateAsync(3, token);
            client.SetPushToTalk(true);
            await python.CommandAsync("ptt on", "configured", token);
            await UntilAsync(() => client.VoiceSnapshot is { ReceivedFrames: >= 5, TransmittedFrames: >= 5 }, token);
            await python.WaitTrafficAsync(token);
            await client.SetVoiceProfileAsync(TelephoneProfile.LowBandwidth, token);
            var baseline = client.VoiceSnapshot!;
            await python.WaitProfileAsync(48, token);
            await UntilAsync(() => client.VoiceSnapshot!.ReceivedFrames >= baseline.ReceivedFrames + 3 &&
                client.VoiceSnapshot.TransmittedFrames >= baseline.TransmittedFrames + 3, token);
            await client.HangupCallAsync(token);
            await python.WaitStateAsync(0, token);
            await client.DisconnectAsync();
            var restored = await new SidebandStateStore(store.DataDirectory).LoadAsync(cancellationToken: token);
            Assert.Contains(restored.Calls!, call => call.Event == (pythonOriginates ? "incoming-success" : "outgoing-success"));
            Assert.Contains(restored.Calls!, call => call.Event == "ongoing-ended");
            Assert.True(Assert.Single(restored.Contacts).DirectPushToTalk);
            await client.ConnectAsync(config, token);
            Assert.Equal(identity, client.ConnectionInfo!.IdentityHash);
            Assert.Equal(SidebandVoiceState.Available, client.VoiceSnapshot!.State);
        }
        finally
        {
            await stop.CancelAsync();
            try { await relayRun; } catch (OperationCanceledException) { }
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [SidebandVoiceLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Live")]
    public async Task VoiceOnlyNameTrustDeletionDiscoveryAndDeliveryOverrideKeepActualCallsAvailable(bool transportHop)
    {
        var root = Path.Combine(Path.GetTempPath(), "sideband-voice-peer-live-" + Guid.NewGuid().ToString("N"));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(150));
        var token = stop.Token;
        var port = FreePort();
        var relayPort = transportHop ? FreePort() : 0;
        await using var interfaces = transportHop ? new PacketInterfaceManager(
            [new TcpServerPacketInterface(new IPEndPoint(IPAddress.Loopback, relayPort), perClientInterfaces: true)]) : null;
        await using var relay = interfaces is null ? null : new TransportRouter(interfaces, new() { TransportEnabled = true });
        if (interfaces is not null) await interfaces.StartAsync(token);
        var relayRun = relay?.RunAsync(token) ?? Task.CompletedTask;
        try
        {
            await using var python = await SidebandVoiceLiveTests.PythonVoice.StartAsync(port, relayPort, token);
            var config = Path.Combine(root, "rns");
            Directory.CreateDirectory(config);
            await File.WriteAllTextAsync(Path.Combine(config, "config"),
                "[reticulum]\n  enable_transport = no\n  share_instance = no\n\n[logging]\n  loglevel = 0\n\n[interfaces]\n" +
                $"  [[Calls]]\n    type = TCPClientInterface\n    enabled = yes\n    target_host = 127.0.0.1\n    target_port = {(transportHop ? relayPort : port)}\n", token);
            var store = new SidebandStateStore(Path.Combine(root, "profile"));
            await store.ConfigureVoiceAsync(true, new() { TrustedOnly = true }, token);
            await using var client = new SidebandClient(store, new HeadlessAudioDeviceBackend());
            await client.ConnectAsync(config, token);
            var identity = client.ConnectionInfo!.IdentityHash;
            var peer = python.Identity.HexHash;
            var delivery = python.Ready.GetProperty("lxmf_dest").GetString()!;
            if (!transportHop) await python.CommandAsync("wait-client", "client-ready", token);
            await python.CommandAsync("announce", "announced", token);
            await client.AnnounceVoiceAsync(token);
            await python.CommandAsync($"trust {identity} true true", "configured", token);
            await python.CommandAsync("policy true false", "configured", token);
            await python.CommandAsync($"wait-path {identity}", "path-ready", token);
            await python.CommandAsync("announce-delivery", "delivery-announced", token);
            while (!(await store.LoadAsync(cancellationToken: token)).Contacts
                .Any(contact => contact.DeliveryHash == delivery && !contact.ConversationKnown))
                await Task.Delay(20, token);
            await client.ConfigureVoicePeerAsync(peer, "Saved reference voice", true, token);

            async Task AcceptActualCallAsync()
            {
                await python.CommandAsync($"call {identity} 64 1", "calling", token);
                await UntilAsync(() => client.VoiceSnapshot?.State == SidebandVoiceState.Ringing, token);
                await client.AnswerCallAsync(token);
                await UntilAsync(() => client.VoiceSnapshot is { State: SidebandVoiceState.InCall, ReceivedFrames: >= 5, TransmittedFrames: >= 5 }, token);
                await python.WaitStateAsync(3, token);
                await python.WaitTrafficAsync(token);
                await client.HangupCallAsync(token);
                await python.WaitStateAsync(0, token);
            }

            async Task RejectActualCallAsync(int priorFailures)
            {
                await python.CommandAsync($"call {identity} 64 1", "calling", token);
                await python.WaitStateAsync(0, token);
                var history = await python.CommandAsync("history", "history", token);
                Assert.Equal(priorFailures + 1, history.GetProperty("entries").EnumerateArray()
                    .Count(entry => entry.GetProperty("event").GetString() == "outgoing-failure"));
                Assert.Equal(SidebandVoiceState.Available, client.VoiceSnapshot!.State);
            }

            await AcceptActualCallAsync();
            await client.DeleteVoicePeerAsync(peer, token);
            await RejectActualCallAsync(0);
            await client.ConfigureVoicePeerAsync(peer, "Restored reference voice", true, token);
            await AcceptActualCallAsync();
            await client.AddContactAsync("Explicit untrusted delivery", delivery, token);
            await client.SetConversationTrustAsync(delivery, false, token);
            Assert.True((await client.LoadAsync(token)).Contacts.Single(contact => contact.DeliveryHash == delivery).ConversationKnown);
            await RejectActualCallAsync(1);
            await client.SetConversationTrustAsync(delivery, true, token);
            await AcceptActualCallAsync();
            await client.DisconnectAsync();
            var restored = await new SidebandStateStore(store.DataDirectory).LoadAsync(cancellationToken: token);
            Assert.Equal("Restored reference voice", Assert.Single(restored.VoicePeers!).DisplayName);
            Assert.True(restored.VoicePeers![0].VoiceTrusted);
            Assert.Contains(restored.Calls!, call => call.Event == "incoming-success" && !call.Imported);
            await client.ConnectAsync(config, token);
            Assert.Equal(identity, client.ConnectionInfo!.IdentityHash);
            Assert.Equal(SidebandVoiceState.Available, client.VoiceSnapshot!.State);
        }
        finally
        {
            await stop.CancelAsync();
            try { await relayRun; } catch (OperationCanceledException) { }
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task UntilAsync(Func<bool> predicate, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        while (!predicate()) await Task.Delay(20, deadline.Token);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
