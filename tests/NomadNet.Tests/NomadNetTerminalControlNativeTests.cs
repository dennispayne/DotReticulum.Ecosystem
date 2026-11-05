using System.Security.Cryptography;
using System.Text;
using DotReticulum.Applications.Rnsh;

namespace NomadNet.Tests;

[Trait("Category", "Integration")]
public sealed class NomadNetTerminalControlNativeTests
{
    [TerminalControlFact, Trait("TestId", "T556")]
    public async Task T556ActualConptyIntroThemeEveryColorModeAndGlyphSetRender()
    {
        var index = 0;
        foreach (var mode in new[] { "monochrome", "16", "88", "256", "24bit" })
        {
            var settings = Base() with { IntroTime = .125, IntroText = "Observed custom intro", ColorMode = mode,
                Theme = index % 2 == 0 ? "light" : "dark", Glyphs = new[] { "plain", "unicode", "nerdfont" }[index++ % 3] };
            using var work = await Workspace.CreateAsync(settings);
            await using var terminal = work.Start();
            await terminal.WaitAsync("nomadnet> ");
            Assert.Contains("Observed custom intro", terminal.Text);
            Assert.Contains(NomadNetTerminalControls.Icon(settings), terminal.Text);
            await terminal.CommandAsync("menu");
            Assert.DoesNotContain("Utilities]", terminal.Text);
            Assert.Contains("Guide]", terminal.Text);
            // ConPTY may coalesce SGR attributes, but retains the original palette hue and selected depth.
            if (mode == "24bit") Assert.Contains("38;2;24;64;96m", terminal.Text);
            if (mode == "88") Assert.Contains("38;5;21m", terminal.Text);
            if (mode == "256") Assert.Contains("38;5;153m", terminal.Text);
            if (mode == "16") Assert.Contains("\u001b[37m", terminal.Text);
            if (mode == "monochrome") Assert.DoesNotContain("38;", terminal.Text);
            await terminal.CommandAsync("guide");
            await terminal.CommandAsync("utility identity");
            Assert.Contains("Speed test:", terminal.Text);
            await terminal.CommandAsync("quit", true);
            Assert.DoesNotContain("Could not restore the owned console input mode", terminal.Text);
        }
    }

    [TerminalControlFact, Trait("TestId", "T557")]
    public async Task T557ActualConptyMouseClicksSelectVisibleMenuWhileDisabledAndMalformedInputCannotActivate()
    {
        using var enabled = await Workspace.CreateAsync(Base() with { EnableUtilities = true });
        await using (var terminal = enabled.Start())
        {
            await terminal.WaitAsync("nomadnet> ");
            var prompts = terminal.Count("nomadnet> ");
            await terminal.SendAsync("\u001b[<0;50;1M");
            await terminal.WaitAsync("nomadnet> ", prompts + 1);
            Assert.Contains("Mouse menu selected: utility help", terminal.Text);
            Assert.Contains("Speed sizes:", terminal.Text);
            await terminal.SendAsync("\u001b[<0;99999999;1M");
            await terminal.CommandAsync("utility identity");
            Assert.Contains("Speed test:", terminal.Text);
            await terminal.CommandAsync("quit", true);
        }
        using var disabled = await Workspace.CreateAsync(Base() with { MouseEnabled = false });
        await using (var terminal = disabled.Start())
        {
            await terminal.WaitAsync("nomadnet> ");
            var prompts = terminal.Count("nomadnet> ");
            await terminal.SendAsync("\u001b[<0;4;1M");
            await Task.Delay(350);
            Assert.Equal(prompts, terminal.Count("nomadnet> "));
            Assert.DoesNotContain("\u001b[?1000h", terminal.Text);
            await terminal.CommandAsync("utility identity");
            await terminal.CommandAsync("quit", true);
        }
    }

    [TerminalControlFact, Trait("TestId", "T558")]
    public async Task T558ActualConptyConfiguredAnimationAndCancellationRestoreInputThenIdentityRestart()
    {
        using var work = await Workspace.CreateAsync(Base() with { AnimationInterval = 2 });
        var identity = await work.IdentityHashAsync();
        await using (var terminal = work.Start())
        {
            await terminal.WaitAsync("nomadnet> ");
            var started = System.Diagnostics.Stopwatch.StartNew();
            await terminal.SendAsync("menu watch 2\r");
            await terminal.WaitAsync("2/2 Network");
            Assert.True(started.Elapsed >= TimeSpan.FromSeconds(1.8));
            await terminal.WaitAsync("nomadnet> ", 2);
            await terminal.SendAsync("menu watch 60\r");
            await terminal.WaitAsync("1/60 Network");
            await terminal.SendAsync("\u0003");
            await terminal.ExitAsync();
            Assert.DoesNotContain("Could not restore the owned console input mode", terminal.Text);
        }
        await using (var restart = work.Start())
        {
            await restart.WaitAsync("nomadnet> ");
            await restart.CommandAsync("utility identity");
            await restart.CommandAsync("quit", true);
        }
        Assert.Equal(identity, await work.IdentityHashAsync());
    }

    private static NomadNetSettings Base() => new()
    {
        IntroTime = 0, ClientAnnounceAtStart = false, PeriodicLxmfSync = false, LogLevel = 0,
        NodePageRefreshIntervalMinutes = 0, NodeFileRefreshIntervalMinutes = 0,
    };

    private sealed class TerminalControlFactAttribute : FactAttribute
    {
        public TerminalControlFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Windows ConPTY only; current Linux WSL EIO remains blocked.";
            else if (Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_TERMINAL_CONTROLS") != "1")
                Skip = "Select DOTRETICULUM_NOMADNET_TERMINAL_CONTROLS=1 and an exact published native binary.";
        }
    }

    private sealed class Workspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetFullPath(Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_TERMINAL_EVIDENCE")
            ?? Path.Combine("artifacts", "tests", "nomad-terminal-controls")), "conpty-" + Guid.NewGuid().ToString("N"));
        public string Profile => Path.Combine(Root, "profile");
        public string Rns => Path.Combine(Root, "rns");
        private int _terminals;
        public static async Task<Workspace> CreateAsync(NomadNetSettings settings)
        {
            var work = new Workspace();
            Directory.CreateDirectory(work.Rns);
            await settings.SaveAsync(work.Profile);
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
            ?? throw new InvalidOperationException("Select the exact published native binary."),
            Profile, Rns, Path.Combine(Root, $"terminal-{++_terminals}.txt"));
        public async Task<byte[]> IdentityHashAsync()
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(Profile, "identity"));
            try { return SHA256.HashData(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        public void Dispose() { } // Only synthetic acceptance profiles; retained as evidence.
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
            _process = RnshProcess.Start(command, new RnshExecute { Command = command,
                Terminal = "xterm", Window = new RnshWindow { Columns = 160, Rows = 40 } }, null);
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
        public async Task SendAsync(string text)
        {
            await _process.Stdin.WriteAsync(Encoding.UTF8.GetBytes(text));
            await _process.Stdin.FlushAsync();
        }
        public async Task CommandAsync(string text, bool exits = false)
        {
            var count = Count("nomadnet> ");
            await SendAsync(text + "\r");
            if (exits) await ExitAsync();
            else await WaitAsync("nomadnet> ", count + 1);
        }
        public async Task ExitAsync()
        {
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
