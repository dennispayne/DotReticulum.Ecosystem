using System.Buffers.Binary;

namespace DotLXST.Audio;

public static class AudioResampler
{
    private const int PcmScale = short.MaxValue;

    public static byte[] Resample(
        ReadOnlySpan<byte> sampleBytes,
        int bitDepth,
        int channels,
        int inputSampleRate,
        int outputSampleRate,
        bool normalize = false)
    {
        if (bitDepth != 16) throw new ArgumentOutOfRangeException(nameof(bitDepth), "Only signed 16-bit PCM is supported.");
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, 32);
        ArgumentOutOfRangeException.ThrowIfLessThan(inputSampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(outputSampleRate, 1);

        var frameSize = checked(channels * sizeof(short));
        if (sampleBytes.Length % frameSize != 0)
            throw new ArgumentException("PCM data must contain complete interleaved frames.", nameof(sampleBytes));
        if (sampleBytes.IsEmpty) return [];

        var inputFrames = sampleBytes.Length / frameSize;
        var pcm = new short[checked(inputFrames * channels)];
        var peak = 0;
        for (var i = 0; i < pcm.Length; i++)
        {
            pcm[i] = BinaryPrimitives.ReadInt16LittleEndian(sampleBytes.Slice(i * sizeof(short), sizeof(short)));
            peak = Math.Max(peak, Math.Abs((int)pcm[i]));
        }

        if (normalize && peak > 0)
        {
            var gain = 32768.0 / peak;
            for (var i = 0; i < pcm.Length; i++)
            {
                var scaled = (long)Math.Floor(pcm[i] * gain);
                pcm[i] = (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
            }
        }

        if (inputSampleRate == outputSampleRate)
        {
            var unchanged = new byte[sampleBytes.Length];
            for (var i = 0; i < pcm.Length; i++)
                BinaryPrimitives.WriteInt16LittleEndian(unchanged.AsSpan(i * sizeof(short)), pcm[i]);
            return unchanged;
        }

        var outputFrames = checked((int)(((long)(inputFrames - 1) * outputSampleRate) / inputSampleRate) + 1);
        var output = new byte[checked(outputFrames * frameSize)];
        long phase = 0;
        for (var frame = 0; frame < outputFrames; frame++)
        {
            var inputIndex = (int)(phase / outputSampleRate);
            var remainder = phase % outputSampleRate;
            var nextIndex = Math.Min(inputIndex + 1, inputFrames - 1);
            for (var channel = 0; channel < channels; channel++)
            {
                var first = pcm[inputIndex * channels + channel];
                var second = pcm[nextIndex * channels + channel];
                var numerator = (long)first * (outputSampleRate - remainder) + (long)second * remainder;
                var sample = FloorDivide(numerator, outputSampleRate);
                BinaryPrimitives.WriteInt16LittleEndian(
                    output.AsSpan((frame * channels + channel) * sizeof(short)),
                    (short)Math.Clamp(sample, short.MinValue, short.MaxValue));
            }

            phase += inputSampleRate;
        }

        return output;
    }

    public static AudioFrame Resample(AudioFrame frame, int outputSampleRate, bool normalize = false)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentOutOfRangeException.ThrowIfLessThan(outputSampleRate, 1);
        ValidateFiniteSamples(frame);
        if (frame.SampleRate == outputSampleRate)
        {
            return normalize ? Normalize(frame) : frame;
        }

        var inputFrames = frame.SamplesPerChannel;
        if (inputFrames < 2)
        {
            return new(Array.Empty<float>(), outputSampleRate, frame.Channels);
        }

        var outputFrames = checked((int)(((long)(inputFrames - 1) * outputSampleRate) / frame.SampleRate) + 1);
        var output = new float[checked(outputFrames * frame.Channels)];
        var input = frame.Samples.Span;
        var peak = 0;
        var pcm = new short[input.Length];
        for (var i = 0; i < input.Length; i++)
        {
            pcm[i] = ToPcm(input[i]);
            peak = Math.Max(peak, Math.Abs((int)pcm[i]));
        }

        var normalization = normalize && peak > 0 ? 32768.0 / peak : 1.0;
        for (var i = 0; i < pcm.Length; i++)
        {
            var scaled = (long)Math.Round(pcm[i] * normalization, MidpointRounding.AwayFromZero);
            pcm[i] = (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
        }

        long phase = 0;
        for (var sample = 0; sample < outputFrames; sample++)
        {
            var inputIndex = (int)(phase / outputSampleRate);
            var remainder = phase % outputSampleRate;
            var nextIndex = Math.Min(inputIndex + 1, inputFrames - 1);
            for (var channel = 0; channel < frame.Channels; channel++)
            {
                var first = pcm[inputIndex * frame.Channels + channel];
                var second = pcm[nextIndex * frame.Channels + channel];
                var numerator = (long)first * (outputSampleRate - remainder) + (long)second * remainder;
                output[sample * frame.Channels + channel] = (float)(numerator / outputSampleRate) / PcmScale;
            }

            phase += frame.SampleRate;
        }

        return new(output, outputSampleRate, frame.Channels);
    }

    private static AudioFrame Normalize(AudioFrame frame)
    {
        var input = frame.Samples.Span;
        var peak = 0;
        var pcm = new short[input.Length];
        for (var i = 0; i < input.Length; i++)
        {
            pcm[i] = ToPcm(input[i]);
            peak = Math.Max(peak, Math.Abs((int)pcm[i]));
        }

        if (peak == 0) return new(new float[input.Length], frame.SampleRate, frame.Channels);
        var scale = 32768.0 / peak;
        var output = new float[input.Length];
        for (var i = 0; i < pcm.Length; i++)
        {
            var value = (long)Math.Round(pcm[i] * scale, MidpointRounding.AwayFromZero);
            output[i] = (float)Math.Clamp(value, short.MinValue, short.MaxValue) / PcmScale;
        }

        return new(output, frame.SampleRate, frame.Channels);
    }

    private static short ToPcm(float sample)
    {
        var scaled = (long)Math.Truncate(sample * PcmScale);
        return (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
    }

    private static long FloorDivide(long numerator, int denominator)
    {
        var quotient = Math.DivRem(numerator, denominator, out var remainder);
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private static void ValidateFiniteSamples(AudioFrame frame)
    {
        foreach (var sample in frame.Samples.Span)
            if (!float.IsFinite(sample)) throw new ArgumentException("Audio samples must be finite.", nameof(frame));
    }
}

public abstract class AudioFilter
{
    public abstract AudioFrame Process(AudioFrame frame);

    protected static void ValidateCutoff(float cutoff)
    {
        if (!float.IsFinite(cutoff) || cutoff <= 0)
            throw new ArgumentOutOfRangeException(nameof(cutoff), "Cutoff must be finite and positive.");
    }

    protected static void ValidateFiniteSamples(AudioFrame frame)
    {
        foreach (var sample in frame.Samples.Span)
            if (!float.IsFinite(sample)) throw new ArgumentException("Audio samples must be finite.", nameof(frame));
    }
}

public sealed class HighPassFilter(float cutoffHz) : AudioFilter
{
    private float[] _states = [];
    private float[] _lastInputs = [];
    private int _sampleRate;

    public override AudioFrame Process(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ValidateCutoff(cutoffHz);
        ValidateFiniteSamples(frame);
        if (frame.Samples.IsEmpty) return frame;
        EnsureState(frame);

        var dt = 1f / frame.SampleRate;
        var rc = 1f / (2f * MathF.PI * cutoffHz);
        var alpha = rc / (rc + dt);
        var output = new float[frame.Samples.Length];
        var input = frame.Samples.Span;
        for (var channel = 0; channel < frame.Channels; channel++)
        {
            var previousOutput = _states[channel];
            var previousInput = _lastInputs[channel];
            for (var sample = 0; sample < frame.SamplesPerChannel; sample++)
            {
                var index = sample * frame.Channels + channel;
                var current = input[index];
                var inputDifference = current - previousInput;
                var filtered = alpha * (previousOutput + inputDifference);
                output[index] = filtered;
                previousInput = current;
                previousOutput = filtered;
            }

            _states[channel] = previousOutput;
            _lastInputs[channel] = previousInput;
        }

        return new(output, frame.SampleRate, frame.Channels);
    }

    private void EnsureState(AudioFrame frame)
    {
        if (_sampleRate == frame.SampleRate && _states.Length == frame.Channels) return;
        _sampleRate = frame.SampleRate;
        _states = new float[frame.Channels];
        _lastInputs = new float[frame.Channels];
    }
}

public sealed class LowPassFilter(float cutoffHz) : AudioFilter
{
    private float[] _states = [];
    private int _sampleRate;

    public override AudioFrame Process(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ValidateCutoff(cutoffHz);
        ValidateFiniteSamples(frame);
        if (frame.Samples.IsEmpty) return frame;
        if (_sampleRate != frame.SampleRate || _states.Length != frame.Channels)
        {
            _sampleRate = frame.SampleRate;
            _states = new float[frame.Channels];
        }

        var dt = 1f / frame.SampleRate;
        var rc = 1f / (2f * MathF.PI * cutoffHz);
        var alpha = dt / (rc + dt);
        var beta = 1f - alpha;
        var output = new float[frame.Samples.Length];
        var input = frame.Samples.Span;
        for (var channel = 0; channel < frame.Channels; channel++)
        {
            var previous = _states[channel];
            for (var sample = 0; sample < frame.SamplesPerChannel; sample++)
            {
                var index = sample * frame.Channels + channel;
                previous = alpha * input[index] + beta * previous;
                output[index] = previous;
            }

            _states[channel] = previous;
        }

        return new(output, frame.SampleRate, frame.Channels);
    }
}

public sealed class BandPassFilter(float lowCutHz, float highCutHz) : AudioFilter
{
    private readonly HighPassFilter _highPass = new(lowCutHz);
    private readonly LowPassFilter _lowPass = new(highCutHz);

    public override AudioFrame Process(AudioFrame frame)
    {
        if (lowCutHz >= highCutHz) throw new ArgumentOutOfRangeException(nameof(lowCutHz), "Low cutoff must be below high cutoff.");
        return _lowPass.Process(_highPass.Process(frame));
    }
}

public sealed class AutomaticGainControl(
    float targetLevelDb = -12f,
    float maximumGainDb = 12f,
    float attackSeconds = 0.0001f,
    float releaseSeconds = 0.002f,
    float holdSeconds = 0.001f) : AudioFilter
{
    private const float TriggerLevel = 0.003f;
    private const float PeakLimit = 0.75f;
    private float[] _gains = [];
    private int _sampleRate;
    private int _holdSamples;
    private int _holdCounter;
    private float _attackCoefficient;
    private float _releaseCoefficient;
    private int _blockTarget;

    public override AudioFrame Process(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ValidateFiniteSamples(frame);
        if (frame.Samples.IsEmpty) return frame;
        ValidateParameters();
        if (_sampleRate != frame.SampleRate)
        {
            _sampleRate = frame.SampleRate;
            _attackCoefficient = 1f - MathF.Exp(-1f / (attackSeconds * frame.SampleRate));
            _releaseCoefficient = 1f - MathF.Exp(-1f / (releaseSeconds * frame.SampleRate));
            _holdSamples = (int)(holdSeconds * frame.SampleRate);
            _blockTarget = (int)((frame.SamplesPerChannel / (float)frame.SampleRate) / 0.01f);
        }

        if (_gains.Length != frame.Channels)
        {
            _gains = Enumerable.Repeat(1f, frame.Channels).ToArray();
            _holdCounter = 0;
        }

        var output = frame.Samples.ToArray();
        var sampleCount = frame.SamplesPerChannel;
        var blockCount = Math.Max(_blockTarget, 1);
        var blockSize = Math.Max(sampleCount / blockCount, 1);
        var target = MathF.Pow(10f, targetLevelDb / 10f);
        var maximumGain = MathF.Pow(10f, maximumGainDb / 10f);
        for (var block = 0; block < blockCount; block++)
        {
            var start = block * blockSize;
            var end = block == blockCount - 1 ? sampleCount : Math.Min(start + blockSize, sampleCount);
            var count = end - start;
            if (count <= 0) continue;

            for (var channel = 0; channel < frame.Channels; channel++)
            {
                var sumSquares = 0f;
                for (var sample = start; sample < end; sample++)
                {
                    var value = output[sample * frame.Channels + channel];
                    sumSquares += value * value;
                }

                var rms = MathF.Sqrt(sumSquares / count);
                var targetGain = rms > 1e-9f && rms > TriggerLevel
                    ? Math.Min(target / rms, maximumGain)
                    : _gains[channel];
                if (targetGain < _gains[channel])
                {
                    _gains[channel] = _attackCoefficient * targetGain + (1f - _attackCoefficient) * _gains[channel];
                    _holdCounter = _holdSamples;
                }
                else if (_holdCounter > 0)
                {
                    _holdCounter -= count;
                }
                else
                {
                    _gains[channel] = _releaseCoefficient * targetGain + (1f - _releaseCoefficient) * _gains[channel];
                }

                for (var sample = start; sample < end; sample++)
                    output[sample * frame.Channels + channel] *= _gains[channel];
            }
        }

        for (var channel = 0; channel < frame.Channels; channel++)
        {
            var peak = 0f;
            for (var sample = 0; sample < sampleCount; sample++)
                peak = Math.Max(peak, Math.Abs(output[sample * frame.Channels + channel]));
            if (peak <= PeakLimit) continue;
            var scale = PeakLimit / peak;
            for (var sample = 0; sample < sampleCount; sample++)
                output[sample * frame.Channels + channel] *= scale;
        }

        return new(output, frame.SampleRate, frame.Channels);
    }

    private void ValidateParameters()
    {
        if (!float.IsFinite(targetLevelDb)) throw new ArgumentOutOfRangeException(nameof(targetLevelDb));
        if (!float.IsFinite(maximumGainDb)) throw new ArgumentOutOfRangeException(nameof(maximumGainDb));
        if (!float.IsFinite(attackSeconds) || attackSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(attackSeconds));
        if (!float.IsFinite(releaseSeconds) || releaseSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(releaseSeconds));
        if (!float.IsFinite(holdSeconds) || holdSeconds < 0) throw new ArgumentOutOfRangeException(nameof(holdSeconds));
    }
}
