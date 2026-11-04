using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace NomadNet;

public sealed record NomadNetPrintResult(bool Success, string Detail);

/// <summary>One owned worker; automatic jobs never wait on a printer in the receive loop.</summary>
public sealed class NomadNetPrinter : IAsyncDisposable
{
    public const int QueueCapacity = 8;
    public const int MaximumProcessOutputBytes = 64 * 1024;
    private readonly string _directory;
    private readonly TimeSpan _timeout;
    private readonly Channel<Job> _jobs = Channel.CreateBounded<Job>(new BoundedChannelOptions(QueueCapacity)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private Task? _worker;
    private bool _disposed;
    private bool _initialized;
    private bool _stopDisposed;
    public Action<string>? Log { get; set; }

    public NomadNetPrinter(string profile, TimeSpan? timeout = null)
    {
        _directory = Path.Combine(Path.GetFullPath(profile), "storage", "print-jobs");
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _worker ??= RunAsync();
        }
    }

    public bool TryPrintIncoming(NomadNetSettings settings, NomadNetMessage message, NomadNetContact? contact,
        DateTimeOffset received)
    {
        if (!NomadNetPrintingPolicy.ShouldPrint(settings, message.PeerHash, contact?.TrustLevel ?? 2)) return false;
        try { return QueueMessage(settings, message, contact, received, CancellationToken.None).Accepted; }
        catch (Exception exception)
        {
            Report($"Printing {message.Id}: preparation failed ({exception.GetType().Name}).");
            return false;
        }
    }

    public Task<NomadNetPrintResult> PrintMessageAsync(NomadNetSettings settings, NomadNetMessage message,
        NomadNetContact? contact, DateTimeOffset received, CancellationToken cancellationToken = default) =>
        QueueMessage(settings, message, contact, received, cancellationToken).Completion;

    public Task<NomadNetPrintResult> PrintPaperAsync(NomadNetSettings settings, string id, string uri,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Queue(settings.PrintCommand, id, NomadNetPaperQr.Png(uri, cancellationToken), ".png", cancellationToken).Completion;
    }

    private (bool Accepted, Task<NomadNetPrintResult> Completion) QueueMessage(NomadNetSettings settings,
        NomadNetMessage message, NomadNetContact? contact, DateTimeOffset received, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = NomadNetPrintingPolicy.Render(settings.PrintingMessageTemplate, message,
            NomadNetPrintingPolicy.Origin(message.PeerHash, contact), received);
        return Queue(settings.PrintCommand, message.Id, new UTF8Encoding(false, true).GetBytes(text), ".txt", cancellationToken);
    }

    private (bool Accepted, Task<NomadNetPrintResult> Completion) Queue(string command, string id, byte[] bytes,
        string extension, CancellationToken cancellationToken)
    {
        Job job;
        try
        {
            if (bytes.Length > NomadNetPrintingPolicy.MaximumJobBytes)
                throw new InvalidDataException("Print job exceeds 2 MiB.");
            job = new(NomadNetPrintingPolicy.CommandArguments(command).ToArray(), SafeId(id), bytes, extension, cancellationToken);
            job.Registration = cancellationToken.UnsafeRegister(static state =>
            {
                var queued = (Job)state!;
                if (Interlocked.CompareExchange(ref queued.State, 2, 0) == 0)
                    queued.Result.TrySetCanceled(queued.Cancellation);
            }, job);
            lock (_gate)
            {
                if (!_disposed && _worker is not null && _jobs.Writer.TryWrite(job))
                    return (true, job.Result.Task);
            }
        }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
        CryptographicOperations.ZeroMemory(bytes);
        job.Registration.Dispose();
        var result = new NomadNetPrintResult(false, "Printer is stopped or its bounded queue is full.");
        job.Result.TrySetResult(result);
        Report($"Printing {job.Id}: {result.Detail}");
        return (false, job.Result.Task);
    }

    private async Task RunAsync()
    {
        await foreach (var job in _jobs.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (Interlocked.CompareExchange(ref job.State, 1, 0) != 0) continue;
                using var token = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, job.Cancellation);
                token.Token.ThrowIfCancellationRequested();
                var result = await ExecuteAsync(job, token.Token).ConfigureAwait(false);
                job.Result.TrySetResult(result);
                Report($"Printing {job.Id}: {result.Detail}");
            }
            catch (OperationCanceledException)
            {
                job.Result.TrySetCanceled(job.Cancellation.IsCancellationRequested ? job.Cancellation : _stop.Token);
                Report($"Printing {job.Id}: cancelled.");
            }
            catch (Exception exception)
            {
                var result = new NomadNetPrintResult(false, exception switch
                {
                    PrintOutputLimitException => "Printer output exceeded 64 KiB.",
                    System.ComponentModel.Win32Exception => "Printer command could not start.",
                    _ => $"Printer failed ({exception.GetType().Name}).",
                });
                job.Result.TrySetResult(result);
                Report($"Printing {job.Id}: {result.Detail}");
            }
            finally
            {
                Interlocked.Exchange(ref job.State, 2);
                job.Registration.Dispose();
                CryptographicOperations.ZeroMemory(job.Bytes);
            }
        }
    }

    private async Task<NomadNetPrintResult> ExecuteAsync(Job job, CancellationToken cancellationToken)
    {
        EnsurePrivateDirectory();
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + job.Extension);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        Process? process = null;
        NomadNetWindowsPrintProcess? windows = null;
        NomadNetLinuxPrintProcess? linux = null;
        Task? output = null, error = null, exited = null;
        try
        {
            var options = new FileStreamOptions
            { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new FileStream(path, options))
                await file.WriteAsync(job.Bytes, deadline.Token).ConfigureAwait(false);
            if (!NomadNetLocalFiles.IsRegularFile(path)) throw new IOException("Print job is not a regular file.");
            if (OperatingSystem.IsWindows())
            {
                windows = NomadNetWindowsPrintProcess.Start([.. job.Arguments, path]);
                process = windows.Process;
            }
            else if (OperatingSystem.IsLinux())
            {
                linux = NomadNetLinuxPrintProcess.Start([.. job.Arguments, path]);
                process = linux.Process;
            }
            else
            {
                process = new Process
                {
                    StartInfo = new ProcessStartInfo(job.Arguments[0])
                    { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true },
                };
                foreach (var argument in job.Arguments.Skip(1)) process.StartInfo.ArgumentList.Add(argument);
                process.StartInfo.ArgumentList.Add(path);
                if (!process.Start()) throw new IOException("Print command did not start.");
            }
            output = DrainAsync(windows?.StandardOutput ?? process.StandardOutput.BaseStream, deadline.Token);
            error = DrainAsync(windows?.StandardError ?? process.StandardError.BaseStream, deadline.Token);
            exited = process.WaitForExitAsync(deadline.Token);
            var pending = new List<Task> { exited, output, error };
            while (pending.Count != 0)
            {
                var completed = await Task.WhenAny(pending).WaitAsync(deadline.Token).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
                pending.Remove(completed);
            }
            if (windows is not null) await windows.WaitForEmptyAsync(deadline.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return process.ExitCode == 0 ? new(true, "completed.") : new(false, $"Printer exited with code {process.ExitCode}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, "Printer timed out.");
        }
        finally
        {
            deadline.Cancel();
            Exception? cleanupFailure = null;
            try
            {
                if (windows is not null) await windows.StopAsync().ConfigureAwait(false);
                else if (linux is not null) await linux.StopAsync().ConfigureAwait(false);
                else if (process is not null && process.Id != 0 && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
            }
            catch (InvalidOperationException) when (windows is null) { }
            catch (Exception exception)
            {
                cleanupFailure = exception;
                Report($"Printing {job.Id}: process cleanup failed ({exception.GetType().Name}).");
            }
            foreach (var task in new[] { output, error, exited })
                if (task is not null)
                    try { await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                    catch (Exception) { /* Cancellation, output-limit and process-exit failures are reported by the job. */ }
            windows?.Dispose();
            linux?.Dispose();
            if (windows is null && linux is null) process?.Dispose();
            try { File.Delete(path); }
            catch (Exception exception)
            {
                cleanupFailure = exception;
                Report($"Printing {job.Id}: file cleanup failed ({exception.GetType().Name}).");
            }
            if (cleanupFailure is not null) throw new IOException("Printer cleanup did not complete.", cleanupFailure);
        }
    }

    private static async Task DrainAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[4096];
        var count = 0;
        try
        {
            int read;
            while ((read = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false)) != 0)
                if ((count += read) > MaximumProcessOutputBytes)
                    throw new PrintOutputLimitException();
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private void EnsurePrivateDirectory()
    {
        for (var directory = new DirectoryInfo(_directory); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Print workspace cannot traverse symbolic links.");
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(_directory);
        else Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Print workspace cannot be a symbolic link.");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (!_initialized)
        {
            var stale = Directory.EnumerateFiles(_directory).Take(65).ToArray();
            if (stale.Length > 64) throw new IOException("Print workspace exceeds its recovery file bound.");
            foreach (var file in stale)
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out _) &&
                    Path.GetExtension(file) is ".txt" or ".png")
                {
                    if (!NomadNetLocalFiles.IsRegularFile(file)) throw new IOException("Stale print job is not a regular file.");
                    File.Delete(file);
                }
            _initialized = true;
        }
    }

    private void Report(string context)
    {
        var observers = Log;
        if (observers is null) return;
        foreach (Action<string> observer in observers.GetInvocationList())
            try { observer(context); }
            catch (Exception)
            {
                try { Console.Error.WriteLine("Printing log observer failed: " + context); }
                catch (Exception) { /* A closed diagnostic stream must not fault the worker. */ }
            }
    }

    private static string SafeId(string id) => id.Length is > 0 and <= 64 && id.All(char.IsAsciiHexDigit) ? id : "[invalid-id]";

    public async ValueTask DisposeAsync()
    {
        Task? worker;
        lock (_gate)
        {
            if (_disposed) { worker = _worker; }
            else
            {
                _disposed = true;
                _stop.Cancel();
                _jobs.Writer.TryComplete();
                worker = _worker;
            }
        }
        if (worker is not null) await worker.ConfigureAwait(false);
        lock (_gate)
            if (!_stopDisposed) { _stop.Dispose(); _stopDisposed = true; }
    }

    private sealed record Job(string[] Arguments, string Id, byte[] Bytes, string Extension, CancellationToken Cancellation)
    {
        public TaskCompletionSource<NomadNetPrintResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration { get; set; }
        public int State;
    }

    private sealed class PrintOutputLimitException : IOException { }
}
