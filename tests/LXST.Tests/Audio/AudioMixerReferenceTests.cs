using System.Text.Json;
using System.Threading.Channels;
using LXST.Audio;

namespace LXST.Tests.Audio;

[Trait("Category", "Integration")]
public sealed class AudioMixerReferenceTests
{
    private sealed class Sink(List<string> order, bool fail = false) : IAudioSink
    {
        public Channel<AudioFrame> Frames { get; } = Channel.CreateBounded<AudioFrame>(8);
        public ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken = default)
        {
            order.Add("output");
            Frames.Writer.TryWrite(frame);
            if (fail) throw new IOException("injected sink failure");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Reference(List<string> order, bool fail = false) : IAudioReferenceReceiver
    {
        public Channel<AudioFrame> Frames { get; } = Channel.CreateBounded<AudioFrame>(8);
        public void HandleReference(AudioFrame frame)
        {
            if (fail) throw new IOException("injected reference failure");
            order.Add("reference");
            Frames.Writer.TryWrite(frame);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostGainClippedReferenceFollowsOutputAndOneFailureDoesNotDisableOtherReceivers(bool failOutput)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "lxst-telephone-controls.json")));
        var source = fixture.RootElement.GetProperty("mixer");
        var expected = source[1][2].EnumerateArray().Select(value => value.GetSingle()).ToArray();
        var order = new List<string>();
        var sink = new Sink(order, failOutput);
        await using var mixer = new AudioMixer(sink, TimeSpan.FromMilliseconds(1), 8000, 1) { GainDb = 3 };
        var good = new Reference(order);
        var bad = new Reference(order, fail: true);
        var errors = 0;
        mixer.Error = (_, _) => { Interlocked.Increment(ref errors); throw new InvalidOperationException("error observer"); };
        mixer.AddReferenceReceiver(bad);
        mixer.AddReferenceReceiver(good);
        mixer.Start();
        for (var index = 0; index < 2; index++)
        {
            mixer.TrySubmit(this, new(new[] { .6f, -.6f, .1f, -.1f, .2f, -.2f, 0, 1 }, 8000, 1));
            var actual = await good.Frames.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(expected, actual.Samples.ToArray());
            _ = await sink.Frames.Reader.ReadAsync();
        }
        Assert.Equal(new[] { "output", "reference", "output", "reference" }, order);
        Assert.True(errors >= 2);
        Assert.False(mixer.Completion.IsCompleted);
        Assert.True(mixer.RemoveReferenceReceiver(good));
        Assert.True(mixer.RemoveReferenceReceiver(bad));
        mixer.TrySubmit(this, new(new float[8], 8000, 1));
        _ = await sink.Frames.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await mixer.StopAsync();
        Assert.False(good.Frames.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ReceiverRegistrationIsBoundedAndDisposalPreventsFurtherRegistration()
    {
        var order = new List<string>();
        var mixer = new AudioMixer(new Sink(order), TimeSpan.FromMilliseconds(1), 8000, 1);
        var receiver = new Reference(order);
        for (var index = 0; index < 32; index++) mixer.AddReferenceReceiver(receiver);
        Assert.Throws<InvalidOperationException>(() => mixer.AddReferenceReceiver(receiver));
        Assert.True(mixer.RemoveReferenceReceiver(receiver));
        mixer.AddReferenceReceiver(receiver);
        await mixer.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => mixer.AddReferenceReceiver(receiver));
    }
}
