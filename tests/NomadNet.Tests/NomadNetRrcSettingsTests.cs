using System.Text.Json;
using DotReticulum.Applications;
using DotReticulum.Core;

namespace NomadNet.Tests;

// GenerateNomadNetRrcSettingsVectors.py executes primary f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2
// RRC history/configuration methods and actual Urwid-rendered Channels widgets before implementation.
[Trait("Category", "Unit")]
public sealed class NomadNetRrcSettingsTests
{
    [Fact, Trait("TestId", "T490")]
    public void ExecutedDefaultsAndEsotericsPlaceholderMatchEveryRequiredField()
    {
        using var fixture = Fixture();
        var defaults = fixture.RootElement.GetProperty("defaults");
        var settings = new NomadNetSettings();
        Assert.Equal(11, defaults.EnumerateObject().Count());
        Assert.Equal(defaults.GetProperty("history_per_room_cap").GetInt32(), settings.RrcHistoryPerRoomCap);
        Assert.Equal(defaults.GetProperty("filter_loaded_history").GetBoolean(), settings.RrcFilterLoadedHistory);
        Assert.Equal(defaults.GetProperty("ephemeral_notices").GetDouble(), settings.RrcEphemeralNoticeMinutes * 60);
        Assert.Equal(defaults.GetProperty("color_mention_timestamps").GetBoolean(), settings.RrcColorMentionTimestamps);
        Assert.Equal(defaults.GetProperty("nick_colors").GetBoolean(), settings.RrcNickColors);
        Assert.Equal(defaults.GetProperty("justify_msgs").GetBoolean(), settings.RrcJustifyMessages);
        Assert.Equal(defaults.GetProperty("space_msgs").GetBoolean(), settings.RrcSpaceMessages);
        Assert.Equal(defaults.GetProperty("show_gutters").GetBoolean(), settings.RrcShowGutters);
        Assert.Null(settings.RrcMentionColor);
        Assert.Empty(settings.RrcNickColorsTheme);
        Assert.False(settings.RrcEnableEsoterics);
        Assert.Equal(0, fixture.RootElement.GetProperty("esotericRuntimeAttributeReads").GetInt32());
        var fields = NomadNetConfigurationFields.ForSection("rrc");
        Assert.DoesNotContain(fields, field => field.Disposition == "stored-only");
        Assert.Equal("upstream-placeholder", Assert.Single(fields, field => field.Key == "enable_esoterics").Disposition);
    }

    [Fact, Trait("TestId", "T491")]
    public void ExecutedCustomNickPaletteMentionAndTimestampPoliciesMatchFunctionalStyleSpans()
    {
        using var fixture = Fixture();
        var settings = new NomadNetSettings { RrcNickColorsTheme = ["112233", "445566", "778899"], RrcMentionColor = "ABCDEF",
            RrcRenderMarkdown = false, RrcRenderMicron = false };
        foreach (var color in fixture.RootElement.GetProperty("colors").EnumerateObject())
            Assert.Equal(color.Value.GetString(), NomadNetRrcPresentation.NickColor(
                Convert.ToHexStringLower(Enumerable.Repeat(byte.Parse(color.Name), 16).ToArray()), settings));
        foreach (var observed in fixture.RootElement.GetProperty("widgets").EnumerateArray())
        {
            settings.RrcNickColors = observed.GetProperty("colors").GetBoolean();
            settings.RrcColorMentionTimestamps = observed.GetProperty("timestamp").GetBoolean();
            settings.RrcJustifyMessages = observed.GetProperty("justify").GetBoolean();
            settings.RrcSpaceMessages = observed.GetProperty("spacing").GetBoolean();
            var message = Message("Hello @Native and @Peer");
            var styled = NomadNetRrcPresentation.Style(message, settings, new string('2', 32), "Native",
                new Dictionary<string, string> { [new string('1', 32)] = "Peer" });
            var own = Assert.Single(styled.Body, span => span.Text == "@Native");
            Assert.True(own.Bold);
            Assert.Equal("ABCDEF", own.Color);
            var primarySegments = observed.GetProperty("segments").EnumerateArray().SelectMany(row => row.EnumerateArray()).ToArray();
            Assert.Contains(primarySegments, segment => segment.GetProperty("text").GetString() == "@Native" &&
                segment.GetProperty("foreground").GetString()!.Contains("ABCDEF", StringComparison.Ordinal));
            Assert.Equal(settings.RrcNickColors, styled.Body.Any(span => span.Text == "@Peer" && span.Bold));
            Assert.Equal(settings.RrcColorMentionTimestamps, styled.TimestampColor != "888888");
            if (settings.RrcNickColors && settings.RrcColorMentionTimestamps) Assert.Equal("ABCDEF", styled.TimestampColor);
            var rendered = NomadNetRrcPresentation.Render(message, settings, new string('2', 32), "Native", ansi: true);
            Assert.Contains("\u001b[1;38;2;171;205;239m", rendered);
            Assert.Equal(settings.RrcSpaceMessages, rendered.EndsWith('\n'));
        }
        foreach (var observed in fixture.RootElement.GetProperty("mentionScans").EnumerateArray())
        {
            var style = NomadNetRrcPresentation.Style(Message(observed.GetProperty("text").GetString()!), settings,
                new string('2', 32), observed.GetProperty("nick").GetString()!);
            Assert.Equal(observed.GetProperty("matches").EnumerateArray().Select(value => value.GetString()),
                style.Body.Where(span => span.Bold).Select(span => span.Text));
        }
    }

    [Fact, Trait("TestId", "T492")]
    public void LayoutWrappingGuttersCodeProtectionActionsAndSafeOutputAreFunctionalAndBounded()
    {
        var settings = new NomadNetSettings { RrcRenderMarkdown = false, RrcRenderMicron = false, RrcMentionColor = "ABCDEF" };
        var message = Message("Hello @Native `@Native` **ordinary**\n" + new string('x', 100));
        var style = NomadNetRrcPresentation.Style(message, settings, new string('2', 32), "Native");
        Assert.Single(style.Body, span => span.Text == "@Native" && span.Bold);
        foreach (var markdown in new[] { false, true })
            foreach (var micron in new[] { false, true })
            {
                var policy = settings.Copy();
                policy.RrcRenderMarkdown = markdown;
                policy.RrcRenderMicron = micron;
                Assert.Equal(NomadNetPresentation.Rrc(message.Text, markdown, micron, message.Kind),
                    string.Concat(NomadNetRrcPresentation.Style(message, policy, new string('2', 32), "Native").Body.Select(span => span.Text)));
                var nested = Message("**bold before `@Native` after** @Native");
                Assert.Single(NomadNetRrcPresentation.Style(nested, policy, new string('2', 32), "Native").Body,
                    span => span.Text == "@Native" && span.Bold);
            }
        var justified = NomadNetRrcPresentation.Render(message, settings, new string('2', 32), "Native", width: 40);
        Assert.Contains("\n            ", justified);
        settings.RrcJustifyMessages = false;
        var inline = NomadNetRrcPresentation.Render(message, settings, new string('2', 32), "Native", width: 40);
        Assert.Contains("\n x", inline);
        Assert.DoesNotContain("\n            ", inline);
        using var fixture = Fixture();
        foreach (var row in fixture.RootElement.GetProperty("gutters").EnumerateArray())
        {
            var visible = row.GetProperty("usersVisible").GetBoolean();
            var gutters = row.GetProperty("gutters").GetBoolean();
            Assert.Equal(visible || gutters, NomadNetRrcPresentation.CollapsedPane("users", visible, gutters).Length != 0);
            Assert.Equal(visible || gutters ? 2 : 1, row.GetProperty("columns").GetInt32());
        }
        Assert.Contains("* Peer", NomadNetRrcPresentation.Render(message with { Kind = "action" }, settings, new string('2', 32), "Native"));
        Assert.DoesNotContain('\u001b', NomadNetRrcPresentation.Render(Message("\u001b[31mAttack"), settings, "", ""));
        settings.RrcSpaceMessages = true;
        Assert.False(NomadNetRrcPresentation.Render(message with { Kind = "notice" }, settings, "", "").EndsWith('\n'));
        Assert.Contains("Peer: Hello @Native", NomadNetRrcPresentation.RenderStream(message, settings, "", "Native"));
        Assert.Throws<InvalidDataException>(() => NomadNetRrcPresentation.Style(Message(new string('x', 262145)), settings, "", ""));
        Assert.Throws<ArgumentOutOfRangeException>(() => NomadNetRrcPresentation.Render(message, settings, "", "", width: 10000));
    }

    [Fact, Trait("TestId", "T493")]
    public void CleanupMatchesExecutedNoticeKindsStrictAgeAndFiveSecondAdmission()
    {
        using var fixture = Fixture();
        var now = DateTimeOffset.FromUnixTimeSeconds(1700000000);
        var state = new RrcSessionState(new byte[16]);
        foreach (var (kind, text, age) in new[] { ("notice", "old notice", 61), ("system", "old system", 61),
            ("error", "old error", 61), ("message", "old message", 61), ("notice", "fresh notice", 59) })
            state.Record(Message(text) with { Kind = kind, Timestamp = now.AddSeconds(-age).ToUnixTimeMilliseconds() });
        state.CleanEphemeralNotices(now, 1);
        Assert.Equal(fixture.RootElement.GetProperty("cleaned").EnumerateArray().Select(item => item.GetString()), state.History.Select(item => item.Text));
        state.Record(Message("boundary") with { Kind = "notice", Timestamp = now.AddSeconds(-60).ToUnixTimeMilliseconds() });
        state.CleanEphemeralNotices(now, 1);
        Assert.Contains(state.History, item => item.Text == "boundary");
        state.CleanEphemeralNotices(now.AddSeconds(5), 1);
        Assert.Contains(state.History, item => item.Text == "boundary");
        state.CleanEphemeralNotices(now.AddSeconds(11), 1);
        Assert.DoesNotContain(state.History, item => item.Text == "boundary");
        Assert.Throws<ArgumentOutOfRangeException>(() => state.CleanEphemeralNotices(now, double.NaN));
    }

    [Fact, Trait("TestId", "T494")]
    public void AllRrcSourceControlsProjectExportAndRejectMalformedOrUnboundedSettings()
    {
        var text = "[rrc]\nhistory_per_room_cap=3\nfilter_loaded_history=no\nephemeral_notices=0.1\n" +
            "color_mention_timestamps=no\nnick_colors=no\njustify_msgs=no\nspace_msgs=yes\nshow_gutters=no\n" +
            "mention_color=ABCDEF\nnick_colors_theme=112233,445566\nenable_esoterics=yes\n";
        var parsed = NomadNetConfigImport.Parse(text);
        Assert.Empty(parsed.UnsupportedKeys);
        var settings = parsed.Settings;
        Assert.Equal(3, settings.RrcHistoryPerRoomCap);
        Assert.False(settings.RrcFilterLoadedHistory);
        Assert.Equal(0.1, settings.RrcEphemeralNoticeMinutes);
        Assert.False(settings.RrcColorMentionTimestamps);
        Assert.False(settings.RrcNickColors);
        Assert.False(settings.RrcJustifyMessages);
        Assert.True(settings.RrcSpaceMessages);
        Assert.False(settings.RrcShowGutters);
        Assert.True(settings.RrcEnableEsoterics);
        Assert.Equal("ABCDEF", settings.RrcMentionColor);
        Assert.Equal(["112233", "445566"], settings.RrcNickColorsTheme);
        var exported = NomadNetConfigImport.Parse(NomadNetPrimaryConfiguration.Export(settings));
        Assert.Equal(settings.RrcNickColorsTheme, exported.Settings.RrcNickColorsTheme);
        Assert.Equal(settings.RrcHistoryPerRoomCap, exported.Settings.RrcHistoryPerRoomCap);
        foreach (var invalid in new[] { "history_per_room_cap=1001", "history_per_room_cap=-1", "ephemeral_notices=NaN",
            "mention_color=zz0000", "nick_colors_theme=bad,", "nick_colors=invalid" })
            Assert.Throws<InvalidDataException>(() => NomadNetConfigImport.Parse("[rrc]\n" + invalid));
        Assert.Throws<InvalidDataException>(() => NomadNetRrcPolicy.Validate(new() { RrcNickColorsTheme = Enumerable.Repeat("112233", 129).ToList() }));
        var copy = settings.Copy();
        settings.RrcNickColorsTheme.Clear();
        Assert.Equal(2, copy.RrcNickColorsTheme.Count);
    }

    internal static RrcChatMessage Message(string text) => new("message", "room", new string('1', 32), "Peer", text, 4102444800000, false);
    internal static JsonDocument Fixture() => JsonDocument.Parse(typeof(NomadNetRrcSettingsTests).Assembly
        .GetManifestResourceStream("NomadNet.Tests.NomadNetRrcSettingsVectors.json")!);
}

[Trait("Category", "Integration")]
public sealed class NomadNetRrcSettingsStorageTests
{
    [Fact, Trait("TestId", "T495")]
    public async Task ExecutedLoadFilterAndCapUseDurableExistingProfilesWithoutLosingFilteredRows()
    {
        using var workspace = new Workspace();
        using var identity = Identity.Generate();
        await using var node = ReticulumNode.Open(workspace.Rns);
        using var fixture = NomadNetRrcSettingsTests.Fixture();
        foreach (var row in fixture.RootElement.GetProperty("history").EnumerateArray())
        {
            var settings = new NomadNetSettings { RrcHistoryPerRoomCap = row.GetProperty("cap").GetInt32(),
                RrcFilterLoadedHistory = row.GetProperty("filter").GetBoolean() };
            await using var client = await RrcClient.OpenAsync(node, identity, workspace.Root, new string('3', 32), settings: settings);
            Assert.Equal(row.GetProperty("texts").EnumerateArray().Select(item => item.GetString()),
                (await client.HistoryAsync("room")).Select(message => message.Text));
            await client.SetNickAsync("retained");
            Assert.Equal("retained", client.Presentation.Nick);
        }
        await using var unfiltered = await RrcClient.OpenAsync(node, identity, workspace.Root, new string('3', 32),
            settings: new() { RrcHistoryPerRoomCap = 0, RrcFilterLoadedHistory = false });
        Assert.Equal(6, (await unfiltered.HistoryAsync("room")).Count);
        await unfiltered.ClearHistoryAsync("room");
        await using var cleared = await RrcClient.OpenAsync(node, identity, workspace.Root, new string('3', 32));
        Assert.Empty(await cleared.HistoryAsync());
    }

    [Fact, Trait("TestId", "T496")]
    public async Task SafeguardsGlobalAndStricterPerHubCapsCancellationInvalidSettingsAndRestartKeepIdentityAndState()
    {
        using var workspace = new Workspace();
        using var identity = Identity.Generate();
        await using var node = ReticulumNode.Open(workspace.Rns);
        var settings = new NomadNetSettings { RrcHistoryPerRoomCap = 3, RrcFilterLoadedHistory = false };
        await using var client = await RrcClient.OpenAsync(node, identity, workspace.Root, new string('3', 32), settings: settings);
        Assert.Equal(3, (await client.HistoryAsync()).Count);
        await client.SetHistoryPerRoomCapAsync(2);
        Assert.Equal(2, (await client.HistoryAsync()).Count);
        await using var restored = await RrcClient.OpenAsync(node, identity, workspace.Root, new string('3', 32), settings: settings);
        Assert.Equal(2, (await restored.HistoryAsync()).Count);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var before = await File.ReadAllBytesAsync(workspace.Profile);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restored.SetHistoryPerRoomCapAsync(1, cancellation.Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(workspace.Profile));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => restored.SetHistoryPerRoomCapAsync(1001));
        Assert.Equal(before, await File.ReadAllBytesAsync(workspace.Profile));
        await Assert.ThrowsAsync<InvalidDataException>(() => RrcClient.OpenAsync(node, identity, workspace.Root, new string('3', 32),
            settings: new() { RrcEphemeralNoticeMinutes = double.PositiveInfinity }));
        await restored.SetHistoryPerRoomCapAsync(0);
        Assert.Equal(2, (await restored.HistoryAsync()).Count);
        await restored.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => restored.HistoryAsync());
    }

    private sealed class Workspace : IDisposable
    {
        public string Root { get; } = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomad-rrc-settings", "managed-" + Guid.NewGuid().ToString("N")));
        public string Rns => Path.Combine(Root, "rns");
        public string Profile => Path.Combine(Root, "rrc", new string('3', 32) + ".cbor");
        public Workspace()
        {
            Directory.CreateDirectory(Rns);
            File.WriteAllText(Path.Combine(Rns, "config"), "[reticulum]\nshare_instance=no\n[interfaces]\n");
            Directory.CreateDirectory(Path.GetDirectoryName(Profile)!);
            var records = new[] { ("msg", "first"), ("notice", "notice"), ("system", "system"), ("action", "action"), ("error", "error"), ("msg", "last") };
            File.WriteAllBytes(Profile, RrcCbor.Encode(new Dictionary<long, object?>
            {
                [0] = 1L, [1] = "Native", [2] = new object?[] { "room" },
                [3] = records.Select(item => (object?)new object?[] { item.Item1 == "msg" ? "message" : item.Item1, "room", new string('1', 32), "Peer", item.Item2, 4102444800000L, false }).ToArray(),
            }));
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
