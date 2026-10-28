using System.Diagnostics;

namespace LXST.Tests;

/// <summary>Isolated generated/silent audio only. Never connects to the desktop audio server.</summary>
internal sealed class VirtualPulseServer : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _errors;
    private readonly string _directory;
    public string Address { get; }

    private VirtualPulseServer(Process process, string directory, string address)
    {
        _process = process;
        _directory = directory;
        Address = address;
        _errors = process.StandardError.ReadToEndAsync();
    }

    public static async Task<VirtualPulseServer> StartAsync()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Virtual PulseAudio evidence currently requires Linux.");
        var directory = Path.Combine(LxstPythonNode.ArtifactsDirectory, "lxst-pa-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var port = LiveStreamTests.FreePort();
        var config = Path.Combine(directory, "pulse.pa");
        await File.WriteAllTextAsync(config,
            $"load-module module-native-protocol-tcp listen=127.0.0.1 port={port} auth-anonymous=yes\n" +
            "load-module module-null-sink sink_name=lxst_output rate=48000 channels=2\n" +
            "load-module module-null-source source_name=lxst_input rate=48000 channels=2\n" +
            "set-default-sink lxst_output\nset-default-source lxst_input\n");
        var start = new ProcessStartInfo("pulseaudio")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-n", "--file=" + config, "--daemonize=no", "--use-pid-file=no",
            "--exit-idle-time=-1", "--disable-shm=yes", "--log-target=stderr" }) start.ArgumentList.Add(argument);
        start.Environment["PULSE_RUNTIME_PATH"] = directory;
        start.Environment["PULSE_STATE_PATH"] = directory;
        start.Environment["XDG_RUNTIME_DIR"] = directory;
        start.Environment["DBUS_SESSION_BUS_ADDRESS"] = "unix:abstract=dotlxst-no-bus-" + Guid.NewGuid().ToString("N");
        start.Environment.Remove("PULSE_SERVER");
        var server = new VirtualPulseServer(Process.Start(start) ?? throw new InvalidOperationException("PulseAudio startup failed."),
            directory, $"tcp:127.0.0.1:{port}");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                if (server._process.HasExited) throw new InvalidOperationException(await server._errors);
                var probe = new ProcessStartInfo("pactl") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { "-s", server.Address, "info" }) probe.ArgumentList.Add(argument);
                using var check = Process.Start(probe)!;
                var output = check.StandardOutput.ReadToEndAsync();
                var errors = check.StandardError.ReadToEndAsync();
                try { await check.WaitForExitAsync(timeout.Token); }
                catch
                {
                    if (!check.HasExited) check.Kill(entireProcessTree: true);
                    await check.WaitForExitAsync(CancellationToken.None);
                    throw;
                }
                _ = await output;
                var diagnostic = await errors;
                if (check.ExitCode == 0) return server;
                if (timeout.IsCancellationRequested) throw new TimeoutException("Virtual PulseAudio was not ready: " + diagnostic);
                await Task.Delay(50, timeout.Token);
            }
        }

        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    public async Task<float[]> RecordOutputAsync(int samples, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("parec")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "--server=" + Address, "--device=lxst_output.monitor",
            "--raw", "--format=float32le", "--rate=48000", "--channels=2", "--latency-msec=10" }) start.ArgumentList.Add(argument);
        using var recording = Process.Start(start) ?? throw new InvalidOperationException("Virtual monitor recording failed.");
        var error = recording.StandardError.ReadToEndAsync();
        try
        {
            var bytes = new byte[samples * sizeof(float)];
            await recording.StandardOutput.BaseStream.ReadExactlyAsync(bytes, cancellationToken);
            var output = new float[samples];
            Buffer.BlockCopy(bytes, 0, output, 0, bytes.Length);
            return output;
        }
        finally
        {
            if (!recording.HasExited) recording.Kill(entireProcessTree: true);
            await recording.WaitForExitAsync(CancellationToken.None);
            _ = await error;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync();
        _ = await _errors;
        _process.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
