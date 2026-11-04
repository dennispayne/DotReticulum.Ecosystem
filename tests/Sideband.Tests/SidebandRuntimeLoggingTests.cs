using System.Collections.Concurrent;
using System.Text.Json;
using Sideband;
using Sideband.Desktop;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandRuntimeLoggingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("TestId", "T377")]
    public async Task ActualDaemonQuietAndVerboseStartupFollowExecutedPrimarySelection(bool verbose)
    {
        var root = Root();
        var token = TestContext.Current.CancellationToken;
        try
        {
            using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-cli.json"), token));
            Assert.Equal("8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25",
                fixture.RootElement.GetProperty("log_startup_source_sha256").GetString());
            var cases = fixture.RootElement.GetProperty("log_startup_cases").EnumerateArray().ToArray();
            var expected = cases.Single(item => item.GetProperty("quiet").GetBoolean() == !verbose &&
                item.GetProperty("verbose").GetBoolean() == verbose).GetProperty("level").GetInt32();
            await using (var client = new SidebandClient(new(Path.Combine(root, "unconnected"))))
                Assert.Equal(cases.Single(item => !item.GetProperty("quiet").GetBoolean() &&
                    !item.GetProperty("verbose").GetBoolean()).GetProperty("level").GetInt32(), client.RuntimeLogLevel);
            var config = Path.Combine(root, "rns");
            Directory.CreateDirectory(config);
            await File.WriteAllTextAsync(Path.Combine(config, "config"), "[reticulum]\n share_instance = no\n[interfaces]\n", token);
            using var input = new StringReader("log\nquit\n");
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(0, await SidebandDaemon.RunAsync(Path.Combine(root, "profile"), config,
                interactive: true, syntheticAudio: true, verbose, input, output, error, token));
            Assert.Contains($"Current log level: {expected} (", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("ERROR", error.ToString(), StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("TestId", "T373")]
    public async Task LogCommandsMatchExecutedPrimaryLevelsLabelsAndRejectedInputsWithoutChangingState()
    {
        var root = Root();
        var token = TestContext.Current.CancellationToken;
        try
        {
            using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-cli.json"), token));
            Assert.Equal("d069122af14880e43607aed0b71add4c7806fbe0", fixture.RootElement.GetProperty("sideband_sha").GetString());
            var store = new SidebandStateStore(root);
            await using var client = new SidebandClient(store);
            var output = new ConcurrentQueue<string>();
            await using var console = new SidebandConsole(client, store, output.Enqueue, output.Enqueue, token);
            foreach (var item in fixture.RootElement.GetProperty("log_cases").EnumerateArray())
            {
                client.RuntimeLogLevel = 3;
                output.Clear();
                var args = item.GetProperty("arguments").EnumerateArray().Select(value => value.GetString()!).ToArray();
                var invocation = SidebandConsoleSyntax.Resolve(["log", .. args])!;
                var expected = item.GetProperty("result").GetString();
                if (expected!.StartsWith("Invalid", StringComparison.Ordinal))
                    Assert.Equal(expected, (await Assert.ThrowsAsync<ArgumentException>(() => console.ExecuteAsync(invocation, token))).Message);
                else
                {
                    Assert.True(await console.ExecuteAsync(invocation, token));
                    Assert.Equal(expected, Assert.Single(output));
                }
                Assert.Equal(item.GetProperty("level").GetInt32(), client.RuntimeLogLevel);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("TestId", "T374")]
    public async Task NumericFilteringPreservesCriticalAtZeroAndAFailingObserverCannotDisableOtherObservers()
    {
        var root = Root();
        try
        {
            await using var client = new SidebandClient(new(root));
            var entries = new ConcurrentQueue<SidebandRuntimeLogEntry>();
            var errors = new ConcurrentQueue<string>();
            client.RuntimeLogReported += entries.Enqueue;
            client.ErrorReported += errors.Enqueue;
            using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-cli.json"), TestContext.Current.CancellationToken));
            Assert.Equal("217d8cd060844475acb692f869c9eb1e051ce5b800d3f23479d562e37f048f33",
                fixture.RootElement.GetProperty("rns_logging_source_sha256").GetString());
            foreach (var item in fixture.RootElement.GetProperty("log_filter_cases").EnumerateArray())
            {
                entries.Clear();
                client.RuntimeLogLevel = item.GetProperty("threshold").GetInt32();
                client.ReportRuntimeLog(item.GetProperty("level").GetInt32(), "Synthetic filter observation");
                Assert.Equal(item.GetProperty("emitted").GetBoolean(), entries.Count == 1);
            }
            entries.Clear();
            errors.Clear();
            client.RuntimeLogReported -= entries.Enqueue;
            client.RuntimeLogReported += _ => throw new InvalidOperationException("Synthetic observer failure");
            client.RuntimeLogReported += entries.Enqueue;
            client.RuntimeLogLevel = 2;
            client.ReportRuntimeLog(3, "Suppressed notice");
            client.ReportRuntimeLog(2, "Visible warning");
            Assert.Equal(new SidebandRuntimeLogEntry(2, "Visible warning"), Assert.Single(entries));
            Assert.Contains(errors, text => text.Contains("runtime log observer failed", StringComparison.Ordinal));
            client.RuntimeLogLevel = 0;
            client.ReportRuntimeLog(1, "Suppressed error");
            client.ReportRuntimeLog(0, "Visible critical");
            Assert.Equal(2, entries.Count);
            Assert.Contains(errors, text => text == "Visible critical");
            Assert.Throws<ArgumentOutOfRangeException>(() => client.RuntimeLogLevel = -1);
            Assert.Throws<ArgumentOutOfRangeException>(() => client.RuntimeLogLevel = 8);
            Assert.Equal(0, client.RuntimeLogLevel);
            client.RuntimeLogLevel = 7;
            Parallel.For(0, 64, index => client.ReportRuntimeLog(5, "Concurrent " + index));
            Assert.Equal(66, entries.Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("TestId", "T375")]
    public async Task StreamEndsAtEnterSanitizesReportsAndDetachesOnDisposal()
    {
        var root = Root();
        var token = TestContext.Current.CancellationToken;
        try
        {
            var store = new SidebandStateStore(root);
            await using var client = new SidebandClient(store) { RuntimeLogLevel = 7 };
            var output = new ConcurrentQueue<string>();
            await using var console = new SidebandConsole(client, store, output.Enqueue, output.Enqueue, token);
            var invocation = SidebandConsoleSyntax.Resolve(["log", "stream"])!;
            Assert.True(await console.ExecuteAsync(invocation, token));
            output.Clear();
            client.ReportRuntimeLog(6, "Remote\u001b[2J\u202e text");
            var line = Assert.Single(output);
            Assert.StartsWith("LOG [6] ", line, StringComparison.Ordinal);
            Assert.DoesNotContain('\u001b', line);
            Assert.DoesNotContain('\u202e', line);
            Assert.False(console.EndStream(["status"]));
            Assert.True(console.IsStreaming);
            Assert.True(console.EndStream([]));
            Assert.False(console.IsStreaming);
            output.Clear();
            client.ReportRuntimeLog(6, "After Enter");
            Assert.Empty(output);
            Assert.True(await console.ExecuteAsync(invocation, token));
            await console.DisposeAsync();
            output.Clear();
            client.ReportRuntimeLog(6, "After disposal");
            Assert.Empty(output);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("TestId", "T376")]
    public async Task ActualRuntimeReportsPreserveTheirNumericLevelAndConnectionWorksWithoutSubscribers()
    {
        var root = Root();
        var token = TestContext.Current.CancellationToken;
        try
        {
            var config = Path.Combine(root, "rns");
            Directory.CreateDirectory(config);
            await File.WriteAllTextAsync(Path.Combine(config, "config"), """
                [reticulum]
                share_instance = no
                enable_interface_management = yes
                [interfaces]
                """, token);
            await using var client = new SidebandClient(new(Path.Combine(root, "profile"))) { RuntimeLogLevel = 7 };
            var entries = new ConcurrentQueue<SidebandRuntimeLogEntry>();
            client.RuntimeLogReported += entries.Enqueue;
            await client.ConnectAsync(config, token);
            Assert.True(client.IsConnected);
            var node = client.RuntimeNode ?? throw new InvalidOperationException("No active runtime.");
            Assert.Null(await node.CallManagementAsync(new()
            {
                ["manage"] = "attach_interface", ["name"] = "definitely-absent",
            }, token));
            Assert.Equal(new SidebandRuntimeLogEntry(1,
                "Cannot attach interface 'definitely-absent': no configuration entry exists."), Assert.Single(entries));
            client.RuntimeLogReported -= entries.Enqueue;
            await client.DisconnectAsync();
            await client.ConnectAsync(config, token);
            Assert.True(client.IsConnected);
            Assert.Null(await client.RuntimeNode!.CallManagementAsync(new()
            {
                ["manage"] = "attach_interface", ["name"] = "definitely-absent",
            }, token));
            Assert.Single(entries);
            await client.DisconnectAsync();
            await client.DisposeAsync();
            Assert.Throws<ObjectDisposedException>(() => client.RuntimeLogLevel = 3);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static string Root() => Path.Combine(AppContext.BaseDirectory, "runtime-log-" + Guid.NewGuid().ToString("N"));
}
