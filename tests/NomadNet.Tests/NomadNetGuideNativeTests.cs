using System.Security.Cryptography;
using System.Text;
using DotReticulum.Applications.Rnsh;

namespace NomadNet.Tests;

[Trait("Category", "Integration")]
public sealed class NomadNetGuideNativeTests
{
    [GuideNativeFact, Trait("TestId", "T820")]
    public async Task T820ActualConptyHiddenMenuGuideSelectionAnchorsViewportHistoryAndErrorsRecover()
    {
        var work = await Workspace.CreateAsync();
        await using var terminal = work.Start("navigation");
        await terminal.WaitAsync("nomadnet> ");
        Assert.Contains("Local application guide", terminal.Text);
        Assert.DoesNotContain("5:Guide", terminal.Text);
        await terminal.CommandAsync("guide topics");
        Assert.Contains("network-configuration", terminal.Text);
        await terminal.CommandAsync("guide hosting#access");
        await terminal.CommandAsync("guide view 3");
        var access = System.Text.RegularExpressions.Regex.Match(terminal.LastResponse, @"Guide hosting; lines [\d-]+/\d+").Value;
        Assert.NotEmpty(access);
        await terminal.CommandAsync("guide messages");
        await terminal.CommandAsync("guide back");
        await terminal.CommandAsync("guide view");
        Assert.Contains(access, terminal.LastResponse);
        await terminal.CommandAsync("guide forward");
        Assert.Contains("Messages and attachments", terminal.LastResponse);
        await terminal.CommandAsync("guide display");
        await terminal.CommandAsync("guide next");
        Assert.Contains("Heading levels", terminal.LastResponse);
        await terminal.CommandAsync("guide next");
        Assert.Contains("Anchor exercise", terminal.LastResponse);
        await terminal.CommandAsync("guide links");
        Assert.Contains("guide:display#sample", terminal.LastResponse);
        await terminal.CommandAsync("guide open 2");
        Assert.Contains("Original console controls", terminal.LastResponse);
        foreach (var command in new[] { "guide missing", "guide keyboard#missing", "guide view 0",
            "guide scroll 10001", "guide topics surplus", "guide back surplus", "guide open zero",
            "guide open 2147483647", "guide select 0", "guide find", "guide " + new string('x', 257) })
        {
            await terminal.CommandAsync(command);
            Assert.Contains("Error:", terminal.LastResponse);
        }
        await terminal.CommandAsync("guide find RESTART");
        Assert.Contains("local guide matches", terminal.LastResponse);
        await terminal.CommandAsync("guide");
        Assert.Contains("Local application guide", terminal.LastResponse);
        await terminal.CommandAsync("quit", true);
    }

    [GuideNativeFact, Trait("TestId", "T821")]
    public async Task T821ActualConptyGuideAuthoringAndInterruptRestartRetainIdentityFileAndFreshHistory()
    {
        var work = await Workspace.CreateAsync();
        byte[] identity;
        await using (var terminal = work.Start("authoring-interrupt"))
        {
            await terminal.WaitAsync("nomadnet> ");
            identity = SHA256.HashData(await File.ReadAllBytesAsync(work.Identity));
            await terminal.CommandAsync("guide authoring");
            await terminal.CommandAsync("edit mkdir pages");
            await terminal.CommandAsync("edit new pages guide-example.mu");
            await terminal.CommandAsync("edit text \">Synthetic local guide flow\\nsaved without executing links\\n\"");
            await terminal.CommandAsync("edit preview");
            Assert.Contains("Synthetic local guide flow", terminal.LastResponse);
            await terminal.CommandAsync("edit save");
            await terminal.CommandAsync("edit close discard");
            await terminal.CommandAsync("guide hosting#access");
            await terminal.InterruptAsync();
            Assert.DoesNotContain("Error:", terminal.Text);
        }
        var path = Path.Combine(work.Profile, "storage", "pages", "guide-example.mu");
        Assert.Equal(">Synthetic local guide flow\nsaved without executing links\n", await File.ReadAllTextAsync(path));
        var hash = SHA256.HashData(await File.ReadAllBytesAsync(path));
        await using (var terminal = work.Start("restart"))
        {
            await terminal.WaitAsync("nomadnet> ");
            Assert.DoesNotContain("Local application guide", terminal.Text);
            await terminal.CommandAsync("guide back");
            Assert.Contains("No previous guide topic", terminal.LastResponse);
            await terminal.CommandAsync("guide view 1");
            Assert.Contains("Guide start; lines 1-1/", terminal.LastResponse);
            await terminal.CommandAsync("edit open pages guide-example.mu");
            await terminal.CommandAsync("edit show");
            Assert.Contains("saved without executing links", terminal.LastResponse);
            await terminal.CommandAsync("guide licenses");
            await terminal.CommandAsync("quit", true);
        }
        Assert.Equal(identity, SHA256.HashData(await File.ReadAllBytesAsync(work.Identity)));
        Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(path)));
    }

    private sealed class GuideNativeFactAttribute : FactAttribute
    {
        public GuideNativeFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "C102 is Windows ConPTY acceptance; current Linux execution is blocked by WSL EIO.";
            else if (Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_GUIDE") != "1")
                Skip = "Enable C102 with the frozen native binary and owned evidence directory.";
        }
    }

    private sealed class Workspace
    {
        public string Root { get; } = Path.Combine(Path.GetFullPath(
            Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_GUIDE_EVIDENCE")
            ?? Path.Combine("artifacts", "tests", "nomad-guide")), "conpty-" + Guid.NewGuid().ToString("N"));
        public string Profile => Path.Combine(Root, "profile");
        public string Rns => Path.Combine(Root, "rns");
        public string Identity => Path.Combine(Rns, "storage", "nomadnet_identity");
        public static async Task<Workspace> CreateAsync()
        {
            var work = new Workspace();
            Directory.CreateDirectory(work.Rns);
            await File.WriteAllTextAsync(Path.Combine(work.Rns, "config"),
                "[reticulum]\nshare_instance=no\nenable_transport=no\n[logging]\nloglevel=0\n[interfaces]\n");
            await new NomadNetSettings { ClientAnnounceAtStart = false, PeriodicLxmfSync = false,
                IntroTime = 0, ColorMode = "monochrome", Glyphs = "plain", MouseEnabled = false, HideGuide = true }
                .SaveAsync(work.Profile);
            return work;
        }
        public Terminal Start(string name) => new(
            Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_BINARY")
                ?? throw new InvalidOperationException("Select the frozen native guide binary."),
            Profile, Rns, Path.Combine(Root, name + ".terminal.txt"));
    }

    private sealed class Terminal : IAsyncDisposable
    {
        private readonly RnshProcess _process;
        private readonly Task<int> _exit;
        private readonly Task _output;
        private readonly StringBuilder _text = new();
        private readonly string _log;
        private int _responseStart;
        public string Text { get { lock (_text) return _text.ToString(); } }
        private string PromptText => Text.Replace("\u001b[1C", " ", StringComparison.Ordinal);
        public string LastResponse => Text[_responseStart..].Replace("\r", "", StringComparison.Ordinal);
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
                    if (_text.Length + length > 2_000_000) throw new IOException("Guide ConPTY evidence exceeds2 MiB.");
                    _text.Append(buffer, 0, length);
                }
        }
        public async Task WaitAsync(string text, int count = 1)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                while (PromptText.Split(text, StringSplitOptions.None).Length - 1 < count)
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
            var count = PromptText.Split("nomadnet> ", StringSplitOptions.None).Length - 1;
            _responseStart = Text.Length;
            await _process.Stdin.WriteAsync(Encoding.UTF8.GetBytes(text + "\r"));
            await _process.Stdin.FlushAsync();
            if (!exits) await WaitAsync("nomadnet> ", count + 1);
            else
            {
                Assert.Equal(0, await _exit.WaitAsync(TimeSpan.FromSeconds(15)));
                await _output.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        public async Task InterruptAsync()
        {
            await _process.Stdin.WriteAsync(new byte[] { 3 });
            await _process.Stdin.FlushAsync();
            Assert.Equal(0, await _exit.WaitAsync(TimeSpan.FromSeconds(15)));
            await _output.WaitAsync(TimeSpan.FromSeconds(5));
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
