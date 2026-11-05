using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NomadNet;

/// <summary>ConPTY converts SGR reports to Win32 mouse records, which Console.ReadKey discards.</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class NomadNetWindowsTerminalInput : IDisposable
{
    private readonly nint _handle;
    private readonly uint _previous;
    private bool _disposed;
    private ConsoleKeyInfo _repeat;
    private int _remaining;

    public NomadNetWindowsTerminalInput(bool mouse)
    {
        _handle = GetStdHandle(-10);
        if (!GetConsoleMode(_handle, out _previous)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var mode = (_previous | 0x80u) & ~(0x40u | 0x10u | 0x2u | 0x4u);
        if (mouse) mode |= 0x10;
        if (!SetConsoleMode(_handle, mode)) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    public bool TryRead(out ConsoleKeyInfo? key, out (int Column, int Row)? click)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        key = null;
        click = null;
        if (_remaining > 0) { _remaining--; key = _repeat; return true; }
        if (!GetNumberOfConsoleInputEvents(_handle, out var count)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (count == 0) return false;
        if (!ReadConsoleInputW(_handle, out var record, 1, out var read) || read != 1)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (record.Type == 2 && record.Flags == 0 && (record.Buttons & 1) != 0)
            click = (record.X + 1, record.Y + 1);
        else if (record.Type == 1 && record.Down != 0 && record.VirtualKey is >= 8 and <= 254)
        {
            key = new ConsoleKeyInfo((char)record.Character, (ConsoleKey)record.VirtualKey,
                (record.Controls & 16) != 0, (record.Controls & 3) != 0, (record.Controls & 12) != 0);
            _repeat = key.Value;
            _remaining = Math.Clamp(record.Repeats - 1, 0, 16384);
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!SetConsoleMode(_handle, _previous) || !GetConsoleMode(_handle, out var restored) || restored != _previous)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not restore the owned console input mode.");
    }

    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct InputRecord
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(4)] public int Down;
        [FieldOffset(8)] public ushort Repeats;
        [FieldOffset(10)] public ushort VirtualKey;
        [FieldOffset(14)] public ushort Character;
        [FieldOffset(16)] public uint Controls;
        [FieldOffset(4)] public short X;
        [FieldOffset(6)] public short Y;
        [FieldOffset(8)] public uint Buttons;
        [FieldOffset(16)] public uint Flags;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int standardHandle);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(nint handle, out uint mode);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(nint handle, uint mode);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNumberOfConsoleInputEvents(nint handle, out uint count);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadConsoleInputW(nint handle, out InputRecord record, uint count, out uint read);
}
