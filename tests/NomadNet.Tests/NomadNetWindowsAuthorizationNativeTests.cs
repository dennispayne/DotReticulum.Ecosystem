using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using DotReticulum.Applications.Rnsh;

namespace NomadNet.Tests;

[Trait("Category", "Integration")]
public sealed class NomadNetWindowsAuthorizationNativeTests
{
    [AuthorizationNativeFact, Trait("Category", "Live"), Trait("TestId", "T1065")]
    public async Task PublishedHostAuthorizesActualPrimaryWithPeOptInRecoveryAndRestart()
    {
        var root = Path.Combine(Path.GetFullPath(Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_AUTHORIZATION_EVIDENCE")
            ?? throw new InvalidOperationException("Select the owned authorization evidence root.")), "native-" + Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(root, "native");
        var rns = Path.Combine(root, "native-rns");
        var primary = Path.Combine(root, "primary");
        var primaryRns = Path.Combine(root, "primary-rns");
        foreach (var directory in new[] { profile, rns, primary, primaryRns }) Directory.CreateDirectory(directory);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await File.WriteAllTextAsync(Path.Combine(rns, "config"),
            $"[reticulum]\nshare_instance=no\nenable_transport=no\n[logging]\nloglevel=0\n[interfaces]\n[[authorization]]\n" +
            $"type=TCPServerInterface\nenabled=yes\nlisten_ip=127.0.0.1\nlisten_port={port}\n");
        await File.WriteAllTextAsync(Path.Combine(primaryRns, "config"),
            $"[reticulum]\nshare_instance=no\nenable_transport=no\n[logging]\nloglevel=0\n[interfaces]\n[[authorization]]\n" +
            $"type=TCPClientInterface\nenabled=yes\ntarget_host=127.0.0.1\ntarget_port={port}\n");
        await File.WriteAllTextAsync(Path.Combine(primary, "config"),
            "[client]\nenable_client=yes\nuser_interface=none\nannounce_at_start=no\nperiodic_lxmf_sync=no\n" +
            "[node]\nenable_node=yes\ndisable_propagation=yes\nannounce_at_start=no\n");
        var settings = new NomadNetSettings { EnableNode = true, EnablePropagation = false, NodeAnnounceAtStart = false,
            ClientAnnounceAtStart = false, PeriodicLxmfSync = false, IntroTime = 0, ColorMode = "monochrome",
            Glyphs = "plain", MouseEnabled = false, NodePageRefreshIntervalMinutes = 0, NodeFileRefreshIntervalMinutes = 0 };
        await settings.SaveAsync(profile);
        var pages = Path.Combine(profile, "storage", "pages");
        Directory.CreateDirectory(pages);
        var page = Path.Combine(pages, "private.mu");
        var generator = page + ".allowed";
        const string payload = "authorization synthetic private payload";
        await File.WriteAllTextAsync(page, payload);
        var fixture = Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_AUTHORIZATION_GENERATOR")!;
        File.Copy(fixture, generator);
        var binary = Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_BINARY")!;
        var accepted = new List<string>();
        string destination;
        string identity;
        await using (var peer = new Peer(primary, primaryRns, Path.Combine(root, "primary.txt")))
        {
            identity = await peer.ReadyAsync();
            await File.WriteAllTextAsync(generator + ".input", "allow\n" + identity + "\n");
            await using var terminal = new Terminal(binary, profile, rns, Path.Combine(root, "disabled-terminal.txt"));
            await terminal.WaitAsync("nomadnet> ");
            destination = Address(terminal.Text);
            await terminal.CommandAsync("node announce");
            Assert.Equal(DeniedHash, await peer.RequestAsync(destination, "/page/private.mu"));
            Assert.False(File.Exists(generator + ".pid"));
            accepted.Add("disabled PE denied without execution");
            await File.WriteAllTextAsync(generator, identity + "\n");
            Assert.Equal(Hash(Encoding.UTF8.GetBytes(payload)), await peer.RequestAsync(destination, "/page/private.mu"));
            accepted.Add("static list works with execution disabled");
            await peer.QuitAsync();
            await terminal.CommandAsync("quit", exits: true);
        }
        File.Copy(fixture, generator, overwrite: true);
        await (settings with { EnableExecutablePages = true }).SaveAsync(profile);
        await using (var terminal = new Terminal(binary, profile, rns, Path.Combine(root, "enabled-terminal.txt")))
        {
            await terminal.WaitAsync("nomadnet> ");
            Assert.Equal(destination, Address(terminal.Text));
            await terminal.CommandAsync("node announce");
            await using var peer = new Peer(primary, primaryRns, Path.Combine(root, "primary-restarted.txt"));
            Assert.Equal(identity, await peer.ReadyAsync());
            accepted.Add("native destination and primary identity persist across clean process restart");
            foreach (var mode in new[] { "allow", "mixed", "cr", "nonzero", "stderr" })
            {
                await File.WriteAllTextAsync(generator + ".input", mode + "\n" + identity + "\n");
                Assert.Equal(Hash(Encoding.UTF8.GetBytes(payload)), await peer.RequestAsync(destination, "/page/private.mu"));
                accepted.Add(mode + " PE allowed actual primary identity");
            }
            foreach (var mode in new[] { "malformed", "overflow" })
            {
                await File.WriteAllTextAsync(generator + ".input", mode + "\n" + identity + "\n");
                Assert.Equal(DeniedHash, await peer.RequestAsync(destination, "/page/private.mu"));
                accepted.Add(mode + " denied");
            }
            await File.WriteAllTextAsync(generator + ".input", "allow\n" + new string('0', 32) + "\n");
            Assert.Equal(DeniedHash, await peer.RequestAsync(destination, "/page/private.mu"));
            accepted.Add("outsider denied");
            await File.WriteAllTextAsync(generator + ".input", "allow\n" + identity + "\n");
            Assert.Equal(Hash(Encoding.UTF8.GetBytes(payload)), await peer.RequestAsync(destination, "/page/private.mu"));
            accepted.Add("healthy generator after failures");
            File.Delete(generator);
            File.Copy(fixture, page, overwrite: true);
            Assert.True(new FileInfo(page).Length > MicronText.MaximumPageBytes);
            Assert.Equal("failed", await peer.RequestAsync(destination, "/page/private.mu", expectedKind: "failed"));
            Assert.False(File.Exists(page + ".pid"));
            accepted.Add("oversize PE page is bounded static content, fails without execution");
            await peer.QuitAsync();
            await terminal.CommandAsync("quit", exits: true);
        }
        await File.WriteAllTextAsync(Path.Combine(root, "accepted-flow.json"), JsonSerializer.Serialize(new
        {
            BinarySha256 = Hash(await File.ReadAllBytesAsync(binary)), FixtureSha256 = Hash(await File.ReadAllBytesAsync(fixture)),
            Accepted = accepted, Topology = "actual pinned primary NomadNetworkApp/RNS client -> Windows NativeAOT host over direct loopback TCP",
            Boundary = "No transport hop, loss, Linux execution, hardware or new UI claim. Managed tests separately verify deadlines/cancellation/job descendants."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Address(string text)
    {
        var address = Regex.Match(text, @"Hosted node address: ([0-9a-f]{32})").Groups[1].Value;
        Assert.Equal(32, address.Length);
        return address;
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string DeniedHash => Hash(">Request Not Allowed\n\nThis identity is not authorised for this resource.\n"u8.ToArray());

    private sealed class AuthorizationNativeFactAttribute : FactAttribute
    {
        public AuthorizationNativeFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Windows native PE-generator acceptance; Linux replay is blocked by WSL read-only/EIO.";
            else if (Environment.GetEnvironmentVariable("DOTRETICULUM_LIVE") != "1" ||
                Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_WINDOWS_AUTHORIZATION") != "1")
                Skip = "Enable DOTRETICULUM_LIVE=1 and DOTRETICULUM_NOMADNET_WINDOWS_AUTHORIZATION=1 with frozen native/PE and pinned primary inputs.";
        }
    }

    private sealed class Peer : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task _output;
        private readonly Task<string> _errors;
        private readonly Channel<string> _events = Channel.CreateBounded<string>(32);
        private readonly StringBuilder _log = new();
        private readonly string _path;
        public Peer(string profile, string rns, string path)
        {
            _path = path;
            var repository = Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_AUTHORIZATION_ROOT")
                ?? throw new InvalidOperationException("Select the authorization acceptance repository root.");
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTRETICULUM_PYTHON")!)
            { UseShellExecute = false, WorkingDirectory = repository, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { Path.Combine(repository, "tests", "NomadNet.Tests", "nomadnet_host_statistics_peer.py"),
                "--config", profile, "--rnsconfig", rns }) start.ArgumentList.Add(argument);
            _process = Process.Start(start) ?? throw new IOException("Actual primary failed to start.");
            _errors = _process.StandardError.ReadToEndAsync();
            _output = ReadAsync();
        }
        private async Task ReadAsync()
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (_log.Length + line.Length > 2_000_000) throw new IOException("Primary evidence exceeds its bound.");
                _log.AppendLine(line);
                if (line.StartsWith("@@", StringComparison.Ordinal)) await _events.Writer.WriteAsync(line[2..]);
            }
            _events.Writer.TryComplete(new IOException("Primary output closed."));
        }
        private async Task<JsonDocument> EventAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var result = JsonDocument.Parse(await _events.Reader.ReadAsync(timeout.Token));
            Assert.NotEqual("error", result.RootElement.GetProperty("event").GetString());
            return result;
        }
        public async Task<string> ReadyAsync()
        {
            using var result = await EventAsync();
            Assert.Equal("ready", result.RootElement.GetProperty("event").GetString());
            return result.RootElement.GetProperty("identity").GetString()!;
        }
        public async Task<string> RequestAsync(string destination, string path, string expectedKind = "bytes")
        {
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { destination, path }));
            await _process.StandardInput.FlushAsync();
            using var response = await EventAsync();
            Assert.Equal("response", response.RootElement.GetProperty("event").GetString());
            var kind = response.RootElement.GetProperty("kind").GetString();
            Assert.Equal(expectedKind, kind);
            return expectedKind == "bytes" ? response.RootElement.GetProperty("sha256").GetString()! : kind!;
        }
        public async Task QuitAsync()
        {
            await _process.StandardInput.WriteLineAsync("quit");
            await _process.StandardInput.FlushAsync();
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, _process.ExitCode);
        }
        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited) { _process.Kill(entireProcessTree: true); await _process.WaitForExitAsync(); }
            try { await _output.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally
            {
                await File.WriteAllTextAsync(_path, _log + "\nSTDERR:\n" + await _errors, new UTF8Encoding(false));
                _process.Dispose();
            }
        }
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
                    if (_text.Length + length > 2_000_000) throw new IOException("Native terminal evidence exceeds its bound.");
                    _text.Append(buffer, 0, length);
                }
        }
        public async Task WaitAsync(string text, int count = 1)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            while (Text.Replace("\u001b[1C", " ", StringComparison.Ordinal).Split(text, StringSplitOptions.None).Length - 1 < count)
            {
                if (_output.IsFaulted) await _output;
                if (_exit.IsCompleted) throw new IOException("Native process exited before " + text + "\n" + Text);
                await Task.Delay(30, timeout.Token);
            }
        }
        public async Task CommandAsync(string text, bool exits = false)
        {
            var count = Text.Replace("\u001b[1C", " ", StringComparison.Ordinal).Split("nomadnet> ", StringSplitOptions.None).Length - 1;
            await _process.Stdin.WriteAsync(Encoding.UTF8.GetBytes(text + "\r"));
            await _process.Stdin.FlushAsync();
            if (!exits) await WaitAsync("nomadnet> ", count + 1);
            else
            {
                Assert.Equal(0, await _exit.WaitAsync(TimeSpan.FromSeconds(20)));
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
