using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NomadNet.Tests;

// GenerateNomadNetFolderVectors.py executes primary f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2
// Editor.py SHA256 ae6c09cde38d473fd97d1758ebcbc13edaf5d7e1f97dc4e698a870c68cd8d378.
[Trait("Category", "Integration")]
public sealed class NomadNetFolderTests
{
    [Fact]
    [Trait("TestId", "T420")]
    public async Task ExecutedPrimaryTreesAndSelectionMatchBoundedCreateRenameMoveRemove()
    {
        using var work = new Workspace();
        using var fixture = JsonDocument.Parse(typeof(NomadNetFolderTests).Assembly
            .GetManifestResourceStream("NomadNet.Tests.NomadNetFolderVectors.json")!);
        var root = fixture.RootElement;
        Assert.Equal("f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2", root.GetProperty("revision").GetString());
        Assert.Equal("ae6c09cde38d473fd97d1758ebcbc13edaf5d7e1f97dc4e698a870c68cd8d378", root.GetProperty("sourceSha256").GetString());
        await work.Editor.CreateFolderAsync("pages", "drafts/chapter");
        await work.Editor.CreateFolderAsync("pages", "drafts/empty");
        await work.Editor.CreateFolderAsync("pages", "drafts/chapter");
        await work.Editor.CreateFolderAsync("files");
        foreach (var file in root.GetProperty("initialFiles").EnumerateObject())
            await File.WriteAllBytesAsync(System.IO.Path.Combine(work.Root, file.Name.Replace('/', System.IO.Path.DirectorySeparatorChar)),
                Convert.FromBase64String(file.Value.GetString()!));
        MatchTree(root.GetProperty("initial"));
        await work.Editor.OpenAsync("pages", "drafts/chapter/notice.mu");
        var plan = await work.Editor.InspectFolderAsync("pages", "drafts");
        Assert.Equal(3, plan.Directories);
        Assert.Equal(3, plan.Files);
        await work.Editor.MoveFolderAsync(plan.Token, "pages", "renamed");
        Assert.Equal("renamed" + System.IO.Path.DirectorySeparatorChar + "chapter" + System.IO.Path.DirectorySeparatorChar + "notice.mu", work.Editor.RelativePath);
        MatchTree(root.GetProperty("renamed").GetProperty("tree"));
        Assert.True(root.GetProperty("renamed").GetProperty("dirty").GetBoolean()); // Primary preserves dirty moves; native deliberately refuses them.
        Assert.False(work.Editor.Buffer.Dirty);
        await work.Editor.CreateFolderAsync("files", "archive");
        plan = await work.Editor.InspectFolderAsync("pages", "renamed");
        await work.Editor.MoveFolderAsync(plan.Token, "files", "archive/moved");
        Assert.Equal("files", work.Editor.Scope);
        MatchTree(root.GetProperty("moved").GetProperty("tree"));
        using (var resumed = work.AnotherEditor())
        {
            await resumed.ResumeAsync();
            Assert.Equal(work.Editor.RelativePath, resumed.RelativePath);
            Assert.Equal("files", resumed.Scope);
        }
        await work.Editor.CreateFolderAsync("files", "occupied");
        plan = await work.Editor.InspectFolderAsync("files", "archive/moved");
        await Assert.ThrowsAsync<IOException>(() => work.Editor.MoveFolderAsync(plan.Token, "files", "occupied"));
        Assert.Equal(["overwrite occupied"], root.GetProperty("overwrite").GetProperty("prompts").EnumerateArray().Select(value => value.GetString()));
        plan = await work.Editor.InspectFolderAsync("files", "archive/moved");
        await work.Editor.RemoveFolderAsync(plan.Token, confirmed: true);
        Assert.False(work.Editor.IsOpen);
        Assert.False(work.Editor.Buffer.Dirty);
        MatchTree(root.GetProperty("deleted").GetProperty("tree"));
        Assert.Empty(Directory.GetDirectories(work.Path("files", "archive"), ".editor-folder-delete-*"));

        void MatchTree(JsonElement expected)
        {
            var actual = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var scope in new[] { "pages", "files" })
            {
                var directory = work.Path(scope);
                actual[scope] = "directory";
                foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
                    actual[scope + "/" + System.IO.Path.GetRelativePath(directory, path).Replace('\\', '/')] =
                        Directory.Exists(path) ? "directory" : Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
            }
            Assert.Equal(expected.EnumerateArray().Select(item => item.GetProperty("path").GetString()).Order(), actual.Keys);
            foreach (var item in expected.EnumerateArray())
                Assert.Equal(item.GetProperty("directory").GetBoolean() ? "directory" : item.GetProperty("sha256").GetString(),
                    actual[item.GetProperty("path").GetString()!]);
        }
    }

    [Fact]
    [Trait("TestId", "T421")]
    public async Task CreationIsConfinedIdempotentNonoverwritingAndCancellationSafe()
    {
        using var work = new Workspace();
        await work.Editor.CreateFolderAsync("pages", "new/nested");
        await work.Editor.CreateFolderAsync("pages", "new/nested");
        await File.WriteAllTextAsync(work.Path("pages", "occupied"), "untouched");
        await Assert.ThrowsAsync<IOException>(() => work.Editor.CreateFolderAsync("pages", "occupied/child"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.Editor.CreateFolderAsync("files", "cancelled/path", cancelled.Token));
        Assert.False(Directory.Exists(work.Path("files", "cancelled")));
        foreach (var relative in new[] { "../escape", ".", ".hidden", "device:", "CON", "a.allowed" })
            await Assert.ThrowsAnyAsync<Exception>(() => work.Editor.CreateFolderAsync("pages", relative));
        await Assert.ThrowsAsync<ArgumentException>(() => work.Editor.CreateFolderAsync("config", "arbitrary"));
        Assert.Equal("untouched", await File.ReadAllTextAsync(work.Path("pages", "occupied")));
    }

    [Fact]
    [Trait("TestId", "T422")]
    public async Task MoveRebasesClosedSelectionPreservesBinaryPermissionsAndRequiresCleanAffectedBuffer()
    {
        using var work = new Workspace();
        await work.SeedAsync();
        await work.Editor.OpenAsync("pages", "source/notice");
        work.Editor.Buffer.Set("dirty");
        var plan = await work.Editor.InspectFolderAsync("pages", "source");
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.Editor.MoveFolderAsync(plan.Token, "pages", "renamed"));
        Assert.Equal("dirty", work.Editor.Buffer.Text);
        await work.Editor.CloseAsync("discard");
        var longParent = string.Join('/', Enumerable.Repeat(new string('n', 100), 5));
        await work.Editor.CreateFolderAsync("files", longParent);
        plan = await work.Editor.InspectFolderAsync("pages", "source");
        await Assert.ThrowsAsync<ArgumentException>(() => work.Editor.MoveFolderAsync(plan.Token, "files", longParent + "/target"));
        Assert.True(Directory.Exists(work.Path("pages", "source")));
        plan = await work.Editor.InspectFolderAsync("pages", "source");
        await work.Editor.MoveFolderAsync(plan.Token, "files", "moved");
        using var resumed = work.AnotherEditor();
        await resumed.ResumeAsync();
        Assert.Equal("files", resumed.Scope);
        Assert.Equal(System.IO.Path.Combine("moved", "notice"), resumed.RelativePath);
        Assert.Equal("notice\n", resumed.Buffer.Text);
        Assert.Equal(new string('a', 32) + "\n", await File.ReadAllTextAsync(work.Path("files", "moved/notice.allowed")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.Editor.MoveFolderAsync(plan.Token, "pages", "again"));
    }

    [Fact]
    [Trait("TestId", "T423")]
    public async Task RemovalRequiresExactTokenConfirmationAndClearsPersistedDescendantOnly()
    {
        using var work = new Workspace();
        await work.SeedAsync();
        await work.Editor.OpenAsync("pages", "source/notice");
        await work.Editor.CloseAsync("discard");
        var plan = await work.Editor.InspectFolderAsync("pages", "source");
        await Assert.ThrowsAsync<ArgumentException>(() => work.Editor.RemoveFolderAsync(plan.Token, false));
        Assert.Equal(plan, work.Editor.PendingFolder);
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.Editor.RemoveFolderAsync("wrong", true));
        Assert.True(Directory.Exists(work.Path("pages", "source")));
        await work.Editor.RemoveFolderAsync(plan.Token, true);
        using var resumed = work.AnotherEditor();
        await Assert.ThrowsAsync<InvalidDataException>(() => resumed.ResumeAsync());
        Assert.False(Directory.Exists(work.Path("pages", "source")));
        await work.Editor.CreateFolderAsync("pages", "empty");
        plan = await work.Editor.InspectFolderAsync("pages", "empty");
        await work.Editor.RemoveFolderAsync(plan.Token, true);
        Assert.False(Directory.Exists(work.Path("pages", "empty")));
    }

    [Fact]
    [Trait("TestId", "T424")]
    public async Task ConflictsAndDestinationFailuresPreserveFilesAndService()
    {
        using var work = new Workspace();
        await work.SeedAsync();
        var plan = await work.Editor.InspectFolderAsync("pages", "source");
        await File.WriteAllTextAsync(work.Path("pages", "source/notice"), "external wins\n");
        await Assert.ThrowsAsync<IOException>(() => work.Editor.RemoveFolderAsync(plan.Token, true));
        Assert.Equal("external wins\n", await File.ReadAllTextAsync(work.Path("pages", "source/notice")));
        Assert.Null(work.Editor.PendingFolder);
        plan = await work.Editor.InspectFolderAsync("pages", "source");
        await work.Editor.CreateFolderAsync("files", "occupied");
        await Assert.ThrowsAsync<IOException>(() => work.Editor.MoveFolderAsync(plan.Token, "files", "occupied"));
        plan = await work.Editor.InspectFolderAsync("pages", "source");
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => work.Editor.MoveFolderAsync(plan.Token, "files", "missing/target"));
        plan = await work.Editor.InspectFolderAsync("pages", "source");
        await Assert.ThrowsAsync<IOException>(() => work.Editor.MoveFolderAsync(plan.Token, "pages", "source/nested"));
        plan = await work.Editor.InspectFolderAsync("pages", "source");
        work.Editor.CancelFolderPlan();
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.Editor.RemoveFolderAsync(plan.Token, true));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        plan = await work.Editor.InspectFolderAsync("pages", "source");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.Editor.RemoveFolderAsync(plan.Token, true, cancelled.Token));
        await work.Editor.MoveFolderAsync(plan.Token, "files", "recovered");
        Assert.Equal("external wins\n", await File.ReadAllTextAsync(work.Path("files", "recovered/notice")));
    }

    [Fact]
    [Trait("TestId", "T424")]
    public async Task ConflictsConcurrentEditorsCannotMoveTheSameTreeTwice()
    {
        using var work = new Workspace();
        await work.SeedAsync();
        using var other = work.AnotherEditor();
        var first = await work.Editor.InspectFolderAsync("pages", "source");
        var second = await other.InspectFolderAsync("pages", "source");
        var results = await Task.WhenAll(TryMoveAsync(work.Editor, first.Token, "one"), TryMoveAsync(other, second.Token, "two"));
        Assert.Equal(1, results.Count(success => success));
        Assert.False(Directory.Exists(work.Path("pages", "source")));
        var destination = results[0] ? "one" : "two";
        Assert.Equal("notice\n", await File.ReadAllTextAsync(work.Path("files", destination + "/notice")));
        Assert.False(Directory.Exists(work.Path("files", results[0] ? "two" : "one")));

        static async Task<bool> TryMoveAsync(NomadNetEditor editor, string token, string target)
        {
            try { await editor.MoveFolderAsync(token, "files", target); return true; }
            catch (IOException) { return false; }
        }
    }

    [Fact]
    [Trait("TestId", "T425")]
    public async Task BoundedTraversalRefusesOversizeDeepWideAndHiddenTrees()
    {
        using var work = new Workspace();
        await work.Editor.CreateFolderAsync("pages", "wide");
        for (var index = 0; index < NomadNetEditor.MaximumFolderEntries; index++)
            await File.WriteAllTextAsync(work.Path("pages", "wide/" + index), "");
        await Assert.ThrowsAsync<InvalidDataException>(() => work.Editor.InspectFolderAsync("pages", "wide"));
        await work.Editor.CreateFolderAsync("pages", "large");
        await File.WriteAllBytesAsync(work.Path("pages", "large/payload"), new byte[NomadNetEditor.MaximumBytes + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => work.Editor.InspectFolderAsync("pages", "large"));
        await work.Editor.CreateFolderAsync("pages", "deep");
        Directory.CreateDirectory(work.Path("pages", "deep/" + string.Join('/', Enumerable.Repeat("x", NomadNetEditor.MaximumFolderDepth + 1))));
        await Assert.ThrowsAsync<InvalidDataException>(() => work.Editor.InspectFolderAsync("pages", "deep"));
        await work.Editor.CreateFolderAsync("pages", "hidden");
        await File.WriteAllTextAsync(work.Path("pages", "hidden/.private"), "not implicitly moved/deleted");
        await Assert.ThrowsAsync<IOException>(() => work.Editor.InspectFolderAsync("pages", "hidden"));
        Assert.Null(work.Editor.PendingFolder);
        await work.Editor.CreateFolderAsync("pages", "healthy");
        var healthy = await work.Editor.InspectFolderAsync("pages", "healthy");
        await work.Editor.RemoveFolderAsync(healthy.Token, true);
    }

    [Fact]
    [Trait("TestId", "T425")]
    public async Task BoundedAggregateContentCannotBypassTheTreeBudget()
    {
        using var work = new Workspace();
        await work.Editor.CreateFolderAsync("pages", "aggregate");
        var bytes = new byte[NomadNetEditor.MaximumBytes];
        for (var index = 0; index < 17; index++)
            await File.WriteAllBytesAsync(work.Path("pages", "aggregate/" + index), bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => work.Editor.InspectFolderAsync("pages", "aggregate"));
        Assert.Null(work.Editor.PendingFolder);
        Assert.Equal(17, Directory.GetFiles(work.Path("pages", "aggregate")).Length);
    }

    [Fact]
    [Trait("TestId", "T426")]
    public async Task SafeguardsRefuseReparseRootCompanionsAndNestedConfiguredRoots()
    {
        using var work = new Workspace();
        await work.SeedAsync();
        File.CreateSymbolicLink(work.Path("pages", "source/link"), work.Path("pages", "source/notice"));
        await Assert.ThrowsAsync<IOException>(() => work.Editor.InspectFolderAsync("pages", "source"));
        File.Delete(work.Path("pages", "source/link"));
        await File.WriteAllTextAsync(work.Path("pages", "source.allowed"), "orphan folder companion");
        await Assert.ThrowsAsync<IOException>(() => work.Editor.InspectFolderAsync("pages", "source"));
        File.Delete(work.Path("pages", "source.allowed"));
        using var nested = new NomadNetEditor(work.Root, new NomadNetSettings
            { PagesPath = work.Path("pages"), FilesPath = work.Path("pages", "source/other-root") });
        await nested.CreateFolderAsync("files");
        await Assert.ThrowsAsync<IOException>(() => nested.InspectFolderAsync("pages", "source"));
        foreach (var path in new[] { ".", "../", "" })
            await Assert.ThrowsAnyAsync<Exception>(() => work.Editor.InspectFolderAsync("pages", path));
    }

    [WindowsFolderFact]
    [Trait("TestId", "T426")]
    public async Task SafeguardsWindowsLockRollbackAndCommittedCleanupFailureAreExplicitAndRecover()
    {
        using var work = new Workspace();
        await work.SeedAsync();
        var plan = await work.Editor.InspectFolderAsync("pages", "source");
        using (var locked = new FileStream(work.Path("pages", "source/notice"), FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAnyAsync<IOException>(() => work.Editor.RemoveFolderAsync(plan.Token, true));
        Assert.Equal("notice\n", await File.ReadAllTextAsync(work.Path("pages", "source/notice")));
        Assert.Empty(Directory.GetDirectories(work.Path("pages"), ".editor-folder-delete-*"));
        plan = await work.Editor.InspectFolderAsync("pages", "source");
        await work.Editor.RemoveFolderAsync(plan.Token, true);
        Assert.False(Directory.Exists(work.Path("pages", "source")));
        await work.SeedAsync();
        await work.Editor.OpenAsync("pages", "source/notice");
        File.SetAttributes(work.Path("pages", "source/notice.allowed"), FileAttributes.ReadOnly);
        plan = await work.Editor.InspectFolderAsync("pages", "source");
        string? staged = null;
        try
        {
            var failure = await Assert.ThrowsAsync<IOException>(() => work.Editor.RemoveFolderAsync(plan.Token, true));
            Assert.Contains("removal committed; cleanup incomplete", failure.Message, StringComparison.Ordinal);
            Assert.False(work.Editor.IsOpen);
            Assert.False(Directory.Exists(work.Path("pages", "source")));
            staged = Assert.Single(Directory.GetDirectories(work.Path("pages"), ".editor-folder-delete-*"));
            Assert.True(File.Exists(System.IO.Path.Combine(staged, "notice.allowed")));
            await work.Editor.CreateFolderAsync("pages", "healthy");
            var healthy = await work.Editor.InspectFolderAsync("pages", "healthy");
            await work.Editor.RemoveFolderAsync(healthy.Token, true);
        }
        finally
        {
            var readonlyFile = staged is null ? work.Path("pages", "source/notice.allowed") : System.IO.Path.Combine(staged, "notice.allowed");
            if (File.Exists(readonlyFile)) File.SetAttributes(readonlyFile, FileAttributes.Normal);
        }
    }

    private sealed class WindowsFolderFactAttribute : FactAttribute
    {
        public WindowsFolderFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Requires actual Windows deny-delete sharing; Linux behavior is not this case.";
        }
    }

    private sealed class Workspace : IDisposable
    {
        public string Root { get; } = System.IO.Path.GetFullPath(System.IO.Path.Combine("artifacts", "tests", "nomad-folders", Guid.NewGuid().ToString("N")));
        public NomadNetEditor Editor { get; }
        public Workspace() { Directory.CreateDirectory(Root); Editor = AnotherEditor(); }
        public NomadNetEditor AnotherEditor() => new(Root, new NomadNetSettings { PagesPath = Path("pages"), FilesPath = Path("files") });
        public string Path(string scope, string relative = "") => System.IO.Path.Combine(Root, scope, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        public async Task SeedAsync()
        {
            await Editor.CreateFolderAsync("pages", "source/sub");
            await Editor.CreateFolderAsync("files");
            await File.WriteAllTextAsync(Path("pages", "source/notice"), "notice\n", new UTF8Encoding(false));
            await File.WriteAllTextAsync(Path("pages", "source/notice.allowed"), new string('a', 32) + "\n", new UTF8Encoding(false));
        }
        public void Dispose() { Editor.Dispose(); Directory.Delete(Root, true); }
    }
}
