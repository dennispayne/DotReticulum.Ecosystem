using System.Globalization;
using System.Runtime.InteropServices;

namespace DotLXST.Devices;

internal sealed unsafe partial class WindowsWaveDriver : IWindowsWaveDriver
{
    public IReadOnlyList<AudioDeviceInfo> List(bool capture)
    {
        RequireWindows();
        var count = capture ? WaveInGetNumDevs() : WaveOutGetNumDevs();
        var devices = new List<AudioDeviceInfo>((int)count + 1);
        if (count != 0) devices.Add(new("default", "Windows default audio device", true));
        for (uint index = 0; index < count; index++)
        {
            DeviceCaps caps = default;
            Check(capture ? WaveInGetDevCaps(index, &caps, 80) : WaveOutGetDevCaps(index, &caps, 84),
                "enumerate waveform device");
            var name = new ReadOnlySpan<char>(caps.Name, 32);
            var end = name.IndexOf('\0');
            devices.Add(new(index.ToString(CultureInfo.InvariantCulture), new string(end < 0 ? name : name[..end]), false));
        }
        return devices;
    }

    public IWindowsWaveSession Open(bool capture, string? device, AudioDeviceFormat format)
    {
        RequireWindows();
        var id = uint.MaxValue;
        if (device is not null && device != "default" &&
            (!uint.TryParse(device, NumberStyles.None, CultureInfo.InvariantCulture, out id) || id == uint.MaxValue))
            throw new ArgumentException("Select a waveform device by its enumerated ID or 'default'.", nameof(device));
        return new Session(capture, id, format);
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows waveform audio requires Windows.");
    }

    private static void Check(uint result, string operation)
    {
        if (result != 0) throw new IOException($"Windows waveform audio could not {operation} (MMRESULT {result}).");
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct WaveFormat
    {
        internal ushort Tag, Channels;
        internal uint Rate, BytesPerSecond;
        internal ushort BlockAlign, Bits, Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WaveHeader
    {
        internal nint Data;
        internal uint BufferLength, BytesRecorded;
        internal nuint User;
        internal uint Flags, Loops;
        internal nint Next;
        internal nuint Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DeviceCaps
    {
        internal ushort Manufacturer, Product;
        internal uint Version;
        internal fixed char Name[32];
        internal uint Formats;
        internal ushort Channels, Reserved;
        internal uint Support;
    }

    private sealed class Buffer : IDisposable
    {
        internal readonly WaveHeader* Header;
        internal readonly int CapacitySamples;
        internal bool Prepared;
        internal Buffer(int samples)
        {
            CapacitySamples = samples;
            Header = (WaveHeader*)NativeMemory.AllocZeroed((nuint)sizeof(WaveHeader));
            if (Header is null) throw new OutOfMemoryException();
            Header->Data = (nint)NativeMemory.AllocZeroed((nuint)checked(samples * 2));
            if (Header->Data == 0)
            {
                NativeMemory.Free(Header);
                throw new OutOfMemoryException();
            }
            Header->BufferLength = (uint)checked(samples * 2);
        }

        public void Dispose()
        {
            if (Prepared) throw new InvalidOperationException("A driver-owned waveform buffer cannot be freed.");
            NativeMemory.Clear((void*)Header->Data, (nuint)(CapacitySamples * 2));
            NativeMemory.Free((void*)Header->Data);
            NativeMemory.Free(Header);
        }
    }

    private sealed class Session : IWindowsWaveSession
    {
        private readonly bool _capture;
        private readonly int _channels;
        private readonly ManualResetEvent _signal = new(false);
        private readonly Buffer[] _buffers;
        private nint _handle;
        private int _next;
        private bool _disposed;
        private bool _eventReference;
        private bool _unusable;

        internal Session(bool capture, uint device, AudioDeviceFormat format)
        {
            _capture = capture;
            _channels = format.Channels;
            _buffers = new Buffer[capture ? 2 : 1];
            var waveFormat = new WaveFormat
            {
                Tag = 1, Channels = (ushort)format.Channels, Rate = (uint)format.SampleRate,
                Bits = 16, BlockAlign = (ushort)(format.Channels * 2),
                BytesPerSecond = checked((uint)(format.SampleRate * format.Channels * 2)),
            };
            try
            {
                _signal.SafeWaitHandle.DangerousAddRef(ref _eventReference);
                var eventHandle = _signal.SafeWaitHandle.DangerousGetHandle();
                Check(capture ? WaveInOpen(out _handle, device, &waveFormat, eventHandle, 0, 0x50000) :
                    WaveOutOpen(out _handle, device, &waveFormat, eventHandle, 0, 0x50000), "open device");
                var samples = capture ? checked((int)(format.SampleRate * format.FrameDuration.TotalSeconds) * format.Channels) :
                    checked(format.SampleRate * format.Channels);
                for (var index = 0; index < _buffers.Length; index++)
                {
                    var buffer = _buffers[index] = new(samples);
                    Check(capture ? WaveInPrepareHeader(_handle, buffer.Header, (uint)sizeof(WaveHeader)) :
                        WaveOutPrepareHeader(_handle, buffer.Header, (uint)sizeof(WaveHeader)), "prepare buffer");
                    buffer.Prepared = true;
                    if (capture) Check(WaveInAddBuffer(_handle, buffer.Header, (uint)sizeof(WaveHeader)), "queue capture buffer");
                }
                if (capture) Check(WaveInStart(_handle), "start capture");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public float[] Read(CancellationToken cancellationToken)
        {
            if (!_capture) throw new InvalidOperationException("Not a capture session.");
            ObjectDisposedException.ThrowIf(_disposed, this);
            var buffer = _buffers[_next];
            Wait(buffer.Header, cancellationToken);
            var bytes = buffer.Header->BytesRecorded;
            if (bytes > buffer.Header->BufferLength || bytes % (_channels * 2) != 0)
                throw new IOException("Windows returned an invalid capture buffer length.");
            var samples = new float[bytes / 2];
            var pcm = new ReadOnlySpan<short>((void*)buffer.Header->Data, samples.Length);
            for (var index = 0; index < samples.Length; index++) samples[index] = pcm[index] / 32768f;
            buffer.Header->BytesRecorded = 0;
            Check(WaveInAddBuffer(_handle, buffer.Header, (uint)sizeof(WaveHeader)), "requeue capture buffer");
            _next = (_next + 1) % _buffers.Length;
            return samples;
        }

        public void Write(ReadOnlySpan<float> samples, CancellationToken cancellationToken)
        {
            if (_capture) throw new InvalidOperationException("Not a playback session.");
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_unusable) throw new IOException("The Windows waveform device did not return its previous playback buffer.");
            cancellationToken.ThrowIfCancellationRequested();
            var buffer = _buffers[0];
            if (samples.Length > buffer.CapacitySamples || samples.Length % _channels != 0)
                throw new ArgumentException("Playback samples exceed the native buffer or contain a partial channel frame.", nameof(samples));
            var target = new Span<short>((void*)buffer.Header->Data, samples.Length);
            for (var index = 0; index < samples.Length; index++)
            {
                if (!float.IsFinite(samples[index])) throw new ArgumentException("Device samples must be finite.", nameof(samples));
                target[index] = (short)Math.Clamp(MathF.Round(samples[index] * 32768), short.MinValue, short.MaxValue);
            }
            buffer.Header->BufferLength = (uint)checked(samples.Length * 2);
            _signal.Reset();
            Check(WaveOutWrite(_handle, buffer.Header, (uint)sizeof(WaveHeader)), "queue playback buffer");
            try { Wait(buffer.Header, cancellationToken); }
            catch
            {
                var result = WaveOutReset(_handle);
                _unusable = result != 0;
                Check(result, "return canceled playback buffer");
                throw;
            }
        }

        private void Wait(WaveHeader* header, CancellationToken token)
        {
            var handles = new[] { _signal, token.WaitHandle };
            while ((Volatile.Read(ref header->Flags) & 1) == 0)
            {
                token.ThrowIfCancellationRequested();
                _signal.Reset();
                if ((Volatile.Read(ref header->Flags) & 1) != 0) break;
                WaitHandle.WaitAny(handles, 1000);
            }
            token.ThrowIfCancellationRequested();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            List<Exception>? failures = null;
            if (_handle != 0)
            {
                var reset = _capture ? WaveInReset(_handle) : WaveOutReset(_handle);
                if (reset != 0) (failures ??= []).Add(new IOException($"Windows waveform reset failed (MMRESULT {reset})."));
                foreach (var buffer in _buffers)
                {
                    if (buffer is null || !buffer.Prepared) continue;
                    var result = _capture ? WaveInUnprepareHeader(_handle, buffer.Header, (uint)sizeof(WaveHeader)) :
                        WaveOutUnprepareHeader(_handle, buffer.Header, (uint)sizeof(WaveHeader));
                    if (result == 0) buffer.Prepared = false;
                    else (failures ??= []).Add(new IOException($"Windows waveform unprepare failed (MMRESULT {result}); driver-owned memory retained."));
                }
                var close = _capture ? WaveInClose(_handle) : WaveOutClose(_handle);
                if (close != 0) (failures ??= []).Add(new IOException($"Windows waveform close failed (MMRESULT {close})."));
                else ReleaseEventReference();
                _handle = 0;
            }
            else ReleaseEventReference();
            foreach (var buffer in _buffers)
                if (buffer is not null && !buffer.Prepared) buffer.Dispose();
            _signal.Dispose();
            if (failures is not null) throw new AggregateException("Windows waveform cleanup failed.", failures);
        }

        private void ReleaseEventReference()
        {
            if (!_eventReference) return;
            _eventReference = false;
            _signal.SafeWaitHandle.DangerousRelease();
        }
    }

    [LibraryImport("winmm.dll", EntryPoint = "waveInGetNumDevs")] private static partial uint WaveInGetNumDevs();
    [LibraryImport("winmm.dll", EntryPoint = "waveOutGetNumDevs")] private static partial uint WaveOutGetNumDevs();
    [LibraryImport("winmm.dll", EntryPoint = "waveInGetDevCapsW")] private static partial uint WaveInGetDevCaps(nuint device, DeviceCaps* caps, uint size);
    [LibraryImport("winmm.dll", EntryPoint = "waveOutGetDevCapsW")] private static partial uint WaveOutGetDevCaps(nuint device, DeviceCaps* caps, uint size);
    [LibraryImport("winmm.dll", EntryPoint = "waveInOpen")] private static partial uint WaveInOpen(out nint handle, uint device, WaveFormat* format, nint callback, nuint instance, uint flags);
    [LibraryImport("winmm.dll", EntryPoint = "waveOutOpen")] private static partial uint WaveOutOpen(out nint handle, uint device, WaveFormat* format, nint callback, nuint instance, uint flags);
    [LibraryImport("winmm.dll", EntryPoint = "waveInPrepareHeader")] private static partial uint WaveInPrepareHeader(nint handle, WaveHeader* header, uint size);
    [LibraryImport("winmm.dll", EntryPoint = "waveOutPrepareHeader")] private static partial uint WaveOutPrepareHeader(nint handle, WaveHeader* header, uint size);
    [LibraryImport("winmm.dll", EntryPoint = "waveInAddBuffer")] private static partial uint WaveInAddBuffer(nint handle, WaveHeader* header, uint size);
    [LibraryImport("winmm.dll", EntryPoint = "waveOutWrite")] private static partial uint WaveOutWrite(nint handle, WaveHeader* header, uint size);
    [LibraryImport("winmm.dll", EntryPoint = "waveInStart")] private static partial uint WaveInStart(nint handle);
    [LibraryImport("winmm.dll", EntryPoint = "waveInReset")] private static partial uint WaveInReset(nint handle);
    [LibraryImport("winmm.dll", EntryPoint = "waveOutReset")] private static partial uint WaveOutReset(nint handle);
    [LibraryImport("winmm.dll", EntryPoint = "waveInUnprepareHeader")] private static partial uint WaveInUnprepareHeader(nint handle, WaveHeader* header, uint size);
    [LibraryImport("winmm.dll", EntryPoint = "waveOutUnprepareHeader")] private static partial uint WaveOutUnprepareHeader(nint handle, WaveHeader* header, uint size);
    [LibraryImport("winmm.dll", EntryPoint = "waveInClose")] private static partial uint WaveInClose(nint handle);
    [LibraryImport("winmm.dll", EntryPoint = "waveOutClose")] private static partial uint WaveOutClose(nint handle);
}
