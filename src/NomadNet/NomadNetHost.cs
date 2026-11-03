using System.Diagnostics;
using System.Text;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace NomadNet;

/// <summary>Original implementation of the primary 1.4.4 Node.py request contracts.</summary>
public sealed class NomadNetHost : IAsyncDisposable
{
    public const int MaximumFileBytes = 16 * 1024 * 1024;
    private readonly TransportRouter _router;
    private readonly LocalDestination _destination;
    private readonly NomadNetContent _content;
    private readonly TimeSpan _announceInterval;
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<string> _registered = new(StringComparer.Ordinal);
    private Task? _jobs;
    private readonly object _lifecycle = new();
    private readonly object _refresh = new();
    private bool _disposed;

    public NomadNetHost(TransportRouter router, Identity identity, string directory, NomadNetSettings settings)
    {
        _router = router;
        _content = new NomadNetContent(directory, settings.EnableExecutablePages);
        _announceInterval = TimeSpan.FromMinutes(settings.NodeAnnounceIntervalMinutes);
        _destination = new LocalDestination(identity, Destination.Create(DestinationType.Single, "nomadnetwork", ["node"], identity))
        {
            AppData = Encoding.UTF8.GetBytes(settings.NodeName ?? settings.DisplayName + "'s Node"),
            MaxRequestSize = 64 * 1024,
        };
        Refresh();
        _router.RegisterLocalDestination(_destination);
    }

    public string DestinationHash => Convert.ToHexStringLower(_destination.Destination.Hash);
    public Action<string>? Log { get; set; }

    public void Start()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_jobs is not null) throw new InvalidOperationException("The node is already started.");
            _jobs = RunAsync(_stop.Token);
        }
    }

    public ValueTask AnnounceAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycle) ObjectDisposedException.ThrowIf(_disposed, this);
        return _router.AnnounceAsync(_destination, cancellationToken: cancellationToken);
    }

    public void Refresh()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (_refresh) RefreshCore();
        }
    }

    private void RefreshCore()
    {
        var paths = _content.RequestPaths().ToHashSet(StringComparer.Ordinal);
        foreach (var removed in _registered.Except(paths).ToArray())
        {
            _destination.DeregisterRequestHandler(removed);
            _registered.Remove(removed);
        }
        foreach (var path in paths)
        {
            _destination.RegisterAsyncRequestHandler(path, _content.RespondAsync, RequestPolicy.All);
            _registered.Add(path);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false);
            await AnnounceAsync(cancellationToken).ConfigureAwait(false);
            var nextAnnounce = DateTimeOffset.UtcNow + _announceInterval;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    Refresh();
                    if (DateTimeOffset.UtcNow >= nextAnnounce)
                    {
                        await AnnounceAsync(cancellationToken).ConfigureAwait(false);
                        nextAnnounce = DateTimeOffset.UtcNow + _announceInterval;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Report("Hosted node maintenance failed: " + exception.Message);
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

    public async ValueTask DisposeAsync()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
        }
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_jobs is not null) await _jobs.ConfigureAwait(false);
        _destination.AcceptLinks = false;
        lock (_refresh)
            foreach (var path in _registered) _destination.DeregisterRequestHandler(path);
        _stop.Dispose();
    }
}

public sealed class NomadNetContent
{
    private readonly string _pages;
    private readonly string _files;
    private readonly bool _execute;
    private static readonly byte[] Denied = ">Request Not Allowed\n\nThis identity is not authorised for this resource.\n"u8.ToArray();

    public NomadNetContent(string directory, bool enableExecutablePages = false)
    {
        _pages = Path.GetFullPath(Path.Combine(directory, "storage", "pages"));
        _files = Path.GetFullPath(Path.Combine(directory, "storage", "files"));
        Directory.CreateDirectory(_pages);
        Directory.CreateDirectory(_files);
        _execute = enableExecutablePages;
    }

    public IEnumerable<string> RequestPaths()
    {
        yield return "/page/index.mu";
        yield return "/media";
        foreach (var entry in Enumerate(_pages)) yield return "/page/" + Path.GetRelativePath(_pages, entry).Replace('\\', '/');
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

    public async ValueTask<object?> RespondAsync(RequestContext request, CancellationToken cancellationToken)
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
        if (media && !Path.GetExtension(file).Equals(".webp", StringComparison.OrdinalIgnoreCase)) return false;
        if (!await IsAllowedAsync(file, request.RemoteIdentity, request, cancellationToken).ConfigureAwait(false))
            return media ? false : Denied.ToArray();
        if (!File.Exists(file))
            return request.Path == "/page/index.mu"
                ? ">Home\n\nCreate storage/pages/index.mu to publish a home page.\n"u8.ToArray() : null;
        if (page && IsExecutable(file))
        {
            if (!_execute) return ">Executable page disabled\n\nEnable executable pages explicitly in settings.json.\n"u8.ToArray();
            return await ExecuteAsync(file, request, MicronText.MaximumPageBytes, cancellationToken).ConfigureAwait(false);
        }
        if (page) return await ReadBoundedAsync(file, MicronText.MaximumPageBytes, cancellationToken).ConfigureAwait(false);
        var bytes = await ReadBoundedAsync(file, NomadNetHost.MaximumFileBytes, cancellationToken).ConfigureAwait(false);
        return new FileResponse(bytes, new Dictionary<object, object?> { ["name"] = Encoding.UTF8.GetBytes(Path.GetFileName(file)) });
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
        if (remoteIdentity is null || (File.GetAttributes(allowFile) & FileAttributes.ReparsePoint) != 0) return false;
        var bytes = IsExecutable(allowFile) ? _execute
            ? await ExecuteAsync(allowFile, request, 64 * 1024, cancellationToken).ConfigureAwait(false) : []
            : await ReadBoundedAsync(allowFile, 64 * 1024, cancellationToken).ConfigureAwait(false);
        var hash = Convert.ToHexStringLower(remoteIdentity.Hash);
        return Encoding.UTF8.GetString(bytes).Split('\n').Any(line => line.TrimEnd('\r').Equals(hash, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsExecutable(string file) => !OperatingSystem.IsWindows() && File.Exists(file) &&
        (File.GetUnixFileMode(file) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;

    private static async Task<byte[]> ReadBoundedAsync(string file, int maximum, CancellationToken cancellationToken)
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
            StartInfo = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true },
        };
        process.StartInfo.Environment.Clear();
        process.StartInfo.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "";
        process.StartInfo.Environment["link_id"] = Convert.ToHexStringLower(request.LinkId);
        if (request.RemoteIdentity is not null) process.StartInfo.Environment["remote_identity"] = Convert.ToHexStringLower(request.RemoteIdentity.Hash);
        if (request.Data is Dictionary<object, object?> fields)
            foreach (var (key, value) in fields.Take(128))
                if (key is string name && name.Length <= 128 && name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_') &&
                    (name.StartsWith("field_", StringComparison.Ordinal) || name.StartsWith("var_", StringComparison.Ordinal)) &&
                    value is string text && text.Length <= 16 * 1024 && !text.Contains('\0'))
                    process.StartInfo.Environment[name] = text;
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

    private static async Task DrainAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        while (await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) != 0) { }
    }
}
