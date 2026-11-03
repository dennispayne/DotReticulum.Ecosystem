using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Sideband;
using V = Sideband.SidebandTelemetryValue;
using S = Sideband.SidebandTelemetrySensorId;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandTelemetryStoreTests
{
    private const double Now = 1_700_000_000;
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;
    private static string Source(int value) => Convert.ToHexStringLower(Enumerable.Repeat((byte)value, 16).ToArray());
    private static byte[] Wire(long timestamp, string information = "synthetic") =>
        SidebandTelemetryCodec.Encode(SidebandTelemetryCodec.Create(timestamp,
            SidebandTelemetryCodec.FromData(S.Information, V.Fields(("contents", V.From(information))))));
    private static JsonDocument Fixture() => JsonDocument.Parse(File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-telemetry-store-geo.json")));

    [Fact]
    public async Task ActualPrimaryAppendDuplicateSourceTimestampAndQueryBoundariesSurviveRestart()
    {
        using var directory = new OwnedDirectory();
        using var fixture = Fixture();
        var store = await SidebandTelemetryStore.OpenAsync(directory.Path, cancellationToken: TestCancellation);
        try
        {
            foreach (var item in fixture.RootElement.GetProperty("append").EnumerateArray())
            {
                var accepted = item.GetProperty("accepted").GetBoolean();
                var result = await store.AppendAsync(item.GetProperty("source").GetString()!,
                    Convert.FromHexString(item.GetProperty("wire_hex").GetString()!), Now, TestCancellation);
                Assert.Equal(accepted, result == SidebandTelemetryAppendResult.Added);
            }
            foreach (var item in fixture.RootElement.GetProperty("queries").EnumerateArray())
            {
                var arguments = V.Decode(Convert.FromHexString(item.GetProperty("arguments_hex").GetString()!));
                var query = new SidebandTelemetryQuery(
                    arguments.Get("context_dest")?.GetBinary() is { } source ? Convert.ToHexStringLower(source) : null,
                    arguments.Get("after")?.Number, arguments.Get("before")?.Number,
                    arguments.Get("limit")?.Integer is { } limit ? (int)limit : null);
                var records = await store.QueryAsync(query, TestCancellation);
                var expected = V.Decode(Convert.FromHexString(item.GetProperty("result_hex").GetString()!));
                if (item.GetProperty("name").GetString() == "global-unfiltered-limit-bug")
                {
                    Assert.Single(records);
                    Assert.True(expected.Members.Sum(m => m.Value.Items.Count) > records.Count);
                    continue;
                }
                var count = expected.IsNil ? 0 : expected.Members.Sum(m => m.Value.Items.Count);
                Assert.Equal(count, records.Count);
                foreach (var record in records)
                {
                    var sourceEntries = Assert.Single(expected.Members, m =>
                        Convert.ToHexStringLower(m.Key.GetBinary()!) == record.SourceHash).Value;
                    var entry = Assert.Single(sourceEntries.Items, e => e.Items[0].Number == record.TimestampUtcSeconds);
                    Assert.Equal(entry.Items[1].GetBinary(), record.GetPackedTelemetry());
                }
            }
            var recordsBefore = await store.QueryAsync(cancellationToken: TestCancellation);
            Assert.Equal(6, recordsBefore.Count);
            Assert.Equal(SidebandTelemetryAppendResult.Duplicate, await store.AppendAsync(Source(1), Wire(1699999990, "overwrite forbidden"), Now, TestCancellation));
            Assert.Equal(3, await store.ClearSourceAsync(Source(1), TestCancellation));
        }
        finally { await store.DisposeAsync(); }
        await using var reopened = await SidebandTelemetryStore.OpenAsync(directory.Path, cancellationToken: TestCancellation);
        Assert.Equal(3, (await reopened.QueryAsync(cancellationToken: TestCancellation)).Count);
        Assert.Empty(await reopened.QueryAsync(new(Source(1)), TestCancellation));
    }

    [Fact]
    public async Task PrimaryCleanupDefectIsExplicitlySelectableAndItsStrictBoundariesMatchExecutedSqlite()
    {
        using var directory = new OwnedDirectory();
        await using var store = await SidebandTelemetryStore.OpenAsync(directory.Path, cancellationToken: TestCancellation);
        using var fixture = Fixture();
        var retention = fixture.RootElement.GetProperty("retention");
        Assert.Equal(SidebandTelemetryRetention.PrimaryKeepSeconds, retention.GetProperty("keep").GetInt32());
        Assert.Equal(SidebandTelemetryRetention.PrimaryFutureGraceSeconds, retention.GetProperty("future_grace").GetInt32());
        foreach (var item in retention.GetProperty("inputs").EnumerateArray())
            Assert.Equal(SidebandTelemetryAppendResult.Added, await store.AppendAsync(Source(1), Wire(item.GetInt64()), Now, TestCancellation));
        var expected = retention.GetProperty("retained").EnumerateArray().Select(e => e.GetDouble()).Order().ToArray();
        var removed = await store.PruneAsync(SidebandTelemetryRetention.FromPrimary(Now), TestCancellation);
        Assert.Equal(retention.GetProperty("inputs").GetArrayLength() - expected.Length, removed);
        Assert.Equal(expected, (await store.QueryAsync(cancellationToken: TestCancellation)).Select(r => r.TimestampUtcSeconds).Order());
        Assert.Contains(Now - 240, expected);
        Assert.DoesNotContain(Now, expected);
    }

    [Fact]
    public async Task SizeRecordAndQueryBoundsAreTransactionalAndInputOwnershipIsExplicit()
    {
        using var directory = new OwnedDirectory();
        var firstWire = Wire(100);
        await using var store = await SidebandTelemetryStore.OpenAsync(directory.Path,
            new() { MaxRecords = 1, MaxBytes = 44 + 20 + firstWire.Length, MaxQueryRecords = 1 }, TestCancellation);
        Assert.Equal(SidebandTelemetryAppendResult.Added, await store.AppendAsync(Source(1), firstWire, Now, TestCancellation));
        var before = await File.ReadAllBytesAsync(store.StorePath, TestCancellation);
        firstWire[0] = 0;
        var record = Assert.Single(await store.QueryAsync(cancellationToken: TestCancellation));
        var copy = record.GetPackedTelemetry(); copy[0] = 0;
        Assert.NotEqual(0, record.GetPackedTelemetry()[0]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendAsync(Source(2), Wire(101), Now, TestCancellation));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.StorePath, TestCancellation));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.QueryAsync(new(Limit: 2), TestCancellation));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AppendAsync("bad", Wire(1), Now, TestCancellation));
        await Assert.ThrowsAsync<ArgumentException>(() => store.PruneAsync(new(100, 1), TestCancellation));
        Assert.Equal(0, await store.ClearSourceAsync(Source(2), TestCancellation));
        Assert.Equal(1, await store.ClearSourceAsync(Source(1), TestCancellation));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendAsync(Source(1), Wire(100, new string('x', 100)), Now, TestCancellation));
        Assert.Empty(await store.QueryAsync(cancellationToken: TestCancellation));
    }

    [Fact]
    public async Task MissingFutureAndLargeExactTimestampSemanticsAreNotReplacedByReceiptTime()
    {
        using var directory = new OwnedDirectory();
        await using var store = await SidebandTelemetryStore.OpenAsync(directory.Path, cancellationToken: TestCancellation);
        Assert.Equal(SidebandTelemetryAppendResult.InvalidTimestamp, await store.AppendAsync(Source(1), Convert.FromHexString("80"), Now, TestCancellation));
        Assert.Equal(SidebandTelemetryAppendResult.InvalidTimestamp, await store.AppendAsync(Source(1), Wire((long)Now + 241), Now, TestCancellation));
        Assert.Equal(SidebandTelemetryAppendResult.Added, await store.AppendAsync(Source(1), Wire((long)Now + 240), Now, TestCancellation));
        Assert.Equal(SidebandTelemetryAppendResult.Added, await store.AppendAsync(Source(1), Wire(-1), Now, TestCancellation));
        var floatWire = SidebandTelemetryCodec.Encode(SidebandTelemetryCodec.Create(-1d));
        Assert.Equal(SidebandTelemetryAppendResult.Duplicate, await store.AppendAsync(Source(1), floatWire, Now, TestCancellation));
        const long large = 9_007_199_254_740_993;
        Assert.Equal(SidebandTelemetryAppendResult.Added, await store.AppendAsync(Source(2), Wire(large), large + 1000d, TestCancellation));
        Assert.Equal(SidebandTelemetryAppendResult.Added, await store.AppendAsync(Source(2), Wire(large - 1), large + 1000d, TestCancellation));
        var exact = await store.QueryAsync(new(Source(2)), TestCancellation);
        Assert.Equal(large, exact[0].TimestampValue.Integer);
        Assert.Equal(large - 1, exact[1].TimestampValue.Integer);
    }

    [Fact]
    public async Task CancellationConcurrentAppendDisposeAndExclusiveOwnershipPreserveWholeSnapshots()
    {
        using var directory = new OwnedDirectory();
        var store = await SidebandTelemetryStore.OpenAsync(directory.Path, cancellationToken: TestCancellation);
        await Assert.ThrowsAsync<IOException>(() => SidebandTelemetryStore.OpenAsync(directory.Path, cancellationToken: TestCancellation));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.AppendAsync(Source(1), Wire(1), Now, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.QueryAsync(cancellationToken: cancellation.Token));
        var same = Enumerable.Range(0, 16).Select(_ => store.AppendAsync(Source(1), Wire(1), Now, TestCancellation)).ToArray();
        var results = await Task.WhenAll(same);
        Assert.Equal(1, results.Count(r => r == SidebandTelemetryAppendResult.Added));
        Assert.Equal(15, results.Count(r => r == SidebandTelemetryAppendResult.Duplicate));
        var work = Enumerable.Range(2, 20).Select(i => store.AppendAsync(Source(1), Wire(i, new string('x', 60000)), Now, TestCancellation)).ToArray();
        var firstDispose = store.DisposeAsync().AsTask();
        var secondDispose = store.DisposeAsync().AsTask();
        Assert.Same(firstDispose, secondDispose);
        foreach (var task in work)
            try { await task; }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { }
        await Task.WhenAll(firstDispose, secondDispose);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.QueryAsync(cancellationToken: TestCancellation));
        await using var reopened = await SidebandTelemetryStore.OpenAsync(directory.Path, cancellationToken: TestCancellation);
        Assert.NotEmpty(await reopened.QueryAsync(cancellationToken: TestCancellation));
        Assert.Empty(Directory.GetFiles(directory.Path, "telemetry.write-*"));
    }

    [Fact]
    public async Task CorruptOversizedAndSemanticallyInvalidStoresFailWithoutResettingData()
    {
        using var directory = new OwnedDirectory();
        var store = await SidebandTelemetryStore.OpenAsync(directory.Path, cancellationToken: TestCancellation);
        await store.AppendAsync(Source(1), Wire(1), Now, TestCancellation);
        var path = store.StorePath;
        await store.DisposeAsync();
        var good = await File.ReadAllBytesAsync(path, TestCancellation);
        foreach (var mode in new[] { "truncated", "checksum", "count", "record-length", "timestamp" })
        {
            var bad = good.ToArray();
            if (mode == "truncated") bad = bad[..30];
            else if (mode == "checksum") bad[^1] ^= 1;
            else
            {
                if (mode == "count") BinaryPrimitives.WriteUInt32BigEndian(bad.AsSpan(8, 4), 10001);
                if (mode == "record-length") BinaryPrimitives.WriteUInt32BigEndian(bad.AsSpan(28, 4), uint.MaxValue);
                if (mode == "timestamp") bad[33] = 0x0d; // Replace sensor ID 1 with safe unknown ID; checksum stays valid.
                SHA256.HashData(bad.AsSpan(0, bad.Length - 32), bad.AsSpan(bad.Length - 32));
            }
            await File.WriteAllBytesAsync(path, bad, TestCancellation);
            await Assert.ThrowsAsync<InvalidDataException>(() => SidebandTelemetryStore.OpenAsync(directory.Path, cancellationToken: TestCancellation));
            Assert.Equal(bad, await File.ReadAllBytesAsync(path, TestCancellation));
        }
        await File.WriteAllBytesAsync(path, good, TestCancellation);
        await Assert.ThrowsAsync<InvalidDataException>(() => SidebandTelemetryStore.OpenAsync(directory.Path, new() { MaxBytes = 44 }, TestCancellation));
        await using var recovered = await SidebandTelemetryStore.OpenAsync(directory.Path, cancellationToken: TestCancellation);
        Assert.Single(await recovered.QueryAsync(cancellationToken: TestCancellation));
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }
    }

    [Fact]
    public async Task FailedAtomicRenameKeepsMemoryStateAndAllowsLaterWrites()
    {
        using var directory = new OwnedDirectory();
        await using var store = await SidebandTelemetryStore.OpenAsync(directory.Path, cancellationToken: TestCancellation);
        await store.AppendAsync(Source(1), Wire(100), Now, TestCancellation);
        var old = await File.ReadAllBytesAsync(store.StorePath, TestCancellation);
        var backup = System.IO.Path.Combine(directory.Path, "test-owned-backup");
        File.Move(store.StorePath, backup);
        Directory.CreateDirectory(store.StorePath);
        var failure = await Record.ExceptionAsync(() => store.AppendAsync(Source(1), Wire(101), Now, TestCancellation));
        Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        Assert.Single(await store.QueryAsync(cancellationToken: TestCancellation));
        Assert.Empty(Directory.GetFiles(directory.Path, "telemetry.write-*"));
        Assert.Equal(old, await File.ReadAllBytesAsync(backup, TestCancellation));
        Directory.Delete(store.StorePath);
        File.Move(backup, store.StorePath);
        Assert.Equal(SidebandTelemetryAppendResult.Added, await store.AppendAsync(Source(1), Wire(102), Now, TestCancellation));
        Assert.Equal(new double[] { 102, 100 }, (await store.QueryAsync(cancellationToken: TestCancellation)).Select(r => r.TimestampUtcSeconds));
    }

    private sealed class OwnedDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.GetFullPath(System.IO.Path.Combine("artifacts", "builds",
            "sideband-telemetry-contracts", "store-tests", Guid.NewGuid().ToString("N")));
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
