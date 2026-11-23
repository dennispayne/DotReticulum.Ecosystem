using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace NomadNet;

/// <summary>Original implementation of the primary 1.4.4 Node.py request contracts.</summary>
public sealed class NomadNetHost : IAsyncDisposable
{
    public const int MaximumFileBytes = 16 * 1024 * 1024;
    public const int MaximumPaths = 10_000;
    private readonly TransportRouter _router;
    private readonly LocalDestination _destination;
    private readonly NomadNetContent _content;
    private readonly bool _announceAtStart;
    private readonly NomadNetNodeSchedule _schedule;
    private readonly Func<CancellationToken, ValueTask>? _announcePropagation;
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<string> _registered = new(StringComparer.Ordinal);
    private readonly HashSet<TaskCompletionSource> _requests = [];
    private Task? _jobs;
    private readonly object _lifecycle = new();
    private readonly object _refresh = new();
    private bool _disposed;
    private Task? _disposal;

    public NomadNetHost(TransportRouter router, Identity identity, string directory, NomadNetSettings settings,
        Func<CancellationToken, ValueTask>? announcePropagation = null)
    {
        _router = router;
        Statistics = NomadNetHostedStatistics.Open(directory);
        Statistics.Log = Report;
        _content = new NomadNetContent(directory, settings.EnableExecutablePages, settings.MediaConverter, settings.PagesPath, settings.FilesPath);
        _content.Log = Report;
        _announceAtStart = settings.NodeAnnounceAtStart;
        _announcePropagation = announcePropagation;
        _schedule = new(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(settings.NodeAnnounceIntervalMinutes),
            TimeSpan.FromMinutes(settings.NodePageRefreshIntervalMinutes), TimeSpan.FromMinutes(settings.NodeFileRefreshIntervalMinutes));
        _destination = new LocalDestination(identity, Destination.Create(DestinationType.Single, "nomadnetwork", ["node"], identity))
        {
            AppData = Encoding.UTF8.GetBytes(settings.NodeName ?? settings.DisplayName + "'s Node"),
            MaxRequestSize = 64 * 1024,
            LinkEstablished = _ =>
            {
                lock (_lifecycle)
                    if (!_disposed) Statistics.Record(NomadNetHostedRequestKind.Connection);
            },
        };
        Refresh();
        _router.RegisterLocalDestination(_destination);
    }

    public string DestinationHash => Convert.ToHexStringLower(_destination.Destination.Hash);
    public NomadNetHostedStatistics Statistics { get; }
    public Action<string>? Log { get; set; }
    public IReadOnlyList<string> RegisteredPaths
    {
        get
        {
            lock (_lifecycle)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                lock (_refresh) return _registered.Order(StringComparer.Ordinal).ToArray();
            }
        }
    }

    public void Start()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_jobs is not null) throw new InvalidOperationException("The node is already started.");
            Statistics.Start();
            _jobs = RunAsync(_stop.Token);
        }
    }

    public async ValueTask AnnounceAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycle) ObjectDisposedException.ThrowIf(_disposed, this);
        _schedule.Completed(DateTimeOffset.UtcNow, new(Announce: true, Pages: false, Files: false));
        await _router.AnnounceAsync(_destination, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (_announcePropagation is not null) await _announcePropagation(cancellationToken).ConfigureAwait(false);
    }

    public void Refresh()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_refresh) RefreshCore();
        }
    }

    private void RefreshCore(bool pages = true, bool files = true)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var indexIsHosted = false;
        foreach (var path in _content.RequestPaths(pages, files))
        {
            // RequestPaths emits the fallback first, then a second index only if the scan found a file.
            if (!paths.Add(path) && path == "/page/index.mu") indexIsHosted = true;
            if (paths.Count > MaximumPaths) throw new InvalidDataException("Hosted request-path limit exceeded.");
        }
        var selected = _registered.Where(path => path.StartsWith("/file/", StringComparison.Ordinal) ? files : pages);
        if (_registered.Count - selected.Count() + paths.Count > MaximumPaths)
            throw new InvalidDataException("Hosted request-path limit exceeded.");
        foreach (var removed in selected.Except(paths).ToArray())
        {
            _destination.DeregisterRequestHandler(removed);
            _registered.Remove(removed);
        }
        foreach (var path in paths)
        {
            var defaultIndex = path == "/page/index.mu" && !indexIsHosted;
            _destination.RegisterAsyncRequestHandler(path, (request, token) => RespondAsync(request, token, defaultIndex), RequestPolicy.All);
            _registered.Add(path);
        }
    }

    internal ValueTask<object?> RespondAsync(RequestContext request, CancellationToken token, bool defaultIndex)
    {
        TaskCompletionSource completion;
        lock (_lifecycle)
        {
            if (_disposed) return ValueTask.FromResult<object?>(null);
            token.ThrowIfCancellationRequested();
            if (defaultIndex) return ValueTask.FromResult<object?>(NomadNetContent.DefaultIndex.ToArray());
            if (request.Path.StartsWith("/page/", StringComparison.Ordinal)) Statistics.Record(NomadNetHostedRequestKind.Page);
            else if (request.Path.StartsWith("/file/", StringComparison.Ordinal)) Statistics.Record(NomadNetHostedRequestKind.File);
            else if (request.Path == "/media" && _content.IsCountedMediaRequest(request))
                Statistics.Record(NomadNetHostedRequestKind.Media);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _requests.Add(completion);
        }
        return RespondOwnedAsync(request, token, completion);
    }

    private async ValueTask<object?> RespondOwnedAsync(RequestContext request, CancellationToken token, TaskCompletionSource completion)
    {
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
            return await _content.RespondRegisteredAsync(request, lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_lifecycle) _requests.Remove(completion);
            completion.TrySetResult();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false);
            if (_announceAtStart)
            {
                try { await AnnounceAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                { Report("Hosted node startup announce failed: " + exception.Message); }
            }
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var due = _schedule.Due(DateTimeOffset.UtcNow);
                foreach (var job in new[] { new NomadNetNodeJobs(false, due.Pages, false), new NomadNetNodeJobs(false, false, due.Files) })
                {
                    if (!job.Pages && !job.Files) continue;
                    try
                    {
                        lock (_lifecycle)
                        {
                            ObjectDisposedException.ThrowIf(_disposed, this);
                            lock (_refresh) RefreshCore(job.Pages, job.Files);
                        }
                        _schedule.Completed(DateTimeOffset.UtcNow, job);
                    }
                    catch (Exception exception) { Report("Hosted content refresh failed: " + exception.Message); }
                }
                if (due.Announce)
                {
                    try { await AnnounceAsync(cancellationToken).ConfigureAwait(false); }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    { Report("Hosted node maintenance announce failed: " + exception.Message); }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { Report("Hosted node stopped: " + exception.Message); }
    }

    private void Report(string message)
    {
        if (Log is not { } callbacks) return;
        foreach (Action<string> callback in callbacks.GetInvocationList())
            try { callback(message); }
            catch (Exception exception) { Trace.TraceError("NomadNet node log observer: {0}", exception.Message); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycle)
        {
            if (_disposal is not null) return new(_disposal);
            _disposed = true;
            return new(_disposal = DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        Task[] requests;
        lock (_lifecycle) requests = _requests.Select(request => request.Task).ToArray();
        await Task.WhenAll(requests).ConfigureAwait(false);
        if (_jobs is not null) await _jobs.ConfigureAwait(false);
        _destination.AcceptLinks = false;
        lock (_refresh)
            foreach (var path in _registered) _destination.DeregisterRequestHandler(path);
        try { await Statistics.DisposeAsync().ConfigureAwait(false); }
        finally { _stop.Dispose(); }
    }
}

public sealed partial class NomadNetContent
{
    private readonly string _pages;
    private readonly string _files;
    private readonly bool _execute;
    private readonly NomadNetImageCache _images;
    public Action<string>? Log { get; set; }
    private static readonly byte[] Denied = ">Request Not Allowed\n\nThis identity is not authorised for this resource.\n"u8.ToArray();
    internal static ReadOnlySpan<byte> DefaultIndex => ">Home\n\nCreate storage/pages/index.mu to publish a home page.\n"u8;

    public NomadNetContent(string directory, bool enableExecutablePages = false, string mediaConverter = "ffmpeg",
        string? pagesPath = null, string? filesPath = null)
    {
        _pages = Path.GetFullPath(pagesPath ?? Path.Combine(directory, "storage", "pages"));
        _files = Path.GetFullPath(filesPath ?? Path.Combine(directory, "storage", "files"));
        Directory.CreateDirectory(_pages);
        Directory.CreateDirectory(_files);
        _execute = enableExecutablePages;
        _images = new(directory, new NomadNetImages(mediaConverter, Path.Combine(directory, "storage", "image-work")));
    }

    public IEnumerable<string> RequestPaths(bool includePages = true, bool includeFiles = true)
    {
        if (includePages)
        {
            yield return "/page/index.mu";
            yield return "/media";
            foreach (var entry in Enumerate(_pages)) yield return "/page/" + Path.GetRelativePath(_pages, entry).Replace('\\', '/');
        }
        if (includeFiles)
            foreach (var entry in Enumerate(_files)) yield return "/file/" + Path.GetRelativePath(_files, entry).Replace('\\', '/');
    }

    private static IEnumerable<string> Enumerate(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (Path.GetFileName(entry).StartsWith('.') || (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) continue;
            if (Directory.Exists(entry))
            {
                foreach (var file in Enumerate(entry)) yield return file;
            }
            else if (!entry.EndsWith(".allowed", StringComparison.OrdinalIgnoreCase)) yield return entry;
        }
    }

    public ValueTask<object?> RespondAsync(RequestContext request, CancellationToken cancellationToken) =>
        RespondCoreAsync(request, cancellationToken, allowDefaultIndex: true);

    internal ValueTask<object?> RespondRegisteredAsync(RequestContext request, CancellationToken cancellationToken) =>
        RespondCoreAsync(request, cancellationToken, allowDefaultIndex: false);

    private async ValueTask<object?> RespondCoreAsync(RequestContext request, CancellationToken cancellationToken, bool allowDefaultIndex)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var media = request.Path == "/media";
        var page = request.Path.StartsWith("/page/", StringComparison.Ordinal);
        var root = page || media ? _pages : _files;
        var relative = media && request.Data is Dictionary<object, object?> data &&
            data.GetValueOrDefault("path") is string mediaPath && data.ContainsKey("key")
            ? mediaPath.StartsWith("/media/", StringComparison.Ordinal) ? mediaPath[7..] : mediaPath.TrimStart('/')
            : page ? request.Path[6..] : request.Path.StartsWith("/file/", StringComparison.Ordinal) ? request.Path[6..] : null;
        if (relative is null) return media ? false : null;
        var file = Resolve(root, relative);
        if (file is null) return media ? false : null;
        if (media && !new[] { ".webp", ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tiff" }
            .Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) return false;
        if (media && file.Length > 512) return false;
        if (!await IsAllowedAsync(file, request.RemoteIdentity, request, cancellationToken).ConfigureAwait(false))
            return media ? false : Denied.ToArray();
        if (!File.Exists(file))
            return allowDefaultIndex && request.Path == "/page/index.mu" ? DefaultIndex.ToArray() : null;
        if (page && IsExecutable(file))
        {
            if (!_execute) return ">Executable page disabled\n\nEnable executable pages explicitly in settings.json.\n"u8.ToArray();
            return await ExecuteAsync(file, request, MicronText.MaximumPageBytes, cancellationToken).ConfigureAwait(false);
        }

        if (page) return await ReadBoundedAsync(file, MicronText.MaximumPageBytes, cancellationToken).ConfigureAwait(false);
        if (media && !Path.GetExtension(file).Equals(".webp", StringComparison.OrdinalIgnoreCase))
        {
            var converted = await _images.ConvertAsync(file, cancellationToken).ConfigureAwait(false);
            return new FileResponse(converted, new Dictionary<object, object?>
            { ["name"] = Encoding.UTF8.GetBytes(Path.GetFileNameWithoutExtension(file) + ".webp") });
        }
        var bytes = await ReadBoundedAsync(file, NomadNetHost.MaximumFileBytes, cancellationToken).ConfigureAwait(false);
        return new FileResponse(bytes, new Dictionary<object, object?> { ["name"] = Encoding.UTF8.GetBytes(Path.GetFileName(file)) });
    }

    internal bool IsCountedMediaRequest(RequestContext request)
    {
        if (request.Data is not Dictionary<object, object?> data || !data.ContainsKey("key") ||
            data.GetValueOrDefault("path") is not string path) return false;
        var relative = path.StartsWith("/media/", StringComparison.Ordinal) ? path[7..] : path.TrimStart('/');
        var file = Resolve(_pages, relative);
        return file is not null && file.Length <= 512 &&
            new[] { ".webp", ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tiff" }
                .Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase);
    }

    private static string? Resolve(string root, string relative)
    {
        if (relative.Length is 0 or > 512 || relative.Contains('\\') || relative.Contains(':') || relative.Any(char.IsControl)) return null;
        var segments = relative.Split('/');
        if (segments.Any(part => part.Length == 0 || part.StartsWith('.') || part.EndsWith('.') || part.EndsWith(' ') ||
            part.EndsWith(".allowed", StringComparison.OrdinalIgnoreCase))) return null;
        var current = root;
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return null;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return null;
        }
        return current;
    }

    private async Task<bool> IsAllowedAsync(string file, Identity? remoteIdentity, RequestContext request, CancellationToken cancellationToken)
    {
        var allowFile = file + ".allowed";
        if (!File.Exists(allowFile)) return true;
        if (remoteIdentity is null) return false;
        try
        {
            if ((File.GetAttributes(allowFile) & FileAttributes.ReparsePoint) != 0) return false;
            var executable = OperatingSystem.IsWindows()
                ? GetBinaryTypeW(allowFile, out var type) && type is 0 or 6
                : IsExecutable(allowFile);
            var bytes = executable ? _execute
                ? OperatingSystem.IsWindows()
                    ? await ExecuteWindowsAuthorizationAsync(allowFile, request, cancellationToken).ConfigureAwait(false)
                    : await ExecuteAsync(allowFile, request, 64 * 1024, cancellationToken).ConfigureAwait(false)
                : []
                : await ReadBoundedAsync(allowFile, 64 * 1024, cancellationToken).ConfigureAwait(false);
            var hash = Convert.ToHexStringLower(remoteIdentity.Hash);
            return Encoding.UTF8.GetString(bytes).Split(['\r', '\n']).Any(line => line.Equals(hash, StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or OperationCanceledException or TimeoutException)
        {
            ReportAuthorizationFailure(exception);
            return false;
        }
    }

    private void ReportAuthorizationFailure(Exception exception)
    {
        var message = "Hosted authorization failed (" + exception.GetType().Name + ").";
        Trace.TraceError(message);
        if (Log is not { } callbacks) return;
        foreach (Action<string> callback in callbacks.GetInvocationList())
            try { callback(message); }
            catch (Exception observer) { Trace.TraceError("NomadNet authorization log observer: {0}", observer.GetType().Name); }
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetBinaryTypeW(string path, out uint binaryType);

    private static bool IsExecutable(string file) => !OperatingSystem.IsWindows() && File.Exists(file) &&
        (File.GetUnixFileMode(file) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;

    internal static async Task<byte[]> ReadBoundedAsync(string file, int maximum, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        if (input.Length > maximum) throw new InvalidDataException("Hosted content exceeds the response limit.");
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > maximum) throw new InvalidDataException("Hosted content exceeds the response limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return output.ToArray();
    }

    private static async Task<byte[]> ExecuteAsync(string file, RequestContext request, int maximum, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var process = new Process
        {
            StartInfo = GeneratorStartInfo(file, request),
        };
        process.Start();
        var stderr = DrainAsync(process.StandardError.BaseStream, deadline.Token);
        try
        {
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
            {
                if (output.Length + read > maximum) throw new InvalidDataException("Executable page exceeds the response limit.");
                await output.WriteAsync(buffer.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
            }
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            return output.ToArray();
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            await deadline.CancelAsync().ConfigureAwait(false);
            try { await stderr.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private static ProcessStartInfo GeneratorStartInfo(string file, RequestContext request)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment.Clear();
        start.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (OperatingSystem.IsWindows())
            start.Environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        start.Environment["link_id"] = Convert.ToHexStringLower(request.LinkId);
        if (request.RemoteIdentity is not null) start.Environment["remote_identity"] = Convert.ToHexStringLower(request.RemoteIdentity.Hash);
        if (request.Data is Dictionary<object, object?> fields)
            foreach (var (key, value) in fields.Take(128))
                if (key is string name && name.Length <= 128 && name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_') &&
                    (name.StartsWith("field_", StringComparison.Ordinal) || name.StartsWith("var_", StringComparison.Ordinal)) &&
                    value is string text && text.Length <= 16 * 1024 && !text.Contains('\0'))
                    start.Environment[name] = text;
        return start;
    }

    private static async Task<byte[]> ExecuteWindowsAuthorizationAsync(string file, RequestContext request, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var start = GeneratorStartInfo(file, request);
        using var owned = NomadNetWindowsJobProcess.Start([file], start.Environment.ToDictionary());
        var stderr = DrainAsync(owned.StandardError, deadline.Token);
        var stdout = ReadAuthorizationOutputAsync(owned.StandardOutput, deadline.Token);
        var exited = owned.Process.WaitForExitAsync(deadline.Token);
        try
        {
            var pending = new List<Task> { stdout, stderr, exited };
            while (pending.Count != 0)
            {
                var completed = await Task.WhenAny(pending).WaitAsync(deadline.Token).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
                pending.Remove(completed);
            }
            await owned.WaitForEmptyAsync(deadline.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return await stdout.ConfigureAwait(false);
        }
        finally
        {
            await deadline.CancelAsync().ConfigureAwait(false);
            await owned.StopAsync().ConfigureAwait(false);
            foreach (var task in new Task[] { stdout, stderr, exited })
                try { await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (Exception exception) when (exception is OperationCanceledException or IOException or InvalidDataException) { }
        }
    }

    private static async Task<byte[]> ReadAuthorizationOutputAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > 64 * 1024) throw new InvalidDataException("Authorization generator exceeds the response limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return output.ToArray();
    }

    private static async Task DrainAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        while (await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) != 0) { }
    }
}
