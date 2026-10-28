using System.Security.Cryptography;
using DotReticulum.Core;

namespace LXMF;

/// <summary>Upstream LXMF ticket lifetime rules and available_tickets persistence. The store owns its secret copies.</summary>
public sealed class LxmTicketStore : IDisposable
{
    public static readonly TimeSpan Expiry = TimeSpan.FromDays(21);
    public static readonly TimeSpan Grace = TimeSpan.FromDays(5);
    public static readonly TimeSpan Renew = TimeSpan.FromDays(14);
    public static readonly TimeSpan DeliveryInterval = TimeSpan.FromDays(1);
    private const int MaximumFileBytes = 4 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<string, List<LxmTicket>> _inbound = [];
    private readonly Dictionary<string, LxmTicket> _outbound = [];
    private readonly Dictionary<string, double> _lastDeliveries = [];
    private readonly TimeProvider _time;
    private readonly string? _path;
    private readonly int _capacity;
    private readonly FileStream? _lease;
    private bool _disposed;

    public LxmTicketStore(string? path = null, TimeProvider? timeProvider = null, int capacity = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _time = timeProvider ?? TimeProvider.System;
        _capacity = capacity;
        if (path is null) return;
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            LxmPersistence.RemoveInterruptedWrites(_path);
            if (File.Exists(_path)) Load(_path);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private double Now => _time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;

    /// <summary>Returns a reusable or newly generated inbound ticket, unless one was delivered less than a day ago.</summary>
    public LxmTicket? Generate(ReadOnlySpan<byte> destination, TimeSpan? expiry = null)
    {
        var key = Key(destination);
        lock (_gate)
        {
            ThrowIfDisposed();
            var now = Now;
            CleanCore(now);
            if (_lastDeliveries.TryGetValue(key, out var last) && now - last < DeliveryInterval.TotalSeconds) return null;
            if (_inbound.TryGetValue(key, out var tickets))
            {
                var existing = tickets.FirstOrDefault(ticket => ticket.Expires - now > Renew.TotalSeconds);
                if (existing is not null) return Copy(existing);
                if (tickets.Count >= 16) throw new InvalidOperationException("Inbound ticket limit reached for destination.");
            }
            else
            {
                EnsureCapacity(_inbound.Count);
                tickets = [];
                _inbound.Add(key, tickets);
            }

            var lifetime = (expiry ?? Expiry).TotalSeconds;
            if (!double.IsFinite(lifetime) || lifetime <= 0) throw new ArgumentOutOfRangeException(nameof(expiry));
            var created = new LxmTicket(now + lifetime, RandomNumberGenerator.GetBytes(LxMessage.TicketLength));
            tickets.Add(created);
            SaveCore();
            return Copy(created);
        }
    }

    public void Remember(ReadOnlySpan<byte> destination, LxmTicket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var key = Key(destination);
        if (ticket.Token.Length != LxMessage.TicketLength || !double.IsFinite(ticket.Expires))
            throw new ArgumentException("Invalid ticket.", nameof(ticket));
        lock (_gate)
        {
            ThrowIfDisposed();
            if (ticket.Expires <= Now) return;
            if (!_outbound.ContainsKey(key)) EnsureCapacity(_outbound.Count);
            if (_outbound.Remove(key, out var previous)) CryptographicOperations.ZeroMemory(previous.Token);
            _outbound.Add(key, Copy(ticket));
            SaveCore();
        }
    }

    public LxmTicket? GetOutbound(ReadOnlySpan<byte> destination)
    {
        var key = Key(destination);
        lock (_gate)
        {
            ThrowIfDisposed();
            return _outbound.TryGetValue(key, out var ticket) && ticket.Expires > Now ? Copy(ticket) : null;
        }
    }

    /// <summary>Caller owns returned secret copies and should zero them after validating.</summary>
    public byte[][] GetInbound(ReadOnlySpan<byte> destination)
    {
        var key = Key(destination);
        lock (_gate)
        {
            ThrowIfDisposed();
            return _inbound.TryGetValue(key, out var tickets)
                ? tickets.Where(ticket => Now < ticket.Expires).Select(ticket => ticket.Token.ToArray()).ToArray()
                : [];
        }
    }

    public void MarkDelivered(ReadOnlySpan<byte> destination)
    {
        var key = Key(destination);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_lastDeliveries.ContainsKey(key)) EnsureCapacity(_lastDeliveries.Count);
            _lastDeliveries[key] = Now;
            SaveCore();
        }
    }

    public void Clean()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            CleanCore(Now);
            SaveCore();
        }
    }

    public void Save()
    {
        lock (_gate) { ThrowIfDisposed(); SaveCore(); }
    }

    public bool Reload()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_path is null || !File.Exists(_path)) return false;
            using var loaded = new LxmTicketStore(timeProvider: _time, capacity: _capacity);
            loaded.Load(_path);
            ClearSecrets();
            foreach (var (key, tickets) in loaded._inbound) _inbound.Add(key, tickets);
            foreach (var (key, ticket) in loaded._outbound) _outbound.Add(key, ticket);
            foreach (var (key, timestamp) in loaded._lastDeliveries) _lastDeliveries.Add(key, timestamp);
            loaded._inbound.Clear();
            loaded._outbound.Clear();
            loaded._lastDeliveries.Clear();
            return true;
        }
    }

    private void CleanCore(double now)
    {
        foreach (var key in _outbound.Where(entry => entry.Value.Expires < now).Select(entry => entry.Key).ToArray())
        {
            CryptographicOperations.ZeroMemory(_outbound[key].Token);
            _outbound.Remove(key);
        }

        foreach (var (key, tickets) in _inbound.ToArray())
        {
            foreach (var ticket in tickets.Where(ticket => ticket.Expires + Grace.TotalSeconds < now).ToArray())
            {
                tickets.Remove(ticket);
                CryptographicOperations.ZeroMemory(ticket.Token);
            }

            if (tickets.Count == 0) _inbound.Remove(key);
        }

        foreach (var key in _lastDeliveries.Where(entry => now - entry.Value >= DeliveryInterval.TotalSeconds).Select(entry => entry.Key).ToArray())
            _lastDeliveries.Remove(key);
    }

    private void SaveCore()
    {
        if (_path is null) return;
        var root = new Dictionary<object, object?>
        {
            ["outbound"] = _outbound.ToDictionary(entry => (object)Convert.FromHexString(entry.Key),
                entry => (object?)new object?[] { entry.Value.Expires, entry.Value.Token }),
            ["inbound"] = _inbound.ToDictionary(entry => (object)Convert.FromHexString(entry.Key),
                entry => (object?)entry.Value.ToDictionary(ticket => (object)ticket.Token, ticket => (object?)new object?[] { ticket.Expires })),
            ["last_deliveries"] = _lastDeliveries.ToDictionary(entry => (object)Convert.FromHexString(entry.Key), entry => (object?)entry.Value),
        };
        var bytes = MsgPack.Pack(root);
        var output = _path + "." + Guid.NewGuid().ToString("N") + ".out";
        try
        {
            if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("Ticket store exceeds maximum size.");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(output, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(output, _path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (File.Exists(output)) File.Delete(output);
        }
    }

    private void Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > MaximumFileBytes) throw new InvalidDataException("Ticket store exceeds maximum size.");
        var bytes = new byte[checked((int)stream.Length)];
        object? decoded = null;
        try
        {
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new InvalidDataException("Ticket store grew while reading.");
            decoded = MsgPack.UnpackApplicationValue(bytes);
            if (decoded is not Dictionary<object, object?> root) throw new InvalidDataException("Invalid ticket store.");
            foreach (var (destination, value) in Map(root, "outbound"))
            {
                var key = Key(destination as byte[] ?? throw new InvalidDataException("Invalid ticket destination."));
                if (value is not object?[] { Length: >= 2 } entry || entry[1] is not byte[] { Length: LxMessage.TicketLength } token)
                    throw new InvalidDataException("Invalid outbound ticket.");
                EnsureCapacity(_outbound.Count);
                _outbound.Add(key, new LxmTicket(Number(entry[0]), token.ToArray()));
            }

            foreach (var (destination, value) in Map(root, "inbound"))
            {
                var key = Key(destination as byte[] ?? throw new InvalidDataException("Invalid ticket destination."));
                if (value is not Dictionary<object, object?> map || map.Count > 16) throw new InvalidDataException("Invalid inbound tickets.");
                EnsureCapacity(_inbound.Count);
                var tickets = new List<LxmTicket>();
                _inbound.Add(key, tickets);
                foreach (var (token, expiry) in map)
                {
                    if (token is not byte[] { Length: LxMessage.TicketLength } secret || expiry is not object?[] { Length: > 0 } entry)
                        throw new InvalidDataException("Invalid inbound ticket.");
                    tickets.Add(new LxmTicket(Number(entry[0]), secret.ToArray()));
                }
            }

            foreach (var (destination, value) in Map(root, "last_deliveries"))
            {
                EnsureCapacity(_lastDeliveries.Count);
                _lastDeliveries.Add(Key(destination as byte[] ?? throw new InvalidDataException("Invalid ticket destination.")), Number(value));
            }

            CleanCore(Now);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            ZeroDecoded(decoded);
        }
    }

    private static Dictionary<object, object?> Map(Dictionary<object, object?> root, string key) =>
        root.TryGetValue(key, out var value) && value is Dictionary<object, object?> map
            ? map : throw new InvalidDataException("Missing ticket store map: " + key);

    private static double Number(object? value)
    {
        if (value is not (double or float or long or ulong)) throw new InvalidDataException("Invalid ticket time.");
        var number = MsgPack.ToDouble(value);
        return double.IsFinite(number) ? number : throw new InvalidDataException("Invalid ticket time.");
    }

    private static void ZeroDecoded(object? value)
    {
        if (value is byte[] bytes) CryptographicOperations.ZeroMemory(bytes);
        else if (value is object?[] list) foreach (var item in list) ZeroDecoded(item);
        else if (value is Dictionary<object, object?> map)
            foreach (var (key, item) in map) { ZeroDecoded(key); ZeroDecoded(item); }
    }

    private void EnsureCapacity(int count)
    {
        if (count >= _capacity) throw new InvalidOperationException("Ticket store destination capacity reached.");
    }

    private static string Key(ReadOnlySpan<byte> destination) => destination.Length == LxMessage.DestinationLength
        ? Convert.ToHexStringLower(destination) : throw new ArgumentException("Destination must be 16 bytes.", nameof(destination));

    private static LxmTicket Copy(LxmTicket ticket) => new(ticket.Expires, ticket.Token.ToArray());
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void ClearSecrets()
    {
        foreach (var ticket in _outbound.Values.Concat(_inbound.Values.SelectMany(tickets => tickets)))
            CryptographicOperations.ZeroMemory(ticket.Token);
        _outbound.Clear();
        _inbound.Clear();
        _lastDeliveries.Clear();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ClearSecrets();
            _lease?.Dispose();
        }
    }
}

/// <summary>Caller-owned ticket value; clear Token after use.</summary>
public sealed record LxmTicket(double Expires, byte[] Token)
{
    public object?[] ToField() => [Expires, Token.ToArray()];
}
