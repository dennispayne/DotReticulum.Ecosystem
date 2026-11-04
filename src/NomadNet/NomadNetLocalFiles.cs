using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace NomadNet;

/// <summary>Bounded local attachment reads that do not open Unix FIFOs in blocking mode.</summary>
public static partial class NomadNetLocalFiles
{
    public static bool IsRegularFile(string path)
    {
        path = Normalize(path);
        if (OperatingSystem.IsLinux())
            return LinuxStatx(-100, path, 0x100, 1, out var status) == 0 &&
                (status.Mask & 1) != 0 && (status.Mode & 0xF000) == 0x8000;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Local file selection is verified on Windows and Linux only.");
        var file = new FileInfo(path);
        return file.Exists && (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
    }

    public static async Task<byte[]> ReadAsync(string path, int maximumBytes = NomadNetAttachments.MaximumBytes,
        CancellationToken cancellationToken = default)
    {
        if (maximumBytes is < 0 or > NomadNetAttachments.MaximumBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        cancellationToken.ThrowIfCancellationRequested();
        using var handle = Open(Normalize(path));
        var length = RandomAccess.GetLength(handle);
        if (length < 0 || length > maximumBytes) throw new InvalidDataException("Local attachment exceeds its byte limit.");
        var bytes = new byte[checked((int)length)];
        await using var input = new FileStream(handle, FileAccess.Read, 8192, isAsync: false);
        try
        {
            await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (await input.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
                throw new IOException("Local attachment changed size while being read.");
            return bytes;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    private static SafeFileHandle Open(string path)
    {
        SafeFileHandle handle;
        if (OperatingSystem.IsLinux())
        {
            // Linux UAPI: O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC; statx uses AT_EMPTY_PATH.
            var descriptor = LinuxOpen(path, 0x800 | 0x20000 | 0x80000);
            if (descriptor < 0) throw new IOException("Could not open the selected local file.");
            handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
            try
            {
                if (LinuxStatx(descriptor, "", 0x1000, 1, out var status) == 0 &&
                    (status.Mask & 1) != 0 && (status.Mode & 0xF000) == 0x8000) return handle;
            }
            catch { handle.Dispose(); throw; }
        }
        else if (OperatingSystem.IsWindows())
        {
            var extended = path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
            handle = WindowsOpen(extended, 0x80000000, 7, 0, 3, 0x00200000 | 0x08000000, 0);
            try
            {
                if (!handle.IsInvalid && WindowsFileType(handle) == 1 &&
                    WindowsAttributes(handle, 9, out var attributes, 8) &&
                    (attributes.Attributes & ((uint)FileAttributes.Directory | (uint)FileAttributes.ReparsePoint)) == 0) return handle;
            }
            catch { handle.Dispose(); throw; }
        }
        else throw new PlatformNotSupportedException("Local file selection is verified on Windows and Linux only.");
        handle.Dispose();
        throw new IOException("Select a readable regular file, not a link, pipe or device.");
    }

    private static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Length > 4096 || path.Any(char.IsControl) ||
            OperatingSystem.IsWindows() && (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)))
            throw new ArgumentException("Local file path is invalid.", nameof(path));
        var full = Path.GetFullPath(path);
        if (full.Length > 4096 || full.Any(char.IsControl)) throw new ArgumentException("Local file path is invalid.", nameof(path));
        return full;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag
    {
        public uint Attributes;
        public uint ReparseTag;
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LinuxOpen(string path, int flags);

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LinuxStatx(int directory, string path, int flags, uint mask, out Statx status);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle WindowsOpen(string path, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileType")]
    private static partial uint WindowsFileType(SafeFileHandle handle);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WindowsAttributes(SafeFileHandle handle, int informationClass, out AttributeTag attributes, uint size);
}
