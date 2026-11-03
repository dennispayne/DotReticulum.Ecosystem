namespace LXST;

public interface ITelephoneOutputControl
{
    string? SpeakerDevice { get; set; }
    string? LoudspeakerDevice { get; set; }
    ValueTask SelectLoudspeakerAsync(bool enabled, CancellationToken cancellationToken = default);
    ValueTask ResetAsync();
}

/// <summary>Caller-owned speaker/loudspeaker routing; selecting a route never opens an unused device.</summary>
public sealed class TelephoneAudioOutput : IAudioSink, ITelephoneOutputControl, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly DeviceAudioSink _sink;
    private readonly SemaphoreSlim _routes = new(1, 1);
    private readonly Audio.BufferedAudioSink? _buffer;
    private string? _speaker;
    private string? _loudspeaker;
    private bool _enabled;
    private bool _activated;
    private bool _disposed;
    private Task? _disposal;

    public TelephoneAudioOutput(IAudioDeviceBackend backend, string? speaker = null, string? loudspeaker = null,
        Action? firstFrame = null, bool bufferOutput = false)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _speaker = speaker;
        _loudspeaker = loudspeaker;
        _sink = new(backend, speaker, firstFrame);
        if (bufferOutput)
            _buffer = new(_sink, 48000, 2) { Error = (context, exception) => Report(context, exception) };
    }

    public Action<string, Exception?>? Error { get; set; }
    public void ConfigureProfile(TelephoneProfile profile) => _buffer?.ConfigureProfile(profile);

    public string? SpeakerDevice
    {
        get { lock (_gate) return _speaker; }
        set { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _speaker = value; } }
    }

    public string? LoudspeakerDevice
    {
        get { lock (_gate) return _loudspeaker; }
        set { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _loudspeaker = value; } }
    }

    public bool LoudspeakerEnabled { get { lock (_gate) return _enabled; } }

    public ValueTask RefreshDeviceAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new(ChangeRouteAsync(_enabled ? _loudspeaker : _speaker, cancellationToken));
        }
    }

    public ValueTask SelectLoudspeakerAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_enabled == enabled) return ValueTask.CompletedTask;
            _enabled = enabled;
            return new(ChangeRouteAsync(enabled ? _loudspeaker : _speaker, cancellationToken));
        }
    }

    private async Task ChangeRouteAsync(string? device, CancellationToken cancellationToken)
    {
        await _routes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_buffer is not null) await _buffer.ResetAsync().ConfigureAwait(false);
            await _sink.SetDeviceAsync(device, cancellationToken).ConfigureAwait(false);
        }
        finally { _routes.Release(); }
    }

    public ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_activated)
            {
                _sink.SetDevice(_enabled ? _loudspeaker : _speaker);
                _activated = true;
            }
            return _buffer is null ? _sink.WriteAsync(frame, cancellationToken) :
                _buffer.WriteAsync(frame.SampleRate == 48000 ? frame : Audio.AudioResampler.Resample(frame, 48000), cancellationToken);
        }
    }

    public ValueTask ResetAsync()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _enabled = false;
            _activated = false;
            return new(StopCoreAsync());
        }
    }

    private async Task StopCoreAsync()
    {
        await _routes.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_buffer is not null) await _buffer.ResetAsync().ConfigureAwait(false);
            await _sink.StopAsync().ConfigureAwait(false);
        }
        finally { _routes.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposal is not null) return new(_disposal);
            _disposed = true;
            return new(_disposal = DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _routes.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_buffer is not null) await _buffer.DisposeAsync().ConfigureAwait(false);
            await _sink.DisposeAsync().ConfigureAwait(false);
        }
        finally { _routes.Release(); _routes.Dispose(); }
    }

    private void Report(string context, Exception? exception)
    {
        try
        {
            if (Error is { } report) report(context, exception);
            else System.Diagnostics.Trace.TraceError($"{context} {exception}");
        }
        catch (Exception observer) { System.Diagnostics.Trace.TraceError($"{context} {exception}; error observer: {observer}"); }
    }
}
