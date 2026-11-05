using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SharedPrinting;

internal static partial class PrintFiles
{
    [SupportedOSPlatform("windows")]
    public static void RestrictWindowsDirectory(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new IOException("Cannot identify the private print workspace owner.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    public static bool IsRegularFile(string path)
    {
        if (OperatingSystem.IsLinux())
            return LinuxStatx(-100, path, 0x100, 1, out var status) == 0 &&
                (status.Mask & 1) != 0 && (status.Mode & 0xF000) == 0x8000;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Printing supports Windows and Linux only.");
        var file = new FileInfo(path);
        return file.Exists && (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
    }

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LinuxStatx(int directory, string path, int flags, uint mask, out Statx status);
}
