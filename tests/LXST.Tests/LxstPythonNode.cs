using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DotReticulum.Core;

namespace LXST.Tests;

internal sealed class LxstPythonNode : IAsyncDisposable
{
    private readonly ConcurrentQueue<JsonElement> _events = new();
    private readonly StringBuilder _log = new();
    private readonly string _directory;
    private readonly Process _process;
    private readonly Task[] _pumps;
    public byte[] Hash { get; private set; } = [];
    public Identity Identity { get; private set; } = null!;

    private LxstPythonNode(string directory, Process process)
    {
        _directory = directory;
        _process = process;
        _pumps = [PumpAsync(process.StandardOutput, true), PumpAsync(process.StandardError, false)];
    }

    public static async Task<LxstPythonNode> StartAsync(int port, bool telephone = false, string? pulseServer = null,
        bool transport = false, int connectPort = 0)
    {
        var directory = Path.Combine(ArtifactsDirectory, "lxst-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTRETICULUM_PYTHON") ??
            throw new InvalidOperationException("Set DOTRETICULUM_PYTHON to the isolated Python interpreter."))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var value in new[] { Path.Combine(AppContext.BaseDirectory, "Interop", "live_lxst_node.py"),
            "--config", directory, "--tcp-server-port", port.ToString() }) start.ArgumentList.Add(value);
        if (telephone) start.ArgumentList.Add("--telephone");
        if (transport) start.ArgumentList.Add("--transport");
        if (connectPort != 0)
        {
            start.ArgumentList.Add("--connect-port");
            start.ArgumentList.Add(connectPort.ToString());
        }
        var roots = new List<string>
        {
            Environment.GetEnvironmentVariable("DOTRETICULUM_LXST_UPSTREAM") ?? throw new InvalidOperationException("Set DOTRETICULUM_LXST_UPSTREAM."),
            Environment.GetEnvironmentVariable("DOTRETICULUM_UPSTREAM") ?? throw new InvalidOperationException("Set DOTRETICULUM_UPSTREAM."),
        };
        if (Environment.GetEnvironmentVariable("DOTRETICULUM_LXMF_UPSTREAM") is { Length: > 0 } lxmfRoot)
            roots.Add(lxmfRoot);
        start.Environment["PYTHONPATH"] = string.Join(Path.PathSeparator, roots);
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        if (pulseServer is not null)
        {
            start.Environment["PULSE_SERVER"] = pulseServer;
            start.Environment["DOTLXST_VIRTUAL_PULSE"] = "1";
        }
        var node = new LxstPythonNode(directory, Process.Start(start) ?? throw new InvalidOperationException("Python startup failed."));
        try
        {
            var ready = await node.WaitAsync("ready");
            node.Hash = Convert.FromHexString(ready.GetProperty("dest").GetString()!);
            node.Identity = Identity.FromPublicKey(Convert.FromHexString(ready.GetProperty("public_key").GetString()!));
            return node;
        }
        catch
        {
            await node.DisposeAsync();
            throw;
        }
    }

    public int Count(string name) => _events.Count(value => value.GetProperty("event").GetString() == name);

    public async Task<JsonElement> WaitAsync(string name, int after = 0)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!timeout.IsCancellationRequested)
        {
            var found = _events.Where(value => value.GetProperty("event").GetString() == name).Skip(after).FirstOrDefault();
            if (found.ValueKind != JsonValueKind.Undefined) return found;
            if (_process.HasExited) break;
            await Task.Delay(25);
        }
        string log;
        lock (_log) log = _log.ToString();
        throw new TimeoutException($"Python LXST did not report {name}. {log}");
    }

    public async Task<JsonElement> CommandAsync(string command, string response)
    {
        var before = Count(response);
        await _process.StandardInput.WriteLineAsync(command);
        await _process.StandardInput.FlushAsync();
        return await WaitAsync(response, before);
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
                if (_log.Length > 64_000) _log.Remove(0, _log.Length - 64_000);
            }
        }
    }

    public static string ArtifactsDirectory => Path.Combine(AppContext.BaseDirectory, "artifacts");

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            try { await _process.StandardInput.WriteLineAsync("quit"); await _process.StandardInput.FlushAsync(); }
            catch (IOException) { }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await _process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { _process.Kill(entireProcessTree: true); await _process.WaitForExitAsync(); }
        }
        await Task.WhenAll(_pumps);
        _process.Dispose();
        Identity?.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
