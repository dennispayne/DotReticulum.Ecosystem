using System.Globalization;
using LXMF;
using DotReticulum.Applications;
using DotReticulum.Applications.Rnsd;
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
        ConfigObj-style quotes, lists, sections, multiline values and interpolation are supported.
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

        if (options.Status || options.Peers || options.Sync is not null || options.Unpeer is not null)
            return await RemoteControl.RunAsync(GetConfigDirectory(options.ConfigDirectory), options.ReticulumDirectory,
                options.IdentityPath is null ? null : ExpandHome(options.IdentityPath), options.Remote, options.Sync, options.Unpeer,
                options.Status, options.Peers, options.Timeout, output, error, stop).ConfigureAwait(false);

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
            if (stampCost >= 255) throw new FormatException("stamp_cost must be below 255.");
            var acceptedKilobytes = Math.Max(0.38, config.GetNumber("lxmf", "delivery_transfer_max_accepted_size", 1));
            if (!double.IsFinite(acceptedKilobytes)) throw new FormatException("delivery_transfer_max_accepted_size must be finite.");
            var announceInterval = config.GetInteger("lxmf", "announce_interval", null);
            var announceAtStart = config.GetBoolean("lxmf", "announce_at_start", false);
            var propagationAnnounceInterval = config.GetInteger("propagation", "announce_interval", null);
            var propagationAnnounceAtStart = config.GetBoolean("propagation", "announce_at_start", false);
            var logLevel = (int)Math.Clamp((long)config.GetInteger("logging", "loglevel", 3)!.Value + options.Verbosity, 0, 8);
            var onInbound = options.OnInbound ?? config.Get("lxmf", "on_inbound");
            var ignored = LoadHashes(Path.Combine(configDirectory, "ignored"));
            var configurationWarnings = new List<string>();
            var propagationOptions = propagationEnabled ? CreatePropagationOptions(config, configDirectory, configurationWarnings) : null;
            var storageDirectory = Path.Combine(configDirectory, "storage");
            var messagesDirectory = Path.Combine(storageDirectory, "messages");
            Directory.CreateDirectory(messagesDirectory);

            {
                var logger = new RnsdLogger(logLevel, timestamps: true, output, error,
                    options.Service ? Path.Combine(configDirectory, "logfile") : null).Log;
                logger(3, $"Configuration loaded from {configPath}");
                foreach (var warning in configurationWarnings) logger(1, warning);
                await using var node = ReticulumNode.Open(options.ReticulumDirectory, logger);
                using var identity = LoadOrCreateIdentity(Path.Combine(configDirectory, "identity"), logger);
                var routerOptions = new LxmRouterOptions
                {
                    DeliveryPerTransferLimit = checked((long)(acceptedKilobytes * 1000)),
                    InboundStampCost = stampCost,
                    EnforceStamps = false,
                    StoragePath = storageDirectory,
                };
                await using var router = new LxmRouter(node.Router, identity, displayName, routerOptions)
                {
                    Log = message => logger(5, message),
                };
                foreach (var ignoredSource in ignored) router.IgnoreDestination(Convert.FromHexString(ignoredSource));
                var propagationNode = propagationOptions is null ? null : router.EnablePropagationNode(propagationOptions);
                if (propagationNode is not null) propagationNode.Log = message => logger(5, message);
                var store = new MessageStore(messagesDirectory);
                router.MessageReceived = received =>
                {
                    try
                    {
                        var path = store.Save(received.Message);
                        logger(6, $"Received {received.Message} written to {path}");
                        if (!string.IsNullOrWhiteSpace(onInbound)) RunInbound(onInbound, path, logger, stop);
                    }
                    catch (Exception ex) { logger(1, $"Error processing received {received.Message}: {ex}"); }
                };

                await node.StartAsync(stop).ConfigureAwait(false);
                router.Start();
                await output.WriteLineAsync($"lxmd: delivery destination {router.DeliveryHashHex}").ConfigureAwait(false);
                if (propagationNode is not null)
                    await output.WriteLineAsync($"lxmd: propagation destination {Convert.ToHexStringLower(propagationNode.DestinationHash)}").ConfigureAwait(false);
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
            string? inlineValue = null;
            var equals = option.StartsWith("--", StringComparison.Ordinal) ? option.IndexOf('=') : -1;
            if (equals >= 0)
            {
                inlineValue = option[(equals + 1)..];
                option = option[..equals];
            }
            string Value()
            {
                if (inlineValue is { } value) { inlineValue = null; return value; }
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
                    if (!double.TryParse(Value(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
                        seconds <= 0 || !double.IsFinite(seconds) || seconds * 1000 > uint.MaxValue - 1)
                        throw new ArgumentException("--timeout must be positive, finite, and within the platform timer limit.");
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
            if (inlineValue is not null) throw new ArgumentException($"{option} does not accept a value.");
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

    private static LxmPropagationOptions CreatePropagationOptions(LxmdConfig config, string configDirectory, List<string> warnings)
    {
        // Upstream parses the legacy key, then overwrites it even when the new key is absent.
        _ = config.GetNumber("propagation", "propagation_transfer_max_accepted_size", 256);
        var transferKilobytes = config.GetNumber("propagation", "propagation_message_max_accepted_size", 256);
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
            MaxPeers = config.GetInteger("propagation", "max_peers", 20)!.Value,
            RequireAuthentication = config.GetBoolean("propagation", "auth_required", false),
            AllowedIdentities = LoadHashes(Path.Combine(configDirectory, "allowed")).Select(hash => Convert.FromHexString(hash)).ToArray(),
            FromStaticOnly = config.GetBoolean("propagation", "from_static_only", false),
            StaticPeers = staticPeers,
            AutoPeer = config.GetBoolean("propagation", "autopeer", true),
            AutoPeerMaxDepth = Math.Max(0, config.GetInteger("propagation", "autopeer_maxdepth", 4)!.Value),
            MaxPeeringCost = Math.Max(0, config.GetInteger("propagation", "remote_peering_cost_max", 26)!.Value),
            ControlAllowedIdentities = ReadOptionalHashes(config, "control_allowed", warnings),
            PrioritisedDestinations = ReadOptionalHashes(config, "prioritise_destinations", warnings),
            MaxInboundSyncs = Math.Max(1, config.GetInteger("propagation", "max_inbound_syncs", 3)!.Value),
            SequentialValidation = config.GetBoolean("propagation", "sequential_pn_stamp_validation", true),
            StaticPeerSequential = !config.GetBoolean("propagation", "static_peers_bypass_sequential", true),
        };
    }

    private static byte[][] ReadOptionalHashes(LxmdConfig config, string key, List<string> warnings)
    {
        var hashes = new List<byte[]>();
        foreach (var value in config.GetList("propagation", key))
        {
            try { hashes.Add(ParseHash(value)); }
            catch (FormatException) { warnings.Add($"Ignoring invalid 16-byte hash in propagation {key}: {value}"); }
        }
        return hashes.ToArray();
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
                        propagationNode.ScheduleAnnounce();
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
                    propagationNode.ScheduleAnnounce();
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

    private static void RunInbound(string command, string messagePath, Action<int, string> log, CancellationToken stop)
    {
        try
        {
            var parts = SplitCommand(command);
            if (parts.Count == 0) return;
            var start = new System.Diagnostics.ProcessStartInfo(parts[0])
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in parts.Skip(1)) start.ArgumentList.Add(argument);
            start.ArgumentList.Add(messagePath);
            using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("Inbound handler did not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            try
            {
                process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
                Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
                if (process.ExitCode != 0) log(2, $"Inbound handler returned {process.ExitCode} for {messagePath}.");
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                log(1, $"Inbound handler {(stop.IsCancellationRequested ? "cancelled" : "timed out")} for {messagePath}.");
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
