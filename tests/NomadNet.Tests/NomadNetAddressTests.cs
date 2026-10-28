namespace NomadNet.Tests;

[Trait("Category", "Unit")]
public sealed class NomadNetAddressTests
{
    [Fact]
    public void ParseUrl_UsesUpstreamDefaultAndCurrentNodeForRelativeLink()
    {
        var hash = "ABCDEF0123456789ABCDEF0123456789";

        Assert.Equal((hash.ToLowerInvariant(), NomadNetAddress.DefaultPagePath), NomadNetAddress.ParseUrl(hash));
        Assert.Equal((hash.ToLowerInvariant(), "/page/about.mu"), NomadNetAddress.ParseUrl(":" + "/page/about.mu", hash));
        Assert.Equal((hash.ToLowerInvariant(), NomadNetAddress.DefaultPagePath), NomadNetAddress.ParseUrl(hash + ":"));
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("/file/private")]
    [InlineData("/page/../../secret")]
    [InlineData("C:\\secret")]
    public void NormalizePagePath_RejectsUnsafeOrUnsupportedPaths(string path) =>
        Assert.Throws<ArgumentException>(() => NomadNetAddress.NormalizePagePath(path));

    [Fact]
    public void NormalizeHash_RequiresExactTruncatedHash()
    {
        Assert.Equal(new string('a', 32), NomadNetAddress.NormalizeHash(new string('A', 32)));
        Assert.Throws<ArgumentException>(() => NomadNetAddress.NormalizeHash(new string('a', 31)));
        Assert.Throws<ArgumentException>(() => NomadNetAddress.NormalizeHash(new string('g', 32)));
    }

    [Fact]
    public void TerminalText_RemovesControlSequences()
    {
        Assert.Equal("hello[2J", NomadNetTerminalText.Safe("hello\u001b[2J"));
    }
}

[Trait("Category", "Unit")]
public sealed class MicronTextTests
{
    [Fact]
    public void Render_ExtractsUpstreamBracketLinksAndTextualHeadings()
    {
        var page = MicronText.Render(">>Heading\nWelcome [next`/page/next.mu]\n".Select(character => (byte)character).ToArray());

        Assert.Contains("Heading", page.Text);
        Assert.Contains("[1] next", page.Text);
        Assert.Equal(new NomadNetPageLink("next", "/page/next.mu", ""), Assert.Single(page.Links));
    }

    [Fact]
    public void Render_RejectsOversizedPagesAndMarksUnsupportedMedia()
    {
        Assert.Throws<InvalidDataException>(() => MicronText.Render(new byte[MicronText.MaximumPageBytes + 1]));

        var page = MicronText.Render("`(alt`w=12`/media/picture.webp)\n`{/page/partial.mu}\n"u8);
        Assert.Contains("[image content is not rendered]", page.Text);
        Assert.Contains("[dynamic page content is not rendered]", page.Text);
    }

    [Fact]
    public void Render_LiteralSectionDoesNotInterpretLinks()
    {
        var page = MicronText.Render("`=\n[not a link`/page/not-a-link.mu]\n`=\n"u8);

        Assert.Contains("[not a link`/page/not-a-link.mu]", page.Text);
        Assert.Empty(page.Links);
    }
}
