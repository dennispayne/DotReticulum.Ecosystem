using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace DotLXST.Hardware;

/// <summary>Injectable Linux syscall boundary; implementations own no descriptor passed to them.</summary>
public interface ILinuxHardwareIo
{
    int Open(string path);
    void Control(int descriptor, uint request, Span<byte> data);
    void ControlValue(int descriptor, uint request, nuint value);
    void Write(int descriptor, ReadOnlySpan<byte> data);
    void Close(int descriptor);
}

/// <summary>Linux GPIO character-device v2 provider. Pin numbers are chip line offsets, not header positions.</summary>
public sealed class LinuxDigitalPins : IDigitalPins, IDisposable
{
    private readonly object _gate = new();
    private readonly ILinuxHardwareIo _io;
    private readonly Dictionary<int, (HardwareDescriptor Descriptor, PinDirection Direction, PinPull Pull)> _lines = [];
    private readonly HardwareDescriptor _chip;
    private bool _disposed;

    public LinuxDigitalPins(string chip = "/dev/gpiochip0", ILinuxHardwareIo? io = null)
    {
        _io = io ?? new LinuxHardwareIo();
        _chip = new(_io, _io.Open(chip));
    }

    public void Configure(int pin, PinDirection direction, PinPull pull = PinPull.Disabled)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pin);
        if (!Enum.IsDefined(direction) || !Enum.IsDefined(pull)) throw new ArgumentOutOfRangeException(nameof(direction));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var flags = (direction == PinDirection.Input ? 1UL << 2 : 1UL << 3) |
                (pull switch { PinPull.Up => 1UL << 8, PinPull.Down => 1UL << 9, _ => 1UL << 10 });
            if (_lines.TryGetValue(pin, out var line))
            {
                if (line.Direction == direction && line.Pull == pull) return;
                Span<byte> config = stackalloc byte[272];
                config.Clear();
                BinaryPrimitives.WriteUInt64LittleEndian(config, flags);
                line.Descriptor.Control(0xc110b40d, config);
                _lines[pin] = (line.Descriptor, direction, pull);
            }
            else
            {
                Span<byte> request = stackalloc byte[592];
                request.Clear();
                BinaryPrimitives.WriteInt32LittleEndian(request, pin);
                Encoding.ASCII.GetBytes("DotLXST", request[256..]);
                BinaryPrimitives.WriteUInt64LittleEndian(request[288..], flags);
                BinaryPrimitives.WriteUInt32LittleEndian(request[560..], 1);
                _chip.Control(0xc250b407, request);
                var descriptor = BinaryPrimitives.ReadInt32LittleEndian(request[588..]);
                if (descriptor < 0) throw new IOException("GPIO request returned an invalid line descriptor.");
                _lines.Add(pin, (new(_io, descriptor), direction, pull));
            }
        }
    }

    public void Write(int pin, bool high)
    {
        lock (_gate)
        {
            var line = GetLine(pin);
            if (line.Direction != PinDirection.Output) throw new InvalidOperationException("The selected GPIO line is not configured as output.");
            Span<byte> values = stackalloc byte[16];
            values.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(values, high ? 1UL : 0);
            BinaryPrimitives.WriteUInt64LittleEndian(values[8..], 1);
            line.Descriptor.Control(0xc010b40f, values);
        }
    }

    public bool Read(int pin)
    {
        lock (_gate)
        {
            var line = GetLine(pin);
            Span<byte> values = stackalloc byte[16];
            values.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(values[8..], 1);
            line.Descriptor.Control(0xc010b40e, values);
            return (BinaryPrimitives.ReadUInt64LittleEndian(values) & 1) != 0;
        }
    }

    private (HardwareDescriptor Descriptor, PinDirection Direction, PinPull Pull) GetLine(int pin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _lines.TryGetValue(pin, out var line) ? line : throw new InvalidOperationException("Configure the GPIO line before access.");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                foreach (var line in _lines.Values) line.Descriptor.Dispose();
            }
            finally { _lines.Clear(); _chip.Dispose(); }
        }
    }
}

/// <summary>Linux i2c-dev single-byte transfers; no force-address ioctl or device discovery is performed.</summary>
public sealed class LinuxI2cByteBus : IByteBus
{
    private readonly object _gate = new();
    private readonly ILinuxHardwareIo _io;
    private readonly HardwareDescriptor _descriptor;
    private byte? _address;
    private bool _disposed;

    public LinuxI2cByteBus(string path = "/dev/i2c-1", ILinuxHardwareIo? io = null)
    {
        _io = io ?? new LinuxHardwareIo();
        _descriptor = new(_io, _io.Open(path));
    }

    public void WriteByte(byte address, byte value)
    {
        if (address > 0x7f) throw new ArgumentOutOfRangeException(nameof(address));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_address != address)
            {
                _descriptor.ControlValue(0x0703, address);
                _address = address;
            }
            Span<byte> data = stackalloc byte[1];
            data[0] = value;
            _descriptor.Write(data);
        }
    }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _descriptor.Dispose(); }
    }
}

internal sealed class HardwareDescriptor : SafeHandle
{
    private readonly ILinuxHardwareIo _io;
    public HardwareDescriptor(ILinuxHardwareIo io, int descriptor) : base(new IntPtr(-1), ownsHandle: true)
    {
        _io = io;
        if (descriptor < 0) throw new IOException("Hardware provider returned an invalid descriptor.");
        SetHandle(descriptor);
    }
    public override bool IsInvalid => handle == new IntPtr(-1);
    public void Control(uint request, Span<byte> data)
    {
        var acquired = false;
        try { DangerousAddRef(ref acquired); _io.Control((int)handle, request, data); }
        finally { if (acquired) DangerousRelease(); }
    }
    public void ControlValue(uint request, nuint value)
    {
        var acquired = false;
        try { DangerousAddRef(ref acquired); _io.ControlValue((int)handle, request, value); }
        finally { if (acquired) DangerousRelease(); }
    }
    public void Write(ReadOnlySpan<byte> data)
    {
        var acquired = false;
        try { DangerousAddRef(ref acquired); _io.Write((int)handle, data); }
        finally { if (acquired) DangerousRelease(); }
    }
    protected override bool ReleaseHandle()
    {
        try { _io.Close((int)handle); return true; }
        catch (Exception exception) { System.Diagnostics.Trace.TraceError("Closing hardware descriptor failed: " + exception); return false; }
    }
}

internal sealed partial class LinuxHardwareIo : ILinuxHardwareIo
{
    public LinuxHardwareIo()
    {
        if (!OperatingSystem.IsLinux() || !BitConverter.IsLittleEndian ||
            RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException("Linux GPIO/I2C requires little-endian Linux x64 or arm64.");
    }

    public int Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var descriptor = OpenNative(path, 2 | 0x80000);
        if (descriptor < 0) ThrowError("Opening hardware device " + path);
        return descriptor;
    }

    public unsafe void Control(int descriptor, uint request, Span<byte> data)
    {
        fixed (byte* pointer = data)
            if (ControlNative(descriptor, request, (nuint)pointer) < 0) ThrowError($"Linux hardware ioctl 0x{request:X}");
    }

    public void ControlValue(int descriptor, uint request, nuint value)
    {
        if (ControlNative(descriptor, request, value) < 0) ThrowError("Linux hardware address selection");
    }

    public unsafe void Write(int descriptor, ReadOnlySpan<byte> data)
    {
        fixed (byte* pointer = data)
        {
            var written = WriteNative(descriptor, pointer, (nuint)data.Length);
            if (written < 0) ThrowError("Linux hardware write");
            if (written != data.Length) throw new IOException("Incomplete Linux hardware transfer.");
        }
    }

    // Never retry close: on Linux even EINTR may have released the descriptor.
    public void Close(int descriptor) => _ = CloseNative(descriptor);

    private static void ThrowError(string operation)
    {
        var error = Marshal.GetLastPInvokeError();
        throw new IOException($"{operation} failed: {new Win32Exception(error).Message} (errno {error}).");
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenNative(string path, int flags);
    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int ControlNative(int descriptor, nuint request, nuint argument);
    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    private static unsafe partial nint WriteNative(int descriptor, byte* data, nuint length);
    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int CloseNative(int descriptor);
}
