using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;

namespace DotLXST.Tests;

public sealed class LxstLiveFactAttribute : FactAttribute
{
    public LxstLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DOTRETICULUM_LIVE") != "1" ||
            Environment.GetEnvironmentVariable("DOTLXST_LIVE") != "1")
            Skip = "Set DOTRETICULUM_LIVE=1 and DOTLXST_LIVE=1 with pinned Python RNS/LXST checkouts and native codecs.";
    }
}

public sealed class LxstLiveTheoryAttribute : TheoryAttribute
{
    public LxstLiveTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("DOTRETICULUM_LIVE") != "1" ||
            Environment.GetEnvironmentVariable("DOTLXST_LIVE") != "1")
            Skip = "Set DOTRETICULUM_LIVE=1 and DOTLXST_LIVE=1 with pinned Python RNS/LXST checkouts and native codecs.";
    }
}

[Trait("Category", "Integration")]
public sealed class LiveStreamTests
{
    [LxstLiveTheory]
    [InlineData("direct")]
    [InlineData("dotnet-hop")]
    [InlineData("python-hop")]
    [Trait("Category", "Live")]
    public async Task RealPythonPacketizerAndLinkSourceExchangeAllCodecsAndSignals(string topology)
    {
        var port = FreePort();
        var relayPort = topology == "direct" ? 0 : FreePort();
        using var stop = new CancellationTokenSource();
        await using var relayManager = topology == "dotnet-hop" ? new PacketInterfaceManager(
            [new TcpServerPacketInterface(new IPEndPoint(IPAddress.Loopback, relayPort), perClientInterfaces: true)]) : null;
        await using var relayRouter = relayManager is null ? null : new TransportRouter(relayManager,
            new TransportRouterOptions { TransportEnabled = true });
        if (relayManager is not null) await relayManager.StartAsync(stop.Token);
        var relayDispatch = relayRouter?.RunAsync(stop.Token) ?? Task.CompletedTask;
        await using var relayPython = topology == "python-hop" ? await LxstPythonNode.StartAsync(relayPort, transport: true) : null;
        await using var python = await LxstPythonNode.StartAsync(port, connectPort: relayPort);
        var impaired = new ImpairedPacketInterface(new TcpClientPacketInterface("127.0.0.1", relayPort == 0 ? port : relayPort));
        await using var manager = new PacketInterfaceManager([impaired]);
        await using var router = new TransportRouter(manager, new TransportRouterOptions());
        await manager.StartAsync(stop.Token);
        var dispatch = router.RunAsync(stop.Token);
        try
        {
            if (relayPort == 0) await python.CommandAsync("wait-client", "client-ready");
            await python.CommandAsync("announce", "announced");
            await WaitForAsync(() => router.HasPath(python.Hash));
            if (relayPort != 0) Assert.True(router.HopsTo(python.Hash) >= 2, "The announced path did not traverse the configured transport hop.");
            var active = new TaskCompletionSource<Link>(TaskCreationOptions.RunContinuationsAsynchronously);
            var link = router.Links.Connect(python.Identity, python.Hash, established: value => active.TrySetResult(value));
            using var transport = new ReticulumStreamTransport(link);
            await using var session = new StreamSession(transport, new NativeAudioCodecFactory());
            var inbox = new ConcurrentQueue<AudioFrame>();
            var signals = new ConcurrentQueue<long>();
            var errors = new ConcurrentQueue<string>();
            session.FrameReceived += inbox.Enqueue;
            session.SignalReceived += signals.Enqueue;
            session.Error = (context, error) => errors.Enqueue(context + " " + error?.Message);
            session.Start();
            await active.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await python.WaitAsync("linked");
            await session.SendSignalAsync(383);
            var observed = await python.WaitAsync("signals");
            Assert.Equal(383, observed.GetProperty("values")[0].GetInt32());
            await python.CommandAsync("signal 319", "sent");
            await WaitForAsync(() => signals.Contains(319));

            var samples = Enumerable.Range(0, 32).SelectMany(i => new[] { i / 128f, -i / 128f }).ToArray();
            using var raw = new RawAudioCodec(32, 2);
            await session.SendFrameAsync(new(CodecKind.Raw, raw.Encode(new(samples, 48000, 2))));
            var received = await python.WaitAsync("frame");
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes(samples.AsSpan()))),
                received.GetProperty("sha256").GetString());
            using var opus = new NativeOpusCodec();
            await session.SendFrameAsync(new(CodecKind.Opus, opus.Encode(new(new float[160], 8000, 1))));
            await python.WaitAsync("frame", 1);
            using var codec2 = new NativeCodec2();
            await session.SendFrameAsync(new(CodecKind.Codec2, codec2.Encode(new(new float[320], 8000, 1))));
            await python.WaitAsync("frame", 2);

            foreach (var kind in new[] { "raw", "opus", "codec2", "raw" })
                await python.CommandAsync($"send {kind} 3", "sent");
            await WaitForAsync(() => inbox.Count == 12);
            Assert.Contains(inbox, frame => frame.SampleRate == 8000);
            Assert.Contains(inbox, frame => frame.SampleRate == 48000);
            Assert.Empty(errors);

            var beforePython = python.Count("frame");
            var beforeDotNet = inbox.Count;
            impaired.Enabled = true;
            for (var i = 0; i < 10; i++)
                await session.SendFrameAsync(new(CodecKind.Raw, raw.Encode(new(samples, 48000, 2))));
            await python.WaitAsync("frame", beforePython + 7);
            await python.CommandAsync("send raw 10", "sent");
            await WaitForAsync(() => inbox.Count == beforeDotNet + 8);
            await Task.Delay(150);
            Assert.Equal(2, impaired.OutboundDrops);
            Assert.Equal(2, impaired.InboundDrops);
            Assert.Equal(beforePython + 8, python.Count("frame"));
            Assert.Equal(beforeDotNet + 8, inbox.Count);
            impaired.Enabled = false;
            await python.CommandAsync("send raw", "sent");
            await WaitForAsync(() => inbox.Count == beforeDotNet + 9);
            link.Teardown();
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await stop.CancelAsync();
            try { await dispatch; }
            catch (OperationCanceledException) { }
            try { await relayDispatch; }
            catch (OperationCanceledException) { }
        }
    }

    internal static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    internal static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition()) await Task.Delay(25, timeout.Token);
    }
}
