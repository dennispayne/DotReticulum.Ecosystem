namespace LXST.Devices;

public enum WasapiShareMode { Shared, Exclusive }

public sealed record WindowsWasapiOptions
{
    public WasapiShareMode ShareMode { get; init; }
    public bool IncludeLoopbackInputs { get; init; }
    public IReadOnlyList<int>? ChannelMap { get; init; }
}

/// <summary>Original float32 WASAPI provider, independent of Python's platform bindings.</summary>
public sealed class WindowsWasapiAudioBackend : IAudioDeviceBackend
{
    private readonly WindowsWaveAudioBackend _backend;
    private readonly WindowsWasapiDriver? _native;

    public WindowsWasapiAudioBackend(WindowsWasapiOptions? options = null)
        : this(new WindowsWasapiDriver(options ?? new())) { }

    internal WindowsWasapiAudioBackend(IWindowsWaveDriver driver)
    {
        _backend = new(driver, 32, 384000);
        _native = driver as WindowsWasapiDriver;
    }

    public ValueTask<bool> SupportsPlaybackFormatAsync(AudioDeviceFormat format, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        format.Validate();
        return ValueTask.FromResult((_native ?? throw new InvalidOperationException("No native WASAPI driver.")).SupportsPlaybackFormat(format));
    }

    public ValueTask<IReadOnlyList<AudioDeviceInfo>> ListInputsAsync(CancellationToken cancellationToken = default) =>
        _backend.ListInputsAsync(cancellationToken);
    public ValueTask<IReadOnlyList<AudioDeviceInfo>> ListOutputsAsync(CancellationToken cancellationToken = default) =>
        _backend.ListOutputsAsync(cancellationToken);
    public ValueTask<IAudioCapture> OpenCaptureAsync(string? device, AudioDeviceFormat format, CancellationToken cancellationToken = default) =>
        _backend.OpenCaptureAsync(device, format, cancellationToken);
    public ValueTask<IAudioPlayback> OpenPlaybackAsync(string? device, AudioDeviceFormat format, CancellationToken cancellationToken = default) =>
        _backend.OpenPlaybackAsync(device, format, cancellationToken);
    public ValueTask<IAudioCapture> OpenLoopbackCaptureAsync(string? output, AudioDeviceFormat format,
        CancellationToken cancellationToken = default) =>
        OpenCaptureAsync("loopback:" + (output ?? "default"), format, cancellationToken);
}

internal sealed class WasapiChannelMap
{
    private readonly int[] _map;
    internal WasapiChannelMap(int channels, IReadOnlyList<int>? map, bool capture)
    {
        _map = map?.ToArray() ?? Enumerable.Range(0, channels).ToArray();
        if (_map.Length != channels || _map.Length == 0 || _map.Any(channel => channel is < 0 or > 31))
            throw new ArgumentException("The channel map must contain one index per logical channel, from 0 to 31.");
        NativeChannels = _map.Max() + 1;
        if (!_map.Distinct().Order().SequenceEqual(Enumerable.Range(0, NativeChannels)))
            throw new ArgumentException("The reference channel map must cover contiguous native channels starting at zero.");
        if (!capture && _map.Distinct().Count() != _map.Length)
            throw new ArgumentException("The reference playback path does not support repeated channel indices.");
    }
    internal int NativeChannels { get; }
    internal uint Mask => NativeChannels == 32 ? uint.MaxValue : (1u << NativeChannels) - 1;
    internal float[] Capture(ReadOnlySpan<float> samples)
    {
        var result = new float[samples.Length / NativeChannels * _map.Length];
        for (var frame = 0; frame < samples.Length / NativeChannels; frame++)
            for (var channel = 0; channel < _map.Length; channel++)
                result[frame * _map.Length + channel] = samples[frame * NativeChannels + _map[channel]];
        return result;
    }
    internal void Playback(ReadOnlySpan<float> samples, Span<float> output)
    {
        for (var frame = 0; frame < samples.Length / _map.Length; frame++)
            for (var channel = 0; channel < _map.Length; channel++)
                output[frame * NativeChannels + _map[channel]] = samples[frame * _map.Length + channel];
    }
}
