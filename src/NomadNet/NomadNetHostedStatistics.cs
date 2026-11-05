using System.Diagnostics;
using System.Text.Json;

namespace NomadNet;

public sealed record NomadNetHostedStatisticsSnapshot(
    long NodeConnects = 0, long ServedPageRequests = 0, long ServedFileRequests = 0, long ServedMediaRequests = 0);

public enum NomadNetHostedRequestKind { Connection, Page, File, Media }

/// <summary>Attempt counters for the primary Node handlers, with bounded native persistence.</summary>
public sealed class NomadNetHostedStatistics : IAsyncDisposable
{
    public const int MaximumStateBytes = 1024;
    public static readonly TimeSpan JobInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan SettingsFlushInterval = TimeSpan.FromSeconds(60);
    public const string RelativeStatePath = "storage/hosted-statistics.json";
    private readonly string _path;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private NomadNetHostedStatisticsSnapshot _snapshot;
    private bool _dirty;
    private bool _disposed;
    private Task? _worker;
    private Task? _disposal;
    private DateTimeOffset _lastFlush = DateTimeOffset.UtcNow;

    private NomadNetHostedStatistics(string path, NomadNetHostedStatisticsSnapshot snapshot, bool dirty)
    { _path = path; _snapshot = snapshot; _dirty = dirty; }

    public Action<string>? Log { get; set; }
    public NomadNetHostedStatisticsSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public bool IsDirty { get { lock (_gate) return _dirty; } }

    public static NomadNetHostedStatistics Open(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var path = Path.GetFullPath(Path.Combine(directory, "storage", "hosted-statistics.json"));
        NomadNetProfileImport.EnsureNoLinks(path);
        if (Directory.Exists(path)) throw new InvalidDataException("Hosted statistics must be a regular file.");
        if (!File.Exists(path)) return new(path, new(), true);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumStateBytes) throw new InvalidDataException("Hosted statistics exceed the native size limit.");
        var bytes = new byte[MaximumStateBytes + 1];
        var count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (count > MaximumStateBytes) throw new InvalidDataException("Hosted statistics exceed the native size limit.");
        return new(path, Decode(bytes.AsMemory(0, count)), false);
    }

    public static Task<NomadNetHostedStatistics> OpenAsync(string directory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Open(directory));
    }

    public static NomadNetHostedStatisticsSnapshot ReadSnapshot(string directory)
    {
        var statistics = Open(directory);
        statistics._stop.Dispose();
        return statistics.Snapshot;
    }

    public static async Task InitializeFromPrimaryAsync(string stagingDirectory,
        IReadOnlyDictionary<object, object?> peerSettings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peerSettings);
        var snapshot = new NomadNetHostedStatisticsSnapshot(Counter("node_connects"), Counter("served_page_requests"),
            Counter("served_file_requests"), Counter("served_media_requests"));
        cancellationToken.ThrowIfCancellationRequested();
        var statistics = Open(stagingDirectory);
        if (File.Exists(statistics._path) || Directory.Exists(statistics._path))
        {
            statistics._stop.Dispose();
            throw new IOException("Hosted statistics import refuses to replace existing native counters.");
        }
        lock (statistics._gate) { statistics._snapshot = snapshot; statistics._dirty = true; }
        try { await statistics.FlushCoreAsync(cancellationToken, createOnly: true).ConfigureAwait(false); }
        finally { statistics._stop.Dispose(); }

        long Counter(string name)
        {
            if (!peerSettings.TryGetValue(name, out var value)) return 0;
            return value switch
            {
                byte number => number,
                sbyte number when number >= 0 => number,
                short number when number >= 0 => number,
                ushort number => number,
                int number when number >= 0 => number,
                uint number => number,
                long number when number >= 0 => number,
                ulong number when number <= long.MaxValue => (long)number,
                _ => throw new InvalidDataException($"Primary hosted counter '{name}' is not an integer in the native 0..Int64.MaxValue bound."),
            };
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker is not null) throw new InvalidOperationException("Hosted statistics are already started.");
            _worker = RunAsync(_stop.Token);
        }
    }

    /// <summary>Saturates at Int64.MaxValue and reports overflow instead of wrapping or refusing service.</summary>
    public void Record(NomadNetHostedRequestKind kind)
    {
        var overflow = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var value = kind switch
            {
                NomadNetHostedRequestKind.Connection => _snapshot.NodeConnects,
                NomadNetHostedRequestKind.Page => _snapshot.ServedPageRequests,
                NomadNetHostedRequestKind.File => _snapshot.ServedFileRequests,
                NomadNetHostedRequestKind.Media => _snapshot.ServedMediaRequests,
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            if (value == long.MaxValue) overflow = true;
            else
            {
                _snapshot = kind switch
                {
                    NomadNetHostedRequestKind.Connection => _snapshot with { NodeConnects = value + 1 },
                    NomadNetHostedRequestKind.Page => _snapshot with { ServedPageRequests = value + 1 },
                    NomadNetHostedRequestKind.File => _snapshot with { ServedFileRequests = value + 1 },
                    _ => _snapshot with { ServedMediaRequests = value + 1 },
                };
                _dirty = true;
            }
        }
        if (overflow) Report($"Hosted {kind} statistics overflow: counter saturated at {long.MaxValue}.");
    }

    /// <summary>Matches NodeInfo's reset: connections, pages and files only; media is retained.</summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            _snapshot = _snapshot with { NodeConnects = 0, ServedPageRequests = 0, ServedFileRequests = 0 };
            _dirty = true;
        }
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        return FlushCoreAsync(cancellationToken);
    }

    public async Task FlushIfDueAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_dirty || now <= _lastFlush + SettingsFlushInterval) return;
        }
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task FlushCoreAsync(CancellationToken cancellationToken, bool createOnly = false)
    {
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            NomadNetHostedStatisticsSnapshot snapshot;
            lock (_gate) { if (!_dirty) return; snapshot = _snapshot; }
            var data = Encode(snapshot);
            NomadNetProfileImport.EnsureNoLinks(_path);
            NomadNetUtilities.CreatePrivateDirectory(Path.GetDirectoryName(_path)!);
            var staging = _path + "." + Guid.NewGuid().ToString("N") + ".new";
            try
            {
                await using (var stream = NomadNetUtilities.CreatePrivateFile(staging, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                NomadNetProfileImport.EnsureNoLinks(_path);
                if (createOnly)
                {
                    if (!DotReticulum.Core.AtomicFilePublication.TryPublishNew(staging, _path))
                        throw new IOException("Hosted statistics import refuses to replace existing native counters.");
                }
                else File.Move(staging, _path, overwrite: true);
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
            lock (_gate)
            {
                if (_snapshot == snapshot) _dirty = false;
                _lastFlush = DateTimeOffset.UtcNow;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Report("Hosted statistics persistence failed: " + exception.Message);
            throw;
        }
        finally { _writes.Release(); }
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(JobInterval);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try { await FlushIfDueAsync(DateTimeOffset.UtcNow, token).ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                { Report("Hosted statistics maintenance will retry: " + exception.Message); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void Report(string message)
    {
        Trace.TraceError("{0}", message);
        if (Log is not { } callbacks) return;
        foreach (Action<string> callback in callbacks.GetInvocationList())
            try { callback(message); }
            catch (Exception exception) { Trace.TraceError("Hosted statistics log observer failed: {0}", exception.Message); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposal is not null) return new(_disposal);
            _disposed = true;
            return new(_disposal = DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_worker is not null) await _worker.ConfigureAwait(false);
        try { await FlushCoreAsync(CancellationToken.None).ConfigureAwait(false); }
        finally { _stop.Dispose(); }
    }

    internal static byte[] Encode(NomadNetHostedStatisticsSnapshot snapshot)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteNumber("nodeConnects", snapshot.NodeConnects);
            writer.WriteNumber("servedPageRequests", snapshot.ServedPageRequests);
            writer.WriteNumber("servedFileRequests", snapshot.ServedFileRequests);
            writer.WriteNumber("servedMediaRequests", snapshot.ServedMediaRequests);
            writer.WriteEndObject();
        }
        return output.ToArray();
    }

    private static NomadNetHostedStatisticsSnapshot Decode(ReadOnlyMemory<byte> data)
    {
        try
        {
            using var document = JsonDocument.Parse(data, new() { MaxDepth = 2 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Hosted statistics must be an object.");
            var values = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Name is not ("schemaVersion" or "nodeConnects" or "servedPageRequests" or "servedFileRequests" or "servedMediaRequests") ||
                    !property.Value.TryGetInt64(out var value) || value < 0 || !values.TryAdd(property.Name, value))
                    throw new InvalidDataException("Hosted statistics contain an unknown, duplicate or invalid counter.");
            if (values.Count != 5 || values["schemaVersion"] != 1)
                throw new InvalidDataException("Hosted statistics have an unsupported or incomplete schema.");
            return new(values["nodeConnects"], values["servedPageRequests"], values["servedFileRequests"], values["servedMediaRequests"]);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        { throw new InvalidDataException("Hosted statistics are corrupt; no counters were reset.", exception); }
    }
}
