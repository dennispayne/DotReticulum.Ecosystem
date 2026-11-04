using System.Text;
using System.Text.Json;
using DotReticulum.Applications.Rnsd;

namespace NomadNet.Tests;

// Executed primary f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2; generator records
// the explicit terminal-construction observation boundary, not a primary Windows PTY claim.
[Trait("Category", "Unit")]
public sealed class NomadNetExternalEditorContractTests
{
    [Fact]
    public void ArgumentsMatchExecutedPrimaryWithoutShellSplitting()
    {
        using var fixture = JsonDocument.Parse(typeof(NomadNetExternalEditorContractTests).Assembly
            .GetManifestResourceStream("NomadNet.Tests.NomadNetExternalEditorVectors.json")!);
        Assert.Equal("f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2", fixture.RootElement.GetProperty("revision").GetString());
        Assert.Equal("13a0a531ccc2fc207dc6b4c7f644c3eff6d261bb34edf84781c1c6f76f4b0421",
            fixture.RootElement.GetProperty("sources").GetProperty("nomadnet/ui/textui/Config.py").GetString());
        foreach (var row in fixture.RootElement.GetProperty("launches").EnumerateArray())
            Assert.Equal(row.GetProperty("argv").EnumerateArray().Select(value => value.GetString()),
                NomadNetExternalEditor.Arguments(row.GetProperty("configured").GetString()!, "owned/native config",
                    row.GetProperty("platform").GetString() == "Darwin"));
        Assert.Throws<ArgumentException>(() => NomadNetExternalEditor.Arguments("bad\neditor", "config"));
        Assert.Equal("nano", new NomadNetSettings().Editor);
        Assert.Equal("original editor", NomadNetConfigImport.Parse("[textui]\neditor = original editor\n").Settings.Editor);
    }
}

[Trait("Category", "Integration")]
public sealed class NomadNetExternalEditorStorageTests
{
    private const string InitialRns = "# Keep original comment\n[reticulum]\n share_instance = no\n enable_transport = no\n[interfaces]\n";

    [Fact]
    public async Task SelectedRnsBufferPreservesExactTextAndCannotRenameDeleteOrPublish()
    {
        using var work = new Workspace();
        await File.WriteAllTextAsync(work.Rns, InitialRns, new UTF8Encoding(false));
        using var editor = work.Editor();
        await editor.OpenAsync("rns", "selected");
        Assert.Equal(InitialRns, editor.Buffer.Text);
        Assert.Throws<ArgumentException>(() => editor.Resolve("rns", "some-other-config"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => editor.DeleteAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => editor.RenameAsync("pages", "published"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => editor.PermissionsAsync([]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => editor.SaveAsAsync("files", "published"));
        var changed = InitialRns.Replace("enable_transport = no", "enable_transport = yes", StringComparison.Ordinal);
        editor.Buffer.Set(changed);
        editor.ValidateConfiguration();
        await editor.SaveAsync();
        Assert.Equal(changed, await File.ReadAllTextAsync(work.Rns));
        Assert.True(RnsdConfigParser.Parse(changed).EnableTransport);
        await editor.CloseAsync("discard");
        using var resumed = work.Editor();
        await resumed.ResumeAsync();
        Assert.Equal(changed, await File.ReadAllTextAsync(work.Rns));
    }

    [Fact]
    public async Task StagedNativeCommitFreezesStaleRuntimeWritersButDoesNotHotReload()
    {
        using var work = new Workspace();
        var settings = new NomadNetSettings { DisplayName = "Original" };
        await settings.SaveAsync(work.Root);
        using var editor = work.Editor();
        await editor.OpenAsync("config", "settings.json");
        await using var policy = new NomadNetPropagation(work.Root, settings, () => [], _ => { },
            (_, _) => Task.FromResult(0), () => { });
        Assert.True(await NomadNetExternalEditor.EditAsync(editor, work.Root,
            async (path, token) =>
            {
                Assert.NotEqual(Path.Combine(work.Root, "settings.json"), path);
                var text = await File.ReadAllTextAsync(path, token);
                await File.WriteAllTextAsync(path, text.Replace("Original", "Changed", StringComparison.Ordinal), new UTF8Encoding(false), token);
            }, token => policy.SaveConfigurationForRestartAsync(editor.SaveAsync, token)));
        Assert.True(policy.ConfigurationRestartPending);
        Assert.Equal("Original", policy.Settings.DisplayName);
        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.UpdateSettingsAsync(value => value with { ClipboardCopy = true }));
        var bytes = await File.ReadAllBytesAsync(Path.Combine(work.Root, "settings.json"));
        await policy.RecordClientAnnounceAsync(DateTimeOffset.UtcNow);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(work.Root, "settings.json")));
        Assert.Equal("Changed", (await NomadNetSettings.OpenAsync(work.Root)).DisplayName);
        Assert.Empty(Directory.GetDirectories(work.Root, ".editor-session-*"));
    }

    [Fact]
    public async Task StagingRejectsInvalidOversizeConflictNonzeroAndCancellationThenRecovers()
    {
        using var work = new Workspace();
        await File.WriteAllTextAsync(work.Rns, InitialRns);
        using var editor = work.Editor();
        await editor.OpenAsync("rns", "selected");
        foreach (var text in new[] { "[reticulum]\nshare_instance=invalid\n", new string('x', NomadNetEditor.MaximumBytes + 1) })
        {
            await Assert.ThrowsAnyAsync<Exception>(() => NomadNetExternalEditor.EditAsync(editor, work.Root,
                (path, token) => File.WriteAllTextAsync(path, text, token), editor.SaveAsync));
            Assert.Equal(InitialRns, await File.ReadAllTextAsync(work.Rns));
            Assert.False(editor.Buffer.Dirty);
            Assert.Empty(Directory.GetDirectories(work.Root, ".editor-session-*"));
        }
        await Assert.ThrowsAsync<IOException>(() => NomadNetExternalEditor.EditAsync(editor, work.Root,
            (_, _) => throw new IOException("Synthetic nonzero exit"), editor.SaveAsync));
        using (var cancellation = new CancellationTokenSource())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NomadNetExternalEditor.EditAsync(editor, work.Root,
                async (path, token) =>
                {
                    await File.WriteAllTextAsync(path, InitialRns + "# uncommitted\n", token);
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }, editor.SaveAsync, cancellation.Token));
        }
        var external = InitialRns + "# External writer wins\n";
        await Assert.ThrowsAsync<IOException>(() => NomadNetExternalEditor.EditAsync(editor, work.Root,
            async (path, token) =>
            {
                await File.WriteAllTextAsync(path, InitialRns + "# staged edit\n", token);
                await File.WriteAllTextAsync(work.Rns, external, token);
            }, editor.SaveAsync));
        Assert.Equal(external, await File.ReadAllTextAsync(work.Rns));
        await editor.OpenAsync("rns", "selected");
        Assert.False(await NomadNetExternalEditor.EditAsync(editor, work.Root, (_, _) => Task.CompletedTask, editor.SaveAsync));
        Assert.Empty(Directory.GetDirectories(work.Root, ".editor-session-*"));
    }

    [Fact]
    public async Task ReparseConfigurationAndStagedReplacementAreRejectedWithoutFollowing()
    {
        using var work = new Workspace();
        await File.WriteAllTextAsync(work.Rns, InitialRns);
        using var editor = work.Editor();
        await editor.OpenAsync("rns", "selected");
        var other = Path.Combine(work.Root, "other");
        await File.WriteAllTextAsync(other, "untouched");
        await Assert.ThrowsAsync<IOException>(() => NomadNetExternalEditor.EditAsync(editor, work.Root,
            (path, _) =>
            {
                File.Delete(path);
                File.CreateSymbolicLink(path, other);
                return Task.CompletedTask;
            }, editor.SaveAsync));
        Assert.Equal("untouched", await File.ReadAllTextAsync(other));
        Assert.Equal(InitialRns, await File.ReadAllTextAsync(work.Rns));
        Assert.Empty(Directory.GetDirectories(work.Root, ".editor-session-*"));
    }

    [Fact]
    public async Task DisposingConfigurationClearsSelectionAndDoesNotSaveADraft()
    {
        using var work = new Workspace();
        await File.WriteAllTextAsync(work.Rns, InitialRns);
        var editor = work.Editor();
        await editor.OpenAsync("rns", "selected");
        editor.Buffer.Set(InitialRns + "# Uncommitted draft\n");
        editor.Dispose();
        editor.Dispose();
        Assert.False(editor.IsOpen);
        Assert.Empty(editor.Buffer.Text);
        Assert.Throws<ObjectDisposedException>(() => editor.Resolve("rns", "selected"));
        Assert.Equal(InitialRns, await File.ReadAllTextAsync(work.Rns));
    }

    private sealed class Workspace : IDisposable
    {
        public string Root { get; } = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomad-external-editor", Guid.NewGuid().ToString("N")));
        public string Rns => Path.Combine(Root, "explicitly-selected-rns");
        public Workspace() => Directory.CreateDirectory(Root);
        public NomadNetEditor Editor() => new(Root, new NomadNetSettings(), Rns);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
