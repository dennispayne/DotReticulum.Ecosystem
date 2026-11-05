using Sideband;

namespace Sideband.Tests;

public sealed partial class SidebandProfileImportTests
{
    [Theory]
    [InlineData("markdown", 2L)]
    [InlineData("bbcode", 3L)]
    [InlineData("unknown", 255L)]
    [InlineData("float-markdown", 2L)]
    [InlineData("float-bbcode", 3L)]
    [InlineData("float-unknown", null)]
    [InlineData("text", null)]
    [InlineData("bytes", null)]
    [Trait("TestId", "T579")]
    public async Task HistoricalRendererFieldsSurviveBoundedImportAndRestartWithoutChangingSourceOrTrust(string shape, long? expected)
    {
        using var profile = new Profile();
        object renderer = shape switch
        {
            "markdown" => 2L,
            "bbcode" => 3L,
            "unknown" => 255L,
            "float-markdown" => 2d,
            "float-bbcode" => 3d,
            "float-unknown" => 2.5d,
            "text" => "2",
            "bytes" => new byte[] { 2 },
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
        AddMessage(profile, "valid", renderer);
        var before = profile.Fingerprint();
        await SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, TestContext.Current.CancellationToken);
        var state = await new SidebandStateStore(profile.Target).LoadAsync(cancellationToken: TestContext.Current.CancellationToken);
        var message = Assert.Single(state.Messages);
        Assert.Equal(expected, message.Renderer);
        Assert.Equal("Test content", message.Content);
        Assert.True(Assert.Single(state.Contacts).Trusted);
        Assert.Equal(before, profile.Fingerprint());
        await using var reopened = new SidebandClient(new(profile.Target));
        var restored = Assert.Single((await reopened.LoadAsync(TestContext.Current.CancellationToken)).Messages);
        Assert.Equal(message with { Attachments = null }, restored with { Attachments = null });
        Assert.Equal(message.Attachments, restored.Attachments);
        Assert.False(reopened.IsConnected);
    }
}
