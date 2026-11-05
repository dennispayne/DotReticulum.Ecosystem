using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using DotReticulum.Core;
using DotReticulum.Transport;
using LXMF;

namespace NomadNet;

public sealed record NomadNetAnnounceEntry(double Timestamp, string DestinationHash, string DataHex, string Kind);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<NomadNetAnnounceEntry>))]
internal partial class NomadNetAnnounceJsonContext : JsonSerializerContext;

/// <summary>Receive-time compaction, bounded per-type history and atomic standalone persistence.</summary>
public sealed class NomadNetAnnounceStream : IAsyncDisposable
{
    public const int MaximumPerType = 256;
    public const int MaximumDataBytes = 4096;
    public const int MaximumFileBytes = 7 * 1024 * 1024;
    public const int MaximumPending = 128;
    private static readonly string[] Kinds = ["node", "peer", "pn"];
    private readonly string _path;
    private readonly Func<bool> _compact;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lifecycle = new();
    private readonly Channel<NomadNetAnnounceEntry> _pending = System.Threading.Channels.Channel.CreateBounded<NomadNetAnnounceEntry>(
        new BoundedChannelOptions(MaximumPending) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private List<NomadNetAnnounceEntry> _entries;
    private readonly List<IAnnounceHandler> _handlers = [];
    private AnnounceEngine? _engine;
    private Task? _worker;
    private Task? _disposal;
    private bool _disposed;
    private int _active;
    private readonly TaskCompletionSource _activeDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private NomadNetAnnounceStream(string path, Func<bool> compact, List<NomadNetAnnounceEntry> entries)
    { _path = path; _compact = compact; _entries = entries; }

    public Action<string>? Log { get; set; }
    public Action? Changed { get; set; }
    public Func<string, CancellationToken, Task<bool>>? Accept { get; set; }

    public static async Task<NomadNetAnnounceStream> OpenAsync(string directory, Func<bool> compact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compact);
        var path = Path.Combine(Path.GetFullPath(directory), "storage", "announce-stream.json");
        List<NomadNetAnnounceEntry> entries = [];
        if (File.Exists(path))
        {
            var data = await NomadNetContent.ReadBoundedAsync(path, MaximumFileBytes, cancellationToken).ConfigureAwait(false);
            entries = JsonSerializer.Deserialize(data, NomadNetAnnounceJsonContext.Default.ListNomadNetAnnounceEntry)
                ?? throw new InvalidDataException("Empty announce stream.");
            Validate(entries);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(path, compact, entries);
    }

    public void Start(AnnounceEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker is not null) throw new InvalidOperationException("Announce stream is already started.");
            _engine = engine;
            foreach (var (aspect, kind) in new[] { ("nomadnetwork.node", "node"), ("lxmf.delivery", "peer"), ("lxmf.propagation", "pn") })
            {
                var handler = new CallbackAnnounceHandler(aspect, received =>
                {
                    try
                    {
                        var data = received.AppData;
                        // Primary delivery callbacks supply the decoded display name, not the LXMF wire envelope.
                        if (kind == "peer")
                        {
                            var name = LxmfAppData.DisplayName(data);
                            data = Encoding.UTF8.GetBytes(name ?? "");
                        }
                        if (data is null) return;
                        if (kind == "pn" && (!LxmfPropagationAnnounce.TryParse(data, out var pn) || !pn!.Enabled)) return;
                        var entry = new NomadNetAnnounceEntry(
                            (DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).TotalSeconds,
                            Convert.ToHexStringLower(received.DestinationHash), Convert.ToHexStringLower(data), kind);
                        ValidateEntry(entry);
                        if (!_pending.Writer.TryWrite(entry)) Report("Announce stream queue full or stopped; announce not recorded.");
                    }
                    catch (Exception exception) { Report("Announce stream receive failed: " + exception.Message); }
                });
                _handlers.Add(handler);
                engine.RegisterHandler(handler);
            }
            _worker = RunAsync();
        }
    }

    private async Task RunAsync()
    {
        await foreach (var entry in _pending.Reader.ReadAllAsync().ConfigureAwait(false))
            try
            {
                if (Accept is null || await Accept(entry.DestinationHash, CancellationToken.None).ConfigureAwait(false))
                    await AddCoreAsync(entry, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) { Report("Announce stream persistence failed: " + exception.Message); }
    }

    public async Task AddAsync(NomadNetAnnounceEntry entry, CancellationToken cancellationToken = default)
    {
        using var operation = Enter();
        await AddCoreAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    private async Task AddCoreAsync(NomadNetAnnounceEntry entry, CancellationToken cancellationToken)
    {
        ValidateEntry(entry);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var compact = _compact();
            var next = _entries.Where(e => e.Kind != entry.Kind ||
                !compact || e.DestinationHash != entry.DestinationHash).ToList();
            next.Insert(0, entry);
            next = Kinds.SelectMany(kind => next.Where(e => e.Kind == kind).Take(MaximumPerType)).ToList();
            await SaveAsync(next, cancellationToken).ConfigureAwait(false);
            _entries = next;
        }
        finally { _gate.Release(); }
        Notify();
    }

    public async Task<IReadOnlyList<NomadNetAnnounceEntry>> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        using var operation = Enter();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return _entries.ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<int> ImportAsync(string primaryProfileDirectory, CancellationToken cancellationToken = default)
    {
        using var operation = Enter();
        var data = await NomadNetContent.ReadBoundedAsync(Path.Combine(Path.GetFullPath(primaryProfileDirectory),
            "storage", "directory"), NomadNetDirectoryImport.MaximumDirectoryBytes, cancellationToken).ConfigureAwait(false);
        var imported = ParsePrimary(data);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        int added;
        try
        {
            var merged = _entries.Concat(imported).Distinct().ToList();
            Validate(merged);
            added = merged.Count - _entries.Count;
            merged = Kinds.SelectMany(kind => merged.Where(e => e.Kind == kind).OrderByDescending(e => e.Timestamp)).ToList();
            await SaveAsync(merged, cancellationToken).ConfigureAwait(false);
            _entries = merged;
        }
        finally { _gate.Release(); }
        Notify();
        return added;
    }

    public static IReadOnlyList<NomadNetAnnounceEntry> ParsePrimary(ReadOnlySpan<byte> data)
    {
        if (data.Length > NomadNetDirectoryImport.MaximumDirectoryBytes) throw new InvalidDataException("Primary directory too large.");
        try
        {
            var decoded = MsgPack.UnpackApplicationValue(data, new MsgPackLimits
            { MaxEncodedBytes = NomadNetDirectoryImport.MaximumDirectoryBytes, MaxValueBytes = MaximumFileBytes,
                MaxContainerEntries = 10000, MaxValues = 150000, MaxDepth = 8 });
            if (decoded is not Dictionary<object, object?> map ||
                map.GetValueOrDefault("announce_stream") is not object?[] rows)
                throw new InvalidDataException("Expected a primary announce_stream array.");
            var entries = new List<NomadNetAnnounceEntry>();
            foreach (var row in rows)
            {
                if (row is not object?[] { Length: 4 } fields || fields[1] is not byte[] { Length: 16 } hash ||
                    fields[2] is not byte[] appData || fields[3] is not string kind)
                    throw new InvalidDataException("Invalid primary announce record.");
                var timestamp = fields[0] switch { double value => value, float value => value, long value => value,
                    _ => throw new InvalidDataException("Invalid announce timestamp.") };
                entries.Add(new(timestamp, Convert.ToHexStringLower(hash), Convert.ToHexStringLower(appData), kind));
            }
            Validate(entries);
            return Kinds.SelectMany(kind => entries.Where(e => e.Kind == kind)).ToArray();
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or OverflowException)
        { throw new InvalidDataException("Malformed primary announce stream.", exception); }
    }

    private static void Validate(IReadOnlyList<NomadNetAnnounceEntry> entries)
    {
        if (entries.Count > MaximumPerType * 3 || Kinds.Any(kind => entries.Count(e => e?.Kind == kind) > MaximumPerType))
            throw new InvalidDataException("Announce stream exceeds 256 records per type.");
        foreach (var entry in entries) ValidateEntry(entry);
    }

    private static void ValidateEntry(NomadNetAnnounceEntry entry)
    {
        if (entry is null || !Kinds.Contains(entry.Kind) || !double.IsFinite(entry.Timestamp) ||
            entry.Timestamp is < 0 or > 253402300799 ||
            entry.DestinationHash != NomadNetAddress.NormalizeHash(entry.DestinationHash) ||
            entry.DataHex is null || entry.DataHex.Length > MaximumDataBytes * 2 ||
            (entry.DataHex.Length & 1) != 0 || !entry.DataHex.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("Invalid announce stream entry.");
    }

    private async Task SaveAsync(List<NomadNetAnnounceEntry> entries, CancellationToken cancellationToken)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(entries, NomadNetAnnounceJsonContext.Default.ListNomadNetAnnounceEntry);
        if (data.Length > MaximumFileBytes) throw new InvalidDataException("Announce stream exceeds the file limit.");
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var staged = _path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            await File.WriteAllBytesAsync(staged, data, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staged, _path, overwrite: true);
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }

    public static string Label(NomadNetAnnounceEntry entry, bool destination, DateTimeOffset now, bool sanitizeNames = true)
    {
        ValidateEntry(entry);
        var date = DateTimeOffset.UnixEpoch.AddSeconds(entry.Timestamp).ToLocalTime();
        string? text;
        try { text = new UTF8Encoding(false, true).GetString(Convert.FromHexString(entry.DataHex)); }
        catch (DecoderFallbackException) { text = null; }
        text = destination ? entry.DestinationHash : NomadNetTerminalControls.Name(text ?? "", sanitizeNames);
        if (text.Length == 0) text = "<" + entry.DestinationHash + ">";
        var runes = text.EnumerateRunes().ToArray();
        if (runes.Length > 34) text = string.Concat(runes.Take(34).Select(r => r.ToString())) + "…";
        var symbol = entry.Kind switch { "node" => "N", "peer" => "P", _ => "S" };
        return date.ToString(date.Date == now.ToLocalTime().Date ? "HH:mm:ss" : "yyyy-MM-dd",
            CultureInfo.InvariantCulture) + " " + symbol + " " + text;
    }

    public static bool Matches(NomadNetAnnounceEntry entry, string search)
    {
        ValidateEntry(entry);
        ArgumentNullException.ThrowIfNull(search);
        if (search.Length > 4096) throw new ArgumentException("Announce search exceeds 4096 characters.", nameof(search));
        if (search.Length == 0) return true;
        try { return new UTF8Encoding(false, true).GetString(Convert.FromHexString(entry.DataHex))
            .Contains(search, StringComparison.OrdinalIgnoreCase); }
        catch (DecoderFallbackException) { return false; }
    }

    internal static string SanitizeName(string name)
    {
        var result = new StringBuilder();
        foreach (var rune in name.Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            if (rune.Value is >= 0xf900 and <= 0xfaff or >= 0xfe10 and <= 0xfe2f) continue;
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            { if (result.Length > 0 && result[^1] != ' ') result.Append(' '); }
            else if (category is >= UnicodeCategory.UppercaseLetter and <= UnicodeCategory.OtherLetter or
                >= UnicodeCategory.DecimalDigitNumber and <= UnicodeCategory.OtherNumber or
                >= UnicodeCategory.ConnectorPunctuation and <= UnicodeCategory.OtherPunctuation or UnicodeCategory.SpacingCombiningMark)
                result.Append(rune.ToString());
        }
        return result.ToString().Trim();
    }

    private void Notify()
    {
        if (Changed is not { } observers) return;
        foreach (Action observer in observers.GetInvocationList())
            try { observer(); } catch (Exception exception) { Report("Announce stream observer failed: " + exception.Message); }
    }

    private void Report(string message)
    {
        Trace.TraceError(message);
        if (Log is not { } observers) return;
        foreach (Action<string> observer in observers.GetInvocationList())
            try { observer(message); } catch (Exception exception) { Trace.TraceError("Announce stream log observer: {0}", exception.Message); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycle)
        {
            if (_disposal is not null) return new(_disposal);
            _disposed = true;
            if (_engine is not null) foreach (var handler in _handlers) _engine.DeregisterHandler(handler);
            _pending.Writer.TryComplete();
            if (_active == 0) _activeDrained.TrySetResult();
            _disposal = DrainAsync();
            return new(_disposal);
        }
    }

    private async Task DrainAsync()
    {
        if (_worker is not null) await _worker.ConfigureAwait(false);
        await _activeDrained.Task.ConfigureAwait(false);
        _gate.Dispose();
    }

    private IDisposable Enter()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _active++;
            return new Operation(this);
        }
    }

    private sealed class Operation(NomadNetAnnounceStream owner) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._lifecycle)
                if (--owner._active == 0 && owner._disposed) owner._activeDrained.TrySetResult();
        }
    }
}
