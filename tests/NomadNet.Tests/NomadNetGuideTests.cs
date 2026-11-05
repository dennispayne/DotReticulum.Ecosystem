using System.Text;
using System.Text.Json;

namespace NomadNet.Tests;

// Actual GuideDisplay/GuideEntry/MicronParser at f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2.
// GenerateNomadNetGuideVectors.py records source hashes and headless/image-resolver seams.
[Trait("Category", "Unit")]
public sealed class NomadNetGuideTests
{
    private static JsonDocument Reference() => JsonDocument.Parse(typeof(NomadNetGuideTests).Assembly
        .GetManifestResourceStream("NomadNet.Tests.NomadNetGuideVectors.json")!);

    [Fact, Trait("TestId", "T800")]
    public void T800ExecutedPrimaryTopicCoverageIncludesTheUnlistedConversationBody()
    {
        using var reference = Reference();
        var root = reference.RootElement;
        Assert.Equal("f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2", root.GetProperty("revision").GetString());
        Assert.Equal("cff869dfd8834fa55ccc25e1ddbece06a61d72329589e77c62236f72220bd2c5",
            root.GetProperty("sources").GetProperty("nomadnet/ui/textui/Guide.py").GetString());
        var topics = root.GetProperty("topics").EnumerateArray().ToArray();
        Assert.Equal(13, topics.Length);
        Assert.Equal(12, root.GetProperty("listedTopics").GetArrayLength());
        Assert.Equal(NomadNetGuide.PrimaryTopicCoverage.Keys.Order(),
            topics.Select(topic => topic.GetProperty("name").GetString()!).Order());
        foreach (var topic in topics)
        {
            Assert.True(topic.GetProperty("characters").GetInt32() > 0);
            Assert.True(topic.GetProperty("widgets").GetInt32() > 0);
            Assert.Equal(1, topic.GetProperty("focusAfterSelection").GetInt32());
            Assert.NotEmpty(new NomadNetGuide().Open(NomadNetGuide.PrimaryTopicCoverage[topic.GetProperty("name").GetString()!]));
        }
        Assert.False(Assert.Single(topics, topic => topic.GetProperty("name").GetString() == "Conversations")
            .GetProperty("listed").GetBoolean());
    }

    [Fact, Trait("TestId", "T801")]
    public void T801ExecutedPrimaryFocusFirstRunActivationAndHandoffHaveExplicitOriginalDispositions()
    {
        using var reference = Reference();
        var root = reference.RootElement;
        Assert.False(root.GetProperty("initial").GetProperty("hasContent").GetBoolean());
        Assert.Equal("First Run", root.GetProperty("firstRun").GetProperty("selected").GetString());
        Assert.Equal(1, root.GetProperty("firstRun").GetProperty("focus").GetInt32());
        Assert.Equal(0, root.GetProperty("focus").GetProperty("topics").GetInt32());
        Assert.Equal(1, root.GetProperty("focus").GetProperty("reader").GetInt32());
        Assert.Equal(3, root.GetProperty("activation").GetProperty("clicks").GetInt32());
        Assert.Equal(new[] { true, false, false }, root.GetProperty("activation").GetProperty("mouse")
            .EnumerateArray().Select(row => row.GetProperty("accepted").GetBoolean()));
        Assert.Equal(2, root.GetProperty("delegate").GetProperty("alarms").GetArrayLength());
        Assert.Equal(2, root.GetProperty("delegate").GetProperty("calls").GetArrayLength());
        var guide = new NomadNetGuide();
        Assert.Equal("start", guide.CurrentTopic); // Original console always has a local start position.
        Assert.Contains("not emulated as fullscreen keys", guide.Open("keyboard"), StringComparison.Ordinal);
        Assert.Contains("network actions, unlike guide open", guide.Open("browser"), StringComparison.Ordinal);
    }

    [Fact, Trait("TestId", "T802")]
    public void T802AllOriginalLinksAndHeadingAnchorsResolveWithoutNetworkOrFields()
    {
        Assert.InRange(NomadNetGuide.Topics.Count, 18, 32);
        foreach (var (topic, body) in NomadNetGuide.Topics)
        {
            Assert.InRange(Encoding.UTF8.GetByteCount(body), 100, 32768);
            var guide = new NomadNetGuide();
            guide.Open(topic);
            Assert.Empty(guide.Page.Fields);
            foreach (var link in guide.Page.Links)
            {
                Assert.StartsWith("guide:", link.Target);
                Assert.NotEmpty(new NomadNetGuide().Open(link.Target));
            }
            foreach (var anchor in guide.Page.Anchors.Keys)
                Assert.NotEmpty(new NomadNetGuide().Open(topic + "#" + anchor));
        }
    }

    [Fact, Trait("TestId", "T803")]
    public void T803AnchorVisitsAndScrollPositionsSurviveBackForwardAndNewBranchesTruncateForward()
    {
        var guide = new NomadNetGuide();
        guide.Open("hosting#access");
        var access = guide.CurrentLine;
        Assert.True(access > 0);
        guide.Open("messages");
        guide.View(3, 2);
        Assert.Contains("Access lists", guide.Back(), StringComparison.Ordinal);
        Assert.Equal(access, guide.CurrentLine);
        guide.Forward();
        Assert.Equal("messages", guide.CurrentTopic);
        Assert.Equal(2, guide.CurrentLine);
        guide.Back();
        guide.Open("concepts");
        Assert.Equal(0, guide.ForwardCount);
        Assert.Throws<InvalidOperationException>(() => guide.Forward());
        Assert.Equal("concepts", guide.CurrentTopic);
        var count = guide.BackCount;
        guide.Open("concepts");
        Assert.Equal(count, guide.BackCount);
    }

    [Fact, Trait("TestId", "T804")]
    public void T804HistoryRemainsBoundedOnBothDirectionsAndRestartsFresh()
    {
        var guide = new NomadNetGuide();
        for (var i = 0; i < 101; i++) guide.Open(i % 2 == 0 ? "messages" : "hosting");
        Assert.Equal(32, guide.BackCount);
        for (var i = 0; i < 32; i++) guide.Back();
        Assert.Throws<InvalidOperationException>(() => guide.Back());
        Assert.Equal(32, guide.ForwardCount);
        for (var i = 0; i < 32; i++) guide.Forward();
        Assert.Throws<InvalidOperationException>(() => guide.Forward());
        Assert.Equal(32, guide.BackCount);
        var restarted = new NomadNetGuide();
        Assert.Equal("start", restarted.CurrentTopic);
        Assert.Equal(0, restarted.CurrentLine);
        Assert.Equal(0, restarted.BackCount);
        Assert.Equal(0, restarted.ForwardCount);
    }

    [Fact, Trait("TestId", "T805")]
    public void T805MalformedTargetsAndIndicesNeverMutateReaderState()
    {
        var guide = new NomadNetGuide();
        guide.Open("hosting#access");
        var line = guide.CurrentLine;
        var back = guide.BackCount;
        foreach (var target in new[] { "https://example.invalid", "../start", "guide:missing",
            "hosting#missing", "hosting#access#other", "start\0", new string('x', 257) })
        {
            Assert.Throws<ArgumentException>(() => guide.Open(target));
            Assert.Equal("hosting", guide.CurrentTopic);
            Assert.Equal(line, guide.CurrentLine);
            Assert.Equal(back, guide.BackCount);
        }
        Assert.Throws<ArgumentNullException>(() => guide.Open(null!));
        foreach (var index in new[] { int.MinValue, 0, int.MaxValue })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => guide.Follow(index));
            Assert.Throws<ArgumentOutOfRangeException>(() => guide.Select(index));
        }
        Assert.Equal(line, guide.CurrentLine);
    }

    [Fact, Trait("TestId", "T806")]
    public void T806CancellationPrecedesEveryMutationAndLeavesNavigationUsable()
    {
        var guide = new NomadNetGuide();
        guide.Open("messages");
        guide.Open("hosting#access");
        var line = guide.CurrentLine;
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => guide.Open("start", cancel.Token));
        Assert.Throws<OperationCanceledException>(() => guide.Back(cancel.Token));
        Assert.Throws<OperationCanceledException>(() => guide.Forward(cancel.Token));
        Assert.Throws<OperationCanceledException>(() => guide.Follow(1, cancel.Token));
        Assert.Throws<OperationCanceledException>(() => guide.Select(1, cancel.Token));
        Assert.Throws<OperationCanceledException>(() => guide.View(1, 100, cancel.Token));
        Assert.Throws<OperationCanceledException>(() => guide.Find("local", cancel.Token));
        Assert.Equal("hosting", guide.CurrentTopic);
        Assert.Equal(line, guide.CurrentLine);
        Assert.Equal(2, guide.BackCount);
        Assert.Contains("Messages and attachments", guide.Back(), StringComparison.Ordinal);
        Assert.NotEmpty(guide.Forward());
    }

    [Fact, Trait("TestId", "T807")]
    public void T807ViewportBoundsSaturateAndDoNotFabricateTerminalWidth()
    {
        var guide = new NomadNetGuide();
        guide.Open("hosting");
        Assert.Contains("lines 1-3/", guide.View(3), StringComparison.Ordinal);
        var last = guide.Page.Text.Split('\n').Length - 1;
        guide.View(scroll: 10000);
        Assert.Equal(last, guide.CurrentLine);
        guide.View(scroll: 10000);
        Assert.Equal(last, guide.CurrentLine);
        guide.View(scroll: -10000);
        Assert.Equal(0, guide.CurrentLine);
        Assert.Throws<ArgumentOutOfRangeException>(() => guide.View(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => guide.View(101));
        Assert.Throws<ArgumentOutOfRangeException>(() => guide.View(scroll: int.MinValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => guide.View(scroll: int.MaxValue));
        Assert.Equal(0, guide.CurrentLine);
        Assert.Contains("lines 1-3/", guide.View(), StringComparison.Ordinal);
    }

    [Fact, Trait("TestId", "T808")]
    public void T808NextHeadingAndUnknownAnchorKeepExplicitPrimarySafetyDifference()
    {
        using var reference = Reference();
        var positions = reference.RootElement.GetProperty("syntheticAnchorPositions").EnumerateArray().ToArray();
        Assert.True(positions[0].GetProperty("after").GetInt32() > 0);
        Assert.Equal(positions[1].GetProperty("before").GetInt32(), positions[1].GetProperty("after").GetInt32());
        var guide = new NomadNetGuide();
        guide.Open("display");
        guide.Open("#");
        Assert.True(guide.CurrentLine > 0);
        guide.Open("#");
        var last = guide.CurrentLine;
        guide.Open("#");
        Assert.Equal(last, guide.CurrentLine);
        Assert.Throws<ArgumentException>(() => guide.Open("#missing")); // Primary silently ignores unknown anchors.
        Assert.Equal(last, guide.CurrentLine);
    }

    [Fact, Trait("TestId", "T809")]
    public void T809LocalSearchIsBoundedCaseInsensitiveAndDoesNotMutateOrSearchPrivateState()
    {
        var guide = new NomadNetGuide();
        guide.Open("hosting#access");
        var line = guide.CurrentLine;
        var matches = guide.Find("restart");
        Assert.NotEmpty(matches);
        Assert.Equal(matches, guide.Find("RESTART"));
        foreach (var match in matches)
        {
            Assert.Contains("restart", match.Text, StringComparison.OrdinalIgnoreCase);
            Assert.NotEmpty(new NomadNetGuide().Open(match.Target));
        }
        Assert.Equal(64, guide.Find("e").Count);
        Assert.Empty(guide.Find("UNIQUE-PRIVATE-SYNTHETIC-NOT-IN-GUIDE"));
        Assert.Throws<ArgumentNullException>(() => guide.Find(null!));
        foreach (var term in new[] { "", " ", "a\nb", new string('x', 129) })
            Assert.Throws<ArgumentException>(() => guide.Find(term));
        Assert.Equal("hosting", guide.CurrentTopic);
        Assert.Equal(line, guide.CurrentLine);
    }

    [Fact, Trait("TestId", "T810")]
    public void T810EveryTopicCanBeSelectedAndAllOriginalSamplesAreBounded()
    {
        var guide = new NomadNetGuide();
        var topics = NomadNetGuide.Topics.Keys.ToArray();
        for (var i = 0; i < topics.Length; i++)
        {
            Assert.NotEmpty(guide.Select(i + 1));
            Assert.Equal(topics[i], guide.CurrentTopic);
        }
        foreach (var sample in NomadNetGuide.Snippets.Values)
        {
            Assert.InRange(sample.Length, 1, 2048);
            Assert.DoesNotContain('\0', sample);
        }
        Assert.Contains("physical", guide.Open("troubleshooting"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GPLv3", guide.Open("licenses"), StringComparison.Ordinal);
    }
}
