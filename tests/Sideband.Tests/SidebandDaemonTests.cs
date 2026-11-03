using Sideband.Desktop;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandDaemonTests
{
    [Fact]
    public async Task InteractiveConsoleBoundsCommandsCancelsRepeatedDialsAndExitsAtEof()
    {
        var root = Path.Combine(Path.GetTempPath(), "sideband-console-" + Guid.NewGuid().ToString("N"));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var config = await ConfigAsync(root, stop.Token);
            using var input = new StringReader(new string('x', 8193) + "\nhelp\nvoice enable\nvoice status\n" +
                "voice profile invalid\nvoice dial 0123456789abcdef0123456789abcdef\nvoice hangup\n" +
                "voice dial fedcba9876543210fedcba9876543210\nvoice hangup\nvoice log\n" +
                "voice dial aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\nvoice disable\nvoice log clear\nstatus\n");
            using var output = new StringWriter();
            using var error = new StringWriter();
            var result = await SidebandDaemon.RunAsync(Path.Combine(root, "profile"), config, true, true, true,
                input, output, error, stop.Token);
            Assert.Equal(0, result);
            Assert.Contains("READY identity=", output.ToString());
            Assert.Contains("pinned upstream handlers are placeholders", output.ToString());
            Assert.Contains("STATUS voice=Available", output.ToString());
            Assert.Contains("STATUS voice=disabled", output.ToString());
            Assert.Contains("OK voice log cleared", output.ToString());
            Assert.Equal(3, output.ToString().Split("CANCELLED voice dial").Length - 1);
            Assert.Contains("8,192 characters", error.ToString());
            Assert.Contains("Unsupported voice profile or mode", error.ToString());
            var persisted = await new SidebandStateStore(Path.Combine(root, "profile")).LoadAsync(cancellationToken: stop.Token);
            Assert.False(persisted.VoiceEnabled);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task NoninteractiveDaemonIgnoresClosedStdinAndStopsOnCancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), "sideband-daemon-" + Guid.NewGuid().ToString("N"));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(10));
        using var output = new ReadyWriter();
        using var error = new StringWriter();
        Task<int>? run = null;
        try
        {
            var config = await ConfigAsync(root, stop.Token);
            run = SidebandDaemon.RunAsync(Path.Combine(root, "profile"), config, false, true, false,
                TextReader.Null, output, error, stop.Token);
            var first = await Task.WhenAny(run, output.Ready).WaitAsync(stop.Token);
            if (first == run)
            {
                await run;
                Assert.Fail("The daemon exited before readiness.");
            }
            await Task.Delay(100, stop.Token);
            Assert.False(run.IsCompleted);
            await stop.CancelAsync();
            Assert.Equal(0, await run);
            Assert.Contains("READY identity=", output.ToString());
        }
        finally
        {
            await stop.CancelAsync();
            if (run is not null) await run;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class ReadyWriter : StringWriter
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Ready => _ready.Task;

        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value?.StartsWith("READY identity=", StringComparison.Ordinal) == true)
                _ready.TrySetResult();
        }
    }

    private static async Task<string> ConfigAsync(string root, CancellationToken token)
    {
        var config = Path.Combine(root, "rns");
        Directory.CreateDirectory(config);
        await File.WriteAllTextAsync(Path.Combine(config, "config"),
            "[reticulum]\n  enable_transport = no\n  share_instance = no\n\n[logging]\n  loglevel = 0\n\n[interfaces]\n", token);
        return config;
    }
}
