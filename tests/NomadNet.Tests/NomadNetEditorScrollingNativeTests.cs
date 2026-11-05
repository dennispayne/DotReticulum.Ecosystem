using System.Security.Cryptography;
using System.Text;
using DotReticulum.Applications.Rnsh;

namespace NomadNet.Tests;

[Trait("Category", "Integration")]
public sealed class NomadNetEditorScrollingNativeTests
{
    [ScrollingFact, Trait("TestId", "T710")]
    public async Task ActualConptyIndependentScrollRefreshToggleSaveAndRestart()
    {
        var work = await Workspace.CreateAsync();
        var text = string.Join("\n\n", Enumerable.Range(0, 80).Select(index => $"SCROLL-{index:D3}"));
        await File.WriteAllTextAsync(Path.Combine(work.Pages, "long.mu"), text);
        await using (var terminal = work.Start())
        {
            await terminal.WaitAsync("nomadnet> ");
            await terminal.CommandAsync("edit open pages long.mu");
            await terminal.CommandAsync("edit view 120 6");
            Assert.Contains("editor=5; preview=0; height=6", await terminal.CommandAsync("edit scroll editor page-down"));
            Assert.Contains("editor=5; preview=17; height=6", await terminal.CommandAsync("edit scroll preview 17"));
            await terminal.CommandAsync("edit pane preview");
            await terminal.CommandAsync("edit pane preview");
            Assert.Contains("editor=5; preview=17; height=6", await terminal.CommandAsync("edit view 120 6"));
            await terminal.CommandAsync("edit insert 0 \"# inserted\\n\"");
            await Task.Delay(550);
            Assert.Contains("editor=5; preview=17; height=6", await terminal.CommandAsync("edit view 120 6"));
            await terminal.CommandAsync("edit save");
            Assert.Contains("editor=5; preview=17; height=6", await terminal.CommandAsync("edit view 120 6"));
            await terminal.CommandAsync("quit", true);
            Assert.DoesNotContain("Error:", terminal.Text);
        }
        Assert.Equal("# inserted\n" + text, await File.ReadAllTextAsync(Path.Combine(work.Pages, "long.mu")));
        await using (var terminal = work.Start())
        {
            await terminal.WaitAsync("nomadnet> ");
            Assert.Contains("editor=0; preview=0; height=6", await terminal.CommandAsync("edit view 120 6"));
            Assert.Contains("long.mu", await terminal.CommandAsync("edit status"));
            Assert.Contains("editor=0; preview=5; height=6", await terminal.CommandAsync("edit scroll preview page-down"));
            await terminal.CommandAsync("quit", true);
            Assert.DoesNotContain("Error:", terminal.Text);
        }
    }

    [ScrollingFact, Trait("TestId", "T711")]
    public async Task ActualConptyMalformedCommandsClampAndPendingCloseRecovery()
    {
        var work = await Workspace.CreateAsync();
        await using var terminal = work.Start();
        await terminal.WaitAsync("nomadnet> ");
        await terminal.CommandAsync("edit new pages pending.mu");
        await terminal.CommandAsync("edit view 80 6");
        foreach (var command in new[] { "edit scroll", "edit scroll files down", "edit scroll editor 2147483648",
            "edit scroll editor 1 extra", "edit scroll preview nonsense", "edit scroll focused down" })
            Assert.Contains("Error:", await terminal.CommandAsync(command));
        Assert.Contains("editor=0; preview=0; height=6", await terminal.CommandAsync("edit scroll editor -2147483648"));
        await terminal.CommandAsync("edit pane preview");
        Assert.Contains("Error: Show the selected text pane before scrolling.", await terminal.CommandAsync("edit scroll preview down"));
        await terminal.CommandAsync("edit pane files");
        await terminal.CommandAsync("edit pane editor");
        Assert.Contains("Editor visible panes: editor", terminal.Text);
        await terminal.CommandAsync("edit text \"cancelled pending\"");
        await terminal.CommandAsync("edit close discard");
        await Task.Delay(500);
        Assert.Contains("state=ready", await terminal.CommandAsync("edit preview-status"));
        Assert.DoesNotContain("cancelled pending", await terminal.CommandAsync("edit view 80 6"));
        Assert.Contains("Speed test:", await terminal.CommandAsync("utility identity"));
        await terminal.CommandAsync("quit", true);
        Assert.False(File.Exists(Path.Combine(work.Pages, "pending.mu")));
    }

    [ScrollingFact, Trait("TestId", "T712")]
    public async Task ActualConptyLargeUnicodeTabSourceTailAndFullSavedText()
    {
        var work = await Workspace.CreateAsync();
        var text = string.Join('\n', Enumerable.Repeat(string.Concat(Enumerable.Repeat("😀\tα", 80)), 300)) + "\nSOURCE-TAIL";
        await File.WriteAllTextAsync(Path.Combine(work.Pages, "unicode.mu"), text, new UTF8Encoding(false));
        await using var terminal = work.Start();
        await terminal.WaitAsync("nomadnet> ");
        await terminal.CommandAsync("edit open pages unicode.mu");
        await terminal.CommandAsync("edit view 120 6");
        Assert.Contains("SOURCE-TAIL", await terminal.CommandAsync("edit scroll editor end"));
        Assert.Contains("editor=0;", await terminal.CommandAsync("edit scroll editor home"));
        await terminal.CommandAsync("edit insert 0 \"#saved\\n\"");
        await terminal.CommandAsync("edit save");
        await terminal.CommandAsync("quit", true);
        Assert.DoesNotContain("Error:", terminal.Text);
        Assert.Equal("#saved\n" + text, await File.ReadAllTextAsync(Path.Combine(work.Pages, "unicode.mu")));
    }

    private sealed class ScrollingFactAttribute : FactAttribute
    {
        public ScrollingFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Windows ConPTY only; current Linux WSL EIO/read-only state is blocked.";
            else if (Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_EDITOR_SCROLLING") != "1")
                Skip = "Select DOTRETICULUM_NOMADNET_EDITOR_SCROLLING=1 and the exact frozen NativeAOT binary.";
        }
    }

    private sealed class Workspace
    {
        private readonly string _root = Path.Combine(Path.GetFullPath(Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_SCROLLING_EVIDENCE")
            ?? Path.Combine("artifacts", "tests", "nomad-editor-scrolling")), "conpty-" + Guid.NewGuid().ToString("N"));
        private string Profile => Path.Combine(_root, "profile");
        private string Rns => Path.Combine(_root, "rns");
        public string Pages => Path.Combine(Profile, "storage", "pages");
        private int _terminals;
        public static async Task<Workspace> CreateAsync()
        {
            var work = new Workspace();
            Directory.CreateDirectory(work.Rns);
            Directory.CreateDirectory(work.Pages);
            Directory.CreateDirectory(Path.Combine(work.Profile, "storage", "files"));
            await new NomadNetSettings { IntroTime = 0, ClientAnnounceAtStart = false, PeriodicLxmfSync = false,
                LogLevel = 0, NodePageRefreshIntervalMinutes = 0, NodeFileRefreshIntervalMinutes = 0, ColorMode = "24bit" }.SaveAsync(work.Profile);
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
            Path.Combine(_root, $"terminal-{++_terminals}.txt"));
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
            _process = RnshProcess.Start(command, new RnshExecute { Command = command, Terminal = "xterm",
                Window = new RnshWindow { Columns = 160, Rows = 40 } }, null);
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
        private int Count(string text) => Text.Split(text.TrimEnd(), StringSplitOptions.None).Length - 1;
        public async Task WaitAsync(string text, int count = 1)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
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
        public async Task<string> CommandAsync(string text, bool exits = false)
        {
            var start = Text.Length;
            var count = Count("nomadnet> ");
            await _process.Stdin.WriteAsync(Encoding.UTF8.GetBytes(text + "\r"));
            await _process.Stdin.FlushAsync();
            if (!exits) await WaitAsync("nomadnet> ", count + 1);
            else
            {
                Assert.Equal(0, await _exit.WaitAsync(TimeSpan.FromSeconds(15)));
                await _output.WaitAsync(TimeSpan.FromSeconds(5));
            }
            return Text[start..];
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
