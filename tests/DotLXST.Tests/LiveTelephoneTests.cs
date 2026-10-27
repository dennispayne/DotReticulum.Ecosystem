using System.Collections.Concurrent;
using System.Net;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;

namespace DotLXST.Tests;

public sealed class VirtualAudioTheoryAttribute : TheoryAttribute
{
    public VirtualAudioTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("DOTLXST_LIVE") != "1" ||
            Environment.GetEnvironmentVariable("DOTLXST_VIRTUAL_AUDIO") != "1" || !OperatingSystem.IsLinux())
            Skip = "Requires DOTLXST_LIVE=1, DOTLXST_VIRTUAL_AUDIO=1 and Linux pulseaudio/pactl/parec; no physical audio is used.";
    }
}

[Trait("Category", "Integration")]
public sealed class LiveTelephoneTests
{
    [VirtualAudioTheory]
    [InlineData(false, "direct")]
    [InlineData(true, "direct")]
    [InlineData(false, "dotnet-hop")]
    [InlineData(true, "dotnet-hop")]
    [InlineData(false, "python-hop")]
    [InlineData(true, "python-hop")]
    [Trait("Category", "Live")]
    public async Task ActualPythonTelephoneSetsUpBidirectionalOpusAndTearsDown(bool pythonOriginates, string topology)
    {
        await using var pulse = await VirtualPulseServer.StartAsync();
        var port = LiveStreamTests.FreePort();
        var relayPort = topology == "direct" ? 0 : LiveStreamTests.FreePort();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var relayManager = topology == "dotnet-hop" ? new PacketInterfaceManager(
            [new TcpServerPacketInterface(new IPEndPoint(IPAddress.Loopback, relayPort), perClientInterfaces: true)]) : null;
        await using var relayRouter = relayManager is null ? null : new TransportRouter(relayManager,
            new TransportRouterOptions { TransportEnabled = true });
        if (relayManager is not null) await relayManager.StartAsync(stop.Token);
        var relayDispatch = relayRouter?.RunAsync(stop.Token) ?? Task.CompletedTask;
        await using var relayPython = topology == "python-hop" ? await LxstPythonNode.StartAsync(relayPort, transport: true) : null;
        await using var python = await LxstPythonNode.StartAsync(port, telephone: true, pulseServer: pulse.Address, connectPort: relayPort);
        var impaired = new ImpairedPacketInterface(new TcpClientPacketInterface("127.0.0.1", relayPort == 0 ? port : relayPort));
        await using var manager = new PacketInterfaceManager([impaired]);
        await using var router = new TransportRouter(manager, new TransportRouterOptions());
        using var identity = Identity.Generate();
        await using var telephone = new TelephoneEndpoint(router, identity,
            new() { AutoAnswerDelay = TimeSpan.FromMilliseconds(300) });
        var errors = new ConcurrentQueue<string>();
        var states = new ConcurrentQueue<CallState>();
        var audio = new ConcurrentQueue<AudioFrame>();
        telephone.Error = (context, exception) => errors.Enqueue(context + " " + exception?.Message);
        telephone.StateChanged += states.Enqueue;
        telephone.AudioReceived += audio.Enqueue;
        await manager.StartAsync(stop.Token);
        var dispatch = router.RunAsync(stop.Token);
        telephone.Start();
        try
        {
            if (relayPort == 0) await python.CommandAsync("wait-client", "client-ready");
            await python.CommandAsync("announce", "announced");
            await telephone.AnnounceAsync(stop.Token);
            await LiveStreamTests.WaitForAsync(() => router.HasPath(python.Hash));
            if (relayPort != 0) Assert.True(router.HopsTo(python.Hash) >= 2, "Telephone path bypassed the configured transport.");
            if (pythonOriginates)
                await python.CommandAsync("call " + Convert.ToHexStringLower(telephone.DestinationHash), "calling");
            else
                await telephone.CallAsync(python.Identity, cancellationToken: stop.Token);
            await python.WaitAsync("established");
            await LiveStreamTests.WaitForAsync(() => telephone.State == CallState.Established && !audio.IsEmpty);
            Assert.Contains(states, value => value == CallState.Ringing);
            Assert.Equal(TelephoneProfile.MediumQuality, telephone.ActiveProfile);
            var latency = await python.CommandAsync("low-latency-observation", "low-latency");
            Assert.Equal("LinuxBackend", latency.GetProperty("backend").GetString());
            Assert.False(latency.GetProperty("runtime_supported").GetBoolean());

            // Let the upstream ringtone/dial-tone buffers drain before observing
            // the isolated output monitor for our distinctive generated tone.
            await Task.Delay(1500, stop.Token);
            impaired.Enabled = true;
            var recorded = pulse.RecordOutputAsync(48000 * 2 * 2, stop.Token);
            for (var i = 0; i < 80; i++)
            {
                var samples = Enumerable.Range(i * 480, 480).Select(sample => (float)(.2 * Math.Sin(2 * Math.PI * 440 * sample / 24000))).ToArray();
                await telephone.SendAudioAsync(new(samples, 24000, 1), stop.Token);
                await Task.Delay(20, stop.Token);
            }
            var observed = await recorded;
            var frequencyEnergy = ToneEnergy(observed, 440);
            Assert.True(frequencyEnergy > ToneEnergy(observed, 382) * 2,
                "The Python Telephone output did not contain the generated 440 Hz audio above the dial-tone frequency.");
            Assert.True(frequencyEnergy > .00001,
                $"The virtual output lacked the generated tone (energy {frequencyEnergy}, peak {observed.Max(Math.Abs)}).");
            Assert.Contains(audio, frame => frame.SampleRate == 48000 && frame.Channels == 2);
            Assert.True(impaired.OutboundDrops > 0);
            Assert.True(impaired.InboundDrops > 0);
            Assert.Equal(CallState.Established, telephone.State);
            impaired.Enabled = false;
            var beforeRecovery = audio.Count;
            var recoveredRecording = pulse.RecordOutputAsync(48000 * 2, stop.Token);
            for (var index = 0; index < 60; index++)
            {
                var samples = Enumerable.Range(index * 480, 480).Select(sample => (float)(.2 * Math.Sin(2 * Math.PI * 880 * sample / 24000))).ToArray();
                await telephone.SendAudioAsync(new(samples, 24000, 1), stop.Token);
                await Task.Delay(20, stop.Token);
            }
            Assert.True(ToneEnergy(await recoveredRecording, 880) > .00001, "Python playback did not recover after loss was removed.");
            await LiveStreamTests.WaitForAsync(() => audio.Count > beforeRecovery + 2);
            if (relayPort == 0)
                await VerifyProfileSwitchesAsync(telephone, python, audio, pythonOriginates, stop.Token);
            await python.CommandAsync("hangup", "hungup");
            await LiveStreamTests.WaitForAsync(() => telephone.State == CallState.Available);
            Assert.Empty(errors);
        }
        finally
        {
            await telephone.DisposeAsync();
            await stop.CancelAsync();
            try { await dispatch; }
            catch (OperationCanceledException) { }
            try { await relayDispatch; }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task VerifyProfileSwitchesAsync(TelephoneEndpoint telephone, LxstPythonNode python,
        ConcurrentQueue<AudioFrame> audio, bool pythonOriginates, CancellationToken stop)
    {
        foreach (var profile in Enum.GetValues<TelephoneProfile>())
        {
            var before = audio.Count;
            if (pythonOriginates) await python.CommandAsync("profile " + (int)profile, "profile");
            else await telephone.SwitchProfileAsync(profile, stop);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while (true)
            {
                var observed = await python.CommandAsync("profile", "profile");
                if (observed.GetProperty("value").GetInt32() == (int)profile && telephone.ActiveProfile == profile) break;
                await Task.Delay(25, timeout.Token);
            }
            var (rate, channels, duration) = profile switch
            {
                TelephoneProfile.UltraLowBandwidth => (8000, 1, 400),
                TelephoneProfile.VeryLowBandwidth => (8000, 1, 320),
                TelephoneProfile.LowBandwidth => (8000, 1, 200),
                TelephoneProfile.HighQuality => (48000, 1, 60),
                TelephoneProfile.MaximumQuality => (48000, 2, 60),
                TelephoneProfile.UltraLowLatency => (24000, 1, 10),
                TelephoneProfile.LowLatency => (24000, 1, 20),
                _ => (24000, 1, 60),
            };
            for (var i = 0; i < 3; i++)
            {
                await telephone.SendAudioAsync(new(new float[rate * channels * duration / 1000], rate, channels), stop);
                await Task.Delay(duration, stop);
            }
            var receivedRate = profile < TelephoneProfile.MediumQuality ? 8000 : 48000;
            await LiveStreamTests.WaitForAsync(() => audio.Skip(before).Any(frame => frame.SampleRate == receivedRate));
            Assert.Equal(CallState.Established, telephone.State);
        }
    }

    private static double ToneEnergy(float[] interleaved, double frequency)
    {
        // Audio-device underruns can shift phase between network frames. Average
        // short-window energy instead of cancelling phase over the entire capture.
        var energy = 0.0;
        var samples = interleaved.Length / 2;
        for (var start = 0; start < samples; start += 2048)
        {
            var count = Math.Min(2048, samples - start);
            var real = 0.0;
            var imaginary = 0.0;
            for (var i = 0; i < count; i++)
            {
                var phase = 2 * Math.PI * frequency * i / 48000;
                real += interleaved[(start + i) * 2] * Math.Cos(phase);
                imaginary += interleaved[(start + i) * 2] * Math.Sin(phase);
            }
            energy += (real * real + imaginary * imaginary) / (4 * count);
        }
        return energy / samples;
    }
}
