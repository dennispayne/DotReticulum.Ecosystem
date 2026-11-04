using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotReticulum.Applications;
using DotReticulum.Applications.Rncp;
using DotReticulum.Core;
using DotReticulum.Transport;

namespace NomadNet;

public sealed record NomadNetProbeReply(bool Delivered, double? RoundTripSeconds, int? Hops,
    double? Rssi = null, double? Snr = null, double? Quality = null);
public sealed record NomadNetProbeResult(IReadOnlyList<NomadNetProbeReply> Replies)
{
    public int Sent => Replies.Count;
    public int Received => Replies.Count(reply => reply.Delivered);
    public double LossPercent => Sent == 0 ? 0 : (1d - (double)Received / Sent) * 100;
}

public sealed class NomadNetUtilities(ReticulumNode node, Identity identity, string directory)
{
    public const int DefaultProbeSize = 16;
    public const double DefaultTimeoutSeconds = 12;
    public const int MaximumProbeCount = 1000;
    public const long MaximumFileBytes = MaximumTransferBytes;
    public const long MaximumTransferBytes = 2000L * 1024 * 1024;
    public static readonly TimeSpan TransferTimeout = TimeSpan.FromMinutes(5);

    public static (int Size, int Count) PrepareProbe(string? size, string? count)
    {
        var parsedSize = Parse(size, DefaultProbeSize, Packet.Mtu);
        var parsedCount = Parse(count, 1, MaximumProbeCount);
        return (parsedSize, parsedCount);

        static int Parse(string? text, int fallback, int maximum)
        {
            if (text?.Length > 32) throw new ArgumentOutOfRangeException(nameof(text), "Probe input exceeds native resource bounds.");
            if (!BigInteger.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return fallback;
            if (value > maximum) throw new ArgumentOutOfRangeException(nameof(text), "Probe input exceeds native resource bounds.");
            return value < 1 ? 1 : (int)value;
        }
    }

    public static double TimeoutSeconds(double firstHop, double medium)
    {
        if (!double.IsFinite(firstHop) || !double.IsFinite(medium) || firstHop < 0 || medium < 0 ||
            firstHop > 3600 || medium > 3600)
            throw new InvalidDataException("Reticulum utility timeouts exceed native safety bounds.");
        return Math.Max(DefaultTimeoutSeconds + firstHop, medium);
    }

    public async Task<TimeSpan> GetTimeoutAsync(byte[] destination, CancellationToken token = default)
    {
        ValidateHash(destination);
        var first = await node.CallManagementAsync(new()
            { ["get"] = "first_hop_timeout", ["destination_hash"] = destination }, token).ConfigureAwait(false);
        var medium = await node.CallManagementAsync(new() { ["get"] = "medium_path_timeout" }, token).ConfigureAwait(false);
        return TimeSpan.FromSeconds(TimeoutSeconds(first is null ? 0 : MsgPack.ToDouble(first),
            medium is null ? 0 : MsgPack.ToDouble(medium)));
    }

    public async Task<object?> GetTableAsync(string table, CancellationToken token = default)
    {
        var key = table switch
        {
            "paths" => "path_table",
            "rates" => "rate_table",
            "interfaces" => "interface_stats",
            _ => throw new ArgumentException("Expected paths, rates or interfaces.", nameof(table)),
        };
        var call = new Dictionary<object, object?> { ["get"] = key };
        if (table == "paths") call["max_hops"] = null;
        return await node.CallManagementAsync(call, token).ConfigureAwait(false);
    }

    public async Task<string> ResolveIdentityAsync(byte[] destination, CancellationToken token = default)
    {
        ValidateHash(destination);
        using var remote = await node.ResolveIdentityAsync(destination,
            await GetTimeoutAsync(destination, token).ConfigureAwait(false), token).ConfigureAwait(false)
            ?? throw new TimeoutException("Identity was not discovered before the utility deadline.");
        return remote.HexHash;
    }

    public async Task<object> ResolvePathAsync(byte[] destination, CancellationToken token = default)
    {
        ValidateHash(destination);
        if (!await node.WaitForPathAsync(destination,
            await GetTimeoutAsync(destination, token).ConfigureAwait(false), token).ConfigureAwait(false))
            throw new TimeoutException("Path was not discovered before the utility deadline.");
        var next = await node.CallManagementAsync(new()
            { ["get"] = "next_hop", ["destination_hash"] = destination }, token).ConfigureAwait(false);
        var name = await node.CallManagementAsync(new()
            { ["get"] = "next_hop_if_name", ["destination_hash"] = destination }, token).ConfigureAwait(false);
        return new Dictionary<object, object?> { ["destination"] = destination, ["hops"] = node.Router.HopsTo(destination),
            ["nextHop"] = next, ["interface"] = name };
    }

    public async Task<bool> DropPathAsync(byte[] destination, CancellationToken token = default)
    {
        ValidateHash(destination);
        return await node.CallManagementAsync(new()
            { ["drop"] = "path", ["destination_hash"] = destination }, token).ConfigureAwait(false) is true;
    }

    public async Task<NomadNetProbeResult> ProbeAsync(string fullName, byte[] destination, int size = DefaultProbeSize,
        int count = 1, CancellationToken token = default)
    {
        ValidateHash(destination);
        if (size is < 1 or > Packet.Mtu || count is < 1 or > MaximumProbeCount)
            throw new ArgumentOutOfRangeException(nameof(size), "Probe size/count exceeds native resource bounds.");
        var (app, aspects) = Destination.SplitFullName(fullName);
        var timeout = await GetTimeoutAsync(destination, token).ConfigureAwait(false);
        using var remote = await node.ResolveIdentityAsync(destination, timeout, token).ConfigureAwait(false)
            ?? throw new TimeoutException("No identity was discovered for the probe destination.");
        var target = Destination.Create(DestinationType.Single, app, aspects, remote);
        if (!target.Hash.AsSpan().SequenceEqual(destination))
            throw new ArgumentException("The destination name does not match the discovered identity and address.", nameof(fullName));
        var replies = new List<NomadNetProbeReply>(count);
        for (var index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            var packet = Packet.Create(PacketType.Data, DestinationType.Single, destination,
                remote.Encrypt(RandomNumberGenerator.GetBytes(size)));
            var receipt = await node.Router.SendWithReceiptAsync(packet, remote, timeout, token).ConfigureAwait(false);
            var delivered = false;
            if (receipt is not null)
            {
                try { delivered = await receipt.Completion.WaitAsync(timeout, token).ConfigureAwait(false); }
                catch (TimeoutException) { }
            }
            var proof = receipt?.ProofPacket;
            var physical = proof?.PhysicalLayer ?? default;
            double? quality = null;
            if (proof is not null && node.IsConnectedToSharedInstance)
            {
                var hash = proof.CalculateHash();
                physical = new(await SignalAsync("packet_rssi", hash).ConfigureAwait(false),
                    await SignalAsync("packet_snr", hash).ConfigureAwait(false));
                quality = await SignalAsync("packet_q", hash).ConfigureAwait(false);
            }
            replies.Add(new(delivered && receipt?.Status == PacketReceiptStatus.Delivered,
                receipt?.Rtt, node.Router.HopsTo(destination), physical.Rssi, physical.Snr, quality));
        }
        return new(replies);

        async Task<double?> SignalAsync(string name, byte[] hash)
        {
            var value = await node.CallManagementAsync(new()
                { ["get"] = name, ["packet_hash"] = hash }, token).ConfigureAwait(false);
            if (value is null) return null;
            var measurement = MsgPack.ToDouble(value);
            if (!double.IsFinite(measurement)) throw new InvalidDataException("Invalid proof reception measurement.");
            return measurement;
        }
    }

    public static string FormatReceptionStats(NomadNetProbeReply reply)
    {
        var result = new StringBuilder();
        foreach (var (value, label, unit) in new[]
            { (reply.Rssi, "RSSI ", " dBm"), (reply.Snr, "SNR ", " dB"), (reply.Quality, "Q ", "%") })
        {
            if (value is not { } measurement) continue;
            if (!double.IsFinite(measurement)) throw new InvalidDataException("Invalid proof reception measurement.");
            result.Append(" [").Append(label).Append(measurement.ToString("G", CultureInfo.InvariantCulture)).Append(unit).Append(']');
        }
        return result.ToString();
    }

    public async Task<RncpResult> SendFileAsync(byte[] destination, string file, Action<double>? progress = null,
        CancellationToken token = default)
    {
        ValidateHash(destination);
        token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TransferTimeout);
        token = deadline.Token;
        await using var input = NomadNetLocalFiles.OpenRead(file);
        var length = input.Length;
        if (length > MaximumFileBytes)
            throw new InvalidDataException("Utility rncp files are limited to 2000 MiB.");
        var root = CreatePrivateWorkDirectory(directory);
        var job = Path.Combine(root, Guid.NewGuid().ToString("N"));
        CreatePrivateDirectory(job);
        try
        {
            var path = Path.Combine(job, Path.GetFileName(Path.GetFullPath(file)));
            await using var staged = CreatePrivateFile(path, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                var remaining = length;
                while (remaining > 0)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
                    if (read == 0) throw new IOException("Utility input changed while being staged.");
                    await staged.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    remaining -= read;
                }
                if (await input.ReadAsync(buffer.AsMemory(0, 1), token).ConfigureAwait(false) != 0)
                    throw new IOException("Utility input grew while being staged.");
                await staged.FlushAsync(token).ConfigureAwait(false);
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
            var connected = await ClientLinks.ConnectAsync(node, destination,
                await GetTimeoutAsync(destination, token).ConfigureAwait(false), deadline.Token).ConfigureAwait(false);
            var link = connected.Link ?? throw new IOException(connected.Error);
            try
            {
                link.ResourcePolicy = link.ResourcePolicy with { MaxDataSize = MaximumFileBytes, MaxSegments = 4096 };
                link.Identify(identity);
                var completion = new TaskCompletionSource<ResourceStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
                var segments = new System.Collections.Concurrent.ConcurrentDictionary<int, long>();
                link.Closed = _ => completion.TrySetResult(ResourceStatus.Failed);
                staged.Position = 0;
                await using var transfer = link.SendResource(staged,
                    new Dictionary<object, object?> { ["name"] = Encoding.UTF8.GetBytes(Path.GetFileName(file)) },
                    autoCompress: true, concluded: resource =>
                    {
                        segments[resource.SegmentIndex] = resource.Size;
                        completion.TrySetResult(resource.Status);
                    }, progress: resource =>
                    {
                        segments[resource.SegmentIndex] = resource.Size;
                        progress?.Invoke(resource.Progress);
                    }, leaveOpen: true, cancellationToken: deadline.Token);
                var status = await completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
                return new(status == ResourceStatus.Complete, status == ResourceStatus.Complete
                    ? "Utility file transfer completed." : "Utility file transfer failed or was rejected.",
                    Bytes: length, PhysicalBytes: segments.Values.Sum());
            }
            finally { link.Teardown(); }
        }
        finally { Directory.Delete(job); }
    }

    internal static void ValidateHash(byte[] destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Length != Destination.HashLength) throw new ArgumentException("Expected a 16-byte destination.", nameof(destination));
    }

    internal static string CreatePrivateWorkDirectory(string directory)
    {
        var path = Path.GetFullPath(Path.Combine(directory, "storage", "utility-work"));
        CreatePrivateDirectory(path);
        return path;
    }

    internal static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Utility work directories cannot be symbolic links.");
    }

    internal static FileStream CreatePrivateFile(string path, FileOptions options)
    {
        var settings = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite,
            Share = FileShare.Read, BufferSize = 64 * 1024, Options = options };
        if (!OperatingSystem.IsWindows()) settings.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new(path, settings);
    }

    public static string RenderManagement(object? value)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new() { Indented = true });
        var nodes = 0;
        Write(value, 0);
        writer.Flush();
        if (stream.Length > 4 * 1024 * 1024)
            throw new InvalidDataException("Utility management output exceeds native safety bounds.");
        return Encoding.UTF8.GetString(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));

        void Write(object? current, int depth)
        {
            if (++nodes > 10000 || depth > 16 || writer.BytesCommitted + writer.BytesPending > 4 * 1024 * 1024)
                throw new InvalidDataException("Utility management output exceeds native safety bounds.");
            switch (current)
            {
                case null: writer.WriteNullValue(); break;
                case string text when text.Length <= 8192: writer.WriteStringValue(text); break;
                case byte[] bytes when bytes.Length <= 8192: writer.WriteStringValue(Convert.ToHexStringLower(bytes)); break;
                case bool flag: writer.WriteBooleanValue(flag); break;
                case int number: writer.WriteNumberValue(number); break;
                case long number: writer.WriteNumberValue(number); break;
                case uint number: writer.WriteNumberValue(number); break;
                case ulong number: writer.WriteNumberValue(number); break;
                case double number when double.IsFinite(number): writer.WriteNumberValue(number); break;
                case float number when float.IsFinite(number): writer.WriteNumberValue(number); break;
                case IDictionary<object, object?> map:
                    writer.WriteStartObject();
                    foreach (var (key, entry) in map)
                    {
                        if (key is not string name || name.Length > 256)
                            throw new InvalidDataException("Unexpected utility management map key.");
                        writer.WritePropertyName(name);
                        Write(entry, depth + 1);
                    }
                    writer.WriteEndObject();
                    break;
                case IEnumerable<object?> entries:
                    writer.WriteStartArray();
                    foreach (var entry in entries) Write(entry, depth + 1);
                    writer.WriteEndArray();
                    break;
                default: throw new InvalidDataException("Unexpected utility management value.");
            }
        }
    }
}
