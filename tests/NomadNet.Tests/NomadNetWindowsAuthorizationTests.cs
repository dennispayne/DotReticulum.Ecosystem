using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DotReticulum.Applications;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace NomadNet.Tests;

[Trait("Category", "Integration")]
public sealed class NomadNetWindowsAuthorizationTests
{
    [AuthorizationFact, Trait("TestId", "T1060")]
    public async Task RealPeRequiresOptInPreservesListsAndDoesNotEnableExecutablePages()
    {
        using var work = new Workspace();
        using var identity = Identity.Generate();
        var request = Request(identity);
        var disabled = new NomadNetContent(work.Root);
        await work.GeneratorAsync("allow", identity);
        AssertDenied(await disabled.RespondAsync(request, default));
        Assert.False(File.Exists(work.Allowed + ".pid"));
        AssertDenied(await work.Content.RespondAsync(request with { RemoteIdentity = null }, default));
        Assert.False(File.Exists(work.Allowed + ".pid"));
        Assert.Equal("private page", Text(await work.Content.RespondAsync(request, default)));
        using var outsider = Identity.Generate();
        AssertDenied(await work.Content.RespondAsync(request with { RemoteIdentity = outsider }, default));
        await File.WriteAllTextAsync(work.Allowed, Convert.ToHexStringLower(identity.Hash).ToUpperInvariant() + "\r\n");
        Assert.Equal("private page", Text(await disabled.RespondAsync(request, default)));
        AssertDenied(await disabled.RespondAsync(request with { RemoteIdentity = outsider }, default));
        File.Delete(work.Allowed);
        File.Copy(Fixture, work.Page, overwrite: true);
        Assert.True(new FileInfo(work.Page).Length > MicronText.MaximumPageBytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => work.Content.RespondAsync(request, default).AsTask());
        Assert.False(File.Exists(work.Page + ".pid"));
    }

    [AuthorizationFact, Trait("TestId", "T1061")]
    public async Task EnvironmentIsRestrictedAndMalformedNonzeroAndStderrMatchPrimary()
    {
        using var work = new Workspace();
        using var identity = Identity.Generate();
        var request = Request(identity) with
        {
            Data = new Dictionary<object, object?>
            {
                ["field_name"] = "Alice", ["var_value"] = "bounded",
                ["NOT_ALLOWED"] = "private", ["var_bad-name"] = "private",
                ["var_oversize"] = new string('x', 16385), ["var_nul"] = "\0",
            },
        };
        var old = Environment.GetEnvironmentVariable("AUTHORIZATION_MUST_NOT_LEAK");
        Environment.SetEnvironmentVariable("AUTHORIZATION_MUST_NOT_LEAK", "private synthetic value");
        try
        {
            foreach (var mode in new[] { "environment", "mixed", "cr", "nonzero", "stderr", "boundary" })
            {
                await work.GeneratorAsync(mode, identity);
                Assert.Equal("private page", Text(await work.Content.RespondAsync(request, default)));
            }
            await work.GeneratorAsync("malformed", identity);
            AssertDenied(await work.Content.RespondAsync(request, default));
            await work.GeneratorAsync("allow", identity);
            Assert.Equal("private page", Text(await work.Content.RespondAsync(request, default)));
        }
        finally { Environment.SetEnvironmentVariable("AUTHORIZATION_MUST_NOT_LEAK", old); }
    }

    [AuthorizationFact, Trait("TestId", "T1062")]
    public async Task OutputFailureDeadlineAndCancellationKillOwnedChildrenAndRecover()
    {
        using var work = new Workspace();
        using var identity = Identity.Generate();
        var request = Request(identity);
        var errors = new List<string>();
        work.Content.Log = errors.Add;
        await work.GeneratorAsync("overflow", identity);
        AssertDenied(await work.Content.RespondAsync(request, default));
        Assert.NotEmpty(errors);
        await AssertExitedAsync(work.Allowed + ".pid");
        await work.GeneratorAsync("descendant", identity);
        using (var cancellation = new CancellationTokenSource())
        {
            var pending = work.Content.RespondAsync(request, cancellation.Token).AsTask();
            await WaitForFileAsync(work.Allowed + ".childpid");
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        await AssertExitedAsync(work.Allowed + ".pid");
        await AssertExitedAsync(work.Allowed + ".childpid");
        await work.GeneratorAsync("orphan", identity);
        var timer = Stopwatch.StartNew();
        AssertDenied(await work.Content.RespondAsync(request, default));
        Assert.InRange(timer.Elapsed, TimeSpan.FromSeconds(13), TimeSpan.FromSeconds(24));
        await AssertExitedAsync(work.Allowed + ".pid");
        await AssertExitedAsync(work.Allowed + ".childpid");
        await work.GeneratorAsync("allow", identity);
        Assert.Equal("private page", Text(await work.Content.RespondAsync(request, default)));
        work.Content.Log = _ => throw new InvalidOperationException("failing observer");
        await work.GeneratorAsync("overflow", identity);
        AssertDenied(await work.Content.RespondAsync(request, default));
        await work.GeneratorAsync("allow", identity);
        Assert.Equal("private page", Text(await work.Content.RespondAsync(request, default)));
    }

    [AuthorizationFact, Trait("TestId", "T1063")]
    public async Task InvalidPeAndLockedCompanionFailClosedWithoutDisablingService()
    {
        using var work = new Workspace();
        using var identity = Identity.Generate();
        var request = Request(identity);
        var invalid = new byte[128];
        invalid[0] = (byte)'M';
        invalid[1] = (byte)'Z';
        await File.WriteAllBytesAsync(work.Allowed, invalid);
        AssertDenied(await work.Content.RespondAsync(request, default));
        await File.WriteAllTextAsync(work.Allowed, Convert.ToHexStringLower(identity.Hash));
        using (var locked = new FileStream(work.Allowed, FileMode.Open, FileAccess.Read, FileShare.None))
            AssertDenied(await work.Content.RespondAsync(request, default));
        Assert.Equal("private page", Text(await work.Content.RespondAsync(request, default)));
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.Content.RespondAsync(request, stopped.Token).AsTask());
    }

    [AuthorizationFact, Trait("TestId", "T1064")]
    public async Task HostDisposalJoinsAuthorizationProcessAndDescendantsWithoutSubscribers()
    {
        using var work = new Workspace();
        using var identity = Identity.Generate();
        var rns = Path.Combine(work.Root, "rns");
        Directory.CreateDirectory(rns);
        await File.WriteAllTextAsync(Path.Combine(rns, "config"),
            "[reticulum]\nshare_instance=no\nenable_transport=no\n[logging]\nloglevel=0\n[interfaces]\n");
        await using var node = ReticulumNode.Open(rns);
        var host = new NomadNetHost(node.Router, identity, work.Root, new() { EnableExecutablePages = true });
        await work.GeneratorAsync("descendant", identity);
        var request = host.RespondAsync(Request(identity), default, defaultIndex: false).AsTask();
        await WaitForFileAsync(work.Allowed + ".childpid");
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => host.DisposeAsync().AsTask())).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await AssertExitedAsync(work.Allowed + ".pid");
        await AssertExitedAsync(work.Allowed + ".childpid");
        Assert.Null(await host.RespondAsync(Request(identity), default, defaultIndex: false));
    }

    [AuthorizationFact, Trait("TestId", "T1067")]
    public async Task SharedLauncherPreservesDefaultInheritanceAndCustomUnicodeIsolation()
    {
        using var work = new Workspace();
        using var identity = Identity.Generate();
        await work.GeneratorAsync("allow", identity);
        var old = Environment.GetEnvironmentVariable("var_allow");
        Environment.SetEnvironmentVariable("var_allow", "Inherited Å 🧭");
        try
        {
            Assert.Equal("Inherited Å 🧭", await ExecuteAsync(null));
            Assert.Equal("Custom μ 🧭", await ExecuteAsync(new Dictionary<string, string?>
            {
                ["var_allow"] = "Custom μ 🧭", ["var_mode"] = "allow",
                ["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            }));
        }
        finally { Environment.SetEnvironmentVariable("var_allow", old); }

        async Task<string> ExecuteAsync(IReadOnlyDictionary<string, string?>? environment)
        {
            using var owned = NomadNetWindowsJobProcess.Start([work.Allowed], environment);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var output = new StreamReader(owned.StandardOutput, Encoding.UTF8).ReadToEndAsync(deadline.Token);
            var errors = new StreamReader(owned.StandardError, Encoding.UTF8).ReadToEndAsync(deadline.Token);
            try
            {
                await owned.Process.WaitForExitAsync(deadline.Token);
                Assert.Equal(0, owned.Process.ExitCode);
                Assert.Empty(await errors);
                await owned.WaitForEmptyAsync(deadline.Token);
                return (await output).TrimEnd('\r', '\n');
            }
            finally { await owned.StopAsync(); }
        }
    }

    [AuthorizationFact, Trait("TestId", "T1068")]
    public async Task RealRequestDispatcherBoundsPeProcessesAndLinkCancellationDiscardsQueuedWork()
    {
        using var work = new Workspace();
        using var serverIdentity = Identity.Generate();
        using var clientIdentity = Identity.Generate();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var serverPath = Path.Combine(work.Root, "server-rns");
        var clientPath = Path.Combine(work.Root, "client-rns");
        foreach (var path in new[] { serverPath, clientPath }) Directory.CreateDirectory(path);
        const string prefix = "[reticulum]\nshare_instance=no\nenable_transport=no\n[logging]\nloglevel=0\n[interfaces]\n[[authorization]]\nenabled=yes\n";
        await File.WriteAllTextAsync(Path.Combine(serverPath, "config"),
            prefix + $"type=TCPServerInterface\nlisten_ip=127.0.0.1\nlisten_port={port}\n");
        await File.WriteAllTextAsync(Path.Combine(clientPath, "config"),
            prefix + $"type=TCPClientInterface\ntarget_host=127.0.0.1\ntarget_port={port}\n");
        await using var server = ReticulumNode.Open(serverPath);
        await using var client = ReticulumNode.Open(clientPath);
        await using var host = new NomadNetHost(server.Router, serverIdentity, work.Root,
            new() { EnableExecutablePages = true, NodeAnnounceAtStart = false });
        var pages = Path.GetDirectoryName(work.Page)!;
        for (var index = 0; index < 12; index++)
        {
            var path = Path.Combine(pages, $"parallel-{index}.mu");
            await File.WriteAllTextAsync(path, "parallel private page");
            File.Copy(Fixture, path + ".allowed");
            await File.WriteAllTextAsync(path + ".allowed.input", "sleep\n" + Convert.ToHexStringLower(clientIdentity.Hash));
        }
        host.Refresh();
        await server.StartAsync();
        await client.StartAsync();
        var address = Convert.FromHexString(host.DestinationHash);
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            while (!client.Router.HasPath(address))
            {
                await host.AnnounceAsync(deadline.Token);
                await Task.Delay(50, deadline.Token);
            }
        var connected = await ClientLinks.ConnectAsync(client, address, TimeSpan.FromSeconds(12));
        var link = Assert.IsType<Link>(connected.Link);
        link.Identify(clientIdentity);
        await Task.Delay(200);
        var pending = Enumerable.Range(0, 12).Select(index =>
            link.Request($"/page/parallel-{index}.mu", timeout: 30) ?? throw new IOException("Request was not admitted.")).ToArray();
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            while (Directory.GetFiles(pages, "parallel-*.mu.allowed.pid").Length < LinkManager.RequestConcurrency)
                await Task.Delay(20, deadline.Token);
        Assert.Equal(4, LinkManager.RequestConcurrency);
        Assert.Equal(256, LinkManager.WorkQueueCapacity);
        await Task.Delay(300);
        Assert.Equal(LinkManager.RequestConcurrency, Directory.GetFiles(pages, "parallel-*.mu.allowed.pid").Length);
        Assert.InRange(server.Router.Links.QueuedRequests, 1, LinkManager.WorkQueueCapacity);
        link.Teardown();
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            while (server.Router.Links.QueuedRequests != 0 ||
                Directory.GetFiles(pages, "parallel-*.mu.allowed.pid").Any(IsRunning))
                await Task.Delay(20, deadline.Token);
        Assert.Equal(LinkManager.RequestConcurrency, Directory.GetFiles(pages, "parallel-*.mu.allowed.pid").Length);
        Assert.All(pending, receipt => Assert.NotEqual(RequestStatus.Ready, receipt.Status));
        var recovered = await ClientLinks.ConnectAsync(client, address, TimeSpan.FromSeconds(12));
        var healthy = Assert.IsType<Link>(recovered.Link);
        healthy.Identify(clientIdentity);
        await Task.Delay(200);
        var first = Path.Combine(pages, "parallel-0.mu");
        await File.WriteAllTextAsync(first + ".allowed.input", "allow\n" + Convert.ToHexStringLower(clientIdentity.Hash));
        var response = healthy.Request("/page/parallel-0.mu", timeout: 10)!;
        Assert.Equal("parallel private page", Encoding.UTF8.GetString(Assert.IsType<byte[]>(
            await response.ResponseTask.WaitAsync(TimeSpan.FromSeconds(15)))));
        healthy.Teardown();

        static bool IsRunning(string path)
        {
            try { using var process = Process.GetProcessById(int.Parse(File.ReadAllText(path))); return !process.HasExited; }
            catch (ArgumentException) { return false; }
        }
    }

    private static string Fixture => Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_AUTHORIZATION_GENERATOR")
        ?? throw new InvalidOperationException("Select independently built authorization PE fixture.");
    private static RequestContext Request(Identity identity) => new("/page/private.mu", null, new byte[16], new byte[16], identity, 0);
    private static string Text(object? result) => Encoding.UTF8.GetString(Assert.IsType<byte[]>(result));
    private static void AssertDenied(object? result) => Assert.Contains("Request Not Allowed", Text(result));

    private static async Task WaitForFileAsync(string path)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(path)) await Task.Delay(20, deadline.Token);
    }

    private static async Task AssertExitedAsync(string path)
    {
        await WaitForFileAsync(path);
        var pid = int.Parse(await File.ReadAllTextAsync(path));
        try
        {
            using var process = Process.GetProcessById(pid);
            Assert.True(process.HasExited, $"Owned fixture process {pid} survived.");
        }
        catch (ArgumentException) { }
    }

    private sealed class AuthorizationFactAttribute : FactAttribute
    {
        public AuthorizationFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Real Windows PE authorization-generator contract; Linux remains separately tested.";
            else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_AUTHORIZATION_GENERATOR")))
                Skip = "Select the independently built NativeAOT PE fixture with DOTRETICULUM_NOMADNET_AUTHORIZATION_GENERATOR.";
        }
    }

    private sealed class Workspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetFullPath("artifacts"), "tests", "nomad-windows-authorization", "case-" + Guid.NewGuid().ToString("N"));
        public NomadNetContent Content { get; }
        public string Page => Path.Combine(Root, "storage", "pages", "private.mu");
        public string Allowed => Page + ".allowed";
        public Workspace()
        {
            Content = new(Root, enableExecutablePages: true);
            File.WriteAllText(Page, "private page");
        }
        public async Task GeneratorAsync(string mode, Identity identity)
        {
            foreach (var suffix in new[] { ".pid", ".childpid" })
                if (File.Exists(Allowed + suffix)) File.Delete(Allowed + suffix);
            File.Copy(Fixture, Allowed, overwrite: true);
            await File.WriteAllTextAsync(Allowed + ".input", mode + "\n" + Convert.ToHexStringLower(identity.Hash) + "\n");
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
