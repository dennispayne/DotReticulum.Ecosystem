using System.Collections.Concurrent;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;

namespace DotLXST.Tests;

[Trait("Category", "Integration")]
public sealed class LiveTelephoneProfileTests
{
    [VirtualAudioTheory]
    [InlineData(false, TelephoneProfile.UltraLowBandwidth)]
    [InlineData(false, TelephoneProfile.VeryLowBandwidth)]
    [InlineData(false, TelephoneProfile.LowBandwidth)]
    [InlineData(false, TelephoneProfile.MediumQuality)]
    [InlineData(false, TelephoneProfile.HighQuality)]
    [InlineData(false, TelephoneProfile.MaximumQuality)]
    [InlineData(false, TelephoneProfile.LowLatency)]
    [InlineData(false, TelephoneProfile.UltraLowLatency)]
    [InlineData(true, TelephoneProfile.UltraLowBandwidth)]
    [InlineData(true, TelephoneProfile.VeryLowBandwidth)]
    [InlineData(true, TelephoneProfile.LowBandwidth)]
    [InlineData(true, TelephoneProfile.MediumQuality)]
    [InlineData(true, TelephoneProfile.HighQuality)]
    [InlineData(true, TelephoneProfile.MaximumQuality)]
    [InlineData(true, TelephoneProfile.LowLatency)]
    [InlineData(true, TelephoneProfile.UltraLowLatency)]
    [Trait("Category", "Live")]
    public async Task EveryInitialProfileNegotiatesAndReceivesRealCodecFrames(bool pythonOriginates, TelephoneProfile profile)
    {
        await using var pulse = await VirtualPulseServer.StartAsync();
        var port = LiveStreamTests.FreePort();
        await using var python = await LxstPythonNode.StartAsync(port, telephone: true, pulseServer: pulse.Address);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var manager = new PacketInterfaceManager([new TcpClientPacketInterface("127.0.0.1", port)]);
        await using var router = new TransportRouter(manager, new());
        using var identity = Identity.Generate();
        await using var phone = new TelephoneEndpoint(router, identity,
            new() { AutoAnswerDelay = TimeSpan.FromMilliseconds(300) });
        var frames = new ConcurrentQueue<AudioFrame>();
        var errors = new ConcurrentQueue<string>();
        phone.AudioReceived += frames.Enqueue;
        phone.Error = (context, exception) => errors.Enqueue(context + " " + exception?.Message);
        await manager.StartAsync(stop.Token);
        var run = router.RunAsync(stop.Token);
        phone.Start();
        try
        {
            await python.CommandAsync("wait-client", "client-ready");
            await python.CommandAsync("announce", "announced");
            await phone.AnnounceAsync(stop.Token);
            await LiveStreamTests.WaitForAsync(() => router.HasPath(python.Hash));
            if (pythonOriginates)
                await python.CommandAsync($"call {Convert.ToHexStringLower(phone.DestinationHash)} {(int)profile}", "calling");
            else await phone.CallAsync(python.Identity, profile, stop.Token);
            await python.WaitAsync("established");
            await LiveStreamTests.WaitForAsync(() => phone.State == CallState.Established && !frames.IsEmpty);
            Assert.Equal(profile, phone.ActiveProfile);
            var observed = await python.CommandAsync("profile", "profile");
            Assert.Equal((int)profile, observed.GetProperty("value").GetInt32());
            var rate = profile < TelephoneProfile.MediumQuality ? 8000 :
                profile is TelephoneProfile.HighQuality or TelephoneProfile.MaximumQuality ? 48000 : 24000;
            var channels = profile == TelephoneProfile.MaximumQuality ? 2 : 1;
            var duration = TelephoneProfiles.Get(profile).FrameMilliseconds;
            await phone.SendAudioAsync(new(new float[rate * channels * duration / 1000], rate, channels), stop.Token);
            Assert.Contains(frames, frame => frame.SampleRate == (profile < TelephoneProfile.MediumQuality ? 8000 : 48000));
            await python.CommandAsync("hangup", "hungup");
            await LiveStreamTests.WaitForAsync(() => phone.State == CallState.Available);
            Assert.Empty(errors);
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
