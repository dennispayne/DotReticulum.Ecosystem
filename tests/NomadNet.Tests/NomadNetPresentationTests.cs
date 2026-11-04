using System.Text.Json;
using System.Text;
using LXMF;

namespace NomadNet.Tests;

// Actual primary f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2 widgets and pinned RNS MarkdownToMicron.
// GenerateNomadNetPresentationVectors.py records source hashes and the headless palette host.
[Trait("Category", "Unit")]
public sealed class NomadNetPresentationTests
{
    [Fact]
    public void ConversationBodyMatchesActualPrimaryFormattingPipeline()
    {
        using var fixture = Fixture();
        Assert.Equal(NomadNetPresentation.MarkdownRenderer, fixture.RootElement.GetProperty("rendererMarkdown").GetInt32());
        foreach (var vector in fixture.RootElement.GetProperty("conversation").EnumerateArray())
            Assert.True(vector.GetProperty("visible").GetString() ==
                NomadNetPresentation.Conversation(vector.GetProperty("input").GetString()!, NomadNetPresentation.MarkdownRenderer),
                vector.GetProperty("name").GetString());
    }

    [Fact]
    public void RrcBodyMatchesActualPrimaryWidgetAcrossAllRenderingPolicies()
    {
        using var fixture = Fixture();
        var failures = new List<string>();
        foreach (var vector in fixture.RootElement.GetProperty("rrc").EnumerateArray())
        {
            var actual = NomadNetPresentation.Rrc(
                vector.GetProperty("input").GetString()!, vector.GetProperty("markdown").GetBoolean(),
                vector.GetProperty("micron").GetBoolean());
            var expected = vector.GetProperty("visible").GetString();
            if (expected != actual)
                failures.Add($"{vector.GetProperty("name").GetString()} markdown={vector.GetProperty("markdown")} micron={vector.GetProperty("micron")}: expected={JsonSerializer.Serialize(expected)} actual={JsonSerializer.Serialize(actual)}");
        }
        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }

    [Fact]
    public void PlainAndUnknownRenderersRemainLiteralAndSystemMessagesDoNotInterpretMarkup()
    {
        const string content = "**literal** ¦!text¦!";
        Assert.Equal(content, NomadNetPresentation.Conversation(content, null));
        Assert.Equal(content, NomadNetPresentation.Conversation(content, 999));
        Assert.Equal(content, NomadNetPresentation.Rrc(content, kind: "notice"));
        Assert.Equal("", NomadNetPresentation.Rrc(""));
        Assert.Equal("trailing", NomadNetPresentation.Rrc("trailing`", markdown: false));
        Assert.Throws<EncoderFallbackException>(() => NomadNetPresentation.Conversation("\ud800", 2));
        Assert.Throws<InvalidDataException>(() => NomadNetPresentation.Rrc(new string('x', MicronText.MaximumPageBytes + 1)));
        Assert.Equal(2, NomadNetPresentation.Renderer(new Dictionary<object, object?> { [LxmField.Renderer] = 2L }));
        Assert.Null(NomadNetPresentation.Renderer(new Dictionary<object, object?> { [LxmField.Renderer] = true }));
    }

    [Fact]
    public void DisplayNameSourceObservationsPreserveStateAndDoNotAnnounceWithExplicitNativeBounds()
    {
        using var fixture = Fixture();
        foreach (var vector in fixture.RootElement.GetProperty("displayNames").EnumerateArray())
        {
            Assert.Equal(vector.GetProperty("input").GetRawText(), vector.GetProperty("stored").GetRawText());
            Assert.Equal(vector.GetProperty("input").GetRawText(), vector.GetProperty("destination").GetRawText());
            Assert.Equal(123, vector.GetProperty("lastAnnounce").GetDouble());
            Assert.Equal("save", Assert.Single(vector.GetProperty("calls").EnumerateArray()).GetString());
        }
        Assert.Throws<ArgumentNullException>(() => NomadNetClientProfile.ValidateDisplayName(null!));
        Assert.Throws<InvalidDataException>(() => NomadNetClientProfile.ValidateDisplayName(""));
        Assert.Throws<InvalidDataException>(() => NomadNetClientProfile.ValidateDisplayName("line\nbreak"));
        Assert.Throws<InvalidDataException>(() => NomadNetClientProfile.ValidateDisplayName(new string('x', 81)));
        Assert.Throws<EncoderFallbackException>(() => NomadNetClientProfile.ValidateDisplayName("\ud800"));
        NomadNetClientProfile.ValidateDisplayName("Native café 雨");
        NomadNetClientProfile.ValidateDisplayName(new string('x', 80));
    }

    private static JsonDocument Fixture() => JsonDocument.Parse(typeof(NomadNetPresentationTests).Assembly
        .GetManifestResourceStream("NomadNet.Tests.NomadNetPresentationVectors.json")!);
}

[Trait("Category", "Integration")]
public sealed class NomadNetPresentationPersistenceTests
{
    [Fact]
    public async Task ProfileNamePersistsBeforeApplyingAndFailedOrCanceledSavesLeaveRunningNameAlone()
    {
        var root = NewRoot();
        try
        {
            var initial = new NomadNetSettings { LastClientAnnounce = DateTimeOffset.UnixEpoch, InboundStampCost = 14 };
            await initial.SaveAsync(root);
            var live = initial.DisplayName;
            var applications = 0;
            await using var policy = new NomadNetPropagation(root, initial, () => [], _ => { },
                (_, _) => Task.FromResult(0), () => { });
            await NomadNetClientProfile.SetDisplayNameAsync(policy, name => { live = name; applications++; }, "Native café 雨");
            Assert.Equal("Native café 雨", live);
            var saved = await NomadNetSettings.OpenAsync(root);
            Assert.Equal(live, saved.DisplayName);
            Assert.Equal(DateTimeOffset.UnixEpoch, saved.LastClientAnnounce);
            Assert.Equal(14, saved.InboundStampCost);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NomadNetClientProfile.SetDisplayNameAsync(
                policy, name => { live = name; applications++; }, "not committed", canceled.Token));
            File.Delete(Path.Combine(root, "settings.json"));
            Directory.CreateDirectory(Path.Combine(root, "settings.json"));
            var saveFailure = await Record.ExceptionAsync(() => NomadNetClientProfile.SetDisplayNameAsync(
                policy, name => { live = name; applications++; }, "cannot save"));
            Assert.True(saveFailure is IOException or UnauthorizedAccessException);
            Assert.Equal(1, applications);
            Assert.Equal("Native café 雨", live);
            Assert.Equal(live, policy.Settings.DisplayName);
            Assert.Empty(Directory.GetFiles(root, "*.new"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RendererMetadataSurvivesHistoryRestartWithoutChangingOriginalContent()
    {
        var root = NewRoot();
        try
        {
            var store = await NomadNetStore.OpenAsync(root);
            var message = new NomadNetMessage(new string('a', 64), new string('b', 32), "inbound",
                DateTimeOffset.FromUnixTimeSeconds(100000), "Generated", "**original**", "received") { Renderer = 2 };
            await store.SaveMessageAsync(message);
            var reopened = await NomadNetStore.OpenAsync(root);
            var restored = Assert.Single(await reopened.MessagesAsync());
            Assert.Equal(message, restored);
            Assert.Equal("original", NomadNetPresentation.Conversation(restored.Content, restored.Renderer));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string NewRoot()
    {
        var root = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomadnet-presentation-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        return root;
    }
}
