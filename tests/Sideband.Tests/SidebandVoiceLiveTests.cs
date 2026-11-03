using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using DotReticulum.Core;
using DotReticulum.Interfaces;
using DotReticulum.Transport;
using LXST;

namespace Sideband.Tests;

public sealed class SidebandVoiceLiveTheoryAttribute : TheoryAttribute
{
    public SidebandVoiceLiveTheoryAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_VOICE_LIVE") != "1")
            Skip = "Requires explicit DOTRETICULUM_SIDEBAND_VOICE_LIVE=1, pinned Python/Sideband/LXST and isolated virtual or synthetic audio.";
    }
}

[Trait("Category", "Integration")]
public sealed class SidebandVoiceLiveTests
{
    [SidebandVoiceLiveTheory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [Trait("Category", "Live")]
    public async Task ActualPrimaryCoreCoordinatesBidirectionalOpusCodec2PttAndRecovery(
        bool pythonOriginates, bool halfDuplex, bool transportHop)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(110));
        var port = FreePort();
        var relayPort = transportHop ? FreePort() : 0;
        await using var relayManager = transportHop ? new PacketInterfaceManager(
            [new TcpServerPacketInterface(new IPEndPoint(IPAddress.Loopback, relayPort), perClientInterfaces: true)]) : null;
        await using var relay = relayManager is null ? null : new TransportRouter(relayManager, new() { TransportEnabled = true });
        if (relayManager is not null) await relayManager.StartAsync(stop.Token);
        var relayRun = relay?.RunAsync(stop.Token) ?? Task.CompletedTask;
        await using var python = await PythonVoice.StartAsync(port, relayPort, stop.Token);
        await using var manager = new PacketInterfaceManager(
            [new TcpClientPacketInterface("127.0.0.1", transportHop ? relayPort : port)]);
        await using var router = new TransportRouter(manager, new());
        using var identity = Identity.Generate();
        await using var voice = new SidebandVoiceService(router, identity, new HeadlessAudioDeviceBackend());
        var errors = new ConcurrentQueue<SidebandVoiceError>();
        var history = new ConcurrentQueue<SidebandVoiceCallEvent>();
        voice.Error += errors.Enqueue;
        voice.CallHistory += history.Enqueue;
        await manager.StartAsync(stop.Token);
        var dispatch = router.RunAsync(stop.Token);
        voice.Start();
        try
        {
            if (!transportHop) await python.CommandAsync("wait-client", "client-ready", stop.Token);
            await python.CommandAsync("announce", "announced", stop.Token);
            await voice.AnnounceAsync(stop.Token);
            await WaitAsync(() => router.HasPath(python.Destination), stop.Token);
            Assert.Equal("d069122af14880e43607aed0b71add4c7806fbe0", python.Ready.GetProperty("sideband_sha").GetString());
            Assert.Equal(Destination.Create(DestinationType.Single, "lxst", ["telephony"], python.Identity).Hash, python.Destination);
            Assert.Equal(Destination.Create(DestinationType.Single, "lxmf", ["delivery"], python.Identity).Hash,
                Convert.FromHexString(python.Ready.GetProperty("lxmf_dest").GetString()!));
            if (transportHop) Assert.True(router.HopsTo(python.Destination) >= 2);
            if (pythonOriginates)
            {
                var path = await python.CommandAsync($"wait-path {identity.HexHash}", "path-ready", stop.Token);
                Assert.Equal(Convert.ToHexStringLower(voice.DestinationHash), path.GetProperty("dest").GetString());
                await voice.ConfigureAsync(new(new() { TrustedOnly = true, DirectPushToTalk = true },
                    [new(python.Identity.HexHash, true, false, true)]), stop.Token);
                await python.CommandAsync($"call {identity.HexHash} 64 {(halfDuplex ? 2 : 1)}", "calling", stop.Token);
                if (!halfDuplex)
                {
                    await WaitAsync(() => voice.Snapshot.State == SidebandVoiceState.Ringing, stop.Token);
                    await voice.AnswerAsync(stop.Token);
                }
            }
            else
            {
                await python.CommandAsync($"trust {identity.HexHash} true true", "configured", stop.Token);
                await python.CommandAsync("policy true true", "configured", stop.Token);
                await voice.CallAsync(python.Identity, mode: halfDuplex ? TelephoneMode.HalfDuplex : TelephoneMode.FullDuplex,
                    cancellationToken: stop.Token);
                if (!halfDuplex)
                {
                    await python.WaitStateAsync(2, stop.Token);
                    await python.CommandAsync("answer", "answered", stop.Token);
                }
            }
            await WaitAsync(() => voice.Snapshot.State == SidebandVoiceState.InCall, stop.Token);
            await python.WaitStateAsync(3, stop.Token);
            if (halfDuplex)
            {
                voice.SetPushToTalk(true);
                await python.CommandAsync("ptt on", "configured", stop.Token);
            }
            await WaitAsync(() => voice.Snapshot.ReceivedFrames >= 5 && voice.Snapshot.TransmittedFrames >= 5, stop.Token);
            var observed = await python.WaitTrafficAsync(stop.Token);
            Assert.Equal(64, observed.GetProperty("stats").GetProperty("profile").GetInt32());
            await voice.SwitchProfileAsync(TelephoneProfile.LowBandwidth, stop.Token);
            var received = voice.Snapshot.ReceivedFrames;
            var transmitted = voice.Snapshot.TransmittedFrames;
            await WaitAsync(() => voice.Snapshot.ReceivedFrames >= received + 3 && voice.Snapshot.TransmittedFrames >= transmitted + 3, stop.Token);
            observed = await python.WaitProfileAsync(48, stop.Token);
            Assert.Contains("Codec2", observed.GetProperty("stats").GetProperty("txcodec").GetString()!);
            await python.CommandAsync("hangup", "hungup", stop.Token);
            await WaitAsync(() => voice.Snapshot.State == SidebandVoiceState.Available, stop.Token);
            Assert.Contains(history, entry => entry.Event == (pythonOriginates ? "incoming-success" : "outgoing-success"));
            Assert.Contains(history, entry => entry.Event == "ongoing-ended");
            var pythonHistory = await python.CommandAsync("history", "history", stop.Token);
            Assert.Contains(pythonHistory.GetProperty("entries").EnumerateArray(),
                entry => entry.GetProperty("event").GetString() == (pythonOriginates ? "outgoing-success" : "incoming-success"));
            // A fresh call after remote hangup verifies the coordinator stays usable.
            await voice.CallAsync(python.Identity, mode: TelephoneMode.HalfDuplex, cancellationToken: stop.Token);
            await WaitAsync(() => voice.Snapshot.State == SidebandVoiceState.Ringing ||
                voice.Snapshot.State == SidebandVoiceState.Connecting || voice.Snapshot.State == SidebandVoiceState.InCall, stop.Token);
            if (pythonOriginates)
            {
                await python.WaitStateAsync(2, stop.Token);
                await python.CommandAsync("answer", "answered", stop.Token);
            }
            await WaitAsync(() => voice.Snapshot.State == SidebandVoiceState.InCall, stop.Token);
            await voice.HangupAsync(stop.Token);
            Assert.Empty(errors);
        }
        finally
        {
            await voice.DisposeAsync();
            await stop.CancelAsync();
            try { await Task.WhenAll(dispatch, relayRun); } catch (OperationCanceledException) { }
        }
    }

    private static async Task WaitAsync(Func<bool> predicate, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        while (!predicate()) await Task.Delay(20, deadline.Token);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    internal sealed class PythonVoice : IAsyncDisposable
    {
        private readonly ConcurrentQueue<JsonElement> _events = new();
        private readonly StringBuilder _log = new();
        private readonly Process _process;
        private readonly Task[] _pumps;
        private readonly string _directory;
        public JsonElement Ready { get; private set; }
        public byte[] Destination { get; private set; } = [];
        public Identity Identity { get; private set; } = null!;

        private PythonVoice(Process process, string directory)
        {
            _process = process;
            _directory = directory;
            _pumps = [PumpAsync(process.StandardOutput, true), PumpAsync(process.StandardError, false)];
        }

        public static async Task<PythonVoice> StartAsync(int port, int relayPort, CancellationToken token)
        {
            var directory = Path.GetFullPath(Path.Combine("artifacts", "tests", "sideband-voice-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(directory);
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTRETICULUM_PYTHON")
                ?? throw new InvalidOperationException("Set DOTRETICULUM_PYTHON to the pinned reference interpreter."))
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var value in new[] { FindHarness(), "--config", directory, "--source",
                Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_SOURCE") ??
                    throw new InvalidOperationException("Set DOTRETICULUM_SIDEBAND_SOURCE to primary Sideband 2.2.0."),
                "--tcp-server-port", port.ToString(), "--connect-port", relayPort.ToString() })
                start.ArgumentList.Add(value);
            if (Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_VOICE_SYNTHETIC") == "1")
                start.ArgumentList.Add("--synthetic-audio");
            var roots = new[] { Environment.GetEnvironmentVariable("DOTRETICULUM_UPSTREAM"),
                Environment.GetEnvironmentVariable("DOTRETICULUM_LXST_UPSTREAM"), Environment.GetEnvironmentVariable("DOTRETICULUM_LXMF_UPSTREAM") };
            if (roots[0] is null || roots[1] is null) throw new InvalidOperationException("Set pinned RNS and LXST checkout paths.");
            start.Environment["PYTHONPATH"] = string.Join(Path.PathSeparator, roots.Where(value => !string.IsNullOrEmpty(value)));
            start.Environment["PYTHONIOENCODING"] = "utf-8";
            var node = new PythonVoice(Process.Start(start) ?? throw new IOException("Could not start actual Sideband voice."), directory);
            try
            {
                node.Ready = await node.WaitAsync("ready", 0, token);
                node.Destination = Convert.FromHexString(node.Ready.GetProperty("dest").GetString()!);
                node.Identity = Identity.FromPublicKey(Convert.FromHexString(node.Ready.GetProperty("public_key").GetString()!));
                return node;
            }
            catch { await node.DisposeAsync(); throw; }
        }

        public async Task<JsonElement> CommandAsync(string command, string response, CancellationToken token)
        {
            var before = _events.Count(value => value.GetProperty("event").GetString() == response);
            await _process.StandardInput.WriteLineAsync(command.AsMemory(), token);
            await _process.StandardInput.FlushAsync(token);
            return await WaitAsync(response, before, token);
        }

        public async Task<JsonElement> WaitStateAsync(int state, CancellationToken token)
        {
            while (true)
            {
                var result = await CommandAsync("state", "state", token);
                if (result.GetProperty("value").GetInt32() == state) return result;
                await Task.Delay(25, token);
            }
        }

        public async Task<JsonElement> WaitTrafficAsync(CancellationToken token)
        {
            while (true)
            {
                var result = await CommandAsync("state", "state", token);
                var stats = result.GetProperty("stats");
                if (stats.GetProperty("rxb").GetInt64() > 0 && stats.GetProperty("txb").GetInt64() > 0) return result;
                await Task.Delay(25, token);
            }
        }

        public async Task<JsonElement> WaitProfileAsync(int profile, CancellationToken token)
        {
            while (true)
            {
                var result = await CommandAsync("state", "state", token);
                if (result.GetProperty("profile").GetInt32() == profile) return result;
                await Task.Delay(25, token);
            }
        }

        private async Task<JsonElement> WaitAsync(string name, int after, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            while (!timeout.IsCancellationRequested)
            {
                var result = _events.Where(value => value.GetProperty("event").GetString() == name).Skip(after).FirstOrDefault();
                if (result.ValueKind != JsonValueKind.Undefined) return result;
                if (_process.HasExited || _events.Any(value => value.GetProperty("event").GetString() == "error")) break;
                try { await Task.Delay(25, timeout.Token); } catch (OperationCanceledException) when (timeout.IsCancellationRequested) { break; }
            }
            string log;
            lock (_log) log = _log.ToString();
            throw new TimeoutException($"Actual Sideband voice did not report {name}: {log}");
        }

        private async Task PumpAsync(StreamReader reader, bool events)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (events && line.StartsWith("@@", StringComparison.Ordinal))
                {
                    using var document = JsonDocument.Parse(line.AsSpan(2).ToString());
                    _events.Enqueue(document.RootElement.Clone());
                }
                lock (_log)
                {
                    _log.AppendLine(line);
                    if (_log.Length > 64000) _log.Remove(0, _log.Length - 64000);
                }
            }
        }

        private static string FindHarness([CallerFilePath] string sourceFile = "")
        {
            if (Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_VOICE_HARNESS") is { Length: > 0 } path) return path;
            var deployed = Path.Combine(AppContext.BaseDirectory, "Interop", "sideband_voice_node.py");
            if (File.Exists(deployed)) return deployed;
            foreach (var root in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory(), Path.GetDirectoryName(sourceFile)! })
                for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
                {
                    var candidate = Path.Combine(directory.FullName, "tests", "Interop", "sideband_voice_node.py");
                    if (File.Exists(candidate)) return candidate;
                }
            throw new FileNotFoundException("Set DOTRETICULUM_SIDEBAND_VOICE_HARNESS to tests/Interop/sideband_voice_node.py.");
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                try { await _process.StandardInput.WriteLineAsync("quit"); await _process.StandardInput.FlushAsync(); } catch (IOException) { }
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try { await _process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { _process.Kill(entireProcessTree: true); await _process.WaitForExitAsync(); }
            }
            await Task.WhenAll(_pumps);
            _process.Dispose();
            Identity?.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}
