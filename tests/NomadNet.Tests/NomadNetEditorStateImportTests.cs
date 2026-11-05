using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotReticulum.Core;

namespace NomadNet.Tests;

// Vectors execute unmodified Editor.py constructor/start/load_state/save_state at
// f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2; canonical blob hashes are in the fixture.
[Trait("Category", "Unit")]
public sealed class NomadNetEditorStateImportTests
{
    [Fact]
    public void T730ExecutedPrimaryVectorsTranslatePersistedFieldsAndReportUnsupportedFields()
    {
        using var fixture = JsonDocument.Parse(typeof(NomadNetEditorStateImportTests).Assembly
            .GetManifestResourceStream("NomadNet.Tests.NomadNetEditorStateImportVectors.json")!);
        Assert.Equal("f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2", fixture.RootElement.GetProperty("revision").GetString());
        var source = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomad-editor-state-import", "unit-source"));
        foreach (var item in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString();
            if (name == "absent") continue; // Absent files are covered by the real profile-import test.
            var bytes = Encoding.UTF8.GetBytes(item.GetProperty("input").GetRawText()
                .Replace("@/", source.Replace('\\', '/') + "/", StringComparison.Ordinal));
            if (name is "truthy" or "malformed" or "wrong-root")
            {
                Assert.Throws<InvalidDataException>(() => NomadNetEditorStateImport.Parse(bytes, source));
                continue;
            }
            var actual = NomadNetEditorStateImport.Parse(bytes, source);
            var observed = item.GetProperty("observed");
            Assert.Equal(observed.GetProperty("filter").GetBoolean(), actual.State.FilterMu);
            Assert.Equal(observed.GetProperty("visible").EnumerateArray().Select(value => value.GetString()), actual.State.VisiblePanes);
            Assert.Equal(observed.GetProperty("expanded").EnumerateArray()
                .Select(value => value.GetString()!["@/storage/".Length..]).Order(StringComparer.Ordinal),
                actual.State.Expanded);
            if (name == "saved")
            {
                Assert.Equal("pages", actual.State.Scope);
                Assert.Equal("nested/notice.mu", actual.State.Path);
                Assert.False(observed.GetProperty("dirty").GetBoolean());
            }
            if (name == "unknown") Assert.Equal(2, actual.Warnings.Count);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("{\"filter_mu\":1}")]
    [InlineData("{\"filter_mu\":\"false\"}")]
    [InlineData("{\"filter_mu\":true,\"filter_mu\":false}")]
    [InlineData("{\"pane_visible\":{\"files\":true,\"files\":false}}")]
    [InlineData("{\"pane_visible\":[]}")]
    [InlineData("{\"expanded\":[null]}")]
    [InlineData("{\"expanded\":\"pages\"}")]
    [InlineData("{\"last_file\":23}")]
    [InlineData("{\"last_file\":\"\"}")]
    public void T731MalformedOrAmbiguousPrimaryStateIsRejectedRatherThanPartiallyApplied(string json) =>
        Assert.Throws<InvalidDataException>(() => NomadNetEditorStateImport.Parse(Encoding.UTF8.GetBytes(json), "primary"));

    [Fact]
    public void T732StateExpansionPathAndJsonDepthBoundsAreExplicit()
    {
        var source = Path.Combine(Path.GetPathRoot(Path.GetFullPath("primary"))!, "p");
        Assert.Throws<InvalidDataException>(() => NomadNetEditorStateImport.Parse(new byte[32769], source));
        var root = Path.Combine(source, "storage", "pages").Replace('\\', '/');
        var paths = Enumerable.Range(0, 256).Select(index => root + "/dir" + index).ToArray();
        Assert.Equal(256, NomadNetEditorStateImport.Parse(JsonSerializer.SerializeToUtf8Bytes(new { expanded = paths }), source).State.Expanded!.Length);
        Assert.Throws<InvalidDataException>(() => NomadNetEditorStateImport.Parse(
            JsonSerializer.SerializeToUtf8Bytes(new { expanded = paths.Append(root).ToArray() }), source));
        Assert.Throws<InvalidDataException>(() => NomadNetEditorStateImport.Parse(
            JsonSerializer.SerializeToUtf8Bytes(new { last_file = root + "/" + new string('x', 513) }), source));
        Assert.Throws<InvalidDataException>(() => NomadNetEditorStateImport.Parse(
            JsonSerializer.SerializeToUtf8Bytes(new { last_file = root + "/../escape" }), source));
        Assert.Throws<InvalidDataException>(() => NomadNetEditorStateImport.Parse(
            Encoding.UTF8.GetBytes("{\"unknown\":" + new string('[', 9) + "0" + new string(']', 9) + "}"), source));
    }

    [Fact]
    public void T733ExternalAndForeignPathsAreNotReboundBySuffixAndUnknownFieldsAreInert()
    {
        var result = NomadNetEditorStateImport.Parse(Encoding.UTF8.GetBytes(
            "{\"last_file\":\"relative/storage/pages/index.mu\",\"expanded\":[\"other/storage/pages\"],\"scroll\":999}"), "primary");
        Assert.Null(result.State.Scope);
        Assert.Empty(result.State.Expanded!);
        Assert.Equal(3, result.Warnings.Count);
        Assert.Equal(["files", "editor", "preview"], result.State.VisiblePanes!);
    }
}

[Trait("Category", "Integration")]
public sealed class NomadNetEditorStateProfileImportTests
{
    [Fact]
    public async Task T734ProfileImportActivatesOriginalEditorAndRestartsWithoutSourceOrUnrelatedStateChanges()
    {
        await using var profile = await Profile.CreateAsync();
        var before = Snapshot(profile.Source);
        var report = await profile.ImportAsync();
        Assert.Equal(before, Snapshot(profile.Source));
        Assert.Contains("editor_state.json", report.ArchivedFiles);
        Assert.DoesNotContain("editor_state.json", report.NotImportedFiles);
        var settings = await NomadNetSettings.OpenAsync(profile.Target);
        await using (var editor = new NomadNetEditor(profile.Target, settings))
        {
            await editor.ActivatePresentationAsync();
            Assert.Equal("pages", editor.Scope);
            Assert.Equal("nested/notice.mu", editor.RelativePath);
            Assert.Equal(">Imported synthetic heading\nsaved body\n", editor.Buffer.Text);
            Assert.True(editor.FilterMu);
            Assert.Equal(["files", "editor"], editor.VisiblePanes);
            Assert.True(Assert.Single(editor.Tree(), row => row.Scope == "pages" && row.Path == "nested").Expanded);
            Assert.False(editor.Buffer.Dirty);
            Assert.Equal(0, editor.SourceScrollPosition);
            editor.Buffer.Set(">Native persisted edit");
            await editor.SaveAsync();
        }
        await using (var restarted = new NomadNetEditor(profile.Target, await NomadNetSettings.OpenAsync(profile.Target)))
        {
            await restarted.ActivatePresentationAsync();
            Assert.Equal(">Native persisted edit", restarted.Buffer.Text);
            Assert.True(restarted.FilterMu);
        }
        Assert.Equal("private-policy\n", await File.ReadAllTextAsync(Path.Combine(profile.Target, "storage", "pages", "nested", "notice.mu.allowed")));
        Assert.Equal("unrelated-user-file", await File.ReadAllTextAsync(Path.Combine(profile.Target, "storage", "files", "owned.txt")));
        Assert.Contains("storage/files/.hidden", report.NotImportedFiles);
        Assert.Contains("storage/files/orphan.allowed", report.NotImportedFiles);
        Assert.Equal("source", (await NomadNetSettings.OpenAsync(profile.Target)).DisplayName);
        Assert.Empty(await (await NomadNetStore.OpenAsync(profile.Target)).MessagesAsync());
        var originalState = await File.ReadAllBytesAsync(Path.Combine(profile.Source, "editor_state.json"));
        Assert.Equal(originalState, await File.ReadAllBytesAsync(Path.Combine(profile.Target, "import-archive", "editor_state.json.bin")));
    }

    [Fact]
    public async Task T735AbsentAndMissingSelectionHaveExplicitUsableDefaults()
    {
        await using var absent = await Profile.CreateAsync();
        File.Delete(Path.Combine(absent.Source, "editor_state.json"));
        var report = await absent.ImportAsync();
        Assert.Contains(report.Warnings, warning => warning.Contains("state absent", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(absent.Target, "editor_state.json")));
        Assert.False(Directory.Exists(Path.Combine(absent.Target, "storage", "pages")));
        await using (var editor = new NomadNetEditor(absent.Target, await NomadNetSettings.OpenAsync(absent.Target)))
        {
            await editor.ActivatePresentationAsync();
            Assert.False(editor.IsOpen);
            Assert.False(editor.FilterMu);
        }
        await using var missing = await Profile.CreateAsync();
        File.Delete(Path.Combine(missing.Source, "storage", "pages", "nested", "notice.mu"));
        report = await missing.ImportAsync();
        Assert.Contains(report.Warnings, warning => warning.Contains("selected file is absent", StringComparison.Ordinal));
        var logs = new List<string>();
        await using var active = new NomadNetEditor(missing.Target, await NomadNetSettings.OpenAsync(missing.Target)) { PresentationLog = logs.Add };
        await active.ActivatePresentationAsync();
        Assert.False(active.IsOpen);
        Assert.True(active.FilterMu);
        Assert.Contains(logs, message => message.Contains("no longer exists", StringComparison.Ordinal));
        await active.OpenAsync("files", "owned.txt");
        Assert.Equal("unrelated-user-file", active.Buffer.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T736ActivationPreservesExistingCurrentEditorAndDirtyBufferOwnership(bool dirty)
    {
        await using var profile = await Profile.CreateAsync();
        await profile.ImportAsync();
        await using var editor = new NomadNetEditor(profile.Target, await NomadNetSettings.OpenAsync(profile.Target));
        await editor.OpenAsync("files", "owned.txt");
        if (dirty) editor.Buffer.Set("PRIVATE UNSAVED DRAFT");
        await editor.ActivatePresentationAsync();
        Assert.Equal("files", editor.Scope);
        Assert.Equal("owned.txt", editor.RelativePath);
        Assert.Equal(dirty ? "PRIVATE UNSAVED DRAFT" : "unrelated-user-file", editor.Buffer.Text);
        Assert.Equal(dirty, editor.Buffer.Dirty);
        Assert.True(editor.FilterMu);
        Assert.DoesNotContain("PRIVATE UNSAVED DRAFT", await File.ReadAllTextAsync(Path.Combine(profile.Target, "editor_state.json")));
    }

    [Fact]
    public async Task T737MalformedAndUnsafeSelectionRollBackTheWholeProfileWithoutOverwritingNativeState()
    {
        foreach (var json in new[] { "{", "{\"expanded\":[1]}", "{\"pane_visible\":{\"files\":null}}",
            "{\"last_file\":\"@/storage/pages/.hidden\"}", "{\"last_file\":\"@/storage/pages/CON\"}" })
        {
            await using var profile = await Profile.CreateAsync();
            await File.WriteAllTextAsync(Path.Combine(profile.Source, "editor_state.json"),
                json.Replace("@/", profile.Source.Replace('\\', '/') + "/", StringComparison.Ordinal));
            var before = Snapshot(profile.Source);
            await Assert.ThrowsAnyAsync<Exception>(() => profile.ImportAsync());
            Assert.Equal(before, Snapshot(profile.Source));
            Assert.False(Directory.Exists(profile.Target));
            Assert.Empty(Directory.GetDirectories(profile.Root, "native.profile-import-*"));
        }
        await using var existing = await Profile.CreateAsync();
        Directory.CreateDirectory(existing.Target);
        await File.WriteAllTextAsync(Path.Combine(existing.Target, "editor_state.json"), "NATIVE OWNED STATE");
        await Assert.ThrowsAsync<IOException>(() => existing.ImportAsync());
        Assert.Equal("NATIVE OWNED STATE", await File.ReadAllTextAsync(Path.Combine(existing.Target, "editor_state.json")));
    }

    [Fact]
    public async Task T738StateDocumentAndAggregateBoundsFailAtomicallyAndExactDocumentBoundLoads()
    {
        await using var state = await Profile.CreateAsync();
        await File.WriteAllBytesAsync(Path.Combine(state.Source, "editor_state.json"), new byte[32769]);
        await Assert.ThrowsAsync<InvalidDataException>(() => state.ImportAsync());
        Assert.False(Directory.Exists(state.Target));
        await using var document = await Profile.CreateAsync();
        var selected = Path.Combine(document.Source, "storage", "pages", "nested", "notice.mu");
        await File.WriteAllTextAsync(selected, new string('x', NomadNetEditor.MaximumBytes + 1));
        await Assert.ThrowsAsync<InvalidDataException>(() => document.ImportAsync());
        Assert.False(Directory.Exists(document.Target));
        await File.WriteAllTextAsync(selected, new string('x', NomadNetEditor.MaximumBytes));
        await document.ImportAsync();
        await using (var editor = new NomadNetEditor(document.Target, await NomadNetSettings.OpenAsync(document.Target)))
        {
            await editor.ActivatePresentationAsync();
            Assert.Equal(NomadNetEditor.MaximumBytes, editor.Buffer.Text.Length);
        }
        await using var aggregate = await Profile.CreateAsync();
        for (var index = 0; index < 17; index++)
            await File.WriteAllBytesAsync(Path.Combine(aggregate.Source, "storage", "files", $"file{index}"), new byte[NomadNetEditor.MaximumBytes]);
        await Assert.ThrowsAsync<InvalidDataException>(() => aggregate.ImportAsync());
        Assert.False(Directory.Exists(aggregate.Target));
        Assert.Empty(Directory.GetDirectories(aggregate.Root, "native.profile-import-*"));
    }

    [Fact]
    public async Task T739CancellationReadFailureAndCompetingImportPreserveAtomicityAndRecovery()
    {
        await using var profile = await Profile.CreateAsync();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => profile.ImportAsync(cancelled.Token));
        Assert.False(Directory.Exists(profile.Target));
        using (var locked = new FileStream(Path.Combine(profile.Source, "storage", "pages", "nested", "notice.mu"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => profile.ImportAsync());
        Assert.False(Directory.Exists(profile.Target));
        var tasks = Enumerable.Range(0, 2).Select(async _ =>
        {
            try { await profile.ImportAsync(); return true; }
            catch (IOException) { return false; }
        });
        Assert.Single(await Task.WhenAll(tasks), value => value);
        Assert.Empty(Directory.GetDirectories(profile.Root, "native.profile-import-*"));
        await using var editor = new NomadNetEditor(profile.Target, await NomadNetSettings.OpenAsync(profile.Target));
        await editor.ActivatePresentationAsync();
        Assert.True(editor.IsOpen);
    }

    [Fact]
    public async Task T740OrphanExternalAndMissingExpansionDispositionIsExplicit()
    {
        await using var profile = await Profile.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(profile.Source, "editor_state.json"), JsonSerializer.Serialize(new
        {
            filter_mu = true, expanded = new[] { Path.Combine(profile.Source, "storage", "pages", "absent") },
            last_file = Path.Combine(profile.Root, "outside", "storage", "pages", "notice.mu"),
            pane_visible = new { files = false, editor = false, preview = false }, scroll = 800
        }));
        var report = await profile.ImportAsync();
        Assert.Contains(report.Warnings, warning => warning.Contains("outside this profile", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, warning => warning.Contains("inert expansion", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, warning => warning.Contains("scroll", StringComparison.Ordinal));
        await using var editor = new NomadNetEditor(profile.Target, await NomadNetSettings.OpenAsync(profile.Target));
        await editor.ActivatePresentationAsync();
        Assert.False(editor.IsOpen);
        Assert.Equal(["editor"], editor.VisiblePanes);
        Assert.True(editor.FilterMu);
    }

    [Fact]
    public async Task T748ExactStateLimitAndDocumentCountOverflowAreBoundedWithoutPartialTargets()
    {
        await using var exact = await Profile.CreateAsync();
        var path = Path.Combine(exact.Source, "editor_state.json");
        var json = await File.ReadAllTextAsync(path);
        var padded = json + new string(' ', NomadNetEditorStateImport.MaximumStateBytes - Encoding.UTF8.GetByteCount(json));
        await File.WriteAllTextAsync(path, padded, new UTF8Encoding(false));
        await exact.ImportAsync();
        Assert.Equal(32768, new FileInfo(Path.Combine(exact.Target, "import-archive", "editor_state.json.bin")).Length);
        await using var tooMany = await Profile.CreateAsync();
        for (var index = 0; index < NomadNetEditorStateImport.MaximumDocuments; index++)
            await File.WriteAllBytesAsync(Path.Combine(tooMany.Source, "storage", "files", $"document-{index}"), []);
        await Assert.ThrowsAsync<InvalidDataException>(() => tooMany.ImportAsync());
        Assert.False(Directory.Exists(tooMany.Target));
        Assert.Empty(Directory.GetDirectories(tooMany.Root, "native.profile-import-*"));
    }

    private static string[] Snapshot(string source) => Directory.GetFiles(source, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(source, path) + ":" +
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    private sealed class Profile : IAsyncDisposable
    {
        public string Root { get; } = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomad-editor-state-import", "managed-" + Guid.NewGuid().ToString("N")));
        public string Source => Path.Combine(Root, "primary");
        public string Target => Path.Combine(Root, "native");
        public string Rns => Path.Combine(Root, "rns");
        public static async Task<Profile> CreateAsync()
        {
            var profile = new Profile();
            var pages = Path.Combine(profile.Source, "storage", "pages", "nested");
            var files = Path.Combine(profile.Source, "storage", "files");
            Directory.CreateDirectory(pages);
            Directory.CreateDirectory(files);
            Directory.CreateDirectory(profile.Rns);
            using var identity = Identity.Generate();
            var secret = identity.ExportPrivateKey();
            try { await File.WriteAllBytesAsync(Path.Combine(profile.Source, "storage", "identity"), secret); }
            finally { CryptographicOperations.ZeroMemory(secret); }
            await File.WriteAllBytesAsync(Path.Combine(profile.Source, "storage", "peersettings"),
                MsgPack.Pack(new Dictionary<object, object?> { ["display_name"] = "source" }));
            await File.WriteAllTextAsync(Path.Combine(profile.Source, "config"), "[client]\ndisplay_name=source\n[node]\nenable_node=no\n");
            await File.WriteAllTextAsync(Path.Combine(pages, "notice.mu"), ">Imported synthetic heading\nsaved body\n");
            await File.WriteAllTextAsync(Path.Combine(pages, "notice.mu.allowed"), "private-policy\n");
            await File.WriteAllTextAsync(Path.Combine(files, "owned.txt"), "unrelated-user-file");
            await File.WriteAllTextAsync(Path.Combine(files, ".hidden"), "not native editor content");
            await File.WriteAllTextAsync(Path.Combine(files, "orphan.allowed"), "not loosened");
            await File.WriteAllTextAsync(Path.Combine(profile.Source, "editor_state.json"), JsonSerializer.Serialize(new
            {
                filter_mu = true, expanded = new[] { Path.GetDirectoryName(pages)!, pages },
                pane_visible = new { files = true, editor = true, preview = false }, last_file = Path.Combine(pages, "notice.mu")
            }));
            return profile;
        }
        public Task<NomadNetProfileImportReport> ImportAsync(CancellationToken token = default) =>
            NomadNetProfileImport.ImportAsync(Source, Target, Rns, token);
        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
            return ValueTask.CompletedTask;
        }
    }
}
