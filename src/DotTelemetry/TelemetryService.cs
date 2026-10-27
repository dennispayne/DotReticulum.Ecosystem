using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using DotReticulum.Applications;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotTelemetry;

/// <summary>
/// One telemetry snapshot (format documented in docs/telemetry.md). On the wire it is a msgpack map with string keys, so a
/// receiver ignores keys it does not know and an older receiver keeps working when keys are added; breaking changes bump <c>v</c>.
/// </summary>
public sealed class TelemetryReport
{
    public const int Version = 1;

    private TelemetryReport(Dictionary<object, object?> fields) => Fields = fields;

    public Dictionary<object, object?> Fields { get; }

    public long Sequence => Long("seq");
    public double Timestamp => Double("ts");
    public string Host => Fields.GetValueOrDefault("host") as string ?? "";
    public long Links => Long("links");

    public byte[] Pack() => MsgPack.Pack(Fields);

    public static TelemetryReport? TryUnpack(ReadOnlySpan<byte> data)
    {
        try
        {
            return MsgPack.Unpack(data) is Dictionary<object, object?> map && map.GetValueOrDefault("v") is { } v && MsgPack.ToInt64(v) == Version ? new TelemetryReport(map) : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidCastException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Collects the current metrics of this process and node.</summary>
    public static TelemetryReport Collect(long sequence, TransportRouter router, IEnumerable<(string Name, string Type)> interfaces, string storageDir)
    {
        using var process = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        var fields = new Dictionary<object, object?>
        {
            ["v"] = (long)Version,
            ["seq"] = sequence,
            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            ["host"] = Environment.MachineName,
            ["os"] = RuntimeInformation.OSDescription,
            ["arch"] = RuntimeInformation.OSArchitecture.ToString(),
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["uptime"] = (DateTime.Now - process.StartTime).TotalSeconds,
            ["cpu_count"] = (long)Environment.ProcessorCount,
            ["cpu_time"] = process.TotalProcessorTime.TotalSeconds,
            ["mem_rss"] = process.WorkingSet64,
            ["mem_total"] = gc.TotalAvailableMemoryBytes,
            ["gc_heap"] = GC.GetTotalMemory(false),
            ["threads"] = (long)process.Threads.Count,
            ["load"] = LoadAverage(),
            ["transport"] = router.TransportEnabled,
            ["links"] = (long)router.Links.Links.Count,
            ["interfaces"] = interfaces.Select(i => (object?)new Dictionary<object, object?> { ["name"] = i.Name, ["type"] = i.Type }).ToArray(),
        };
        try
        {
            var drive = new DriveInfo(Path.GetFullPath(storageDir));
            fields["disk_total"] = drive.TotalSize;
            fields["disk_free"] = drive.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            fields["disk_total"] = null;
            fields["disk_free"] = null;
        }

        return new TelemetryReport(fields);
    }

    /// <summary>One-minute load average where the OS exposes it (Linux); null elsewhere.</summary>
    private static object? LoadAverage()
    {
        try
        {
            if (!OperatingSystem.IsLinux()) return null;
            var parts = File.ReadAllText("/proc/loadavg").Split(' ');
            return double.Parse(parts[0], CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public string Format()
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"[{DateTimeOffset.FromUnixTimeSeconds((long)Timestamp):u}] #{Sequence} {Host}");
        sb.Append(CultureInfo.InvariantCulture, $" up {TimeSpan.FromSeconds(Double("uptime")):d\\.hh\\:mm\\:ss}");
        sb.Append(CultureInfo.InvariantCulture, $" cpu {Double("cpu_time"):0.0}s mem {CliSupport.SizeString(Long("mem_rss"))}/{CliSupport.SizeString(Long("mem_total"))}");
        if (Fields.GetValueOrDefault("load") is { } load) sb.Append(CultureInfo.InvariantCulture, $" load {MsgPack.ToDouble(load):0.00}");
        if (Fields.GetValueOrDefault("disk_free") is { } free && Fields.GetValueOrDefault("disk_total") is { } total)
        {
            sb.Append(CultureInfo.InvariantCulture, $" disk {CliSupport.SizeString(MsgPack.ToInt64(free))} free of {CliSupport.SizeString(MsgPack.ToInt64(total))}");
        }

        var count = Fields.GetValueOrDefault("interfaces") is object?[] list ? list.Length : 0;
        sb.Append(CultureInfo.InvariantCulture, $" | {count} interface(s), {Links} link(s), transport {(Fields.GetValueOrDefault("transport") is true ? "on" : "off")}");
        return sb.ToString();
    }

    private long Long(string key) => Fields.GetValueOrDefault(key) is { } v ? MsgPack.ToInt64(v) : 0;

    private double Double(string key) => Fields.GetValueOrDefault(key) is { } v ? MsgPack.ToDouble(v) : 0;
}

/// <summary>
/// The telemetry publisher: destination <c>dotreticulum.telemetry</c>. Every client that links to it receives a
/// <see cref="TelemetryReport"/> as an encrypted link packet each interval; the <c>report</c> request returns a fresh one on demand.
/// </summary>
public sealed class TelemetryDaemon
{
    public const string AppName = "dotreticulum";
    public const string Aspect = "telemetry";
    public const string ReportPath = "report";

    private readonly ReticulumNode _node;
    private readonly LocalDestination _local;
    private readonly ConcurrentDictionary<byte[], Link> _subscribers = new(ByteArrayComparer.Instance);
    private long _sequence;

    public TelemetryDaemon(ReticulumNode node, Identity identity, string? displayName = null)
    {
        _node = node;
        var destination = Destination.Create(DestinationType.Single, AppName, [Aspect], identity);
        var name = displayName ?? Environment.MachineName;
        _local = new LocalDestination(identity, destination)
        {
            AcceptLinks = true,
            LinkEstablished = OnLinkEstablished,
            AppData = MsgPack.Pack(new Dictionary<object, object?> { ["v"] = (long)TelemetryReport.Version, ["name"] = name }),
        };
        _local.RegisterRequestHandler(ReportPath, _ => Snapshot().Pack(), RequestPolicy.All);
        node.Router.RegisterLocalDestination(_local);
    }

    public byte[] DestinationHash => _local.Destination.Hash;

    public string DestinationHashHex => Convert.ToHexStringLower(DestinationHash);

    public int SubscriberCount => _subscribers.Count;

    public Action<string>? Log { get; set; }

    public ValueTask AnnounceAsync(CancellationToken cancellationToken = default) => _node.Router.AnnounceAsync(_local, cancellationToken: cancellationToken);

    public TelemetryReport Snapshot() => TelemetryReport.Collect(Interlocked.Increment(ref _sequence), _node.Router,
        _node.Interfaces.Select(i => (i.Name, i.Interface.GetType().Name)), _node.StorageDir);

    /// <summary>Publishes to all subscribers every <paramref name="interval"/> until cancelled.</summary>
    public async Task RunAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false)) Publish();
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Sends one report to every active subscriber; returns how many received it.</summary>
    public int Publish()
    {
        if (_subscribers.IsEmpty) return 0;
        var packed = Snapshot().Pack();
        var sent = 0;
        foreach (var (id, link) in _subscribers)
        {
            if (link.Status != LinkStatus.Active) { _subscribers.TryRemove(id, out _); continue; }
            try
            {
                // Link packets are limited to the link MDU; a larger report (many interfaces) travels as a Resource.
                if (packed.Length <= link.Mdu) link.Send(packed, createReceipt: false);
                else link.SendResource(packed, null, true);
                sent++;
            }
            catch (InvalidOperationException ex)
            {
                Log?.Invoke("Could not publish to link " + Convert.ToHexStringLower(id) + ": " + ex.Message);
            }
        }

        return sent;
    }

    private void OnLinkEstablished(Link link)
    {
        Log?.Invoke("Telemetry subscriber " + Convert.ToHexStringLower(link.LinkId) + " connected");
        _subscribers[link.LinkId] = link;
        link.Closed = l =>
        {
            _subscribers.TryRemove(l.LinkId, out _);
            Log?.Invoke("Telemetry subscriber " + Convert.ToHexStringLower(l.LinkId) + " disconnected");
        };
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();
        public bool Equals(byte[]? x, byte[]? y) => x is not null && y is not null && x.AsSpan().SequenceEqual(y);
        public int GetHashCode(byte[] obj) => obj.Length >= 4 ? BitConverter.ToInt32(obj, 0) : obj.Length;
    }
}

/// <summary>Subscriber side: links to a <see cref="TelemetryDaemon"/>, pulls one report, then receives the periodic pushes.</summary>
public static class TelemetryReceiver
{
    /// <summary>Delivers reports to <paramref name="onReport"/> until cancelled or the link closes; returns an error text or null.</summary>
    public static async Task<string?> WatchAsync(ReticulumNode node, byte[] destinationHash, TimeSpan connectTimeout, Action<TelemetryReport> onReport,
        CancellationToken cancellationToken)
    {
        var connected = await ClientLinks.ConnectAsync(node, destinationHash, connectTimeout, cancellationToken).ConfigureAwait(false);
        if (connected.Link is not { } link) return connected.Error;

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Deliver(ReadOnlySpan<byte> data)
        {
            if (TelemetryReport.TryUnpack(data) is { } report) onReport(report);
        }

        link.PacketReceived = (data, _) => Deliver(data);
        link.ResourceStrategy = ResourceStrategy.AcceptAll;
        link.ResourceReceived = resource => { if (resource.Status == ResourceStatus.Complete && resource.Data is { } d) Deliver(d); };
        link.Closed = _ => closed.TrySetResult();

        var receipt = link.Request(TelemetryDaemon.ReportPath, null, timeout: 15);
        if (receipt is not null)
        {
            try { if (await receipt.ResponseTask.WaitAsync(cancellationToken).ConfigureAwait(false) is byte[] first) Deliver(first); }
            catch (TimeoutException) { }
        }

        try { await closed.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally { link.Teardown(); }
        return null;
    }
}
