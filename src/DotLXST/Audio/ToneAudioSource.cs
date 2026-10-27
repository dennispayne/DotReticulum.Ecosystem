using System.Runtime.CompilerServices;

namespace DotLXST.Audio;

public sealed class ToneAudioSource : IAudioSource
{
    private readonly TimeSpan _frameDuration;
    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly bool _ease;
    private readonly float _easeStep;
    private double _phase;
    private float _gain;
    private float _easeGain;
    private volatile bool _stopRequested;
    private volatile bool _running;

    public ToneAudioSource(
        float frequency = 400,
        float gain = 0.1f,
        bool ease = true,
        TimeSpan? easeDuration = null,
        TimeSpan? frameDuration = null,
        int sampleRate = 48_000,
        int channels = 1)
    {
        if (!float.IsFinite(frequency) || frequency <= 0 || frequency >= sampleRate / 2f)
            throw new ArgumentOutOfRangeException(nameof(frequency));
        if (!float.IsFinite(gain) || gain < 0) throw new ArgumentOutOfRangeException(nameof(gain));
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, 32);
        _frameDuration = frameDuration ?? TimeSpan.FromMilliseconds(80);
        var easeTime = easeDuration ?? TimeSpan.FromMilliseconds(20);
        if (_frameDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(frameDuration));
        if (easeTime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(easeDuration));
        if (_frameDuration.TotalSeconds * sampleRate > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(frameDuration));
        Frequency = frequency;
        Gain = gain;
        _gain = gain;
        _ease = ease;
        _easeGain = ease ? 0f : 1f;
        _easeStep = (float)(1.0 / (sampleRate * easeTime.TotalSeconds));
        _sampleRate = sampleRate;
        _channels = channels;
    }

    public float Frequency { get; }
    public float Gain { get; }
    public bool IsRunning => _running && !_stopRequested;

    public void Stop() => _stopRequested = true;

    public async IAsyncEnumerable<AudioFrame> CaptureAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_running) throw new InvalidOperationException("The tone source is already running.");
        _running = true;
        var samplesPerFrame = (int)Math.Ceiling(_frameDuration.TotalSeconds * _sampleRate);
        var frameTime = TimeSpan.FromSeconds((double)samplesPerFrame / _sampleRate);
        try
        {
            while (!_stopRequested || (_ease && _easeGain > 0f))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var samples = new float[checked(samplesPerFrame * _channels)];
                var phaseStep = 2.0 * Math.PI * Frequency / _sampleRate;
                for (var sample = 0; sample < samplesPerFrame; sample++)
                {
                    _phase += phaseStep;
                    var value = (float)Math.Sin(_phase) * _gain * _easeGain;
                    for (var channel = 0; channel < _channels; channel++)
                        samples[sample * _channels + channel] = value;

                    if (!_ease) continue;
                    if (!_stopRequested && _easeGain < 1f)
                        _easeGain = Math.Min(1f, _easeGain + _easeStep);
                    else if (_stopRequested && _easeGain > 0f)
                        _easeGain = Math.Max(0f, _easeGain - _easeStep);
                }

                yield return new(samples, _sampleRate, _channels);
                await Task.Delay(frameTime, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _running = false;
        }
    }
}
