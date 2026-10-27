using System.Text.Json;
using DotLXST.Audio;

namespace DotLXST.Tests.Audio;

[Trait("Category", "Unit")]
public sealed class ResamplingAudioCodecTests
{
    private static readonly JsonDocument Vectors = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxst-audio-processing.json")));

    [Fact]
    public void FixtureRecordsTheExecutedPinnedReferenceAndSources()
    {
        var reference = Vectors.RootElement.GetProperty("reference");
        Assert.Equal("3b596f48ddd252ddc9c315ef8f3020cfd3688223", reference.GetProperty("revision").GetString());
        Assert.Contains("Executed pinned LXST APIs", reference.GetProperty("method").GetString());
        Assert.Equal(
            "2bd8d637d573476099996498e649a8fa5ea88507dd3c44a4c8b261e6187b3900",
            reference.GetProperty("sourceSha256").GetProperty("LXST/Codecs/Codec.py").GetString());
        Assert.Equal(
            "0f0e6f9ffa48e899a80220b33485226fc411d70cd07dce3ff0b7582a208deb03",
            reference.GetProperty("sourceSha256").GetProperty("LXST/Codecs/Opus.py").GetString());
    }

    [Fact]
    public void Pcm16ResamplingMatchesExecutedReferenceBytes()
    {
        var source = Vectors.RootElement.GetProperty("resampleBytes");
        foreach (var vector in source.GetProperty("cases").EnumerateArray())
        {
            var actual = AudioResampler.Resample(
                Convert.FromHexString(vector.GetProperty("input").GetString()!),
                source.GetProperty("bitDepth").GetInt32(),
                vector.GetProperty("channels").GetInt32(),
                vector.GetProperty("inputRate").GetInt32(),
                vector.GetProperty("outputRate").GetInt32(),
                vector.GetProperty("normalize").GetBoolean());
            Assert.Equal(Convert.FromHexString(vector.GetProperty("output").GetString()!), actual);
        }
    }

    [Fact]
    public void Pcm16ResamplingHandlesIdentitySingleSampleAndInvalidInput()
    {
        byte[] sample = [0x01, 0x80];
        Assert.Equal(sample, AudioResampler.Resample(sample, 16, 1, 8_000, 8_000));
        Assert.Equal(sample, AudioResampler.Resample(sample, 16, 1, 8_000, 16_000));
        Assert.Empty(AudioResampler.Resample([], 16, 1, 8_000, 16_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioResampler.Resample(sample, 8, 1, 8_000, 16_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioResampler.Resample(sample, 16, 0, 8_000, 16_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioResampler.Resample(sample, 16, 1, 0, 16_000));
        Assert.Throws<ArgumentException>(() => AudioResampler.Resample([0x00], 16, 1, 8_000, 16_000));
        Assert.Throws<ArgumentException>(() => AudioResampler.Resample([0x00, 0x00, 0x00], 16, 2, 8_000, 16_000));
    }

    [Fact]
    public void CodecWrapperResamplesInputAndOutputUsingExplicitRates()
    {
        var native = new RecordingCodec
        {
            DecodedFrame = new AudioFrame(new float[] { 0f, 0.5f, 1f }, 8_000, 1),
        };
        using var codec = new ResamplingAudioCodec(native, codecSampleRate: 16_000, outputSampleRate: 16_000);

        codec.Encode(new AudioFrame(new float[] { 0f, 1f, 0f }, 8_000, 1));
        var converted = native.EncodedFrame!;
        Assert.Equal(16_000, converted.SampleRate);
        Assert.Equal(1, converted.Channels);
        Assert.Equal(5, converted.SamplesPerChannel);
        Assert.Equal(new short[] { 0, 16_383, 32_767, 16_383, 0 },
            converted.Samples.ToArray().Select(value => (short)(value * short.MaxValue)).ToArray());

        var decoded = codec.Decode([0x01]);
        Assert.Equal(16_000, decoded.SampleRate);
        Assert.Equal(5, decoded.SamplesPerChannel);
        Assert.Equal(0f, decoded.Samples.Span[0]);
        Assert.Equal(1f, decoded.Samples.Span[^1]);
        Assert.Equal(CodecKind.Opus, codec.Kind);
    }

    [Fact]
    public void Codec2InputRateConversionSelectsTheReferenceMonoChannel()
    {
        var native = new RecordingCodec(CodecKind.Codec2);
        using var codec = new ResamplingAudioCodec(native, codecSampleRate: 8_000);
        codec.Encode(new AudioFrame(
            new float[] { 0.1f, 0.25f, 0.2f, 0.5f, 0.3f, 0.75f, 0.4f, 1f },
            16_000,
            2));

        Assert.Equal(1, native.EncodedFrame!.Channels);
        Assert.Equal(8_000, native.EncodedFrame.SampleRate);
        var pcm = native.EncodedFrame.Samples.ToArray().Select(value => (short)(value * short.MaxValue)).ToArray();
        Assert.Equal(new short[] { 8191, 24575 }, pcm);
    }

    [Fact]
    public void DisposingWrapperDisposesCodecAndRejectsFurtherOperations()
    {
        var native = new RecordingCodec();
        var codec = new ResamplingAudioCodec(native, codecSampleRate: 8_000);
        codec.Dispose();
        codec.Dispose();

        Assert.True(native.Disposed);
        Assert.Throws<ObjectDisposedException>(() => codec.Encode(new AudioFrame(new float[] { 0f }, 8_000, 1)));
        Assert.Throws<ObjectDisposedException>(() => codec.Decode([0x01]));
    }

    private sealed class RecordingCodec(CodecKind kind = CodecKind.Opus) : IAudioCodec
    {
        public CodecKind Kind => kind;
        public AudioFrame? EncodedFrame { get; private set; }
        public AudioFrame DecodedFrame { get; init; } = new(new float[] { 0f }, 8_000, 1);
        public bool Disposed { get; private set; }

        public byte[] Encode(AudioFrame frame)
        {
            EncodedFrame = frame;
            return [];
        }

        public AudioFrame Decode(ReadOnlySpan<byte> data) => DecodedFrame;
        public void Dispose() => Disposed = true;
    }
}

[Trait("Category", "Integration")]
public sealed class ResamplingAudioCodecIntegrationTests
{
    [NativeCodecFact]
    public void ResamplingOpusEncodingMatchesExecutedReferenceBitstream()
    {
        using var vectors = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxst-audio-processing.json")));
        var source = vectors.RootElement.GetProperty("resamplingCodec");
        var samples = source.GetProperty("input").EnumerateArray().Select(value => value.GetSingle()).ToArray();
        using var native = new NativeOpusCodec(OpusProfile.VoiceLow);
        using var codec = new ResamplingAudioCodec(native, source.GetProperty("codecRate").GetInt32());

        var actual = codec.Encode(new AudioFrame(samples, source.GetProperty("inputRate").GetInt32(), 1));

        Assert.Equal(Convert.FromHexString(source.GetProperty("encoded").GetString()!), actual);
    }
}
