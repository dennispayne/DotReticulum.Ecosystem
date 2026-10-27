using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DotLXMF.Tests.Interop;

/// <summary>A real upstream Python RNS/LXMF node (a harness under tests/Interop) driven over stdin/stdout.</summary>
public sealed class PyLinkNode : IAsyncDisposable
{
    private readonly ConcurrentQueue<JsonElement> _events = new();
    private readonly StringBuilder _log = new();
    private readonly TaskCompletionSource<JsonElement> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _configDir = Path.Combine(AppContext.BaseDirectory, "live-rns-" + Guid.NewGuid().ToString("N"));
    private readonly List<Task> _pumps = [];
    private Process _process = null!;

    public string Name { get; }
    public byte[] Hash { get; private set; } = [];
    public string HashHex => Convert.ToHexStringLower(Hash);
    public string Log { get { lock (_log) return _log.ToString(); } }

    private PyLinkNode(string name) => Name = name;

    /// <summary>Starts a node; pass <paramref name="tcpServerPort"/> to let .NET connect to it, and/or <paramref name="rnsdPort"/> to connect it to a .NET rnsd.</summary>
    public static async Task<PyLinkNode> StartAsync(string name, int tcpServerPort = 0, int rnsdPort = 0, string harness = "live_link_node.py",
        string[]? extraArguments = null)
    {
        var node = new PyLinkNode(name);
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTRETICULUM_PYTHON") ?? "python")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { FindHarness(harness), "--config", node._configDir, "--tcp-server-port", tcpServerPort.ToString(), "--rnsd-port", rnsdPort.ToString() })
        {
            psi.ArgumentList.Add(a);
        }
        foreach (var argument in extraArguments ?? []) psi.ArgumentList.Add(argument);

        var upstream = Environment.GetEnvironmentVariable("DOTRETICULUM_UPSTREAM")
            ?? throw new InvalidOperationException("DOTRETICULUM_UPSTREAM must point at the pinned upstream checkout.");
        var lxmf = Environment.GetEnvironmentVariable("DOTRETICULUM_LXMF_UPSTREAM");
        psi.Environment["PYTHONPATH"] = string.IsNullOrEmpty(lxmf)
            ? upstream : string.Join(Path.PathSeparator, upstream, lxmf);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        node._process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start python.");
        node._pumps.Add(Task.Run(node.PumpStdout));
        node._pumps.Add(Task.Run(node.PumpStderr));
        try
        {
            var ready = await node._ready.Task.WaitAsync(TimeSpan.FromSeconds(60));
            node.Hash = Convert.FromHexString(ready.GetProperty("dest").GetString()!);
            return node;
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or FormatException)
        {
            await node.DisposeAsync();
            throw new InvalidOperationException($"Python node {name} startup failed:\n{node.Log}", ex);
        }
    }

    public int Count(string eventName) => _events.Count(e => e.GetProperty("event").GetString() == eventName);

    public List<JsonElement> Events(string eventName) => [.. _events.Where(e => e.GetProperty("event").GetString() == eventName)];

    /// <summary>Waits for the (<paramref name="after"/>+1)-th event of the given type.</summary>
    public async Task<JsonElement> NextAsync(string eventName, int after, TimeSpan timeout, Func<JsonElement, bool>? where = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var found = _events.Where(e => e.GetProperty("event").GetString() == eventName && (where?.Invoke(e) ?? true)).ToList();
            if (found.Count > after) return found[after];
            await Task.Delay(25);
        }

        throw new TimeoutException($"Python node {Name}: no '{eventName}' event in time. Output:\n{Log}");
    }

    public Task<JsonElement> WaitForAnnounce(byte[] hash, TimeSpan timeout) =>
        NextAsync("announce", 0, timeout, e => e.GetProperty("dest").GetString() == Convert.ToHexStringLower(hash));

    public async Task SendLineAsync(string line)
    {
        await _process.StandardInput.WriteLineAsync(line);
        await _process.StandardInput.FlushAsync();
    }

    /// <summary>Sends a command and waits for the next event of <paramref name="expectedEvent"/> produced after it.</summary>
    public async Task<JsonElement> Command(string line, string expectedEvent, TimeSpan timeout)
    {
        var before = Count(expectedEvent);
        await SendLineAsync(line);
        return await NextAsync(expectedEvent, before, timeout);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                try { await _process.StandardInput.WriteLineAsync("quit"); _process.StandardInput.Close(); }
                catch (IOException) { }
                if (!_process.WaitForExit(10_000)) _process.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            foreach (var t in _pumps)
            {
                try { await t.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch { }
            }

            _process.Dispose();
            if (Directory.Exists(_configDir)) Directory.Delete(_configDir, recursive: true);
        }
    }

    private static string FindHarness(string harness)
    {
        var deployed = Path.Combine(AppContext.BaseDirectory, "Interop", harness);
        if (File.Exists(deployed)) return deployed;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "Interop", harness);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException($"tests/Interop/{harness} not found above the test output directory.");
    }

    private async Task PumpStdout()
    {
        string? line;
        while ((line = await _process.StandardOutput.ReadLineAsync()) is not null)
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse(line[2..]);
                var root = doc.RootElement.Clone();
                if (root.GetProperty("event").GetString() == "ready") _ready.TrySetResult(root);
                _events.Enqueue(root);
            }

            lock (_log) _log.AppendLine(line);
        }

        _ready.TrySetException(new InvalidOperationException($"Python node {Name} exited before ready. Output:\n{Log}"));
    }

    private async Task PumpStderr()
    {
        string? line;
        while ((line = await _process.StandardError.ReadLineAsync()) is not null)
        {
            lock (_log) _log.AppendLine("[stderr] " + line);
        }
    }
}
