using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text.Json;
using DotReticulum.Applications;
using DotReticulum.Core;
using LXMF;

namespace NomadNet.Tests;

// Independently executed primary RRCManager/RRCHub persistence vectors, generated FIRST at
// f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2; fixture records exact Git blob and SHA-256.
[Trait("Category", "Integration")]
public sealed class NomadNetRrcImportTests
{
    [Fact]
    public async Task ActualPrimaryProfilesRetainNamesAspectsNickJoinedPartedFilteredHistoryAndProvenanceAcrossRestart()
    {
        await using var profile = await Profile.CreateAsync();
        var before = Snapshot(profile.Source);
        var report = await profile.ImportAsync();
        Assert.DoesNotContain("storage/rrc_hubs", report.NotImportedFiles);
        Assert.Contains(report.Warnings, warning => warning.Contains("unauthenticated", StringComparison.Ordinal));
        Assert.Equal(before, Snapshot(profile.Source));
        await using var node = ReticulumNode.Open(profile.Rns);
        using var identity = Identity.Generate();
        for (var restart = 0; restart < 3; restart++)
        {
            await using var client = await RrcClient.OpenAsync(node, identity, profile.Target, new string('1', 32));
            var status = await client.StatusAsync();
            Assert.Contains("connected: False", status);
            Assert.Contains("nick: Migrated nick; rooms: lobby;", status);
            Assert.Contains("auto-reconnect: True", status);
            Assert.Contains("auto-list: True; auto-who: True", status);
            Assert.Contains("parted: empty-parted,old/room", status);
            Assert.Contains("saved name: Saved primary hub; aspect: rrc.hub", status);
            Assert.Contains("unread: ; mentions: ", status);
            var history = await client.HistoryAsync();
            Assert.Equal(3, history.Count);
            Assert.All(history, item => Assert.True(item.ImportedHistory));
            Assert.Equal(1700000000123, history[0].Timestamp);
            Assert.Equal("Retained primary message", history[0].Text);
            Assert.True(history[0].Mention);
            Assert.Single(await client.HistoryAsync("old/room"));
            Assert.Empty(await client.MembersAsync("lobby"));
            await client.SetAutomaticDirectoryAsync(list: true);
        }
        await using var alternate = await RrcClient.OpenAsync(node, identity, profile.Target, new string('3', 32));
        Assert.Contains("nick: Primary RRC fallback", await alternate.StatusAsync());
        Assert.Contains("aspect: custom.chat", await alternate.StatusAsync());
        Assert.Empty(await alternate.HistoryAsync());
    }

    [Theory]
    [InlineData(true, 1, 2)]
    [InlineData(false, 0, 4)]
    public async Task AppliesActualPrimaryLoadFilteringAndPerRoomWindow(bool filter, int cap, int expected)
    {
        await using var profile = await Profile.CreateAsync();
        await File.AppendAllTextAsync(Path.Combine(profile.Source, "config"),
            $"\n[rrc]\nhistory_per_room_cap={cap}\nfilter_loaded_history={filter}\n");
        await profile.ImportAsync();
        await using var node = ReticulumNode.Open(profile.Rns);
        using var identity = Identity.Generate();
        await using var client = await RrcClient.OpenAsync(node, identity, profile.Target, new string('1', 32));
        var history = await client.HistoryAsync();
        Assert.Equal(expected, history.Count);
        if (filter) Assert.Equal("action", history[0].Kind);
        else Assert.Contains(history, item => item.Kind == "notice");
    }

    [Theory]
    [InlineData("root")]
    [InlineData("hash")]
    [InlineData("nick")]
    [InlineData("aspect")]
    [InlineData("rooms")]
    [InlineData("room-type")]
    [InlineData("room-limit")]
    [InlineData("overlap")]
    [InlineData("policy")]
    [InlineData("duplicates")]
    [InlineData("hub-limit")]
    [InlineData("bad-time")]
    [InlineData("bad-source")]
    [InlineData("bad-mention")]
    [InlineData("bad-kind")]
    [InlineData("bad-text")]
    [InlineData("oversize-text")]
    [InlineData("tail")]
    [InlineData("cap-limit")]
    public async Task MalformedSchemaTypesLimitsAndCorruptTailsRollbackAllStagedProfiles(string problem)
    {
        await using var profile = await Profile.CreateAsync();
        var hub = new Dictionary<string, object?>
        {
            ["hash"] = Enumerable.Repeat((byte)0x11, 16).ToArray(),
            ["dest_name"] = "rrc.hub", ["rooms"] = new object?[] { "lobby" },
            ["parted_rooms"] = Array.Empty<object?>(), ["nick"] = "Nick",
        };
        var entry = new Dictionary<string, object?>
        { ["k"] = "msg", ["t"] = "text", ["ts"] = 1700000000123L, ["s"] = new byte[16], ["m"] = false, ["n"] = null };
        object?[] hubs = [hub];
        switch (problem)
        {
            case "root": break;
            case "hash": hub["hash"] = new byte[15]; break;
            case "nick": hub["nick"] = true; break;
            case "aspect": hub["dest_name"] = "../escape"; break;
            case "rooms": hub["rooms"] = "lobby"; break;
            case "room-type": hub["rooms"] = new object?[] { 1L }; break;
            case "room-limit": hub["rooms"] = Enumerable.Repeat<object?>("lobby", 257).ToArray(); break;
            case "overlap": hub["parted_rooms"] = new object?[] { "lobby" }; break;
            case "policy": hub["auto_who"] = 1L; break;
            case "duplicates": hubs = [hub, hub]; break;
            case "hub-limit": hubs = Enumerable.Repeat<object?>(hub, 129).ToArray(); break;
            case "bad-time": entry["ts"] = "1700000000123"; break;
            case "bad-source": entry["s"] = new byte[15]; break;
            case "bad-mention": entry["m"] = "yes"; break;
            case "bad-kind": entry["k"] = "unknown"; break;
            case "bad-text": entry["t"] = new byte[4]; break;
            case "oversize-text": entry["t"] = new string('x', 262145); break;
            case "cap-limit":
                await File.AppendAllTextAsync(Path.Combine(profile.Source, "config"), "\n[rrc]\nhistory_per_room_cap=1001\n");
                break;
        }
        await File.WriteAllBytesAsync(Path.Combine(profile.Source, "storage", "rrc_hubs"),
            problem == "root" ? [0xa0] : Encode(new Dictionary<string, object?> { ["hubs"] = hubs }));
        var path = Directory.GetFiles(Path.Combine(profile.Source, "storage", "rrc_history"), "lobby*.log", SearchOption.AllDirectories).Single();
        var bytes = Encode(entry);
        await File.WriteAllBytesAsync(path, problem == "tail" ? [.. bytes, 0xa6, 0x61] : bytes);
        var before = Snapshot(profile.Source);
        await Assert.ThrowsAnyAsync<Exception>(() => profile.ImportAsync());
        Assert.Equal(before, Snapshot(profile.Source));
        Assert.False(Directory.Exists(profile.Target));
        Assert.Empty(Directory.GetDirectories(profile.Root, "native.profile-import-*"));
        Assert.False(File.Exists(Path.Combine(profile.Root, "identity")));
    }

    [Fact]
    public async Task ExcessRetainedHistoryFailsRatherThanSilentlyEvictingAndCanceledImportsDoNotPublish()
    {
        await using var profile = await Profile.CreateAsync();
        await File.AppendAllTextAsync(Path.Combine(profile.Source, "config"), "\n[rrc]\nhistory_per_room_cap=0\n");
        var path = Directory.GetFiles(Path.Combine(profile.Source, "storage", "rrc_history"), "lobby*.log", SearchOption.AllDirectories).Single();
        var entry = Encode(new Dictionary<string, object?> { ["k"] = "msg", ["t"] = "body",
            ["ts"] = 0L, ["s"] = null, ["m"] = false, ["n"] = null });
        await File.WriteAllBytesAsync(path, Enumerable.Range(0, 1001).SelectMany(_ => entry).ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => profile.ImportAsync());
        Assert.False(Directory.Exists(profile.Target));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => profile.ImportAsync(canceled.Token));
        Assert.False(Directory.Exists(profile.Target));
    }

    private static byte[] Encode(object? value)
    {
        var writer = new CborWriter();
        void Write(object? item)
        {
            switch (item)
            {
                case null: writer.WriteNull(); break;
                case bool flag: writer.WriteBoolean(flag); break;
                case string text: writer.WriteTextString(text); break;
                case byte[] bytes: writer.WriteByteString(bytes); break;
                case long number: writer.WriteInt64(number); break;
                case object?[] array:
                    writer.WriteStartArray(array.Length);
                    foreach (var child in array) Write(child);
                    writer.WriteEndArray();
                    break;
                case Dictionary<string, object?> map:
                    writer.WriteStartMap(map.Count);
                    foreach (var (key, child) in map) { writer.WriteTextString(key); Write(child); }
                    writer.WriteEndMap();
                    break;
                default: throw new InvalidDataException("Unsupported test input");
            }
        }
        Write(value);
        return writer.Encode();
    }

    private static string[] Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + ":" +
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    private sealed class Profile : IAsyncDisposable
    {
        public string Root { get; } = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomad-rrc-migration", "test-" + Guid.NewGuid().ToString("N")));
        public string Source => Path.Combine(Root, "primary");
        public string Target => Path.Combine(Root, "native");
        public string Rns => Path.Combine(Root, "rns");
        public Task<NomadNetProfileImportReport> ImportAsync(CancellationToken token = default) =>
            NomadNetProfileImport.ImportAsync(Source, Target, Rns, token);

        public static async Task<Profile> CreateAsync()
        {
            var profile = new Profile();
            Directory.CreateDirectory(Path.Combine(profile.Source, "storage"));
            Directory.CreateDirectory(profile.Rns);
            await File.WriteAllTextAsync(Path.Combine(profile.Rns, "config"), "[reticulum]\nshare_instance=No\n[interfaces]\n");
            using var identity = Identity.Generate();
            var secret = identity.ExportPrivateKey();
            try { await File.WriteAllBytesAsync(Path.Combine(profile.Source, "storage", "identity"), secret); }
            finally { CryptographicOperations.ZeroMemory(secret); }
            await File.WriteAllTextAsync(Path.Combine(profile.Source, "config"), "[client]\nenable_client=Yes\n");
            await File.WriteAllBytesAsync(Path.Combine(profile.Source, "storage", "peersettings"),
                MsgPack.Pack(new Dictionary<object, object?> { ["display_name"] = "Primary RRC fallback" }));
            using var fixture = JsonDocument.Parse(typeof(NomadNetRrcImportTests).Assembly
                .GetManifestResourceStream("NomadNet.Tests.NomadNetRrcImportVectors.json")!);
            Assert.Equal("f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2", fixture.RootElement.GetProperty("commit").GetString());
            await File.WriteAllBytesAsync(Path.Combine(profile.Source, "storage", "rrc_hubs"),
                Convert.FromHexString(fixture.RootElement.GetProperty("hubsHex").GetString()!));
            foreach (var entry in fixture.RootElement.GetProperty("historyHex").EnumerateObject())
            {
                var path = Path.Combine(profile.Source, entry.Name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, Convert.FromHexString(entry.Value.GetString()!));
            }
            return profile;
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(Root, true);
            return ValueTask.CompletedTask;
        }
    }
}
