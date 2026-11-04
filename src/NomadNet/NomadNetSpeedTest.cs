using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using DotReticulum.Applications;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace NomadNet;

public sealed record NomadNetSpeedResult(long Bytes, double Seconds, string Sha256, long PhysicalBytes)
{
    public double BytesPerSecond => Bytes / Math.Max(.001, Seconds);
}

public sealed class NomadNetSpeedTest : IAsyncDisposable
{
    public const string DestinationName = "nomadnetwork.utilities.speedtest";
    public static IReadOnlyDictionary<string, long> Sizes { get; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, long>(new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["2 KB"] = 2 * 1024, ["8 KB"] = 8 * 1024, ["32 KB"] = 32 * 1024,
            ["128 KB"] = 128 * 1024, ["512 KB"] = 512 * 1024, ["1 MB"] = 1024 * 1024,
            ["4 MB"] = 4 * 1024 * 1024, ["16 MB"] = 16 * 1024 * 1024,
            ["64 MB"] = 64 * 1024 * 1024, ["256 MB"] = 256 * 1024 * 1024,
            ["1000 MB"] = 1000L * 1024 * 1024, ["2000 MB"] = 2000L * 1024 * 1024,
        });
    private readonly ReticulumNode _node;
    private readonly Identity _identity;
    private readonly string _directory;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private LocalDestination? _destination;
    private byte[]? _expected;
    private Link? _incoming;
    private CancellationTokenSource? _listenStop;
    private Stopwatch? _receiveClock;
    private long _incomingDeadline;
    private long _identifiedDeadline;
    private Task _monitor = Task.CompletedTask;
    private Task _reading = Task.CompletedTask;
    private Task? _disposal;
    private readonly Dictionary<int, long> _receivedPhysical = [];
    private NomadNetSpeedResult? _lastReceived;
    private bool _disposed;

    public NomadNetSpeedTest(ReticulumNode node, Identity identity, string directory)
    {
        _node = node;
        _identity = identity;
        _directory = directory;
        DestinationHash = Convert.ToHexStringLower(Destination.ComputeHashFromNameAndIdentity(DestinationName, identity.Hash));
    }

    public string DestinationHash { get; }
    public Action<string>? Log { get; set; }
    public NomadNetSpeedResult? LastReceived { get { lock (_gate) return _lastReceived; } }
    public bool Listening { get { lock (_gate) return _destination is not null; } }

    public static bool IsAuthorized(byte[]? expected, Identity remote) =>
        expected is { Length: Destination.HashLength } &&
        CryptographicOperations.FixedTimeEquals(expected,
            Destination.ComputeHashFromNameAndIdentity(DestinationName, remote.Hash));

    public async Task StartListeningAsync(string peerHash, CancellationToken token = default)
    {
        var expected = Convert.FromHexString(NomadNetAddress.NormalizeHash(peerHash));
        await _lifecycle.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
            if (Listening) throw new InvalidOperationException("Stop the existing utility listener before selecting another peer.");
            var hint = await _node.CallManagementAsync(new() { ["get"] = "medium_path_timeout" }, token).ConfigureAwait(false);
            var medium = hint is null ? 0 : MsgPack.ToDouble(hint);
            _ = NomadNetUtilities.TimeoutSeconds(0, medium);
            var local = new LocalDestination(_identity,
                Destination.Create(DestinationType.Single, "nomadnetwork", ["utilities", "speedtest"], _identity))
            {
                LinkEstablished = Incoming,
                AcceptLinks = true,
                MaxRequestSize = 0,
            };
            lock (_gate)
            {
                _expected = expected;
                _destination = local;
                _listenStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                _identifiedDeadline = (long)(Math.Max(8, medium) * Stopwatch.Frequency);
            }
            try { _node.Router.RegisterLocalDestination(local); }
            catch
            {
                lock (_gate) { _destination = null; _expected = null; _listenStop?.Dispose(); _listenStop = null; }
                local.Dispose();
                throw;
            }
            _monitor = MonitorAsync(_listenStop.Token);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopListeningAsync(CancellationToken token = default)
    {
        await _lifecycle.WaitAsync(token).ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    private async Task StopCoreAsync()
    {
        LocalDestination? destination;
        Link? incoming;
        CancellationTokenSource? stop;
        lock (_gate)
        {
            destination = _destination;
            _destination = null;
            _expected = null;
            incoming = _incoming;
            _incoming = null;
            stop = _listenStop;
        }
        if (destination is not null) _node.Router.DeregisterLocalDestination(destination);
        if (stop is not null) await stop.CancelAsync().ConfigureAwait(false);
        incoming?.Teardown();
        await _monitor.ConfigureAwait(false);
        Task reading;
        lock (_gate) reading = _reading;
        await reading.ConfigureAwait(false);
        destination?.Dispose();
        lock (_gate) _listenStop = null;
        stop?.Dispose();
    }

    private void Incoming(Link link)
    {
        var accepted = false;
        lock (_gate)
        {
            if (!_disposed && _destination is not null && _incoming is null && _reading.IsCompleted)
            {
                _incoming = link;
                _receiveClock = null;
                _receivedPhysical.Clear();
                _incomingDeadline = Stopwatch.GetTimestamp() + _identifiedDeadline;
                accepted = true;
            }
        }
        if (!accepted) { link.Teardown(); return; }
        link.ResourcePolicy = link.ResourcePolicy with
        {
            MaxDataSize = NomadNetUtilities.MaximumTransferBytes,
            MemoryThreshold = 1024 * 1024,
            MaxIncomingResources = 1,
            MaxCompletedFileResources = 1,
            MaxSegments = 4096,
            StorageDirectory = NomadNetUtilities.CreatePrivateWorkDirectory(_directory),
        };
        link.Closed = closed => { lock (_gate) if (ReferenceEquals(_incoming, closed)) _incoming = null; };
        link.RemoteIdentified = (current, remote) => Identify(current, remote);
        if (link.RemoteIdentity is { } identified) Identify(link, identified);
    }

    private void Identify(Link link, Identity remote)
    {
        bool accepted;
        lock (_gate)
        {
            accepted = !_disposed && _destination is not null && ReferenceEquals(_incoming, link) &&
                IsAuthorized(_expected, remote);
            if (accepted)
                _incomingDeadline = Stopwatch.GetTimestamp() +
                    (long)(NomadNetUtilities.TransferTimeout.TotalSeconds * Stopwatch.Frequency);
        }
        if (!accepted)
        {
            Report("Rejected speed test from an unauthorized peer.");
            link.Teardown();
            return;
        }
        link.ResourceAdvertised = advertisement =>
        {
            lock (_gate)
                return !_disposed && ReferenceEquals(_incoming, link) && _destination is not null &&
                    advertisement.DataSize is > 0 and <= NomadNetUtilities.MaximumTransferBytes &&
                    !advertisement.HasMetadata && !advertisement.IsRequest && !advertisement.IsResponse;
        };
        link.ResourceStarted = resource =>
        {
            lock (_gate)
            {
                if (_destination is null || !ReferenceEquals(_incoming, resource.Link)) return;
                _receiveClock ??= Stopwatch.StartNew();
                _receivedPhysical[resource.SegmentIndex] = resource.Size;
            }
        };
        link.ResourceReceived = Receive;
        link.ResourceStrategy = ResourceStrategy.AcceptApp;
    }

    private void Receive(Resource resource)
    {
        if (resource.Status == ResourceStatus.Complete && resource.SegmentIndex < resource.TotalSegments) return;
        if (resource.Status != ResourceStatus.Complete)
        {
            Report("Incoming speed test failed.");
            resource.Dispose();
            resource.Link.Teardown();
            return;
        }
        Stream input;
        try { input = resource.OpenRead(); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            Report("Could not read completed speed test: " + exception.Message);
            resource.Dispose();
            resource.Link.Teardown();
            return;
        }
        var admitted = false;
        lock (_gate)
        {
            if (!_disposed && _destination is not null && _listenStop is { IsCancellationRequested: false })
            {
                var seconds = _receiveClock?.Elapsed.TotalSeconds ?? .001;
                var token = _listenStop.Token;
                _receivedPhysical[resource.SegmentIndex] = resource.Size;
                var physical = _receivedPhysical.Values.Sum();
                _reading = Task.Run(() => ReadResultAsync(resource, input, seconds, physical, token));
                admitted = true;
            }
        }
        if (admitted) return;
        input.Dispose();
        resource.Dispose();
        resource.Link.Teardown();
    }

    private async Task ReadResultAsync(Resource resource, Stream input, double seconds, long physical, CancellationToken token)
    {
        var completed = false;
        try
        {
            await using (input.ConfigureAwait(false))
            {
                var bytes = input.Length;
                var hash = await SHA256.HashDataAsync(input, token).ConfigureAwait(false);
                lock (_gate) _lastReceived = new(bytes, seconds, Convert.ToHexStringLower(hash), physical);
                completed = true;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or CryptographicException)
        {
            Report("Could not inspect completed speed test: " + exception.Message);
        }
        finally
        {
            resource.Dispose();
            if (!completed) resource.Link.Teardown();
        }
    }

    private async Task MonitorAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                await Task.Delay(100, token).ConfigureAwait(false);
                Link? expired = null;
                lock (_gate)
                    if (_incoming is not null && Stopwatch.GetTimestamp() > _incomingDeadline)
                        expired = _incoming;
                if (expired is not null)
                {
                    Report("Incoming speed test exceeded its identification or transfer deadline.");
                    expired.Teardown();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    public async Task<NomadNetSpeedResult> SendAsync(string peerHash, string size = "1 MB",
        Action<double>? progress = null, CancellationToken token = default)
    {
        var destination = Convert.FromHexString(NomadNetAddress.NormalizeHash(peerHash));
        if (!Sizes.TryGetValue(size, out var length)) throw new ArgumentException("Unknown primary speed-test size.", nameof(size));
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        stop.CancelAfter(NomadNetUtilities.TransferTimeout);
        if (!await _sending.WaitAsync(0, stop.Token).ConfigureAwait(false))
            throw new InvalidOperationException("A speed test is already sending.");
        string? path = null;
        Link? link = null;
        FileStream? file = null;
        try
        {
            lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
            var root = NomadNetUtilities.CreatePrivateWorkDirectory(_directory);
            path = Path.Combine(root, "speed-" + Guid.NewGuid().ToString("N"));
            file = NomadNetUtilities.CreatePrivateFile(path, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            try
            {
                long written = 0;
                while (written < length)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    var count = (int)Math.Min(buffer.Length, length - written);
                    RandomNumberGenerator.Fill(buffer.AsSpan(0, count));
                    hash.AppendData(buffer, 0, count);
                    await file.WriteAsync(buffer.AsMemory(0, count), stop.Token).ConfigureAwait(false);
                    written += count;
                }
                await file.FlushAsync(stop.Token).ConfigureAwait(false);
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
            var utility = new NomadNetUtilities(_node, _identity, _directory);
            var connected = await ClientLinks.ConnectAsync(_node, destination,
                await utility.GetTimeoutAsync(destination, stop.Token).ConfigureAwait(false), stop.Token).ConfigureAwait(false);
            link = connected.Link ?? throw new IOException(connected.Error);
            link.ResourcePolicy = link.ResourcePolicy with
                { MaxDataSize = NomadNetUtilities.MaximumTransferBytes, MaxSegments = 4096 };
            link.Identify(_identity);
            await Task.Delay(TimeSpan.FromMilliseconds(600), stop.Token).ConfigureAwait(false);
            var completion = new TaskCompletionSource<ResourceStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            link.Closed = _ => completion.TrySetResult(ResourceStatus.Failed);
            var segments = new System.Collections.Concurrent.ConcurrentDictionary<int, long>();
            var clock = Stopwatch.StartNew();
            file.Position = 0;
            await using var transfer = link.SendResource(file, autoCompress: false,
                concluded: resource =>
                {
                    segments[resource.SegmentIndex] = resource.Size;
                    completion.TrySetResult(resource.Status);
                }, progress: resource =>
                {
                    segments[resource.SegmentIndex] = resource.Size;
                    progress?.Invoke(resource.Progress);
                }, leaveOpen: true, cancellationToken: stop.Token);
            var result = await completion.Task.WaitAsync(stop.Token).ConfigureAwait(false);
            if (result != ResourceStatus.Complete) throw new IOException("The speed test failed or was rejected.");
            return new(length, clock.Elapsed.TotalSeconds, Convert.ToHexStringLower(hash.GetHashAndReset()), segments.Values.Sum());
        }
        finally
        {
            link?.Teardown();
            if (file is not null) await file.DisposeAsync().ConfigureAwait(false);
            if (path is not null && File.Exists(path)) File.Delete(path);
            _sending.Release();
        }
    }

    private void Report(string message)
    {
        try
        {
            if (Log is { } log) log(message);
            else Console.Error.WriteLine(message);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Speed-test error observer failed: " + exception.Message + "; " + message);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposal is not null) return new(_disposal);
            _disposed = true;
            return new(_disposal = Task.Run(FinishAsync));
        }
    }

    private async Task FinishAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            await _sending.WaitAsync().ConfigureAwait(false);
            _sending.Release();
        }
        finally { _lifecycle.Release(); }
        _stop.Dispose();
    }

    public static string FormatRate(double bytesPerSecond)
    {
        if (!double.IsFinite(bytesPerSecond) || bytesPerSecond < 0) throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        var bits = bytesPerSecond * 8;
        if (!double.IsFinite(bits)) throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        foreach (var unit in new[] { "bps", "Kbps", "Mbps", "Gbps" })
        {
            if (bits < 1000) return bits.ToString("F1", CultureInfo.InvariantCulture) + " " + unit;
            bits /= 1000;
        }
        return bits.ToString("F1", CultureInfo.InvariantCulture) + " Tbps";
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        var value = (double)bytes;
        foreach (var unit in new[] { "B", "KB", "MB", "GB", "TB" })
        {
            if (value < 1024) return value.ToString("F1", CultureInfo.InvariantCulture) + " " + unit;
            value /= 1024;
        }
        return value.ToString("F1", CultureInfo.InvariantCulture) + " PB";
    }
}
