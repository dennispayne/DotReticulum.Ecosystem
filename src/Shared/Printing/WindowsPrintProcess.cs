using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SharedPrinting;

/// <summary>Starts suspended so no descendant can escape the private print job before assignment.</summary>
internal sealed partial class WindowsPrintProcess : IDisposable
{
    private readonly SafeFileHandle _job;
    private readonly SafeFileHandle _completion;
    private readonly Dictionary<int, Process> _descendants = [];
    private bool _empty;
    public Process Process { get; }
    public Stream StandardOutput { get; }
    public Stream StandardError { get; }

    private WindowsPrintProcess(SafeFileHandle job, SafeFileHandle completion, Process process, Stream output, Stream error) =>
        (_job, _completion, Process, StandardOutput, StandardError) = (job, completion, process, output, error);

    public static unsafe WindowsPrintProcess Start(IReadOnlyList<string> arguments)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var handles = new StartHandles();
        handles.Job = new(CreateJobObjectW(0, 0), true);
        if (handles.Job.IsInvalid) throw new Win32Exception();
        var limits = new ExtendedLimitInformation();
        limits.Basic.LimitFlags = 0x2008; // KILL_ON_JOB_CLOSE | ACTIVE_PROCESS
        limits.Basic.ActiveProcessLimit = 64;
        if (!SetInformationJobObject(handles.Job.DangerousGetHandle(), 9, &limits, (uint)sizeof(ExtendedLimitInformation)))
            throw new Win32Exception();
        handles.Completion = new(CreateIoCompletionPort(-1, 0, 0, 1), true);
        if (handles.Completion.IsInvalid) throw new Win32Exception();
        var association = new CompletionAssociation { Key = 1, Port = handles.Completion.DangerousGetHandle() };
        if (!SetInformationJobObject(handles.Job.DangerousGetHandle(), 7, &association, (uint)sizeof(CompletionAssociation)))
            throw new Win32Exception();
        var security = new SecurityAttributes { Length = (uint)sizeof(SecurityAttributes), InheritHandle = 1 };
        CreatePipePair(ref security, out handles.OutputRead, out handles.OutputWrite);
        CreatePipePair(ref security, out handles.ErrorRead, out handles.ErrorWrite);
        handles.Input = new(CreateFileW("NUL", 0x80000000, 3, ref security, 3, 0, 0), true);
        if (handles.Input.IsInvalid) throw new Win32Exception();
        nuint size = 0;
        InitializeProcThreadAttributeList(0, 1, 0, ref size);
        if (size is 0 or > 65536) throw new IOException("Invalid process attribute size.");
        var attributes = NativeMemory.Alloc(size);
        var initialized = false;
        Process? process = null;
        var info = new ProcessInformation();
        try
        {
            if (!InitializeProcThreadAttributeList((nint)attributes, 1, 0, ref size)) throw new Win32Exception();
            initialized = true;
            nint* inherited = stackalloc nint[3]
            {
                handles.Input.DangerousGetHandle(),
                handles.OutputWrite!.DangerousGetHandle(),
                handles.ErrorWrite!.DangerousGetHandle(),
            };
            if (!UpdateProcThreadAttribute((nint)attributes, 0, 0x20002, inherited, (nuint)(3 * sizeof(nint)), 0, 0))
                throw new Win32Exception();
            var startup = new StartupInformationEx
            {
                Startup = new StartupInformation
                {
                    Size = (uint)sizeof(StartupInformationEx),
                    Flags = 0x100,
                    Input = inherited[0], Output = inherited[1], Error = inherited[2],
                },
                Attributes = (nint)attributes,
            };
            var command = CommandLine(arguments).ToCharArray();
            fixed (char* commandPointer = command)
                if (!CreateProcessW(0, commandPointer, 0, 0, true, 0x08080004, 0, 0, ref startup, out info))
                    throw new Win32Exception();
            if (!AssignProcessToJobObject(handles.Job.DangerousGetHandle(), info.Process)) throw new Win32Exception();
            process = Process.GetProcessById(checked((int)info.ProcessId));
            _ = process.SafeHandle;
            handles.OutputStream = new FileStream(handles.OutputRead!, FileAccess.Read, 4096, false);
            handles.ErrorStream = new FileStream(handles.ErrorRead!, FileAccess.Read, 4096, false);
            if (ResumeThread(info.Thread) == uint.MaxValue) throw new Win32Exception();
            var result = new WindowsPrintProcess(handles.Job, handles.Completion, process, handles.OutputStream, handles.ErrorStream);
            handles.Transferred = true;
            return result;
        }
        catch
        {
            if (info.Process != 0) TerminateProcess(info.Process, 1);
            process?.Dispose();
            throw;
        }
        finally
        {
            if (info.Thread != 0) CloseHandle(info.Thread);
            if (info.Process != 0) CloseHandle(info.Process);
            if (initialized) DeleteProcThreadAttributeList((nint)attributes);
            NativeMemory.Free(attributes);
        }
    }

    public async Task WaitForEmptyAsync(CancellationToken cancellationToken)
    {
        while (!ObserveEmpty())
        {
            CaptureDescendants();
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool ObserveEmpty()
    {
        while (GetQueuedCompletionStatus(_completion.DangerousGetHandle(), out var message, out var key, out _, 0))
            if (key == 1 && message == 4) _empty = true; // JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO
        var error = Marshal.GetLastPInvokeError();
        if (error != 258) throw new Win32Exception(error);
        return _empty && ActiveProcesses() == 0;
    }

    public async Task StopAsync()
    {
        CaptureDescendants();
        if (ActiveProcesses() != 0 && !TerminateJobObject(_job.DangerousGetHandle(), 1)) throw new Win32Exception();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        foreach (var descendant in _descendants.Values)
            await descendant.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        await WaitForEmptyAsync(deadline.Token).ConfigureAwait(false);
    }

    private unsafe void CaptureDescendants()
    {
        foreach (var exited in _descendants.Where(pair => pair.Value.HasExited).Select(pair => pair.Key).ToArray())
        {
            _descendants[exited].Dispose();
            _descendants.Remove(exited);
        }
        byte* buffer = stackalloc byte[8 + 64 * sizeof(nuint)];
        if (!QueryInformationJobObject(_job.DangerousGetHandle(), 3, buffer, (uint)(8 + 64 * sizeof(nuint)), 0))
            throw new Win32Exception();
        var count = ((uint*)buffer)[1];
        if (count > 64) throw new IOException("Print process ownership exceeds its bound.");
        var identifiers = (nuint*)(buffer + 8);
        for (var index = 0; index < count; index++)
        {
            var identifier = checked((int)identifiers[index]);
            if (identifier == Process.Id || _descendants.ContainsKey(identifier)) continue;
            Process? child = null;
            try
            {
                child = Process.GetProcessById(identifier);
                var handle = child.SafeHandle;
                if (!IsProcessInJob(handle.DangerousGetHandle(), _job.DangerousGetHandle(), out var owned))
                    throw new Win32Exception();
                if (owned)
                {
                    _descendants.Add(identifier, child);
                    child = null;
                }
            }
            catch (ArgumentException) { /* The kernel-reported member exited before its handle was opened. */ }
            finally { child?.Dispose(); }
        }
    }

    private unsafe uint ActiveProcesses()
    {
        var accounting = new AccountingInformation();
        if (!QueryInformationJobObject(_job.DangerousGetHandle(), 1, &accounting, (uint)sizeof(AccountingInformation), 0))
            throw new Win32Exception();
        return accounting.ActiveProcesses;
    }

    private static void CreatePipePair(ref SecurityAttributes security, out SafeFileHandle? read, out SafeFileHandle? write)
    {
        if (!CreatePipe(out var input, out var output, ref security, 0)) throw new Win32Exception();
        read = new(input, true);
        write = new(output, true);
        if (!SetHandleInformation(input, 1, 0)) throw new Win32Exception();
    }

    private static string CommandLine(IReadOnlyList<string> arguments)
    {
        var command = new StringBuilder();
        foreach (var argument in arguments)
        {
            if (command.Length != 0) command.Append(' ');
            command.Append('"');
            var slashes = 0;
            foreach (var character in argument)
            {
                if (character == '\\') { slashes++; continue; }
                command.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                command.Append(character);
                slashes = 0;
            }
            command.Append('\\', slashes * 2).Append('"');
        }
        if (command.Length > 32766) throw new IOException("Print command exceeds the Windows process limit.");
        return command.Append('\0').ToString();
    }

    public void Dispose()
    {
        _job.Dispose();
        _completion.Dispose();
        foreach (var descendant in _descendants.Values) descendant.Dispose();
        Process.Dispose();
        StandardOutput.Dispose();
        StandardError.Dispose();
    }

    private sealed class StartHandles : IDisposable
    {
        public SafeFileHandle? Job, Completion, Input, OutputRead, OutputWrite, ErrorRead, ErrorWrite;
        public Stream? OutputStream, ErrorStream;
        public bool Transferred;
        public void Dispose()
        {
            Input?.Dispose();
            OutputWrite?.Dispose();
            ErrorWrite?.Dispose();
            if (Transferred) return;
            Job?.Dispose();
            Completion?.Dispose();
            OutputStream?.Dispose();
            ErrorStream?.Dispose();
            OutputRead?.Dispose();
            ErrorRead?.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public uint Length; public nint Descriptor; public int InheritHandle; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInformation
    {
        public uint Size;
        public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
        public ushort Show, ReservedSize;
        public nint ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInformationEx { public StartupInformation Startup; public nint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CompletionAssociation { public nint Key, Port; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long ProcessTime, JobTime;
        public uint LimitFlags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct AccountingInformation
    {
        public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateJobObjectW(nint attributes, nint name);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetInformationJobObject(nint job, int informationClass, void* information, uint length);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryInformationJobObject(nint job, int informationClass, void* information, uint length, nint returned);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool owned);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(nint job, uint exitCode);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateIoCompletionPort(nint file, nint existing, nuint key, uint concurrency);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetQueuedCompletionStatus(nint port, out uint bytes, out nuint key, out nint overlapped, uint milliseconds);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreatePipe(out nint read, out nint write, ref SecurityAttributes attributes, uint size);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(nint handle, uint mask, uint flags);
    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFileW(string file, uint access, uint share, ref SecurityAttributes attributes, uint disposition, uint flags, nint template);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint size);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, void* value, nuint size, nint previous, nint returned);
    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(nint list);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcessW(nint application, char* command, nint processAttributes,
        nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment,
        nint directory, ref StartupInformationEx startup, out ProcessInformation information);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(nint thread);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint process, uint exitCode);
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
