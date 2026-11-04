using System.Text;
using System.Text.Json;
using DotReticulum.Core;

namespace NomadNet.Tests;

// Generated FIRST using actual Directory persistence and NomadNetworkApp._persist_ignored_list at
// primary f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2. Generator records source/blob hashes and methods.
[Trait("Category", "Unit")]
public sealed class NomadNetDirectoryImportTests
{
    internal static JsonDocument Fixture() => JsonDocument.Parse(typeof(NomadNetDirectoryImportTests).Assembly
        .GetManifestResourceStream("NomadNet.Tests.NomadNetDirectoryImportVectors.json")!);

    [Fact]
    public void ActualPrimaryCurrentLegacyAndDuplicatePersistenceMatches()
    {
        using var fixture = Fixture();
        Assert.Equal("f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2", fixture.RootElement.GetProperty("commit").GetString());
        foreach (var vector in fixture.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var parsed = NomadNetDirectoryImport.Parse(Convert.FromBase64String(vector.GetProperty("directory").GetString()!),
                Convert.FromBase64String(vector.GetProperty("ignored").GetString()!));
            Assert.Empty(parsed.Warnings);
            Assert.Equal(vector.GetProperty("announceCount").GetInt32(), parsed.SkippedAnnounces);
            Assert.Equal(vector.GetProperty("ignoredHashes").EnumerateArray().Select(value => value.GetString()), parsed.IgnoredDestinations);
            var expected = vector.GetProperty("observed").EnumerateArray().ToArray();
            Assert.Equal(expected.Length, parsed.Contacts.Count);
            foreach (var observed in expected)
            {
                var contact = Assert.Single(parsed.Contacts, value => value.DestinationHash == observed.GetProperty("hash").GetString());
                Assert.Equal(observed.GetProperty("name").GetString(), contact.Name);
                Assert.Equal(observed.GetProperty("trust").GetInt32(), contact.TrustLevel);
                Assert.Equal(observed.GetProperty("node").GetBoolean(), contact.HostsNode);
                Assert.Equal(observed.GetProperty("identify").GetBoolean(), contact.IdentifyOnConnect);
                Assert.Equal(observed.GetProperty("delivery").GetInt32(), contact.PreferredDelivery);
                Assert.Equal(observed.GetProperty("rank").ValueKind == JsonValueKind.Null ? null :
                    (int?)observed.GetProperty("rank").GetInt32(), contact.SortRank);
                Assert.Equal(observed.GetProperty("notes").GetString(), contact.Notes);
            }
        }
    }

    [Fact]
    public void IgnoredLinesAreBoundedIndependentDeduplicatedAndReportedWithoutLeakingInput()
    {
        var directory = Pack([]);
        var ignored = Encoding.UTF8.GetBytes("ABCDEF0123456789ABCDEF0123456789\r\nshort\n" +
            new string('g', 32) + "\nabcdef0123456789abcdef0123456789\r" + new string(' ', 32) + "\n");
        var parsed = NomadNetDirectoryImport.Parse(directory, ignored);
        Assert.Equal("abcdef0123456789abcdef0123456789", Assert.Single(parsed.IgnoredDestinations));
        Assert.Contains("Skipped 3 malformed", Assert.Single(parsed.Warnings));
        Assert.Throws<InvalidDataException>(() => NomadNetDirectoryImport.Parse(directory,
            Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(new string('a', 32) + "\n", 10_001)))));
        Assert.Throws<InvalidDataException>(() => NomadNetDirectoryImport.Parse(directory,
            new byte[NomadNetDirectoryImport.MaximumIgnoredBytes + 1]));
    }

    [Fact]
    public void MalformedTruncatedExtendedAndUnsafePoliciesFailRatherThanLoseData()
    {
        object?[] valid = [new byte[16], "Contact", 2L, false, 1L, false, null, ""];
        foreach (var (index, value) in new (int, object?)[]
        {
            (0, new byte[15]), (0, new string('a', 32)), (1, 123L), (1, " padded "),
            (1, "\u001b[31m"), (1, new string('n', 81)), (2, 3L), (2, 255d), (2, long.MaxValue),
            (3, 1L), (4, 0L), (5, "yes"), (6, long.MaxValue), (7, new string('x', 4097)), (7, "\0"),
        })
        {
            var entry = valid.ToArray();
            entry[index] = value;
            Assert.Throws<InvalidDataException>(() => NomadNetDirectoryImport.Parse(Pack([entry])));
        }
        Assert.Throws<InvalidDataException>(() => NomadNetDirectoryImport.Parse(Pack([valid[..2]])));
        Assert.Throws<InvalidDataException>(() => NomadNetDirectoryImport.Parse(Pack([valid.Concat(new object?[] { true }).ToArray()])));
        var bytes = Pack([valid]);
        Assert.Throws<InvalidDataException>(() => NomadNetDirectoryImport.Parse(bytes[..^1]));
        Assert.Throws<InvalidDataException>(() => NomadNetDirectoryImport.Parse(bytes.Concat(new byte[] { 0xc0 }).ToArray()));
        Assert.Throws<InvalidDataException>(() => NomadNetDirectoryImport.Parse(new byte[NomadNetDirectoryImport.MaximumDirectoryBytes + 1]));
        Assert.Throws<InvalidDataException>(() => NomadNetDirectoryImport.Parse(MsgPack.Pack(new Dictionary<object, object?>
            { ["entry_list"] = new object?[] { valid } })));
        Assert.Throws<InvalidDataException>(() => NomadNetDirectoryImport.Parse(Pack(Enumerable.Repeat<object?>(valid, 10_001).ToArray())));
    }

    internal static byte[] Pack(object?[] entries) => MsgPack.Pack(new Dictionary<object, object?>
        { ["entry_list"] = entries, ["announce_stream"] = Array.Empty<object?>() });
}

[Trait("Category", "Integration")]
public sealed class NomadNetDirectoryImportPersistenceTests
{
    [Fact]
    public async Task SourceReaderHandlesAbsentIgnoreFileAndRejectsMissingOversizeOrCanceledInputs()
    {
        var root = Root();
        try
        {
            await Assert.ThrowsAsync<DirectoryNotFoundException>(() => NomadNetDirectoryImport.ReadAsync(root));
            Directory.CreateDirectory(Path.Combine(root, "storage"));
            var path = Path.Combine(root, "storage", "directory");
            var bytes = NomadNetDirectoryImportTests.Pack([]);
            await File.WriteAllBytesAsync(path, bytes);
            Assert.Empty((await NomadNetDirectoryImport.ReadAsync(root)).IgnoredDestinations);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NomadNetDirectoryImport.ReadAsync(root, canceled.Token));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            await File.WriteAllBytesAsync(path, new byte[NomadNetDirectoryImport.MaximumDirectoryBytes + 1]);
            await Assert.ThrowsAsync<InvalidDataException>(() => NomadNetDirectoryImport.ReadAsync(root));
            await File.WriteAllBytesAsync(path, bytes);
            await File.WriteAllBytesAsync(Path.Combine(root, "ignored"), new byte[NomadNetDirectoryImport.MaximumIgnoredBytes + 1]);
            await Assert.ThrowsAsync<InvalidDataException>(() => NomadNetDirectoryImport.ReadAsync(root));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ImportsAreAdditiveIdempotentDurableAndNeverEvictHistoryOrWeakenIgnorePolicy()
    {
        var root = Root();
        try
        {
            using var fixture = NomadNetDirectoryImportTests.Fixture();
            var current = fixture.RootElement.GetProperty("vectors")[0];
            var source = Path.Combine(root, "primary");
            Directory.CreateDirectory(Path.Combine(source, "storage"));
            var sourceBytes = Convert.FromBase64String(current.GetProperty("directory").GetString()!);
            await File.WriteAllBytesAsync(Path.Combine(source, "storage", "directory"), sourceBytes);
            await File.WriteAllBytesAsync(Path.Combine(source, "ignored"), Convert.FromBase64String(current.GetProperty("ignored").GetString()!));
            var import = await NomadNetDirectoryImport.ReadAsync(source);
            var target = Path.Combine(root, "native");
            var store = await NomadNetStore.OpenAsync(target);
            var hash = new string('f', 32);
            await store.SaveContactAsync(new(hash, "Native") { Ignored = true });
            await store.SaveMessageAsync(new("history", hash, "inbound", DateTimeOffset.UtcNow, "", "keep", "received"));
            var result = await store.ImportDirectoryAsync(import);
            Assert.Equal(4, result.AddedContacts);
            Assert.Equal(2, result.AddedIgnoredDestinations);
            Assert.Equal(1, result.SkippedAnnounces);
            Assert.True(await store.IsIgnoredAsync(hash));
            Assert.True((await store.FindContactAsync(import.Contacts[0].DestinationHash))!.Ignored);
            Assert.Equal(2, (await store.KnownNodesAsync()).Count);
            var stateBytes = await File.ReadAllBytesAsync(Path.Combine(target, "state.json"));
            var again = await store.ImportDirectoryAsync(import);
            Assert.Equal(0, again.AddedContacts);
            Assert.Equal(0, again.AddedIgnoredDestinations);
            var reopened = await NomadNetStore.OpenAsync(target);
            Assert.Equal(5, (await reopened.ContactsAsync()).Count);
            Assert.Equal("keep", Assert.Single(await reopened.MessagesAsync()).Content);
            Assert.Equal(3, (await reopened.IgnoredDestinationsAsync()).Count);
            Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(Path.Combine(source, "storage", "directory")));
            Assert.Equal(stateBytes, await File.ReadAllBytesAsync(Path.Combine(target, "state.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ConflictCancellationDiskFailureAndOversizeLeaveMemoryAndDiskUnchanged()
    {
        var root = Root();
        try
        {
            var store = await NomadNetStore.OpenAsync(root);
            var hash = new string('1', 32);
            await store.SaveContactAsync(new(hash, "Existing"));
            var path = Path.Combine(root, "state.json");
            var original = await File.ReadAllBytesAsync(path);
            NomadNetDirectoryImportData Data(params NomadNetContact[] contacts) => new(contacts, [new string('a', 32)], 0, []);
            var extra = new NomadNetContact(new string('2', 32), "Extra");
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.ImportDirectoryAsync(Data(extra, new(hash, "Conflicting"))));
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ImportDirectoryAsync(Data(extra), canceled.Token));
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            Assert.Single(await store.ContactsAsync());
            Assert.Empty(await store.IgnoredDestinationsAsync());
            File.Move(path, path + ".saved");
            Directory.CreateDirectory(path);
            var failure = await Record.ExceptionAsync(() => store.ImportDirectoryAsync(Data(extra)));
            Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
            Assert.Single(await store.ContactsAsync());
            Assert.Empty(await store.IgnoredDestinationsAsync());
            Assert.Empty(Directory.GetFiles(root, "*.new"));
            Directory.Delete(path);
            File.Move(path + ".saved", path);
            var large = Enumerable.Range(1, 1400).Select(index => new NomadNetContact(index.ToString("x32"), "Name")
                { Notes = new string('n', 4096) }).ToArray();
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportDirectoryAsync(Data(large)));
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            Assert.Single(await store.ContactsAsync());
            Assert.Empty(await store.IgnoredDestinationsAsync());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ConcurrentDuplicateImportsSerializeWithoutLostPolicies()
    {
        var root = Root();
        try
        {
            var store = await NomadNetStore.OpenAsync(root);
            var imports = Enumerable.Range(1, 16).Select(index => new NomadNetDirectoryImportData(
                [new(index.ToString("x32"), "Imported " + index)], [index.ToString("x32")], 0, [])).ToArray();
            await Task.WhenAll(imports.SelectMany(import => new[] { store.ImportDirectoryAsync(import), store.ImportDirectoryAsync(import) }));
            Assert.Equal(16, (await store.ContactsAsync()).Count);
            Assert.Equal(16, (await (await NomadNetStore.OpenAsync(root)).IgnoredDestinationsAsync()).Count);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Root()
    {
        var path = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomad-directory-import-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(path);
        return path;
    }
}
