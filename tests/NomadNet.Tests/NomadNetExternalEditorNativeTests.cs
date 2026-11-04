using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotReticulum.Applications.Rnsh;

namespace NomadNet.Tests;

[Trait("Category", "Integration")]
public sealed class NomadNetExternalEditorNativeTests
{
    [NativeEditorFact]
    public async Task ActualConptyNativeAndSelectedRnsEditsReturnAndRestartWithIdentity()
    {
        using var work = await Workspace.CreateAsync();
        await using (var terminal = work.Start())
        {
            await terminal.WaitAsync("nomadnet> ");
            await terminal.StartEditorAsync("native");
            await terminal.SendAsync(":%s/Synthetic Original/Synthetic Edited/g\r:wq\r");
            await terminal.WaitAsync("Configuration saved. Restart NomadNet");
            await terminal.WaitAsync("nomadnet> ", 2);
            Assert.Equal("Synthetic Edited", (await NomadNetSettings.OpenAsync(work.Profile)).DisplayName);
            var bytes = await File.ReadAllBytesAsync(work.Settings);
            await terminal.CommandAsync("clipboard on");
            Assert.Contains("restart before changing runtime settings", terminal.Text, StringComparison.Ordinal);
            await terminal.CommandAsync("client announce");
            Assert.Equal(bytes, await File.ReadAllBytesAsync(work.Settings));
            await terminal.CommandAsync("status");
            var identity = await work.IdentityHashAsync();
            await terminal.CommandAsync("quit", exits: true);
            await work.RecordAsync("identity-first.sha256", Convert.ToHexStringLower(identity));
        }
        var savedIdentity = await work.IdentityHashAsync();
        await using (var terminal = work.Start())
        {
            await terminal.WaitAsync("nomadnet> ");
            await terminal.StartEditorAsync("rns");
            await terminal.SendAsync(":%s/Synthetic RNS original/Synthetic RNS changed/g\r:wq\r");
            await terminal.WaitAsync("Configuration saved. Restart NomadNet");
            await terminal.WaitAsync("nomadnet> ", 2);
            Assert.Equal(work.RnsOriginal.Replace("Synthetic RNS original", "Synthetic RNS changed", StringComparison.Ordinal),
                await File.ReadAllTextAsync(work.RnsFile));
            await terminal.CommandAsync("status");
            await terminal.CommandAsync("quit", exits: true);
        }
        await using (var terminal = work.Start())
        {
            await terminal.WaitAsync("nomadnet> ");
            await terminal.CommandAsync("config show rns");
            Assert.Contains("Synthetic RNS changed", terminal.Text, StringComparison.Ordinal);
            await terminal.CommandAsync("config show native");
            Assert.Contains("Synthetic Edited", terminal.Text, StringComparison.Ordinal);
            await terminal.CommandAsync("quit", exits: true);
        }
        Assert.Equal(savedIdentity, await work.IdentityHashAsync());
        Assert.Empty(Directory.GetDirectories(work.Profile, ".editor-session-*"));
    }

    [NativeEditorFact]
    public async Task ActualConptyInvalidConflictCancellationAndDescendantsRecover()
    {
        using var work = await Workspace.CreateAsync();
        await using var terminal = work.Start();
        await terminal.WaitAsync("nomadnet> ");
        var original = await File.ReadAllBytesAsync(work.Settings);
        await terminal.StartEditorAsync("native");
        await terminal.SendAsync("gg0iINVALID\u001b:wq\r");
        await terminal.WaitAsync("Error:");
        await terminal.WaitAsync("nomadnet> ", 2);
        Assert.Equal(original, await File.ReadAllBytesAsync(work.Settings));
        await terminal.CommandAsync("status");

        await terminal.StartEditorAsync("native");
        var failures = terminal.Count("Error:");
        var exits = terminal.Count("nomadnet> ");
        await terminal.SendAsync(":cq\r");
        await terminal.WaitAsync("Error:", failures + 1);
        await terminal.WaitAsync("nomadnet> ", exits + 1);
        Assert.Contains("exited with code 1", terminal.Text, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllBytesAsync(work.Settings));
        await terminal.CommandAsync("status");

        await terminal.StartEditorAsync("native");
        await terminal.SendAsync(":%s/Synthetic Original/Synthetic Uncommitted/g\r");
        var external = Encoding.UTF8.GetString(original).Replace("Synthetic Original", "Synthetic Concurrent", StringComparison.Ordinal);
        await File.WriteAllTextAsync(work.Settings, external, new UTF8Encoding(false));
        var errors = terminal.Count("Error:");
        var prompts = terminal.Count("nomadnet> ");
        await terminal.SendAsync(":wq\r");
        await terminal.WaitAsync("Error:", errors + 1);
        await terminal.WaitAsync("nomadnet> ", prompts + 1);
        Assert.Equal(external, await File.ReadAllTextAsync(work.Settings));
        await terminal.CommandAsync("status");

        await terminal.StartEditorAsync("native");
        var pidPath = Path.Combine(work.Root, "owned-descendant.pid");
        var shell = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_EDITOR_EXE"))!, "sh.exe");
        Assert.True(File.Exists(shell));
        await terminal.SendAsync(":let &shell='/usr/bin/sh'\r");
        var shellCommand = ":!pwsh.exe -NoProfile -Command '[IO.File]::WriteAllText(\"" +
            pidPath.Replace('\\', '/') + "\",$PID); Start-Sleep -Seconds 120'\r";
        await terminal.SendAsync(shellCommand);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!File.Exists(pidPath)) await Task.Delay(50, deadline.Token);
        int pid;
        while (!int.TryParse(await File.ReadAllTextAsync(pidPath, deadline.Token), out pid))
            await Task.Delay(50, deadline.Token);
        using var descendant = Process.GetProcessById(pid);
        prompts = terminal.Count("nomadnet> ");
        await terminal.SendAsync("\u001d");
        await terminal.WaitAsync("External editor cancelled");
        await terminal.WaitAsync("nomadnet> ", prompts + 1);
        await descendant.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(external, await File.ReadAllTextAsync(work.Settings));
        Assert.Empty(Directory.GetDirectories(work.Profile, ".editor-session-*"));
        await terminal.CommandAsync("status");
        await terminal.CommandAsync("quit", exits: true);
    }

    private sealed class NativeEditorFactAttribute : FactAttribute
    {
        public NativeEditorFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Windows ConPTY acceptance only; Linux replay is blocked by WSL EIO.";
            else if (Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_EXTERNAL_EDITOR") != "1")
                Skip = "Enable DOTRETICULUM_NOMADNET_EXTERNAL_EDITOR=1 and select an actual native binary and real Vim executable.";
        }
    }

    private sealed class Workspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetFullPath(Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_EDITOR_EVIDENCE")
            ?? Path.Combine("artifacts", "tests", "nomad-external-editor")), "terminal-" + Guid.NewGuid().ToString("N"));
        public string Profile => Path.Combine(Root, "profile");
        public string Settings => Path.Combine(Profile, "settings.json");
        public string RnsFile => Path.Combine(Root, "selected-rns", "config");
        public string RnsOriginal => "# Synthetic RNS original\n[reticulum]\n share_instance = no\n enable_transport = no\n[logging]\n loglevel = 0\n[interfaces]\n";
        private int _terminals;
        public static async Task<Workspace> CreateAsync()
        {
            var work = new Workspace();
            Directory.CreateDirectory(work.Profile);
            Directory.CreateDirectory(Path.GetDirectoryName(work.RnsFile)!);
            var executable = Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_EDITOR_EXE")
                ?? throw new InvalidOperationException("Select the real Vim executable with DOTRETICULUM_NOMADNET_EDITOR_EXE.");
            Assert.True(File.Exists(executable));
            await new NomadNetSettings
            {
                Editor = executable, DisplayName = "Synthetic Original", ClientAnnounceAtStart = false,
                PeriodicLxmfSync = false, LogLevel = 0,
            }.SaveAsync(work.Profile);
            await File.WriteAllTextAsync(work.RnsFile, work.RnsOriginal, new UTF8Encoding(false));
            using var identity = DotReticulum.Core.Identity.Generate();
            var key = identity.ExportPrivateKey();
            try { await File.WriteAllBytesAsync(Path.Combine(work.Profile, "identity"), key); }
            finally { CryptographicOperations.ZeroMemory(key); }
            return work;
        }
        public Terminal Start() => new(Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_BINARY")
            ?? throw new InvalidOperationException("Select the exact published NativeAOT executable."),
            Profile, Path.GetDirectoryName(RnsFile)!, Path.Combine(Root, "terminal-" + ++_terminals + ".txt"));
        public Task RecordAsync(string name, string text) => File.WriteAllTextAsync(Path.Combine(Root, name), text);
        public async Task<byte[]> IdentityHashAsync()
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(Profile, "identity"));
            try { return SHA256.HashData(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        public void Dispose() { } // Evidence and synthetic profiles deliberately survive acceptance.
    }

    private sealed class Terminal : IAsyncDisposable
    {
        private readonly RnshProcess _process;
        private readonly Task _output;
        private readonly Task<int> _exit;
        private readonly StringBuilder _text = new();
        private readonly string _log;
        public string Text { get { lock (_text) return _text.ToString(); } }
        public Terminal(string binary, string profile, string rns, string log)
        {
            _log = log;
            string[] argv = [binary, "--config", profile, "--rnsconfig", rns];
            _process = RnshProcess.Start(argv, new RnshExecute { Command = argv,
                Terminal = "xterm", Window = new RnshWindow { Columns = 120, Rows = 40 } }, null);
            _exit = _process.WaitAsync(CancellationToken.None);
            _output = ReadAsync();
        }
        private async Task ReadAsync()
        {
            using var reader = new StreamReader(_process.Stdout, Encoding.UTF8, leaveOpen: true);
            var buffer = new char[4096];
            while (await reader.ReadAsync(buffer) is var length && length > 0)
                lock (_text)
                {
                    if (_text.Length + length > 2_000_000) throw new IOException("Terminal evidence exceeded its two-megacharacter bound.");
                    _text.Append(buffer, 0, length);
                }
        }
        public int Count(string text) => Text.Split(text, StringSplitOptions.None).Length - 1;
        public async Task WaitAsync(string text, int count = 1)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try
            {
                while (Count(text) < count)
                {
                    if (_output.IsFaulted) await _output;
                    if (_exit.IsCompleted) throw new IOException("Native terminal exited before: " + text + "\n" + Text);
                    await Task.Delay(30, deadline.Token);
                }
            }
            catch (OperationCanceledException) { throw new IOException("Timed out waiting for: " + text + "\n" + Text); }
        }
        public async Task SendAsync(string text)
        {
            await _process.Stdin.WriteAsync(Encoding.UTF8.GetBytes(text));
            await _process.Stdin.FlushAsync();
        }
        public async Task StartEditorAsync(string target)
        {
            var before = Count(".editor-session-");
            await SendAsync("config external " + target + "\r");
            await WaitAsync(".editor-session-", before + 1);
        }
        public async Task CommandAsync(string text, bool exits = false)
        {
            var before = Count("nomadnet> ");
            await SendAsync(text + "\r");
            if (!exits) await WaitAsync("nomadnet> ", before + 1);
            else Assert.Equal(0, await _exit.WaitAsync(TimeSpan.FromSeconds(15)));
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
