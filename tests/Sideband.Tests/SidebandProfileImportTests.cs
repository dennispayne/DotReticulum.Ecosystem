using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using DotReticulum.Core;
using LXMF;
using Sideband;
using Sideband.Desktop;

namespace Sideband.Tests;

// GenerateSidebandProfile.py executes unchanged persistence methods at primary Sideband
// d069122af14880e43607aed0b71add4c7806fbe0; core.py SHA-256
// 8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25.
// The committed fixture is an empty upstream-created database and non-secret synthetic config.
[Trait("Category", "Integration")]
public sealed class SidebandProfileImportTests
{
    public static bool ReferenceProfileIsUnavailable => string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_PROFILE"));

    [Fact]
    public async Task EmptyUpstreamProfileImportsIdentityConfigurationAndRetainsOriginals()
    {
        using var profile = new Profile();
        var before = profile.Fingerprint();
        var result = await SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, TestContext.Current.CancellationToken);
        var state = await new SidebandStateStore(profile.Target).LoadAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Python Profile", state.DisplayName);
        Assert.Equal("Light", state.Theme);
        Assert.True(state.PeriodicSynchronization);
        Assert.Equal(1800, state.SynchronizationIntervalSeconds);
        Assert.False(state.TryPropagationOnFail);
        Assert.Equal("00112233445566778899aabbccddeeff", state.PropagationNode);
        Assert.Equal("0001", state.ImportedSettings!["profile_fixture_unknown"].GetProperty("binary").GetString());
        Assert.Equal(Path.Combine(profile.Target, "reticulum"), state.ReticulumConfigDirectory);
        Assert.Equal(before, profile.Fingerprint());
        using var original = IdentityFile.Load(Path.Combine(profile.Source, "primary_identity"));
        using var imported = IdentityFile.Load(Path.Combine(profile.Target, "reticulum", "storage", "sideband"));
        Assert.Equal(original.Hash, imported.Hash);
        Assert.Equal(Convert.ToHexStringLower(LxMessage.DeliveryDestinationHash(original)), result.DeliveryHash);
        Assert.Equal(result.DeliveryHash, state.OwnDeliveryHash);
        Assert.False(File.Exists(Path.Combine(profile.Target, "imported-profile", "primary_identity")));
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(profile.Source, "sideband_config"), TestContext.Current.CancellationToken),
            await File.ReadAllBytesAsync(Path.Combine(profile.Target, "imported-profile", "sideband_config"), TestContext.Current.CancellationToken));
        Assert.Contains(result.Notices, notice => notice.Contains("profile_fixture_unknown", StringComparison.Ordinal));
        Assert.Contains(result.Notices, notice => notice.Contains("Trust, unread", StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Path.Combine(profile.Target, "reticulum", "storage", "sideband")));
        }
    }

    [Fact]
    public async Task ExistingDestinationAndIdentityAreNeverReplacedAndRetryIsSafe()
    {
        using var profile = new Profile();
        Directory.CreateDirectory(profile.Target);
        var sentinel = Path.Combine(profile.Target, "state.json");
        await File.WriteAllTextAsync(sentinel, "unrelated native profile", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, TestContext.Current.CancellationToken));
        Assert.Equal("unrelated native profile", await File.ReadAllTextAsync(sentinel, TestContext.Current.CancellationToken));
        File.Delete(sentinel);
        Directory.Delete(profile.Target);
        await SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, TestContext.Current.CancellationToken);
        var state = await File.ReadAllBytesAsync(sentinel, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, TestContext.Current.CancellationToken));
        Assert.Equal(state, await File.ReadAllBytesAsync(sentinel, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("config")]
    [InlineData("config-type")]
    [InlineData("schema")]
    [InlineData("database")]
    [InlineData("file-bound")]
    [InlineData("rows-bound")]
    [InlineData("message")]
    [InlineData("unrelated-message")]
    [InlineData("missing-conversation")]
    [InlineData("bad-attachment")]
    public async Task InvalidInputRollsBackWithoutChangingSourceAndCanRecover(string scenario)
    {
        using var profile = new Profile();
        var identityPath = Path.Combine(profile.Source, "primary_identity");
        var databasePath = Path.Combine(profile.Source, "sideband.db");
        switch (scenario)
        {
            case "identity":
                await File.WriteAllBytesAsync(identityPath, new byte[63], TestContext.Current.CancellationToken);
                break;
            case "config":
                await File.WriteAllBytesAsync(Path.Combine(profile.Source, "sideband_config"), [0xC1], TestContext.Current.CancellationToken);
                break;
            case "config-type":
                await File.WriteAllBytesAsync(Path.Combine(profile.Source, "sideband_config"),
                    MsgPack.Pack(new Dictionary<object, object?> { ["lxmf_sync_interval"] = 1.5 }), TestContext.Current.CancellationToken);
                break;
            case "schema":
                WriteSql(databasePath, "ALTER TABLE conv RENAME COLUMN dest_context TO unsupported");
                break;
            case "database":
                await File.WriteAllBytesAsync(databasePath, "not SQLite"u8.ToArray(), TestContext.Current.CancellationToken);
                break;
            case "file-bound":
                using (var stream = File.OpenWrite(databasePath)) stream.SetLength(128L * 1024 * 1024 + 1);
                break;
            case "rows-bound":
                WriteSql(databasePath, "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<1001) INSERT INTO conv SELECT randomblob(16),0,0,0,1,0,X'61',X'C0' FROM n");
                break;
            default:
                AddMessage(profile, scenario);
                break;
        }
        var before = profile.Fingerprint();
        var exception = await Record.ExceptionAsync(() => SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, TestContext.Current.CancellationToken));
        Assert.NotNull(exception);
        Assert.False(Directory.Exists(profile.Target));
        Assert.Equal(before, profile.Fingerprint());
        Assert.Empty(Directory.GetDirectories(profile.Root, ".native.import-*"));
        if (scenario == "identity")
        {
            File.Delete(identityPath);
            using var replacement = Identity.Generate();
            IdentityFile.SaveNew(replacement, identityPath);
            await SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, TestContext.Current.CancellationToken);
            Assert.True(File.Exists(Path.Combine(profile.Target, "state.json")));
        }
    }

    [Fact]
    public async Task ConversationMessageFieldsAndDeliveryMethodImportUsingPublicLxmfDecoder()
    {
        using var profile = new Profile();
        AddMessage(profile, "valid");
        var result = await SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, TestContext.Current.CancellationToken);
        Assert.Equal(1, result.Conversations);
        Assert.Equal(1, result.Messages);
        var state = await new SidebandStateStore(profile.Target).LoadAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Reference contact", Assert.Single(state.Contacts).DisplayName);
        var message = Assert.Single(state.Messages);
        Assert.False(message.Incoming);
        Assert.Equal("Test content", message.Content);
        Assert.Equal("Propagated", message.DeliveryMethod);
        Assert.Equal("Delivered", message.DeliveryState);
        Assert.Equal(2, message.Attachments!.Count);
        var attachment = message.Attachments[0];
        Assert.Equal("reference.txt", attachment.Name);
        Assert.Equal("content", await File.ReadAllTextAsync(Path.Combine(profile.Target, "attachments", attachment.ContentHash), TestContext.Current.CancellationToken));
        var store = new SidebandStateStore(profile.Target);
        await store.UpdateMessageStateAsync(message.Id, "Failed", TestContext.Current.CancellationToken, deliveryMethod: "Direct");
        Assert.Equal("Direct", Assert.Single((await store.LoadAsync(cancellationToken: TestContext.Current.CancellationToken)).Messages).DeliveryMethod);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UpdateMessageStateAsync(message.Id, "Failed",
            TestContext.Current.CancellationToken, deliveryMethod: "Invented"));
    }

    [Fact]
    public async Task CancellationIsExplicitAndDoesNotCreateDestination()
    {
        using var profile = new Profile();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, cancellation.Token));
        Assert.False(Directory.Exists(profile.Target));
        using var output = new StringWriter();
        Assert.Equal(130, await ProfileImportCommand.RunAsync(profile.Source, profile.Target, output, cancellation.Token));
        Assert.Contains("cancelled", output.ToString());
    }

    [Fact]
    public async Task CancellationDuringSnapshotRollsBackAndConcurrentImportsCommitOnlyOnce()
    {
        using var profile = new Profile();
        var database = Path.Combine(profile.Source, "sideband.db");
        var originalLength = new FileInfo(database).Length;
        using (var stream = File.OpenWrite(database)) stream.SetLength(128L * 1024 * 1024);
        using var cancellation = new CancellationTokenSource();
        var importing = SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importing);
        Assert.False(Directory.Exists(profile.Target));
        Assert.Empty(Directory.GetDirectories(profile.Root, ".native.import-*"));
        using (var stream = File.OpenWrite(database)) stream.SetLength(originalLength);
        var first = SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, TestContext.Current.CancellationToken);
        var second = SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, TestContext.Current.CancellationToken);
        var outcomes = await Task.WhenAll(Record.ExceptionAsync(() => first).AsTask(), Record.ExceptionAsync(() => second).AsTask());
        Assert.Single(outcomes, outcome => outcome is null);
        Assert.IsType<IOException>(Assert.Single(outcomes, outcome => outcome is not null));
        Assert.True(File.Exists(Path.Combine(profile.Target, "state.json")));
        Assert.True(File.Exists(Path.Combine(profile.Target, "reticulum", "storage", "sideband")));
        Assert.Empty(Directory.GetDirectories(profile.Root, ".native.import-*"));
    }

    [Fact]
    public async Task MaintenanceCommandReportsFailureWithoutEchoingSecretValues()
    {
        using var profile = new Profile();
        const string secret = "PRIVATE-NOT-FOR-LOGGING";
        await File.WriteAllBytesAsync(Path.Combine(profile.Source, "sideband_config"),
            MsgPack.Pack(new Dictionary<object, object?> { ["display_name"] = secret, ["lxmf_sync_interval"] = secret }),
            TestContext.Current.CancellationToken);
        using var output = new StringWriter();
        Assert.Equal(1, await ProfileImportCommand.RunAsync(profile.Source, profile.Target, output, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(secret, output.ToString());
        Assert.False(Directory.Exists(profile.Target));
    }

    [Fact(SkipWhen = nameof(ReferenceProfileIsUnavailable), Skip = "Set DOTRETICULUM_SIDEBAND_PROFILE to an ephemeral profile produced by GenerateSidebandProfile.py.")]
    public async Task ActualUpstreamHistoryAndWalImportWithoutSourceModification()
    {
        using var profile = new Profile();
        Directory.Delete(profile.Source, true);
        var reference = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_PROFILE")!;
        var storage = Path.Combine(reference, "app_storage");
        Directory.CreateDirectory(profile.Source);
        foreach (var file in Directory.GetFiles(storage))
            File.Copy(file, Path.Combine(profile.Source, Path.GetFileName(file)));
        Assert.True(new FileInfo(Path.Combine(profile.Source, "sideband.db-wal")).Length > 0);
        var before = profile.Fingerprint();
        var result = await SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, TestContext.Current.CancellationToken);
        var state = await new SidebandStateStore(profile.Target).LoadAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Messages);
        Assert.Equal(1, result.Conversations);
        Assert.Equal(before, profile.Fingerprint());
        using var observations = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(reference, "observations.json"), TestContext.Current.CancellationToken));
        Assert.Equal(observations.RootElement.GetProperty("ownDeliveryHash").GetString(), state.OwnDeliveryHash);
        var incoming = Assert.Single(state.Messages, message => message.Incoming);
        Assert.Equal("Native migration inbound", incoming.Content);
        Assert.Equal("Direct", incoming.DeliveryMethod);
        Assert.Equal(1760000001.5, (incoming.Timestamp - DateTimeOffset.UnixEpoch).TotalSeconds);
        Assert.Equal("Native migration outbound", Assert.Single(state.Messages, message => !message.Incoming).Content);
        Assert.Equal("Propagated", Assert.Single(state.Messages, message => !message.Incoming).DeliveryMethod);
        var attachment = Assert.Single(incoming.Attachments!);
        Assert.Equal("Python attachment\n", await File.ReadAllTextAsync(Path.Combine(profile.Target, "attachments", attachment.ContentHash), TestContext.Current.CancellationToken));
    }

    private static void AddMessage(Profile profile, string scenario)
    {
        using var own = IdentityFile.Load(Path.Combine(profile.Source, "primary_identity"));
        using var peer = Identity.Generate();
        var peerHash = LxMessage.DeliveryDestinationHash(peer);
        var fields = new Dictionary<object, object?>
        {
            [LxmField.FileAttachments] = new object?[]
            {
                new object?[] { "reference.txt", "content"u8.ToArray() },
                new object?[] { scenario == "bad-attachment" ? "../unsafe" : "second.txt", "second"u8.ToArray() },
            },
        };
        var message = LxMessage.Create(peerHash, scenario == "unrelated-message" ? peer : own,
            "Test content", fields: fields, method: LxmMethod.Propagated, timestamp: 1760000000.25);
        var packed = message.Pack(destinationIdentity: peer);
        if (scenario == "message") packed[^1] ^= 0x01;
        var sql = "";
        if (scenario != "missing-conversation")
            sql += $"INSERT INTO conv VALUES (X'{Convert.ToHexString(peerHash)}',0,0,1,1,1,X'{Convert.ToHexString("Reference contact"u8.ToArray())}',X'C0');";
        sql += $"INSERT INTO lxm VALUES(X'{Convert.ToHexString(message.Hash!)}',X'{Convert.ToHexString(message.DestinationHash)}',X'{Convert.ToHexString(message.SourceHash)}',X'',0,0,8,3,1,0,X'{Convert.ToHexString(packed)}',X'80');";
        WriteSql(Path.Combine(profile.Source, "sideband.db"), sql);
    }

    private static void WriteSql(string path, string sql)
    {
        var library = NativeLibrary.Load(OperatingSystem.IsWindows() ? "winsqlite3.dll" :
            OperatingSystem.IsMacOS() ? "libsqlite3.dylib" : "libsqlite3.so.0");
        try
        {
            var open = Marshal.GetDelegateForFunctionPointer<Open>(NativeLibrary.GetExport(library, "sqlite3_open_v2"));
            var exec = Marshal.GetDelegateForFunctionPointer<Exec>(NativeLibrary.GetExport(library, "sqlite3_exec"));
            var close = Marshal.GetDelegateForFunctionPointer<Close>(NativeLibrary.GetExport(library, "sqlite3_close_v2"));
            Assert.Equal(0, open(path, out var database, 2, IntPtr.Zero));
            try { Assert.Equal(0, exec(database, sql, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero)); }
            finally { close(database); }
        }
        finally { NativeLibrary.Free(library); }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr database, int flags, IntPtr vfs);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Exec(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr argument, IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Close(IntPtr database);

    private sealed class Profile : IDisposable
    {
        internal string Root { get; } = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "profile-tests-" + Guid.NewGuid().ToString("N"));
        internal string Source => Path.Combine(Root, "source");
        internal string Target => Path.Combine(Root, "native");
        internal Profile()
        {
            Directory.CreateDirectory(Source);
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-profile.json")));
            File.WriteAllBytes(Path.Combine(Source, "sideband.db"), Convert.FromBase64String(fixture.RootElement.GetProperty("emptyDatabase").GetString()!));
            File.WriteAllBytes(Path.Combine(Source, "sideband_config"), Convert.FromBase64String(fixture.RootElement.GetProperty("configuration").GetString()!));
            using var identity = Identity.Generate();
            IdentityFile.SaveNew(identity, Path.Combine(Source, "primary_identity"));
        }

        internal string Fingerprint() => string.Join("|", Directory.GetFiles(Source).Order(StringComparer.Ordinal)
            .Select(path => Path.GetFileName(path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
