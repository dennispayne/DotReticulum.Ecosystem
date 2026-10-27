using System.Globalization;
using DotReticulum.Applications;
using DotReticulum.Core;

namespace DotTelemetry;

/// <summary>Command line front end of the telemetry daemon and viewer (see docs/telemetry.md).</summary>
public static class TelemetryApp
{
    public const string Usage = """
        telemetry - publish and view node telemetry over Reticulum (experimental, DotReticulum-specific format)

        Usage:
          telemetry daemon [-i seconds] [-b seconds]   Publish periodic system/node metrics
          telemetry watch <destination> [-n count]     Subscribe to a daemon and print its reports
          telemetry get <destination>                  Print one report and exit
          telemetry -p                                 Print this node's telemetry destination and exit

        Options:
          --config <path>      Reticulum config directory or file (default ~/.dotreticulum)
          -i <seconds>         Publish interval (default 10)
          -b <seconds>         Announce interval, 0 = only at startup (default 0)
          --identity <path>    Identity file to use (created when missing)
          -n <count>           Stop after this many reports (watch)
          -w <seconds>         Connect timeout (default 30)
          -v / -q              Increase / decrease logging verbosity
          -h, --help           Show this help
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken stop)
    {
        string? config = null, identityPath = null, mode = null, destination = null;
        var verbosity = 0;
        double interval = 10, announce = 0, wait = 30;
        var count = 0;
        var print = false;
        for (var i = 0; i < args.Length; i++)
        {
            string Next()
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"{args[i]} requires a value.");
                return args[++i];
            }

            try
            {
                switch (args[i])
                {
                    case "-h" or "--help" or "-?": output.WriteLine(Usage); return 0;
                    case "--config" or "-c": config = Next(); break;
                    case "--identity": identityPath = Next(); break;
                    case "-i": interval = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "-b": announce = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "-w": wait = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "-n": count = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "-p": print = true; break;
                    case "-v": verbosity++; break;
                    case "-q": verbosity--; break;
                    default:
                        if (args[i].StartsWith('-') && args[i].Length > 1) throw new ArgumentException($"unknown argument '{args[i]}'.");
                        if (mode is null) mode = args[i];
                        else destination ??= args[i];
                        break;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
            {
                error.WriteLine($"telemetry: {ex.Message}");
                return 2;
            }
        }

        if (interval <= 0) { error.WriteLine("telemetry: the interval must be positive."); return 2; }
        if (!print && mode is not ("daemon" or "watch" or "get")) { output.WriteLine(Usage); return 2; }
        byte[] hash = [];
        if (mode is "watch" or "get" && !CliSupport.TryParseHash(destination ?? "", out hash))
        {
            error.WriteLine($"telemetry: the destination must be {CliSupport.HashHexLength} hexadecimal characters ({Identity.HashSize} bytes).");
            return 2;
        }

        try
        {
            var log = CliSupport.Logger(output, 3 + verbosity);
            await using var node = ReticulumNode.Open(config, log);
            if (print || mode == "daemon")
            {
                var identity = CliSupport.LoadOrCreateIdentity(node, identityPath, "telemetry");
                var daemon = new TelemetryDaemon(node, identity) { Log = m => log(5, m) };
                if (print)
                {
                    output.WriteLine("Identity     : <" + identity.HexHash + ">");
                    output.WriteLine("Listening on : <" + daemon.DestinationHashHex + ">");
                    return 0;
                }

                await node.StartAsync(stop).ConfigureAwait(false);
                log(4, "telemetry publishing on <" + daemon.DestinationHashHex + "> every " + interval.ToString(CultureInfo.InvariantCulture) + "s");
                output.Flush();
                await daemon.AnnounceAsync(stop).ConfigureAwait(false);
                var run = daemon.RunAsync(TimeSpan.FromSeconds(interval), stop);
                while (announce > 0 && !stop.IsCancellationRequested)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(announce), stop).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    await daemon.AnnounceAsync(stop).ConfigureAwait(false);
                }

                await run.ConfigureAwait(false);
                return 0;
            }

            await node.StartAsync(stop).ConfigureAwait(false);
            using var done = CancellationTokenSource.CreateLinkedTokenSource(stop);
            var received = 0;
            var limit = mode == "get" ? 1 : count;
            var failure = await TelemetryReceiver.WatchAsync(node, hash, TimeSpan.FromSeconds(wait), report =>
            {
                output.WriteLine(report.Format());
                output.Flush();
                if (limit > 0 && Interlocked.Increment(ref received) >= limit) done.Cancel();
            }, done.Token).ConfigureAwait(false);
            if (failure is not null)
            {
                error.WriteLine("telemetry: " + failure);
                return 1;
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidDataException)
        {
            error.WriteLine($"telemetry: {ex.Message}");
            return 1;
        }
    }
}
