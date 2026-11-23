using System.Diagnostics;
using LXST;
using LXST.Audio;

namespace Sideband;

/// <summary>
/// Original synthesized notification, not Sideband's licensed soft1.opus sound.
/// Keeps the reference's delayed incoming-only trigger, with joined native device ownership.
/// </summary>
internal sealed class SidebandVoiceRinger(IAudioDeviceBackend devices, Action<string, Exception?> report) : IAsyncDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _stop;
    private Task _playback = Task.CompletedTask;
    private Task? _disposal;
    private bool _disposed;

    public void Start(string? device, CancellationToken callStop)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var previous = _playback;
            var previousStop = _stop;
            previousStop?.Cancel();
            _stop = CancellationTokenSource.CreateLinkedTokenSource(callStop);
            var token = _stop.Token;
            _playback = Task.Run(() => PlayAsync(previous, previousStop, device, token));
        }
    }

    public void Stop()
    {
        lock (_gate) _stop?.Cancel();
    }

    public Task StopAsync()
    {
        lock (_gate) { _stop?.Cancel(); return _playback; }
    }

    private async Task PlayAsync(Task previous, CancellationTokenSource? previousStop, string? device, CancellationToken token)
    {
        try
        {
            await previous.ConfigureAwait(false);
            previousStop?.Dispose();
            await Task.Delay(SidebandVoiceService.RingerDelay, token).ConfigureAwait(false);
            await using var sink = new DeviceAudioSink(devices, device);
            var tone = new ToneAudioSource(frequency: 660, gain: .05f, frameDuration: TimeSpan.FromMilliseconds(60),
                sampleRate: 48000, channels: 2);
            var clock = Stopwatch.StartNew();
            await foreach (var frame in tone.CaptureAsync(token).ConfigureAwait(false))
            {
                // Deliberately original sound: a short 660 Hz pulse every two seconds.
                tone.Gain = clock.Elapsed.TotalSeconds % 2 < .6 ? .05f : 0;
                await sink.WriteAsync(frame, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { report("Voice ringer failed.", exception); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposal is not null) return new(_disposal);
            _disposed = true;
            _stop?.Cancel();
            return new(_disposal = FinishAsync(_playback));
        }
    }

    private async Task FinishAsync(Task playback)
    {
        await playback.ConfigureAwait(false);
        lock (_gate) { _stop?.Dispose(); _stop = null; }
    }
}
