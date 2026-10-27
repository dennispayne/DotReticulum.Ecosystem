using System.Collections.Concurrent;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;

namespace DotLXST.Tests;

public sealed class VirtualAudioTheoryAttribute : TheoryAttribute
{
    public VirtualAudioTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("DOTRETICULUM_LIVE") != "1" || Environment.GetEnvironmentVariable("DOTLXST_LIVE") != "1" ||
            Environment.GetEnvironmentVariable("DOTLXST_VIRTUAL_AUDIO") != "1" || !OperatingSystem.IsLinux())
            Skip = "Requires DOTRETICULUM_LIVE=1, DOTLXST_LIVE=1, DOTLXST_VIRTUAL_AUDIO=1 and Linux pulseaudio/pactl/parec; no physical audio is used.";
    }
}

[Trait("Category", "Integration")]
public sealed class LiveTelephoneTests
{
    [VirtualAudioTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Live")]
    public async Task ActualPythonTelephoneSetsUpBidirectionalOpusAndTearsDown(bool pythonOriginates)
    {
        await using var pulse = await VirtualPulseServer.StartAsync();
        var port = LiveStreamTests.FreePort();
        await using var python = await LxstPythonNode.StartAsync(port, telephone: true, pulseServer: pulse.Address);
        await using var manager = new PacketInterfaceManager([new TcpClientPacketInterface("127.0.0.1", port)]);
        await using var router = new TransportRouter(manager, new TransportRouterOptions());
        using var identity = Identity.Generate();
        await using var telephone = new TelephoneEndpoint(router, identity,
            new() { AutoAnswerDelay = TimeSpan.FromMilliseconds(300) });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(45));
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
            await python.CommandAsync("announce", "announced");
            await telephone.AnnounceAsync(stop.Token);
            await LiveStreamTests.WaitForAsync(() => router.HasPath(python.Hash));
            if (pythonOriginates)
                await python.CommandAsync("call " + Convert.ToHexStringLower(telephone.DestinationHash), "calling");
            else
                await telephone.CallAsync(python.Identity, cancellationToken: stop.Token);
            await python.WaitAsync("established");
            await LiveStreamTests.WaitForAsync(() => telephone.State == CallState.Established && !audio.IsEmpty);
            Assert.Contains(states, value => value == CallState.Ringing);
            Assert.Equal(TelephoneProfile.MediumQuality, telephone.ActiveProfile);

            // Let the upstream ringtone/dial-tone buffers drain before observing
            // the isolated output monitor for our distinctive generated tone.
            await Task.Delay(1500, stop.Token);
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
