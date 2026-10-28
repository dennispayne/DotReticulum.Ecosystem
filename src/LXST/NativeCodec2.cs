using System.Runtime.InteropServices;

namespace LXST;

/// <summary>Original Codec2 C ABI adapter. No Codec2 implementation is embedded or redistributed.</summary>
public sealed unsafe class NativeCodec2 : IAudioCodec
{
    private static readonly object NativeGate = new();
    private readonly nint _library;
    private readonly delegate* unmanaged[Cdecl]<int, nint> _create;
    private readonly delegate* unmanaged[Cdecl]<nint, void> _destroy;
    private readonly delegate* unmanaged[Cdecl]<nint, byte*, short*, void> _encode;
    private readonly delegate* unmanaged[Cdecl]<nint, short*, byte*, void> _decode;
    private readonly delegate* unmanaged[Cdecl]<nint, int> _samplesPerFrame;
    private readonly delegate* unmanaged[Cdecl]<nint, int> _bitsPerFrame;
    private nint _encoder;
    private nint _decoder;
    private int _decodeMode;
    private bool _disposed;

    public NativeCodec2(int mode = 2400, string? libraryPath = null)
    {
        var nativeMode = NativeMode(mode);
        Mode = mode;
        _decodeMode = mode;
        _library = LoadLibrary(libraryPath);
        try
        {
            _create = (delegate* unmanaged[Cdecl]<int, nint>)NativeLibrary.GetExport(_library, "codec2_create");
            _destroy = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(_library, "codec2_destroy");
            _encode = (delegate* unmanaged[Cdecl]<nint, byte*, short*, void>)NativeLibrary.GetExport(_library, "codec2_encode");
            _decode = (delegate* unmanaged[Cdecl]<nint, short*, byte*, void>)NativeLibrary.GetExport(_library, "codec2_decode");
            _samplesPerFrame = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(_library, "codec2_samples_per_frame");
            _bitsPerFrame = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(_library, "codec2_bits_per_frame");
            _encoder = _create(nativeMode);
            _decoder = _encoder;
            if (_encoder == 0 || _decoder == 0) throw new InvalidOperationException("Codec2 mode is unavailable.");
        }
        catch
        {
            if (_encoder != 0) _destroy(_encoder);
            NativeLibrary.Free(_library);
            throw;
        }
    }

    public CodecKind Kind => CodecKind.Codec2;
    public int Mode { get; private set; }

    private static nint LoadLibrary(string? path)
    {
        if (path is not null) return NativeLibrary.Load(path);
        if (OperatingSystem.IsWindows()) return NativeLibrary.Load("codec2.dll");
        if (OperatingSystem.IsMacOS()) return NativeLibrary.Load("libcodec2.dylib");
        if (NativeLibrary.TryLoad("libcodec2.so.1.2", out var library)) return library;
        return NativeLibrary.Load("libcodec2.so");
    }

    private static int NativeMode(int mode) => mode switch
    {
        3200 => 0, 2400 => 1, 1600 => 2, 1400 => 3, 1300 => 4, 1200 => 5, 700 => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
    private static byte ModeHeader(int mode) => mode switch
    {
        700 => 0, 1200 => 1, 1300 => 2, 1400 => 3, 1600 => 4, 2400 => 5, 3200 => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public byte[] Encode(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.SampleRate != 8000) throw new ArgumentException("Codec2 requires 8 kHz input.");
        lock (NativeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var samplesPerFrame = _samplesPerFrame(_encoder);
            var bytesPerFrame = (_bitsPerFrame(_encoder) + 7) / 8;
            var count = frame.SamplesPerChannel / samplesPerFrame;
            if (count > (StreamPacket.MaximumEncodedBytes - 1) / bytesPerFrame) throw new ArgumentException("Codec2 frame is too large.");
            var output = new byte[1 + count * bytesPerFrame];
            output[0] = ModeHeader(Mode);
            var pcm = new short[samplesPerFrame];
            for (var f = 0; f < count; f++)
            {
                for (var i = 0; i < samplesPerFrame; i++)
                    pcm[i] = NativeOpusCodec.ToPcm(frame.Samples.Span[(f * samplesPerFrame + i) * frame.Channels + (frame.Channels > 1 ? 1 : 0)]);
                fixed (short* samples = pcm)
                fixed (byte* bytes = output)
                    _encode(_encoder, bytes + 1 + f * bytesPerFrame, samples);
            }
            return output;
        }
    }

    public AudioFrame Decode(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || data.Length > StreamPacket.MaximumEncodedBytes) throw new FormatException("Invalid Codec2 frame length.");
        lock (NativeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var mode = data[0] switch { 0 => 700, 1 => 1200, 2 => 1300, 3 => 1400, 4 => 1600, 5 => 2400, 6 => 3200, _ => _decodeMode };
            if (mode != _decodeMode)
            {
                var replacement = _create(NativeMode(mode));
                if (replacement == 0) throw new FormatException("Codec2 mode is unavailable.");
                _destroy(_decoder);
                _encoder = replacement;
                _decoder = replacement;
                _decodeMode = mode;
                Mode = mode;
            }
            var samplesPerFrame = _samplesPerFrame(_decoder);
            var bytesPerFrame = (_bitsPerFrame(_decoder) + 7) / 8;
            var count = (data.Length - 1) / bytesPerFrame;
            var output = new float[count * samplesPerFrame];
            var pcm = new short[samplesPerFrame];
            for (var f = 0; f < count; f++)
            {
                fixed (short* samples = pcm)
                fixed (byte* bytes = data)
                    _decode(_decoder, samples, bytes + 1 + f * bytesPerFrame);
                for (var i = 0; i < samplesPerFrame; i++) output[f * samplesPerFrame + i] = pcm[i] / 32767f;
            }
            return new(output, 8000, 1);
        }
    }

    public void Dispose()
    {
        lock (NativeGate)
        {
            if (_disposed) return;
            _disposed = true;
            _destroy(_encoder);
            _encoder = _decoder = 0;
            NativeLibrary.Free(_library);
        }
    }
}
