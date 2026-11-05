using Sideband;

namespace Sideband.Tests;

[Trait("Category", "Unit")]
public sealed class SidebandStyledTextTests
{
    [Fact]
    [Trait("TestId", "T572")]
    public void OriginalBoundedSpanParserPreservesLiteralTextAndIndependentStyles()
    {
        var runs = SidebandStyledText.Parse("[b]bold [i]both[/b] italic[/i] &bl;b&br; &amp;amp;");
        Assert.Equal(["bold ", "both", " italic", " [b] &amp;"], runs.Select(run => run.Text));
        Assert.Equal(new(Bold: true), runs[0].Style);
        Assert.Equal(new(Bold: true, Italic: true), runs[1].Style);
        Assert.Equal(new(Italic: true), runs[2].Style);
        Assert.Equal(new(), runs[3].Style);
        Assert.Equal("[B]literal[/B][b=x]literal[/b=x]\n[unfinished\n",
            string.Concat(SidebandStyledText.Parse("[B]literal[/B][b=x]literal[/b=x]\n[unfinished\n[b]x[/b]")
                .SkipLast(1).Select(run => run.Text)));
        var bold = SidebandStyledText.Parse("[unfinished\n[b]x[/b]");
        Assert.True(bold[^1].Style.Bold);
        var code = Assert.Single(SidebandStyledText.Parse("[font=mono][size=14]code[/size][/font]"));
        Assert.True(code.Style.Monospace);
        Assert.Equal(14, code.Style.Size);
        Assert.Equal("#ff000080", Assert.Single(SidebandStyledText.Parse("[color=ff000080]red[/color]")).Style.Color);
        Assert.Equal("purple", Assert.Single(SidebandStyledText.Parse("[color=purple]name[/color]")).Style.Color);
        Assert.Equal(8, Assert.Single(SidebandStyledText.Parse("[size=0]small[/size]")).Style.Size);
        Assert.Equal(48, Assert.Single(SidebandStyledText.Parse("[size=1000000]large[/size]")).Style.Size);
        Assert.Equal(-1, Assert.Single(SidebandStyledText.Parse("[sub]low[/sub]")).Style.Script);
        Assert.Equal(1, Assert.Single(SidebandStyledText.Parse("[sup]high[/sup]")).Style.Script);
    }

    [Fact]
    [Trait("TestId", "T573")]
    public void MalformedAndExcessiveStylesFailExplicitlyAndTrustCannotActivateEscapedMarkup()
    {
        foreach (var markup in new[] { "[/b]", "[size=NaN]x", "[size=-1]x", "[color=##ffffff]x",
            "[color=url(x)]x", "[font=C:\\private.ttf]x", string.Concat(Enumerable.Repeat("[b]", 65)),
            string.Concat(Enumerable.Repeat("[b]x[/b]", 4097)), new string('x', SidebandMessageMarkup.MaximumMarkupLength + 1) })
            Assert.Throws<InvalidDataException>(() => SidebandStyledText.Parse(markup));
        Assert.Throws<ArgumentNullException>(() => SidebandStyledText.Parse(null!));
        Assert.Throws<InvalidDataException>(() => SidebandMessageMarkup.Build(new string('x', 65_537), 2, false, false));
        var protectedRuns = SidebandStyledText.Parse(SidebandMessageMarkup.Build("[b]raw[/b]", 3, true, false));
        Assert.Equal("[b]raw[/b]", Assert.Single(protectedRuns).Text);
        Assert.False(protectedRuns[0].Style.Bold);
        Assert.True(Assert.Single(SidebandStyledText.Parse(SidebandMessageMarkup.Build("[b]bold[/b]", 3, true, true))).Style.Bold);
        Assert.False(Assert.Single(SidebandStyledText.Parse(SidebandMessageMarkup.Build("[b]raw[/b]", 255, false, true))).Style.Bold);
        Assert.True(Assert.Single(SidebandStyledText.Parse(SidebandMessageMarkup.Build(" \n", 2, false, false))).Style.Italic);
    }
}
