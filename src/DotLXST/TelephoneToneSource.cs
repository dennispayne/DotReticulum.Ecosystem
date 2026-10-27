using System.Diagnostics;
using System.Runtime.CompilerServices;
using DotLXST.Audio;

namespace DotLXST;

public enum TelephoneTone { Ringback, Busy }

public sealed class TelephoneToneSource : IAudioSource
{
    private readonly TelephoneTone _tone;
    private readonly TimeSpan _busyDuration;
    private readonly int _sampleRate;
    private readonly int _channels;

    public TelephoneToneSource(TelephoneTone tone, TimeSpan? busyDuration = null, int sampleRate = 48000, int channels = 2)
    {
        if (!Enum.IsDefined(tone)) throw new ArgumentOutOfRangeException(nameof(tone));
        _tone = tone;
        _busyDuration = busyDuration ?? TimeSpan.FromSeconds(4.25);
        if (_busyDuration < TimeSpan.Zero || _busyDuration > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(busyDuration));
        _sampleRate = sampleRate;
        _channels = channels;
    }

    public static bool IsAudible(TelephoneTone tone, TimeSpan elapsed, TimeSpan? busyDuration = null)
    {
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        return tone switch
        {
            TelephoneTone.Ringback => elapsed.TotalSeconds % 7 is > .05 and < 2.05,
            TelephoneTone.Busy => elapsed < (busyDuration ?? TimeSpan.FromSeconds(4.25)) && elapsed.TotalSeconds % .5 > .25,
            _ => throw new ArgumentOutOfRangeException(nameof(tone)),
        };
    }

    public async IAsyncEnumerable<AudioFrame> CaptureAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_tone == TelephoneTone.Busy && _busyDuration == TimeSpan.Zero) yield break;
        var source = new ToneAudioSource(frequency: 382, gain: 0, easeDuration: TimeSpan.FromMilliseconds(3.14159),
            frameDuration: TimeSpan.FromMilliseconds(60), sampleRate: _sampleRate, channels: _channels);
        var clock = Stopwatch.StartNew();
        await using var frames = source.CaptureAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (_tone != TelephoneTone.Busy || clock.Elapsed < _busyDuration + TimeSpan.FromSeconds(.5))
        {
            source.Gain = IsAudible(_tone, clock.Elapsed, _busyDuration) ? .04f : 0;
            if (!await frames.MoveNextAsync().ConfigureAwait(false)) yield break;
            yield return frames.Current;
        }
    }
}
