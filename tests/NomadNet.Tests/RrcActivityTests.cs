using System.Text.Json;
using DotReticulum.Applications;
using DotReticulum.Core;
using NomadNet;

namespace NomadNet.Tests;

[Trait("Category", "Unit")]
public sealed class RrcActivityTests
{
    [Fact]
    public void ActiveUnreadMentionNoticeRoutingAndRetentionMatchExecutedPrimary()
    {
        using var fixture = JsonDocument.Parse(typeof(RrcActivityTests).Assembly
            .GetManifestResourceStream("NomadNet.Tests.NomadNetRrcVectors.json")!);
        var state = new RrcSessionState(Enumerable.Range(0, 16).Select(value => (byte)value).ToArray()) { Nick = "Alice" };
        state.SetHistoryPerRoomCap(2);
        var peer = Enumerable.Range(16, 16).Select(value => (byte)value).ToArray();
        foreach (var step in fixture.RootElement.GetProperty("activity").EnumerateArray())
        {
            var room = step.GetProperty("room").GetString();
            switch (step.GetProperty("operation").GetString())
            {
                case "active": state.SetActiveRoom(room); break;
                case "inactive": state.SetActiveRoom(null); break;
                case "read": state.MarkRead(room!); break;
                case "clear": state.ClearMessages(room!); break;
                case "notice":
                    state.Apply(RrcEnvelope.Create(21, peer, room, step.GetProperty("text").GetString()));
                    break;
                case "message":
                    var text = step.GetProperty("text").GetString()!;
                    if (step.GetProperty("local").GetBoolean()) state.Sent(state.MakeMessage(room, text));
                    else state.Apply(RrcEnvelope.Create(20, peer, room, text, "Bob"));
                    break;
            }
            Assert.Equal(step.GetProperty("active").GetString(), state.ActiveRoom);
            Assert.Equal(step.GetProperty("unread").EnumerateArray().Select(value => value.GetString()), state.UnreadRooms.Order());
            Assert.Equal(step.GetProperty("mentions").EnumerateArray().Select(value => value.GetString()), state.MentionRooms.Order());
            foreach (var expected in step.GetProperty("messages").EnumerateObject())
                Assert.Equal(expected.Value.EnumerateArray().Select(value => value.GetString()),
                    state.History.Where(message => message.Room == expected.Name).Select(message => message.Text));
        }
    }

    [Fact]
    public void UnreadRoomsAreBoundedAndHistoryChangesClearTheirFlags()
    {
        var state = new RrcSessionState(new byte[16]) { Nick = "Alice" };
        for (var index = 0; index < 256; index++)
            state.Apply(RrcEnvelope.Create(20, new byte[16], "room" + index, "@Alice"));
        Assert.Equal(256, state.UnreadRooms.Count);
        Assert.Equal(256, state.MentionRooms.Count);
        Assert.Throws<InvalidDataException>(() => state.Apply(RrcEnvelope.Create(20, new byte[16], "overflow", "text")));
        state.SetActiveRoom("ROOM0");
        Assert.Equal("room0", state.ActiveRoom);
        Assert.DoesNotContain("room0", state.UnreadRooms);
        Assert.DoesNotContain("room0", state.MentionRooms);
        state.ClearMessages("room1");
        Assert.DoesNotContain(state.History, message => message.Room == "room1");
        Assert.DoesNotContain("room1", state.UnreadRooms);
        state.SetHistoryPerRoomCap(1);
        state.Apply(RrcEnvelope.Create(20, new byte[16], "room2", "latest"));
        Assert.Equal("latest", Assert.Single(state.History, message => message.Room == "room2").Text);
        Assert.Throws<ArgumentOutOfRangeException>(() => state.SetHistoryPerRoomCap(1001));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.SetHistoryPerRoomCap(-1));
    }

    [Fact]
    public void AutomaticDirectoryRequestsAndSilentRepliesMatchExecutedPrimary()
    {
        using var fixture = JsonDocument.Parse(typeof(RrcActivityTests).Assembly
            .GetManifestResourceStream("NomadNet.Tests.NomadNetRrcVectors.json")!);
        var own = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        var state = new RrcSessionState(own) { Nick = "Alice" };
        foreach (var step in fixture.RootElement.GetProperty("automaticDirectory").EnumerateArray())
        {
            var operation = step.GetProperty("operation").GetString();
            var commands = new List<RrcEnvelope>();
            bool? consumed = null;
            switch (operation)
            {
                case "default-welcome":
                case "enabled-welcome":
                    if (operation == "enabled-welcome") { state.AutoList = true; state.AutoWho = true; }
                    state.Apply(RrcEnvelope.Create(2, own, body: new Dictionary<long, object?> { [0] = "hub", [1] = "0.1" }));
                    break;
                case "join":
                    var join = state.MakeJoin("lobby");
                    state.Sent(join);
                    commands.Add(join);
                    break;
                case "own-joined":
                case "other-joined":
                    state.Apply(RrcEnvelope.Create(11, own, "lobby",
                        new object?[] { operation == "own-joined" ? own : Enumerable.Range(16, 16).Select(value => (byte)value).ToArray() }));
                    break;
                default:
                    var text = operation switch
                    {
                        "list" or "manual-list" => "Registered public rooms:\n  lobby - Main room",
                        "fragment" => "  support - Help",
                        _ => "members in lobby: Alice (000102030405)",
                    };
                    var before = state.History.Count;
                    state.Apply(RrcEnvelope.Create(21, own, body: text));
                    consumed = state.History.Count == before;
                    break;
            }
            commands.AddRange(state.DrainAutomaticCommands());
            var expected = step.GetProperty("commands").EnumerateArray().ToArray();
            Assert.Equal(expected.Length, commands.Count);
            for (var index = 0; index < expected.Length; index++)
            {
                Assert.Equal(expected[index].GetProperty("type").GetInt32(), commands[index].Type);
                Assert.Equal(expected[index].GetProperty("room").GetString(), commands[index].Room);
                Assert.Equal(expected[index].GetProperty("body").GetString(), commands[index].Body);
            }
            Assert.Equal(step.GetProperty("consumed").ValueKind == JsonValueKind.Null ? null :
                step.GetProperty("consumed").GetBoolean(), consumed);
        }
        state.ResetConnection();
        Assert.Empty(state.DrainAutomaticCommands());
        Assert.True(state.AutoWho);
        Assert.True(state.AutoList);
    }
}

[Trait("Category", "Integration")]
public sealed class RrcActivityPersistenceTests
{
    [Fact]
    public async Task HistoryPolicyPersistsButViewSelectionIsSessionLocal()
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomadnet-activity-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "config"), "[reticulum]\n share_instance = No\n[interfaces]\n");
            await using var node = ReticulumNode.Open(directory);
            using var identity = Identity.Generate();
            var client = await RrcClient.OpenAsync(node, identity, directory, new string('a', 32));
            await client.SetHistoryPerRoomCapAsync(3);
            await client.SetAutomaticDirectoryAsync(list: true, who: true);
            await client.SetActiveRoomAsync("lobby");
            Assert.Contains("active: lobby", await client.StatusAsync());
            await client.DisposeAsync();
            await using var restored = await RrcClient.OpenAsync(node, identity, directory, new string('a', 32));
            Assert.Contains("per-room history: 3", await restored.StatusAsync());
            Assert.Contains("active: none", await restored.StatusAsync());
            Assert.Contains("auto-list: True; auto-who: True", await restored.StatusAsync());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.SetActiveRoomAsync("lobby"));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.MarkReadAsync("lobby"));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ClearHistoryAsync("lobby"));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.SetAutomaticDirectoryAsync(list: false));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
