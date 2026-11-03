using System.Numerics;

namespace LXST.Audio;

public sealed record EchoSuppressorOptions
{
    public double MaximumDelayMilliseconds { get; init; } = 550;
    public double TrackingWindowMilliseconds { get; init; } = 150;
    public double CorrelationFrameMilliseconds { get; init; } = 120;
    public double CorrelationThreshold { get; init; } = .070;
    public double RmsThreshold { get; init; } = .002;
    public double DelaySmoothing { get; init; } = .2;
    public double Preemphasis { get; init; } = .95;
    public double CorrelationRetention { get; init; } = .92;
    public double CouplingWindowSeconds { get; init; } = 5;
    public double CouplingPercentile { get; init; } = 15;
    public double CouplingThresholdDb { get; init; } = -30;
    public double GateRatioDb { get; init; } = -3;
    public double EchoCorrelationThreshold { get; init; } = .13;
    public double DoubleTalkEnergyDb { get; init; } = 3;
    public double HangoverMilliseconds { get; init; } = 150;
    public double AttackMilliseconds { get; init; } = 5;
    public double ReleaseMilliseconds { get; init; } = 80;
    public double ReferenceRmsThreshold { get; init; } = .0001;
    public int EstimateEveryFrames { get; init; } = 2;
    public bool ComfortNoiseEnabled { get; init; } = true;
    public double ComfortNoiseGain { get; init; } = .0015;
    public double ComfortNoiseColor { get; init; } = .98;
}

public sealed record EchoSuppressionState(double? DelaySamples, double? DelayMilliseconds, double Confidence,
    double CouplingDb, double Gain, double SignalToEchoDb, int ReferenceSamples, long ProcessedFrames);

public interface IAudioReferenceReceiver
{
    void HandleReference(AudioFrame frame);
}

/// <summary>Bounded, headless echo detection and gain gating; it does not subtract an estimated waveform.</summary>
public sealed class EchoSuppressor : AudioFilter, IAudioReferenceReceiver, IDisposable
{
    private sealed class History(int capacity)
    {
        private readonly float[] _samples = new float[Math.Max(1, capacity)];
        private int _next;
        public int Count { get; private set; }

        public void Add(ReadOnlySpan<float> samples)
        {
            if (samples.Length > _samples.Length) samples = samples[^_samples.Length..];
            foreach (var value in samples)
            {
                _samples[_next] = value;
                _next = (_next + 1) % _samples.Length;
            }
            Count = Math.Min(_samples.Length, Count + samples.Length);
        }

        public float[] Tail(int length, int skip = 0)
        {
            length = Math.Min(length, Count - skip);
            if (length <= 0) return [];
            var result = new float[length];
            var start = (_next - length - skip + 2 * _samples.Length) % _samples.Length;
            for (var index = 0; index < result.Length; index++)
                result[index] = _samples[(start + index) % _samples.Length];
            return result;
        }

        public void Clear() => Array.Clear(_samples);
    }

    private sealed class AnalysisInput
    {
        private readonly float[] _firHistory = new float[11];
        private long _position;
        private float _previousFull;
        private float _previousReduced;

        public (float[] Full, float[] Reduced) Analyze(float[] mono, float emphasis)
        {
            var joined = new float[mono.Length + _firHistory.Length];
            _firHistory.CopyTo(joined, 0);
            mono.CopyTo(joined, _firHistory.Length);
            var first = (int)((6 - _position % 6) % 6);
            var reduced = new float[Math.Max(0, (mono.Length - first + 5) / 6)];
            for (var output = 0; output < reduced.Length; output++)
            {
                float sum = 0;
                var start = first + output * 6;
                for (var tap = 0; tap < Decimator.Length; tap++)
                    sum += joined[start + tap] * Decimator[Decimator.Length - 1 - tap];
                reduced[output] = sum;
            }
            joined.AsSpan(joined.Length - 11).CopyTo(_firHistory);
            _position += mono.Length;
            var full = Emphasize(mono, emphasis, ref _previousFull);
            return (full, Emphasize(reduced, emphasis, ref _previousReduced));
        }

        public void ResetReduced()
        {
            Array.Clear(_firHistory);
            _position = 0;
            _previousReduced = 0;
        }

        public void Clear()
        {
            ResetReduced();
            _previousFull = 0;
        }

        private static float[] Emphasize(float[] samples, float amount, ref float previous)
        {
            var result = new float[samples.Length];
            for (var index = 0; index < samples.Length; index++)
            {
                result[index] = samples[index] - amount * previous;
                previous = samples[index];
            }
            return result;
        }
    }

    private sealed class AnalysisWindow(int rate, EchoSuppressorOptions options)
    {
        public int Rate { get; } = rate;
        public int ReducedRate { get; } = rate / 6;
        public int CorrelationSamples { get; } = Math.Max(1, (int)(options.CorrelationFrameMilliseconds / 1000 * (rate / 6)));
        public int TrackingSamples { get; } = (int)(options.TrackingWindowMilliseconds / 1000 * (rate / 6));
        public History Reference { get; } = new((int)(options.MaximumDelayMilliseconds / 1000 * rate) + 16384);
        public History ReducedReference { get; } = new((int)(options.MaximumDelayMilliseconds / 1000 * (rate / 6)) + 4096);
        public History Microphone { get; } = new(Math.Max(1, (int)(options.CorrelationFrameMilliseconds / 1000 * (rate / 6))));
        public double[] Scores { get; } = new double[(int)(options.MaximumDelayMilliseconds / 1000 * (rate / 6)) + 1];
        public void Clear()
        {
            Reference.Clear();
            ReducedReference.Clear();
            Microphone.Clear();
            Array.Clear(Scores);
        }
    }

    private static readonly float[] Decimator = CreateDecimator();
    private readonly object _gate = new();
    private readonly EchoSuppressorOptions _options;
    private readonly TimeProvider _clock;
    private readonly Func<float> _normalSample;
    private readonly AnalysisInput _referenceInput = new();
    private readonly AnalysisInput _microphoneInput = new();
    private readonly Queue<double> _couplingRatios = new(10);
    private AnalysisWindow? _window;
    private Complex[] _referenceSpectrum = [];
    private Complex[] _microphoneSpectrum = [];
    private float[] _noise = [];
    private int _noisePosition;
    private double _noiseMemory;
    private double? _delay;
    private double? _delayMilliseconds;
    private double _confidence;
    private double _coupling = .001;
    private double _couplingDb = -30;
    private double _gain = 1;
    private double _signalToEchoDb;
    private double _lastCorrelation;
    private double _nearEndUntil;
    private int _nearEndCount;
    private int _hangover;
    private long _frames;
    private bool _weakCoupling;
    private bool _disposed;

    public EchoSuppressor(EchoSuppressorOptions? options = null, TimeProvider? timeProvider = null,
        Func<float>? normalSample = null)
    {
        _options = options ?? new();
        Validate(_options);
        _clock = timeProvider ?? TimeProvider.System;
        _normalSample = normalSample ?? NextNormalSample;
    }

    public EchoSuppressionState State
    {
        get { lock (_gate) return new(_delay, _delayMilliseconds, _confidence, _couplingDb, _gain, _signalToEchoDb,
            _window?.Reference.Count ?? 0, _frames); }
    }

    public void HandleReference(AudioFrame frame)
    {
        ValidateFrame(frame);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var analysis = _referenceInput.Analyze(Mono(frame), (float)_options.Preemphasis);
            if (_window is null || _window.Rate != frame.SampleRate)
            {
                _window?.Clear();
                _window = new(frame.SampleRate, _options);
                // Executed primary observations retain full-rate emphasis/delay,
                // but restart reduced-rate analysis when reference buffers change.
                _referenceInput.ResetReduced();
                _microphoneInput.ResetReduced();
                _couplingRatios.Clear();
                _coupling = .001;
                _couplingDb = -30;
                _weakCoupling = false;
                _gain = 1;
                _hangover = 0;
            }
            _window.Reference.Add(analysis.Full);
            _window.ReducedReference.Add(analysis.Reduced);
        }
    }

    public override AudioFrame Process(AudioFrame frame)
    {
        ValidateFrame(frame);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (frame.Samples.IsEmpty) return frame;
            var analysis = _microphoneInput.Analyze(Mono(frame), (float)_options.Preemphasis);
            _frames++;
            var window = _window;
            if (window is null || window.Reference.Count < frame.SamplesPerChannel + 100 ||
                window.Rate != frame.SampleRate) return frame;
            window.Microphone.Add(analysis.Reduced);
            if (window.Microphone.Count < window.CorrelationSamples / 2) return frame;
            if (_frames % _options.EstimateEveryFrames == 0 && !EstimateDelay(window)) return frame;
            if (_delay is not { } delay) return frame;
            var offset = (int)Math.Round(delay);
            if (offset + frame.SamplesPerChannel > window.Reference.Count) return frame;
            var reference = window.Reference.Tail(frame.SamplesPerChannel, offset);
            if (!UpdateGate(analysis.Full, reference, frame.SampleRate)) return frame;
            return Render(frame);
        }
    }

    private bool EstimateDelay(AnalysisWindow window)
    {
        var microphone = window.Microphone.Tail(window.CorrelationSamples);
        var reference = window.ReducedReference.Tail(window.Scores.Length - 1 + microphone.Length);
        if (reference.Length <= microphone.Length) return false;
        var microphoneNorm = Norm(microphone);
        if (microphoneNorm == 0) return false;
        var length = 1;
        while (length < reference.Length + microphone.Length) length <<= 1;
        if (_referenceSpectrum.Length != length)
        {
            _referenceSpectrum = new Complex[length];
            _microphoneSpectrum = new Complex[length];
        }
        Array.Clear(_referenceSpectrum);
        Array.Clear(_microphoneSpectrum);
        for (var index = 0; index < reference.Length; index++) _referenceSpectrum[index] = reference[index];
        for (var index = 0; index < microphone.Length; index++) _microphoneSpectrum[index] = microphone[index];
        Transform(_referenceSpectrum, false);
        Transform(_microphoneSpectrum, false);
        for (var index = 0; index < length; index++)
            _referenceSpectrum[index] *= Complex.Conjugate(_microphoneSpectrum[index]);
        Transform(_referenceSpectrum, true);

        var powers = new double[reference.Length + 1];
        for (var index = 0; index < reference.Length; index++)
            powers[index + 1] = powers[index] + reference[index] * reference[index];
        for (var index = 0; index < window.Scores.Length; index++)
            window.Scores[index] *= _options.CorrelationRetention;
        var count = reference.Length - microphone.Length + 1;
        for (var index = 0; index < count; index++)
        {
            var norm = Math.Sqrt(Math.Max(0, powers[index + microphone.Length] - powers[index]));
            var correlation = norm == 0 ? 0 : Math.Abs(_referenceSpectrum[index].Real / (microphoneNorm * norm));
            window.Scores[count - 1 - index] += correlation;
        }

        var lower = 0;
        var upper = window.Scores.Length - 1;
        if (_delay is { } previous)
        {
            var center = (int)Math.Round(previous / 6);
            lower = Math.Max(0, center - window.TrackingSamples);
            upper = Math.Min(upper, center + window.TrackingSamples);
        }
        if (lower > upper) { lower = 0; upper = window.Scores.Length - 1; }
        var peak = MaximumIndex(window.Scores, lower, upper);
        var confidence = window.Scores[peak] * (1 - _options.CorrelationRetention);
        if (confidence < _options.CorrelationThreshold)
        {
            peak = MaximumIndex(window.Scores, 0, window.Scores.Length - 1);
            confidence = window.Scores[peak] * (1 - _options.CorrelationRetention);
        }
        if (confidence >= _options.CorrelationThreshold)
        {
            if (_delay is null)
            {
                _delay = peak * 6d;
                _delayMilliseconds = peak * 1000d / window.ReducedRate;
                _confidence = confidence;
            }
            else
            {
                _delay = _options.DelaySmoothing * peak * 6 + (1 - _options.DelaySmoothing) * _delay.Value;
                _delayMilliseconds = _delay * 1000 / window.Rate;
                _confidence = _options.DelaySmoothing * confidence + (1 - _options.DelaySmoothing) * _confidence;
            }
        }
        return true;
    }

    private bool UpdateGate(float[] microphone, float[] reference, int rate)
    {
        var microphoneEnergy = Energy(microphone);
        var referenceEnergy = Energy(reference);
        var product = Dot(microphone, reference);
        var norms = Norm(microphone) * Norm(reference);
        var correlation = norms > 1e-24 ? Math.Abs(product / norms) : 0;
        var now = (_clock.GetUtcNow() - DateTimeOffset.UnixEpoch).TotalSeconds;
        var predicted = _coupling * referenceEnergy;
        if (correlation > _options.EchoCorrelationThreshold) _lastCorrelation = now;
        var echo = now < _lastCorrelation + 1;
        if (microphoneEnergy > 2e-6)
            _signalToEchoDb = 10 * Math.Log10(microphoneEnergy / predicted + 1e-12);
        var nearEnd = _nearEndUntil > now;
        if (!nearEnd && _couplingRatios.Count < 4)
            nearEnd = Math.Max(0, microphoneEnergy - predicted) > 2e-6 && correlation < _options.EchoCorrelationThreshold;
        else if (!nearEnd)
        {
            nearEnd = now > _lastCorrelation + 1;
            if (_signalToEchoDb > 11)
            {
                if (++_nearEndCount >= 2)
                {
                    nearEnd = true;
                    _nearEndUntil = now + .75;
                }
            }
            else _nearEndCount = 0;
        }

        if (echo && referenceEnergy > 6e-7 && microphoneEnergy > 6e-7 &&
            correlation > _options.EchoCorrelationThreshold)
        {
            if (_couplingRatios.Count == 10) _couplingRatios.Dequeue();
            _couplingRatios.Enqueue(microphoneEnergy / referenceEnergy);
            if (_couplingRatios.Count >= 4)
            {
                var sorted = _couplingRatios.Order().ToArray();
                var position = (sorted.Length - 1) * _options.CouplingPercentile / 100;
                var left = (int)position;
                _coupling = sorted[left] + (sorted[Math.Min(left + 1, sorted.Length - 1)] - sorted[left]) * (position - left);
                _couplingDb = 10 * Math.Log10(_coupling + 1e-12);
                if (_couplingDb < _options.CouplingThresholdDb) _weakCoupling = true;
                else if (_couplingDb > _options.CouplingThresholdDb + 1.5) _weakCoupling = false;
            }
        }
        if (_weakCoupling || _couplingRatios.Count < 4) return false;
        var target = 1d;
        if (nearEnd) _hangover = 0;
        else if (echo)
        {
            target = 0;
            _hangover = (int)(_options.HangoverMilliseconds / 1000 * rate);
        }
        else if (_hangover > 0)
        {
            target = 0;
            _hangover -= microphone.Length;
        }
        var duration = microphone.Length * 1000d / rate;
        var smoothing = target < _gain ? _options.AttackMilliseconds : _options.ReleaseMilliseconds;
        _gain += (target - _gain) * (1 - Math.Exp(-duration / smoothing));
        if (_gain < 1e-6) _gain = 0;
        else if (_gain > 1) _gain = 1;
        return true;
    }

    private AudioFrame Render(AudioFrame frame)
    {
        var output = frame.Samples.ToArray();
        var gain = (float)_gain;
        for (var index = 0; index < output.Length; index++) output[index] *= gain;
        if (_options.ComfortNoiseEnabled && _gain < .05)
        {
            var count = frame.SamplesPerChannel;
            for (var start = 0; start < count;)
            {
                var length = Math.Min(16384, count - start);
                if (_noisePosition + length > _noise.Length)
                {
                    _noise = new float[16384];
                    _noisePosition = 0;
                    var color = Math.Clamp(_options.ComfortNoiseColor, 0, .999);
                    var scale = _options.ComfortNoiseGain * Math.Sqrt(1 - color * color);
                    for (var index = 0; index < _noise.Length; index++)
                    {
                        var white = _normalSample();
                        if (!float.IsFinite(white)) throw new InvalidOperationException("The normal-noise source returned a non-finite value.");
                        _noiseMemory = color * _noiseMemory + white;
                        _noise[index] = (float)(_noiseMemory * scale);
                    }
                }
                for (var sample = 0; sample < length; sample++)
                    for (var channel = 0; channel < frame.Channels; channel++)
                    {
                        var index = (start + sample) * frame.Channels + channel;
                        var value = output[index] + _noise[_noisePosition + sample];
                        output[index] = frame.Channels == 1 ? value : Math.Clamp(value, -1, 1);
                    }
                _noisePosition += length;
                start += length;
            }
        }
        return new(output, frame.SampleRate, frame.Channels);
    }

    private static float[] Mono(AudioFrame frame)
    {
        var input = frame.Samples.Span;
        var result = new float[frame.SamplesPerChannel];
        for (var index = 0; index < result.Length; index++)
            result[index] = frame.Channels == 1 ? input[index] :
                (input[index * frame.Channels] + input[index * frame.Channels + 1]) * .5f;
        return result;
    }

    private static float Dot(float[] left, float[] right)
    {
        double result = 0;
        for (var index = 0; index < left.Length; index++) result += left[index] * right[index];
        return (float)result;
    }

    private static float Norm(float[] samples) => MathF.Sqrt(Dot(samples, samples));
    private static float Energy(float[] samples) => Dot(samples, samples) / samples.Length;

    private static int MaximumIndex(double[] values, int first, int last)
    {
        var result = first;
        for (var index = first + 1; index <= last; index++)
            if (values[index] > values[result]) result = index;
        return result;
    }

    private static void Transform(Complex[] values, bool inverse)
    {
        for (int index = 1, reverse = 0; index < values.Length; index++)
        {
            var bit = values.Length >> 1;
            for (; (reverse & bit) != 0; bit >>= 1) reverse ^= bit;
            reverse ^= bit;
            if (index < reverse) (values[index], values[reverse]) = (values[reverse], values[index]);
        }
        for (var size = 2; size <= values.Length; size <<= 1)
        {
            var angle = (inverse ? 2 : -2) * Math.PI / size;
            var step = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var block = 0; block < values.Length; block += size)
            {
                var phase = Complex.One;
                for (var offset = 0; offset < size / 2; offset++)
                {
                    var left = values[block + offset];
                    var right = values[block + offset + size / 2] * phase;
                    values[block + offset] = left + right;
                    values[block + offset + size / 2] = left - right;
                    phase *= step;
                }
            }
        }
        if (inverse)
            for (var index = 0; index < values.Length; index++) values[index] /= values.Length;
    }

    private static float[] CreateDecimator()
    {
        var taps = new double[12];
        for (var index = 0; index < taps.Length; index++)
        {
            var position = index - 5.5;
            var angle = 2 * Math.PI * 3700 / 48000 * position;
            taps[index] = 2 * 3700d / 48000 * Math.Sin(angle) / angle *
                (.54 - .46 * Math.Cos(2 * Math.PI * index / 11));
        }
        var sum = taps.Sum();
        return taps.Select(tap => (float)(tap / sum)).ToArray();
    }

    private static float NextNormalSample() =>
        (float)(Math.Sqrt(-2 * Math.Log(1 - Random.Shared.NextDouble())) * Math.Cos(2 * Math.PI * Random.Shared.NextDouble()));

    private static void Validate(EchoSuppressorOptions options)
    {
        var values = new[] { options.MaximumDelayMilliseconds, options.TrackingWindowMilliseconds,
            options.CorrelationFrameMilliseconds, options.CorrelationThreshold, options.RmsThreshold,
            options.DelaySmoothing, options.Preemphasis, options.CorrelationRetention, options.CouplingWindowSeconds,
            options.CouplingPercentile, options.CouplingThresholdDb, options.GateRatioDb, options.EchoCorrelationThreshold,
            options.DoubleTalkEnergyDb, options.HangoverMilliseconds, options.AttackMilliseconds, options.ReleaseMilliseconds,
            options.ReferenceRmsThreshold, options.ComfortNoiseGain, options.ComfortNoiseColor };
        if (values.Any(value => !double.IsFinite(value)) ||
            options.MaximumDelayMilliseconds is < 0 or > 2000 ||
            options.TrackingWindowMilliseconds is < 0 or > 2000 ||
            options.CorrelationFrameMilliseconds is <= 0 or > 2000 ||
            options.CorrelationThreshold is < 0 or > 1 || options.DelaySmoothing is < 0 or > 1 ||
            options.Preemphasis is < 0 or > 1 || options.CorrelationRetention is < 0 or >= 1 ||
            options.CouplingPercentile is < 0 or > 100 || options.CouplingWindowSeconds <= 0 ||
            options.EchoCorrelationThreshold is < 0 or > 1 || options.HangoverMilliseconds is < 0 or > 10000 ||
            options.AttackMilliseconds <= 0 || options.ReleaseMilliseconds <= 0 ||
            options.ComfortNoiseGain < 0 || options.EstimateEveryFrames is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(options), "Echo parameters must be finite and within bounded processing limits.");
    }

    private static void ValidateFrame(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.SampleRate is < 6 or > 384000 || frame.SamplesPerChannel > frame.SampleRate)
            throw new ArgumentOutOfRangeException(nameof(frame), "Echo frames support rates 6–384000 Hz and at most one second of samples.");
        ValidateFiniteSamples(frame);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _window?.Clear();
            _referenceInput.Clear();
            _microphoneInput.Clear();
            Array.Clear(_referenceSpectrum);
            Array.Clear(_microphoneSpectrum);
            Array.Clear(_noise);
            _couplingRatios.Clear();
        }
    }
}
