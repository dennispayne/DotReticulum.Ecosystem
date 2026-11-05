using System.Security.Cryptography;
using System.Text;
using DotReticulum.Applications.Rnsh;

namespace NomadNet.Tests;

[Trait("Category", "Integration")]
public sealed class NomadNetEditorPresentationNativeTests
{
    [EditorPresentationFact, Trait("TestId", "T620")]
    public async Task T620ActualConptySourceSyntaxGutterSplitPanesAndDebouncedLatestPreview()
    {
        using var work = await Workspace.CreateAsync();
        await using var terminal = work.Start();
        await terminal.WaitAsync("nomadnet> ");
        await terminal.CommandAsync("edit new pages observed.mu");
        await terminal.CommandAsync("edit text \"#Synthetic comment\\n>Initial heading\\nbody\"");
        await terminal.CommandAsync("edit text \"#Synthetic comment\\n>Latest heading\\n`!body`!\"");
        await Task.Delay(550);
        await terminal.CommandAsync("edit preview-status");
        Assert.Contains("state=ready", terminal.Text);
        await terminal.CommandAsync("edit view 120 12");
        Assert.Contains("files=22%", terminal.Text);
        Assert.Contains("editor=40%", terminal.Text);
        Assert.Contains("preview=38%", terminal.Text);
        Assert.Contains("Latest heading", terminal.Text);
        Assert.Contains(" 1 ", terminal.Text);
        Assert.Contains("38;2;", terminal.Text);
        await terminal.CommandAsync("edit pane files");
        await terminal.CommandAsync("edit pane preview");
        await terminal.CommandAsync("edit pane editor");
        Assert.Contains("Editor visible panes: editor", terminal.Text);
        await terminal.CommandAsync("edit view 80 6");
        await terminal.CommandAsync("edit save");
        await terminal.CommandAsync("quit", exits: true);
        Assert.DoesNotContain("Error:", terminal.Text);
        Assert.Equal("#Synthetic comment\n>Latest heading\n`!body`!",
            await File.ReadAllTextAsync(Path.Combine(work.Profile, "storage", "pages", "observed.mu")));
    }

    [EditorPresentationFact, Trait("TestId", "T621")]
    public async Task T621ActualConptyFilteredTreeVisibilitySelectionAndIdentitySurviveRestart()
    {
        using var work = await Workspace.CreateAsync();
        Directory.CreateDirectory(Path.Combine(work.Profile, "storage", "pages", "nested"));
        await File.WriteAllTextAsync(Path.Combine(work.Profile, "storage", "pages", "nested", "saved.mu"), ">Saved preview");
        await File.WriteAllTextAsync(Path.Combine(work.Profile, "storage", "pages", "nested", "hidden.txt"), "not micron");
        var identity = await work.IdentityHashAsync();
        await using (var terminal = work.Start())
        {
            await terminal.WaitAsync("nomadnet> ");
            await terminal.CommandAsync("edit expand pages nested");
            await terminal.CommandAsync("edit filter on");
            await terminal.CommandAsync("edit open pages nested\\saved.mu");
            await terminal.CommandAsync("edit pane preview");
            await terminal.CommandAsync("edit focus next");
            await terminal.CommandAsync("edit view 100 8");
            Assert.Contains("Saved preview", terminal.Text.Replace(
                "\uF192 [1:Conversations] [2:Network] [3:Channels] [4:Config] [5:Guide] [6:Quit]",
                "", StringComparison.Ordinal));
            Assert.DoesNotContain("hidden.txt", terminal.Text);
            await terminal.CommandAsync("quit", true);
        }
        await using (var terminal = work.Start())
        {
            await terminal.WaitAsync("nomadnet> ");
            await terminal.CommandAsync("edit view 100 8");
            Assert.Contains("filter.mu=True", terminal.Text);
            Assert.DoesNotContain("preview=38%", terminal.Text);
            Assert.Contains("saved.mu", terminal.Text);
            await terminal.CommandAsync("edit status");
            Assert.Contains("nested\\saved.mu", terminal.Text);
            await terminal.CommandAsync("edit pane preview");
            await terminal.CommandAsync("edit preview");
            await terminal.CommandAsync("quit", true);
            Assert.DoesNotContain("Error:", terminal.Text);
        }
        Assert.Equal(identity, await work.IdentityHashAsync());
    }

    [EditorPresentationFact, Trait("TestId", "T622")]
    public async Task T622ActualConptyPendingCloseCancellationAndFailedControlContinueHealthyService()
    {
        using var work = await Workspace.CreateAsync();
        await using (var terminal = work.Start())
        {
            await terminal.WaitAsync("nomadnet> ");
            await terminal.CommandAsync("edit new pages scratch.mu");
            await terminal.CommandAsync("edit text \">Unsaved cancelled preview\"");
            await terminal.CommandAsync("edit close discard");
            await Task.Delay(500);
            await terminal.CommandAsync("edit preview-status");
            Assert.Contains("state=ready", terminal.Text);
            await terminal.CommandAsync("edit pane invalid");
            Assert.Contains("Error: Choose files, editor or preview.", terminal.Text);
            await terminal.CommandAsync("edit pane files");
            await terminal.CommandAsync("edit view 80 5");
            await terminal.CommandAsync("utility identity");
            await terminal.CommandAsync("quit", true);
            Assert.DoesNotContain("Could not restore the owned console input mode", terminal.Text);
        }
        Assert.False(File.Exists(Path.Combine(work.Profile, "storage", "pages", "scratch.mu")));
        Assert.DoesNotContain("Unsaved cancelled", await File.ReadAllTextAsync(Path.Combine(work.Profile, "editor_state.json")));
    }

    private sealed class EditorPresentationFactAttribute : FactAttribute
    {
        public EditorPresentationFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Windows ConPTY acceptance only; current Linux WSL EIO remains blocked.";
            else if (Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_EDITOR_PRESENTATION") != "1")
                Skip = "Select DOTRETICULUM_NOMADNET_EDITOR_PRESENTATION=1 with the exact frozen published binary.";
        }
    }

    private sealed class Workspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetFullPath(Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_EDITOR_EVIDENCE")
            ?? Path.Combine("artifacts", "tests", "nomad-editor-presentation")), "conpty-" + Guid.NewGuid().ToString("N"));
        public string Profile => Path.Combine(Root, "profile");
        public string Rns => Path.Combine(Root, "rns");
        private int _terminals;
        public static async Task<Workspace> CreateAsync()
        {
            var work = new Workspace();
            Directory.CreateDirectory(work.Rns);
            Directory.CreateDirectory(Path.Combine(work.Profile, "storage", "pages"));
            Directory.CreateDirectory(Path.Combine(work.Profile, "storage", "files"));
            await new NomadNetSettings
            {
                IntroTime = 0,
                ClientAnnounceAtStart = false,
                PeriodicLxmfSync = false,
                LogLevel = 0,
                NodePageRefreshIntervalMinutes = 0,
                NodeFileRefreshIntervalMinutes = 0,
                ColorMode = "24bit"
            }.SaveAsync(work.Profile);
            _ = await NomadNetStore.OpenAsync(work.Profile);
            await File.WriteAllTextAsync(Path.Combine(work.Rns, "config"),
                "[reticulum]\nshare_instance=no\nenable_transport=no\n[logging]\nloglevel=0\n[interfaces]\n");
            using var identity = DotReticulum.Core.Identity.Generate();
            var key = identity.ExportPrivateKey();
            try { await File.WriteAllBytesAsync(Path.Combine(work.Profile, "identity"), key); }
            finally { CryptographicOperations.ZeroMemory(key); }
            return work;
        }
        public Terminal Start() => new(Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_BINARY")
            ?? throw new InvalidOperationException("Select the exact published native binary."), Profile, Rns,
            Path.Combine(Root, $"terminal-{++_terminals}.txt"));
        public async Task<byte[]> IdentityHashAsync()
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(Profile, "identity"));
            try { return SHA256.HashData(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        public void Dispose() { } // Synthetic profiles are retained with the native receipts, never user profiles.
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
                Command = command,
                Terminal = "xterm",
                Window = new RnshWindow { Columns = 160, Rows = 40 }
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
                    if (_text.Length + length > 2_000_000) throw new IOException("Terminal output exceeds2 MiB.");
                    _text.Append(buffer, 0, length);
                }
        }
        public int Count(string text) => Text.Split(text, StringSplitOptions.None).Length - 1;
        public async Task WaitAsync(string text, int count = 1)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                while (Count(text) < count)
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
            var count = Count("nomadnet> ");
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
            try { await _output.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (ObjectDisposedException) { }
            finally { await File.WriteAllTextAsync(_log, Text, new UTF8Encoding(false)); }
        }
    }
}
