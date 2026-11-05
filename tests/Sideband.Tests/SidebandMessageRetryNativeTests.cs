using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using LXMF.Tests.Interop;
using Sideband;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandMessageRetryNativeTests
{
    public static bool Disabled => Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_MESSAGE_RETRY") != "1";

    [Fact(SkipWhen = nameof(Disabled), Skip = "Requires explicit native retry opt-in, selected executable/hash, pinned actual primary paths and live opt-ins.")]
    [Trait("Category", "Live")]
    [Trait("TestId", "T417")]
    public async Task SelectedNativeRetriesFailedTextIntoActualPrimaryWithReturnDeliveryAndIdentityPreservingRestart()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("DOTRETICULUM_LIVE"));
        Assert.Equal("1", Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_LIVE"));
        var binary = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_BINARY")
            ?? throw new InvalidOperationException("Select the exact native executable.");
        var expectedHash = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_MESSAGE_RETRY_SHA256");
        Assert.Equal(expectedHash, Convert.ToHexStringLower(SHA256.HashData(
            await File.ReadAllBytesAsync(binary, TestContext.Current.CancellationToken))));
        var source = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_UPSTREAM")
            ?? throw new InvalidOperationException("Select the exact primary checkout.");
        var results = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_MESSAGE_RETRY_RESULTS")
            ?? throw new InvalidOperationException("Select a persistent synthetic acceptance results directory.");
        var root = Path.Combine(results, "native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromMinutes(3));
        var token = stop.Token;
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        await using var python = await PyLinkNode.StartAsync("NativeRetryPrimary", tcpServerPort: port,
            harness: "sideband_console_node.py", extraArguments: ["--source", source]);
        var config = Path.Combine(root, "rns");
        Directory.CreateDirectory(config);
        await File.WriteAllTextAsync(Path.Combine(config, "config"),
            "[reticulum]\n share_instance = no\n enable_transport = no\n[logging]\n loglevel = 0\n[interfaces]\n" +
            $" [[Primary retry]]\n type = TCPClientInterface\n enabled = yes\n target_host = 127.0.0.1\n target_port = {port}\n", token);
        var store = new SidebandStateStore(Path.Combine(root, "data"));
        var original = SidebandMessageRetryTests.Failed(python.HashHex);
        var blob = Encoding.UTF8.GetBytes("Synthetic media must not be reattached by text retry");
        var blobHash = Convert.ToHexStringLower(SHA256.HashData(blob));
        Directory.CreateDirectory(Path.Combine(store.DataDirectory, "attachments"));
        var blobPath = Path.Combine(store.DataDirectory, "attachments", blobHash);
        await File.WriteAllBytesAsync(blobPath, blob, token);
        await store.AddMessageAsync(original with { Attachments = [new("synthetic.txt", blobHash, blob.Length)] }, token);
        string own;
        await using (var native = new NativeRetryConsole(binary, store.DataDirectory, config, Path.Combine(root, "first.log")))
        {
            var ready = await native.WaitAsync("READY identity=", token);
            own = ready.Split("delivery=", StringSplitOptions.None)[1].Split(' ')[0];
            await native.SendAsync("conv announce", token);
            await native.WaitAsync("LXMF announce sent.", token);
            await python.WaitForAnnounce(Convert.FromHexString(own), TimeSpan.FromSeconds(30));
            await Primary("conv announce");
            await native.SendAsync($"conv retry-text {python.HashHex} {original.Id}", token);
            await native.WaitAsync("Message retry queued new-id=", token);
            await MessageRetryPeer.UntilAsync(async () => (await Snapshot()).GetProperty("conversations").EnumerateArray()
                .SelectMany(item => item.GetProperty("messages").EnumerateArray()).Any(item =>
                    Encoding.UTF8.GetString(Convert.FromBase64String(item.GetProperty("content").GetString()!)) == original.Content), token);
            await MessageRetryPeer.UntilAsync(async () => (await new SidebandStateStore(store.DataDirectory)
                .LoadAsync(cancellationToken: token)).Messages.Any(message => message.DeliveryState == "Delivered"), token);
            var saved = await new SidebandStateStore(store.DataDirectory).LoadAsync(cancellationToken: token);
            var replacement = Assert.Single(saved.Messages);
            Assert.NotEqual(original.Id, replacement.Id);
            Assert.Equal(original.Content, replacement.Content);
            Assert.Equal("", replacement.Title);
            Assert.Null(replacement.Attachments);
            await native.SendAsync($"conv retry-text {python.HashHex} {original.Id}", token);
            await native.WaitAsync("Select an outgoing failed non-paper message", token);
            await Primary($"conv send {own} 'primary reply after text retry'");
            await WaitIncoming("primary reply after text retry");
            await native.SendAsync("status", token);
            await native.WaitAsync("LXMF router: active", token);
            await native.QuitAsync(token);
        }
        await using (var native = new NativeRetryConsole(binary, store.DataDirectory, config, Path.Combine(root, "restart.log")))
        {
            var ready = await native.WaitAsync("READY identity=", token);
            Assert.Contains("delivery=" + own, ready);
            await Primary($"conv send {own} 'primary reply after restart'");
            await WaitIncoming("primary reply after restart");
            await native.QuitAsync(token);
        }
        Assert.Equal(blob, await File.ReadAllBytesAsync(blobPath, token));
        var final = await new SidebandStateStore(store.DataDirectory).LoadAsync(cancellationToken: token);
        Assert.Equal(own, final.OwnDeliveryHash);
        Assert.Equal(3, final.Messages.Count);
        Assert.DoesNotContain(final.Messages, message => message.Id == original.Id);
        Assert.Single((await Snapshot()).GetProperty("conversations").EnumerateArray()
            .SelectMany(item => item.GetProperty("messages").EnumerateArray()), item =>
                Encoding.UTF8.GetString(Convert.FromBase64String(item.GetProperty("content").GetString()!)) == original.Content);
        Assert.Equal(expectedHash, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(binary, token))));

        async Task Primary(string text)
        {
            var result = await python.Command("cli " + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)), "console", TimeSpan.FromSeconds(15));
            await File.AppendAllTextAsync(Path.Combine(root, "primary-commands.jsonl"), result.GetRawText() + "\n", token);
            Assert.Equal(System.Text.Json.JsonValueKind.Null, result.GetProperty("error").ValueKind);
            Assert.DoesNotContain("Unknown destination", result.GetProperty("result").GetString() ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain("Failed to", result.GetProperty("result").GetString() ?? "", StringComparison.Ordinal);
        }
        async Task<System.Text.Json.JsonElement> Snapshot()
        {
            var result = await python.Command("snapshot", "snapshot", TimeSpan.FromSeconds(10));
            await File.WriteAllTextAsync(Path.Combine(root, "primary-snapshot.json"), result.GetRawText(), token);
            return result;
        }
        Task WaitIncoming(string text) => MessageRetryPeer.UntilAsync(async () =>
        {
            _ = await Snapshot();
            return (await new SidebandStateStore(store.DataDirectory).LoadAsync(cancellationToken: token))
                .Messages.Any(message => message.Incoming && message.Content == text);
        }, token);
    }

    internal sealed class NativeRetryConsole : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly ConcurrentQueue<string> _lines = new();
        private readonly Task _stdout;
        private readonly Task _stderr;
        private readonly string _log;

        public NativeRetryConsole(string binary, string profile, string config, string log)
        {
            _log = log;
            var start = new ProcessStartInfo(binary)
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            foreach (var argument in new[] { "--daemon", "--interactive", "--synthetic-audio", "--config", profile, "--rnsconfig", config })
                start.ArgumentList.Add(argument);
            _process = Process.Start(start) ?? throw new InvalidOperationException("Native retry console did not start.");
            _stdout = ReadAsync(_process.StandardOutput);
            _stderr = ReadAsync(_process.StandardError);
        }

        private async Task ReadAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (_lines.Count >= 20_000 || line.Length > 65_536) throw new InvalidDataException("Native console output exceeded the test bound.");
                _lines.Enqueue(line);
            }
        }

        public async Task SendAsync(string text, CancellationToken token)
        {
            await _process.StandardInput.WriteLineAsync(text.AsMemory(), token);
            await _process.StandardInput.FlushAsync(token);
        }

        public bool ContainsLine(string text) => _lines.Any(line => line.Contains(text, StringComparison.Ordinal));

        public async Task<string> WaitAsync(string text, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            while (true)
            {
                if (_stdout.IsFaulted) await _stdout;
                if (_stderr.IsFaulted) await _stderr;
                if (_lines.FirstOrDefault(line => line.Contains(text, StringComparison.Ordinal)) is { } found) return found;
                if (_process.HasExited) throw new InvalidOperationException("Native exited before " + text + "\n" + string.Join('\n', _lines));
                await Task.Delay(20, timeout.Token);
            }
        }

        public async Task WaitForOccurrencesAsync(string text, int count, CancellationToken token)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            while (_lines.Count(line => line.Contains(text, StringComparison.Ordinal)) < count)
            {
                if (_stdout.IsFaulted) await _stdout;
                if (_stderr.IsFaulted) await _stderr;
                if (_process.HasExited) throw new InvalidOperationException("Native exited before repeated " + text + "\n" + string.Join('\n', _lines));
                await Task.Delay(20, timeout.Token);
            }
        }

        public async Task QuitAsync(CancellationToken token)
        {
            await SendAsync("quit", token);
            await _process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(15), token);
            Assert.Equal(0, _process.ExitCode);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
                await Task.WhenAll(_stdout, _stderr);
            }
            finally
            {
                await File.WriteAllLinesAsync(_log, _lines);
                _process.Dispose();
            }
        }
    }
}
