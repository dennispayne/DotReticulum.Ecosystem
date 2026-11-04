using System.Collections.Concurrent;
using System.Text.Json;
using LXST.Audio;

namespace LXST.Tests;

public sealed partial class TelephoneLifecycleTests
{
    private sealed class AgcPacketCapture(ConcurrentQueue<byte[]> packets) : IAudioCodecFactory
    {
        public IAudioCodec Create(CodecKind kind) => new CapturingCodec(packets);

        private sealed class CapturingCodec(ConcurrentQueue<byte[]> packets) : IAudioCodec
        {
            public CodecKind Kind => CodecKind.Opus;
            public byte[] Encode(AudioFrame frame) => throw new NotSupportedException();
            public AudioFrame Decode(ReadOnlySpan<byte> data)
            {
                packets.Enqueue(data.ToArray());
                return new(new float[1], 24000, 1);
            }
            public void Dispose() { }
        }
    }

    // GenerateTelephoneAgcVectors.py executes primary 0.5.4 eba5abc77fef7fef3bcb6c9c091c80b7f7492c9f:
    // actual telephone/AGC methods, with constructor/call/packetizer boundaries substituted.
    private static JsonDocument LoadAgcControls() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxst-telephone-agc.json")));

    private static void ApplyAgcControl(TelephoneEndpoint phone, JsonElement step)
    {
        var argument = step.GetProperty("argument");
        switch (step.GetProperty("operation").GetString())
        {
            case "initial": break;
            case "pause_agc": phone.PauseAutomaticGainControl(argument.GetBoolean()); break;
            case "resume_agc": phone.ResumeAutomaticGainControl(argument.GetBoolean()); break;
            case "squelch_transmit": phone.SetTransmitSquelched(argument.GetBoolean()); break;
            case "unsquelch_transmit": phone.SetTransmitSquelched(!argument.GetBoolean()); break;
            default: throw new InvalidDataException("Unknown executed-reference AGC operation.");
        }
    }

    [Fact]
    public async Task IndependentAgcControlMatchesExecutedPrimaryWithoutChangingPtt()
    {
        using var fixture = LoadAgcControls();
        await using var rig = new Rig(new() { AutoAnswerDelay = TimeSpan.FromMilliseconds(20) });
        await rig.StartAsync();
        rig.Phone.PauseAutomaticGainControl();
        Assert.False(rig.Phone.AutomaticGainControlPaused);
        rig.Phone.ResumeAutomaticGainControl(false);
        Assert.False(rig.Phone.AutomaticGainControlPaused);
        await using var peer = rig.Connect(out var established);
        await established.WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var step in fixture.RootElement.GetProperty("steps").EnumerateArray())
        {
            ApplyAgcControl(rig.Phone, step);
            Assert.Equal(step.GetProperty("paused").GetBoolean(), rig.Phone.AutomaticGainControlPaused);
            Assert.Equal(step.GetProperty("squelched").GetBoolean(), rig.Phone.TransmitSquelched);
        }
        foreach (var step in fixture.RootElement.GetProperty("default_arguments").EnumerateArray())
        {
            if (step.GetProperty("operation").GetString() == "pause_agc") rig.Phone.PauseAutomaticGainControl();
            else rig.Phone.ResumeAutomaticGainControl();
            Assert.Equal(step.GetProperty("paused").GetBoolean(), rig.Phone.AutomaticGainControlPaused);
        }
        Assert.False(rig.Phone.TransmitSquelched);
    }

    [Fact]
    public async Task AgcControlWithoutAFilterIsNoopAndFilterPresenceIsCallLocal()
    {
        using var fixture = LoadAgcControls();
        Assert.All(fixture.RootElement.GetProperty("without_filter").EnumerateArray(), step =>
        {
            Assert.True(step.GetProperty("returned_none").GetBoolean());
            Assert.False(step.GetProperty("filter_created").GetBoolean());
        });
        await using var rig = new Rig(new() { UseAutomaticGainControl = false });
        await rig.StartAsync();
        await using (var first = rig.Connect(out var established))
        {
            await LiveStreamTests.WaitForAsync(() => rig.Phone.State == CallState.Ringing);
            rig.Phone.PauseAutomaticGainControl();
            Assert.False(rig.Phone.AutomaticGainControlPaused);
            await rig.Phone.AnswerAsync();
            await established.WaitAsync(TimeSpan.FromSeconds(5));
            rig.Phone.UseAutomaticGainControl = true;
            rig.Phone.PauseAutomaticGainControl();
            rig.Phone.ResumeAutomaticGainControl(false);
            Assert.False(rig.Phone.AutomaticGainControlPaused);
            Assert.False(rig.Phone.TransmitSquelched);
            await rig.Phone.HangupAsync();
        }
        await using (var second = rig.Connect(out var established))
        {
            await LiveStreamTests.WaitForAsync(() => rig.Phone.State == CallState.Ringing);
            rig.Phone.PauseAutomaticGainControl();
            Assert.False(rig.Phone.AutomaticGainControlPaused);
            await rig.Phone.AnswerAsync();
            await established.WaitAsync(TimeSpan.FromSeconds(5));
            rig.Phone.PauseAutomaticGainControl();
            Assert.True(rig.Phone.AutomaticGainControlPaused);
            rig.Phone.UseAutomaticGainControl = false;
            rig.Phone.ResumeAutomaticGainControl();
            Assert.False(rig.Phone.AutomaticGainControlPaused);
            rig.Phone.PauseAutomaticGainControl();
            Assert.True(rig.Phone.AutomaticGainControlPaused);
            await rig.Phone.HangupAsync();
        }
        Assert.False(rig.Phone.AutomaticGainControlPaused);
        rig.Phone.ResumeAutomaticGainControl(false);
        Assert.False(rig.Phone.AutomaticGainControlPaused);
        await using var third = rig.Connect(out var thirdEstablished);
        await LiveStreamTests.WaitForAsync(() => rig.Phone.State == CallState.Ringing);
        await rig.Phone.AnswerAsync();
        await thirdEstablished.WaitAsync(TimeSpan.FromSeconds(5));
        rig.Phone.PauseAutomaticGainControl();
        Assert.False(rig.Phone.AutomaticGainControlPaused);
    }

    [Fact]
    public async Task AgcControlRacesWithHangupAndDisposalWithoutReachingTheNextCall()
    {
        await using var rig = new Rig(new() { AutoAnswerDelay = TimeSpan.FromMilliseconds(20) });
        await rig.StartAsync();
        await using (var peer = rig.Connect(out var established))
        {
            await established.WaitAsync(TimeSpan.FromSeconds(5));
            var controls = Task.Run(() =>
            {
                for (var index = 0; index < 2000; index++)
                {
                    rig.Phone.PauseAutomaticGainControl();
                    rig.Phone.ResumeAutomaticGainControl();
                }
            });
            await Task.WhenAll(controls, rig.Phone.HangupAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
        }
        await using var next = rig.Connect(out var nextEstablished);
        await nextEstablished.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(rig.Phone.AutomaticGainControlPaused);
        var duringDisposal = Task.Run(() =>
        {
            for (var index = 0; index < 2000; index++)
            {
                try { rig.Phone.PauseAutomaticGainControl(); rig.Phone.ResumeAutomaticGainControl(); }
                catch (ObjectDisposedException) { return; }
            }
        });
        await Task.WhenAll(duringDisposal, rig.Phone.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<ObjectDisposedException>(() => rig.Phone.PauseAutomaticGainControl());
        Assert.Throws<ObjectDisposedException>(() => rig.Phone.ResumeAutomaticGainControl());
    }

    [NativeCodecFact]
    public async Task AgcControlAffectsNativeEncodedFramesWhilePttStillSuppressesPackets()
    {
        using var fixture = LoadAgcControls();
        await using var rig = new Rig(new()
        {
            AutoAnswerDelay = TimeSpan.FromMilliseconds(20), UseBandPass = false, UseEchoSuppression = false,
        });
        await rig.StartAsync();
        var packets = new ConcurrentQueue<byte[]>();
        await using var peer = rig.Connect(out var established, codecFactory: new AgcPacketCapture(packets));
        await established.WaitAsync(TimeSpan.FromSeconds(5));
        var input = new AudioFrame(fixture.RootElement.GetProperty("input_samples").EnumerateArray()
            .Select(sample => sample.GetSingle()).ToArray(), fixture.RootElement.GetProperty("sample_rate").GetInt32(), 1);
        var model = new AutomaticGainControl(targetLevelDb: -15);
        using var encoder = new NativeOpusCodec(OpusProfile.VoiceMedium);
        var transmitted = 0;
        foreach (var step in fixture.RootElement.GetProperty("steps").EnumerateArray())
        {
            ApplyAgcControl(rig.Phone, step);
            var processed = step.GetProperty("paused").GetBoolean() ? input : model.Process(input);
            var reference = step.GetProperty("samples").EnumerateArray().Select(sample => sample.GetSingle()).ToArray();
            Assert.Equal(reference.Length, processed.Samples.Length);
            for (var sample = 0; sample < reference.Length; sample++)
                Assert.InRange(MathF.Abs(reference[sample] - processed.Samples.Span[sample]), 0, 1e-6f);
            var expected = encoder.Encode(processed);
            await rig.Phone.SendAudioAsync(input);
            if (!step.GetProperty("squelched").GetBoolean())
            {
                transmitted++;
                await LiveStreamTests.WaitForAsync(() => packets.Count == 1);
                Assert.True(packets.TryDequeue(out var actual));
                Assert.Equal(expected, actual);
            }
            Assert.Equal(transmitted, rig.Phone.TransmittedFrames);
            Assert.Empty(packets);
        }
    }
}
