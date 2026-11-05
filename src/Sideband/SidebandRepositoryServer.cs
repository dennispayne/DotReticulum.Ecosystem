using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Sideband;

/// <summary>
/// Bounded, opt-in, unauthenticated HTTPS mirror of administrator-controlled public files.
/// The root must not be writable by untrusted users while sharing. No downloads or executable plugins run here.
/// </summary>
public sealed class SidebandRepositoryServer : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SidebandRepositoryOptions _options;
    private readonly string _root;
    private readonly string _certificates;
    private CancellationTokenSource? _stop;
    private TcpListener? _listener;
    private X509Certificate2? _certificate;
    private Task _accept = Task.CompletedTask;
    private Task? _disposal;
    private IPEndPoint? _endpoint;
    private bool _started;
    private int _active;

    public SidebandRepositoryServer(SidebandRepositoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.CertificateDirectory);
        ArgumentNullException.ThrowIfNull(options.Address);
        if (options.Port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(options.Port));
        if (options.MaximumConnections is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(options.MaximumConnections));
        if (options.MaximumHeaderBytes is < 512 or > 32768) throw new ArgumentOutOfRangeException(nameof(options.MaximumHeaderBytes));
        if (options.MaximumFileBytes is < 1 or > 1024L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(options.MaximumFileBytes));
        if (options.MaximumPackageEntries is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(options.MaximumPackageEntries));
        if (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(options.RequestTimeout));
        _options = options;
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RootDirectory));
        _certificates = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.CertificateDirectory));
        if (Inside(_certificates, _root))
            throw new ArgumentException("Repository private certificate storage cannot be inside the public root.", nameof(options));
    }

    public event Action<string>? ErrorReported;
    public IPEndPoint? LocalEndpoint { get { lock (_gate) return _endpoint; } }
    public string? CertificateSha256 { get; private set; }
    public int ActiveConnections => Volatile.Read(ref _active);

    /// <summary>The token controls the server lifetime, not just startup. Dispose always joins owned work.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            if (_started) throw new InvalidOperationException("Repository server was already started; create a fresh instance to restart.");
            cancellationToken.ThrowIfCancellationRequested();
            if (!_options.AllowPublicUnauthenticatedAccess)
                throw new InvalidOperationException("Public unauthenticated repository access requires explicit opt-in.");
            if (!Directory.Exists(_root)) throw new DirectoryNotFoundException("Repository public root must already exist.");
            RequirePlainPath(_root);
            var certificate = SidebandRepositoryCertificate.Open(_certificates);
            var listener = new TcpListener(_options.Address, _options.Port);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                listener.Start(_options.MaximumConnections);
                _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _certificate = certificate;
                CertificateSha256 = certificate.GetCertHashString(HashAlgorithmName.SHA256);
                _listener = listener;
                _endpoint = (IPEndPoint)listener.LocalEndpoint;
                _started = true;
                _accept = Task.Run(() => AcceptAsync(listener, certificate, _stop.Token));
            }
            catch
            {
                listener.Stop();
                certificate.Dispose();
                throw;
            }
        }

        Report("Repository sharing is public and unauthenticated: every reachable peer can download its public files. " +
            "The self-signed certificate is not a public trust anchor; verify its SHA-256 fingerprint separately." +
            (OperatingSystem.IsWindows() ? " Certificate storage inherits Windows ACLs; restrict that directory to its owner." : ""));
        return Task.CompletedTask;
    }

    private async Task AcceptAsync(TcpListener listener, X509Certificate2 certificate, CancellationToken token)
    {
        var workers = new List<Task>(_options.MaximumConnections);
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                workers.RemoveAll(task => task.IsCompleted);
                if (Volatile.Read(ref _active) >= _options.MaximumConnections)
                {
                    client.Dispose();
                    Report("Repository connection limit reached; rejected a peer.");
                    continue;
                }

                Interlocked.Increment(ref _active);
                workers.Add(Task.Run(async () =>
                {
                    try { await ServeAsync(client, certificate, token).ConfigureAwait(false); }
                    finally
                    {
                        client.Dispose();
                        Interlocked.Decrement(ref _active);
                    }
                }, CancellationToken.None));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (SocketException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { Report("Repository listener failed: " + exception.Message); _stop?.Cancel(); }
        finally
        {
            listener.Stop();
            lock (_gate) _endpoint = null;
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
    }

    private async Task ServeAsync(TcpClient client, X509Certificate2 certificate, CancellationToken stop)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(_options.RequestTimeout);
        var token = deadline.Token;
        try
        {
            using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                AllowRenegotiation = false,
            }, token).ConfigureAwait(false);
            try
            {
                var target = await ReadRequestAsync(tls, token).ConfigureAwait(false);
                await RespondAsync(tls, target, token).ConfigureAwait(false);
            }
            catch (RequestFailure exception)
            {
                await WriteResponseAsync(tls, exception.Status, "text/plain; charset=utf-8",
                    Encoding.UTF8.GetBytes(exception.PublicMessage), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (OperationCanceledException) { Report("Repository peer exceeded its request deadline."); }
        catch (Exception exception) { Report("Repository peer failed: " + exception.Message); }
    }

    private async Task<string> ReadRequestAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[_options.MaximumHeaderBytes];
        var count = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), token).ConfigureAwait(false);
            if (read == 0) throw new IOException("Repository peer closed an incomplete request.");
            count += read;
            var end = buffer.AsSpan(0, count).IndexOf("\r\n\r\n"u8);
            if (end < 0)
            {
                if (count == buffer.Length) throw new RequestFailure(431, "Request headers too large");
                continue;
            }

            foreach (var b in buffer.AsSpan(0, end))
                if (b >= 127 || (b < 32 && b != 9 && b != 10 && b != 13))
                    throw new RequestFailure(400, "Invalid request headers");
            for (var i = 0; i < end; i++)
                if ((buffer[i] == 13 && (i + 1 >= end || buffer[i + 1] != 10)) ||
                    (buffer[i] == 10 && (i == 0 || buffer[i - 1] != 13)))
                    throw new RequestFailure(400, "Invalid line endings");
            var lines = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n", StringSplitOptions.None);
            var request = lines[0].Split(' ');
            if (request.Length != 3 || request[2] is not ("HTTP/1.0" or "HTTP/1.1"))
                throw new RequestFailure(400, "Invalid HTTP request");
            if (request[0] != "GET") throw new RequestFailure(405, request[0] == "HEAD" ? "" : "Only GET is supported");
            if (request[1].Length > 2048) throw new RequestFailure(414, "Request target too long");
            if (!request[1].StartsWith('/') || request[1].Contains('#'))
                throw new RequestFailure(400, "Expected an origin-form request target");
            var host = false;
            var length = false;
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0 || !line.AsSpan(0, colon).ContainsOnlyTokenCharacters())
                    throw new RequestFailure(400, "Invalid request header");
                var name = line[..colon];
                var value = line[(colon + 1)..].Trim();
                if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    if (host || value.Length == 0) throw new RequestFailure(400, "Invalid Host header");
                    host = true;
                }
                if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Expect", StringComparison.OrdinalIgnoreCase))
                    throw new RequestFailure(400, "Request bodies are not supported");
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    if (length || value != "0") throw new RequestFailure(400, "Request bodies are not supported");
                    length = true;
                }
            }

            if (request[2] == "HTTP/1.1" && !host) throw new RequestFailure(400, "Host header is required");
            if (count != end + 4) throw new RequestFailure(400, "Request bodies and pipelining are not supported");
            return request[1];
        }
    }

    private async Task RespondAsync(Stream stream, string target, CancellationToken token)
    {
        var query = target.IndexOf('?');
        var rawPath = query < 0 ? target : target[..query];
        for (var i = 0; i < rawPath.Length; i++)
            if (rawPath[i] == '%' && (i + 2 >= rawPath.Length || !Uri.IsHexDigit(rawPath[++i]) || !Uri.IsHexDigit(rawPath[++i])))
                throw new RequestFailure(400, "Invalid path encoding");
        var relative = Uri.UnescapeDataString(rawPath);
        if (relative.Contains('\\') || relative.Contains(':') || relative.Any(char.IsControl) ||
            relative.Split('/').Any(part => part is "." or ".."))
            throw new RequestFailure(403, "Forbidden");
        if (relative == "/") relative = "/index.html";
        if (relative == "/pkglist")
        {
            var directory = PublicPath("/pkg");
            if (!Directory.Exists(directory)) throw new RequestFailure(404, "Not found");
            var names = new List<string>();
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                token.ThrowIfCancellationRequested();
                try { RequirePlainPath(file); }
                catch (InvalidDataException) { continue; }
                if (names.Count == _options.MaximumPackageEntries)
                    throw new RequestFailure(413, "Package list exceeds configured limit");
                names.Add(Path.GetFileName(file));
            }

            names.Sort(StringComparer.Ordinal);
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartArray();
                foreach (var name in names) writer.WriteStringValue(name);
                writer.WriteEndArray();
            }
            if (output.Length > _options.MaximumHeaderBytes * 32L)
                throw new RequestFailure(413, "Package list exceeds configured limit");
            await WriteResponseAsync(stream, 200, "text/json", output.ToArray(), token).ConfigureAwait(false);
            return;
        }

        var path = PublicPath(relative);
        if (!File.Exists(path)) throw new RequestFailure(404, "Not found");
        await using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            32768, FileOptions.Asynchronous | FileOptions.SequentialScan);
        RequirePlainPath(path);
        var size = fileStream.Length;
        if (size > _options.MaximumFileBytes) throw new RequestFailure(413, "File exceeds configured limit");
        await WriteHeadersAsync(stream, 200, ContentType(path), size, token).ConfigureAwait(false);
        var buffer = new byte[32768];
        var remaining = size;
        while (remaining > 0)
        {
            var read = await fileStream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
            if (read == 0) throw new IOException("Repository file changed during transfer.");
            await stream.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private string PublicPath(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relative.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
        if (!Inside(path, _root)) throw new RequestFailure(403, "Forbidden");
        try { RequirePlainPath(path); }
        catch (InvalidDataException) { throw new RequestFailure(403, "Symbolic links and reparse points cannot be shared"); }
        return path;
    }

    internal static void RequirePlainPath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Repository paths must not contain symbolic links or reparse points.");
        }
    }

    private static bool Inside(string path, string root) =>
        string.Equals(path, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".apk" => "application/vnd.android.package-archive",
        ".js" => "text/javascript",
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".css" => "text/css",
        ".txt" => "text/plain; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".svg" => "image/svg+xml",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };

    private static async Task WriteResponseAsync(Stream stream, int status, string type, byte[] body, CancellationToken token)
    {
        await WriteHeadersAsync(stream, status, type, body.Length, token).ConfigureAwait(false);
        await stream.WriteAsync(body, token).ConfigureAwait(false);
    }

    private static Task WriteHeadersAsync(Stream stream, int status, string type, long length, CancellationToken token)
    {
        var reason = status switch
        {
            200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found",
            405 => "Method Not Allowed", 413 => "Content Too Large", 414 => "URI Too Long",
            431 => "Request Header Fields Too Large", _ => "Error",
        };
        var text = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {length.ToString(CultureInfo.InvariantCulture)}\r\n" +
            "Connection: close\r\nX-Content-Type-Options: nosniff\r\n" + (status == 405 ? "Allow: GET\r\n" : "") + "\r\n";
        return stream.WriteAsync(Encoding.ASCII.GetBytes(text), token).AsTask();
    }

    private void Report(string message)
    {
        if (ErrorReported is not { } callbacks) { Trace.TraceError("Sideband repository: {0}", message); return; }
        foreach (Action<string> callback in callbacks.GetInvocationList())
        {
            try { callback(message); }
            catch (Exception exception) { Trace.TraceError("Sideband repository error observer failed: {0}", exception.Message); }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposal is not null) return new(_disposal);
            _stop?.Cancel();
            _listener?.Stop();
            return new(_disposal = FinishAsync());
        }
    }

    private async Task FinishAsync()
    {
        await _accept.ConfigureAwait(false);
        lock (_gate)
        {
            _listener = null;
            _endpoint = null;
            _certificate?.Dispose();
            _certificate = null;
            _stop?.Dispose();
            _stop = null;
        }
    }

    private sealed class RequestFailure(int status, string publicMessage) : Exception(publicMessage)
    {
        public int Status { get; } = status;
        public string PublicMessage { get; } = publicMessage;
    }
}

internal static class SidebandRepositoryHeaderCharacters
{
    internal static bool ContainsOnlyTokenCharacters(this ReadOnlySpan<char> value)
    {
        foreach (var c in value)
            if (!char.IsAsciiLetterOrDigit(c) && !"!#$%&'*+-.^_`|~".Contains(c)) return false;
        return true;
    }
}
