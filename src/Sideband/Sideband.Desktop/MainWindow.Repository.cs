using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Sideband;

namespace Sideband.Desktop;

public sealed partial class MainWindow
{
    private const string RepositoryPublicWarning =
        "PUBLIC AND UNAUTHENTICATED: every reachable peer can download every file in this dedicated public directory. " +
        "Do not include private profiles, identities or keys, or allow untrusted users to modify it. " +
        "This is an HTTPS software mirror, not Reticulum/rngit hosting. Nothing starts automatically.";
    private readonly object _repositoryGate = new();
    private RepositoryLease? _repositoryLease;
    private Task? _repositoryDisposal;
    private bool _repositoryStarting;
    private bool _repositoryStopping;
    private bool _repositoryNoticePending;
    private string? _repositoryNotice;
    private SidebandRepositoryServer? _repositoryNoticeServer;

    internal bool RepositorySmokeVerified { get; private set; }

    private void StartRepositoryClicked(object? sender, RoutedEventArgs e)
    {
        if (_closing || _repositoryStarting || _repositoryStopping) return;
        lock (_repositoryGate)
            if (_repositoryDisposal is not null || _repositoryLease is not null) return;
        TrackOperation(async () =>
        {
            RepositoryLease? lease = null;
            try
            {
                RepositorySmokeVerified = false;
                if (RepositoryControl<CheckBox>("RepositoryConsentBox").IsChecked != true)
                    throw new InvalidOperationException("Confirm public, unauthenticated sharing before starting the repository.");
                var root = RepositoryControl<TextBox>("RepositoryRootBox").Text?.Trim();
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                    throw new ArgumentException("Choose an existing, dedicated public directory.");
                root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
                var profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_store.DataDirectory));
                if (RepositoryContainsDirectory(root, profile) || RepositoryContainsDirectory(profile, root))
                    throw new ArgumentException("The public directory must be outside the Sideband profile directory and cannot contain it.");
                if (!IPAddress.TryParse(RepositoryControl<TextBox>("RepositoryBindBox").Text?.Trim(), out var address))
                    throw new ArgumentException("Enter a literal IPv4 or IPv6 bind address, not a hostname.");
                if (!int.TryParse(RepositoryControl<TextBox>("RepositoryPortBox").Text, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var port) || port is < 0 or > 65535)
                    throw new ArgumentException("Enter a port from 0 to 65535; 0 chooses an available port.");
                var server = new SidebandRepositoryServer(new()
                {
                    RootDirectory = root,
                    CertificateDirectory = Path.Combine(_store.DataDirectory, "repository-certificate"),
                    AllowPublicUnauthenticatedAccess = true,
                    Address = address,
                    Port = port,
                });
                Action<string> reported = message => QueueRepositoryNotice(server, message);
                server.ErrorReported += reported;
                lock (_repositoryGate)
                {
                    lease = new RepositoryLease(server, reported, _windowCancellation.Token);
                    _repositoryLease = lease;
                }
                _repositoryStarting = true;
                SetRepositoryControls(active: true);
                RepositoryControl<TextBlock>("RepositoryStatusText").Text = "Starting HTTPS repository.\n" + RepositoryPublicWarning;
                await lease.StartTask;
                if (RepositoryIsCurrent(lease) && !_closing && !lease.Token.IsCancellationRequested)
                {
                    RepositoryControl<TextBox>("RepositoryFingerprintText").Text = server.CertificateSha256;
                    RepositoryControl<TextBlock>("RepositoryStatusText").Text = RepositoryListeningStatus(lease);
                    if (_smokeUi && Program.SmokeRepositoryRoot is not null)
                    {
                        await VerifyRepositorySmokeAsync(server, lease.Token);
                        if (!RepositoryIsCurrent(lease) || _closing || lease.Token.IsCancellationRequested) return;
                        RepositorySmokeVerified = true;
                        Console.WriteLine("SMOKE_UI_REPOSITORY_HTTPS=1");
                        InvalidateVisual();
                    }
                }
            }
            catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested || lease?.Token.IsCancellationRequested == true) { }
            catch (Exception exception)
            {
                if (!_closing)
                {
                    var message = "Repository start failed: " + exception.Message;
                    RepositoryControl<TextBlock>("RepositoryStatusText").Text = message + "\n" + RepositoryPublicWarning;
                    ShowError(message);
                }
                else Trace.TraceError("Sideband repository startup failed during close: {0}", exception.Message);
                if (lease is not null) await lease.StopAsync();
            }
            finally
            {
                _repositoryStarting = false;
                if (lease is not null && (lease.Token.IsCancellationRequested || lease.Server.LocalEndpoint is null))
                {
                    await lease.StopAsync();
                    ClearRepositoryLease(lease);
                }
                if (!_closing && !HasRepositoryLease())
                {
                    SetRepositoryControls(active: false);
                    RepositoryControl<TextBox>("RepositoryFingerprintText").Text = "";
                }
            }
        });
    }

    private static async Task VerifyRepositorySmokeAsync(SidebandRepositoryServer server, CancellationToken cancellationToken)
    {
        var endpoint = server.LocalEndpoint ?? throw new InvalidOperationException("Repository smoke listener is not running.");
        if (!IPAddress.IsLoopback(endpoint.Address))
            throw new InvalidOperationException("Repository smoke verification only connects to an explicit loopback endpoint.");
        var fingerprint = Convert.FromHexString(server.CertificateSha256 ??
            throw new InvalidOperationException("Repository smoke certificate fingerprint is unavailable."));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var handler = new HttpClientHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            MaxConnectionsPerServer = 1,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null && CryptographicOperations.FixedTimeEquals(
                    certificate.GetCertHash(HashAlgorithmName.SHA256), fingerprint),
        };
        using var client = new HttpClient(handler)
        {
            BaseAddress = new UriBuilder("https", endpoint.Address.ToString(), endpoint.Port).Uri,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using (var index = await client.GetAsync("/", HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false))
        {
            if (index.StatusCode != HttpStatusCode.OK)
                throw new InvalidDataException("Repository smoke HTTPS index did not return HTTP 200.");
            if ((await ReadRepositorySmokeBodyAsync(index, 65536, deadline.Token).ConfigureAwait(false)).Length == 0)
                throw new InvalidDataException("Repository smoke HTTPS index was empty.");
        }
        using var packages = await client.GetAsync("/pkglist", HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (packages.StatusCode != HttpStatusCode.OK)
            throw new InvalidDataException("Repository smoke HTTPS package index did not return HTTP 200.");
        var body = await ReadRepositorySmokeBodyAsync(packages, 262144, deadline.Token).ConfigureAwait(false);
        using var names = JsonDocument.Parse(body);
        if (names.RootElement.ValueKind != JsonValueKind.Array || names.RootElement.GetArrayLength() is < 1 or > 1024 ||
            names.RootElement.EnumerateArray().Any(name => name.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(name.GetString())))
            throw new InvalidDataException("Repository smoke HTTPS package index must contain a nonempty array of package names.");
    }

    private static async Task<byte[]> ReadRepositorySmokeBodyAsync(HttpResponseMessage response, int maximumBytes, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException("Repository smoke HTTPS response exceeds its bounded body limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) return output.ToArray();
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("Repository smoke HTTPS response exceeds its bounded body limit.");
            output.Write(buffer, 0, read);
        }
    }

    private void StopRepositoryClicked(object? sender, RoutedEventArgs e)
    {
        if (_closing || _repositoryStopping) return;
        RepositoryLease? lease;
        lock (_repositoryGate) lease = _repositoryLease;
        if (lease is null) return;
        _repositoryStopping = true;
        var stop = lease.StopAsync();
        RepositoryControl<Button>("StopRepositoryButton").IsEnabled = false;
        RepositoryControl<TextBlock>("RepositoryStatusText").Text = "Stopping HTTPS repository.";
        TrackOperation(async () =>
        {
            try { await stop; }
            catch (Exception exception) { ShowError("Repository stop failed: " + exception.Message); }
            finally
            {
                ClearRepositoryLease(lease);
                _repositoryStopping = false;
                if (!_closing)
                {
                    SetRepositoryControls(active: false);
                    RepositoryControl<CheckBox>("RepositoryConsentBox").IsChecked = false;
                    RepositoryControl<TextBox>("RepositoryFingerprintText").Text = "";
                    RepositoryControl<TextBlock>("RepositoryStatusText").Text = "Repository stopped. No files are being shared.\n" + RepositoryPublicWarning;
                }
            }
        });
    }

    internal Task DisposeRepositoryAsync()
    {
        lock (_repositoryGate)
        {
            if (_repositoryDisposal is not null) return _repositoryDisposal;
            var lease = _repositoryLease;
            _repositoryLease = null;
            return _repositoryDisposal = lease?.StopAsync() ?? Task.CompletedTask;
        }
    }

    private void QueueRepositoryNotice(SidebandRepositoryServer server, string message)
    {
        lock (_repositoryGate)
        {
            if (_repositoryDisposal is not null || _repositoryLease?.Server != server) return;
            _repositoryNotice = message;
            _repositoryNoticeServer = server;
            if (_repositoryNoticePending) return;
            _repositoryNoticePending = true;
        }
        Dispatcher.UIThread.Post(() =>
        {
            RepositoryLease? lease;
            string? notice;
            lock (_repositoryGate)
            {
                _repositoryNoticePending = false;
                notice = _repositoryNotice;
                lease = _repositoryLease;
                if (_repositoryDisposal is not null || lease?.Server != _repositoryNoticeServer) return;
            }
            if (_closing || _repositoryStopping || lease.Token.IsCancellationRequested) return;
            RepositoryControl<TextBlock>("RepositoryStatusText").Text = RepositoryListeningStatus(lease) + "\nLast notice: " + notice;
            if (notice?.StartsWith("Repository sharing is public", StringComparison.Ordinal) != true)
                ShowError("Repository: " + notice);
        });
    }

    private T RepositoryControl<T>(string name) where T : Control =>
        this.FindControl<T>(name) ?? throw new InvalidOperationException("Missing repository desktop control: " + name);

    private void SetRepositoryControls(bool active)
    {
        RepositoryControl<Button>("StartRepositoryButton").IsEnabled = !active;
        RepositoryControl<Button>("StopRepositoryButton").IsEnabled = active && !_repositoryStopping;
        foreach (var name in new[] { "RepositoryRootBox", "RepositoryBindBox", "RepositoryPortBox", "RepositoryConsentBox" })
            RepositoryControl<Control>(name).IsEnabled = !active;
    }

    private static string RepositoryListeningStatus(RepositoryLease lease)
    {
        var endpoint = lease.Server.LocalEndpoint;
        var location = endpoint is null ? "Repository is not listening." :
            $"Sharing at {new UriBuilder("https", endpoint.Address.ToString(), endpoint.Port).Uri}";
        return location + "\n" + RepositoryPublicWarning + "\nSelf-signed certificate: verify the displayed SHA-256 fingerprint separately.";
    }

    private static bool RepositoryContainsDirectory(string parent, string child)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return child.Equals(parent, comparison) ||
            child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, comparison);
    }

    private bool RepositoryIsCurrent(RepositoryLease lease)
    {
        lock (_repositoryGate) return _repositoryDisposal is null && ReferenceEquals(_repositoryLease, lease);
    }

    private bool HasRepositoryLease()
    {
        lock (_repositoryGate) return _repositoryLease is not null;
    }

    private void ClearRepositoryLease(RepositoryLease lease)
    {
        lock (_repositoryGate)
            if (ReferenceEquals(_repositoryLease, lease)) _repositoryLease = null;
    }

    private sealed class RepositoryLease
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _stop;
        private readonly Action<string> _reported;
        private Task? _shutdown;
        public SidebandRepositoryServer Server { get; }
        public Task StartTask { get; }
        public CancellationToken Token { get; }

        public RepositoryLease(SidebandRepositoryServer server, Action<string> reported, CancellationToken windowToken)
        {
            Server = server;
            _reported = reported;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(windowToken);
            Token = _stop.Token;
            StartTask = Task.Run(() => Server.StartAsync(Token));
        }

        public Task StopAsync()
        {
            lock (_gate)
            {
                if (_shutdown is not null) return _shutdown;
                _stop.Cancel();
                return _shutdown = FinishAsync();
            }
        }

        private async Task FinishAsync()
        {
            try
            {
                try { await StartTask.ConfigureAwait(false); }
                catch (OperationCanceledException) when (Token.IsCancellationRequested) { }
                catch (Exception exception) { Trace.TraceError("Sideband repository startup failed: {0}", exception.Message); }
                await Server.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                Server.ErrorReported -= _reported;
                _stop.Dispose();
            }
        }
    }
}
