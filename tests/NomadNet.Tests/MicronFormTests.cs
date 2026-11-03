using System.Text;
using System.Text.Json;
using NomadNet;

namespace NomadNet.Tests;

[Trait("Category", "Unit")]
public sealed class MicronFormTests
{
    [Fact]
    public void FieldsAndSubmissionMatchExecutedPrimaryParserAndBrowser()
    {
        using var fixture = JsonDocument.Parse(typeof(MicronFormTests).Assembly.GetManifestResourceStream("NomadNet.Tests.NomadNetVectors.json")!);
        var source = string.Join('\n', fixture.RootElement.GetProperty("fields").EnumerateArray().Select(item => item.GetProperty("source").GetString()));
        var page = MicronText.Render(Encoding.UTF8.GetBytes(source));
        var index = 0;
        foreach (var item in fixture.RootElement.GetProperty("fields").EnumerateArray())
        {
            var field = item.GetProperty("field");
            var actual = page.Fields[index++];
            Assert.Equal(field.GetProperty("type").GetString(), actual.Kind);
            Assert.Equal(field.GetProperty("name").GetString(), actual.Name);
            if (actual.Kind == "field")
            {
                Assert.Equal(field.GetProperty("data").GetString(), actual.Value);
                Assert.Equal(field.GetProperty("width").GetInt32(), actual.Width);
                Assert.Equal(field.GetProperty("rows").GetInt32(), actual.Rows);
                Assert.Equal(field.GetProperty("masked").GetBoolean(), actual.Masked);
            }
            else
            {
                Assert.Equal(field.GetProperty("label").GetString(), actual.Label);
                Assert.Equal(field.GetProperty("value").GetString(), actual.Value);
                Assert.Equal(field.GetProperty("prechecked").GetBoolean(), actual.Selected);
            }
        }
        Assert.DoesNotContain("not-a-real-secret", page.Text);
        foreach (var item in fixture.RootElement.GetProperty("submissions").EnumerateArray())
        {
            var selection = string.Join('|', item.GetProperty("fields").EnumerateArray().Select(token => token.GetString()));
            var actual = page.Submission(selection)!;
            var expected = item.GetProperty("data");
            Assert.Equal(expected.EnumerateObject().Count(), actual.Count);
            foreach (var property in expected.EnumerateObject()) Assert.Equal(property.Value.GetString(), actual[property.Name]);
        }
        foreach (var item in fixture.RootElement.GetProperty("slug").EnumerateArray())
            Assert.Equal(item.GetProperty("slug").GetString(), NomadNetRenderedPage.HeadingSlug(item.GetProperty("source").GetString()!));
    }

    [Fact]
    public void EditingSelectionsMultilineAndBoundsAreFunctional()
    {
        var page = MicronText.Render("`<message`old>\n`<?|flags|a|*`Alpha>\n`<?|flags|b`Beta>\n`<^|choice|one|*`One>\n`<^|choice|two`Two>"u8);
        page.SetField(1, "new\nmultiline");
        page.SetField(2, "off");
        page.SetField(3, "on");
        page.SetField(5, "on");
        var data = page.Submission("*")!;
        Assert.Equal("new\nmultiline", data["field_message"]);
        Assert.Equal("b", data["field_flags"]);
        Assert.Equal("two", data["field_choice"]);
        Assert.False(page.Fields[3].Selected);
        Assert.Throws<ArgumentException>(() => page.SetField(1, "\x1b[2J"));
        Assert.Throws<ArgumentException>(() => page.SetField(1, new string('x', 65537)));
        Assert.Throws<ArgumentOutOfRangeException>(() => page.SetField(0, ""));
        var sanitised = MicronText.Render(Encoding.UTF8.GetBytes("`<input`a\u202eb\u001bc>"));
        Assert.Equal("abc", sanitised.Submission("*")!["field_input"]);
        Assert.Throws<InvalidDataException>(() => MicronText.Render(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("`<x`y>", 129)))));
        page.SetField(1, new string('x', 65536));
        Assert.Throws<InvalidDataException>(() => page.Submission("*"));
    }

    [Fact]
    public void AnchorsFoldVisibilityTablesAndPartialMetadataHaveExplicitBounds()
    {
        var page = MicronText.Render(">:top Header\n`->Folded\nhidden\n>>Nested\nnested-hidden\n>Visible\n`= \n`t\n| A | B |\n|--|--|\n| 1 | 2 |\n`t\n`{/page/update.mu`2`*|pid=summary}\n`[update`p:summary]\n"u8);
        Assert.DoesNotContain("hidden", page.DisplayText);
        Assert.Contains("| 1 | 2 |", page.DisplayText);
        Assert.Contains("hidden", page.AtAnchor("nested"));
        Assert.Contains("hidden", page.DisplayText);
        page.ToggleFold(1);
        Assert.DoesNotContain("hidden", page.DisplayText);
        var partial = Assert.Single(page.Partials);
        Assert.Equal("summary", partial.Id);
        Assert.Equal(2, partial.RefreshSeconds);
        Assert.Equal("*|pid=summary", partial.Fields);
        Assert.Throws<ArgumentException>(() => page.AtAnchor("missing"));
        Assert.Throws<ArgumentOutOfRangeException>(() => page.ToggleFold(2));
    }
}
