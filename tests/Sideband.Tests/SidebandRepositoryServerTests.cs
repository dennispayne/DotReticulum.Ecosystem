using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sideband;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandRepositoryServerTests
{
    [Fact]
    public async Task ActualHttpsGetRoutesMatchIndependentlyExecutedPrimaryPublicFileObservations()
    {
        await using var environment = new Environment();
        await environment.StartAsync();
        using var client = environment.Client();
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "sideband-extensions.json")));
        foreach (var observation in fixture.RootElement.GetProperty("repository_routes").EnumerateArray())
        {
            var path = observation.GetProperty("path").GetString()!;
            if (path == "/missing") continue;
            using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var actual = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            if (path == "/pkglist")
            {
                Assert.Equal("text/json", response.Content.Headers.ContentType?.MediaType);
                using var observedNames = JsonDocument.Parse(observation.GetProperty("body_utf8").GetString()!);
                using var actualNames = JsonDocument.Parse(actual);
                Assert.Equal(observedNames.RootElement.EnumerateArray().Select(x => x.GetString()),
                    actualNames.RootElement.EnumerateArray().Select(x => x.GetString()));
            }
            else Assert.Equal(observation.GetProperty("body_utf8").GetString(), actual);
        }

        using var apk = await client.GetAsync("/pkg/z.apk", TestContext.Current.CancellationToken);
        Assert.Equal("application/vnd.android.package-archive", apk.Content.Headers.ContentType?.MediaType);
        Assert.Contains("nosniff", apk.Headers.GetValues("X-Content-Type-Options"));
        Assert.True(apk.Headers.ConnectionClose);
    }

    [Fact]
    public async Task PublicFilesNeedNoAuthenticationButStartupAlwaysRequiresExplicitAcknowledgement()
    {
        await using var environment = new Environment(acknowledge: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => environment.Server.StartAsync(TestContext.Current.CancellationToken));
        Assert.Null(environment.Server.LocalEndpoint);
        Assert.False(Directory.Exists(environment.Certificates));
        Assert.Empty(environment.Errors);
    }

    [Fact]
    public async Task MissingFilesAndUnsupportedMethodsAreExplicitSafetyDifferencesFromPrimary()
    {
        await using var environment = new Environment();
        await environment.StartAsync();
        using var client = environment.Client();
        using var missing = await client.GetAsync("/missing", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var head = new HttpRequestMessage(HttpMethod.Head, "/");
        using var response = await client.SendAsync(head, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(["GET"], response.Content.Headers.Allow);
    }

    [Theory]
    [InlineData("/../secret", 403)]
    [InlineData("/%2e%2e/secret", 403)]
    [InlineData("/pkg/..%2fsecret", 403)]
    [InlineData("/pkg%5csecret", 403)]
    [InlineData("/index.html:secret", 403)]
    [InlineData("/%00", 403)]
    [InlineData("/bad%", 400)]
    [InlineData("/bad%xy", 400)]
    [InlineData("/space%20name.txt", 200)]
    public async Task RawHttpsPathsAreDecodedOnceAndCannotEscapePublicRoot(string path, int expected)
    {
        await using var environment = new Environment();
        File.WriteAllText(Path.Combine(environment.Root, "space name.txt"), "synthetic");
        await environment.StartAsync();
        var response = await environment.RawAsync($"GET {path} HTTP/1.1\r\nHost: localhost\r\n\r\n");
        Assert.StartsWith($"HTTP/1.1 {expected} ", response);
    }

    [Theory]
    [InlineData("GET / HTTP/1.1\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: a\r\nHost: b\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost : a\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: a\r\nTransfer-Encoding: chunked\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: a\r\nContent-Length: 1\r\n\r\nx")]
    [InlineData("GET / HTTP/1.1\r\nHost: a\r\nContent-Length: 0\r\nContent-Length: 0\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: a\r\n broken: folded\r\n\r\n")]
    [InlineData("GET https://localhost/ HTTP/1.1\r\nHost: a\r\n\r\n")]
    [InlineData("GET / HTTP/2.0\r\nHost: a\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nHost: a\rwrong\r\n\r\n")]
    public async Task MalformedAndBodyBearingRequestsFailWithoutDisablingOtherPeers(string request)
    {
        await using var environment = new Environment();
        await environment.StartAsync();
        Assert.StartsWith("HTTP/1.1 400 ", await environment.RawAsync(request));
        using var client = environment.Client();
        Assert.Equal("synthetic-index", await client.GetStringAsync("/", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RequestHeadersTargetsFilesAndPackageEnumerationAreBounded()
    {
        await using var environment = new Environment(configure: options => options with
        {
            MaximumHeaderBytes = 4096, MaximumFileBytes = 15, MaximumPackageEntries = 1,
        });
        File.WriteAllText(Path.Combine(environment.Root, "large.bin"), new string('x', 16));
        await environment.StartAsync();
        Assert.StartsWith("HTTP/1.1 431 ", await environment.RawAsync("GET / HTTP/1.1\r\nHost: a\r\nLong: " + new string('x', 5000)));
        Assert.StartsWith("HTTP/1.1 414 ", await environment.RawAsync("GET /" + new string('x', 2500) + " HTTP/1.1\r\nHost: a\r\n\r\n"));
        using var client = environment.Client();
        using var large = await client.GetAsync("/large.bin", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
        using var index = await client.GetAsync("/pkglist", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, index.StatusCode);
        Assert.Equal("synthetic-index", await client.GetStringAsync("/", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MultiMegabyteFileIsStreamedWithoutChangingItsContents()
    {
        await using var environment = new Environment();
        var payload = Enumerable.Range(0, 2 * 1024 * 1024).Select(i => (byte)(i % 251)).ToArray();
        File.WriteAllBytes(Path.Combine(environment.Root, "large.bin"), payload);
        await environment.StartAsync();
        using var client = environment.Client();
        var received = await client.GetByteArrayAsync("/large.bin", TestContext.Current.CancellationToken);
        Assert.Equal(SHA256.HashData(payload), SHA256.HashData(received));
    }

    [Fact]
    public async Task CertificateAndFingerprintSurviveFreshInstanceRestartAndPrivateStoreIsNeverServed()
    {
        await using var environment = new Environment();
        await environment.StartAsync();
        var fingerprint = environment.Server.CertificateSha256;
        using (var client = environment.Client())
        {
            using var response = await client.GetAsync("/repository-certificate.pfx", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        await environment.Server.DisposeAsync();
        await using var restarted = new SidebandRepositoryServer(environment.Options);
        await restarted.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(fingerprint, restarted.CertificateSha256);
        Assert.Single(Directory.GetFiles(environment.Certificates));
    }

    [Fact]
    public async Task InvalidOwnedCertificateIsNotSilentlyReplaced()
    {
        await using var environment = new Environment();
        Directory.CreateDirectory(environment.Certificates);
        var path = Path.Combine(environment.Certificates, "repository-certificate.pfx");
        File.WriteAllText(path, "synthetic-invalid-certificate");
        await Assert.ThrowsAnyAsync<CryptographicException>(() => environment.StartAsync());
        Assert.Equal("synthetic-invalid-certificate", File.ReadAllText(path));
        Assert.Null(environment.Server.LocalEndpoint);
    }

    [Fact]
    public async Task PreCancellationDoesNotCreateCertificateOrOpenListener()
    {
        await using var environment = new Environment();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => environment.Server.StartAsync(stop.Token));
        Assert.False(Directory.Exists(environment.Certificates));
        Assert.Null(environment.Server.LocalEndpoint);
    }

    [Fact]
    public async Task OccupiedPortFailureCanBeRetriedWithoutLeakingListenerOrCertificate()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        await using var environment = new Environment(configure: options => options with { Port = port });
        await Assert.ThrowsAnyAsync<SocketException>(() => environment.StartAsync());
        occupied.Stop();
        await environment.StartAsync();
        using var client = environment.Client();
        Assert.Equal("synthetic-index", await client.GetStringAsync("/", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BadTlsPeerAndFailingErrorObserverDoNotDisableHealthyRequests()
    {
        await using var environment = new Environment();
        environment.Server.ErrorReported += _ => throw new InvalidOperationException("Synthetic observer failure");
        var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        environment.Server.ErrorReported += text => { if (text.Contains("peer failed", StringComparison.Ordinal)) failure.TrySetResult(); };
        await environment.StartAsync();
        using (var bad = new TcpClient())
        {
            await bad.ConnectAsync(environment.Endpoint, TestContext.Current.CancellationToken);
            await bad.GetStream().WriteAsync("not TLS\r\n\r\n"u8.ToArray(), TestContext.Current.CancellationToken);
        }
        await failure.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        using var client = environment.Client();
        Assert.Equal("synthetic-index", await client.GetStringAsync("/", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HandshakeDeadlineReleasesCapacityWithoutAnyErrorSubscriber()
    {
        await using var environment = new Environment(configure: options => options with
        {
            RequestTimeout = TimeSpan.FromMilliseconds(400), MaximumConnections = 1,
        }, subscribe: false);
        await environment.StartAsync();
        using var stalled = new TcpClient();
        await stalled.ConnectAsync(environment.Endpoint, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => environment.Server.ActiveConnections == 1);
        await WaitUntilAsync(() => environment.Server.ActiveConnections == 0);
        using var client = environment.Client();
        Assert.Equal("synthetic-index", await client.GetStringAsync("/", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConnectionLimitRejectsOverflowAndRecoversAfterPeerDisconnect()
    {
        await using var environment = new Environment(configure: options => options with { MaximumConnections = 1 });
        await environment.StartAsync();
        using var stalled = new TcpClient();
        await stalled.ConnectAsync(environment.Endpoint, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => environment.Server.ActiveConnections == 1);
        using var overflow = new TcpClient();
        await overflow.ConnectAsync(environment.Endpoint, TestContext.Current.CancellationToken);
        var read = await overflow.GetStream().ReadAsync(new byte[1], TestContext.Current.CancellationToken);
        Assert.Equal(0, read);
        Assert.Equal(1, environment.Server.ActiveConnections);
        stalled.Dispose();
        await WaitUntilAsync(() => environment.Server.ActiveConnections == 0);
        using var client = environment.Client();
        Assert.Equal("synthetic-index", await client.GetStringAsync("/", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentDisposalJoinsIncompleteTlsPeersAndRefusesRestart()
    {
        await using var environment = new Environment();
        await environment.StartAsync();
        var endpoint = environment.Endpoint;
        using var stalled = new TcpClient();
        await stalled.ConnectAsync(endpoint, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => environment.Server.ActiveConnections == 1);
        var first = environment.Server.DisposeAsync().AsTask();
        var second = environment.Server.DisposeAsync().AsTask();
        Assert.Same(first, second);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, environment.Server.ActiveConnections);
        Assert.Null(environment.Server.LocalEndpoint);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => environment.StartAsync());
        using var rebound = new TcpListener(endpoint);
        rebound.Start();
    }

    [Fact]
    public async Task LifetimeCancellationClosesListenerAndActivePeersBeforeDisposal()
    {
        await using var environment = new Environment();
        using var stop = new CancellationTokenSource();
        await environment.Server.StartAsync(stop.Token);
        var endpoint = environment.Endpoint;
        using var stalled = new TcpClient();
        await stalled.ConnectAsync(endpoint, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => environment.Server.ActiveConnections == 1);
        await stop.CancelAsync();
        await WaitUntilAsync(() => environment.Server.ActiveConnections == 0);
        Assert.Null(environment.Server.LocalEndpoint);
        using var rebound = new TcpListener(endpoint);
        rebound.Start();
    }

    [Fact]
    public async Task SymbolicLinksAreNotFollowedAndCannotExposePrivateStore()
    {
        await using var environment = new Environment();
        await environment.StartAsync();
        try { File.CreateSymbolicLink(Path.Combine(environment.Root, "private.pfx"),
            Path.Combine(environment.Certificates, "repository-certificate.pfx")); }
        catch (UnauthorizedAccessException) { Assert.Skip("Creating Windows symbolic links requires Developer Mode or elevated privilege."); }
        using var client = environment.Client();
        using var response = await client.GetAsync("/private.pfx", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("synthetic-index", await client.GetStringAsync("/", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnixCertificateFileIsOwnerOnly()
    {
        if (OperatingSystem.IsWindows()) { Assert.Skip("Windows inherits directory ACLs; Unix mode bits do not describe Windows key protection."); return; }
        await using var environment = new Environment();
        await environment.StartAsync();
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(Path.Combine(environment.Certificates, "repository-certificate.pfx")));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }

    private sealed class Environment : IAsyncDisposable
    {
        private readonly string _directory = Path.GetFullPath(Path.Combine("artifacts", "tests", "sideband-repository", Guid.NewGuid().ToString("N")));
        public string Root { get; }
        public string Certificates { get; }
        public SidebandRepositoryServer Server { get; }
        public SidebandRepositoryOptions Options { get; }
        public ConcurrentQueue<string> Errors { get; } = new();
        public IPEndPoint Endpoint => Server.LocalEndpoint!;

        public Environment(bool acknowledge = true, Func<SidebandRepositoryOptions, SidebandRepositoryOptions>? configure = null,
            bool subscribe = true)
        {
            Root = Path.Combine(_directory, "public");
            Certificates = Path.Combine(_directory, "private");
            Directory.CreateDirectory(Path.Combine(Root, "pkg"));
            File.WriteAllText(Path.Combine(Root, "index.html"), "synthetic-index");
            File.WriteAllText(Path.Combine(Root, "pkg", "a.txt"), "synthetic-text");
            File.WriteAllText(Path.Combine(Root, "pkg", "z.apk"), "synthetic-apk");
            Options = new() { RootDirectory = Root, CertificateDirectory = Certificates,
                Port = 0, AllowPublicUnauthenticatedAccess = acknowledge };
            if (configure is not null) Options = configure(Options);
            Server = new(Options);
            if (subscribe) Server.ErrorReported += Errors.Enqueue;
        }

        public Task StartAsync() => Server.StartAsync(TestContext.Current.CancellationToken);

        public HttpClient Client()
        {
            var handler = new HttpClientHandler
            {
                UseProxy = false,
                ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                    certificate?.GetCertHashString(HashAlgorithmName.SHA256) == Server.CertificateSha256,
            };
            return new(handler) { BaseAddress = new UriBuilder("https", Endpoint.Address.ToString(), Endpoint.Port).Uri,
                Timeout = TimeSpan.FromSeconds(10) };
        }

        public async Task<string> RawAsync(string request)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(Endpoint, TestContext.Current.CancellationToken);
            using var tls = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
                certificate?.GetCertHashString(HashAlgorithmName.SHA256) == Server.CertificateSha256);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost", EnabledSslProtocols = SslProtocols.Tls12,
            }, TestContext.Current.CancellationToken);
            await tls.WriteAsync(Encoding.ASCII.GetBytes(request), TestContext.Current.CancellationToken);
            using var reader = new StreamReader(tls, Encoding.UTF8);
            return await reader.ReadToEndAsync(TestContext.Current.CancellationToken).WaitAsync(
                TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            foreach (var error in Errors) TestContext.Current.TestOutputHelper?.WriteLine(error);
            Directory.Delete(_directory, recursive: true);
        }
    }
}
