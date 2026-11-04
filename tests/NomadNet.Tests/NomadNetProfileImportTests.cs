using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotReticulum.Core;
using LXMF;

namespace NomadNet.Tests;

// Generated FIRST by executing the complete primary constructor and actual persistence APIs at
// f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2. The JSON records Git-blob SHA-256s; no fixture contains private keys.
[Trait("Category", "Integration")]
public sealed class NomadNetProfileImportTests
{
    [Fact]
    public async Task ImportsIdentityHistoryPaperFieldsAttachmentsSettingsAndHistoricalClaimsWithoutMutatingSource()
    {
        await using var profile = await Profile.CreateAsync();
        var before = Snapshot(profile.Source);
        var report = await profile.ImportAsync();
        Assert.Equal(4, report.Messages);
        Assert.Equal(1, report.Attachments);
        Assert.Equal(before, Snapshot(profile.Source));
        using var fixture = JsonDocument.Parse(typeof(NomadNetProfileImportTests).Assembly
            .GetManifestResourceStream("NomadNet.Tests.NomadNetProfileImportVectors.json")!);
        Assert.Equal("f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2",
            fixture.RootElement.GetProperty("commit").GetString());
        var store = await NomadNetStore.OpenAsync(profile.Target);
        var messages = await store.MessagesAsync();
        Assert.Equal(4, messages.Count);
        foreach (var expected in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var actual = Assert.Single(messages, message => message.Content == expected.GetProperty("content").GetString());
            Assert.Equal(expected.GetProperty("title").GetString(), actual.Title);
            Assert.Equal(expected.GetProperty("state").GetInt32(), actual.HistoricalState);
            Assert.Equal(expected.GetProperty("method").GetInt32(), actual.HistoricalMethod);
        }
        Assert.Equal(5, messages.Single(message => message.Content == "paper body").HistoricalMethod);
        Assert.Equal("paper", messages.Single(message => message.Content == "paper body").Status);
        Assert.Equal("failed", messages.Single(message => message.Content == "failed body").Status);
        var media = messages.Single(message => message.Content == "media-stripped body");
        Assert.Equal("container-hash-mismatch-not-authenticated", media.HistoricalAuthentication);
        Assert.True(media.HistoricalSignatureValidated);
        Assert.Equal("public.txt", media.ImportedAttachmentNames);
        Assert.Equal("public profile attachment"u8.ToArray(), await NomadNetAttachments.ReadAsync(profile.Target, media.Id, 0));
        Assert.Equal("remote-public-key-unavailable", messages.Single(message => message.Content == "incoming body").HistoricalAuthentication);
        Assert.Equal("verified-local-signature", messages.Single(message => message.Content == "paper body").HistoricalAuthentication);
        var settings = await NomadNetSettings.OpenAsync(profile.Target);
        Assert.Equal("Imported primary user", settings.DisplayName);
        Assert.Equal(Path.GetFullPath(profile.Rns), settings.RnsConfigDirectory);
        Assert.Null(settings.PagesPath);
        Assert.False(settings.EnableExecutablePages);
        Assert.Equal(2, (await store.ConversationsAsync()).Single().Unread);
        Assert.Equal(1, (await store.ConversationsAsync()).Single().Failures);
        Assert.True(File.Exists(Path.Combine(profile.Target, "profile-import.json")));
        var identity = await File.ReadAllBytesAsync(Path.Combine(profile.Target, "identity"));
        try
        {
            using var restored = Identity.FromPrivateKey(identity);
            Assert.Equal(profile.Own.HexHash, restored.HexHash);
        }
        finally { CryptographicOperations.ZeroMemory(identity); }
        var ratchets = await File.ReadAllBytesAsync(profile.NativeRatchets);
        try
        {
            var decoded = SignedDestinationRatchetFile.Decode(profile.Own, ratchets);
            Assert.Single(decoded);
            foreach (var key in decoded) CryptographicOperations.ZeroMemory(key);
        }
        finally { CryptographicOperations.ZeroMemory(ratchets); }
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(profile.Target, "identity")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(profile.Target));
        }
    }

    [Fact]
    public async Task ExistingTargetAndOverlappingRootsAreNeverChanged()
    {
        await using var profile = await Profile.CreateAsync();
        Directory.CreateDirectory(profile.Target);
        await File.WriteAllTextAsync(Path.Combine(profile.Target, "sentinel"), "unchanged");
        await Assert.ThrowsAsync<IOException>(() => profile.ImportAsync());
        Assert.Equal("unchanged", await File.ReadAllTextAsync(Path.Combine(profile.Target, "sentinel")));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NomadNetProfileImport.ImportAsync(profile.Source, Path.Combine(profile.Source, "native"), profile.Rns));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NomadNetProfileImport.ImportAsync(profile.Source, profile.Root, profile.Rns));
    }

    [Fact]
    public async Task BackupRecoveryIsExplicitAndCorruptBothDoesNotCreateTarget()
    {
        await using var profile = await Profile.CreateAsync();
        var peers = Path.Combine(profile.Source, "storage", "peersettings");
        File.Copy(peers, peers + ".bak");
        await File.WriteAllBytesAsync(peers, [0xc1]);
        var report = await profile.ImportAsync();
        Assert.Contains(report.Warnings, warning => warning.Contains("Recovered peer settings", StringComparison.Ordinal));
        Directory.Delete(profile.Target, true);
        await File.WriteAllBytesAsync(peers + ".bak", [0xc1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => profile.ImportAsync());
        Assert.False(Directory.Exists(profile.Target));
        Assert.Empty(Directory.GetDirectories(profile.Root, "native.profile-import-*"));
    }

    [Fact]
    public async Task FailedLateMessageRollsBackAlreadyWrittenAttachmentsAndIdentity()
    {
        await using var profile = await Profile.CreateAsync();
        var path = Path.Combine(profile.Conversation, new string('f', 64));
        await File.WriteAllBytesAsync(path, [0xc1]);
        var before = Snapshot(profile.Source);
        await Assert.ThrowsAnyAsync<Exception>(() => profile.ImportAsync());
        Assert.Equal(before, Snapshot(profile.Source));
        Assert.False(Directory.Exists(profile.Target));
        Assert.Empty(Directory.GetDirectories(profile.Root, "native.profile-import-*"));
        File.Delete(path);
        Assert.Equal(4, (await profile.ImportAsync()).Messages);
    }

    [Fact]
    public async Task PreCanceledImportPreservesAllSourceAndDoesNotPublish()
    {
        await using var profile = await Profile.CreateAsync();
        var before = Snapshot(profile.Source);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => profile.ImportAsync(cancellation.Token));
        Assert.Equal(before, Snapshot(profile.Source));
        Assert.False(Directory.Exists(profile.Target));
        Assert.Empty(Directory.GetDirectories(profile.Root, "native.profile-import-*"));
    }

    [Fact]
    public async Task CancellationAfterStagingAdmissionRollsBackPrivateState()
    {
        await using var profile = await Profile.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        using var watcher = new FileSystemWatcher(profile.Root, "native.profile-import-*")
        { NotifyFilter = NotifyFilters.DirectoryName };
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Created += (_, _) => { cancellation.Cancel(); observed.TrySetResult(); };
        watcher.EnableRaisingEvents = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => profile.ImportAsync(cancellation.Token));
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(Directory.Exists(profile.Target));
        Assert.Empty(Directory.GetDirectories(profile.Root, "native.profile-import-*"));
    }

    [Fact]
    public async Task EmptyConversationsAndCorruptCacheSurviveWithoutInventingMessages()
    {
        await using var profile = await Profile.CreateAsync();
        var emptyPeer = new string('a', 32);
        var empty = Path.Combine(profile.Source, "storage", "conversations", emptyPeer);
        Directory.CreateDirectory(empty);
        await File.WriteAllTextAsync(Path.Combine(empty, "unread"), "1");
        await File.WriteAllBytesAsync(Path.Combine(profile.Conversation, ".index"), [0xc1]);
        var report = await profile.ImportAsync();
        Assert.Contains(report.Warnings, warning => warning.Contains("Corrupt conversation index", StringComparison.Ordinal));
        var store = await NomadNetStore.OpenAsync(profile.Target);
        var conversation = Assert.Single(await store.ConversationsAsync(), item => item.PeerHash == emptyPeer);
        Assert.Equal(0, conversation.MessageCount);
        Assert.Equal(1, conversation.Unread);
        Assert.All(await store.MessagesAsync(), message => Assert.Null(message.HistoricalSignatureValidated));
        await store.ImportDirectoryAsync(new([], [], 0, []));
        Assert.Equal(2, (await store.ConversationsAsync()).Count);
        await store.DeleteConversationAsync(emptyPeer);
        Assert.Single(await (await NomadNetStore.OpenAsync(profile.Target)).ConversationsAsync());
    }

    [Fact]
    public async Task TamperedLocalSignatureIsNotLabeledUnknownOrValidatedByCachedClaim()
    {
        await using var profile = await Profile.CreateAsync();
        foreach (var path in Directory.GetFiles(profile.Conversation))
        {
            if (Path.GetFileName(path).Length != 64) continue;
            var container = (Dictionary<object, object?>)MsgPack.UnpackApplicationValue(await File.ReadAllBytesAsync(path))!;
            var parsed = LxMessage.UnpackContainer(await File.ReadAllBytesAsync(path), _ => null);
            if (parsed.Content != "paper body") continue;
            var wire = (byte[])container["lxmf_bytes"]!;
            wire[2 * LxMessage.DestinationLength] ^= 1;
            await File.WriteAllBytesAsync(path, MsgPack.Pack(container));
        }
        await profile.ImportAsync();
        var paper = Assert.Single(await (await NomadNetStore.OpenAsync(profile.Target)).MessagesAsync(),
            message => message.Content == "paper body");
        Assert.True(paper.HistoricalSignatureValidated);
        Assert.Equal("invalid-local-signature", paper.HistoricalAuthentication);
        Assert.Equal("paper", paper.Status);
    }

    [Fact]
    public async Task ConcurrentAdmissionPublishesExactlyOneCompleteProfile()
    {
        await using var profile = await Profile.CreateAsync();
        async Task<bool> Attempt()
        {
            try { await profile.ImportAsync(); return true; }
            catch (IOException) { return false; }
        }
        Assert.Single(await Task.WhenAll(Attempt(), Attempt(), Attempt()), result => result);
        Assert.Equal(4, (await (await NomadNetStore.OpenAsync(profile.Target)).MessagesAsync()).Count);
        Assert.Empty(Directory.GetDirectories(profile.Root, "native.profile-import-*"));
    }

    [Theory]
    [InlineData("../outside", 25)]
    [InlineData("file_0", 26)]
    public async Task UnsafeAttachmentPathsAndIncorrectSizeFailAtomically(string stored, long size)
    {
        await using var profile = await Profile.CreateAsync();
        var manifest = Directory.GetFiles(Path.Combine(profile.Source, "storage", "attachments"), "manifest",
            SearchOption.AllDirectories).Single();
        await File.WriteAllBytesAsync(manifest, MsgPack.Pack(new Dictionary<object, object?> { ["files"] = new object?[]
        {
            new Dictionary<object, object?> { ["name"] = "public.txt", ["stored_name"] = stored, ["size"] = size },
        } }));
        await Assert.ThrowsAsync<InvalidDataException>(() => profile.ImportAsync());
        Assert.False(Directory.Exists(profile.Target));
    }

    [Fact]
    public async Task OversizeMessagesAndUnexpectedParticipantsFailRatherThanTruncateOrForgeConversation()
    {
        await using var profile = await Profile.CreateAsync();
        var message = LxMessage.Create(profile.PeerHash, profile.Own, new string('x', NomadNetStore.MaximumMessageCharacters + 1));
        message.Pack();
        await message.WriteToDirectoryAsync(profile.Conversation);
        await Assert.ThrowsAsync<InvalidDataException>(() => profile.ImportAsync());
        Assert.False(Directory.Exists(profile.Target));
        File.Delete(Path.Combine(profile.Conversation, Convert.ToHexStringLower(message.Hash!)));
        var unrelated = LxMessage.Create(new byte[16], profile.Peer, "wrong destination");
        unrelated.Pack();
        await unrelated.WriteToDirectoryAsync(profile.Conversation);
        await Assert.ThrowsAsync<InvalidDataException>(() => profile.ImportAsync());
        Assert.False(Directory.Exists(profile.Target));
    }

    [Fact]
    public async Task OversizeSourceFilesFailBeforeReadingAndStateQuotaNeverEvictsHistory()
    {
        await using var profile = await Profile.CreateAsync();
        var oversized = Path.Combine(profile.Source, "oversized");
        await using (var stream = new FileStream(oversized, FileMode.CreateNew, FileAccess.Write))
            stream.SetLength(NomadNetProfileImport.MaximumFileBytes + 1L);
        await Assert.ThrowsAsync<InvalidDataException>(() => profile.ImportAsync());
        Assert.False(Directory.Exists(profile.Target));
        File.Delete(oversized);
        for (var index = 0; index < 85; index++)
        {
            var message = LxMessage.Create(profile.PeerHash, profile.Own,
                new string('x', NomadNetStore.MaximumMessageCharacters), timestamp: 1700000100 + index);
            message.Pack();
            await message.WriteToDirectoryAsync(profile.Conversation);
        }
        var before = Snapshot(profile.Source);
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => profile.ImportAsync());
        Assert.Contains("no records were evicted", failure.Message);
        Assert.Equal(before, Snapshot(profile.Source));
        Assert.False(Directory.Exists(profile.Target));
        Assert.Empty(Directory.GetDirectories(profile.Root, "native.profile-import-*"));
    }

    [Fact]
    public async Task ExternalSettingsAndRequiredRrcGapAreExplicitNotActivated()
    {
        await using var profile = await Profile.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(profile.Source, "config"),
            "[client]\nenable_client=Yes\n[node]\npages_path=../../outside\nfiles_path=../../outside\n[unknown]\nkey=value\n");
        await File.WriteAllBytesAsync(Path.Combine(profile.Source, "storage", "rrc_hubs"), [0xa0]);
        var report = await profile.ImportAsync();
        Assert.Contains("storage/rrc_hubs", report.NotImportedFiles);
        Assert.Contains(report.Warnings, warning => warning.Contains("REQUIRED GAP", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, warning => warning.Contains("unknown.key", StringComparison.Ordinal));
        var settings = await NomadNetSettings.OpenAsync(profile.Target);
        Assert.Null(settings.PagesPath);
        Assert.Null(settings.FilesPath);
    }

    [LinuxProfileFact]
    public async Task SymlinkSourceOrAncestorAndSpecialFileAreRejected()
    {
        await using var profile = await Profile.CreateAsync();
        var link = Path.Combine(profile.Source, "linked");
        Directory.CreateSymbolicLink(link, profile.Rns);
        await Assert.ThrowsAsync<InvalidDataException>(() => profile.ImportAsync());
        Directory.Delete(link);
        var linkedRoot = Path.Combine(profile.Root, "source-alias");
        Directory.CreateSymbolicLink(linkedRoot, profile.Source);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NomadNetProfileImport.ImportAsync(linkedRoot, profile.Target, profile.Rns));
        Directory.Delete(linkedRoot);
        using var process = System.Diagnostics.Process.Start("mkfifo", [Path.Combine(profile.Source, "pipe")])!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        await Assert.ThrowsAsync<InvalidDataException>(() => profile.ImportAsync());
    }

    private sealed class LinuxProfileFactAttribute : FactAttribute
    {
        public LinuxProfileFactAttribute()
        {
            if (!OperatingSystem.IsLinux()) Skip = "Symlink/FIFO import boundary requires Linux; Windows junction acceptance is separate.";
        }
    }

    private static string[] Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + ":" +
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    private sealed class Profile : IAsyncDisposable
    {
        public string Root { get; } = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomad-profile-import", "unit-" + Guid.NewGuid().ToString("N")));
        public string Source => Path.Combine(Root, "primary");
        public string Target => Path.Combine(Root, "native");
        public string Rns => Path.Combine(Root, "rns");
        public Identity Own { get; } = Identity.Generate();
        public Identity Peer { get; } = Identity.Generate();
        public byte[] PeerHash => Destination.ComputeHashFromNameAndIdentity("lxmf.delivery", Peer.Hash);
        public string Conversation => Path.Combine(Source, "storage", "conversations", Convert.ToHexStringLower(PeerHash));
        public string NativeRatchets => Path.Combine(Target, "storage", "lxmf", "ratchets",
            Convert.ToHexStringLower(Destination.ComputeHashFromNameAndIdentity("lxmf.delivery", Own.Hash)) + ".ratchets");

        public Task<NomadNetProfileImportReport> ImportAsync(CancellationToken token = default) =>
            NomadNetProfileImport.ImportAsync(Source, Target, Rns, token);

        public static async Task<Profile> CreateAsync()
        {
            var profile = new Profile();
            Directory.CreateDirectory(profile.Conversation);
            Directory.CreateDirectory(profile.Rns);
            var secret = profile.Own.ExportPrivateKey();
            try { await File.WriteAllBytesAsync(Path.Combine(profile.Source, "storage", "identity"), secret); }
            finally { CryptographicOperations.ZeroMemory(secret); }
            await File.WriteAllTextAsync(Path.Combine(profile.Source, "config"), "[client]\nenable_client=Yes\n");
            await File.WriteAllBytesAsync(Path.Combine(profile.Source, "storage", "peersettings"),
                MsgPack.Pack(new Dictionary<object, object?> { ["display_name"] = "Imported primary user", ["last_announce"] = null, ["last_lxmf_sync"] = 0L }));
            var ownHash = Destination.ComputeHashFromNameAndIdentity("lxmf.delivery", profile.Own.Hash);
            var index = new Dictionary<object, object?>();
            foreach (var (label, outgoing, state) in new[] {
                ("incoming", false, LxmState.Delivered), ("failed", true, LxmState.Failed),
                ("media-stripped", false, LxmState.Delivered), ("paper", true, LxmState.Paper) })
            {
                var fields = label == "media-stripped" ? new Dictionary<object, object?>
                { [LxmField.FileAttachments] = new object?[] { new object?[] { "public.txt", "public profile attachment"u8.ToArray() } },
                    [LxmField.Renderer] = 2L } : [];
                var message = LxMessage.Create(outgoing ? profile.PeerHash : ownHash, outgoing ? profile.Own : profile.Peer,
                    label + " body", label + " title", fields, timestamp: 1700000000);
                message.Pack();
                var id = Convert.ToHexStringLower(message.Hash!);
                var path = await message.WriteToDirectoryAsync(profile.Conversation);
                if (label == "media-stripped")
                {
                    var attachmentRoot = Path.Combine(profile.Source, "storage", "attachments", id);
                    Directory.CreateDirectory(attachmentRoot);
                    await File.WriteAllBytesAsync(Path.Combine(attachmentRoot, "file_0"), "public profile attachment"u8.ToArray());
                    await File.WriteAllBytesAsync(Path.Combine(attachmentRoot, "manifest"), MsgPack.Pack(new Dictionary<object, object?>
                    { ["files"] = new object?[] { new Dictionary<object, object?>
                    { ["name"] = "public.txt", ["stored_name"] = "file_0", ["size"] = 25L } } }));
                    fields.Remove(LxmField.FileAttachments);
                    var changed = LxMessage.Create(ownHash, profile.Peer, label + " body", label + " title", fields, timestamp: message.Timestamp);
                    changed.Pack();
                    await File.WriteAllBytesAsync(path, changed.PackedContainer());
                }
                var container = (Dictionary<object, object?>)MsgPack.UnpackApplicationValue(await File.ReadAllBytesAsync(path))!;
                container["state"] = (long)state;
                if (label == "paper")
                    container["method"] = 5L;
                await File.WriteAllBytesAsync(path, MsgPack.Pack(container));
                index[id] = new Dictionary<object, object?> { ["signature_validated"] = true };
            }
            await File.WriteAllBytesAsync(Path.Combine(profile.Conversation, ".index"), MsgPack.Pack(index));
            await File.WriteAllTextAsync(Path.Combine(profile.Conversation, "unread"), "2");
            await File.WriteAllTextAsync(Path.Combine(profile.Conversation, "failed"), "1");
            var ratchet = RandomNumberGenerator.GetBytes(32);
            byte[]? encoded = null;
            try
            {
                encoded = SignedDestinationRatchetFile.Encode(profile.Own, [ratchet]);
                var path = Path.Combine(profile.Source, "storage", "lxmf", "ratchets", Convert.ToHexStringLower(ownHash) + ".ratchets");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, encoded);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(ratchet);
                if (encoded is not null) CryptographicOperations.ZeroMemory(encoded);
            }
            return profile;
        }

        public ValueTask DisposeAsync()
        {
            Own.Dispose();
            Peer.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
            return ValueTask.CompletedTask;
        }
    }
}
