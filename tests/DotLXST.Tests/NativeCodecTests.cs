using System.Runtime.InteropServices;
using System.Text.Json;

namespace DotLXST.Tests;

public sealed class NativeCodecFactAttribute : FactAttribute
{
    public NativeCodecFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DOTLXST_NATIVE") != "1")
            Skip = "Set DOTLXST_NATIVE=1 with libopus and libcodec2 installed; this test loads native libraries.";
    }
}

/// <summary>Actual LXST vector generator, SHA 3b596f48ddd252ddc9c315ef8f3020cfd3688223; no reimplemented reference codec.</summary>
[Trait("Category", "Integration")]
public sealed class NativeCodecTests
{
    [NativeCodecFact]
    public void ProfileChangesBeforeEncodingAndChannelConversionMatchActualPrimaryControls()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxst-codec-state.json")));
        foreach (var vector in fixture.RootElement.GetProperty("opus_channels").EnumerateArray())
        {
            using var codec = new NativeOpusCodec();
            codec.SetProfile((OpusProfile)vector.GetProperty("profile").GetInt32());
            var input = vector.GetProperty("input").EnumerateArray().Select(value => value.GetSingle()).ToArray();
            var encoded = codec.Encode(new(input, vector.GetProperty("rate").GetInt32(), vector.GetProperty("channels").GetInt32()));
            Assert.Equal(Convert.FromHexString(vector.GetProperty("encoded").GetString()!), encoded);
            Assert.Equal(encoded.Length, codec.OutputBytes);
            Assert.Equal(20, codec.OutputMilliseconds);
            Assert.Equal(encoded.Length * 400, codec.OutputBitrate);
            Assert.Throws<InvalidOperationException>(() => codec.SetProfile(OpusProfile.VoiceLow));
        }
        foreach (var vector in fixture.RootElement.GetProperty("opus_controls").EnumerateArray())
        {
            Assert.Equal("PyOggError", vector.GetProperty("post_encode_set_profile").GetString());
            using var codec = new NativeOpusCodec((OpusProfile)vector.GetProperty("profile").GetInt32());
            Assert.Equal(vector.GetProperty("max_bytes").GetInt32(), codec.UpdateBitrate(2.5));
            Assert.Throws<ArgumentOutOfRangeException>(() => codec.UpdateBitrate(double.NaN));
            codec.Dispose();
            Assert.Throws<ObjectDisposedException>(() => codec.SetProfile(OpusProfile.AudioMaximum));
        }
    }

    [NativeCodecFact]
    public void StatefulCodec2ModeSwitchesAndUnknownHeadersMatchActualPrimary()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxst-codec-state.json")));
        using var codec = new NativeCodec2();
        var input = fixture.RootElement.GetProperty("codec2_input").EnumerateArray().Select(value => value.GetSingle()).ToArray();
        foreach (var vector in fixture.RootElement.GetProperty("codec2_sequence").EnumerateArray())
        {
            var incoming = Convert.FromHexString(vector.GetProperty("incoming").GetString()!);
            Assert.Equal(vector.GetProperty("decoded_count").GetInt32(), codec.Decode(incoming).SamplesPerChannel);
            Assert.Equal(vector.GetProperty("mode").GetInt32(), codec.Mode);
            Assert.Equal(Convert.FromHexString(vector.GetProperty("encoded_after_decode").GetString()!), codec.Encode(new(input, 8000, 2)));
            incoming[0] = 255;
            Assert.Equal(vector.GetProperty("unknown_count").GetInt32(), codec.Decode(incoming).SamplesPerChannel);
            Assert.Equal(vector.GetProperty("mode").GetInt32(), codec.Mode);
        }
    }

    [NativeCodecFact]
    public void OpusAllProfilesMatchActualReferenceBitstreamAndDecode()
    {
        using var fixture = StreamPacketTests.LoadVectors();
        foreach (var vector in fixture.RootElement.GetProperty("opus").EnumerateArray())
        {
            using var codec = new NativeOpusCodec((OpusProfile)vector.GetProperty("profile").GetInt32());
            var samples = vector.GetProperty("input").EnumerateArray().Select(v => v.GetSingle()).ToArray();
            var encoded = codec.Encode(new(samples, vector.GetProperty("sample_rate").GetInt32(), vector.GetProperty("channels").GetInt32()));
            Assert.Equal(Convert.FromHexString(vector.GetProperty("encoded").GetString()!), encoded);
            var decoded = codec.Decode(encoded);
            AssertClose(vector.GetProperty("decoded").EnumerateArray().Select(v => v.GetSingle()).ToArray(), decoded.Samples.ToArray(), "Opus");
        }
    }

    [NativeCodecFact]
    public void Codec2AllModesMatchActualReferenceBitstreamAndDecode()
    {
        using var fixture = StreamPacketTests.LoadVectors();
        var path = Environment.GetEnvironmentVariable("DOTLXST_CODEC2_LIBRARY") ??
            (OperatingSystem.IsWindows() ? "codec2.dll" : "libcodec2.so.1.2");
        // Codec2's excitation PRNG is process-global. Keep the module loaded for
        // the complete sequence, exactly as pycodec2 does during generation.
        var module = NativeLibrary.Load(path);
        try
        {
            foreach (var vector in fixture.RootElement.GetProperty("codec2").EnumerateArray())
            {
                using var codec = new NativeCodec2(vector.GetProperty("mode").GetInt32(), path);
                var samples = vector.GetProperty("input").EnumerateArray().Select(v => v.GetSingle()).ToArray();
                var encoded = codec.Encode(new(samples, 8000, 1));
                Assert.Equal(Convert.FromHexString(vector.GetProperty("encoded").GetString()!), encoded);
                var decoded = codec.Decode(encoded);
                AssertClose(vector.GetProperty("decoded").EnumerateArray().Select(v => v.GetSingle()).ToArray(), decoded.Samples.ToArray(), $"Codec2 mode {codec.Mode}");
            }
        }
        finally { NativeLibrary.Free(module); }
    }

    [NativeCodecFact]
    public void MalformedInputAndDisposedNativeHandlesFailSafely()
    {
        using var opus = new NativeOpusCodec();
        Assert.Throws<FormatException>(() => opus.Decode([]));
        Assert.Throws<ArgumentException>(() => opus.Encode(new(new float[13], 8000, 1)));
        opus.Dispose();
        Assert.Throws<ObjectDisposedException>(() => opus.Decode([0]));
        using var codec2 = new NativeCodec2();
        Assert.Throws<FormatException>(() => codec2.Decode([]));
        codec2.Dispose();
        Assert.Throws<ObjectDisposedException>(() => codec2.Decode([0]));
    }

    private static void AssertClose(float[] expected, float[] actual, string context)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) <= 0.000001f, $"{context} sample {i}: expected {expected[i]}, actual {actual[i]}.");
    }
}
