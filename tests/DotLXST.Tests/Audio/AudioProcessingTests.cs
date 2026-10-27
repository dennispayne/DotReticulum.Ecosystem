using System.Text.Json;
using DotLXST.Audio;

namespace DotLXST.Tests.Audio;

[Trait("Category", "Unit")]
public sealed class AudioProcessingTests
{
    private static readonly JsonDocument Vectors = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxst-audio-processing.json")));

    [Fact]
    public void ResamplingAndNormalizationMatchPinnedReference()
    {
        var source = Vectors.RootElement.GetProperty("resample");
        var input = Flatten(source.GetProperty("input"));
        var frame = new AudioFrame(input, source.GetProperty("inputRate").GetInt32(), source.GetProperty("channels").GetInt32());

        var actual = AudioResampler.Resample(frame, source.GetProperty("outputRate").GetInt32());
        var normalized = AudioResampler.Resample(frame, source.GetProperty("outputRate").GetInt32(), normalize: true);

        AssertSamples(source.GetProperty("output"), actual.Samples.Span, 3.4e-5f);
        AssertSamples(source.GetProperty("normalizedOutput"), normalized.Samples.Span, 3.4e-5f);
        Assert.Equal(11, actual.SamplesPerChannel);
        Assert.Equal(12_000, actual.SampleRate);
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioResampler.Resample(frame, 0));
        Assert.Throws<ArgumentException>(() => AudioResampler.Resample(
            new AudioFrame(new float[] { float.NaN }, 8_000, 1), 8_000));
    }

    [Fact]
    public void FiltersMatchPinnedReferenceAndPreserveStateAcrossFrames()
    {
        var source = Vectors.RootElement.GetProperty("filters");
        var input = ReadFloats(source.GetProperty("input"));
        var frame = new AudioFrame(input, source.GetProperty("sampleRate").GetInt32(), 1);
        var highPass = new HighPassFilter(source.GetProperty("highPassCut").GetSingle());
        var lowPass = new LowPassFilter(source.GetProperty("lowPassCut").GetSingle());
        var bandPass = new BandPassFilter(
            source.GetProperty("highPassCut").GetSingle(),
            source.GetProperty("lowPassCut").GetSingle());

        AssertSamples(source.GetProperty("highPass"), highPass.Process(frame).Samples.Span, 2e-6f);
        AssertSamples(source.GetProperty("lowPass"), lowPass.Process(frame).Samples.Span, 2e-6f);
        AssertSamples(source.GetProperty("bandPass"), bandPass.Process(frame).Samples.Span, 2e-6f);

        var continued = new HighPassFilter(source.GetProperty("highPassCut").GetSingle());
        var first = continued.Process(new AudioFrame(input.AsMemory(0, 7), frame.SampleRate, 1));
        var second = continued.Process(new AudioFrame(input.AsMemory(7), frame.SampleRate, 1));
        AssertSamples(source.GetProperty("highPass"), first.Samples.ToArray().Concat(second.Samples.ToArray()).ToArray(), 2e-6f);

        Assert.Throws<ArgumentOutOfRangeException>(() => new LowPassFilter(4_000).Process(frame));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BandPassFilter(1_200, 180).Process(frame));
        Assert.Throws<ArgumentException>(() => new HighPassFilter(100).Process(
            new AudioFrame(new float[] { float.PositiveInfinity }, 8_000, 1)));
    }

    [Fact]
    public void AutomaticGainControlMatchesPinnedReferenceAndLimitsPeaks()
    {
        var source = Vectors.RootElement.GetProperty("filters");
        var input = ReadFloats(source.GetProperty("agcInput"));
        var frame = new AudioFrame(input, source.GetProperty("agcSampleRate").GetInt32(), 2);

        var actual = new AutomaticGainControl().Process(frame);

        AssertSamples(source.GetProperty("agc"), actual.Samples.Span, 2e-5f);
        Assert.All(actual.Samples.ToArray(), sample => Assert.InRange(Math.Abs(sample), 0f, 0.75f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AutomaticGainControl(attackSeconds: 0).Process(frame));
    }

    [Fact]
    public async Task ToneSourceProducesReferenceToneAndHonorsCancellation()
    {
        var source = Vectors.RootElement.GetProperty("tone");
        var generator = new ToneAudioSource(
            source.GetProperty("frequency").GetSingle(),
            source.GetProperty("gain").GetSingle(),
            ease: false,
            frameDuration: TimeSpan.FromMilliseconds(10),
            sampleRate: source.GetProperty("sampleRate").GetInt32(),
            channels: source.GetProperty("channels").GetInt32());
        using var cancellation = new CancellationTokenSource();
        await using var frames = generator.CaptureAsync(cancellation.Token).GetAsyncEnumerator();

        Assert.True(await frames.MoveNextAsync());
        AssertSamples(source.GetProperty("samples"), frames.Current.Samples.Span, 2e-6f);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await frames.MoveNextAsync());
        Assert.False(generator.IsRunning);
    }

    [Fact]
    public async Task ToneSourceEasesOutAfterStop()
    {
        var source = new ToneAudioSource(
            frequency: 100,
            gain: 0.5f,
            ease: true,
            easeDuration: TimeSpan.FromMilliseconds(4),
            frameDuration: TimeSpan.FromMilliseconds(2),
            sampleRate: 1_000);
        await using var frames = source.CaptureAsync().GetAsyncEnumerator();

        Assert.True(await frames.MoveNextAsync());
        var rampUp = frames.Current.Samples.ToArray();
        Assert.Equal(0f, rampUp[0]);
        Assert.True(rampUp[1] > 0f);
        source.Stop();
        Assert.True(await frames.MoveNextAsync());
        Assert.True(frames.Current.Samples.Span[0] > 0f);
        Assert.False(await frames.MoveNextAsync());
        Assert.False(source.IsRunning);
    }

    private static float[] Flatten(JsonElement rows) =>
        rows.EnumerateArray().SelectMany(row => row.EnumerateArray().Select(value => value.GetSingle())).ToArray();

    private static float[] ReadFloats(JsonElement values) =>
        values[0].ValueKind == JsonValueKind.Array
            ? Flatten(values)
            : values.EnumerateArray().Select(value => value.GetSingle()).ToArray();

    private static void AssertSamples(JsonElement expected, ReadOnlySpan<float> actual, float tolerance) =>
        AssertSamples(ReadFloats(expected), actual, tolerance);

    private static void AssertSamples(float[] expected, ReadOnlySpan<float> actual, float tolerance)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
            Assert.InRange(Math.Abs(expected[i] - actual[i]), 0f, tolerance);
    }
}
