using System.Runtime.InteropServices;

namespace LXST;

/// <summary>Original libopus ABI adapter. The caller supplies and licenses the native library separately.</summary>
public sealed unsafe class NativeOpusCodec : IAudioCodec
{
    private readonly object _gate = new();
    private readonly nint _library;
    private readonly delegate* unmanaged[Cdecl]<int, int, int, int*, nint> _createEncoder;
    private readonly delegate* unmanaged[Cdecl]<int, int, int*, nint> _createDecoder;
    private readonly delegate* unmanaged[Cdecl]<nint, short*, int, byte*, int, int> _encode;
    private readonly delegate* unmanaged[Cdecl]<nint, byte*, int, short*, int, int, int> _decode;
    private readonly delegate* unmanaged[Cdecl]<nint, void> _destroyEncoder;
    private readonly delegate* unmanaged[Cdecl]<nint, void> _destroyDecoder;
    private nint _encoder;
    private nint _decoder;
    private bool _disposed;
    private readonly int _decodeRate;
    private readonly int _decodeChannels;
    private OpusParameters _parameters;
    private bool _encoded;
    private long _outputBytes;
    private double _outputMilliseconds;

    public NativeOpusCodec(OpusProfile profile = OpusProfile.VoiceLow, string? libraryPath = null,
        int? decodeSampleRate = null, int? decodeChannels = null)
    {
        _parameters = OpusParameters.For(profile);
        _decodeRate = decodeSampleRate ?? Parameters.SampleRate;
        _decodeChannels = decodeChannels ?? Parameters.Channels;
        if (_decodeRate is not (8000 or 12000 or 16000 or 24000 or 48000)) throw new ArgumentOutOfRangeException(nameof(decodeSampleRate));
        if (_decodeChannels is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(decodeChannels));
        _library = NativeLibrary.Load(libraryPath ?? (OperatingSystem.IsWindows() ? "opus.dll" :
            OperatingSystem.IsMacOS() ? "libopus.0.dylib" : "libopus.so.0"));
        try
        {
            _createEncoder = (delegate* unmanaged[Cdecl]<int, int, int, int*, nint>)NativeLibrary.GetExport(_library, "opus_encoder_create");
            _createDecoder = (delegate* unmanaged[Cdecl]<int, int, int*, nint>)NativeLibrary.GetExport(_library, "opus_decoder_create");
            _encode = (delegate* unmanaged[Cdecl]<nint, short*, int, byte*, int, int>)NativeLibrary.GetExport(_library, "opus_encode");
            _decode = (delegate* unmanaged[Cdecl]<nint, byte*, int, short*, int, int, int>)NativeLibrary.GetExport(_library, "opus_decode");
            _destroyEncoder = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(_library, "opus_encoder_destroy");
            _destroyDecoder = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(_library, "opus_decoder_destroy");
            var error = 0;
            _encoder = _createEncoder(Parameters.SampleRate, Parameters.Channels, Parameters.Voice ? 2048 : 2049, &error);
            if (error != 0 || _encoder == 0) throw new InvalidOperationException($"libopus encoder creation failed: {error}.");
            _decoder = _createDecoder(_decodeRate, _decodeChannels, &error);
            if (error != 0 || _decoder == 0) throw new InvalidOperationException($"libopus decoder creation failed: {error}.");
        }
        catch
        {
            if (_encoder != 0) _destroyEncoder(_encoder);
            NativeLibrary.Free(_library);
            throw;
        }
    }

    public CodecKind Kind => CodecKind.Opus;
    public OpusParameters Parameters { get { lock (_gate) return _parameters; } }
    public long OutputBytes { get { lock (_gate) return _outputBytes; } }
    public double OutputMilliseconds { get { lock (_gate) return _outputMilliseconds; } }
    public double OutputBitrate { get { lock (_gate) return _outputMilliseconds == 0 ? 0 : _outputBytes * 8000 / _outputMilliseconds; } }

    public void SetProfile(OpusProfile profile)
    {
        var parameters = OpusParameters.For(profile);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_encoded) throw new InvalidOperationException("The reference encoder cannot change its application after encoding.");
            var error = 0;
            var replacement = _createEncoder(parameters.SampleRate, parameters.Channels, parameters.Voice ? 2048 : 2049, &error);
            if (error != 0 || replacement == 0) throw new InvalidOperationException($"libopus encoder creation failed: {error}.");
            _destroyEncoder(_encoder);
            _encoder = replacement;
            _parameters = parameters;
        }
    }

    public int UpdateBitrate(double frameDurationMilliseconds)
    {
        if (!double.IsFinite(frameDurationMilliseconds) || frameDurationMilliseconds <= 0 ||
            frameDurationMilliseconds > 120) throw new ArgumentOutOfRangeException(nameof(frameDurationMilliseconds));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return (int)Math.Ceiling(_parameters.BitrateCeiling * frameDurationMilliseconds / 8000);
        }
    }

    public byte[] Encode(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (frame.SampleRate != _parameters.SampleRate) throw new ArgumentException("Supply audio at the codec sample rate.");
            var duration = frame.SamplesPerChannel * 1000.0 / frame.SampleRate;
            if (duration is not (2.5 or 5 or 10 or 20 or 40 or 60)) throw new ArgumentException("Unsupported Opus frame duration.");
            var pcm = new short[frame.SamplesPerChannel * _parameters.Channels];
            var input = frame.Samples.Span;
            for (var i = 0; i < frame.SamplesPerChannel; i++)
                for (var channel = 0; channel < _parameters.Channels; channel++)
                    pcm[i * _parameters.Channels + channel] = ToPcm(input[i * frame.Channels + Math.Min(channel, frame.Channels - 1)]);
            var output = new byte[UpdateBitrate(duration)];
            _encoded = true;
            fixed (short* samples = pcm)
            fixed (byte* bytes = output)
            {
                var count = _encode(_encoder, samples, frame.SamplesPerChannel, bytes, output.Length);
                if (count < 0) throw new InvalidOperationException($"libopus encode failed: {count}.");
                _outputBytes += count;
                _outputMilliseconds += duration;
                return output.AsSpan(0, count).ToArray();
            }
        }
    }

    public AudioFrame Decode(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || data.Length > StreamPacket.MaximumEncodedBytes) throw new FormatException("Invalid Opus packet length.");
        var pcm = new short[_decodeRate * 120 / 1000 * _decodeChannels];
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            fixed (byte* packet = data)
            fixed (short* samples = pcm)
            {
                var count = _decode(_decoder, packet, data.Length, samples, pcm.Length / _decodeChannels, 0);
                if (count < 0) throw new FormatException($"libopus decode failed: {count}.");
                return new(pcm.AsSpan(0, count * _decodeChannels).ToArray().Select(value => value / 32767f).ToArray(),
                    _decodeRate, _decodeChannels);
            }
        }
    }

    internal static short ToPcm(float value)
    {
        if (!float.IsFinite(value)) throw new ArgumentException("Audio samples must be finite.");
        return unchecked((short)(int)(value * 32767f));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _destroyEncoder(_encoder);
            _destroyDecoder(_decoder);
            _encoder = _decoder = 0;
            NativeLibrary.Free(_library);
        }
    }
}
