using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NomadNetAcceptance;

// Test-only HDLC observer: Packet.cs documents context at 18/34 and LinkTypes.cs defines Resource=1.
public sealed class TcpImpairmentProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly int _targetPort;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _connections = new();
    private readonly Task _accept;
    private int _armed;
    private long _dropped;
    private long _forwarded;
    private long _forwardedBytes;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public long Dropped => Interlocked.Read(ref _dropped);
    public long Forwarded => Interlocked.Read(ref _forwarded);
    public long ForwardedBytes => Interlocked.Read(ref _forwardedBytes);

    public TcpImpairmentProxy(int targetPort)
    {
        _targetPort = targetPort;
        _listener.Start();
        _accept = AcceptAsync();
    }

    public void ArmResourceDrop() => Interlocked.Exchange(ref _armed, 1);

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                lock (_connections)
                {
                    _connections.RemoveAll(task => task.IsCompleted);
                    if (_connections.Count >= 8) { client.Dispose(); continue; }
                    _connections.Add(BridgeAsync(client));
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task BridgeAsync(TcpClient client)
    {
        using (client)
        using (var upstream = new TcpClient())
        using (var connection = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            try
            {
                await upstream.ConnectAsync(IPAddress.Loopback, _targetPort, connection.Token);
                var outgoing = RelayAsync(client.GetStream(), upstream.GetStream(), connection.Token);
                var incoming = RelayAsync(upstream.GetStream(), client.GetStream(), connection.Token);
                await Task.WhenAny(outgoing, incoming);
                await connection.CancelAsync();
                await Task.WhenAll(outgoing, incoming);
            }
            catch (OperationCanceledException) when (connection.IsCancellationRequested) { }
            catch (IOException) { }
            catch (SocketException) { }
        }
    }

    private async Task RelayAsync(Stream input, Stream output, CancellationToken token)
    {
        var buffer = new byte[16384];
        using var frame = new MemoryStream();
        while (true)
        {
            var count = await input.ReadAsync(buffer, token);
            if (count == 0) return;
            for (var index = 0; index < count; index++)
            {
                var value = buffer[index];
                frame.WriteByte(value);
                if (frame.Length > 65536) throw new IOException("HDLC test frame exceeded bound.");
                if (value != 0x7e) continue;
                var wire = frame.ToArray();
                frame.SetLength(0);
                frame.WriteByte(0x7e);
                if (wire.Length <= 2) continue;
                if (IsResource(wire) && Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
                {
                    Interlocked.Increment(ref _dropped);
                    continue;
                }
                await Task.Delay(5, token);
                await output.WriteAsync(wire, token);
                Interlocked.Increment(ref _forwarded);
                Interlocked.Add(ref _forwardedBytes, wire.Length);
            }
        }
    }

    private static bool IsResource(byte[] wire)
    {
        var decoded = new List<byte>(wire.Length);
        var escaped = false;
        foreach (var value in wire)
        {
            if (value == 0x7e) continue;
            if (!escaped && value == 0x7d) { escaped = true; continue; }
            decoded.Add(escaped ? (byte)(value ^ 0x20) : value);
            escaped = false;
        }
        if (decoded.Count < 20 || (decoded[0] & 15) != 12) return false;
        var context = (decoded[0] & 0x40) == 0 ? 18 : 34;
        return decoded.Count > context && decoded[context] == 1;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _accept;
        _listener.Stop();
        Task[] connections;
        lock (_connections) connections = _connections.ToArray();
        await Task.WhenAll(connections);
        _stop.Dispose();
    }
}
