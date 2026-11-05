using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DotReticulum.Applications.Rnsh;

namespace NomadNet.Tests;

[Trait("Category", "Integration")]
public sealed class NomadNetEditorStateImportNativeTests
{
    [StateImportFact, Trait("TestId", "T741")]
    public async Task T741ActualConptyImportsPrimarySelectionPreferencesEditsSavesAndRestarts()
    {
        var work = await Workspace.CreateAsync();
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(work.Source, "editor_state.json")));
        var identity = SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(work.Profile, "identity")));
        await using (var terminal = work.Start("first"))
        {
            await terminal.WaitAsync("nomadnet> ");
            await terminal.CommandAsync("edit status");
            Assert.Contains("pages/nested/notice.mu", terminal.Text);
            Assert.Contains("editable=True; dirty=False", terminal.Text);
            await terminal.CommandAsync("edit view 100 8");
            Assert.Contains("filter.mu=True", terminal.Text);
            Assert.Contains("files=22%", terminal.Text);
            Assert.DoesNotContain("preview=38%", terminal.Text);
            await terminal.CommandAsync("edit text \">Native migrated heading\\nretained after restart\"");
            await terminal.CommandAsync("edit save");
            await terminal.CommandAsync("quit", true);
            Assert.DoesNotContain("Error:", terminal.Text);
        }
        await using (var terminal = work.Start("restart"))
        {
            await terminal.WaitAsync("nomadnet> ");
            await terminal.CommandAsync("edit status");
            await terminal.CommandAsync("edit show");
            await terminal.CommandAsync("edit tree");
            await terminal.CommandAsync("edit view 100 8");
            await terminal.CommandAsync("quit", true);
            Assert.Contains("retained after restart", terminal.Text);
            Assert.Contains("[-] pages/nested", terminal.Text);
            Assert.Contains("filter.mu=True", terminal.Text);
            Assert.DoesNotContain("Error:", terminal.Text);
        }
        Assert.Equal(identity, SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(work.Profile, "identity"))));
        Assert.Equal(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(work.Source, "editor_state.json"))));
        Assert.Equal(">Native migrated heading\nretained after restart",
            await File.ReadAllTextAsync(Path.Combine(work.Profile, "storage", "pages", "nested", "notice.mu")));
    }

    [StateImportFact, Trait("TestId", "T742")]
    public async Task T742ActualConptyCurrentDirtyConfigurationOwnershipAndFailedControlRecoverWithoutImportDrafts()
    {
        var work = await Workspace.CreateAsync();
        var statePath = Path.Combine(work.Profile, "settings.json");
        var before = SHA256.HashData(await File.ReadAllBytesAsync(statePath));
        await using var terminal = work.Start("ownership");
        await terminal.WaitAsync("nomadnet> ");
        await terminal.CommandAsync("config edit");
        await terminal.CommandAsync("edit text \"PRIVATE UNSAVED CONFIGURATION\"");
        await terminal.CommandAsync("edit status");
        Assert.Contains("Editor: config/settings.json", terminal.Text);
        Assert.Contains("dirty=True", terminal.Text);
        await terminal.CommandAsync("edit pane invalid");
        Assert.Contains("Choose files, editor or preview", terminal.Text);
        await terminal.CommandAsync("edit close cancel");
        Assert.Contains("buffer retained", terminal.Text);
        await terminal.CommandAsync("edit close discard");
        await terminal.CommandAsync("edit open pages nested/notice.mu");
        await terminal.CommandAsync("edit show");
        await terminal.CommandAsync("quit", true);
        Assert.Contains("Imported synthetic heading", terminal.Text);
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(statePath)));
        Assert.DoesNotContain("PRIVATE UNSAVED", await File.ReadAllTextAsync(Path.Combine(work.Profile, "editor_state.json")));
    }

    private sealed class StateImportFactAttribute : FactAttribute
    {
        public StateImportFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "C100 Windows ConPTY only; Linux acceptance is explicitly blocked by WSL EIO.";
            else if (Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_EDITOR_STATE_IMPORT") != "1")
                Skip = "Enable C100 with the frozen native binary and actual-primary generated synthetic profile.";
        }
    }

    private sealed class Workspace
    {
        public string Root { get; } = Path.Combine(Path.GetFullPath(
            Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_STATE_EVIDENCE")
            ?? Path.Combine("artifacts", "tests", "nomad-editor-state-import")), "conpty-" + Guid.NewGuid().ToString("N"));
        public string Source => Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_STATE_PRIMARY")
            ?? throw new InvalidOperationException("Select the actual-primary generated synthetic saved profile.");
        public string Profile => Path.Combine(Root, "native");
        public string Rns => Path.Combine(Root, "rns");
        private static string Binary => Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_BINARY")
            ?? throw new InvalidOperationException("Select the frozen native binary.");
        public static async Task<Workspace> CreateAsync()
        {
            var work = new Workspace();
            Directory.CreateDirectory(work.Rns);
            await File.WriteAllTextAsync(Path.Combine(work.Rns, "config"),
                "[reticulum]\nshare_instance=no\nenable_transport=no\n[logging]\nloglevel=0\n[interfaces]\n");
            var info = new ProcessStartInfo(Binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "--import-profile", work.Source, "--config", work.Profile, "--rnsconfig", work.Rns })
                info.ArgumentList.Add(argument);
            using var process = Process.Start(info) ?? throw new IOException("Native profile importer did not start.");
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                await File.WriteAllTextAsync(Path.Combine(work.Root, "import.stdout"), await output);
                await File.WriteAllTextAsync(Path.Combine(work.Root, "import.stderr"), await errors);
                Assert.Equal(0, process.ExitCode);
            }
            finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
            return work;
        }
        public Terminal Start(string name) => new(Binary, Profile, Rns, Path.Combine(Root, name + ".terminal.txt"));
    }

    private sealed class Terminal : IAsyncDisposable
    {
        private readonly RnshProcess _process;
        private readonly Task<int> _exit;
        private readonly Task _output;
        private readonly StringBuilder _text = new();
        private readonly string _log;
        public string Text { get { lock (_text) return _text.ToString(); } }
        public Terminal(string binary, string profile, string rns, string log)
        {
            _log = log;
            string[] command = [binary, "--config", profile, "--rnsconfig", rns];
            _process = RnshProcess.Start(command, new RnshExecute
            {
                Command = command, Terminal = "xterm", Window = new RnshWindow { Columns = 160, Rows = 40 }
            }, null);
            _exit = _process.WaitAsync(default);
            _output = ReadAsync();
        }
        private async Task ReadAsync()
        {
            using var reader = new StreamReader(_process.Stdout, Encoding.UTF8, leaveOpen: true);
            var buffer = new char[4096];
            while (await reader.ReadAsync(buffer) is var length && length > 0)
                lock (_text)
                {
                    if (_text.Length + length > 2_000_000) throw new IOException("ConPTY evidence exceeds2 MiB.");
                    _text.Append(buffer, 0, length);
                }
        }
        public async Task WaitAsync(string text, int count = 1)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                while (Text.Split(text, StringSplitOptions.None).Length - 1 < count)
                {
                    if (_output.IsFaulted) await _output;
                    if (_exit.IsCompleted) throw new IOException("Exited before: " + text + "\n" + Text);
                    await Task.Delay(30, deadline.Token);
                }
            }
            catch (OperationCanceledException) { throw new IOException("Timed out before: " + text + "\n" + Text); }
        }
        public async Task CommandAsync(string text, bool exits = false)
        {
            var count = Text.Split("nomadnet> ", StringSplitOptions.None).Length - 1;
            await _process.Stdin.WriteAsync(Encoding.UTF8.GetBytes(text + "\r"));
            await _process.Stdin.FlushAsync();
            if (!exits) await WaitAsync("nomadnet> ", count + 1);
            else
            {
                Assert.Equal(0, await _exit.WaitAsync(TimeSpan.FromSeconds(15)));
                await _output.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        public async ValueTask DisposeAsync()
        {
            await _process.DisposeAsync();
            try
            {
                await _output.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (ObjectDisposedException) { }
            finally { await File.WriteAllTextAsync(_log, Text, new UTF8Encoding(false)); }
        }
    }
}
