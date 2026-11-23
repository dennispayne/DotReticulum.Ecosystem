using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotReticulum.Core;
using Sideband;

namespace Sideband.Tests;

// GenerateSidebandPersistentProfile.py executes unchanged persistence/scheduler methods at
// primary Sideband d069122af14880e43607aed0b71add4c7806fbe0, core.py SHA-256
// 8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25.
// The fixture discloses unused-import, clock, router and scheduler-loop boundary shims.
[Trait("Category", "Integration")]
public sealed class SidebandPersistentProfileTests
{
    public static bool ReferenceUnavailable =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_PERSISTENT_PROFILE"));

    [Theory]
    [InlineData("empty")]
    [InlineData("integer_timestamp")]
    [InlineData("fractional_timestamp")]
    [InlineData("negative_timestamp")]
    [InlineData("large_finite_timestamp")]
    [InlineData("unsigned_timestamp")]
    [InlineData("isolated_retry")]
    [InlineData("null_values")]
    [InlineData("initial_schedule")]
    [InlineData("disabled_preserves")]
    [InlineData("exact_boundary")]
    [InlineData("first_rejection")]
    [InlineData("second_rejection")]
    [InlineData("accepted_request")]
    [InlineData("future_timestamp")]
    [InlineData("unknown_state")]
    public async Task ActualUpstreamPersistentValuesActivateWithoutChangingSnapshot(string name)
    {
        using var profile = new Profile();
        var observation = profile.Case(name);
        profile.Install(observation);
        var before = profile.Fingerprint();
        var result = await SidebandPersistentProfile.ReadAsync(profile.Database, TestContext.Current.CancellationToken);
        AssertObservation(observation, result);
        Assert.Equal(before, profile.Fingerprint());
        if (name == "unknown_state")
            Assert.Contains(result.Notices, notice => notice.Contains("1 unsupported", StringComparison.Ordinal));
        else Assert.Empty(result.Notices);

        var store = new SidebandStateStore(Path.Combine(profile.Root, "native"));
        await store.SetSynchronizationStateAsync(result.LastSynchronization, result.SynchronizationRetrying,
            TestContext.Current.CancellationToken);
        var restarted = await new SidebandStateStore(store.DataDirectory).LoadAsync(
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(result.LastSynchronization, restarted.LastSynchronization);
        Assert.Equal(result.SynchronizationRetrying, restarted.SynchronizationRetrying);

        if (observation.GetProperty("now").ValueKind != JsonValueKind.Null)
        {
            var decision = SidebandSynchronizationPolicy.Advance(observation.GetProperty("enabled").GetBoolean(), 1800,
                Number(observation.GetProperty("initialLastSynchronization")),
                observation.GetProperty("initialSynchronizationRetrying").ValueKind == JsonValueKind.True,
                observation.GetProperty("now").GetDouble(), observation.GetProperty("requestAccepted").GetBoolean());
            Assert.Equal(result.LastSynchronization, decision.LastSync);
            Assert.Equal(result.SynchronizationRetrying, decision.Retrying);
        }
    }

    [Fact]
    public async Task CommittedWalUpdatesOverrideMainDatabaseAndSupportConcurrentReads()
    {
        using var profile = new Profile();
        foreach (var file in profile.Fixture.GetProperty("walFiles").EnumerateObject())
            File.WriteAllBytes(Path.Combine(profile.Root, file.Name), Convert.FromBase64String(file.Value.GetString()!));
        var before = profile.Fingerprint();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            SidebandPersistentProfile.ReadAsync(profile.Database, TestContext.Current.CancellationToken)));
        foreach (var result in results)
            AssertObservation(profile.Fixture.GetProperty("walObservation"), result);
        Assert.Equal(before, profile.Fingerprint());
        File.Delete(profile.Database + "-wal");
        File.Delete(profile.Database + "-shm");
        var mainBefore = profile.Fingerprint();
        var mainOnly = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            SidebandPersistentProfile.ReadAsync(profile.Database, TestContext.Current.CancellationToken)));
        foreach (var result in mainOnly)
        {
            Assert.Equal(1760000000, result.LastSynchronization);
            Assert.False(result.SynchronizationRetrying);
        }
        Assert.Equal(mainBefore, profile.Fingerprint());
    }

    [Fact]
    public async Task RollbackJournalIsRefusedWithoutModifyingSnapshotAndRecoveryIsPossible()
    {
        using var profile = new Profile();
        profile.Install(profile.Case("first_rejection"));
        var journal = profile.Database + "-journal";
        await File.WriteAllBytesAsync(journal, [1, 2, 3], TestContext.Current.CancellationToken);
        var before = profile.Fingerprint();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SidebandPersistentProfile.ReadAsync(profile.Database, TestContext.Current.CancellationToken));
        Assert.Equal(before, profile.Fingerprint());
        File.Delete(journal);
        var result = await SidebandPersistentProfile.ReadAsync(profile.Database, TestContext.Current.CancellationToken);
        AssertObservation(profile.Case("first_rejection"), result);
    }

    [Theory]
    [InlineData("timestamp-bool")]
    [InlineData("timestamp-string")]
    [InlineData("timestamp-nan")]
    [InlineData("timestamp-infinite")]
    [InlineData("retry-integer")]
    [InlineData("retry-string")]
    [InlineData("array")]
    [InlineData("map")]
    [InlineData("reserved")]
    [InlineData("truncated")]
    [InlineData("trailing")]
    [InlineData("oversized")]
    [InlineData("sql-null")]
    [InlineData("sql-text")]
    public async Task InvalidKnownValuesFailWithoutPartialActivationAndRecoveryIsSafe(string scenario)
    {
        using var profile = new Profile();
        profile.Install(profile.Case("fractional_timestamp"));
        var retry = scenario.StartsWith("retry-", StringComparison.Ordinal);
        byte[] value = scenario switch
        {
            "timestamp-bool" => MsgPack.Pack(true),
            "timestamp-string" or "retry-string" => MsgPack.Pack("1"),
            "timestamp-nan" => MsgPack.Pack(double.NaN),
            "timestamp-infinite" => MsgPack.Pack(double.PositiveInfinity),
            "retry-integer" => MsgPack.Pack(1),
            "array" => MsgPack.Pack(new object?[] { 1 }),
            "map" => MsgPack.Pack(new Dictionary<object, object?> { [1] = true }),
            "reserved" => [0xc1],
            "truncated" => [0xcb, 0],
            "trailing" => [1, 0],
            "oversized" => new byte[10],
            _ => [0xc0],
        };
        var literal = scenario switch
        {
            "sql-null" => "NULL",
            "sql-text" => "'123'",
            _ => $"X'{Convert.ToHexString(value)}'",
        };
        profile.Sql($"UPDATE persistent SET value={literal} WHERE property={Key(retry ? "lxmf.syncretrying" : "lxmf.lastsync")}");
        var before = profile.Fingerprint();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SidebandPersistentProfile.ReadAsync(profile.Database, TestContext.Current.CancellationToken));
        Assert.Equal(before, profile.Fingerprint());
        profile.Sql("DELETE FROM persistent");
        profile.Install(profile.Case("first_rejection"));
        var recovered = await SidebandPersistentProfile.ReadAsync(profile.Database, TestContext.Current.CancellationToken);
        AssertObservation(profile.Case("first_rejection"), recovered);
    }

    [Fact]
    public async Task UnknownPropertiesAreNeverDecodedAndTextKeysAreNotUpstreamBinaryKeys()
    {
        using var profile = new Profile();
        profile.Install(profile.Case("integer_timestamp"));
        profile.Sql("INSERT INTO persistent VALUES(X'FF', zeroblob(1048576));" +
            "INSERT INTO persistent VALUES('lxmf.syncretrying', X'C1');");
        var before = profile.Fingerprint();
        var result = await SidebandPersistentProfile.ReadAsync(profile.Database, TestContext.Current.CancellationToken);
        AssertObservation(profile.Case("integer_timestamp"), result);
        Assert.Contains(result.Notices, notice => notice.Contains("2 unsupported", StringComparison.Ordinal));
        Assert.Equal(before, profile.Fingerprint());
    }

    [Theory]
    [InlineData("renamed-column")]
    [InlineData("wrong-type")]
    [InlineData("missing-primary-key")]
    [InlineData("extra-column")]
    [InlineData("view")]
    [InlineData("row-bound")]
    [InlineData("file-bound")]
    [InlineData("wal-bound")]
    [InlineData("shm-bound")]
    [InlineData("corrupt")]
    public async Task MalformedSchemaAndFileOrRowLimitsFailExplicitly(string scenario)
    {
        using var profile = new Profile();
        switch (scenario)
        {
            case "renamed-column":
                profile.Sql("ALTER TABLE persistent RENAME COLUMN property TO unsupported");
                break;
            case "wrong-type":
                profile.Sql("DROP TABLE persistent; CREATE TABLE persistent(property TEXT PRIMARY KEY, value BLOB)");
                break;
            case "missing-primary-key":
                profile.Sql("DROP TABLE persistent; CREATE TABLE persistent(property BLOB, value BLOB)");
                break;
            case "extra-column":
                profile.Sql("ALTER TABLE persistent ADD COLUMN unsupported BLOB");
                break;
            case "view":
                profile.Sql("DROP TABLE persistent; CREATE VIEW persistent AS SELECT X'01' AS property, X'C0' AS value");
                break;
            case "row-bound":
                profile.Sql("WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<65537) " +
                    "INSERT INTO persistent SELECT cast(i AS BLOB),X'C0' FROM n");
                break;
            case "corrupt":
                File.WriteAllText(profile.Database, "not SQLite");
                break;
            default:
                var path = profile.Database + (scenario == "wal-bound" ? "-wal" : scenario == "shm-bound" ? "-shm" : "");
                using (var stream = File.OpenWrite(path))
                    stream.SetLength((scenario == "shm-bound" ? 8L : 128L) * 1024 * 1024 + 1);
                break;
        }
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SidebandPersistentProfile.ReadAsync(profile.Database, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingTableUsesDefaultsAndCancelledOrMissingSnapshotNeverCreatesState()
    {
        using var profile = new Profile();
        profile.Sql("DROP TABLE persistent");
        var missingTable = await SidebandPersistentProfile.ReadAsync(profile.Database, TestContext.Current.CancellationToken);
        Assert.Null(missingTable.LastSynchronization);
        Assert.False(missingTable.SynchronizationRetrying);
        Assert.Contains(missingTable.Notices, notice => notice.Contains("No upstream persistent table", StringComparison.Ordinal));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SidebandPersistentProfile.ReadAsync(profile.Database, cancellation.Token));
        var missing = Path.Combine(profile.Root, "does-not-exist.db");
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            SidebandPersistentProfile.ReadAsync(missing, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(missing));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            SidebandPersistentProfile.ReadAsync(" ", TestContext.Current.CancellationToken));
    }

    [Fact(SkipWhen = nameof(ReferenceUnavailable), Skip = "Set DOTRETICULUM_SIDEBAND_PERSISTENT_PROFILE to GenerateSidebandPersistentProfile.py output.")]
    public async Task ActualUpstreamDatabaseFilesAndWalSnapshotAreReadWithoutChangingTheirContents()
    {
        var root = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_PERSISTENT_PROFILE")!;
        using var observations = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(root, "observations.json"), TestContext.Current.CancellationToken));
        foreach (var observation in observations.RootElement.GetProperty("cases").EnumerateArray())
        {
            var database = Path.Combine(root, observation.GetProperty("name").GetString()! + ".db");
            var before = SHA256.HashData(await File.ReadAllBytesAsync(database, TestContext.Current.CancellationToken));
            var result = await SidebandPersistentProfile.ReadAsync(database, TestContext.Current.CancellationToken);
            AssertObservation(observation, result);
            Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(database, TestContext.Current.CancellationToken)));
        }
        var wal = await SidebandPersistentProfile.ReadAsync(
            Path.Combine(root, "wal-snapshot", "sideband.db"), TestContext.Current.CancellationToken);
        AssertObservation(observations.RootElement.GetProperty("walObservation"), wal);
    }

    private static double? Number(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetDouble();

    private static void AssertObservation(JsonElement observation, SidebandPersistentProfileSnapshot result)
    {
        Assert.Equal(Number(observation.GetProperty("lastSynchronization")), result.LastSynchronization);
        Assert.Equal(observation.GetProperty("synchronizationRetrying").ValueKind == JsonValueKind.True, result.SynchronizationRetrying);
    }

    private static string Key(string value) => $"X'{Convert.ToHexString(Encoding.UTF8.GetBytes(value))}'";

    private sealed class Profile : IDisposable
    {
        private readonly JsonDocument _fixture = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-persistent-profile.json")));
        internal JsonElement Fixture => _fixture.RootElement;
        internal string Root { get; } = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "persistent-profile-tests-" + Guid.NewGuid().ToString("N"));
        internal string Database => Path.Combine(Root, "sideband.db");
        internal Profile()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllBytes(Database, Convert.FromBase64String(Fixture.GetProperty("emptyDatabase").GetString()!));
        }

        internal JsonElement Case(string name) =>
            Fixture.GetProperty("cases").EnumerateArray().Single(entry => entry.GetProperty("name").GetString() == name);

        internal void Install(JsonElement observation)
        {
            foreach (var row in observation.GetProperty("rows").EnumerateArray())
                Sql($"INSERT INTO persistent VALUES(X'{row.GetProperty("property").GetString()}'," +
                    $"X'{Convert.ToHexString(Convert.FromBase64String(row.GetProperty("value").GetString()!))}')");
        }

        internal string Fingerprint() => string.Join("|", Directory.GetFiles(Root, "sideband.db*")
            .Where(path => !path.EndsWith("-shm", StringComparison.Ordinal)).Order(StringComparer.Ordinal)
            .Select(path => Path.GetFileName(path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));

        internal void Sql(string sql)
        {
            var library = NativeLibrary.Load(OperatingSystem.IsWindows() ? "winsqlite3.dll" :
                OperatingSystem.IsMacOS() ? "libsqlite3.dylib" : "libsqlite3.so.0");
            try
            {
                var open = Marshal.GetDelegateForFunctionPointer<Open>(NativeLibrary.GetExport(library, "sqlite3_open_v2"));
                var exec = Marshal.GetDelegateForFunctionPointer<Exec>(NativeLibrary.GetExport(library, "sqlite3_exec"));
                var close = Marshal.GetDelegateForFunctionPointer<Close>(NativeLibrary.GetExport(library, "sqlite3_close_v2"));
                Assert.Equal(0, open(Database, out var database, 2, IntPtr.Zero));
                try { Assert.Equal(0, exec(database, sql, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero)); }
                finally { close(database); }
            }
            finally { NativeLibrary.Free(library); }
        }

        public void Dispose()
        {
            _fixture.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr database, int flags, IntPtr vfs);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Exec(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr argument, IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Close(IntPtr database);
}
