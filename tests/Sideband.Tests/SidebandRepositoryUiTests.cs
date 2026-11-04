using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Sideband.Desktop;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandRepositoryUiTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task DefaultOffAndDeniedConsentNeverExposeFilesOrCreateCertificates()
    {
        await using var environment = new Environment();
        await environment.OpenAsync();
        var window = environment.Window!;
        Assert.False(environment.Control<CheckBox>("RepositoryConsentBox").IsChecked);
        Assert.Equal("127.0.0.1", environment.Control<TextBox>("RepositoryBindBox").Text);
        Assert.Equal("4444", environment.Control<TextBox>("RepositoryPortBox").Text);
        Assert.False(environment.Control<Button>("StopRepositoryButton").IsEnabled);
        Assert.True(string.IsNullOrEmpty(environment.Control<TextBox>("RepositoryRootBox").Text));
        environment.Configure();
        environment.Click("StartRepositoryButton");
        await WaitForAsync(() => environment.Status.StartsWith("Repository start failed:", StringComparison.Ordinal));
        Assert.Contains("Confirm public, unauthenticated sharing", environment.Status);
        Assert.Contains("PUBLIC AND UNAUTHENTICATED", environment.Status);
        Assert.True(environment.Control<Button>("StartRepositoryButton").IsEnabled);
        Assert.False(Directory.Exists(environment.Certificates));
        Assert.True(string.IsNullOrEmpty(environment.Fingerprint));
        await window.CloseAndWaitAsync();
    }

    [AvaloniaFact]
    public async Task InvalidInputsAndOverlappingProfileDirectoriesAreRejectedBeforeOpeningListener()
    {
        await using var environment = new Environment();
        await environment.OpenAsync();
        var nestedProfile = Path.Combine(environment.Profile, "nested");
        Directory.CreateDirectory(nestedProfile);
        foreach (var invalid in new[]
        {
            ("RepositoryRootBox", Path.Combine(environment.Public, "missing"), "existing, dedicated"),
            ("RepositoryRootBox", environment.Profile, "outside the Sideband profile"),
            ("RepositoryRootBox", Path.GetDirectoryName(environment.Profile)!, "outside the Sideband profile"),
            ("RepositoryRootBox", nestedProfile, "outside the Sideband profile"),
            ("RepositoryPortBox", "-1", "port from 0 to 65535"),
            ("RepositoryPortBox", "65536", "port from 0 to 65535"),
            ("RepositoryPortBox", "not-a-port", "port from 0 to 65535"),
            ("RepositoryBindBox", "localhost", "literal IPv4 or IPv6"),
            ("RepositoryBindBox", "999.1.2.3", "literal IPv4 or IPv6"),
        })
        {
            environment.Configure(consent: true);
            environment.Control<TextBox>(invalid.Item1).Text = invalid.Item2;
            environment.Click("StartRepositoryButton");
            await WaitForAsync(() => environment.Status.Contains(invalid.Item3, StringComparison.Ordinal));
            Assert.StartsWith("Repository start failed:", environment.Status);
            Assert.True(string.IsNullOrEmpty(environment.Fingerprint));
            Assert.False(Directory.Exists(environment.Certificates));
            Assert.True(environment.Control<TextBox>("RepositoryRootBox").IsEnabled);
        }
    }

    [AvaloniaFact]
    public async Task StartServesOriginalPublicFilesOverRealTlsAndStopJoinsTheListener()
    {
        await using var environment = new Environment();
        await environment.OpenAsync();
        await environment.StartAsync();
        var uri = environment.ListeningUri;
        using (var client = environment.Client(uri))
        {
            Assert.Equal("original-ui-index", await client.GetStringAsync("/", Token));
            Assert.Equal("[\"a.txt\"]", await client.GetStringAsync("/pkglist", Token));
            Assert.Equal("original-ui-package", await client.GetStringAsync("/pkg/a.txt", Token));
            using var missing = await client.GetAsync("/missing", Token);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        Assert.Equal(64, environment.Fingerprint.Length);
        Assert.True(environment.Control<TextBox>("RepositoryFingerprintText").IsReadOnly);
        Assert.Contains("PUBLIC AND UNAUTHENTICATED", environment.Status);
        Assert.Contains("not Reticulum/rngit", environment.Status);
        Assert.False(environment.Control<Button>("StartRepositoryButton").IsEnabled);
        Assert.True(environment.Control<Button>("StopRepositoryButton").IsEnabled);
        Assert.False(environment.Control<TextBox>("RepositoryRootBox").IsEnabled);
        Assert.False(environment.Control<CheckBox>("RepositoryConsentBox").IsEnabled);
        await environment.StopAsync();
        Assert.False(environment.Control<CheckBox>("RepositoryConsentBox").IsChecked);
        Assert.True(environment.Control<TextBox>("RepositoryRootBox").IsEnabled);
        Assert.Equal("", environment.Fingerprint);
        AssertCanBind(uri);
        var state = await new SidebandStateStore(environment.Profile).LoadAsync(cancellationToken: Token);
        Assert.Null(state.OwnDeliveryHash);
        Assert.Empty(state.Messages);
    }

    [AvaloniaFact]
    public async Task CertificateRestartsButWindowExposureSettingsAndConsentNeverPersist()
    {
        await using var environment = new Environment();
        await environment.OpenAsync();
        await environment.StartAsync();
        var fingerprint = environment.Fingerprint;
        await environment.StopAsync();
        Assert.False(environment.Control<CheckBox>("RepositoryConsentBox").IsChecked);
        await environment.StartAsync();
        Assert.Equal(fingerprint, environment.Fingerprint);
        var uri = environment.ListeningUri;
        await environment.Window!.CloseAndWaitAsync();
        AssertCanBind(uri);
        await environment.OpenAsync();
        Assert.False(environment.Control<CheckBox>("RepositoryConsentBox").IsChecked);
        Assert.True(string.IsNullOrEmpty(environment.Control<TextBox>("RepositoryRootBox").Text));
        Assert.True(string.IsNullOrEmpty(environment.Fingerprint));
        Assert.False(environment.Control<Button>("StopRepositoryButton").IsEnabled);
        Assert.Equal("127.0.0.1", environment.Control<TextBox>("RepositoryBindBox").Text);
        Assert.Equal("4444", environment.Control<TextBox>("RepositoryPortBox").Text);
        Assert.Single(Directory.GetFiles(environment.Certificates));
    }

    [AvaloniaFact]
    public async Task BindFailureIsShownAndExplicitRetryWorksWithoutChangingPublicFiles()
    {
        await using var environment = new Environment();
        await environment.OpenAsync();
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        environment.Configure(consent: true);
        environment.Control<TextBox>("RepositoryPortBox").Text = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        environment.Click("StartRepositoryButton");
        await WaitForAsync(() => environment.Status.StartsWith("Repository start failed:", StringComparison.Ordinal));
        await WaitForAsync(() => environment.Control<Button>("StartRepositoryButton").IsEnabled);
        Assert.True(string.IsNullOrEmpty(environment.Fingerprint));
        Assert.Contains("Repository start failed:", environment.Control<TextBlock>("StatusText").Text);
        occupied.Stop();
        environment.Click("StartRepositoryButton");
        await environment.WaitForStartedAsync();
        using var client = environment.Client(environment.ListeningUri);
        Assert.Equal("original-ui-index", await client.GetStringAsync("/", Token));
        await environment.StopAsync();
    }

    [AvaloniaFact]
    public async Task PeerErrorIsReportedOnUiThreadWhileHealthyTlsRequestsContinue()
    {
        await using var environment = new Environment();
        await environment.OpenAsync();
        await environment.StartAsync();
        var uri = environment.ListeningUri;
        using (var bad = new TcpClient())
        {
            await bad.ConnectAsync(IPAddress.Parse(uri.Host), uri.Port, Token);
            await bad.GetStream().WriteAsync("not TLS\r\n\r\n"u8.ToArray(), Token);
        }
        await WaitForAsync(() => environment.Control<TextBlock>("StatusText").Text?
            .StartsWith("Repository: Repository peer failed:", StringComparison.Ordinal) == true);
        Assert.StartsWith("Sharing at https://", environment.Status);
        Assert.Contains("Last notice: Repository peer failed:", environment.Status);
        using var client = environment.Client(uri);
        Assert.Equal("original-ui-index", await client.GetStringAsync("/", Token));
        await environment.StopAsync();
    }

    [AvaloniaFact]
    public async Task ImmediateStopAndCloseDuringStartCannotLeaveAnExposedListener()
    {
        await using var environment = new Environment();
        await environment.OpenAsync();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            environment.Configure(consent: true);
            environment.Click("StartRepositoryButton");
            environment.Click("StopRepositoryButton");
            await WaitForAsync(() => environment.Status.StartsWith("Repository stopped.", StringComparison.Ordinal) &&
                environment.Control<Button>("StartRepositoryButton").IsEnabled);
            Assert.Equal("", environment.Fingerprint);
        }
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        environment.Configure(consent: true);
        environment.Control<TextBox>("RepositoryPortBox").Text = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        environment.Click("StartRepositoryButton");
        await environment.Window!.CloseAndWaitAsync();
        AssertCanBind(new Uri($"https://127.0.0.1:{port}"));
    }

    [AvaloniaFact]
    public async Task WindowCloseCancelsAnIncompleteTlsPeerAndReleasesItsEndpoint()
    {
        await using var environment = new Environment();
        await environment.OpenAsync();
        await environment.StartAsync();
        var uri = environment.ListeningUri;
        using var stalled = new TcpClient();
        await stalled.ConnectAsync(IPAddress.Parse(uri.Host), uri.Port, Token);
        await Task.Delay(30, Token);
        Dispatcher.UIThread.RunJobs();
        await environment.Window!.CloseAndWaitAsync();
        AssertCanBind(uri);
    }

    [Fact]
    public async Task ExplicitSmokeVerifierUsesRealPinnedHttpsIndexAndPackageList()
    {
        await using var environment = new Environment();
        await using var server = SmokeServer(environment);
        await server.StartAsync(Token);
        await VerifySmokeAsync(server, Token);
    }

    [Theory]
    [InlineData("missing-index", "index did not return HTTP 200")]
    [InlineData("empty-packages", "nonempty array of package names")]
    [InlineData("oversized-index", "bounded body limit")]
    public async Task SmokeVerifierNeverAcceptsMissingEmptyOrOversizedContent(string kind, string expected)
    {
        await using var environment = new Environment();
        if (kind == "missing-index") File.Delete(Path.Combine(environment.Public, "index.html"));
        if (kind == "empty-packages") File.Delete(Path.Combine(environment.Public, "pkg", "a.txt"));
        if (kind == "oversized-index") File.WriteAllBytes(Path.Combine(environment.Public, "index.html"), new byte[65537]);
        await using var server = SmokeServer(environment);
        await server.StartAsync(Token);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => VerifySmokeAsync(server, Token));
        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public async Task SmokeVerifierHonorsCancellationWithoutSuccessFallback()
    {
        await using var environment = new Environment();
        await using var server = SmokeServer(environment);
        await server.StartAsync(Token);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VerifySmokeAsync(server, stop.Token));
    }

    private static SidebandRepositoryServer SmokeServer(Environment environment) => new(new()
    {
        RootDirectory = environment.Public, CertificateDirectory = environment.Certificates,
        AllowPublicUnauthenticatedAccess = true, Port = 0,
    });

    private static Task VerifySmokeAsync(SidebandRepositoryServer server, CancellationToken token)
    {
        // Exercise the private probe without mutating process-global application smoke settings.
        var verifier = typeof(MainWindow).GetMethod("VerifyRepositorySmokeAsync", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(verifier);
        return (Task)verifier.Invoke(null, [server, token])!;
    }

    private static void AssertCanBind(Uri uri)
    {
        using var listener = new TcpListener(IPAddress.Parse(uri.Host), uri.Port);
        listener.Start();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Repository desktop did not reach its expected state.");
            await Task.Delay(10, Token);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private sealed class Environment : IAsyncDisposable
    {
        private readonly string _directory = Path.GetFullPath(Path.Combine("artifacts", "tests", "sideband-repository-ui", Guid.NewGuid().ToString("N")));
        public string Profile { get; }
        public string Public { get; }
        public string Certificates => Path.Combine(Profile, "repository-certificate");
        public MainWindow? Window { get; private set; }
        public string Status => Control<TextBlock>("RepositoryStatusText").Text ?? "";
        public string Fingerprint => Control<TextBox>("RepositoryFingerprintText").Text ?? "";
        public Uri ListeningUri => new(Status.Split('\n')[0]["Sharing at ".Length..].Trim());

        public Environment()
        {
            Profile = Path.Combine(_directory, "profile");
            Public = Path.Combine(_directory, "public");
            Directory.CreateDirectory(Path.Combine(Public, "pkg"));
            File.WriteAllText(Path.Combine(Public, "index.html"), "original-ui-index");
            File.WriteAllText(Path.Combine(Public, "pkg", "a.txt"), "original-ui-package");
        }

        public async Task OpenAsync()
        {
            Window = new MainWindow(Profile);
            Window.Show();
            await WaitForAsync(() => Control<TextBlock>("StatusText").Text?
                .Contains("Offline · local data:", StringComparison.Ordinal) == true);
            Click("UtilitiesNavButton");
        }

        public T Control<T>(string name) where T : Control => Window!.FindControl<T>(name)!;
        public void Click(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        public void Configure(bool consent = false)
        {
            Control<TextBox>("RepositoryRootBox").Text = Public;
            Control<TextBox>("RepositoryBindBox").Text = "127.0.0.1";
            Control<TextBox>("RepositoryPortBox").Text = "0";
            Control<CheckBox>("RepositoryConsentBox").IsChecked = consent;
        }

        public async Task StartAsync()
        {
            Configure(consent: true);
            Click("StartRepositoryButton");
            await WaitForStartedAsync();
        }

        public Task WaitForStartedAsync() => WaitForAsync(() =>
            Status.StartsWith("Sharing at https://", StringComparison.Ordinal) && Fingerprint.Length == 64);

        public async Task StopAsync()
        {
            Click("StopRepositoryButton");
            await WaitForAsync(() => Status.StartsWith("Repository stopped.", StringComparison.Ordinal));
        }

        public HttpClient Client(Uri uri)
        {
            var fingerprint = Fingerprint;
            return new(new HttpClientHandler
            {
                UseProxy = false,
                ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                    certificate?.GetCertHashString(HashAlgorithmName.SHA256) == fingerprint,
            }) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(5) };
        }

        public async ValueTask DisposeAsync()
        {
            if (Window?.IsVisible == true) await Window.CloseAndWaitAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
