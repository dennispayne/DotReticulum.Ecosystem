namespace DotLXST;

/// <summary>Initial capture skip and per-frame gain ramp, driven by a caller-owned monotonic clock.</summary>
public sealed class AudioCaptureEnvelope
{
    private readonly double _skipSeconds;
    private readonly double _easeSeconds;
    private readonly double _targetGain;
    private double _gain;
    private double _easeStarted;
    private double _lastElapsed;
    private bool _skipCompleted;
    private bool _easeCompleted;

    public AudioCaptureEnvelope(TimeSpan skip, TimeSpan easeIn, float gainDb = 0)
    {
        if (skip < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(skip));
        if (easeIn < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(easeIn));
        if (!float.IsFinite(gainDb) || Math.Abs(gainDb) > 120) throw new ArgumentOutOfRangeException(nameof(gainDb));
        _skipSeconds = skip.TotalSeconds;
        _easeSeconds = easeIn.TotalSeconds;
        _targetGain = Math.Pow(10, gainDb / 10.0);
        _gain = easeIn == TimeSpan.Zero ? _targetGain : 0;
        _skipCompleted = skip == TimeSpan.Zero;
        _easeCompleted = easeIn == TimeSpan.Zero;
    }

    public AudioFrame? Process(AudioFrame frame, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var seconds = elapsed.TotalSeconds;
        if (seconds < _lastElapsed) throw new ArgumentOutOfRangeException(nameof(elapsed), "Capture time must be monotonic.");
        _lastElapsed = seconds;
        if (!_skipCompleted)
        {
            if (seconds > _skipSeconds)
            {
                _skipCompleted = true;
                _easeStarted = seconds;
            }
            return null;
        }
        var samples = frame.Samples.ToArray();
        if (_gain != 1)
            for (var index = 0; index < samples.Length; index++) samples[index] *= (float)_gain;
        if (!_easeCompleted)
        {
            _gain = Math.Min((seconds - _easeStarted) / _easeSeconds * _targetGain, _targetGain);
            _easeCompleted = _gain >= _targetGain;
        }
        return new(samples, frame.SampleRate, frame.Channels);
    }
}
