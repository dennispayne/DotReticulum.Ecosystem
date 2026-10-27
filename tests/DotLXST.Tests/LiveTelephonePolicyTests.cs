using System.Collections.Concurrent;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;

namespace DotLXST.Tests;

[Trait("Category", "Integration")]
public sealed class LiveTelephonePolicyTests
{
    [VirtualAudioTheory]
    [InlineData("remote-deny")]
    [InlineData("local-deny")]
    [InlineData("remote-reject")]
    [InlineData("remote-timeout")]
    [InlineData("local-reject")]
    [InlineData("local-timeout")]
    [Trait("Category", "Live")]
    public async Task ActualTelephonePoliciesRejectionTimeoutAndSubsequentCallRecover(string mode)
    {
        await using var pulse = await VirtualPulseServer.StartAsync();
        var port = LiveStreamTests.FreePort();
        await using var python = await LxstPythonNode.StartAsync(
            port, telephone: true, pulseServer: pulse.Address);
        // Native Python's busy tone duration is deliberately disabled in this
        // policy test; audio/tone acceptance is covered separately.
        await python.CommandAsync("busy-tone 0", "configured");
        if (mode.StartsWith("remote-", StringComparison.Ordinal))
        {
            await python.CommandAsync("auto-answer 0", "configured");
            if (mode == "remote-deny") await python.CommandAsync("allowed none", "configured");
        }
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var manager = new PacketInterfaceManager([new TcpClientPacketInterface("127.0.0.1", port)]);
        await using var router = new TransportRouter(manager, new());
        using var identity = Identity.Generate();
        await using var phone = new TelephoneEndpoint(router, identity, new()
        {
            CallerPolicy = mode == "local-deny" ? new(allowedIdentityHashes: []) : new(),
            RingTimeout = mode == "local-timeout" ? TimeSpan.FromMilliseconds(150) : TimeSpan.FromSeconds(5),
            WaitTimeout = mode == "remote-timeout" ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(10),
        });
        var states = new ConcurrentQueue<CallState>();
        phone.StateChanged += states.Enqueue;
        await manager.StartAsync(stop.Token);
        var run = router.RunAsync(stop.Token);
        phone.Start();
        try
        {
            await python.CommandAsync("wait-client", "client-ready");
            await python.CommandAsync("announce", "announced");
            await phone.AnnounceAsync(stop.Token);
            await LiveStreamTests.WaitForAsync(() => router.HasPath(python.Hash));
            if (mode.StartsWith("remote-", StringComparison.Ordinal))
            {
                await phone.CallAsync(python.Identity, cancellationToken: stop.Token);
                if (mode is "remote-reject" or "remote-timeout")
                {
                    await python.WaitAsync("ringing");
                    if (mode == "remote-reject") await python.CommandAsync("hangup", "hungup");
                }
                if (mode == "remote-timeout")
                {
                    await python.WaitAsync("ended");
                    await LiveStreamTests.WaitForAsync(() => phone.State == CallState.Available);
                    Assert.DoesNotContain(CallState.Rejected, states);
                    Assert.DoesNotContain(CallState.Busy, states);
                }
                else
                {
                    var expected = mode == "remote-deny" ? CallState.Busy : CallState.Rejected;
                    await LiveStreamTests.WaitForAsync(() => states.Contains(expected) && phone.State == CallState.Available);
                }
            }
            else
            {
                await python.CommandAsync("call " + Convert.ToHexStringLower(phone.DestinationHash), "calling");
                if (mode != "local-deny")
                {
                    await LiveStreamTests.WaitForAsync(() => phone.State == CallState.Ringing);
                    if (mode == "local-reject") phone.Hangup();
                }
                await python.WaitAsync(mode switch { "local-deny" => "busy", "local-reject" => "rejected", _ => "ended" });
                await LiveStreamTests.WaitForAsync(() => phone.State == CallState.Available);
            }
            // Reverse direction is permitted even after denying an incoming
            // caller, and demonstrates continued service on the same endpoint.
            await python.CommandAsync("allowed all", "configured");
            await python.CommandAsync("auto-answer 0.1", "configured");
            await phone.CallAsync(python.Identity, cancellationToken: stop.Token);
            await python.WaitAsync("established");
            await LiveStreamTests.WaitForAsync(() => phone.State == CallState.Established);
            phone.Hangup();
            await LiveStreamTests.WaitForAsync(() => phone.State == CallState.Available);
        }
        finally
        {
            await phone.DisposeAsync();
            await stop.CancelAsync();
            try { await run; }
            catch (OperationCanceledException) { }
        }
    }
}
