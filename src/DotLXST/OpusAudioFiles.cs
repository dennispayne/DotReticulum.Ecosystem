using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace DotLXST;

/// <summary>Bounded Ogg Opus playback through the separately installed libopusfile ABI.</summary>
public sealed class OpusFileSource : IAudioSource, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly NativeOpusFileReader _reader;
    private readonly int _frameSamples;
    private readonly bool _loop;
    private readonly bool _timed;
    private int _enumerating;
    private bool _disposed;

    public OpusFileSource(string path, int frameDurationMilliseconds = 100, bool loop = false, bool timed = false,
        string? libraryPath = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frameDurationMilliseconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(frameDurationMilliseconds, 1000);
        _frameSamples = 48 * frameDurationMilliseconds;
        _loop = loop;
        _timed = timed;
        _reader = new(path, libraryPath);
    }

    public int SampleRate => 48000;
    public int Channels => _reader.Channels;
    public long SamplesPerChannel => _reader.SamplesPerChannel;

    public async IAsyncEnumerable<AudioFrame> CaptureAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _enumerating, 1, 0) != 0)
            throw new InvalidOperationException("Only one file reader may run at a time.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        try
        {
            await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _reader.Rewind();
            }
            finally { _gate.Release(); }
            while (true)
            {
                AudioFrame? frame;
                await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    frame = _reader.Read(_frameSamples);
                    if (frame is null && _loop && SamplesPerChannel > 0)
                    {
                        _reader.Rewind();
                        frame = _reader.Read(_frameSamples);
                    }
                }
                finally { _gate.Release(); }
                if (frame is null) yield break;
                yield return frame;
                if (_timed)
                    await Task.Delay(TimeSpan.FromSeconds((double)frame.SamplesPerChannel / SampleRate), linked.Token).ConfigureAwait(false);
            }
        }
        finally { Volatile.Write(ref _enumerating, 0); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await _stop.CancelAsync().ConfigureAwait(false);
            _reader.Dispose();
        }
        finally { _gate.Release(); }
    }
}

/// <summary>Original Ogg Opus recording adapter; disposal finalizes the independently installed libopusenc writer.</summary>
public sealed class OpusFileSink : IAudioSink, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly NativeOpusFileWriter _writer;
    private readonly OpusParameters _parameters;
    private int _frameSamples;
    private bool _disposed;

    public OpusFileSink(string path, OpusProfile profile = OpusProfile.AudioMaximum, string? libraryPath = null)
    {
        _parameters = OpusParameters.For(profile);
        _writer = new(path, _parameters, libraryPath);
    }

    public async ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.SampleRate != _parameters.SampleRate || frame.Channels != _parameters.Channels)
            throw new ArgumentException("Supply audio at the selected recording profile's rate and channels.", nameof(frame));
        if (frame.SamplesPerChannel < 1 || frame.SamplesPerChannel > frame.SampleRate)
            throw new ArgumentException("Recording frames must contain at most one second of audio.", nameof(frame));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_frameSamples == 0) _frameSamples = frame.SamplesPerChannel;
            _writer.Write(frame.Samples.Span, Math.Max(frame.SamplesPerChannel, _frameSamples));
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                // Actual LXST OpusFileSink appends ten frames of final silence.
                for (var i = 0; i < 10 && _frameSamples != 0; i++) _writer.Write([], _frameSamples);
            }
            finally { _writer.Dispose(); }
        }
        finally { _gate.Release(); }
    }
}

internal sealed unsafe class NativeOpusFileReader : IDisposable
{
    private readonly nint _library;
    private readonly delegate* unmanaged[Cdecl]<nint, short*, int, int*, int> _read;
    private readonly delegate* unmanaged[Cdecl]<nint, int, int> _channels;
    private readonly delegate* unmanaged[Cdecl]<nint, long, int> _seek;
    private readonly delegate* unmanaged[Cdecl]<nint, void> _free;
    private nint _file;

    public NativeOpusFileReader(string path, string? libraryPath)
    {
        var file = new FileInfo(Path.GetFullPath(path));
        if (!file.Exists || file.Length == 0) throw new IOException("An existing nonempty seekable recording file is required.");
        _library = NativeLibrary.Load(libraryPath ?? (OperatingSystem.IsWindows() ? "opusfile.dll" :
            OperatingSystem.IsMacOS() ? "libopusfile.0.dylib" : "libopusfile.so.0"));
        try
        {
            var open = (delegate* unmanaged[Cdecl]<byte*, int*, nint>)NativeLibrary.GetExport(_library, "op_open_file");
            _read = (delegate* unmanaged[Cdecl]<nint, short*, int, int*, int>)NativeLibrary.GetExport(_library, "op_read");
            _channels = (delegate* unmanaged[Cdecl]<nint, int, int>)NativeLibrary.GetExport(_library, "op_channel_count");
            _seek = (delegate* unmanaged[Cdecl]<nint, long, int>)NativeLibrary.GetExport(_library, "op_pcm_seek");
            _free = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(_library, "op_free");
            var total = (delegate* unmanaged[Cdecl]<nint, int, long>)NativeLibrary.GetExport(_library, "op_pcm_total");
            var bytes = Encoding.UTF8.GetBytes(Path.GetFullPath(path) + "\0");
            var error = 0;
            fixed (byte* name = bytes) _file = open(name, &error);
            if (_file == 0) throw new IOException($"libopusfile could not open the recording: {error}.");
            Channels = _channels(_file, -1);
            if (Channels is not (1 or 2)) throw new NotSupportedException("Only mono/stereo Ogg Opus files are currently supported.");
            SamplesPerChannel = total(_file, -1);
            if (SamplesPerChannel < 0) throw new IOException("The recording does not have a seekable sample count.");
        }
        catch
        {
            if (_file != 0) _free(_file);
            NativeLibrary.Free(_library);
            throw;
        }
    }

    public int Channels { get; }
    public long SamplesPerChannel { get; }

    public void Rewind()
    {
        if (_seek(_file, 0) != 0) throw new IOException("Could not rewind the Ogg Opus recording.");
    }

    public AudioFrame? Read(int frameSamples)
    {
        var buffer = new short[frameSamples * Channels];
        var count = 0;
        while (count < frameSamples)
        {
            var section = 0;
            int read;
            fixed (short* samples = buffer) read = _read(_file, samples + count * Channels, (frameSamples - count) * Channels, &section);
            if (read < 0) throw new FormatException($"Corrupt Ogg Opus recording: {read}.");
            if (read == 0) break;
            if (_channels(_file, section) != Channels) throw new NotSupportedException("Changing channel counts in chained files are unsupported.");
            count += read;
        }
        if (count == 0) return null;
        var output = new float[count * Channels];
        for (var i = 0; i < output.Length; i++) output[i] = buffer[i] / 32767f;
        return new(output, 48000, Channels);
    }

    public void Dispose()
    {
        if (_file == 0) return;
        _free(_file);
        _file = 0;
        NativeLibrary.Free(_library);
    }
}

internal sealed unsafe class NativeOpusFileWriter : IDisposable
{
    private readonly nint _library;
    private readonly delegate* unmanaged[Cdecl]<nint, short*, int, int> _write;
    private readonly delegate* unmanaged[Cdecl]<nint, int> _drain;
    private readonly delegate* unmanaged[Cdecl]<nint, void> _destroy;
    private readonly int _channels;
    private nint _encoder;

    public NativeOpusFileWriter(string path, OpusParameters parameters, string? libraryPath)
    {
        _channels = parameters.Channels;
        _library = NativeLibrary.Load(libraryPath ?? (OperatingSystem.IsWindows() ? "opusenc.dll" :
            OperatingSystem.IsMacOS() ? "libopusenc.0.dylib" : "libopusenc.so.0"));
        try
        {
            var commentsCreate = (delegate* unmanaged[Cdecl]<nint>)NativeLibrary.GetExport(_library, "ope_comments_create");
            var commentsDestroy = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(_library, "ope_comments_destroy");
            var create = (delegate* unmanaged[Cdecl]<byte*, nint, int, int, int, int*, nint>)NativeLibrary.GetExport(_library, "ope_encoder_create_file");
            var control = (delegate* unmanaged[Cdecl]<nint, int, int, int>)NativeLibrary.GetExport(_library, "ope_encoder_ctl");
            _write = (delegate* unmanaged[Cdecl]<nint, short*, int, int>)NativeLibrary.GetExport(_library, "ope_encoder_write");
            _drain = (delegate* unmanaged[Cdecl]<nint, int>)NativeLibrary.GetExport(_library, "ope_encoder_drain");
            _destroy = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(_library, "ope_encoder_destroy");
            var bytes = Encoding.UTF8.GetBytes(Path.GetFullPath(path) + "\0");
            var comments = commentsCreate();
            if (comments == 0) throw new OutOfMemoryException("Could not create Ogg Opus comments.");
            try
            {
                var error = 0;
                fixed (byte* name = bytes) _encoder = create(name, comments, parameters.SampleRate, parameters.Channels, 0, &error);
                if (_encoder == 0) throw new IOException($"libopusenc could not create the recording: {error}.");
            }
            finally { commentsDestroy(comments); }
            if (control(_encoder, 4000, parameters.Voice ? 2048 : 2049) != 0 ||
                control(_encoder, 4002, parameters.BitrateCeiling) != 0)
                throw new IOException("libopusenc could not configure the recording profile.");
        }
        catch
        {
            if (_encoder != 0) _destroy(_encoder);
            NativeLibrary.Free(_library);
            throw;
        }
    }

    public void Write(ReadOnlySpan<float> samples, int samplesPerChannel)
    {
        var pcm = new short[samplesPerChannel * _channels];
        for (var i = 0; i < samples.Length; i++) pcm[i] = NativeOpusCodec.ToPcm(samples[i]);
        fixed (short* data = pcm)
        {
            var result = _write(_encoder, data, samplesPerChannel);
            if (result != 0) throw new IOException($"libopusenc write failed: {result}.");
        }
    }

    public void Dispose()
    {
        if (_encoder == 0) return;
        try
        {
            var result = _drain(_encoder);
            if (result != 0) throw new IOException($"libopusenc finalization failed: {result}.");
        }
        finally
        {
            _destroy(_encoder);
            _encoder = 0;
            NativeLibrary.Free(_library);
        }
    }
}
