using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using V = Sideband.SidebandTelemetryValue;
using K = Sideband.SidebandTelemetryValueKind;

namespace Sideband;

public sealed record SidebandTelemetryStoreOptions
{
    public int MaxRecords { get; init; } = 4096;
    public int MaxBytes { get; init; } = 16 * 1024 * 1024;
    public int MaxQueryRecords { get; init; } = 256;
    internal void Validate()
    {
        if (MaxRecords is < 1 or > 10_000 || MaxBytes is < 44 or > 64 * 1024 * 1024 ||
            MaxQueryRecords is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(SidebandTelemetryStoreOptions));
    }
}

public sealed record SidebandTelemetryQuery(string? SourceHash = null, double? AfterUtcSeconds = null,
    double? BeforeUtcSeconds = null, int? Limit = null);

public sealed record SidebandTelemetryRetention(double? MinimumUtcSeconds = null, double? MaximumUtcSeconds = null)
{
    public const int PrimaryKeepSeconds = 604_800;
    public const int PrimaryFutureGraceSeconds = 240;
    public const int PrimaryCleanIntervalSeconds = 3600;

    /// <summary>Intentionally exposes the primary cleanup defect: maximum is NOW MINUS 240, deleting recent valid records.</summary>
    public static SidebandTelemetryRetention FromPrimary(double nowUtcSeconds)
    {
        if (!double.IsFinite(nowUtcSeconds)) throw new ArgumentOutOfRangeException(nameof(nowUtcSeconds));
        return new(nowUtcSeconds - PrimaryKeepSeconds, nowUtcSeconds - PrimaryFutureGraceSeconds);
    }
}

public enum SidebandTelemetryAppendResult { Added, Duplicate, InvalidTimestamp }

public sealed class SidebandTelemetryStoredReport
{
    private readonly byte[] _packed;
    internal SidebandTelemetryStoredReport(string sourceHash, byte[] packed, SidebandTelemetryReport report)
    {
        SourceHash = sourceHash;
        _packed = packed;
        Report = report;
        TimestampValue = report.TimestampValue ?? throw new InvalidDataException("A stored report requires a source timestamp.");
    }
    public string SourceHash { get; }
    public V TimestampValue { get; }
    public double TimestampUtcSeconds => TimestampValue.Number!.Value;
    public SidebandTelemetryReport Report { get; }
    public byte[] GetPackedTelemetry() => _packed.ToArray();
    internal ReadOnlyMemory<byte> Packed => _packed;
}

/// <summary>
/// Bounded original atomic store, NOT the primary SQLite file format. No trust policy, collection, sending or implicit pruning.
/// One writer owns an exclusive lease. Unix files/directories are private; Windows inherits the caller's directory ACL.
/// </summary>
public sealed class SidebandTelemetryStore : IAsyncDisposable
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SBTELEM1");
    private readonly SidebandTelemetryStoreOptions _options;
    private readonly FileStream _lease;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _lifetime = new();
    private List<SidebandTelemetryStoredReport> _records = [];
    private bool _closing;
    private Task? _disposeTask;

    private SidebandTelemetryStore(string directory, SidebandTelemetryStoreOptions options, FileStream lease)
    {
        DataDirectory = directory;
        StorePath = Path.Combine(directory, "telemetry.sbdb");
        _options = options;
        _lease = lease;
    }

    public string DataDirectory { get; }
    public string StorePath { get; }

    public static async Task<SidebandTelemetryStore> OpenAsync(string directory,
        SidebandTelemetryStoreOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        options.Validate();
        var path = Path.GetFullPath(directory);
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var leasePath = Path.Combine(path, "telemetry.lock");
        var lease = new FileStream(leasePath, StreamOptions(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        SidebandTelemetryStore? store = null;
        try
        {
            PrivateFile(leasePath);
            store = new(path, options, lease);
            if (File.Exists(store.StorePath))
            {
                var info = new FileInfo(store.StorePath);
                if (info.Length > options.MaxBytes || info.Length < 44) throw new InvalidDataException("Telemetry store length violates its bounds.");
                await using var file = new FileStream(store.StorePath, StreamOptions(FileMode.Open, FileAccess.Read, FileShare.Read));
                var bytes = new byte[checked((int)info.Length)];
                await file.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                if (file.Position != file.Length) throw new InvalidDataException("Telemetry store changed during load.");
                store._records = store.Parse(bytes, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                PrivateFile(store.StorePath);
            }
            else await store.PersistAsync([], cancellationToken).ConfigureAwait(false);
            return store;
        }
        catch
        {
            if (store is not null) await store.DisposeAsync().ConfigureAwait(false);
            else await lease.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Duplicates are source+numeric source timestamp; the first payload wins. Received/outer-stream time is never substituted.</summary>
    public async Task<SidebandTelemetryAppendResult> AppendAsync(string sourceHash, ReadOnlyMemory<byte> packedTelemetry,
        double nowUtcSeconds, CancellationToken cancellationToken = default)
    {
        using var operation = Operation(cancellationToken);
        operation.Token.ThrowIfCancellationRequested();
        var source = NormalizeSource(sourceHash);
        if (!double.IsFinite(nowUtcSeconds)) throw new ArgumentOutOfRangeException(nameof(nowUtcSeconds));
        if (packedTelemetry.Length > SidebandTelemetryCodec.MaxEncodedBytes) throw new ArgumentOutOfRangeException(nameof(packedTelemetry));
        var packed = packedTelemetry.ToArray();
        var report = SidebandTelemetryCodec.Decode(packed);
        var timestamp = report.TimestampValue;
        operation.Token.ThrowIfCancellationRequested();
        if (!ValidTimestamp(timestamp) || CompareTimestamp(timestamp!, V.From(nowUtcSeconds + 240)) > 0)
            return SidebandTelemetryAppendResult.InvalidTimestamp;
        await _gate.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            if (_records.Any(r => r.SourceHash == source && CompareTimestamp(r.TimestampValue, timestamp!) == 0))
                return SidebandTelemetryAppendResult.Duplicate;
            if (_records.Count >= _options.MaxRecords) throw new InvalidOperationException("Telemetry record limit reached; prune explicitly.");
            var next = new List<SidebandTelemetryStoredReport>(_records) { new(source, packed, report) };
            await PersistAsync(next, operation.Token).ConfigureAwait(false);
            _records = next;
            return SidebandTelemetryAppendResult.Added;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Exclusive time bounds; global newest-first limit applies before any caller grouping, unlike the primary unfiltered-query defect.</summary>
    public async Task<IReadOnlyList<SidebandTelemetryStoredReport>> QueryAsync(SidebandTelemetryQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        query ??= new();
        var source = query.SourceHash is null ? null : NormalizeSource(query.SourceHash);
        ValidateBound(query.AfterUtcSeconds); ValidateBound(query.BeforeUtcSeconds);
        var limit = query.Limit ?? _options.MaxQueryRecords;
        if (limit < 1 || limit > _options.MaxQueryRecords) throw new ArgumentOutOfRangeException(nameof(query));
        using var operation = Operation(cancellationToken);
        await _gate.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            var selected = _records.Where(r => (source is null || r.SourceHash == source) &&
                (query.AfterUtcSeconds is not { } after || CompareTimestamp(r.TimestampValue, V.From(after)) > 0) &&
                (query.BeforeUtcSeconds is not { } before || CompareTimestamp(r.TimestampValue, V.From(before)) < 0))
                .OrderByDescending(r => r, TimestampComparer.Instance).Take(limit).ToArray();
            operation.Token.ThrowIfCancellationRequested();
            return Array.AsReadOnly(selected);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Removes timestamps strictly outside the inclusive retained window. No automatic scheduling or reference-bug correction.</summary>
    public async Task<int> PruneAsync(SidebandTelemetryRetention retention, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(retention);
        ValidateBound(retention.MinimumUtcSeconds); ValidateBound(retention.MaximumUtcSeconds);
        if (retention.MinimumUtcSeconds > retention.MaximumUtcSeconds) throw new ArgumentException("Retention bounds are reversed.", nameof(retention));
        return await RemoveAsync(r =>
            retention.MinimumUtcSeconds is { } min && CompareTimestamp(r.TimestampValue, V.From(min)) < 0 ||
            retention.MaximumUtcSeconds is { } max && CompareTimestamp(r.TimestampValue, V.From(max)) > 0, cancellationToken).ConfigureAwait(false);
    }

    public Task<int> ClearSourceAsync(string sourceHash, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeSource(sourceHash);
        return RemoveAsync(r => r.SourceHash == normalized, cancellationToken);
    }

    private async Task<int> RemoveAsync(Func<SidebandTelemetryStoredReport, bool> remove, CancellationToken cancellationToken)
    {
        using var operation = Operation(cancellationToken);
        await _gate.WaitAsync(operation.Token).ConfigureAwait(false);
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            var next = _records.Where(r => !remove(r)).ToList();
            operation.Token.ThrowIfCancellationRequested();
            var removed = _records.Count - next.Count;
            if (removed == 0) return 0;
            await PersistAsync(next, operation.Token).ConfigureAwait(false);
            _records = next;
            return removed;
        }
        finally { _gate.Release(); }
    }

    private List<SidebandTelemetryStoredReport> Parse(byte[] bytes, CancellationToken cancellationToken)
    {
        if (!bytes.AsSpan(0, 8).SequenceEqual(Magic) ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32)), bytes.AsSpan(bytes.Length - 32)))
            throw new InvalidDataException("Telemetry store header or checksum is invalid.");
        var count = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4));
        if (count > _options.MaxRecords) throw new InvalidDataException("Telemetry store record count exceeds its bound.");
        var records = new List<SidebandTelemetryStoredReport>();
        var keys = new Dictionary<string, SortedSet<V>>(StringComparer.Ordinal);
        var offset = 12;
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (offset > bytes.Length - 32 - 20) throw new InvalidDataException("Telemetry record header is truncated.");
            var source = Convert.ToHexStringLower(bytes.AsSpan(offset, 16));
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 16, 4));
            offset += 20;
            if (length > SidebandTelemetryCodec.MaxEncodedBytes || length > bytes.Length - 32 - offset)
                throw new InvalidDataException("Telemetry record length is invalid.");
            var packed = bytes.AsSpan(offset, (int)length).ToArray();
            SidebandTelemetryReport report;
            try { report = SidebandTelemetryCodec.Decode(packed); }
            catch (Exception e) when (e is ArgumentException or FormatException or OverflowException)
            { throw new InvalidDataException("Stored telemetry MessagePack is invalid.", e); }
            if (!ValidTimestamp(report.TimestampValue)) throw new InvalidDataException("Stored telemetry lacks a valid source timestamp.");
            if (!keys.TryGetValue(source, out var timestamps))
                keys.Add(source, timestamps = new(Comparer<V>.Create(CompareTimestamp)));
            if (!timestamps.Add(report.TimestampValue!))
                throw new InvalidDataException("Duplicate telemetry source timestamp in store.");
            records.Add(new(source, packed, report));
            offset += (int)length;
        }
        if (offset != bytes.Length - 32) throw new InvalidDataException("Telemetry store has trailing or missing records.");
        return records;
    }

    private async Task PersistAsync(List<SidebandTelemetryStoredReport> records, CancellationToken cancellationToken)
    {
        var length = 44L + records.Sum(r => 20L + r.Packed.Length);
        if (length > _options.MaxBytes) throw new InvalidOperationException("Telemetry byte limit reached; prune explicitly.");
        var bytes = new byte[checked((int)length)];
        Magic.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), (uint)records.Count);
        var offset = 12;
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Convert.FromHexString(record.SourceHash).CopyTo(bytes, offset);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset + 16, 4), (uint)record.Packed.Length);
            record.Packed.Span.CopyTo(bytes.AsSpan(offset + 20));
            offset += 20 + record.Packed.Length;
        }
        SHA256.HashData(bytes.AsSpan(0, offset), bytes.AsSpan(offset, 32));
        var staging = Path.Combine(DataDirectory, "telemetry.write-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var file = new FileStream(staging, StreamOptions(FileMode.CreateNew, FileAccess.Write, FileShare.None)))
            {
                await file.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staging, StorePath, overwrite: true);
            // Rename is the commit point. Do not report cancellation after a durable state change.
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (File.Exists(staging)) File.Delete(staging);
        }
    }

    private CancellationTokenSource Operation(CancellationToken cancellationToken)
    {
        lock (_lifetime)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifetime)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            _closing = true;
            _disposeTask = DisposeCoreAsync();
            return new(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        _shutdown.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await _lease.DisposeAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private sealed class TimestampComparer : IComparer<SidebandTelemetryStoredReport>
    {
        public static TimestampComparer Instance { get; } = new();
        public int Compare(SidebandTelemetryStoredReport? x, SidebandTelemetryStoredReport? y) =>
            x is null ? y is null ? 0 : -1 : y is null ? 1 : CompareTimestamp(x.TimestampValue, y.TimestampValue);
    }
    private static bool ValidTimestamp(V? value) => value is not null &&
        (value.Kind == K.Integer || value.Kind == K.UnsignedInteger && value.UnsignedInteger <= long.MaxValue ||
         value.Kind == K.Number && value.Number >= long.MinValue && value.Number < 9223372036854775808d);
    private static int CompareTimestamp(V first, V second)
    {
        static (BigInteger Numerator, int Shift) Rational(V value)
        {
            if (value.Integer is { } signed) return (new(signed), 0);
            if (value.UnsignedInteger is { } unsigned) return (new(unsigned), 0);
            var bits = BitConverter.DoubleToUInt64Bits(value.Number!.Value);
            var exponent = (int)((bits >> 52) & 0x7ff);
            var mantissa = new BigInteger(bits & 0x000f_ffff_ffff_ffffUL);
            if (exponent != 0) mantissa += BigInteger.One << 52;
            if ((bits >> 63) != 0) mantissa = -mantissa;
            return (mantissa, exponent == 0 ? -1074 : exponent - 1075);
        }
        var a = Rational(first); var b = Rational(second);
        var common = Math.Min(a.Shift, b.Shift);
        return (a.Numerator << (a.Shift - common)).CompareTo(b.Numerator << (b.Shift - common));
    }
    private static void ValidateBound(double? value)
    {
        if (value is { } number && !double.IsFinite(number)) throw new ArgumentOutOfRangeException(nameof(value));
    }
    private static string NormalizeSource(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length != 32 || !source.All(Uri.IsHexDigit)) throw new ArgumentException("Require a 16-byte source hash encoded as 32 hex characters.", nameof(source));
        return source.ToLowerInvariant();
    }
    private static FileStreamOptions StreamOptions(FileMode mode, FileAccess access, FileShare share)
    {
        var options = new FileStreamOptions { Mode = mode, Access = access, Share = share, Options = FileOptions.Asynchronous, BufferSize = 16_384 };
        if (!OperatingSystem.IsWindows() && mode is FileMode.CreateNew or FileMode.OpenOrCreate)
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }
    private static void PrivateFile(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
