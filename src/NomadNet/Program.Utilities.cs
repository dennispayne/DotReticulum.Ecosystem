using System.Globalization;
using DotReticulum.Core;

namespace NomadNet;

static partial class Program
{
    private sealed partial class TerminalSession
    {
        private readonly NomadNetUtilities _utilities = new(node, identity, dataDirectory);
        private readonly NomadNetSpeedTest _speedTest = new(node, identity, dataDirectory);
        private Task _utilityWork = Task.CompletedTask;
        private CancellationTokenSource? _utilityStop;

        private async Task<bool> TryHandleUtilityAsync(string command, CancellationToken token)
        {
            if (!command.Equals("utility", StringComparison.OrdinalIgnoreCase) &&
                !command.StartsWith("utility ", StringComparison.OrdinalIgnoreCase)) return false;
            var text = command.Length <= 7 ? "" : command[8..].Trim();
            var parts = Split(text, 5);
            if (parts.Length == 0 || parts is ["help"])
                Console.WriteLine("""
                    utility paths|rates|interfaces      Inspect the current Reticulum owner
                    utility identity [hash]             Show local addresses or resolve a peer
                    utility path <hash> | drop <hash>    Resolve or expire a path
                    utility probe <name> <hash> [bytes] [count]
                    utility send <hash> <file>           Send to an authorized rncp receiver
                    utility speed address               Show this identity's speed-test address
                    utility speed listen <peer-hash>     Accept only the selected peer
                    utility speed send <peer-hash> [size-label]
                    utility speed status|off             Inspect result or stop accepting tests
                    utility status|cancel|wait           Own/cancel/join one background operation
                    Speed sizes: 2 KB, 8 KB, 32 KB, 128 KB, 512 KB, 1 MB, 4 MB,
                    16 MB, 64 MB, 256 MB, 1000 MB, 2000 MB. Default: 1 MB.
                    Utilities never enable a listener until explicitly requested.
                    """);
            else if (parts is ["status"])
                Console.WriteLine(_utilityWork.IsCompleted ? "Utility idle." : "Utility operation running.");
            else if (parts is ["cancel"])
            {
                if (_utilityStop is not null) await _utilityStop.CancelAsync().ConfigureAwait(false);
                await _utilityWork.ConfigureAwait(false);
                Console.WriteLine("Utility operation joined.");
            }
            else if (parts is ["wait"])
                await _utilityWork.WaitAsync(token).ConfigureAwait(false);
            else if (parts is ["paths"] or ["rates"] or ["interfaces"])
                Console.WriteLine(NomadNetUtilities.RenderManagement(
                    await _utilities.GetTableAsync(parts[0], token).ConfigureAwait(false)));
            else if (parts is ["identity"])
                Console.WriteLine($"Identity: {identity.HexHash}\nLXMF: {router.DeliveryHashHex}\n" +
                    $"Node: {host?.DestinationHash ?? "disabled"}\nSpeed test: {_speedTest.DestinationHash}");
            else if (parts is ["identity", var destination])
                BeginUtility(async stop => Console.WriteLine("Resolved identity: " +
                    await _utilities.ResolveIdentityAsync(Hash(destination), stop).ConfigureAwait(false)), token);
            else if (parts is ["path", var path])
                BeginUtility(async stop => Console.WriteLine(NomadNetUtilities.RenderManagement(
                    await _utilities.ResolvePathAsync(Hash(path), stop).ConfigureAwait(false))), token);
            else if (parts is ["drop", var expired])
                Console.WriteLine(await _utilities.DropPathAsync(Hash(expired), token).ConfigureAwait(false)
                    ? "Path expired." : "No path to expire.");
            else if (parts.Length is >= 3 and <= 5 && parts[0] == "probe")
            {
                var name = parts[1];
                var address = Hash(parts[2]);
                var (size, count) = NomadNetUtilities.PrepareProbe(parts.ElementAtOrDefault(3), parts.ElementAtOrDefault(4));
                BeginUtility(async stop =>
                {
                    var result = await _utilities.ProbeAsync(name, address, size, count, stop).ConfigureAwait(false);
                    foreach (var reply in result.Replies)
                        Console.WriteLine(reply.Delivered
                            ? $"Probe reply: rtt={reply.RoundTripSeconds?.ToString("F6", CultureInfo.InvariantCulture)}s hops={reply.Hops}" +
                                NomadNetUtilities.FormatReceptionStats(reply)
                            : "Probe timed out.");
                    Console.WriteLine($"Probe sent={result.Sent} received={result.Received} loss={result.LossPercent.ToString("F1", CultureInfo.InvariantCulture)}%");
                }, token);
            }
            else if (parts.Length >= 3 && parts[0] == "send")
            {
                var send = Split(text, 3);
                var address = Hash(send[1]);
                var file = send[2];
                BeginUtility(async stop =>
                {
                    var result = await _utilities.SendFileAsync(address, file, token: stop).ConfigureAwait(false);
                    if (!result.Success) throw new IOException(result.Message);
                    Console.WriteLine($"Utility file delivered: bytes={result.Bytes} physical={result.PhysicalBytes}");
                }, token);
            }
            else if (parts is ["speed", "address"])
                Console.WriteLine("Speed-test address: " + _speedTest.DestinationHash);
            else if (parts is ["speed", "listen", var peer])
            {
                _speedTest.Log = message =>
                {
                    Console.Error.WriteLine(message);
                    logging.Write(1, message);
                };
                await _speedTest.StartListeningAsync(peer, token).ConfigureAwait(false);
                Console.WriteLine($"Speed-test listener: {_speedTest.DestinationHash} peer={NomadNetAddress.NormalizeHash(peer)}");
            }
            else if (parts is ["speed", "off"])
            {
                await _speedTest.StopListeningAsync(token).ConfigureAwait(false);
                Console.WriteLine("Speed-test listener stopped.");
            }
            else if (parts is ["speed", "status"])
            {
                Console.WriteLine("Speed-test listening=" + _speedTest.Listening);
                if (_speedTest.LastReceived is { } result) PrintSpeed("Download", result);
            }
            else if (parts.Length >= 3 && parts[0] == "speed" && parts[1] == "send")
            {
                var send = Split(text, 4);
                var address = NomadNetAddress.NormalizeHash(send[2]);
                var size = send.ElementAtOrDefault(3) ?? "1 MB";
                if (!NomadNetSpeedTest.Sizes.ContainsKey(size)) throw new ArgumentException("Unknown speed-test size label.");
                BeginUtility(async stop => PrintSpeed("Upload",
                    await _speedTest.SendAsync(address, size, token: stop).ConfigureAwait(false)), token);
            }
            else throw new ArgumentException("Invalid utility command. Use utility help.");
            return true;

            static byte[] Hash(string value) => Convert.FromHexString(NomadNetAddress.NormalizeHash(value));
        }

        private void BeginUtility(Func<CancellationToken, Task> action, CancellationToken token)
        {
            if (!_utilityWork.IsCompleted) throw new InvalidOperationException("A utility operation is already running. Cancel or wait first.");
            _utilityStop?.Dispose();
            var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            _utilityStop = stop;
            stop.CancelAfter(NomadNetUtilities.TransferTimeout);
            _utilityWork = RunAsync();
            Console.WriteLine("Utility operation started.");
            async Task RunAsync()
            {
                try { await action(stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    Console.Error.WriteLine("Utility operation cancelled or timed out.");
                }
                catch (Exception exception) when (exception is IOException or ArgumentException or
                    InvalidOperationException or TimeoutException or UnauthorizedAccessException or FormatException or
                    System.Security.Cryptography.CryptographicException)
                {
                    Console.Error.WriteLine("Utility failed: " + exception.Message);
                    logging.Write(1, "Utility failed: " + exception.Message);
                }
            }
        }

        private async Task DisposeUtilitiesAsync()
        {
            if (_utilityStop is not null) await _utilityStop.CancelAsync().ConfigureAwait(false);
            await _utilityWork.ConfigureAwait(false);
            await _speedTest.DisposeAsync().ConfigureAwait(false);
            _utilityStop?.Dispose();
        }

        private static void PrintSpeed(string direction, NomadNetSpeedResult result) =>
            Console.WriteLine($"{direction}: bytes={result.Bytes} seconds={result.Seconds.ToString("F6", CultureInfo.InvariantCulture)} " +
                $"rate={NomadNetSpeedTest.FormatRate(result.BytesPerSecond)} sha256={result.Sha256} physical={result.PhysicalBytes}");
    }
}
