using System.Threading.Channels;
using System.Diagnostics;
using DotLXST.Audio;

namespace DotLXST.Tests.Audio;

[Trait("Category", "Unit")]
public sealed class AudioMixerTests
{
    [Fact]
    public async Task MixerBoundsSourcesAndFramesAndMixesAdaptedChannels()
    {
        var sink = new FrameSink();
        await using var mixer = new AudioMixer(
            sink, TimeSpan.FromMilliseconds(10), 8_000, channels: 2,
            maximumSources: 2, framesPerSource: 1);
        var mono = new object();
        var stereo = new object();

        Assert.True(mixer.TrySubmit(mono, new AudioFrame(new float[] { 0.75f, 0.4f }, 8_000, 1)));
        Assert.False(mixer.TrySubmit(mono, new AudioFrame(new float[] { 0.1f, 0.1f }, 8_000, 1)));
        Assert.True(mixer.TrySubmit(stereo, new AudioFrame(new float[] { 0.5f, -0.5f, 0.5f, -0.5f }, 8_000, 2)));
        Assert.False(mixer.TrySubmit(new object(), new AudioFrame(new float[] { 0f }, 8_000, 1)));
        Assert.False(mixer.CanAccept(mono));

        mixer.Start();
        var mixed = await sink.ReadAsync();
        await mixer.StopAsync();

        Assert.Equal(2, mixed.Channels);
        Assert.Equal(80, mixed.SamplesPerChannel);
        var expected = new float[] { 1f, 0.25f, 0.9f, -0.1f };
        for (var i = 0; i < expected.Length; i++)
            Assert.InRange(Math.Abs(expected[i] - mixed.Samples.Span[i]), 0f, 1e-6f);
        Assert.All(mixed.Samples.Span[4..].ToArray(), sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public async Task MixerGainMuteAndDisposalRaceRemainBounded()
    {
        var sink = new FrameSink();
        var mixer = new AudioMixer(sink, TimeSpan.FromMilliseconds(1), 8_000, 1);
        var source = new object();
        mixer.GainDb = 6;
        Assert.True(mixer.TrySubmit(source, new AudioFrame(new float[] { 0.1f }, 8_000, 1)));
        mixer.Start();
        var gained = await sink.ReadAsync();
        Assert.InRange(gained.Samples.Span[0], 0.39f, 0.41f);

        mixer.IsMuted = true;
        Assert.True(mixer.TrySubmit(source, new AudioFrame(new float[] { 0.5f }, 8_000, 1)));
        var muted = await sink.ReadAsync();
        Assert.All(muted.Samples.ToArray(), sample => Assert.Equal(0f, sample));

        var stop = mixer.StopAsync();
        await mixer.DisposeAsync();
        await stop;
        Assert.False(mixer.CanAccept(source));
        Assert.Throws<ObjectDisposedException>(() => mixer.TrySubmit(source, new AudioFrame(new float[] { 0 }, 8_000, 1)));
    }

    [Fact]
    public async Task MixerUsesConfiguredCadenceAndRejectsMismatchedInputRate()
    {
        var sink = new FrameSink();
        await using var mixer = new AudioMixer(sink, TimeSpan.FromMilliseconds(20), 8_000, 1);
        Assert.True(mixer.TrySubmit(new object(), new AudioFrame(new float[] { 0f, 0.25f, 0.5f, 0.75f }, 8_000, 1)));
        Assert.Throws<ArgumentException>(() => mixer.TrySubmit(new object(), new AudioFrame(new float[] { 0f }, 4_000, 1)));
        var started = Stopwatch.StartNew();
        mixer.Start();

        var mixed = await sink.ReadAsync();
        started.Stop();
        await mixer.StopAsync();

        Assert.True(started.Elapsed >= TimeSpan.FromMilliseconds(10));
        Assert.Equal(8_000, mixed.SampleRate);
        Assert.Equal(160, mixed.SamplesPerChannel);
        var samples = mixed.Samples.Span;
        Assert.Equal(new float[] { 0f, 0.25f, 0.5f, 0.75f }, samples[..4].ToArray());
        Assert.Equal(0f, samples[4]);
    }

    private sealed class FrameSink : IAudioSink
    {
        private readonly Channel<AudioFrame> _frames = Channel.CreateUnbounded<AudioFrame>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        public ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken = default) =>
            _frames.Writer.WriteAsync(frame, cancellationToken);

        public async Task<AudioFrame> ReadAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            return await _frames.Reader.ReadAsync(timeout.Token);
        }
    }
}
