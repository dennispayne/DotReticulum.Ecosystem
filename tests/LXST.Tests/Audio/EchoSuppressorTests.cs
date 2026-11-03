using System.Buffers.Binary;
using System.Text.Json;
using LXST.Audio;

namespace LXST.Tests.Audio;

[Trait("Category", "Unit")]
public sealed class EchoSuppressorTests
{
    [Fact]
    public void DelayCouplingDoubleTalkGatingAndCapturedNoiseMatchExecutedPrimary()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "lxst-echo-suppression.json")));
        Assert.Equal("eba5abc77fef7fef3bcb6c9c091c80b7f7492c9f", fixture.RootElement.GetProperty("revision").GetString());
        foreach (var example in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var name = example.GetProperty("name").GetString()!;
            var clock = new Clock();
            var noise = example.GetProperty("noise_blocks").EnumerateArray().SelectMany(Decode).ToArray();
            var nextNoise = 0;
            using var filter = new EchoSuppressor(Options(example.GetProperty("parameters")), clock,
                () => nextNoise < noise.Length ? noise[nextNoise++] :
                    name.EndsWith("-reference-error", StringComparison.Ordinal) ? 0 :
                    throw new InvalidOperationException("The implementation consumed more noise than the reference."));
            var channels = example.GetProperty("channels").GetInt32();
            var index = 0;
            foreach (var row in example.GetProperty("frames").EnumerateArray())
            {
                clock.Now = DateTimeOffset.FromUnixTimeSeconds(1000).AddMilliseconds(index *
                    (name == "long-noise-reference-error" ? 400 : 10));
                filter.HandleReference(new(Decode(row.GetProperty("reference")), row.GetProperty("reference_rate").GetInt32(), channels));
                var microphone = new AudioFrame(Decode(row.GetProperty("microphone")), row.GetProperty("microphone_rate").GetInt32(), channels);
                var actual = filter.Process(microphone);
                if (row.TryGetProperty("reference_error", out var referenceError))
                {
                    Assert.EndsWith("-reference-error", name);
                    Assert.Equal(name == "rate-drop-reference-error" ? "attempt to get argmax of an empty sequence" :
                        "operands could not be broadcast together with shapes (19200,) (16384,) ", referenceError.GetString());
                    Assert.Equal(microphone.Samples.Length, actual.Samples.Length);
                    Assert.All(actual.Samples.ToArray(), sample => Assert.True(float.IsFinite(sample)));
                    break;
                }
                var state = filter.State;
                var context = $"{name} frame {index++}";
                AssertClose(row.GetProperty("delay_samples"), state.DelaySamples, 1e-5, context + " delay");
                AssertClose(row.GetProperty("delay_ms"), state.DelayMilliseconds, 1e-5, context + " ms");
                AssertClose(row.GetProperty("confidence"), state.Confidence, 3e-5, context + " confidence");
                AssertClose(row.GetProperty("coupling_db"), state.CouplingDb, .003, context + " coupling");
                AssertClose(row.GetProperty("gain"), state.Gain, 2e-6, context + " gain");
                AssertClose(row.GetProperty("ser_db"), state.SignalToEchoDb, .003, context + " SER");
                var positions = row.GetProperty("positions").EnumerateArray().Select(value => value.GetInt32()).ToArray();
                var expected = row.GetProperty("output").EnumerateArray().Select(value => value.GetDouble()).ToArray();
                for (var position = 0; position < positions.Length; position++)
                    Assert.True(Math.Abs(actual.Samples.Span[positions[position]] - expected[position]) <= 3e-6,
                        $"{context} sample {positions[position]}: {actual.Samples.Span[positions[position]]} != {expected[position]}");
                var rms = Math.Sqrt(actual.Samples.ToArray().Sum(value => (double)value * value) / actual.Samples.Length);
                AssertClose(row.GetProperty("rms"), rms, 3e-6, context + " RMS");
            }
            Assert.Equal(noise.Length, nextNoise);
            var empty = new AudioFrame(Array.Empty<float>(), 8000, channels);
            Assert.Same(empty, filter.Process(empty));
        }
    }

    private static EchoSuppressorOptions Options(JsonElement parameters)
    {
        double Value(string name, double fallback) => parameters.TryGetProperty(name, out var value) ? value.GetDouble() : fallback;
        var defaults = new EchoSuppressorOptions();
        return defaults with
        {
            ComfortNoiseEnabled = parameters.GetProperty("cng_enabled").GetBoolean(),
            MaximumDelayMilliseconds = Value("max_delay_ms", defaults.MaximumDelayMilliseconds),
            TrackingWindowMilliseconds = Value("track_window_ms", defaults.TrackingWindowMilliseconds),
            CorrelationFrameMilliseconds = Value("correlation_frame_ms", defaults.CorrelationFrameMilliseconds),
            CorrelationRetention = Value("acc_forget", defaults.CorrelationRetention),
            DelaySmoothing = Value("ema_alpha", defaults.DelaySmoothing),
            EstimateEveryFrames = (int)Value("estimate_every_n", defaults.EstimateEveryFrames),
            Preemphasis = Value("preemph_alpha", defaults.Preemphasis),
            AttackMilliseconds = Value("attack_ms", defaults.AttackMilliseconds),
            ReleaseMilliseconds = Value("release_ms", defaults.ReleaseMilliseconds),
            HangoverMilliseconds = Value("hangover_ms", defaults.HangoverMilliseconds),
            CouplingPercentile = Value("coupling_percentile", defaults.CouplingPercentile),
            RmsThreshold = Value("rms_threshold", defaults.RmsThreshold),
            CouplingWindowSeconds = Value("coupling_window_s", defaults.CouplingWindowSeconds),
            GateRatioDb = Value("gate_ratio_db", defaults.GateRatioDb),
            DoubleTalkEnergyDb = Value("dtd_energy_db", defaults.DoubleTalkEnergyDb),
            ReferenceRmsThreshold = Value("ref_rms_threshold", defaults.ReferenceRmsThreshold),
            ComfortNoiseGain = Value("cng_gain", defaults.ComfortNoiseGain),
            ComfortNoiseColor = Value("cng_color", defaults.ComfortNoiseColor),
        };
    }

    [Fact]
    public void DefaultsMatchActualConstructorAndInvalidParametersFailBeforeAllocation()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "lxst-echo-suppression.json")));
        var defaults = fixture.RootElement.GetProperty("defaults");
        var options = new EchoSuppressorOptions();
        foreach (var (key, actual) in new (string, double)[]
        {
            ("max_delay_ms", options.MaximumDelayMilliseconds), ("track_window_ms", options.TrackingWindowMilliseconds),
            ("correlation_frame_ms", options.CorrelationFrameMilliseconds), ("correlation_threshold", options.CorrelationThreshold),
            ("rms_threshold", options.RmsThreshold), ("ema_alpha", options.DelaySmoothing),
            ("preemph_alpha", options.Preemphasis), ("acc_forget", options.CorrelationRetention),
            ("coupling_window_s", options.CouplingWindowSeconds), ("coupling_percentile", options.CouplingPercentile),
            ("coupling_threshold_db", options.CouplingThresholdDb), ("gate_ratio_db", options.GateRatioDb),
            ("corr_threshold", options.EchoCorrelationThreshold), ("dtd_energy_db", options.DoubleTalkEnergyDb),
            ("hangover_ms", options.HangoverMilliseconds), ("attack_ms", options.AttackMilliseconds),
            ("release_ms", options.ReleaseMilliseconds), ("ref_rms_threshold", options.ReferenceRmsThreshold),
            ("estimate_every_n", options.EstimateEveryFrames), ("cng_gain", options.ComfortNoiseGain),
            ("cng_color", options.ComfortNoiseColor),
        }) Assert.Equal(defaults.GetProperty(key).GetDouble(), actual);
        Assert.Equal(defaults.GetProperty("cng_enabled").GetBoolean(), options.ComfortNoiseEnabled);
        Assert.Throws<ArgumentOutOfRangeException>(() => new EchoSuppressor(new() { MaximumDelayMilliseconds = 2001 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EchoSuppressor(new() { EstimateEveryFrames = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EchoSuppressor(new() { AttackMilliseconds = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EchoSuppressor(new() { Preemphasis = double.NaN }));
    }

    [Fact]
    public async Task DisposalWaitsForActiveNoiseProcessingAndThenRejectsFurtherFrames()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "lxst-echo-suppression.json")));
        var example = fixture.RootElement.GetProperty("cases").EnumerateArray()
            .Single(value => value.GetProperty("name").GetString() == "comfort-noise");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new Clock();
        using var filter = new EchoSuppressor(Options(example.GetProperty("parameters")), clock, () =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return 0;
        });
        var processing = Task.CompletedTask;
        var disposal = Task.CompletedTask;
        AudioFrame? last = null;
        try
        {
            var index = 0;
            foreach (var row in example.GetProperty("frames").EnumerateArray())
            {
                clock.Now = DateTimeOffset.FromUnixTimeSeconds(1000).AddMilliseconds(index++ * 10);
                filter.HandleReference(new(Decode(row.GetProperty("reference")), 8000, 2));
                last = new(Decode(row.GetProperty("microphone")), 8000, 2);
                if (row.GetProperty("gain").GetDouble() >= .05) { filter.Process(last); continue; }
                processing = Task.Run(() => filter.Process(last));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                disposal = Task.Run(() => { started.TrySetResult(); filter.Dispose(); });
                await started.Task;
                Assert.False(disposal.IsCompleted);
                break;
            }
            Assert.True(entered.Task.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(processing, disposal);
        }
        Assert.NotNull(last);
        Assert.Throws<ObjectDisposedException>(() => filter.Process(last));
    }

    [Fact]
    public void NoReferenceMalformedInputAndDisposalRemainSafe()
    {
        var filter = new EchoSuppressor();
        var frame = new AudioFrame(new float[480], 48000, 1);
        Assert.Same(frame, filter.Process(frame));
        Assert.Null(filter.State.DelaySamples);
        Assert.Throws<ArgumentException>(() => filter.HandleReference(new(new[] { float.NaN }, 48000, 1)));
        Assert.Throws<ArgumentException>(() => filter.Process(new(new[] { float.PositiveInfinity }, 48000, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => filter.Process(new(new float[48001], 48000, 1)));
        filter.HandleReference(frame);
        filter.Dispose();
        filter.Dispose();
        Assert.Throws<ObjectDisposedException>(() => filter.Process(frame));
        Assert.Throws<ObjectDisposedException>(() => filter.HandleReference(frame));
    }

    private static float[] Decode(JsonElement encoded)
    {
        var bytes = Convert.FromBase64String(encoded.GetString()!);
        var result = new float[bytes.Length / sizeof(float)];
        for (var index = 0; index < result.Length; index++)
            result[index] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(index * sizeof(float), sizeof(float)));
        return result;
    }

    private static void AssertClose(JsonElement expected, double? actual, double tolerance, string context)
    {
        if (expected.ValueKind == JsonValueKind.Null) { Assert.Null(actual); return; }
        Assert.NotNull(actual);
        var value = expected.ValueKind == JsonValueKind.String
            ? expected.GetString() == "infinity" ? double.PositiveInfinity : double.NegativeInfinity
            : expected.GetDouble();
        Assert.True(value.Equals(actual.Value) || Math.Abs(value - actual.Value) <= tolerance,
            $"{context}: expected {value:R}, actual {actual:R}");
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
