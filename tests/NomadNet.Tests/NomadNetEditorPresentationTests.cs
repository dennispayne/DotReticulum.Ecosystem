using System.Text;
using System.Text.Json;

namespace NomadNet.Tests;

// Actual primary 1.4.4 f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2:
// GenerateNomadNetEditorPresentationVectors.py executes real PageEditorDisplay, Browser and Urwid
// on synthetic files; only screen/palette and alarm ownership are headless.
[Trait("Category", "Unit")]
public sealed class NomadNetEditorPresentationTests
{
    internal static JsonDocument Vectors() => JsonDocument.Parse(typeof(NomadNetEditorPresentationTests).Assembly
        .GetManifestResourceStream("NomadNet.Tests.NomadNetEditorPresentationVectors.json")!);

    [Fact, Trait("TestId", "T600")]
    public void OriginalSyntaxMatchesExecutedPrimaryIncludingMalformedTagsAndUnicodeOffsets()
    {
        using var fixture = Vectors();
        Assert.Equal("f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2", fixture.RootElement.GetProperty("revision").GetString());
        Assert.Equal("ae6c09cde38d473fd97d1758ebcbc13edaf5d7e1f97dc4e698a870c68cd8d378",
            fixture.RootElement.GetProperty("canonicalSourceSha256").GetProperty("nomadnet/ui/textui/Editor.py").GetString());
        foreach (var example in fixture.RootElement.GetProperty("highlighting").EnumerateArray())
        {
            var text = example.GetProperty("text").GetString()!;
            var actual = NomadNetEditorSyntax.Highlight(text);
            var expected = example.GetProperty("runs").EnumerateArray().ToArray();
            Assert.Equal(expected.Length, actual.Count);
            var offset = 0;
            for (var index = 0; index < expected.Length; index++)
            {
                var name = expected[index][0].GetString();
                var style = name is null ? NomadNetEditorStyle.Plain :
                    Enum.Parse<NomadNetEditorStyle>(name["editor_".Length..], true);
                var length = text[offset..].EnumerateRunes().Take(expected[index][1].GetInt32()).Sum(rune => rune.Utf16SequenceLength);
                Assert.Equal(new(offset, length, style), actual[index]);
                offset += length;
            }
            Assert.Equal(text.Length, actual.Sum(span => span.Length));
        }
        Assert.NotEmpty(NomadNetEditorSyntax.Highlight("#" + new string('x', 49999)));
        Assert.Empty(NomadNetEditorSyntax.Highlight("#" + new string('x', 50000)));
        Assert.Throws<InvalidDataException>(() => NomadNetEditorSyntax.Highlight(new string('x', 1_000_001)));
    }

    [Fact, Trait("TestId", "T601")]
    public void OriginalWrapAwareGuttersMatchExecutedAsciiRowsAndStayBoundedSafe()
    {
        using var fixture = Vectors();
        foreach (var example in fixture.RootElement.GetProperty("gutters").EnumerateArray())
        {
            var actual = NomadNetEditorSyntax.Rows("alpha beta gamma\nnext\n", example.GetProperty("width").GetInt32());
            Assert.Equal(example.GetProperty("rows").EnumerateArray().Select(row => row.GetString()), actual.Select(row => row.Text));
        }
        Assert.Single(NomadNetEditorSyntax.Rows(new string('x', 100000), 40, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => NomadNetEditorSyntax.Rows("", 0));
        var row = Assert.Single(NomadNetEditorSyntax.Rows("#\u001b[31m\u202eevil", 40));
        Assert.DoesNotContain('\u001b', row.Text);
        Assert.DoesNotContain('\u202e', row.Text);
        Assert.Equal(row.Text, NomadNetEditorSyntax.Color(row, new() { ColorMode = "monochrome" }, true));
        Assert.Contains("\u001b[", NomadNetEditorSyntax.Color(row, new(), true));
        Assert.Single(NomadNetEditorSyntax.Rows("😀😀", 1, 1));
    }

    [Fact, Trait("TestId", "T602")]
    public async Task DebounceRequiresFourHundredMillisecondsAndLatestSnapshotWithoutSubscribers()
    {
        var clock = new ManualClock();
        var rendered = new List<string>();
        await using var preview = new NomadNetEditorPreview(text => { rendered.Add(text); return text; }, clock);
        preview.Change("first");
        await clock.WaitForTimerAsync();
        clock.Advance(399);
        Assert.Null(preview.Current);
        preview.Change("latest");
        await clock.WaitForTimerAsync(replacement: true);
        clock.Advance(399);
        Assert.Null(preview.Current);
        clock.Advance(1);
        await UntilAsync(() => preview.Current is not null);
        Assert.Equal(["latest"], rendered);
        Assert.Equal(preview.Revision, preview.Current!.Revision);
        Assert.Equal("latest", preview.Current.Text);
    }

    [Fact, Trait("TestId", "T603")]
    public async Task CoreRenderingIsolatedObserversReportedErrorsAndContinuedService()
    {
        var clock = new ManualClock();
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var observed = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var preview = new NomadNetEditorPreview(text => text == "fail" ? throw new IOException("synthetic render") : text, clock);
        preview.Log = errors.Enqueue;
        preview.Rendered += _ => throw new InvalidOperationException("synthetic observer");
        preview.Rendered += result =>
        {
            observed.Enqueue(result.Text);
            (result.Error is null ? recovered : failed).TrySetResult();
        };
        preview.Change("fail");
        await clock.WaitForTimerAsync();
        clock.Advance(400);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(observed);
        Assert.NotNull(preview.Current!.Error);
        preview.Change("healthy");
        await clock.WaitForTimerAsync();
        clock.Advance(400);
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, observed.Count);
        Assert.Equal("healthy", preview.Current!.Text);
        Assert.Equal(3, errors.Count);
        Assert.Contains("render failed", errors.First());
        Assert.All(errors.Skip(1), message => Assert.Contains("observer failed", message));
    }

    [Fact, Trait("TestId", "T604")]
    public async Task SelectionRenderAndDisposalRacesCannotPublishStaleContent()
    {
        var clock = new ManualClock();
        NomadNetEditorPreview? preview = null;
        preview = new(text => { preview!.Select("new selection"); return text; }, clock);
        await using (preview)
        {
            preview.Change("old selection");
            await clock.WaitForTimerAsync();
            clock.Advance(400);
            await UntilAsync(() => preview.Current?.Text == "new selection");
            Assert.Equal("new selection", preview.Current!.Text);
            preview.Change("cancelled");
            await clock.WaitForTimerAsync();
            await preview.DisposeAsync();
            clock.Advance(1000);
            Assert.Null(preview.Current);
            Assert.Throws<ObjectDisposedException>(() => preview.Change("late"));
            Assert.Throws<ObjectDisposedException>(() => preview.Select("late"));
        }
    }

    [Fact, Trait("TestId", "T609")]
    public async Task RapidBoundedChangesLoggerFailureAndRepeatedDisposalDoNotStopCore()
    {
        var clock = new ManualClock();
        var count = 0;
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preview = new NomadNetEditorPreview(text => { Interlocked.Increment(ref count); return text; }, clock)
        { Log = _ => throw new IOException("synthetic failing logger") };
        preview.Rendered += _ => throw new IOException("synthetic observer");
        preview.Rendered += _ => observed.TrySetResult();
        for (var index = 0; index < 1000; index++) preview.Change(index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await clock.WaitForTimerAsync();
        // The channel may be draining earlier snapshots; settle until the final timer owns the pending revision.
        for (var index = 0; index < 20 && !observed.Task.IsCompleted; index++)
        {
            clock.Advance(400);
            for (var yield = 0; yield < 100; yield++) await Task.Yield();
        }
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("999", preview.Current!.Text);
        Assert.Equal(1, count);
        Assert.Throws<InvalidDataException>(() => preview.Change(new string('x', 1_000_001)));
        await Task.WhenAll(preview.DisposeAsync().AsTask(), preview.DisposeAsync().AsTask());
        preview.Dispose();
    }

    [Fact, Trait("TestId", "T612")]
    public async Task ReentrantObserverDisposalReportsRatherThanDeadlockingItsOwnedWorker()
    {
        var clock = new ManualClock();
        var errors = new List<string>();
        await using var preview = new NomadNetEditorPreview(text => text, clock) { Log = errors.Add };
        preview.Rendered += _ => preview.Dispose();
        preview.Change("healthy");
        await clock.WaitForTimerAsync();
        clock.Advance(400);
        await UntilAsync(() => errors.Count == 1);
        Assert.Contains("cannot join its own worker", errors[0]);
        Assert.Equal("healthy", preview.Current!.Text);
    }

    internal static async Task UntilAsync(Func<bool> ready)
    {
        for (var count = 0; count < 10000; count++)
        {
            if (ready()) return;
            await Task.Yield();
        }
        Assert.Fail("In-process worker did not reach the expected state.");
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<Timer> _timers = [];
        private long _now;
        private int _created;
        private int _lastWait;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                var timer = new Timer(this, callback, state, _now + (long)dueTime.TotalMilliseconds);
                _timers.Add(timer);
                _created++;
                return timer;
            }
        }
        public Task WaitForTimerAsync(bool replacement = false)
        {
            var minimum = replacement ? _lastWait + 1 : Math.Max(1, _lastWait + 1);
            return WaitAsync();
            async Task WaitAsync()
            {
                await UntilAsync(() => { lock (_gate) return _created >= minimum; });
                lock (_gate) _lastWait = _created;
            }
        }
        public void Advance(long milliseconds)
        {
            Timer[] due;
            lock (_gate)
            {
                _now += milliseconds;
                due = _timers.Where(timer => !timer.Disposed && timer.Due <= _now).ToArray();
                foreach (var timer in due) timer.Disposed = true;
            }
            foreach (var timer in due) timer.Callback(timer.State);
        }
        private sealed class Timer(ManualClock clock, TimerCallback callback, object? state, long due) : ITimer
        {
            public bool Disposed { get; set; }
            public long Due { get; private set; } = due;
            public TimerCallback Callback { get; } = callback;
            public object? State { get; } = state;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._gate) { if (Disposed) return false; Due = clock._now + (long)dueTime.TotalMilliseconds; return true; }
            }
            public void Dispose() { lock (clock._gate) Disposed = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}

[Trait("Category", "Integration")]
public sealed class NomadNetEditorPresentationStorageTests
{
    [Fact, Trait("TestId", "T605")]
    public async Task TreeFilterExpansionPanesFocusAndActivationReopenMatchPrimary()
    {
        using var work = new Workspace();
        Directory.CreateDirectory(Path.Combine(work.Pages, "nested"));
        Directory.CreateDirectory(Path.Combine(work.Pages, "empty"));
        Directory.CreateDirectory(Path.Combine(work.Pages, ".hidden"));
        foreach (var path in new[] { "a.mu", "B.MU", "plain.txt", "nested\\z.mu", "nested\\visible.txt", ".hidden\\secret.mu" })
            await File.WriteAllTextAsync(Path.Combine(work.Pages, path), ">Synthetic source\n");
        await File.WriteAllTextAsync(Path.Combine(work.Pages, "a.mu.allowed"), "synthetic policy");
        using var fixture = NomadNetEditorPresentationTests.Vectors();
        using (var editor = work.Editor())
        {
            await editor.ActivatePresentationAsync();
            CheckTree("initialTree");
            await editor.ExpandAsync("pages", "nested", true);
            CheckTree("expandedTree");
            await editor.FilterAsync(true);
            CheckTree("filteredTree");
            foreach (var row in fixture.RootElement.GetProperty("panes").EnumerateArray())
            {
                await editor.PaneAsync(row.GetProperty("toggle").GetString()!);
                Assert.Equal(row.GetProperty("visible").EnumerateArray().Select(item => item.GetString()), editor.VisiblePanes);
                Assert.Equal(editor.VisiblePanes[row.GetProperty("focus").GetInt32()], editor.FocusedPane);
            }
            await editor.OpenAsync("pages", "a.mu");
            editor.FocusPane();
            Assert.Equal("preview", editor.FocusedPane);
            editor.FocusPane(backwards: true);
            Assert.Equal("editor", editor.FocusedPane);
            Assert.Contains("files=22%", editor.View());
            Assert.Contains("editor=40%", editor.View());
            Assert.Contains("preview=38%", editor.View());
            Assert.Throws<ArgumentOutOfRangeException>(() => editor.View(39));
            await editor.PaneAsync("preview");
            Assert.DoesNotContain("PREVIEW", editor.View());
            void CheckTree(string name) => Assert.Equal(
                fixture.RootElement.GetProperty(name).EnumerateArray().Select(entry => entry.GetProperty("path").GetString()),
                editor.Tree().Select(entry => entry.Scope + (entry.Path.Length == 0 ? "" : "/" + entry.Path.Replace('\\', '/'))));
        }
        using var restart = work.Editor();
        Assert.False(restart.IsOpen);
        await restart.ActivatePresentationAsync();
        Assert.Equal("a.mu", restart.RelativePath);
        Assert.True(restart.FilterMu);
        Assert.Equal(["files", "editor"], restart.VisiblePanes);
        Assert.Contains(restart.Tree(), entry => entry.Path == Path.Combine("nested", "z.mu"));
    }

    [Fact, Trait("TestId", "T606")]
    public async Task AutomaticPreviewHasNoRetrievalOrExecutionAndRetainsExplicitBounds()
    {
        using var work = new Workspace();
        using var editor = work.Editor();
        await editor.ActivatePresentationAsync();
        await editor.OpenAsync("pages", "new.mu", create: true);
        editor.Buffer.Set(">Latest heading\n`[remote`:/page] `{image`:/media/image}\n");
        await WaitPreviewAsync(editor);
        Assert.Contains("Latest heading", editor.AutomaticPreview!.Text);
        editor.Buffer.Set(new string('x', 100001));
        await WaitPreviewAsync(editor);
        Assert.Contains("disabled above 100000", editor.AutomaticPreview!.Text);
        Assert.Contains("disabled above 100000", editor.Preview());
        editor.Buffer.Set("unsaved synthetic draft");
        await editor.CloseAsync("discard");
        await Task.Delay(500);
        Assert.Equal("", editor.AutomaticPreview!.Text);
        Assert.DoesNotContain("unsaved", await File.ReadAllTextAsync(Path.Combine(work.Root, "editor_state.json")));
        await File.WriteAllBytesAsync(Path.Combine(work.Pages, "binary"), [0xff]);
        await editor.OpenAsync("pages", "binary");
        Assert.Contains("Binary", editor.AutomaticPreview!.Text);
        Assert.Throws<InvalidOperationException>(() => editor.Buffer.Set("overwrite"));
    }

    [Fact, Trait("TestId", "T607")]
    public async Task MalformedStateBoundsCancellationAndFailedPersistenceRetainCurrentControls()
    {
        using var work = new Workspace();
        var state = Path.Combine(work.Root, "editor_state.json");
        foreach (var json in new[] { "{", "{\"schemaVersion\":2}", "{\"visiblePanes\":[]}",
                     "{\"expanded\":[\"pages/../escape\"]}", "{\"visiblePanes\":[\"bogus\"]}",
                     "{\"scope\":\"pages\"}", "{\"expanded\":[null]}" })
        {
            await File.WriteAllTextAsync(state, json);
            using var invalid = work.Editor();
            await Assert.ThrowsAnyAsync<Exception>(() => invalid.ActivatePresentationAsync());
        }
        File.Delete(state);
        using var editor = work.Editor();
        await editor.ActivatePresentationAsync();
        await editor.FilterAsync(true);
        using (var locked = new FileStream(state, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAnyAsync<IOException>(() => editor.FilterAsync(false));
        Assert.True(editor.FilterMu);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => editor.ExpandAsync("pages", null, false, cancelled.Token));
        await editor.FilterAsync(false);
        await Assert.ThrowsAsync<ArgumentException>(() => editor.ExpandAsync("pages", "..", true));
        await Assert.ThrowsAsync<ArgumentException>(() => editor.PaneAsync("bogus"));
        Assert.Equal(3, editor.VisiblePanes.Count);
    }

    [Fact, Trait("TestId", "T608")]
    public async Task FolderMoveRemovalRebaseExpansionAndSelectionAcrossRestart()
    {
        using var work = new Workspace();
        Directory.CreateDirectory(Path.Combine(work.Pages, "nested"));
        await File.WriteAllTextAsync(Path.Combine(work.Pages, "nested", "a.mu"), ">Source");
        using (var editor = work.Editor())
        {
            await editor.ActivatePresentationAsync();
            await editor.ExpandAsync("pages", "nested", true);
            await editor.OpenAsync("pages", Path.Combine("nested", "a.mu"));
            var plan = await editor.InspectFolderAsync("pages", "nested");
            await editor.MoveFolderAsync(plan.Token, "files", "moved");
            Assert.Equal("files", editor.Scope);
            Assert.Contains(editor.Tree(), entry => entry.Scope == "files" && entry.Path == Path.Combine("moved", "a.mu"));
        }
        using var restart = work.Editor();
        await restart.ActivatePresentationAsync();
        Assert.Equal(Path.Combine("moved", "a.mu"), restart.RelativePath);
        var removal = await restart.InspectFolderAsync("files", "moved");
        await restart.RemoveFolderAsync(removal.Token, true);
        Assert.False(restart.IsOpen);
        Assert.DoesNotContain("moved", await File.ReadAllTextAsync(Path.Combine(work.Root, "editor_state.json")));
    }

    [Fact, Trait("TestId", "T610")]
    public async Task SchemaOneOldSelectionDeletionAndControlsOnlyStateRemainCompatible()
    {
        using var work = new Workspace();
        var state = Path.Combine(work.Root, "editor_state.json");
        await File.WriteAllTextAsync(Path.Combine(work.Pages, "old.mu"), ">Saved source");
        await File.WriteAllTextAsync(state, "{\"schemaVersion\":1,\"scope\":\"pages\",\"path\":\"old.mu\"}");
        using (var editor = work.Editor())
        {
            await editor.ActivatePresentationAsync();
            Assert.Equal("old.mu", editor.RelativePath);
            await editor.PaneAsync("files");
            await editor.DeleteAsync();
            Assert.False(editor.IsOpen);
        }
        using var restart = work.Editor();
        await restart.ActivatePresentationAsync();
        Assert.False(restart.IsOpen);
        Assert.Equal(["editor", "preview"], restart.VisiblePanes);
        await restart.FilterAsync(true);
        await restart.CloseAsync("discard");
    }

    [Fact, Trait("TestId", "T611")]
    public async Task BoundedTreeRefusesOversizeAndCancellationThenContinuesAfterRecovery()
    {
        using var work = new Workspace();
        using var editor = work.Editor();
        await editor.ActivatePresentationAsync();
        for (var index = 0; index < 2048; index++)
            await File.WriteAllTextAsync(Path.Combine(work.Pages, $"file-{index}.mu"), "");
        Assert.Throws<InvalidDataException>(() => editor.Tree());
        foreach (var file in Directory.EnumerateFiles(work.Pages)) File.Delete(file);
        Assert.Equal(2, editor.Tree().Count);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => editor.Tree(cancelled.Token));
        var nested = work.Pages;
        for (var index = 0; index < 17; index++) nested = Directory.CreateDirectory(Path.Combine(nested, "deeper")).FullName;
        await File.WriteAllTextAsync(Path.Combine(nested, "deep.mu"), "");
        await Assert.ThrowsAsync<InvalidDataException>(() => editor.FilterAsync(true));
        Assert.False(editor.FilterMu);
    }

    private static async Task WaitPreviewAsync(NomadNetEditor editor)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (editor.AutomaticPreview?.Revision != editor.PreviewRevision) await Task.Delay(10, timeout.Token);
    }

    private sealed class Workspace : IDisposable
    {
        public string Root { get; } = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomad-editor-presentation", "storage-" + Guid.NewGuid().ToString("N")));
        public string Pages => Path.Combine(Root, "storage", "pages");
        public Workspace() { Directory.CreateDirectory(Pages); Directory.CreateDirectory(Path.Combine(Root, "storage", "files")); }
        public NomadNetEditor Editor() => new(Root, new());
        public void Dispose() => Directory.Delete(Root, true);
    }
}
