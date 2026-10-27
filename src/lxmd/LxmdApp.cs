using System.Globalization;
using DotLXMF;
using DotReticulum.Applications;
using DotReticulum.Core;

namespace Lxmd;

internal static class LxmdApp
{
    private const string Version = "1.2.1";

    public const string Usage = """
        lxmd - Lightweight Extensible Messaging Daemon

        Usage: lxmd [options]

        Options:
          --config <path>       Alternative lxmd configuration directory
          --rnsconfig <path>    Alternative Reticulum configuration directory
          -p, --propagation-node  Run an LXMF propagation node
          -i, --on-inbound <PATH> Executable to run when a message is received
          -v, --verbose         Increase logging verbosity (repeatable)
          -q, --quiet           Decrease logging verbosity (repeatable)
          -s, --service         Log to <config>/logfile
          --status              Display propagation node status
          --peers               Display peered nodes
          --sync <hash>         Request synchronization with a peer
          -b, --break <hash>    Break peering with a peer
          --timeout <seconds>   Timeout for query operations
          -r, --remote <hash>   Remote propagation-node destination hash
          --identity <path>     Identity used for remote requests
          --exampleconfig       Print an example configuration and exit
          --version             Print version and exit
          -h, --help            Show this help

        This experimental .NET daemon supports delivery destinations, propagation storage, and inbox persistence.
        Automatic peering and remote management operations fail explicitly until matching router APIs are available.
        """;

    private sealed record Arguments(
        string? ConfigDirectory,
        string? ReticulumDirectory,
        string? OnInbound,
        string? IdentityPath,
        string? Remote,
        string? Sync,
        string? Unpeer,
        double? Timeout,
        int Verbosity,
        bool PropagationNode,
        bool Service,
        bool Status,
        bool Peers,
        bool ExampleConfig,
        bool Help,
        bool Version);

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken stop)
    {
        Arguments options;
        try { options = Parse(args); }
        catch (ArgumentException ex)
        {
            await error.WriteLineAsync($"lxmd: {ex.Message}").ConfigureAwait(false);
            return 2;
        }

        if (options.Help) { await output.WriteLineAsync(Usage).ConfigureAwait(false); return 0; }
        if (options.Version) { await output.WriteLineAsync($"lxmd {Version}").ConfigureAwait(false); return 0; }
        if (options.ExampleConfig) { await output.WriteLineAsync(LxmdConfig.Example).ConfigureAwait(false); return 0; }

        if (options.Status || options.Peers || options.Sync is not null || options.Unpeer is not null || options.Remote is not null)
        {
            await error.WriteLineAsync("lxmd: remote propagation management operations are not implemented by this build; no network request was made.").ConfigureAwait(false);
            return 1;
        }

        if (options.IdentityPath is not null)
        {
            await error.WriteLineAsync("lxmd: --identity is only applicable to remote management operations, which are not implemented.").ConfigureAwait(false);
            return 1;
        }

        string configDirectory;
        try
        {
            configDirectory = GetConfigDirectory(options.ConfigDirectory);
            Directory.CreateDirectory(configDirectory);
            var configPath = Path.Combine(configDirectory, "config");
            if (!File.Exists(configPath))
            {
                await File.WriteAllTextAsync(configPath, LxmdConfig.Example + Environment.NewLine, stop).ConfigureAwait(false);
                await output.WriteLineAsync($"lxmd: created default configuration at {configPath}").ConfigureAwait(false);
            }

            var config = LxmdConfig.Load(configPath);
            var propagationEnabled = options.PropagationNode || config.GetBoolean("propagation", "enable_node", false);
            var displayName = config.Get("lxmf", "display_name", "Anonymous Peer");
            var stampCost = Math.Max(1, config.GetInteger("lxmf", "stamp_cost", 12)!.Value);
            var acceptedKilobytes = Math.Max(0.38, config.GetNumber("lxmf", "delivery_transfer_max_accepted_size", 1));
            if (!double.IsFinite(acceptedKilobytes)) throw new FormatException("delivery_transfer_max_accepted_size must be finite.");
            var announceInterval = config.GetInteger("lxmf", "announce_interval", null);
            if (announceInterval is < 0) throw new FormatException("announce_interval cannot be negative.");
            var announceAtStart = config.GetBoolean("lxmf", "announce_at_start", false);
            var propagationAnnounceInterval = config.GetInteger("propagation", "announce_interval", null);
            if (propagationAnnounceInterval is < 0) throw new FormatException("propagation announce_interval cannot be negative.");
            var propagationAnnounceAtStart = config.GetBoolean("propagation", "announce_at_start", false);
            var logLevel = Math.Clamp(config.GetInteger("logging", "loglevel", 4)!.Value + options.Verbosity, 0, 7);
            var onInbound = options.OnInbound ?? config.Get("lxmf", "on_inbound");
            var ignored = LoadHashes(Path.Combine(configDirectory, "ignored"));
            var storageDirectory = Path.Combine(configDirectory, "storage");
            var messagesDirectory = Path.Combine(storageDirectory, "messages");
            Directory.CreateDirectory(messagesDirectory);

            StreamWriter? serviceLog = null;
            TextWriter logOutput = output;
            if (options.Service)
            {
                serviceLog = new StreamWriter(new FileStream(Path.Combine(configDirectory, "logfile"), FileMode.Append, FileAccess.Write, FileShare.Read));
                logOutput = serviceLog;
            }

            try
            {
                var logger = CreateLogger(logOutput, logLevel);
                logger(3, $"Configuration loaded from {configPath}");
                await using var node = ReticulumNode.Open(options.ReticulumDirectory, logger);
                using var identity = LoadOrCreateIdentity(Path.Combine(configDirectory, "identity"), logger);
                var routerOptions = new LxmRouterOptions
                {
                    DeliveryPerTransferLimit = checked((long)(acceptedKilobytes * 1000)),
                    InboundStampCost = stampCost,
                    StoragePath = storageDirectory,
                };
                var propagationOptions = propagationEnabled ? CreatePropagationOptions(config, configDirectory) : null;
                await using var router = new LxmRouter(node.Router, identity, displayName, routerOptions)
                {
                    Log = message => logger(5, message),
                };
                var propagationNode = propagationOptions is null ? null : router.EnablePropagationNode(propagationOptions);
                if (propagationNode is not null) propagationNode.Log = message => logger(5, message);
                var store = new MessageStore(messagesDirectory);
                router.MessageReceived = received =>
                {
                    try
                    {
                        if (ignored.Contains(Convert.ToHexStringLower(received.Message.SourceHash))) return;
                        var path = store.Save(received.Message);
                        logger(6, $"Received {received.Message} written to {path}");
                        if (!string.IsNullOrWhiteSpace(onInbound)) RunInbound(onInbound, path, logger);
                    }
                    catch (Exception ex) { logger(1, $"Error processing received {received.Message}: {ex}"); }
                };

                await node.StartAsync(stop).ConfigureAwait(false);
                router.Start();
                await output.WriteLineAsync($"lxmd: delivery destination {router.DeliveryHashHex}").ConfigureAwait(false);
                logger(3, $"LXMF delivery destination ready <{router.DeliveryHashHex}>");

                using var jobs = CancellationTokenSource.CreateLinkedTokenSource(stop);
                var announceJob = RunAnnounceJobsAsync(router, propagationNode, announceAtStart, announceInterval,
                    propagationAnnounceAtStart, propagationAnnounceInterval, logger, jobs.Token);
                try { await node.Completion.ConfigureAwait(false); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                finally
                {
                    await jobs.CancelAsync().ConfigureAwait(false);
                    try { await announceJob.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (jobs.IsCancellationRequested) { }
                }

                return 0;
            }
            finally
            {
                if (serviceLog is not null) await serviceLog.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidDataException or NotSupportedException or ArgumentException or OverflowException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            await error.WriteLineAsync($"lxmd: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    private static Arguments Parse(string[] args)
    {
        string? config = null, rnsConfig = null, onInbound = null, identity = null, remote = null, sync = null, unpeer = null;
        double? timeout = null;
        var verbosity = 0;
        var propagation = false;
        var service = false;
        var status = false;
        var peers = false;
        var example = false;
        var help = false;
        var version = false;

        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            string Value()
            {
                if (++index >= args.Length) throw new ArgumentException($"{option} requires a value.");
                return args[index];
            }

            switch (option)
            {
                case "-h" or "--help": help = true; break;
                case "--version": version = true; break;
                case "--config": config = Value(); break;
                case "--rnsconfig": rnsConfig = Value(); break;
                case "-p" or "--propagation-node": propagation = true; break;
                case "-i" or "--on-inbound": onInbound = Value(); break;
                case "-v" or "--verbose": verbosity++; break;
                case "-q" or "--quiet": verbosity--; break;
                case "-s" or "--service": service = true; break;
                case "--status": status = true; break;
                case "--peers": peers = true; break;
                case "--sync": sync = Value(); break;
                case "-b" or "--break": unpeer = Value(); break;
                case "--timeout":
                    if (!double.TryParse(Value(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0 || !double.IsFinite(seconds))
                        throw new ArgumentException("--timeout must be a positive finite number.");
                    timeout = seconds;
                    break;
                case "-r" or "--remote": remote = Value(); break;
                case "--identity": identity = Value(); break;
                case "--exampleconfig": example = true; break;
                default:
                    if (option.Length > 2 && option[0] == '-' && option[1] != '-' &&
                        option[1..].All(character => character is 'v' or 'q'))
                    {
                        verbosity += option.Count(character => character == 'v') - option.Count(character => character == 'q');
                        break;
                    }

                    throw new ArgumentException($"unrecognized argument '{option}'.");
            }
        }

        return new(config, rnsConfig, onInbound, identity, remote, sync, unpeer, timeout, verbosity, propagation, service, status, peers, example, help, version);
    }

    private static string GetConfigDirectory(string? config)
    {
        if (!string.IsNullOrWhiteSpace(config)) return Path.GetFullPath(ExpandHome(config));
        if (Directory.Exists("/etc/lxmd") && File.Exists("/etc/lxmd/config")) return "/etc/lxmd";
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdg = Path.Combine(user, ".config", "lxmd");
        return File.Exists(Path.Combine(xdg, "config")) ? xdg : Path.Combine(user, ".lxmd");
    }

    private static string ExpandHome(string path)
    {
        if (path == "~") return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        return path;
    }

    private static Identity LoadOrCreateIdentity(string path, Action<int, string> log)
    {
        if (File.Exists(path))
        {
            log(4, "Loaded primary identity.");
            return IdentityFile.Load(path);
        }

        using var identity = Identity.Generate();
        IdentityFile.SaveNew(identity, path);
        log(4, "Created primary identity.");
        return IdentityFile.Load(path);
    }

    private static HashSet<string> LoadHashes(string path)
    {
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return hashes;
        foreach (var line in File.ReadLines(path))
        {
            var value = line.Trim();
            if (value.Length != Identity.HashSize * 2) continue;
            try { hashes.Add(Convert.ToHexString(Convert.FromHexString(value))); }
            catch (FormatException) { }
        }

        return hashes;
    }

    private static LxmPropagationOptions CreatePropagationOptions(LxmdConfig config, string configDirectory)
    {
        if (config.GetBoolean("propagation", "autopeer", true))
            throw new NotSupportedException("Propagation autopeering is not implemented; set [propagation] autopeer = no.");
        if (config.GetInteger("propagation", "max_peers", null) is not null)
            throw new NotSupportedException("Propagation max_peers is not implemented; remove the max_peers option.");
        if (config.GetList("propagation", "prioritise_destinations").Count > 0)
            throw new NotSupportedException("Propagation prioritise_destinations is not implemented; remove the option.");
        if (config.GetList("propagation", "control_allowed").Count > 0)
            throw new NotSupportedException("Propagation control_allowed is not implemented; remove the option.");
        foreach (var unsupported in new[] { "sequential_pn_stamp_validation", "static_peers_bypass_sequential", "max_inbound_syncs" })
            if (config.Get("propagation", unsupported) is not null)
                throw new NotSupportedException($"Propagation {unsupported} is not implemented; remove the option.");

        var transferKilobytes = config.GetNumber("propagation", "propagation_transfer_max_accepted_size", 256);
        if (config.Get("propagation", "propagation_message_max_accepted_size") is not null)
            transferKilobytes = config.GetNumber("propagation", "propagation_message_max_accepted_size", transferKilobytes);
        var syncKilobytes = config.GetNumber("propagation", "propagation_sync_max_accepted_size", 256 * 40);
        var storageMegabytes = config.GetNumber("propagation", "message_storage_limit", 500);
        if (!double.IsFinite(transferKilobytes) || !double.IsFinite(syncKilobytes) || !double.IsFinite(storageMegabytes))
            throw new FormatException("Propagation storage and transfer limits must be finite.");

        var staticPeers = config.GetList("propagation", "static_peers").Select(ParseHash).ToArray();
        return new LxmPropagationOptions
        {
            Name = config.Get("propagation", "node_name"),
            TransferLimit = checked((long)(Math.Max(0.38, transferKilobytes) * 1000)),
            SyncLimit = checked((long)(Math.Max(0.38, syncKilobytes) * 1000)),
            StorageLimit = checked((long)(Math.Max(0.005, storageMegabytes) * 1_000_000)),
            StampCost = Math.Max(13, config.GetInteger("propagation", "propagation_stamp_cost_target", 16)!.Value),
            StampFlexibility = Math.Max(0, config.GetInteger("propagation", "propagation_stamp_cost_flexibility", 3)!.Value),
            PeeringCost = Math.Max(0, config.GetInteger("propagation", "peering_cost", 18)!.Value),
            RequireAuthentication = config.GetBoolean("propagation", "auth_required", false),
            AllowedIdentities = LoadHashes(Path.Combine(configDirectory, "allowed")).Select(hash => Convert.FromHexString(hash)).ToArray(),
            FromStaticOnly = config.GetBoolean("propagation", "from_static_only", false),
            StaticPeers = staticPeers,
        };
    }

    private static byte[] ParseHash(string value)
    {
        try
        {
            var hash = Convert.FromHexString(value);
            if (hash.Length != Identity.HashSize) throw new FormatException();
            return hash;
        }
        catch (FormatException) { throw new FormatException($"Invalid 16-byte hash in propagation configuration: {value}"); }
    }

    private static Action<int, string> CreateLogger(TextWriter writer, int level)
    {
        var gate = new object();
        return (severity, message) =>
        {
            if (severity > level) return;
            lock (gate) writer.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}");
        };
    }

    private static async Task RunAnnounceJobsAsync(LxmRouter router, LxmPropagationNode? propagationNode,
        bool announceAtStart, int? intervalMinutes, bool propagationAnnounceAtStart, int? propagationIntervalMinutes,
        Action<int, string> log, CancellationToken stop)
    {
        var deferredStartJobs = true;
        var lastAnnounce = DateTimeOffset.UtcNow;
        var lastPropagationAnnounce = DateTimeOffset.UtcNow;
        while (true)
        {
            try
            {
                await Task.Delay(deferredStartJobs ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(5), stop).ConfigureAwait(false);
                if (deferredStartJobs)
                {
                    if (announceAtStart)
                    {
                        log(5, "Sending announce for LXMF delivery destination.");
                        await router.AnnounceAsync(stop).ConfigureAwait(false);
                    }

                    if (propagationNode is not null && propagationAnnounceAtStart)
                    {
                        log(5, "Sending announce for LXMF propagation node.");
                        await propagationNode.AnnounceAsync(stop).ConfigureAwait(false);
                    }

                    lastAnnounce = DateTimeOffset.UtcNow;
                    lastPropagationAnnounce = lastAnnounce;
                    deferredStartJobs = false;
                }

                if (intervalMinutes is { } peerInterval && DateTimeOffset.UtcNow - lastAnnounce >= TimeSpan.FromMinutes(peerInterval))
                {
                    log(5, "Sending announce for LXMF delivery destination.");
                    await router.AnnounceAsync(stop).ConfigureAwait(false);
                    lastAnnounce = DateTimeOffset.UtcNow;
                }

                if (propagationNode is not null && propagationIntervalMinutes is { } nodeInterval &&
                    DateTimeOffset.UtcNow - lastPropagationAnnounce >= TimeSpan.FromMinutes(nodeInterval))
                {
                    log(5, "Sending announce for LXMF propagation node.");
                    await propagationNode.AnnounceAsync(stop).ConfigureAwait(false);
                    lastPropagationAnnounce = DateTimeOffset.UtcNow;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                log(1, $"An error occurred while running announce jobs: {ex.Message}");
            }
        }
    }

    private static void RunInbound(string command, string messagePath, Action<int, string> log)
    {
        try
        {
            var parts = SplitCommand(command);
            if (parts.Count == 0) return;
            var start = new System.Diagnostics.ProcessStartInfo(parts[0]) { UseShellExecute = false };
            foreach (var argument in parts.Skip(1)) start.ArgumentList.Add(argument);
            start.ArgumentList.Add(messagePath);
            using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("Inbound handler did not start.");
            if (!process.WaitForExit(60_000))
            {
                process.Kill(entireProcessTree: true);
                log(1, $"Inbound handler timed out for {messagePath}.");
            }
        }
        catch (Exception ex) { log(1, $"Inbound handler failed for {messagePath}: {ex.Message}"); }
    }

    private static List<string> SplitCommand(string value)
    {
        var result = new List<string>();
        var part = new System.Text.StringBuilder();
        char quote = '\0';
        var escaped = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (escaped) { part.Append(character); escaped = false; continue; }
            if (character == '\\' && quote != '\'' && index + 1 < value.Length && (value[index + 1] is '\'' or '"' or '\\' || char.IsWhiteSpace(value[index + 1])))
            {
                escaped = true;
                continue;
            }
            if (quote != '\0')
            {
                if (character == quote) quote = '\0';
                else part.Append(character);
                continue;
            }

            if (character is '\'' or '"') { quote = character; continue; }
            if (char.IsWhiteSpace(character))
            {
                if (part.Length > 0) { result.Add(part.ToString()); part.Clear(); }
                continue;
            }

            part.Append(character);
        }

        if (escaped) part.Append('\\');
        if (quote != '\0') throw new FormatException("on_inbound has an unterminated quoted argument.");
        if (part.Length > 0) result.Add(part.ToString());
        return result;
    }
}
