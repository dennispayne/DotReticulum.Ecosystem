using System.Text;
using System.Text.Json;
using DotReticulum.Transport;

namespace NomadNet.Tests;

// Primary 1.4.4 f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2.
// GenerateNomadNetEditorVectors.py executes the actual methods and Urwid buffer;
// GenerateNomadNetWorkflowVectors.py independently covers lexical confinement and atomic UTF-8 writes.
[Trait("Category", "Unit")]
public sealed class NomadNetEditorTests
{
    [Fact]
    [Trait("TestId", "T390")]
    public void TextOperationsMatchExecutedPrimary()
    {
        using var fixture = JsonDocument.Parse(typeof(NomadNetEditorTests).Assembly
            .GetManifestResourceStream("NomadNet.Tests.NomadNetEditorVectors.json")!);
        var root = fixture.RootElement;
        Assert.Equal("f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2", root.GetProperty("revision").GetString());
        Assert.Equal("ae6c09cde38d473fd97d1758ebcbc13edaf5d7e1f97dc4e698a870c68cd8d378",
            root.GetProperty("sources").GetProperty("nomadnet/ui/textui/Editor.py").GetString());
        var buffer = new NomadNetEditorBuffer();
        buffer.Load(root.GetProperty("initialText").GetString()!);
        foreach (var row in root.GetProperty("operations").EnumerateArray())
        {
            var args = row.GetProperty("arguments").EnumerateArray().Select(item => item.GetString()!).ToArray();
            switch (row.GetProperty("operation").GetString())
            {
                case "find_next": buffer.Find(args[0]); break;
                case "replace_one": buffer.ReplaceOne(args[0], args[1]); break;
                case "replace_all": buffer.ReplaceAll(args[0], args[1]); break;
                case "_insert_snippet": buffer.Insert(args[0]); break;
                default: Assert.Fail("Unexercised reference operation."); break;
            }
            Assert.Equal(row.GetProperty("text").GetString(), buffer.Text);
            Assert.Equal(row.GetProperty("cursor").GetInt32(), buffer.Cursor);
        }
        Assert.Empty(root.GetProperty("temporaryFiles").EnumerateArray());
    }

    [Fact]
    [Trait("TestId", "T391")]
    public void BoundedBufferIsTransactionalAndInvalidUtf16CannotBeSaved()
    {
        var buffer = new NomadNetEditorBuffer();
        buffer.Load("before");
        Assert.Throws<EncoderFallbackException>(() => buffer.Set("\ud800"));
        Assert.Throws<InvalidDataException>(() => buffer.Set(new string('\u03bb', 500001)));
        Assert.Equal("before", buffer.Text);
        Assert.False(buffer.Dirty);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Insert("after", 7));
        buffer.Load(new string('x', NomadNetEditor.MaximumBytes));
        Assert.Throws<InvalidDataException>(() => buffer.ReplaceAll("x", "xx"));
        Assert.False(buffer.Dirty);
        Assert.Equal(NomadNetEditor.MaximumBytes, buffer.Text.Length);
        Assert.Equal(-1, buffer.Find(""));
        Assert.Equal(-1, buffer.Find("absent"));
        buffer.Load("", editable: false);
        Assert.Throws<InvalidOperationException>(() => buffer.Set("binary override"));
    }

    [Fact]
    [Trait("TestId", "T391")]
    public void BoundedOriginalGuideTopicsLinksAnchorsAndSamplesStayLocalAndUsable()
    {
        var guide = new NomadNetGuide();
        foreach (var topic in NomadNetGuide.Topics.Keys)
        {
            Assert.NotEmpty(guide.Open(topic));
            foreach (var link in guide.Page.Links)
            {
                var target = link.Target;
                Assert.StartsWith("guide:", target);
                Assert.NotEmpty(new NomadNetGuide().Open(target));
            }
        }
        Assert.Contains("Access lists", guide.Open("hosting#access"), StringComparison.Ordinal);
        guide.Open("authoring");
        Assert.Contains("Hosting documents", guide.Back(), StringComparison.Ordinal);
        guide.Open("start");
        Assert.Contains("Authoring local documents", guide.Follow(1), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => guide.Open("https://example.invalid"));
        Assert.Throws<ArgumentException>(() => guide.Open("hosting#missing"));
        Assert.Throws<ArgumentOutOfRangeException>(() => guide.Follow(0));
        foreach (var snippet in NomadNetGuide.Snippets.Values)
            Assert.NotEmpty(MicronText.Render(Encoding.UTF8.GetBytes(snippet)).Text);
    }
}

[Trait("Category", "Integration")]
public sealed class NomadNetEditorStorageTests
{
    [Fact]
    [Trait("TestId", "T392")]
    public async Task PersistenceHostedTextBinaryPermissionsRenameDeleteAndRestart()
    {
        using var workspace = new Workspace();
        var editor = workspace.Editor();
        editor.CreateDirectory("pages", "chapter");
        editor.CreateDirectory("files");
        await editor.OpenAsync("pages", "chapter/notice.mu", create: true);
        editor.Buffer.Set(">Synthetic notice\nA \u03bb line.\n");
        Assert.Contains("Synthetic notice", editor.Preview(), StringComparison.Ordinal);
        Assert.DoesNotContain(">", editor.Preview(), StringComparison.Ordinal);
        Assert.False(await editor.CloseAsync("cancel"));
        Assert.True(editor.Buffer.Dirty);
        await Assert.ThrowsAsync<InvalidOperationException>(() => editor.OpenAsync("pages", "other.mu", create: true));
        await editor.SaveAsync();
        Assert.False(editor.Buffer.Dirty);
        Assert.Equal(editor.Buffer.Text, await File.ReadAllTextAsync(workspace.Path("pages", "chapter", "notice.mu")));
        var content = new NomadNetContent(workspace.Root);
        Assert.Equal(Encoding.UTF8.GetBytes(editor.Buffer.Text), Assert.IsType<byte[]>(
            await content.RespondAsync(new RequestContext("/page/chapter/notice.mu", null, new byte[16], new byte[16], null, 0), default)));
        await editor.PermissionsAsync([new string('a', 32), new string('a', 32)]);
        Assert.Equal(new string('a', 32) + "\n", await File.ReadAllTextAsync(workspace.Path("pages", "chapter", "notice.mu.allowed")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => editor.SaveAsAsync("files", "copy.txt"));
        await editor.RenameAsync("files", "note.txt");
        Assert.False(File.Exists(workspace.Path("pages", "chapter", "notice.mu")));
        Assert.True(File.Exists(workspace.Path("files", "note.txt.allowed")));
        var restarted = workspace.Editor();
        await restarted.ResumeAsync();
        Assert.Equal("files", restarted.Scope);
        Assert.Equal("note.txt", restarted.RelativePath);
        Assert.Equal(editor.Buffer.Text, restarted.Buffer.Text);
        await restarted.PermissionsAsync(null);
        Assert.False(File.Exists(workspace.Path("files", "note.txt.allowed")));
        await restarted.SaveAsAsync("files", "copy.txt");
        await restarted.DeleteAsync();
        Assert.False(restarted.IsOpen);
        Assert.True(File.Exists(workspace.Path("files", "note.txt")));
        Assert.False(File.Exists(workspace.Path("files", "copy.txt")));
        Assert.DoesNotContain(editor.Buffer.Text, await File.ReadAllTextAsync(workspace.Path("editor_state.json")), StringComparison.Ordinal);
        await File.WriteAllBytesAsync(workspace.Path("files", "binary.dat"), [0xff, 0x80]);
        await restarted.OpenAsync("files", "binary.dat");
        Assert.False(restarted.Buffer.Editable);
        Assert.Contains("read-only", restarted.Preview(), StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.SaveAsync());
        Assert.Equal(new byte[] { 0xff, 0x80 }, await File.ReadAllBytesAsync(workspace.Path("files", "binary.dat")));
        Assert.Equal(["binary.dat", "note.txt"], restarted.List("files"));
        Assert.Empty(Directory.GetFiles(workspace.Root, ".editor-*", SearchOption.AllDirectories));
    }

    [Fact]
    [Trait("TestId", "T392")]
    public async Task PersistenceConfigurationValidatesBeforeAtomicSaveAndRestartsWithoutImplicitRuntimeMutation()
    {
        using var workspace = new Workspace();
        var settings = await NomadNetSettings.OpenAsync(workspace.Root);
        await File.WriteAllBytesAsync(workspace.Path("settings.json"),
            [0xef, 0xbb, 0xbf, .. await File.ReadAllBytesAsync(workspace.Path("settings.json"))]);
        var editor = workspace.Editor();
        await editor.OpenAsync("config", "settings.json");
        var original = editor.Buffer.Text;
        editor.Buffer.Set(original.Replace("NomadNet user", "Synthetic author", StringComparison.Ordinal));
        editor.ValidateConfiguration();
        await editor.SaveAsync();
        Assert.Equal("NomadNet user", settings.DisplayName);
        Assert.Equal("Synthetic author", (await NomadNetSettings.OpenAsync(workspace.Root)).DisplayName);
        var saved = await File.ReadAllBytesAsync(workspace.Path("settings.json"));
        foreach (var invalid in new[] { "{}", "null", "{\"unknown\":true}", "{\"schemaVersion\":2}",
            "{\"displayName\":\"first\",\"displayName\":\"second\"}", "{\"displayName\":\"\"}" })
        {
            editor.Buffer.Set(invalid);
            await Assert.ThrowsAnyAsync<Exception>(() => editor.SaveAsync());
            Assert.Equal(saved, await File.ReadAllBytesAsync(workspace.Path("settings.json")));
            Assert.True(editor.Buffer.Dirty);
        }
        await editor.CloseAsync("discard");
        await workspace.Editor().ResumeAsync();
        Assert.Empty(Directory.GetFiles(workspace.Root, ".editor-*", SearchOption.AllDirectories));
    }

    [Fact]
    [Trait("TestId", "T393")]
    public async Task ConflictRuntimeWritersCannotOverwriteSavedRestartConfiguration()
    {
        using var workspace = new Workspace();
        var settings = new NomadNetSettings { PropagationNode = new string('a', 32) };
        await settings.SaveAsync(workspace.Root);
        await using var policy = new NomadNetPropagation(workspace.Root, settings, () => [], _ => { },
            (_, _) => Task.FromResult(0), () => { });
        var editor = workspace.Editor();
        await editor.OpenAsync("config", "settings.json");
        editor.Buffer.Set(editor.Buffer.Text.Replace("NomadNet user", "Synthetic restart", StringComparison.Ordinal));
        await policy.SaveConfigurationForRestartAsync(editor.SaveAsync);
        Assert.True(policy.ConfigurationRestartPending);
        Assert.Equal("NomadNet user", policy.Settings.DisplayName);
        var saved = await File.ReadAllBytesAsync(workspace.Path("settings.json"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.UpdateSettingsAsync(current => current with { ClipboardCopy = true }));
        await policy.RecordClientAnnounceAsync(DateTimeOffset.UtcNow);
        Assert.True(await policy.BeginSyncAsync(1));
        await policy.WaitForSyncAsync();
        Assert.NotNull(policy.Settings.LastClientAnnounce);
        Assert.NotNull(policy.Settings.LastLxmfSync);
        Assert.Equal(saved, await File.ReadAllBytesAsync(workspace.Path("settings.json")));
        Assert.Equal("Synthetic restart", (await NomadNetSettings.OpenAsync(workspace.Root)).DisplayName);
    }

    [Fact]
    [Trait("TestId", "T393")]
    public async Task ConflictConcurrentEditorsCancellationAndInvalidPathsPreserveFiles()
    {
        using var workspace = new Workspace();
        var first = workspace.Editor();
        first.CreateDirectory("pages");
        await first.OpenAsync("pages", "notice.mu", create: true);
        first.Buffer.Set("original");
        await first.SaveAsync();
        var second = workspace.Editor();
        await second.OpenAsync("pages", "notice.mu");
        first.Buffer.Set("first");
        second.Buffer.Set("second");
        var outcomes = await Task.WhenAll(Save(first), Save(second));
        Assert.Single(outcomes, outcome => outcome);
        Assert.Single(outcomes, outcome => !outcome);
        var bytes = await File.ReadAllBytesAsync(workspace.Path("pages", "notice.mu"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.SaveAsync(cancellation.Token));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(workspace.Path("pages", "notice.mu")));
        await first.CloseAsync("discard");
        await first.OpenAsync("pages", "notice.mu");
        await File.WriteAllTextAsync(workspace.Path("pages", "notice.mu.allowed"), "external");
        await Assert.ThrowsAsync<IOException>(() => first.RenameAsync("pages", "renamed.mu"));
        await Assert.ThrowsAsync<IOException>(() => first.DeleteAsync());
        Assert.True(File.Exists(workspace.Path("pages", "notice.mu")));
        foreach (var path in new[] { "../escape", ".hidden", "a/../b", @"a\..\b", "CON.txt", "a:b", "a.", "a ", "a.allowed", @"C:\outside", "a//b" })
            Assert.Throws<ArgumentException>(() => first.Resolve("pages", path));
        Assert.Throws<ArgumentException>(() => first.Resolve("config", "identity"));
        await first.CloseAsync("discard");
        await File.WriteAllBytesAsync(workspace.Path("pages", "huge.mu"), new byte[NomadNetEditor.MaximumBytes + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => first.OpenAsync("pages", "huge.mu"));
        Assert.False(first.IsOpen);
        Assert.Empty(Directory.GetFiles(workspace.Root, ".editor-*", SearchOption.AllDirectories));

        static async Task<bool> Save(NomadNetEditor editor)
        {
            try { await editor.SaveAsync(); return true; }
            catch (IOException) { return false; }
        }
    }

    [WindowsEditorFact]
    [Trait("TestId", "T394")]
    public async Task WindowsLockedPermissionCompanionRollsBackRenameAndDelete()
    {
        using var workspace = new Workspace();
        var editor = workspace.Editor();
        editor.CreateDirectory("pages");
        await editor.OpenAsync("pages", "notice.mu", create: true);
        editor.Buffer.Set("original");
        await editor.SaveAsync();
        await editor.PermissionsAsync([new string('b', 32)]);
        using (var locked = new FileStream(workspace.Path("pages", "notice.mu.allowed"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAsync<IOException>(() => editor.RenameAsync("pages", "renamed.mu"));
            await Assert.ThrowsAsync<IOException>(() => editor.DeleteAsync());
            Assert.True(File.Exists(workspace.Path("pages", "notice.mu")));
            Assert.False(File.Exists(workspace.Path("pages", "renamed.mu")));
            Assert.Equal("notice.mu", editor.RelativePath);
        }
        await editor.RenameAsync("pages", "renamed.mu");
        await editor.DeleteAsync();
        Assert.Empty(Directory.GetFiles(workspace.Root, ".editor-*", SearchOption.AllDirectories));
    }

    [LinuxEditorFact]
    [Trait("TestId", "T398")]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task LinuxSymlinksExecutablePreviewAndUnixModes()
    {
        using var workspace = new Workspace();
        var editor = workspace.Editor();
        editor.CreateDirectory("pages");
        var outside = workspace.Path("outside");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(workspace.Path("pages", "linked"), outside);
        Assert.Throws<IOException>(() => editor.Resolve("pages", "linked/notice.mu"));
        var script = workspace.Path("pages", "script.mu");
        await File.WriteAllTextAsync(script, "#!/bin/sh\nprintf must-not-run\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var mode = File.GetUnixFileMode(script);
        await editor.OpenAsync("pages", "script.mu");
        Assert.Contains("Executable source only", editor.Preview(), StringComparison.Ordinal);
        editor.Buffer.Set("#!/bin/sh\nprintf new\n");
        await editor.SaveAsync();
        Assert.Equal(mode, File.GetUnixFileMode(script));
        Assert.Empty(Directory.GetFileSystemEntries(outside));
        File.CreateSymbolicLink(workspace.Path("pages", "broken.mu"), workspace.Path("missing"));
        await Assert.ThrowsAsync<IOException>(() => editor.OpenAsync("pages", "broken.mu", create: true));
    }

    private sealed class WindowsEditorFactAttribute : FactAttribute
    {
        public WindowsEditorFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Requires Windows deny-delete sharing semantics; not evidence for Unix rename failures.";
        }
    }

    private sealed class LinuxEditorFactAttribute : FactAttribute
    {
        public LinuxEditorFactAttribute()
        {
            if (!OperatingSystem.IsLinux()) Skip = "Requires Linux symlinks/executable modes; current Linux replay is blocked by WSL storage EIO.";
        }
    }

    private sealed class Workspace : IDisposable
    {
        public string Root { get; } = System.IO.Path.GetFullPath(System.IO.Path.Combine("artifacts", "tests", "nomad-editor", Guid.NewGuid().ToString("N")));
        public Workspace() => Directory.CreateDirectory(Root);
        public string Path(params string[] parts) => System.IO.Path.Combine(
            parts.FirstOrDefault() is "pages" or "files" ? [Root, "storage", .. parts] : [Root, .. parts]);
        public NomadNetEditor Editor() => new(Root, new NomadNetSettings());
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
