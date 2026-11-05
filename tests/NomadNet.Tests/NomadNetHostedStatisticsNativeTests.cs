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
public sealed class NomadNetHostedStatisticsNativeTests
{
    [HostedStatisticsNativeFact, Trait("Category", "Live"), Trait("TestId", "T960")]
    public async Task T960ActualPrimaryRequestsNativeHostCountAttemptsResetPreservesMediaAndRestartRetainsCounters()
    {
        var root = Path.Combine(Path.GetFullPath(Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_HOST_STATISTICS_EVIDENCE")
            ?? Path.Combine("artifacts", "tests", "nomad-host-statistics")), "native-" + Guid.NewGuid().ToString("N"));
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
            $"[reticulum]\nshare_instance=no\nenable_transport=no\n[logging]\nloglevel=0\n[interfaces]\n[[stats]]\n" +
            $"type=TCPServerInterface\nenabled=yes\nlisten_ip=127.0.0.1\nlisten_port={port}\n");
        await File.WriteAllTextAsync(Path.Combine(primaryRns, "config"),
            $"[reticulum]\nshare_instance=no\nenable_transport=no\n[logging]\nloglevel=0\n[interfaces]\n[[stats]]\n" +
            $"type=TCPClientInterface\nenabled=yes\ntarget_host=127.0.0.1\ntarget_port={port}\n");
        await File.WriteAllTextAsync(Path.Combine(primary, "config"),
            "[client]\nenable_client=yes\nuser_interface=none\nannounce_at_start=no\nperiodic_lxmf_sync=no\n" +
            "[node]\nenable_node=yes\ndisable_propagation=yes\nannounce_at_start=no\n");
        await new NomadNetSettings { EnableNode = true, EnablePropagation = false, NodeAnnounceAtStart = false,
            ClientAnnounceAtStart = false, PeriodicLxmfSync = false, IntroTime = 0, ColorMode = "monochrome",
            Glyphs = "plain", MouseEnabled = false, NodePageRefreshIntervalMinutes = 0, NodeFileRefreshIntervalMinutes = 0 }
            .SaveAsync(profile);
        var pages = Path.Combine(profile, "storage", "pages");
        var files = Path.Combine(profile, "storage", "files");
        Directory.CreateDirectory(pages);
        Directory.CreateDirectory(files);
        foreach (var name in new[] { "ok.mu", "denied.mu", "stale.mu", "ok.webp", "denied.webp" })
            await File.WriteAllBytesAsync(Path.Combine(pages, name), "synthetic hosted payload"u8.ToArray());
        foreach (var name in new[] { "ok.bin", "denied.bin", "stale.bin" })
            await File.WriteAllBytesAsync(Path.Combine(files, name), "synthetic hosted file"u8.ToArray());
        foreach (var path in new[] { Path.Combine(pages, "denied.mu"), Path.Combine(pages, "denied.webp"), Path.Combine(files, "denied.bin") })
            await File.WriteAllTextAsync(path + ".allowed", new string('0', 32));
        var binary = Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_BINARY")!;
        string destination;
        byte[] identity;
        await using (var terminal = new Terminal(binary, profile, rns, Path.Combine(root, "first-terminal.txt")))
        {
            await terminal.WaitAsync("nomadnet> ");
            destination = Regex.Match(terminal.Text, @"Hosted node address: ([0-9a-f]{32})").Groups[1].Value;
            Assert.Equal(32, destination.Length);
            identity = SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(rns, "storage", "nomadnet_identity")));
            await using var peer = await Primary.StartAsync(primary, primaryRns, Path.Combine(root, "first-primary.txt"), expectMediaCounter: false);
            await terminal.CommandAsync("node announce");
            Assert.Equal("bytes", await peer.RequestAsync(destination, "/page/index.mu"));
            await File.WriteAllTextAsync(Path.Combine(pages, "index.mu"), "published index");
            Assert.Equal("bytes", await peer.RequestAsync(destination, "/page/index.mu"));
            await terminal.CommandAsync("node stats");
            Assert.Contains("connections=1 pages=0 files=0 media=0", terminal.LastResponse);
            await terminal.CommandAsync("node refresh");
            Assert.Equal("bytes", await peer.RequestAsync(destination, "/page/index.mu"));
            File.Delete(Path.Combine(pages, "index.mu"));
            Assert.Equal("failed", await peer.RequestAsync(destination, "/page/index.mu"));
            await terminal.CommandAsync("node refresh");
            Assert.Equal("bytes", await peer.RequestAsync(destination, "/page/index.mu"));
            foreach (var name in new[] { "ok.mu", "denied.mu" })
                Assert.Equal("bytes", await peer.RequestAsync(destination, "/page/" + name));
            File.Delete(Path.Combine(pages, "stale.mu"));
            Assert.Equal("failed", await peer.RequestAsync(destination, "/page/stale.mu"));
            foreach (var name in new[] { "ok.bin", "denied.bin" })
                Assert.Equal("bytes", await peer.RequestAsync(destination, "/file/" + name));
            File.Delete(Path.Combine(files, "stale.bin"));
            Assert.Equal("failed", await peer.RequestAsync(destination, "/file/stale.bin"));
            Assert.Equal("bytes", await peer.RequestAsync(destination, "/media", "/media/ok.webp"));
            Assert.Equal("bool", await peer.RequestAsync(destination, "/media", "/media/denied.webp"));
            _ = await peer.RequestAsync(destination, "/media", "/media/missing.webp");
            _ = await peer.RequestAsync(destination, "/media", "/media/not-image.txt");
            _ = await peer.RequestAsync(destination, "/media", "/media/../escape.webp");
            _ = await peer.RequestAsync(destination, "/media");
            await terminal.CommandAsync("node stats");
            Assert.Contains("connections=1 pages=5 files=3 media=3", terminal.LastResponse);
            await terminal.CommandAsync("node stats reset");
            Assert.Contains("connections=0 pages=0 files=0 media=3", terminal.LastResponse);
            Assert.Equal("bytes", await peer.RequestAsync(destination, "/page/ok.mu"));
            await terminal.CommandAsync("node stats");
            Assert.Contains("connections=0 pages=1 files=0 media=3", terminal.LastResponse);
            await peer.QuitAsync();
            await terminal.CommandAsync("quit", exits: true);
        }
        await using (var statistics = NomadNetHostedStatistics.Open(profile))
            Assert.Equal(new(ServedPageRequests: 1, ServedMediaRequests: 3), statistics.Snapshot);
        await using (var terminal = new Terminal(binary, profile, rns, Path.Combine(root, "restart-terminal.txt")))
        {
            await terminal.WaitAsync("nomadnet> ");
            Assert.Contains("Hosted node address: " + destination, terminal.Text);
            await terminal.CommandAsync("node stats");
            Assert.Contains("connections=0 pages=1 files=0 media=3", terminal.LastResponse);
            await using var peer = await Primary.StartAsync(primary, primaryRns, Path.Combine(root, "restart-primary.txt"), expectMediaCounter: true);
            await terminal.CommandAsync("node announce");
            Assert.Equal("bytes", await peer.RequestAsync(destination, "/file/ok.bin"));
            await peer.QuitAsync();
            await terminal.CommandAsync("quit", exits: true);
        }
        Assert.Equal(identity, SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(rns, "storage", "nomadnet_identity"))));
        await using var reopened = NomadNetHostedStatistics.Open(profile);
        Assert.Equal(new(NodeConnects: 1, ServedPageRequests: 1, ServedFileRequests: 1, ServedMediaRequests: 3), reopened.Snapshot);
        await File.WriteAllTextAsync(Path.Combine(root, "accepted-flow.json"), JsonSerializer.Serialize(new
        {
            Test = "T960", Topology = "actual-primary NomadNetworkApp/RNS client -> Windows native hosted node over direct TCP loopback",
            Boundary = "No Urwid Browser, transport hop, loss injection, Linux, physical RF or image decoding claim.",
            Final = reopened.Snapshot, BinarySha256 = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(binary)))
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class HostedStatisticsNativeFactAttribute : FactAttribute
    {
        public HostedStatisticsNativeFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "C107 Windows native ConPTY acceptance; Linux replay is blocked by WSL read-only/EIO.";
            else if (Environment.GetEnvironmentVariable("DOTRETICULUM_LIVE") != "1" ||
                Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_HOST_STATISTICS") != "1")
                Skip = "Enable explicit C107 live acceptance with a frozen native binary and pinned primary environment.";
        }
    }

    private sealed class Primary : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task _output;
        private readonly Task<string> _errors;
        private readonly Channel<string> _events = Channel.CreateBounded<string>(64);
        private readonly StringBuilder _log = new();
        private readonly string _path;
        private bool _exited;
        private Primary(string profile, string rns, string path)
        {
            _path = path;
            var repository = Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_HOST_STATISTICS_ROOT")
                ?? throw new InvalidOperationException("Select the statistics acceptance repository root.");
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTRETICULUM_PYTHON")!)
            { UseShellExecute = false, WorkingDirectory = repository, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var value in new[] { Path.Combine(repository, "tests", "NomadNet.Tests", "nomadnet_host_statistics_peer.py"),
                "--config", profile, "--rnsconfig", rns }) start.ArgumentList.Add(value);
            _process = Process.Start(start) ?? throw new IOException("Could not start actual primary.");
            _errors = _process.StandardError.ReadToEndAsync();
            _output = ReadAsync();
        }
        public static async Task<Primary> StartAsync(string profile, string rns, string path, bool expectMediaCounter)
        {
            var peer = new Primary(profile, rns, path);
            try
            {
                using var ready = await peer.EventAsync();
                Assert.Equal("ready", ready.RootElement.GetProperty("event").GetString());
                Assert.Equal(expectMediaCounter, ready.RootElement.GetProperty("counterPresence").GetProperty("served_media_requests").GetBoolean());
                return peer;
            }
            catch { await peer.DisposeAsync(); throw; }
        }
        private async Task ReadAsync()
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (_log.Length + line.Length > 2_000_000) throw new IOException("Primary statistics evidence exceeds2 MiB.");
                _log.AppendLine(line);
                if (line.StartsWith("@@", StringComparison.Ordinal)) await _events.Writer.WriteAsync(line[2..]);
            }
            _events.Writer.TryComplete();
        }
        private async Task<JsonDocument> EventAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var result = JsonDocument.Parse(await _events.Reader.ReadAsync(timeout.Token));
            if (result.RootElement.GetProperty("event").GetString() == "error")
                throw new IOException(result.RootElement.ToString());
            return result;
        }
        public async Task<string> RequestAsync(string hash, string path, string? media = null)
        {
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            { destination = hash, path, data = media is null ? null : new Dictionary<string, object?> { ["path"] = media, ["key"] = null } }));
            await _process.StandardInput.FlushAsync();
            using var response = await EventAsync();
            Assert.Equal("response", response.RootElement.GetProperty("event").GetString());
            return response.RootElement.GetProperty("kind").GetString()!;
        }
        public async Task QuitAsync()
        {
            await _process.StandardInput.WriteLineAsync("quit");
            await _process.StandardInput.FlushAsync();
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, _process.ExitCode);
            _exited = true;
        }
        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited) { _process.Kill(entireProcessTree: true); await _process.WaitForExitAsync(); }
            try { await _output.WaitAsync(TimeSpan.FromSeconds(5)); }
            finally
            {
                await File.WriteAllTextAsync(_path, _log + "\nSTDERR:\n" + await _errors, new UTF8Encoding(false));
                if (_exited) Assert.Equal(0, _process.ExitCode);
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
        private int _responseStart;
        public string Text { get { lock (_text) return _text.ToString(); } }
        public string LastResponse => Text[_responseStart..].Replace("\r", "", StringComparison.Ordinal);
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
                    if (_text.Length + length > 2_000_000) throw new IOException("Hosted statistics terminal evidence exceeds2 MiB.");
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
                try { await Task.Delay(30, timeout.Token); }
                catch (OperationCanceledException) { throw new IOException("Native prompt timeout:\n" + Text); }
            }
        }
        public async Task CommandAsync(string text, bool exits = false)
        {
            var count = Text.Replace("\u001b[1C", " ", StringComparison.Ordinal).Split("nomadnet> ", StringSplitOptions.None).Length - 1;
            _responseStart = Text.Length;
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
