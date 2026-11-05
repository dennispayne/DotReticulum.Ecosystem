using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace SharedPrinting;

/// <summary>A separate subreaper owns printer descendants, including double-forked commands.</summary>
internal sealed partial class LinuxPrintProcess : IDisposable
{
    private const string SupervisorArgument = "--internal-print-supervisor";
    private readonly Process _process;
    private Task? _stopping;

    private LinuxPrintProcess(Process process) => _process = process;

    public Process Process => _process;

    [SupportedOSPlatform("linux")]
    public static LinuxPrintProcess Start(IReadOnlyList<string> arguments, string executableName)
    {
        VerifyRequirements();
        var executable = RuntimeFeature.IsDynamicCodeSupported
            ? Path.Combine(AppContext.BaseDirectory, executableName)
            : Environment.ProcessPath ?? throw new IOException("Cannot locate the print supervisor executable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add(SupervisorArgument);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new IOException("Linux print supervisor did not start.");
            return new(process);
        }
        catch { process.Dispose(); throw; }
    }

    public Task StopAsync()
    {
        lock (_process) return _stopping ??= StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        // EOF is private to the supervisor; the actual printer receives a different, closed stdin.
        try { _process.StandardInput.Close(); }
        catch (IOException) { }
        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    public void Dispose() => _process.Dispose();

    public static Task<int?> TryRunSupervisorAsync(string[] arguments)
    {
        if (arguments.Length == 0 || arguments[0] != SupervisorArgument) return Task.FromResult<int?>(null);
        if (!OperatingSystem.IsLinux())
        {
            Diagnostic("private mode requires Linux.");
            return Task.FromResult<int?>(125);
        }
        if (arguments.Length < 3)
        {
            Diagnostic("private mode requires a command and private job filename.");
            return Task.FromResult<int?>(125);
        }
        return RunSupervisorAsync(arguments);
    }

    [SupportedOSPlatform("linux")]
    private static async Task<int?> RunSupervisorAsync(string[] arguments)
    {
        Process? command = null;
        Task? commandExited = null;
        string? jobPath = null;
        var operation = "kernel/procfs preflight";
        try
        {
            VerifyRequirements();
            operation = "private job validation";
            jobPath = ValidateJobPath(arguments[^1]);
            operation = "session/subreaper/privilege setup";
            if (SetSid() < 0 || Prctl(36, 1, 0, 0, 0) < 0 || Prctl(38, 1, 0, 0, 0) < 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            using var input = Console.OpenStandardInput();
            var ownerClosed = input.ReadAsync(new byte[1]).AsTask();
            var start = new ProcessStartInfo(arguments[1])
            {
                UseShellExecute = false, RedirectStandardInput = true,
            };
            foreach (var argument in arguments.Skip(2)) start.ArgumentList.Add(argument);
            operation = "command startup";
            command = new Process { StartInfo = start };
            if (!command.Start()) throw new IOException("Printer command did not start.");
            command.StandardInput.Close();
            commandExited = command.WaitForExitAsync();
            operation = "command/descendant ownership";
            var stopping = false;
            while (true)
            {
                stopping |= ownerClosed.IsCompleted;
                var children = ReadOwnChildren();
                foreach (var child in children)
                {
                    if (stopping) SignalOwnedChild(child);
                    // Process.WaitForExitAsync owns the direct command's wait status.
                    if (child != command.Id || commandExited.IsCompleted) ReapChild(child);
                }
                if (commandExited.IsCompleted && HaveNoChildren())
                {
                    await commandExited.ConfigureAwait(false);
                    if (stopping) File.Delete(jobPath);
                    return stopping ? 125 : command.ExitCode;
                }
                await Task.Delay(20).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            var native = exception is Win32Exception failure ? $", errno={failure.NativeErrorCode}" : "";
            Diagnostic($"{operation} failed ({exception.GetType().Name}{native}).");
            // Never abandon adopted descendants on an exception or an unavailable printer.
            if (command is not null)
            {
                while (true)
                {
                    var children = ReadOwnChildren();
                    foreach (var child in children)
                    {
                        SignalOwnedChild(child);
                        if (child != command.Id || commandExited?.IsCompleted != false) ReapChild(child);
                    }
                    if (commandExited?.IsCompleted != false && HaveNoChildren()) break;
                    await Task.Delay(20).ConfigureAwait(false);
                }
                if (commandExited is not null) await commandExited.ConfigureAwait(false);
            }
            if (jobPath is not null) File.Delete(jobPath);
            return 125;
        }

        finally { command?.Dispose(); }
    }

    private static string ValidateJobPath(string value)
    {
        var path = Path.GetFullPath(value);
        if (path != value || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _) ||
            Path.GetExtension(path) is not (".txt" or ".png") ||
            Path.GetFileName(Path.GetDirectoryName(path)) != "print-jobs" ||
            !PrintFiles.IsRegularFile(path))
            throw new IOException("Linux print supervisor requires a private named print job.");
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linux print job cannot traverse symbolic links.");
        return path;
    }

    private static void Diagnostic(string context)
    {
        try { Console.Error.WriteLine("Linux printer supervisor: " + context); }
        catch (Exception) { /* A dead owner's diagnostic pipe must not disable descendant cleanup. */ }
    }

    [SupportedOSPlatform("linux")]
    private static void VerifyRequirements()
    {
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException("Linux printing ownership requires x64 or arm64.");
        if (!File.Exists($"/proc/self/task/{Environment.ProcessId}/children"))
            throw new PlatformNotSupportedException("Linux printing ownership requires mounted procfs child lists.");
        using var descriptor = OpenPidfd(Environment.ProcessId);
        if (SendSignal(424, descriptor.DangerousGetHandle().ToInt32(), 0, IntPtr.Zero, 0) < 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Linux printing requires pidfd_send_signal.");
        _ = ReadOwnChildren();
    }

    [SupportedOSPlatform("linux")]
    private static HashSet<int> ReadOwnChildren()
    {
        var result = new HashSet<int>();
        // Only this supervisor's kernel child lists are inspected, never the system's process table.
        foreach (var thread in Directory.EnumerateDirectories("/proc/self/task"))
        {
            try
            {
                using var reader = File.OpenText(Path.Combine(thread, "children"));
                var buffer = new char[16384];
                var length = reader.ReadBlock(buffer, 0, buffer.Length);
                var text = buffer.AsSpan(0, length);
                while (!text.IsEmpty && result.Count < 256)
                {
                    var space = text.IndexOf(' ');
                    if (space < 0) break; // Kernel entries end with a space; ignore a truncated last PID.
                    if (int.TryParse(text[..space], NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                        result.Add(pid);
                    text = text[(space + 1)..];
                }
            }
            catch (DirectoryNotFoundException) { }
            catch (FileNotFoundException) { }
            if (result.Count == 256) break;
        }
        return result;
    }

    [SupportedOSPlatform("linux")]
    private static void SignalOwnedChild(int pid)
    {
        SafeFileHandle descriptor;
        try { descriptor = OpenPidfd(pid); }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 3) { return; }
        using (descriptor)
        {
            // Opening first pins the process identity. Revalidate direct kernel parentage afterwards,
            // so PID reuse cannot turn a stale child-list entry into authority over a foreign process.
            try
            {
                using var reader = File.OpenText($"/proc/{pid}/status");
                for (var index = 0; index < 16 && reader.ReadLine() is { } line; index++)
                    if (line.StartsWith("PPid:", StringComparison.Ordinal))
                    {
                        if (!int.TryParse(line.AsSpan(5).Trim(), CultureInfo.InvariantCulture, out var parent) ||
                            parent != Environment.ProcessId) return;
                        if (SendSignal(424, descriptor.DangerousGetHandle().ToInt32(), 9, IntPtr.Zero, 0) < 0 &&
                            Marshal.GetLastPInvokeError() != 3)
                            throw new Win32Exception(Marshal.GetLastPInvokeError());
                        return;
                    }
            }
            catch (DirectoryNotFoundException) { }
            catch (FileNotFoundException) { }
        }
    }

    private static SafeFileHandle OpenPidfd(int pid)
    {
        var descriptor = OpenPid(434, pid, 0);
        if (descriptor < 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Linux printing requires pidfd_open.");
        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    private static void ReapChild(int pid)
    {
        if (WaitPid(pid, out _, 1) < 0 && Marshal.GetLastPInvokeError() is not (4 or 10))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    private static bool HaveNoChildren()
    {
        var result = WaitPid(-1, out _, 1);
        if (result >= 0) return false;
        var error = Marshal.GetLastPInvokeError();
        if (error == 10) return true;
        if (error == 4) return false;
        throw new Win32Exception(error);
    }

    [LibraryImport("libc", EntryPoint = "setsid", SetLastError = true)]
    private static partial int SetSid();
    [LibraryImport("libc", EntryPoint = "prctl", SetLastError = true)]
    private static partial int Prctl(int option, nuint value, nuint unused2, nuint unused3, nuint unused4);
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial long OpenPid(long syscall, int pid, uint flags);
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial long SendSignal(long syscall, int descriptor, int signal, IntPtr information, uint flags);
    [LibraryImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    private static partial int WaitPid(int pid, out int status, int options);
}
