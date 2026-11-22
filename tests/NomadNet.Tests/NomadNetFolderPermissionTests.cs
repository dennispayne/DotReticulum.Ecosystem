using System.Text;
using System.Text.Json;

namespace NomadNet.Tests;

// GenerateNomadNetFolderPermissionVectors.py executes primary f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2.
// Editor.py SHA256 ae6c09cde38d473fd97d1758ebcbc13edaf5d7e1f97dc4e698a870c68cd8d378.
[Trait("Category", "Integration")]
public sealed class NomadNetFolderPermissionTests
{
    private static readonly string A = new('a', 32);
    private static readonly string B = new('b', 32);

    [Fact, Trait("TestId", "T430")]
    public async Task ExecutedPrimaryMixedUniformAndConfirmedPublicMatchWithoutTouchingOrphansOrContent()
    {
        using var work = new Workspace();
        using var fixture = JsonDocument.Parse(typeof(NomadNetFolderPermissionTests).Assembly
            .GetManifestResourceStream("NomadNet.Tests.NomadNetFolderPermissionVectors.json")!);
        var root = fixture.RootElement;
        Assert.Equal("f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2", root.GetProperty("revision").GetString());
        Assert.Equal("ae6c09cde38d473fd97d1758ebcbc13edaf5d7e1f97dc4e698a870c68cd8d378", root.GetProperty("sourceSha256").GetString());
        foreach (var file in root.GetProperty("initial").EnumerateObject())
        {
            var path = work.Path(file.Name);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(file.Value.GetString()!));
        }
        var plan = await work.Inspect();
        MatchSummary("mixed", plan);
        await work.Editor.ApplyFolderPermissionsAsync(plan.Token, [A, B], true);
        MatchTree("restricted");
        plan = await work.Inspect();
        MatchSummary("uniform", plan);
        Assert.True(root.GetProperty("makePublic").GetProperty("treeUnchanged").GetBoolean());
        Assert.True(root.GetProperty("makePublic").GetProperty("cancelUnchanged").GetBoolean());
        await Assert.ThrowsAsync<ArgumentException>(() => work.Editor.ApplyFolderPermissionsAsync(plan.Token, null, false));
        MatchTree("restricted");
        await work.Editor.ApplyFolderPermissionsAsync(plan.Token, null, true);
        MatchTree("public");
        MatchSummary("cleared", await work.Inspect());

        void MatchSummary(string name, NomadNetFolderPermissionSummary summary)
        {
            var expected = root.GetProperty(name);
            Assert.Equal(expected.GetProperty("total").GetInt32(), summary.Total);
            Assert.Equal(expected.GetProperty("restricted").GetInt32(), summary.Restricted);
            Assert.Equal(expected.GetProperty("common").GetString(), summary.Common);
        }
        void MatchTree(string name)
        {
            var expected = root.GetProperty(name);
            Assert.Equal(expected.EnumerateObject().Select(item => item.Name).Order(),
                Directory.GetFiles(work.Tree, "*", SearchOption.AllDirectories)
                    .Select(path => System.IO.Path.GetRelativePath(work.Tree, path).Replace('\\', '/')).Order());
            foreach (var file in expected.EnumerateObject())
            {
                var bytes = Convert.FromBase64String(file.Value.GetString()!);
                var actual = File.ReadAllBytes(work.Path(file.Name));
                if (file.Name.EndsWith(".allowed")) Assert.Equal(Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n"), Encoding.UTF8.GetString(actual).Replace("\r\n", "\n"));
                else Assert.Equal(bytes, actual);
            }
        }
    }

    [Fact, Trait("TestId", "T431")]
    public async Task SummariesPreserveOpaqueOrderDuplicatesEmptyAndSkippedCachePoliciesWithoutExecuting()
    {
        using var work = new Workspace();
        Assert.Equal(0, (await work.Inspect()).Total);
        await work.Seed();
        await File.WriteAllTextAsync(work.Path("a.allowed"), $" \n{A}\n{A} \n\n");
        await File.WriteAllTextAsync(work.Path("b.allowed"), A + "\n" + A + "\n");
        Directory.CreateDirectory(work.Path("__pycache__"));
        await File.WriteAllTextAsync(work.Path("__pycache__/opaque"), "not targeted");
        await File.WriteAllTextAsync(work.Path("__pycache__/opaque.allowed"), "unvalidated policy");
        var plan = await work.Inspect();
        Assert.Equal(A + "\n" + A, plan.Common);
        Assert.Equal(2, plan.Restricted);
        Assert.Equal(2, plan.Total);
        await work.Editor.ApplyFolderPermissionsAsync(plan.Token, [B, B], true);
        Assert.Equal(B + "\n", await File.ReadAllTextAsync(work.Path("a.allowed")));
        Assert.Equal("unvalidated policy", await File.ReadAllTextAsync(work.Path("__pycache__/opaque.allowed")));
        Assert.Equal(B, (await work.Inspect()).Common);
    }

    [Fact, Trait("TestId", "T431")]
    public async Task SummariesRejectInvalidUtf8RatherThanPretendPublicAndRecover()
    {
        using var work = new Workspace();
        await work.Seed();
        await File.WriteAllBytesAsync(work.Path("a.allowed"), [255, 254]);
        await Assert.ThrowsAsync<DecoderFallbackException>(() => work.Inspect());
        Assert.Equal(new byte[] { 255, 254 }, await File.ReadAllBytesAsync(work.Path("a.allowed")));
        File.Delete(work.Path("a.allowed"));
        Assert.Equal(2, (await work.Inspect()).Total);
    }

    [Fact, Trait("TestId", "T432")]
    public async Task ConfirmationTokensCancelInvalidInputAndRestartCannotExpandAccess()
    {
        using var work = new Workspace();
        await work.Seed();
        var plan = await work.Inspect();
        await Assert.ThrowsAsync<ArgumentException>(() => work.Editor.ApplyFolderPermissionsAsync(plan.Token, [A, B], false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.Editor.ApplyFolderPermissionsAsync("wrong", null, true));
        using (var restarted = work.Another())
            await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ApplyFolderPermissionsAsync(plan.Token, null, true));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.Editor.ApplyFolderPermissionsAsync(plan.Token, [B], true, cancelled.Token));
        Assert.Equal(A + "\n", await File.ReadAllTextAsync(work.Path("a.allowed")));
        work.Editor.CancelFolderPlan();
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.Editor.ApplyFolderPermissionsAsync(plan.Token, null, true));
        var management = await work.Editor.InspectFolderAsync("pages", "tree");
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.Editor.ApplyFolderPermissionsAsync(management.Token, null, true));
        plan = await work.Inspect();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => work.Editor.ApplyFolderPermissionsAsync(plan.Token, ["not-a-hash"], true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.Editor.ApplyFolderPermissionsAsync(plan.Token, [B], true));
        plan = await work.Inspect();
        await work.Editor.ApplyFolderPermissionsAsync(plan.Token, [A, B], true);
        Assert.Equal(A + "\n" + B + "\n", await File.ReadAllTextAsync(work.Path("a.allowed")));
        Assert.Empty(Directory.GetDirectories(work.Root, ".editor-permissions-*"));
    }

    [Fact, Trait("TestId", "T433")]
    public async Task ExternalContentPolicyAndTopologyChangesRefuseStalePlansAndConcurrentEditorsSerialize()
    {
        using var work = new Workspace();
        await work.Seed();
        foreach (var name in new[] { "a", "a.allowed", "new" })
        {
            var plan = await work.Inspect();
            await File.WriteAllTextAsync(work.Path(name), "external writer retained");
            await Assert.ThrowsAsync<IOException>(() => work.Editor.ApplyFolderPermissionsAsync(plan.Token, [B], true));
            Assert.Equal("external writer retained", await File.ReadAllTextAsync(work.Path(name)));
        }
        using var another = work.Another();
        var first = await work.Inspect();
        var second = await another.InspectFolderPermissionsAsync("pages", "tree");
        var attempts = await Task.WhenAll(Attempt(work.Editor, first.Token), Attempt(another, second.Token));
        Assert.Single(attempts, value => value);
        Assert.Equal(B, (await work.Inspect()).Common);

        static async Task<bool> Attempt(NomadNetEditor editor, string token)
        {
            try { await editor.ApplyFolderPermissionsAsync(token, [B], true); return true; }
            catch (IOException) { return false; }
        }
    }

    [Fact, Trait("TestId", "T432")]
    public async Task ConfirmationCancellationDuringPrivateStagingLeavesOriginalPoliciesAndContinuesService()
    {
        using var work = new Workspace();
        for (var index = 0; index < 200; index++)
        {
            await File.WriteAllTextAsync(work.Path("item" + index), "synthetic");
            await File.WriteAllTextAsync(work.Path("item" + index + ".allowed"), A + "\n");
        }
        var plan = await work.Inspect();
        using var cancelled = new CancellationTokenSource();
        using var watcher = new FileSystemWatcher(work.Root, ".editor-permissions-*") { NotifyFilter = NotifyFilters.DirectoryName };
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Created += (_, _) => { cancelled.Cancel(); observed.TrySetResult(); };
        watcher.EnableRaisingEvents = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.Editor.ApplyFolderPermissionsAsync(plan.Token, [B], true, cancelled.Token));
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        foreach (var path in Directory.GetFiles(work.Tree, "*.allowed"))
            Assert.Equal(A + "\n", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetDirectories(work.Root, ".editor-permissions-*"));
        Assert.Equal(A, (await work.Inspect()).Common);
    }

    [Fact, Trait("TestId", "T434")]
    public async Task BoundsIncludeResultingCompanionsAggregateBytesAndOriginalTraversal()
    {
        using var work = new Workspace();
        for (var index = 0; index < 512; index++) await File.WriteAllBytesAsync(work.Path("item" + index), []);
        var plan = await work.Inspect();
        await Assert.ThrowsAsync<InvalidDataException>(() => work.Editor.ApplyFolderPermissionsAsync(plan.Token, [A], true));
        File.Delete(work.Path("item511"));
        File.Delete(work.Path("item510"));
        for (var index = 500; index < 510; index++) File.Delete(work.Path("item" + index));
        plan = await work.Inspect();
        await Assert.ThrowsAsync<InvalidDataException>(() => work.Editor.ApplyFolderPermissionsAsync(plan.Token,
            Enumerable.Range(0, 1024).Select(index => index.ToString("x32")).ToArray(), true));
        Assert.Empty(Directory.GetFiles(work.Tree, "*.allowed"));
        await File.WriteAllBytesAsync(work.Path("large"), new byte[NomadNetEditor.MaximumBytes + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => work.Inspect());
        File.Delete(work.Path("large"));
        await work.Inspect();
    }

    [Fact, Trait("TestId", "T435")]
    public async Task ConfinementHiddenRootSidecarAndReparseRefusalsPreserveOutsideBytes()
    {
        using var work = new Workspace();
        await work.Seed();
        foreach (var path in new[] { ".", "../escape", ".hidden", "CON", "tree.allowed" })
            await Assert.ThrowsAnyAsync<Exception>(() => work.Editor.InspectFolderPermissionsAsync("pages", path));
        await File.WriteAllTextAsync(work.Tree + ".allowed", "root policy");
        await Assert.ThrowsAsync<IOException>(() => work.Inspect());
        File.Delete(work.Tree + ".allowed");
        await File.WriteAllTextAsync(work.Path(".hidden"), "retained hidden");
        await Assert.ThrowsAsync<IOException>(() => work.Inspect());
        File.Delete(work.Path(".hidden"));
        var outside = System.IO.Path.Combine(work.Root, "outside");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(System.IO.Path.Combine(outside, "retained"), "outside");
        var link = work.Path("link");
        if (OperatingSystem.IsWindows())
        {
            var start = new System.Diagnostics.ProcessStartInfo("pwsh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoProfile", "-Command", $"New-Item -ItemType Junction -Path '{link}' -Target '{outside}' | Out-Null" }) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, outside);
        try { await Assert.ThrowsAsync<IOException>(() => work.Inspect()); }
        finally { Directory.Delete(link); }
        Assert.Equal("outside", await File.ReadAllTextAsync(System.IO.Path.Combine(outside, "retained")));
        await work.Inspect();
    }

    [Fact, Trait("TestId", "T436")]
    public async Task SafeguardsAffectedDirtyAndStaleBuffersRefuseThenCleanPermissionSnapshotSupportsSaveAndRestart()
    {
        using var work = new Workspace();
        await work.Seed();
        await work.Editor.OpenAsync("pages", "tree/a");
        work.Editor.Buffer.Set("unsaved");
        var plan = await work.Inspect();
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.Editor.ApplyFolderPermissionsAsync(plan.Token, [B], true));
        await work.Editor.CloseAsync("discard");
        await work.Editor.OpenAsync("pages", "tree/a");
        await File.WriteAllTextAsync(work.Path("a"), "external");
        plan = await work.Inspect();
        await Assert.ThrowsAsync<IOException>(() => work.Editor.ApplyFolderPermissionsAsync(plan.Token, [B], true));
        await work.Editor.CloseAsync("discard");
        await work.Editor.OpenAsync("pages", "tree/a");
        plan = await work.Inspect();
        await work.Editor.ApplyFolderPermissionsAsync(plan.Token, [B], true);
        work.Editor.Buffer.Set("healthy save");
        await work.Editor.SaveAsync();
        await work.Editor.PermissionsAsync([A]);
        using var restarted = work.Another();
        await restarted.ResumeAsync();
        Assert.Equal("healthy save", restarted.Buffer.Text);
        Assert.Equal("tree/a", restarted.RelativePath);
    }

    [Fact, Trait("TestId", "T436")]
    public async Task SafeguardsDisposalDuringStagingRollsBackWithoutRecreatingPlanAndAnotherEditorRemainsHealthy()
    {
        using var work = new Workspace();
        for (var index = 0; index < 200; index++) await File.WriteAllTextAsync(work.Path("item" + index), "synthetic");
        var plan = await work.Inspect();
        using var watcher = new FileSystemWatcher(work.Root, ".editor-permissions-*") { NotifyFilter = NotifyFilters.DirectoryName };
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Created += (_, _) => { work.Editor.Dispose(); observed.TrySetResult(); };
        watcher.EnableRaisingEvents = true;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => work.Editor.ApplyFolderPermissionsAsync(plan.Token, [B], true));
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(work.Editor.PendingFolder);
        Assert.Empty(Directory.GetFiles(work.Tree, "*.allowed"));
        Assert.Empty(Directory.GetDirectories(work.Root, ".editor-permissions-*"));
        using var another = work.Another();
        Assert.Equal(0, (await another.InspectFolderPermissionsAsync("pages", "tree")).Restricted);
    }

    [WindowsPermissionFact, Trait("TestId", "T436")]
    public async Task SafeguardsActualWindowsSecondPolicyLockRollsBackEarlierPolicyBytesAndServiceRecovers()
    {
        using var work = new Workspace();
        await work.Seed();
        var original = await File.ReadAllBytesAsync(work.Path("a.allowed"));
        var plan = await work.Inspect();
        using (var locked = new FileStream(work.Path("b.allowed"), FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAsync<IOException>(() => work.Editor.ApplyFolderPermissionsAsync(plan.Token, [B], true));
        Assert.Equal(original, await File.ReadAllBytesAsync(work.Path("a.allowed")));
        Assert.Equal(original, await File.ReadAllBytesAsync(work.Path("b.allowed")));
        Assert.Empty(Directory.GetDirectories(work.Root, ".editor-permissions-*"));
        File.SetAttributes(work.Path("a.allowed"), FileAttributes.ReadOnly);
        try
        {
            plan = await work.Inspect();
            await Assert.ThrowsAsync<IOException>(() => work.Editor.ApplyFolderPermissionsAsync(plan.Token, null, true));
        }
        finally { File.SetAttributes(work.Path("a.allowed"), FileAttributes.Normal); }
        plan = await work.Inspect();
        await work.Editor.ApplyFolderPermissionsAsync(plan.Token, null, true);
        Assert.Equal(0, (await work.Inspect()).Restricted);
    }

    private sealed class WindowsPermissionFactAttribute : FactAttribute
    {
        public WindowsPermissionFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Requires actual Windows deny-delete sharing; Linux is not claimed.";
        }
    }

    private sealed class Workspace : IDisposable
    {
        public string Root { get; } = System.IO.Path.GetFullPath(System.IO.Path.Combine("artifacts", "tests", "nomad-folder-permissions", "managed-" + Guid.NewGuid().ToString("N")));
        public string Tree => System.IO.Path.Combine(Root, "tree");
        public NomadNetEditor Editor { get; }
        public Workspace() { Directory.CreateDirectory(Tree); Editor = Another(); }
        public NomadNetEditor Another() => new(Root, new NomadNetSettings { PagesPath = Root });
        public string Path(string relative) => System.IO.Path.Combine(Tree, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        public Task<NomadNetFolderPermissionSummary> Inspect() => Editor.InspectFolderPermissionsAsync("pages", "tree");
        public async Task Seed()
        {
            foreach (var name in new[] { "a", "b" })
            {
                await File.WriteAllTextAsync(Path(name), "content " + name);
                await File.WriteAllTextAsync(Path(name + ".allowed"), A + "\n");
            }
        }
        public void Dispose() { Editor.Dispose(); Directory.Delete(Root, true); }
    }
}
