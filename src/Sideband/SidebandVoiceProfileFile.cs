using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Sideband;

internal static partial class SidebandVoiceProfileFile
{
    internal static void Validate(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0)
            throw new InvalidDataException("A primary voice log must be a regular file.");
        for (FileSystemInfo? item = new FileInfo(path); item is not null;
            item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Primary voice log files and ancestors cannot be links or reparse points.");
        if (OperatingSystem.IsLinux())
        {
            // Linux UAPI statx: AT_FDCWD, AT_SYMLINK_NOFOLLOW, STATX_TYPE, S_IFMT and S_IFREG.
            if (Statx(-100, Path.GetFullPath(path), 0x100, 1, out var metadata) != 0)
                throw new IOException("Could not inspect the primary voice log.", new Win32Exception(Marshal.GetLastPInvokeError()));
            if ((metadata.Mask & 1) == 0 || (metadata.Mode & 0xF000) != 0x8000)
                throw new InvalidDataException("Primary voice logs cannot be pipes, sockets or devices.");
        }
        else if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Primary voice-log file migration is verified only on Windows and Linux.");
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxMetadata
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(28)] internal ushort Mode;
    }

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Statx(int directory, string path, int flags, uint mask, out LinuxMetadata metadata);
}
