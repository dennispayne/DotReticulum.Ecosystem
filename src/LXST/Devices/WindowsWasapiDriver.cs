using System.Runtime.InteropServices;

namespace LXST.Devices;

internal sealed unsafe partial class WindowsWasapiDriver : IWindowsWaveDriver
{
    private readonly WindowsWasapiOptions _options;
    internal WindowsWasapiDriver(WindowsWasapiOptions options)
    {
        if (!Enum.IsDefined(options.ShareMode)) throw new ArgumentOutOfRangeException(nameof(options));
        _options = options with { ChannelMap = options.ChannelMap?.ToArray() };
    }

    public IReadOnlyList<AudioDeviceInfo> List(bool capture)
    {
        using var apartment = new Apartment();
        var result = Enumerate(capture);
        if (capture && _options.IncludeLoopbackInputs)
            result.AddRange(Enumerate(false).Select(device => device with
            {
                Id = "loopback:" + device.Id, Name = "Loopback " + device.Name, IsDefault = false,
            }));
        return result;
    }

    public IWindowsWaveSession Open(bool capture, string? device, AudioDeviceFormat format)
    {
        var loopback = device?.StartsWith("loopback:", StringComparison.Ordinal) == true;
        if (loopback && (!capture || _options.ShareMode == WasapiShareMode.Exclusive))
            throw new ArgumentException("WASAPI loopback capture requires shared mode.");
        var map = new WasapiChannelMap(format.Channels, _options.ChannelMap, capture);
        return new WasapiSessionHost(() => new Session(capture, loopback ? device!["loopback:".Length..] : device,
            format, _options.ShareMode, loopback, map));
    }

    internal bool SupportsPlaybackFormat(AudioDeviceFormat format)
    {
        using var apartment = new Apartment();
        var enumerator = CreateEnumerator();
        nint device = 0, client = 0, closest = 0;
        try
        {
            device = Default(enumerator, false);
            client = Activate(device);
            var wave = MakeFormat(format, new(format.Channels, _options.ChannelMap, false));
            var error = ((delegate* unmanaged[Stdcall]<nint, int, WaveFormat*, nint*, int>)Table(client)[7])
                (client, (int)_options.ShareMode, &wave, _options.ShareMode == WasapiShareMode.Exclusive ? null : &closest);
            if (error == unchecked((int)0x88890008)) return false;
            Check(error, "probe audio format");
            return error == 0;
        }
        finally { CoTaskMemFree(closest); Release(client); Release(device); Release(enumerator); }
    }

    private static readonly Guid EnumeratorClass = new("bcde0395-e52f-467c-8e3d-c4579291692e");
    private static readonly Guid EnumeratorInterface = new("a95664d2-9614-4f35-a746-de8db63617e6");
    private static readonly Guid AudioClientInterface = new("1cb9ad4c-dbfa-4c32-b178-c2f568a703b2");
    private static readonly Guid RenderInterface = new("f294acfc-3146-4483-a7bf-addca7c260e2");
    private static readonly Guid CaptureInterface = new("c8adbd64-e71e-48a0-a4de-185c395cd317");

    private static nint* Table(nint instance) => (nint*)*(nint*)instance;
    private static void Release(nint instance)
    {
        if (instance != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Table(instance)[2])(instance);
    }
    private static void Check(int error, string operation)
    {
        if (error < 0) throw new IOException($"WASAPI could not {operation} (HRESULT 0x{error:x8}).");
    }

    private sealed class Apartment : IDisposable
    {
        private readonly bool _owned;
        internal Apartment()
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("WASAPI requires Windows.");
            var error = CoInitializeEx(0, 0);
            if (error != unchecked((int)0x80010106)) Check(error, "initialize COM");
            _owned = error >= 0;
        }
        public void Dispose() { if (_owned) CoUninitialize(); }
    }

    private static nint CreateEnumerator()
    {
        var clsid = EnumeratorClass;
        var iid = EnumeratorInterface;
        nint result = 0;
        Check(CoCreateInstance(&clsid, 0, 23, &iid, &result), "create device enumerator");
        return result;
    }
    private static nint Default(nint enumerator, bool capture)
    {
        nint result = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)Table(enumerator)[4])
            (enumerator, capture ? 1 : 0, 0, &result), "select default endpoint");
        return result;
    }
    private static string Id(nint device)
    {
        nint text = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Table(device)[5])(device, &text), "read endpoint ID");
        try { return Marshal.PtrToStringUni(text) ?? throw new IOException("An endpoint returned no ID."); }
        finally { CoTaskMemFree(text); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { internal Guid Format; internal uint Id; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyVariant
    {
        internal ushort Type, Reserved1, Reserved2, Reserved3;
        internal nint Value, Extra;
    }
    private static string Name(nint device)
    {
        nint store = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Table(device)[4])(device, 0, &store), "open endpoint properties");
        try
        {
            var key = new PropertyKey { Format = new("a45c254e-df1c-4efd-8020-67d146a850e0"), Id = 14 };
            PropertyVariant value = default;
            try
            {
                Check(((delegate* unmanaged[Stdcall]<nint, PropertyKey*, PropertyVariant*, int>)Table(store)[5])
                    (store, &key, &value), "read endpoint name");
                return value.Type == 31 ? Marshal.PtrToStringUni(value.Value) ?? Id(device) : Id(device);
            }
            finally { PropVariantClear(&value); }
        }
        finally { Release(store); }
    }
    private static List<AudioDeviceInfo> Enumerate(bool capture)
    {
        var enumerator = CreateEnumerator();
        nint collection = 0;
        try
        {
            string? defaultId = null;
            nint defaultDevice = 0;
            try
            {
                var error = ((delegate* unmanaged[Stdcall]<nint, int, int, nint*, int>)Table(enumerator)[4])
                    (enumerator, capture ? 1 : 0, 0, &defaultDevice);
                if (error != unchecked((int)0x80070490)) Check(error, "select default endpoint");
                if (defaultDevice != 0) defaultId = Id(defaultDevice);
            }
            finally { Release(defaultDevice); }
            Check(((delegate* unmanaged[Stdcall]<nint, int, uint, nint*, int>)Table(enumerator)[3])
                (enumerator, capture ? 1 : 0, 1, &collection), "enumerate endpoints");
            uint count = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Table(collection)[3])(collection, &count), "count endpoints");
            if (count > 4096) throw new IOException("WASAPI returned an excessive endpoint count.");
            var result = new List<AudioDeviceInfo>();
            for (uint index = 0; index < count; index++)
            {
                nint device = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Table(collection)[4])
                    (collection, index, &device), "retrieve endpoint");
                try { var id = Id(device); result.Add(new(id, Name(device), id == defaultId)); }
                finally { Release(device); }
            }
            return result;
        }
        finally { Release(collection); Release(enumerator); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct WaveFormat
    {
        internal ushort Tag, Channels;
        internal uint Rate, BytesPerSecond;
        internal ushort BlockAlign, Bits, Extra, ValidBits;
        internal uint ChannelMask;
        internal Guid SubFormat;
    }
    private static WaveFormat MakeFormat(AudioDeviceFormat format, WasapiChannelMap map) => new()
    {
        Tag = 0xfffe, Channels = (ushort)map.NativeChannels, Rate = (uint)format.SampleRate,
        Bits = 32, ValidBits = 32, Extra = 22, BlockAlign = (ushort)(map.NativeChannels * 4),
        BytesPerSecond = checked((uint)(format.SampleRate * map.NativeChannels * 4)),
        ChannelMask = map.Mask, SubFormat = new("00000003-0000-0010-8000-00aa00389b71"),
    };
    private static nint Activate(nint device)
    {
        var iid = AudioClientInterface;
        nint client = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, nint, nint*, int>)Table(device)[3])
            (device, &iid, 23, 0, &client), "activate audio client");
        return client;
    }

    private sealed class Session : IWindowsWaveSession
    {
        private readonly Apartment _apartment;
        private readonly bool _capture;
        private readonly AudioDeviceFormat _format;
        private readonly WasapiChannelMap _map;
        private readonly Queue<float> _pending = new();
        private nint _client, _service;
        private uint _bufferFrames;
        private bool _started, _disposed;

        internal Session(bool capture, string? id, AudioDeviceFormat format, WasapiShareMode mode, bool loopback, WasapiChannelMap map)
        {
            _apartment = new();
            _capture = capture;
            _format = format;
            _map = map;
            nint device = 0;
            nint enumerator = 0;
            try
            {
                enumerator = CreateEnumerator();
                if (id is null or "default") device = Default(enumerator, capture && !loopback);
                else
                {
                    fixed (char* name = id)
                        Check(((delegate* unmanaged[Stdcall]<nint, char*, nint*, int>)Table(enumerator)[5])
                            (enumerator, name, &device), "select endpoint");
                }
                _client = Activate(device);
                var wave = MakeFormat(format, map);
                var flags = mode == WasapiShareMode.Shared ? 0x88000000u : 0;
                if (loopback) flags |= 0x20000;
                var period = mode == WasapiShareMode.Exclusive ? format.FrameDuration.Ticks : 0;
                var error = ((delegate* unmanaged[Stdcall]<nint, int, uint, long, long, WaveFormat*, nint, int>)Table(_client)[3])
                    (_client, (int)mode, flags, format.FrameDuration.Ticks * 2, period, &wave, 0);
                if (error == unchecked((int)0x88890019) && mode == WasapiShareMode.Exclusive)
                {
                    uint alignedFrames = 0;
                    Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Table(_client)[4])(_client, &alignedFrames), "query aligned buffer");
                    if (alignedFrames == 0 || alignedFrames > format.SampleRate * 4)
                        throw new IOException("WASAPI returned an invalid aligned buffer size.");
                    var alignedPeriod = (long)Math.Round(alignedFrames * 10000000d / format.SampleRate);
                    Release(_client);
                    _client = 0;
                    _client = Activate(device);
                    error = ((delegate* unmanaged[Stdcall]<nint, int, uint, long, long, WaveFormat*, nint, int>)Table(_client)[3])
                        (_client, (int)mode, flags, alignedPeriod, alignedPeriod, &wave, 0);
                }
                Check(error, "initialize audio format/mode");
                uint frames = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Table(_client)[4])(_client, &frames), "query buffer size");
                if (frames == 0 || frames > format.SampleRate * 4) throw new IOException("WASAPI returned an invalid buffer size.");
                _bufferFrames = frames;
                var service = capture ? CaptureInterface : RenderInterface;
                nint pointer = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Table(_client)[14])
                    (_client, &service, &pointer), "open stream service");
                _service = pointer;
                if (capture) Start();
            }
            catch { Dispose(); throw; }
            finally { Release(device); Release(enumerator); }
        }
        private void Start()
        {
            Check(((delegate* unmanaged[Stdcall]<nint, int>)Table(_client)[10])(_client), "start stream");
            _started = true;
        }
        private static void Wait(CancellationToken token)
        {
            if (token.WaitHandle.WaitOne(2)) token.ThrowIfCancellationRequested();
        }
        public float[] Read(CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_capture) throw new InvalidOperationException("Not a capture stream.");
            var target = checked((int)(_format.SampleRate * _format.FrameDuration.TotalSeconds) * _format.Channels);
            while (_pending.Count < target)
            {
                token.ThrowIfCancellationRequested();
                uint size = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Table(_service)[5])(_service, &size), "query capture packet");
                if (size == 0) { Wait(token); continue; }
                byte* data = null;
                uint frames = 0, flags = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, ulong*, ulong*, int>)Table(_service)[3])
                    (_service, &data, &frames, &flags, null, null), "acquire capture packet");
                if (frames == 0) { Wait(token); continue; }
                try
                {
                    if (frames > _bufferFrames) throw new IOException("WASAPI capture packet exceeds its buffer.");
                    var samples = new float[checked((int)frames * _map.NativeChannels)];
                    if ((flags & 2) == 0)
                    {
                        if (data is null) throw new IOException("WASAPI returned a null nonsilent buffer.");
                        new ReadOnlySpan<float>(data, samples.Length).CopyTo(samples);
                    }
                    foreach (var sample in _map.Capture(samples))
                    {
                        if (!float.IsFinite(sample)) throw new IOException("WASAPI capture returned a nonfinite sample.");
                        _pending.Enqueue(sample);
                    }
                }
                finally
                {
                    Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)Table(_service)[4])(_service, frames), "release capture packet");
                }
            }
            var result = new float[target];
            for (var index = 0; index < target; index++) result[index] = _pending.Dequeue();
            return result;
        }
        public void Write(ReadOnlySpan<float> samples, CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_capture) throw new InvalidOperationException("Not a render stream.");
            var offset = 0;
            while (offset < samples.Length)
            {
                token.ThrowIfCancellationRequested();
                uint padding = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Table(_client)[6])(_client, &padding), "query render capacity");
                if (padding > _bufferFrames) throw new IOException("WASAPI returned invalid render padding.");
                var available = (int)(_bufferFrames - padding);
                if (available == 0) { Wait(token); continue; }
                var frames = Math.Min(available, (samples.Length - offset) / _format.Channels);
                byte* pointer = null;
                Check(((delegate* unmanaged[Stdcall]<nint, uint, byte**, int>)Table(_service)[3])
                    (_service, (uint)frames, &pointer), "acquire render buffer");
                try
                {
                    if (pointer is null) throw new IOException("WASAPI returned a null render buffer.");
                    _map.Playback(samples.Slice(offset, frames * _format.Channels), new Span<float>(pointer, frames * _map.NativeChannels));
                }
                finally
                {
                    Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)Table(_service)[4])
                        (_service, (uint)frames, 0), "release render buffer");
                }
                offset += frames * _format.Channels;
                if (!_started) Start();
            }
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_started) Check(((delegate* unmanaged[Stdcall]<nint, int>)Table(_client)[11])(_client), "stop stream");
            }
            finally
            {
                Release(_service); Release(_client);
                _pending.Clear();
                _apartment.Dispose();
            }
        }
    }

    [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(nint reserved, uint flags);
    [LibraryImport("ole32.dll")] private static partial void CoUninitialize();
    [LibraryImport("ole32.dll")] private static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, nint* result);
    [LibraryImport("ole32.dll")] private static partial void CoTaskMemFree(nint memory);
    [LibraryImport("ole32.dll")] private static partial int PropVariantClear(PropertyVariant* variant);
}
