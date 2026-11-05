using System.Text;
using System.Text.Json;

namespace NomadNet.Tests;

// GenerateNomadNetEditorScrollingVectors.py executes primary 1.4.4
// f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2 PageList/Editor; shim boundaries are in the fixture.
[Trait("Category", "Unit")]
public sealed class NomadNetEditorScrollingTests
{
    [Fact, Trait("TestId", "T700")]
    public void PageMovementsAndExtremeBoundsMatchExecutedPrimary()
    {
        using var fixture = JsonDocument.Parse(typeof(NomadNetEditorScrollingTests).Assembly
            .GetManifestResourceStream("NomadNet.Tests.NomadNetEditorScrollingVectors.json")!);
        var root = fixture.RootElement;
        Assert.Equal("f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2", root.GetProperty("revision").GetString());
        Assert.Equal("ae6c09cde38d473fd97d1758ebcbc13edaf5d7e1f97dc4e698a870c68cd8d378",
            root.GetProperty("canonicalSourceSha256").GetProperty("nomadnet/ui/textui/Editor.py").GetString());
        var viewport = new NomadNetEditorViewport();
        foreach (var operation in root.GetProperty("operations").EnumerateArray())
        {
            viewport.Scroll(operation.GetProperty("key").GetString()!.Replace(' ', '-'), 80, 6);
            Assert.Equal(operation.GetProperty("position").GetInt32(), viewport.Position);
        }
        foreach (var operation in root.GetProperty("positions").EnumerateArray())
        {
            viewport.Scroll("home", 80, 6);
            viewport.Scroll(operation.GetProperty("requested").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture), 80, 6);
            Assert.Equal(operation.GetProperty("position").GetInt32(), viewport.Position);
        }
        Assert.DoesNotContain("scroll", string.Join(",", root.GetProperty("editor").GetProperty("persistedKeys")
            .EnumerateArray().Select(value => value.GetString())));
    }

    [Theory, Trait("TestId", "T701")]
    [InlineData("bogus")]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    [InlineData("1 extra")]
    [InlineData(" 1")]
    [InlineData("")]
    public void MalformedMovementsLeavePositionUnchanged(string movement)
    {
        var viewport = new NomadNetEditorViewport();
        viewport.Scroll("12", 100, 6);
        Assert.Throws<ArgumentException>(() => viewport.Scroll(movement, 100, 6));
        Assert.Equal(12, viewport.Position);
    }

    [Fact, Trait("TestId", "T702")]
    public void ClampResizeEmptySingleRowAndInt32DeltasNeverOverflow()
    {
        var viewport = new NomadNetEditorViewport();
        viewport.Scroll(int.MaxValue.ToString(), int.MaxValue, 1);
        Assert.Equal(int.MaxValue - 1, viewport.Position);
        viewport.Scroll(int.MaxValue.ToString(), int.MaxValue, 1);
        Assert.Equal(int.MaxValue - 1, viewport.Position);
        viewport.Scroll(int.MinValue.ToString(), int.MaxValue, 1);
        Assert.Equal(0, viewport.Position);
        viewport.Scroll("end", 80, 6);
        viewport.Clamp(80, 60);
        Assert.Equal(20, viewport.Position);
        viewport.Clamp(1, 60);
        Assert.Equal(0, viewport.Position);
        viewport.Scroll("page-down", 10, 1);
        Assert.Equal(1, viewport.Position);
        viewport.Clamp(0, 1);
        Assert.Equal(0, viewport.Position);
        Assert.Throws<ArgumentOutOfRangeException>(() => viewport.Scroll("end", 10, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => viewport.Clamp(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => viewport.Clamp(10, 61));
    }

    [Fact, Trait("TestId", "T703")]
    public void VisualWindowsPreserveLogicalGuttersAcrossWrappingAndEmptyLines()
    {
        const string text = "alpha beta gamma\nnext\n";
        var count = NomadNetEditorSyntax.CountRows(text, 8);
        var all = NomadNetEditorSyntax.Rows(text, 8, 60);
        Assert.Equal(count, all.Count);
        for (var index = 0; index < count; index++)
            Assert.Equal(all[index].Text, Assert.Single(NomadNetEditorSyntax.Rows(text, 8, 1, index)).Text);
        Assert.StartsWith(" 2 ", all[^2].Text);
        Assert.StartsWith(" 3 ", all[^1].Text);
        Assert.Empty(NomadNetEditorSyntax.Rows(text, 8, 1, count));
        Assert.Throws<ArgumentOutOfRangeException>(() => NomadNetEditorSyntax.Rows(text, 8, 1, -1));
    }

    [Theory, Trait("TestId", "T704")]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(40)]
    [InlineData(240)]
    public void LargeUnicodeTabsAndControlsKeepBoundedWindowsWithoutLosingSource(int width)
    {
        var text = string.Concat(Enumerable.Repeat("😀\tα\u001b\u202e", 10000)) + "\nlast";
        var original = text;
        var count = NomadNetEditorSyntax.CountRows(text, width);
        var tail = NomadNetEditorSyntax.Rows(text, width, 3, Math.Max(0, count - 3));
        Assert.InRange(tail.Count, 1, 3);
        Assert.All(tail, row =>
        {
            Assert.Equal(width, row.Text.Length);
            Assert.DoesNotContain('\u001b', row.Text);
            Assert.DoesNotContain('\t', row.Text);
            Assert.DoesNotContain('\u202e', row.Text);
            Assert.All(row.Spans, span => Assert.InRange(span.Start + span.Length, 0, width));
            _ = Encoding.UTF8.GetBytes(row.Text);
        });
        Assert.Equal(original, text);
        Assert.Throws<InvalidDataException>(() => NomadNetEditorSyntax.CountRows(new string('x', 1_000_001), width));
    }
}

[Trait("Category", "Integration")]
public sealed class NomadNetEditorScrollingIntegrationTests
{
    [Fact, Trait("TestId", "T705")]
    public async Task IndependentPositionsSurviveRefreshPaneToggleSaveAndEditButResetOnSelectionAndRestart()
    {
        using var work = new ScrollingWorkspace();
        var text = string.Join("\n\n", Enumerable.Range(0, 80).Select(index => $"line {index:D3}"));
        await File.WriteAllTextAsync(Path.Combine(work.Pages, "long.mu"), text);
        await using (var editor = work.Editor())
        {
            await editor.ActivatePresentationAsync();
            await editor.OpenAsync("pages", "long.mu");
            editor.View(120, 6);
            editor.Scroll("editor", "12");
            editor.Scroll("preview", "17");
            Assert.Equal(12, editor.SourceScrollPosition);
            Assert.Equal(17, editor.PreviewScrollPosition);
            await editor.PaneAsync("preview");
            editor.View(120, 6);
            Assert.Equal(17, editor.PreviewScrollPosition);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() => editor.Scroll("preview", "down")));
            await editor.PaneAsync("preview");
            editor.View(120, 6);
            editor.Buffer.Set(text + "\n\nlatest");
            Assert.Equal(17, editor.PreviewScrollPosition);
            await UntilReadyAsync(editor);
            editor.View(120, 6);
            Assert.Equal(17, editor.PreviewScrollPosition);
            Assert.Equal(12, editor.SourceScrollPosition);
            await editor.SaveAsync();
            Assert.Equal(text + "\n\nlatest", await File.ReadAllTextAsync(Path.Combine(work.Pages, "long.mu")));
            Assert.Equal(17, editor.PreviewScrollPosition);
            await editor.SaveAsAsync("pages", "copy.mu");
            Assert.Equal(17, editor.PreviewScrollPosition);
            Assert.False(await editor.CloseAsync("cancel"));
            Assert.Equal(17, editor.PreviewScrollPosition);
            Assert.True(await editor.CloseAsync("discard"));
            Assert.Equal(0, editor.SourceScrollPosition);
            Assert.Equal(0, editor.PreviewScrollPosition);
            await editor.OpenAsync("pages", "long.mu");
            Assert.Equal(0, editor.SourceScrollPosition);
            editor.Scroll("editor", "end");
        }
        await using var restarted = work.Editor();
        await restarted.ActivatePresentationAsync();
        Assert.Equal("long.mu", restarted.RelativePath);
        Assert.Equal(0, restarted.SourceScrollPosition);
        Assert.Equal(0, restarted.PreviewScrollPosition);
    }

    [Fact, Trait("TestId", "T706")]
    public async Task ShrinkPendingCancellationAndHiddenLastPaneProtectionRemainHealthy()
    {
        using var work = new ScrollingWorkspace();
        await using var editor = work.Editor();
        Assert.Throws<InvalidOperationException>(() => editor.Scroll("editor", "down"));
        await editor.ActivatePresentationAsync();
        await editor.OpenAsync("pages", "new.mu", true);
        editor.Buffer.Set(string.Join('\n', Enumerable.Repeat("line", 80)));
        await UntilReadyAsync(editor);
        editor.View(80, 6);
        editor.Scroll("editor", "end");
        editor.Scroll("preview", "end");
        editor.Buffer.Set("short");
        await UntilReadyAsync(editor);
        var view = editor.View(80, 6);
        Assert.Equal(0, editor.SourceScrollPosition);
        Assert.Equal(0, editor.PreviewScrollPosition);
        Assert.Contains("short", view);
        Assert.Throws<ArgumentException>(() => editor.Scroll("files", "down"));
        Assert.Throws<ArgumentException>(() => editor.Scroll("focused", "down"));
        await editor.PaneAsync("files");
        await editor.PaneAsync("preview");
        await editor.PaneAsync("editor");
        Assert.Equal(["editor"], editor.VisiblePanes);
        editor.Scroll("focused", "down");
        editor.Buffer.Set("pending");
        await editor.CloseAsync("discard");
        await Task.Delay(500);
        Assert.Equal("", editor.AutomaticPreview!.Text);
        Assert.DoesNotContain("pending", editor.View(80, 6));
    }

    private static async Task UntilReadyAsync(NomadNetEditor editor)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (editor.AutomaticPreview?.Revision != editor.PreviewRevision) await Task.Delay(10, timeout.Token);
    }

    private sealed class ScrollingWorkspace : IDisposable
    {
        private readonly string _root = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomad-editor-scrolling", "managed-" + Guid.NewGuid().ToString("N")));
        public string Pages => Path.Combine(_root, "storage", "pages");
        public ScrollingWorkspace()
        {
            Directory.CreateDirectory(Pages);
            Directory.CreateDirectory(Path.Combine(_root, "storage", "files"));
        }
        public NomadNetEditor Editor() => new(_root, new());
        public void Dispose() => Directory.Delete(_root, true);
    }
}
