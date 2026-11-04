using System.Text;
using DotReticulum.Applications;
using DotReticulum.Core;
using DotReticulum.Transport;
using LXMF;
using System.Threading.Channels;
using System.Runtime.InteropServices;

namespace NomadNet;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false, true);
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
        {
            PrintHelp();
            return 0;
        }
        if (args.Contains("--version", StringComparer.Ordinal))
        {
            Console.WriteLine("NomadNet .NET client 1.4.4 (scoped implementation; upstream parity is not claimed)");
            return 0;
        }

        try
        {
            var options = ParseOptions(args);
            if (options.ImportConfig is { } configuration)
            {
                var imported = await NomadNetConfigImport.ImportAsync(configuration, options.DataDirectory).ConfigureAwait(false);
                foreach (var key in imported.UnsupportedKeys)
                    Console.Error.WriteLine($"Unsupported upstream setting (not imported): {NomadNetTerminalText.Safe(key)}");
                foreach (var warning in imported.Warnings) Console.Error.WriteLine(warning);
                Console.WriteLine($"Imported core configuration into {options.DataDirectory}; unsupported settings: {imported.UnsupportedKeys.Count}.");
                return 0;
            }
            if (options.Initialize)
            {
                await InitializeAsync(options.DataDirectory).ConfigureAwait(false);
                Console.WriteLine($"Initialized NomadNet data directory: {options.DataDirectory}");
                return 0;
            }
            if (options.ShowStatus)
            {
                await PrintStoredStatusAsync(options.DataDirectory).ConfigureAwait(false);
                return 0;
            }

            using var shutdown = new CancellationTokenSource();
            using var terminate = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM,
                context => { context.Cancel = true; shutdown.Cancel(); });
            Console.CancelKeyPress += CancelOnCtrlC;
            void CancelOnCtrlC(object? _, ConsoleCancelEventArgs eventArgs)
            {
                eventArgs.Cancel = true;
                shutdown.Cancel();
            }

            try
            {
                await RunAsync(options, shutdown.Token).ConfigureAwait(false);
                return 0;
            }
            finally
            {
                Console.CancelKeyPress -= CancelOnCtrlC;
            }
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"nomadnet: {exception.Message}");
            return 1;
        }
    }

    private static Options ParseOptions(string[] args)
    {
        string? dataDirectory = null;
        string? rnsConfig = null;
        var initialize = false;
        var showStatus = false;
        var daemon = false;
        string? importConfig = null;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--config":
                    dataDirectory = ValueAfter(args, ref index);
                    break;
                case "--rnsconfig":
                    rnsConfig = ValueAfter(args, ref index);
                    break;
                case "--import-config":
                    importConfig = ValueAfter(args, ref index);
                    break;
                case "--init":
                    initialize = true;
                    break;
                case "--status":
                    showStatus = true;
                    break;
                case "--daemon":
                    daemon = true;
                    break;
                case "--console":
                case "--textui":
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[index]} (use --help).");
            }
        }
        dataDirectory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "nomadnet");
        dataDirectory = Path.GetFullPath(dataDirectory);
        if (importConfig is not null && (initialize || showStatus || daemon))
            throw new ArgumentException("--import-config cannot be combined with --init, --status or --daemon.");
        return new Options(dataDirectory, rnsConfig, initialize, showStatus, daemon, importConfig);
    }

    private static string ValueAfter(string[] args, ref int index)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Expected a value after {args[index]}.");
        return args[++index];
    }

    private static async Task InitializeAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        _ = await NomadNetSettings.OpenAsync(directory).ConfigureAwait(false);
        _ = await NomadNetStore.OpenAsync(directory).ConfigureAwait(false);
    }

    private static async Task PrintStoredStatusAsync(string directory)
    {
        Console.WriteLine($"Data directory: {directory}");
        Console.WriteLine($"Initialized: {File.Exists(Path.Combine(directory, "state.json"))}");
        if (File.Exists(Path.Combine(directory, "state.json")))
        {
            var store = await NomadNetStore.OpenAsync(directory).ConfigureAwait(false);
            Console.WriteLine($"Contacts: {(await store.ContactsAsync().ConfigureAwait(false)).Count}");
            Console.WriteLine($"Messages: {(await store.MessagesAsync().ConfigureAwait(false)).Count}");
        }
    }

    private static async Task RunAsync(Options options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.DataDirectory);
        var settings = await NomadNetSettings.OpenAsync(options.DataDirectory, cancellationToken).ConfigureAwait(false);
        var state = await NomadNetStore.OpenAsync(options.DataDirectory, cancellationToken).ConfigureAwait(false);
        await using var node = ReticulumNode.Open(options.RnsConfig,
            (level, message) => Console.Error.WriteLine($"RNS[{level}]: {message}"));
        var identity = node.LoadOrCreateIdentity("nomadnet_identity");
        await node.StartAsync(cancellationToken).ConfigureAwait(false);
        await using var router = new LxmRouter(node.Router, identity, settings.DisplayName,
            new LxmRouterOptions
            {
                StoragePath = Path.Combine(options.DataDirectory, "storage"),
                DeliveryPerTransferLimit = settings.DeliveryTransferLimitBytes,
                InboundStampCost = settings.InboundStampCost,
                EnforceStamps = !settings.AcceptInvalidStamps,
            })
        {
            Log = message => Console.Error.WriteLine($"LXMF: {message}"),
        };
        var inbox = System.Threading.Channels.Channel.CreateBounded<ReceivedMessage>(new BoundedChannelOptions(128)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        router.MessageReceived = received =>
        {
            try { inbox.Writer.WriteAsync(received, cancellationToken).AsTask().GetAwaiter().GetResult(); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (ChannelClosedException) { }
        };
        foreach (var hash in await state.IgnoredDestinationsAsync(cancellationToken).ConfigureAwait(false))
            router.IgnoreDestination(Convert.FromHexString(hash));
        router.Start();
        await using var propagationPolicy = new NomadNetPropagation(options.DataDirectory, settings,
            () => FindPropagationCandidates(node.Router),
            hash => router.OutboundPropagationNode = hash is null ? null : Convert.FromHexString(hash),
            (limit, token) => router.RequestMessagesFromPropagationNodeAsync(maxMessages: limit, cancellationToken: token),
            router.CancelPropagationNodeRequests)
        { Log = message => Console.WriteLine(message) };
        await propagationPolicy.RefreshSelectionAsync(cancellationToken).ConfigureAwait(false);
        propagationPolicy.Start();
        var propagation = settings.EnablePropagation ? router.EnablePropagationNode(NomadNetNodeConfiguration.CreateOptions(settings)) : null;
        await using var host = settings.EnableNode ? new NomadNetHost(node.Router, identity, options.DataDirectory, settings,
            _ =>
            {
                if (propagation is { } hosted) hosted.ScheduleAnnounce();
                return ValueTask.CompletedTask;
            })
        {
            Log = message => Console.Error.WriteLine(message),
        } : null;
        host?.Start();
        await using var clientAnnounces = new NomadNetClientAnnounces(() => propagationPolicy.Settings,
            token =>
            {
                if (!router.SetInboundStampCost(propagationPolicy.Settings.InboundStampCost))
                    throw new InvalidOperationException("The configured inbound stamp cost is unsupported.");
                return router.AnnounceAsync(token);
            },
            (timestamp, token) => propagationPolicy.UpdateSettingsAsync(
                current => current with { LastClientAnnounce = timestamp }, token))
        { Log = message => Console.Error.WriteLine(message) };
        clientAnnounces.Start();
        await using var session = new TerminalSession(node, identity, router, state, options.DataDirectory, propagationPolicy, host, clientAnnounces);
        var receiver = PersistIncomingAsync(inbox.Reader, state, options.DataDirectory, session.ObserveConversationImageAsync, cancellationToken);

        Console.WriteLine("NomadNet .NET client (scoped native implementation; type help for commands)");
        Console.WriteLine($"LXMF delivery address: {router.DeliveryHashHex}");
        if (host is not null) Console.WriteLine($"Hosted node address: {host.DestinationHash}");
        if (propagation is not null) Console.WriteLine($"Propagation node address: {Convert.ToHexStringLower(propagation.DestinationHash)}");
        try
        {
            if (options.Daemon)
            {
                Console.WriteLine("Daemon ready.");
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            else await session.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            inbox.Writer.TryComplete();
            try { await receiver.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task PersistIncomingAsync(ChannelReader<ReceivedMessage> reader, NomadNetStore store, string directory,
        Func<NomadNetMessage, CancellationToken, Task> observeImage, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var received in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var message = received.Message;
                if (await store.IsIgnoredAsync(Convert.ToHexStringLower(message.SourceHash), cancellationToken).ConfigureAwait(false))
                    continue;
                var content = message.Content ?? "[non-text LXMF content]";
                var id = message.Hash is { Length: > 0 } hash ? Convert.ToHexStringLower(hash) : Guid.NewGuid().ToString("N");
                try
                {
                    int? imageIndex = null;
                    try
                    {
                        await NomadNetAttachments.SaveAsync(directory, id, message.Fields, cancellationToken).ConfigureAwait(false);
                        var attachments = NomadNetAttachments.Extract(message.Fields);
                        imageIndex = NomadNetAttachments.ImageIndex(message.Fields);
                        if (attachments.Count > 0) content += "\nAttachments: " + string.Join(", ", attachments.Select(attachment => attachment.Name));
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        Console.Error.WriteLine($"Could not save incoming attachments for {id}: {exception.Message}");
                        content += "\n[attachments could not be saved]";
                    }
                    var stored = new NomadNetMessage(id, Convert.ToHexStringLower(message.SourceHash), "inbound",
                        DateTimeOffset.FromUnixTimeMilliseconds((long)(message.Timestamp * 1000)), message.Title, content, "received")
                        { Renderer = NomadNetPresentation.Renderer(message.Fields), ImageAttachmentIndex = imageIndex };
                    await store.SaveMessageAsync(stored, cancellationToken).ConfigureAwait(false);
                    if (await store.ActiveConversationAsync(cancellationToken).ConfigureAwait(false) == Convert.ToHexStringLower(message.SourceHash))
                        try
                        {
                            Console.WriteLine($"Conversation received: {NomadNetTerminalText.Safe(message.Title)} " +
                                NomadNetPresentation.Conversation(content, NomadNetPresentation.Renderer(message.Fields)));
                            await observeImage(stored, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception exception)
                        {
                            Console.Error.WriteLine($"Could not render incoming message {id}: {exception.Message}");
                        }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"Could not persist incoming message: {exception.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            NomadNet .NET client 1.4.4
            Usage: nomadnet [--config <directory>] [--rnsconfig <directory>] [--init | --status | --daemon]
                   nomadnet --import-config <upstream-INI> --config <new-directory>
                   nomadnet [--console] [--textui]
                   nomadnet --help | --version

            Interactive commands:
              status                         Show identity and Reticulum connectivity
              announce                       Reannounce delivery and hosted destinations
              client [status|announce]       Inspect or announce only LXMF delivery
              client startup <on|off>         Save next-start announcement policy
              client interval <30–10080>     Set periodic announcement minutes
              client stamp-cost <none|1–254> Set required inbound stamp cost
              client name <text>              Save display name without announcing
              client markdown <on|off>        Set outgoing LXMF Markdown renderer
              peers                          List announced Reticulum destinations
              contact add <hash> <name>      Save a known LXMF destination
              contacts | contact remove <hash>
              contact trust <hash> <unknown|trusted|untrusted|warning>
              contact ignore <hash> <on|off>  Persist and apply LXMF blacklist
              contact delivery <hash> <direct|propagated|opportunistic>
              contact notes <hash> <text>    Store local peer notes
              conversations                   List latest activity and unread/failures
              conversation active <hash|off> Mark read and select viewed conversation
              conversation read|delete|purge-failed <hash>
              conversation export <hash> <name.txt>
              conversation images status|load|reload|cancel
                                             Preview shown conversation images as ASCII
              send <hash> <message>          Send a text LXMF message
              send-propagated <hash> <text>  Send through selected propagation node
              attach <hash> <file>           Send a file attachment
              files [browse <directory>]    Start a local attachment picker
              files list|up|refresh|selected | files open <number>
              files done|cancel|clear        Save, cancel or clear selection
              send-files <hash> <message>   Send text and selected attachments
              image|audio <hash> <file>      Send encoded image/audio (no transcoding)
              paper <hash> <message>         Generate an encrypted lxm:// URI
              paper-qr <hash> <message>      Save encrypted paper PNG and conversation
              paper-save <hash> <message>    Save encrypted paper URI and conversation
              qr <lxm://uri> <name.png>       Export an existing paper QR PNG
              qr-show <lxm://uri>             Display a black-on-white terminal QR
              ingest <lxm://uri>             Ingest a paper URI for this identity
              propagation <hash|auto|off>    Select and persist propagation node
              trust-node <node-hash> <on|off> Trust node identity for automatic selection
              sync [limit] | sync status|cancel|wait
              sync schedule <on|off> [minutes] [limit]
              fallback <on|off>              Retry failed direct messages via propagation
              messages [hash]                 Show durable conversation history
              browse <hash> [page-path]       Fetch a bounded Nomad Network page
              history | back | forward       Show/replay session browser navigation
              nodes | open-node <number>     Browse saved nodes in primary directory order
              contact node|identify <hash> <on|off>
              contact rank <hash> <integer|none>
              page | partials                Show composed page or partial targets
              links | open <number>            Follow a displayed Micron link
              fields | field <number> <value> Edit text or select checkbox/radio (on/off)
              anchor <name> | fold <number>    Jump to anchor or toggle section
              partial <number>                Fetch a partial with current form data
              media <number> <name.webp>      Download image bytes (no visual decode)
              show-media <number> [color]    Decode to text/ANSI true-colour preview
              images [mode|load|reload]      Browser/conversation policy: never/manual/auto/always
              image-convert <file> <name.webp> Convert local image into downloads
              reload                          Fetch current page without cache
              bookmark [title] | bookmarks | open-bookmark <number>
              fetch <hash> <file-path> <name> Download a bounded /file response
              node paths|refresh|announce     Inspect, refresh or announce hosted content
              node-config                    Show saved hosting limits (applied at startup)
              node-config transfer|sync <KB> | node-config storage <MB>
              node-config cost <13-256> | node-config peers <default|0-65536>
              node-config static|priority <hash> <on|off>
              node-peers                     Inspect actual hosted propagation peers
              node-peer-sync <hash>          Request peer transfer through LXMF (three-minute deadline)
              rrc connect <hub> [nick]        Connect to an identified rrc.hub
              rrc join <room> [key] | rrc part <room>
              rrc send|action <room> <text>  Send group message/action
              rrc list | rrc who <room>       Request hub rooms/membership
              rrc rooms | rrc members <room> Show parsed directory/member state
              rrc nick <name> | rrc ping
              rrc auto-reconnect <on|off>     Persist optional hub recovery policy
              rrc status | rrc history [room] | rrc disconnect | rrc reconnect
              rrc active <room|off> | rrc read <room> | rrc clear <room>
              rrc history-limit <0-1000>      Persist per-room retention (0: aggregate cap only)
              rrc auto-list <on|off> | rrc auto-who <on|off>
              help | quit

            The app stores its identity, contacts, conversations, and bookmarks below --config.
            """);
    }

    private sealed record Options(string DataDirectory, string? RnsConfig, bool Initialize, bool ShowStatus, bool Daemon, string? ImportConfig);

    private static IEnumerable<NomadNetPropagationCandidate> FindPropagationCandidates(TransportRouter transport)
    {
        foreach (var entry in transport.Engine.Known.ExportSnapshot())
        {
            if (!LxmfPropagationAnnounce.TryParse(entry.Entry.AppData, out var announce) || !announce!.Enabled) continue;
            var peer = transport.Engine.Known.Recall(entry.DestinationHash);
            if (peer is null) continue;
            var expected = Destination.ComputeHashFromNameAndIdentity("lxmf.propagation", peer.Hash);
            if (!expected.AsSpan().SequenceEqual(entry.DestinationHash)) continue;
            var hops = transport.HopsTo(entry.DestinationHash);
            if (hops is null) continue;
            yield return new NomadNetPropagationCandidate(
                Convert.ToHexStringLower(Destination.ComputeHashFromNameAndIdentity("nomadnetwork.node", peer.Hash)),
                Convert.ToHexStringLower(entry.DestinationHash), hops.Value);
        }
    }

    private sealed class TerminalSession(ReticulumNode node, Identity identity, LxmRouter router, NomadNetStore store,
        string dataDirectory, NomadNetPropagation propagation, NomadNetHost? host, NomadNetClientAnnounces clientAnnounces) : IAsyncDisposable
    {
        private readonly NomadNetDelivery _delivery = new(router, identity, store,
            () => propagation.Settings.TryPropagationOnSendFail)
        { Log = message => Console.Error.WriteLine(message) };
        private readonly NomadNetPageCache _cache = new(dataDirectory);
        private readonly NomadNetRemoteMediaCache _mediaCache = new(dataDirectory);
        private string? _currentNode;
        private string _currentPath = NomadNetAddress.DefaultPagePath;
        private RrcClient? _rrc;
        private NomadNetMicronBrowser? _browser;
        private NomadNetInlineMedia? _inlineImages;
        private NomadNetConversationImages? _conversationImages;
        private readonly object _conversationGate = new();
        private IReadOnlyList<NomadNetMessage> _conversationMessages = [];
        private readonly NomadNetImageCache _conversationConversions = new(dataDirectory,
            new NomadNetImages(propagation.Settings.MediaConverter, Path.Combine(dataDirectory, "storage", "image-work")));
        private NomadNetImageLinkMetrics? _pageMetrics;
        private NomadNetRenderedPage? _page => _browser?.Current;
        private object? _currentRequestData;
        private readonly NomadNetNavigationHistory _navigation = new();
        private NomadNetFilePicker? _filePicker;
        private IReadOnlyList<string> _selectedAttachments = [];

        public async ValueTask DisposeAsync()
        {
            if (_browser is not null) await _browser.DisposeAsync().ConfigureAwait(false);
            if (_inlineImages is not null) await _inlineImages.DisposeAsync().ConfigureAwait(false);
            if (_conversationImages is not null) await _conversationImages.DisposeAsync().ConfigureAwait(false);
            _mediaCache.Dispose();
            await _delivery.DisposeAsync().ConfigureAwait(false);
            if (_rrc is not null) await _rrc.DisposeAsync().ConfigureAwait(false);
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Console.Write("nomadnet> ");
                var line = await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) return;
                if (line.Length > 16 * 1024)
                {
                    Console.WriteLine("Command line exceeds the 16 KiB limit.");
                    continue;
                }
                var command = line.Trim();
                if (command.Length == 0) continue;
                try
                {
                    if (await ExecuteAsync(command, cancellationToken).ConfigureAwait(false)) return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"Error: {exception.Message}");
                }
            }
        }

        private async Task<bool> ExecuteAsync(string command, CancellationToken cancellationToken)
        {
            if (command.Equals("quit", StringComparison.OrdinalIgnoreCase) || command.Equals("exit", StringComparison.OrdinalIgnoreCase))
                return true;
            if (command.Equals("help", StringComparison.OrdinalIgnoreCase)) { PrintHelp(); return false; }
            if (command.Equals("files", StringComparison.OrdinalIgnoreCase) || command.StartsWith("files ", StringComparison.OrdinalIgnoreCase))
            {
                ExecuteFiles(command);
                return false;
            }
            if (command.StartsWith("send-files ", StringComparison.OrdinalIgnoreCase))
            {
                if (_filePicker is not null) throw new InvalidOperationException("Complete or cancel the open file selection first.");
                var parts = Split(command, 3);
                if (parts.Length != 3) throw new ArgumentException("Usage: send-files <hash> <message>");
                var fields = await NomadNetFilePicker.ReadAttachmentsAsync(_selectedAttachments, cancellationToken).ConfigureAwait(false);
                await SendAsync(parts[1], parts[2], cancellationToken, fields).ConfigureAwait(false);
                _selectedAttachments = [];
                return false;
            }
            if (command.StartsWith("rrc ", StringComparison.OrdinalIgnoreCase))
            {
                await ExecuteRrcAsync(command[4..], cancellationToken).ConfigureAwait(false);
                return false;
            }
            if (command.Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"LXMF delivery address: {router.DeliveryHashHex}");
                Console.WriteLine($"Reticulum identity: {Convert.ToHexStringLower(identity.Hash)}");
                Console.WriteLine($"Reticulum interfaces: {node.Interfaces.Count}; known destinations: {node.Router.Engine.Known.Count}");
                Console.WriteLine(node.IsConnectedToSharedInstance ? "Connected to shared Reticulum instance." : "Using local Reticulum instance.");
                if (router.PropagationNode is { } stored) Console.WriteLine($"Propagation stored messages: {stored.MessageCount}");
                return false;
            }
            if (command.Equals("node-config", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("node-config ", StringComparison.OrdinalIgnoreCase))
            {
                var arguments = command.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1..];
                if (arguments.Length > 0)
                {
                    await propagation.UpdateSettingsAsync(current => NomadNetNodeConfiguration.Change(current, arguments),
                        cancellationToken).ConfigureAwait(false);
                    Console.WriteLine("Node configuration saved; restart required to apply hosting changes.");
                }
                var saved = propagation.Settings;
                var configured = NomadNetNodeConfiguration.CreateOptions(saved);
                Console.WriteLine($"Saved node configuration (startup only): transfer={configured.TransferLimit} bytes; sync={configured.SyncLimit} bytes; storage={configured.StorageLimit} bytes; cost={configured.StampCost}; peers={configured.MaxPeers}.");
                Console.WriteLine($"Saved static peers: {string.Join(',', saved.PropagationStaticPeers)}");
                Console.WriteLine($"Saved priority destinations: {string.Join(',', saved.PrioritisedDestinations)}");
                if (router.PropagationNode is { } running &&
                    LxmfPropagationAnnounce.TryParse(running.GetAnnounceAppData(), out var announced))
                    Console.WriteLine(FormattableString.Invariant($"Running node: transfer={announced!.TransferLimitKilobytes} KB; sync={announced.SyncLimitKilobytes} KB; storage={running.StorageLimit} bytes; cost={announced.StampCost}; stored={running.MessageCount}; bytes={running.StoredBytes}."));
                return false;
            }
            if (command.Equals("node-peers", StringComparison.OrdinalIgnoreCase))
            {
                if (router.PropagationNode is not { } running) throw new InvalidOperationException("Propagation hosting is disabled.");
                foreach (var peer in running.Peers)
                    Console.WriteLine($"Peer {Convert.ToHexStringLower(peer.DestinationHash)}: alive={peer.Alive}; state={peer.State}; handled={peer.HandledCount}; unhandled={peer.UnhandledCount}; outgoing={peer.Outgoing}; incoming={peer.Incoming}; tx={peer.TxBytes}; rx={peer.RxBytes}.");
                Console.WriteLine($"Propagation peers: {running.Peers.Count}.");
                return false;
            }
            if (command.StartsWith("node-peer-sync ", StringComparison.OrdinalIgnoreCase))
            {
                if (router.PropagationNode is not { } running) throw new InvalidOperationException("Propagation hosting is disabled.");
                var hash = Convert.FromHexString(NomadNetAddress.NormalizeHash(command["node-peer-sync ".Length..].Trim()));
                if (!running.Peers.Any(peer => peer.DestinationHash.AsSpan().SequenceEqual(hash)))
                    throw new InvalidOperationException("Select an existing propagation peer.");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromMinutes(3));
                var transferred = await running.SynchronizePeerAsync(hash, deadline.Token).ConfigureAwait(false);
                Console.WriteLine($"Propagation peer sync complete: {transferred} messages.");
                return false;
            }
            if (command.Equals("announce", StringComparison.OrdinalIgnoreCase))
            {
                await clientAnnounces.AnnounceNowAsync(cancellationToken).ConfigureAwait(false);
                if (host is not null) await host.AnnounceAsync(cancellationToken).ConfigureAwait(false);
                Console.WriteLine("Destinations announced.");
                return false;
            }
            if (command.Equals("client", StringComparison.OrdinalIgnoreCase) || command.StartsWith("client ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && parts[1] == "name")
                {
                    var name = command["client name ".Length..];
                    await NomadNetClientProfile.SetDisplayNameAsync(propagation, router.SetDisplayName,
                        name, cancellationToken).ConfigureAwait(false);
                    Console.WriteLine("Client display name saved without announce: " + NomadNetTerminalText.Safe(name));
                }
                else if (parts is [_, "markdown", "on" or "off"])
                {
                    await propagation.UpdateSettingsAsync(current => current with { ComposeInMarkdown = parts[2] == "on" }, cancellationToken).ConfigureAwait(false);
                    Console.WriteLine("Outgoing Markdown policy saved.");
                }
                else if (parts.Length == 1 || parts is [_, "status"])
                {
                    var current = propagation.Settings;
                    Console.WriteLine($"Client startup announce: {current.ClientAnnounceAtStart}; interval minutes: {current.ClientAnnounceIntervalMinutes}; last successful announce: {current.LastClientAnnounce?.ToString("O") ?? "never"}.");
                    Console.WriteLine($"Client enable_client metadata: {current.EnableClient} (unused by the primary runtime); stamp cost: {current.InboundStampCost?.ToString() ?? "none"}.");
                    Console.WriteLine("Client display name: " + NomadNetTerminalText.Safe(current.DisplayName));
                }
                else if (parts is [_, "announce"])
                {
                    await clientAnnounces.AnnounceNowAsync(cancellationToken).ConfigureAwait(false);
                    Console.WriteLine("Client destination announced.");
                }
                else if (parts is [_, "startup", "on" or "off"])
                {
                    await propagation.UpdateSettingsAsync(current => current with { ClientAnnounceAtStart = parts[2] == "on" }, cancellationToken).ConfigureAwait(false);
                    Console.WriteLine("Client startup announce policy saved; applies on next restart.");
                }
                else if (parts is [_, "interval", _] && int.TryParse(parts[2], out var interval))
                {
                    await propagation.UpdateSettingsAsync(current => current with { ClientAnnounceIntervalMinutes = interval }, cancellationToken).ConfigureAwait(false);
                    Console.WriteLine("Client announce interval saved.");
                }
                else if (parts is [_, "stamp-cost", _])
                {
                    var cost = parts[2] == "none" ? (int?)null : int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                    await propagation.UpdateSettingsAsync(current => current with { InboundStampCost = cost }, cancellationToken).ConfigureAwait(false);
                    if (!router.SetInboundStampCost(cost)) throw new InvalidOperationException("The saved inbound stamp cost is unsupported.");
                    Console.WriteLine("Client stamp cost saved; next announce advertises it.");
                }
                else throw new ArgumentException("Usage: client [status|announce|name <text>|markdown <on|off>|startup <on|off>|interval <30–10080>|stamp-cost <none|1–254>]");
                return false;
            }

            if (command.Equals("peers", StringComparison.OrdinalIgnoreCase)) { await ShowPeersAsync(cancellationToken).ConfigureAwait(false); return false; }
            if (command.Equals("node refresh", StringComparison.OrdinalIgnoreCase))
            {
                (host ?? throw new InvalidOperationException("Enable node hosting in settings.json.")).Refresh();
                Console.WriteLine("Hosted content refreshed.");
                return false;
            }
            if (command.Equals("node paths", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var path in (host ?? throw new InvalidOperationException("Enable node hosting in settings.json.")).RegisteredPaths)
                    Console.WriteLine("Hosted path " + NomadNetTerminalText.Safe(path));
                Console.WriteLine("Hosted paths complete.");
                return false;
            }
            if (command.Equals("node announce", StringComparison.OrdinalIgnoreCase))
            {
                await (host ?? throw new InvalidOperationException("Enable node hosting in settings.json.")).AnnounceAsync(cancellationToken).ConfigureAwait(false);
                Console.WriteLine("Hosted node announced.");
                return false;
            }
            if (command.StartsWith("propagation ", StringComparison.OrdinalIgnoreCase))
            {
                var value = command["propagation ".Length..].Trim();
                var automatic = value.Equals("auto", StringComparison.OrdinalIgnoreCase);
                var disabled = value.Equals("off", StringComparison.OrdinalIgnoreCase);
                var hash = automatic || disabled ? null : NomadNetAddress.NormalizeHash(value);
                await propagation.UpdateSettingsAsync(current => current with
                    { PropagationNode = hash, UsePropagation = !disabled }, cancellationToken).ConfigureAwait(false);
                Console.WriteLine("Propagation selection saved.");
                return false;
            }
            if (command.StartsWith("trust-node ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = Split(command, 3);
                if (parts.Length != 3 || parts[2] is not ("on" or "off"))
                    throw new ArgumentException("Usage: trust-node <nomadnetwork.node hash> <on|off>");
                var hash = NomadNetAddress.NormalizeHash(parts[1]);
                await propagation.UpdateSettingsAsync(current => current with
                {
                    TrustedNodes = parts[2] == "on"
                        ? current.TrustedNodes.Append(hash).Distinct(StringComparer.Ordinal).ToList()
                        : current.TrustedNodes.Where(item => item != hash).ToList(),
                }, cancellationToken).ConfigureAwait(false);
                Console.WriteLine("Node trust saved.");
                return false;
            }
            if (command.StartsWith("fallback ", StringComparison.OrdinalIgnoreCase))
            {
                var value = command["fallback ".Length..].Trim();
                if (value is not ("on" or "off")) throw new ArgumentException("Usage: fallback <on|off>");
                await propagation.UpdateSettingsAsync(current => current with { TryPropagationOnSendFail = value == "on" },
                    cancellationToken).ConfigureAwait(false);
                Console.WriteLine("Fallback policy saved.");
                return false;
            }
            if (command.Equals("sync", StringComparison.OrdinalIgnoreCase) || command.StartsWith("sync ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[1] == "status")
                    Console.WriteLine($"Propagation sync: {propagation.Status}; node {propagation.Selected ?? "none"}; {router.PropagationTransferState} {router.PropagationTransferProgress:P0}");
                else if (parts.Length == 2 && parts[1] == "cancel")
                    await propagation.CancelSyncAsync(cancellationToken).ConfigureAwait(false);
                else if (parts.Length == 2 && parts[1] == "wait")
                    await propagation.WaitForSyncAsync(cancellationToken).ConfigureAwait(false);
                else if (parts.Length is >= 3 and <= 5 && parts[1] == "schedule" && parts[2] is "on" or "off")
                    await propagation.UpdateSettingsAsync(current => current with
                    {
                        PeriodicLxmfSync = parts[2] == "on",
                        LxmfSyncIntervalMinutes = parts.Length >= 4 ? int.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture) : current.LxmfSyncIntervalMinutes,
                        LxmfSyncLimit = parts.Length >= 5 ? int.Parse(parts[4], System.Globalization.CultureInfo.InvariantCulture) : current.LxmfSyncLimit,
                    }, cancellationToken).ConfigureAwait(false);
                else if (parts.Length <= 2 && (parts.Length == 1 || int.TryParse(parts[1], out _)))
                {
                    var limit = parts.Length == 1 ? 0 : int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                    Console.WriteLine(await propagation.BeginSyncAsync(limit, cancellationToken).ConfigureAwait(false)
                        ? "Propagation sync started." : "Propagation sync already in progress.");
                }
                else throw new ArgumentException("Usage: sync [limit|status|cancel|wait] or sync schedule <on|off> [minutes] [limit]");
                return false;
            }
            if (command.StartsWith("ingest ", StringComparison.OrdinalIgnoreCase))
            {
                var result = await router.IngestPaperUriAsync(command["ingest ".Length..].Trim(), cancellationToken: cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"Paper ingest: {result}");
                return false;
            }
            if (command.StartsWith("paper ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = Split(command, 3);
                if (parts.Length != 3) throw new ArgumentException("Usage: paper <hash> <message>");
                var destination = Convert.FromHexString(NomadNetAddress.NormalizeHash(parts[1]));
                var remote = await RecallAsync(destination, cancellationToken).ConfigureAwait(false);
                var message = LxMessage.Create(destination, identity, parts[2], method: LxmMethod.Paper);
                message.Pack(destinationIdentity: remote);
                Console.WriteLine(message.AsUri());
                return false;
            }
            if (command.StartsWith("paper-qr ", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("paper-save ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = Split(command, 3);
                if (parts.Length != 3) throw new ArgumentException("Usage: paper-qr|paper-save <hash> <message>");
                var destination = Convert.FromHexString(NomadNetAddress.NormalizeHash(parts[1]));
                var remote = await RecallAsync(destination, cancellationToken).ConfigureAwait(false);
                var message = LxMessage.Create(destination, identity, parts[2], method: LxmMethod.Paper);
                message.Pack(destinationIdentity: remote);
                var uri = message.AsUri();
                var id = Convert.ToHexStringLower(message.Hash ?? throw new InvalidDataException("Paper message has no hash."));
                var qr = parts[0].Equals("paper-qr", StringComparison.OrdinalIgnoreCase);
                await SaveDownloadAsync(qr ? NomadNetPaperQr.Png(uri, cancellationToken) : Encoding.UTF8.GetBytes(uri + "\n"),
                    "LXM_" + id + (qr ? ".png" : ".txt"), cancellationToken).ConfigureAwait(false);
                await store.SaveMessageAsync(new NomadNetMessage(id, Convert.ToHexStringLower(destination), "outbound",
                    DateTimeOffset.FromUnixTimeMilliseconds((long)(message.Timestamp * 1000)), "", parts[2], "paper"),
                    cancellationToken).ConfigureAwait(false);
                Console.WriteLine("Paper conversation saved.");
                return false;
            }
            if (command.StartsWith("qr-show ", StringComparison.OrdinalIgnoreCase))
            {
                Console.Write(NomadNetPaperQr.Display(command["qr-show ".Length..].Trim(), cancellationToken));
                Console.WriteLine("QR display complete.");
                return false;
            }
            if (command.StartsWith("qr ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = Split(command, 3);
                if (parts.Length != 3 || !parts[2].EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Usage: qr <lxm://uri> <name.png>");
                await SaveDownloadAsync(NomadNetPaperQr.Png(parts[1], cancellationToken), parts[2],
                    cancellationToken).ConfigureAwait(false);
                return false;
            }
            if (command.StartsWith("send-propagated ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = Split(command, 3);
                if (parts.Length != 3) throw new ArgumentException("Usage: send-propagated <hash> <message>");
                if (router.OutboundPropagationNode is null) throw new InvalidOperationException("Select a propagation node first.");
                await SendAsync(parts[1], parts[2], cancellationToken, method: LxmMethod.Propagated).ConfigureAwait(false);
                return false;
            }
            if (command.StartsWith("attach ", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("image ", StringComparison.OrdinalIgnoreCase) || command.StartsWith("audio ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = Split(command, 3);
                if (parts.Length != 3) throw new ArgumentException("Usage: attach|image|audio <hash> <file>");
                var bytes = await NomadNetLocalFiles.ReadAsync(parts[2], cancellationToken: cancellationToken).ConfigureAwait(false);
                var kind = parts[0].ToLowerInvariant();
                var name = NomadNetAttachments.SafeName(Path.GetFileName(parts[2]));
                Dictionary<object, object?> fields = kind == "attach"
                    ? new() { [LxmField.FileAttachments] = new object?[] { new object?[] { name, bytes } } }
                    : new() { [kind == "image" ? LxmField.Image : LxmField.Audio] = new object?[] { Path.GetExtension(name).TrimStart('.'), bytes } };
                await SendAsync(parts[1], "", cancellationToken, fields).ConfigureAwait(false);
                return false;
            }
            if (command.Equals("contacts", StringComparison.OrdinalIgnoreCase) || command.Equals("contact list", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var contact in await store.ContactsAsync(cancellationToken).ConfigureAwait(false))
                    Console.WriteLine($"{contact.DestinationHash}  {NomadNetTerminalText.Safe(contact.Name)} trust={contact.TrustLevel} ignored={contact.Ignored} delivery={contact.PreferredDelivery}" +
                        (contact.Notes.Length == 0 ? "" : $" notes={NomadNetTerminalText.Safe(contact.Notes)}"));
                Console.WriteLine("Contacts complete.");
                return false;
            }
            if (command.StartsWith("contact trust ", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("contact ignore ", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("contact delivery ", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("contact notes ", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("contact node ", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("contact identify ", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("contact rank ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = Split(command, 4);
                if (parts.Length != 4) throw new ArgumentException("Use contact trust|ignore|delivery|notes|node|identify|rank <hash> <value>.");
                var hash = NomadNetAddress.NormalizeHash(parts[2]);
                var contact = await store.FindContactAsync(hash, cancellationToken).ConfigureAwait(false) ??
                    new NomadNetContact(hash, hash) { Ignored = await store.IsIgnoredAsync(hash, cancellationToken).ConfigureAwait(false) };
                contact = parts[1] switch
                {
                    "trust" => contact with { TrustLevel = parts[3] switch
                        { "warning" => 0, "untrusted" => 1, "unknown" => 2, "trusted" => 255, _ => throw new ArgumentException("Invalid trust level.") } },
                    "ignore" when parts[3] is "on" or "off" => contact with { Ignored = parts[3] == "on" },
                    "delivery" => contact with { PreferredDelivery = parts[3] switch
                        { "direct" => 1, "propagated" => 2, "opportunistic" => 3, _ => throw new ArgumentException("Invalid delivery method.") } },
                    "notes" => contact with { Notes = parts[3] },
                    "node" when parts[3] is "on" or "off" => contact with { HostsNode = parts[3] == "on" },
                    "identify" when parts[3] is "on" or "off" => contact with { IdentifyOnConnect = parts[3] == "on" },
                    "rank" => contact with { SortRank = parts[3] == "none" ? null :
                        int.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture) },
                    _ => throw new ArgumentException("Invalid contact command."),
                };
                await SaveDirectoryContactAsync(contact, cancellationToken).ConfigureAwait(false);
                if (contact.Ignored) router.IgnoreDestination(Convert.FromHexString(hash));
                else router.UnignoreDestination(Convert.FromHexString(hash));
                Console.WriteLine("Contact policy saved.");
                return false;
            }
            if (command.StartsWith("contact add ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = Split(command["contact add ".Length..], 2);
                if (parts.Length != 2) throw new ArgumentException("Usage: contact add <32-hex-hash> <name>");
                var existing = await store.FindContactAsync(parts[0], cancellationToken).ConfigureAwait(false) ??
                    new NomadNetContact(parts[0], parts[1]) { Ignored = await store.IsIgnoredAsync(parts[0], cancellationToken).ConfigureAwait(false) };
                await SaveDirectoryContactAsync(existing with { Name = parts[1] }, cancellationToken).ConfigureAwait(false);
                Console.WriteLine("Contact saved.");
                return false;
            }
            if (command.StartsWith("contact remove ", StringComparison.OrdinalIgnoreCase))
            {
                var hash = command["contact remove ".Length..].Trim();
                Console.WriteLine(await store.RemoveContactAsync(hash, cancellationToken).ConfigureAwait(false) ? "Contact removed." : "Contact not found.");
                return false;
            }
            if (command.Equals("conversations", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var conversation in await store.ConversationsAsync(cancellationToken).ConfigureAwait(false))
                    Console.WriteLine($"Conversation {conversation.PeerHash} unread={conversation.Unread} failed={conversation.Failures} messages={conversation.MessageCount} " +
                        NomadNetTerminalText.Safe(conversation.Name ?? ""));
                Console.WriteLine("Conversations complete.");
                return false;
            }
            if (command.StartsWith("conversation images ", StringComparison.OrdinalIgnoreCase))
            {
                var action = command["conversation images ".Length..].Trim().ToLowerInvariant();
                if (action == "status")
                    Console.WriteLine($"Conversation images: loading={_conversationImages?.Completion.IsCompleted == false}; previews={_conversationImages?.Previews.Count ?? 0}.");
                else if (action == "cancel")
                {
                    if (_conversationImages is not null)
                        await _conversationImages.ShowAsync([], NomadNetImageLoading.Never, cancellationToken: cancellationToken).ConfigureAwait(false);
                    Console.WriteLine("Conversation image loading cancelled.");
                }
                else if (action is "load" or "reload")
                {
                    if (_conversationMessages.Count == 0) throw new InvalidOperationException("Show stored messages first.");
                    await ShowConversationImagesAsync(_conversationMessages, true, action == "reload", cancellationToken).ConfigureAwait(false);
                    Console.WriteLine("Conversation image loading started.");
                }
                else throw new ArgumentException("Use conversation images status|load|reload|cancel.");
                return false;
            }
            if (command.StartsWith("conversation ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = Split(command, 4);
                if (parts.Length < 3) throw new ArgumentException("Use conversation active|read|delete|purge-failed|export <hash>.");
                var hash = parts[1] == "active" && parts[2] == "off" ? null : NomadNetAddress.NormalizeHash(parts[2]);
                switch (parts[1])
                {
                    case "active" when parts.Length == 3:
                        await store.SetActiveConversationAsync(hash, cancellationToken).ConfigureAwait(false);
                        if (hash is not null) await ShowMessagesAsync(hash, cancellationToken).ConfigureAwait(false);
                        else
                        {
                            _conversationMessages = [];
                            if (_conversationImages is not null)
                                await _conversationImages.ShowAsync([], NomadNetImageLoading.Never, cancellationToken: cancellationToken).ConfigureAwait(false);
                        }
                        break;
                    case "read" when parts.Length == 3:
                        await store.MarkReadAsync(hash!, cancellationToken).ConfigureAwait(false);
                        break;
                    case "delete" or "purge-failed" when parts.Length == 3:
                        if (_conversationImages is not null)
                            await _conversationImages.ShowAsync([], NomadNetImageLoading.Never, cancellationToken: cancellationToken).ConfigureAwait(false);
                        _conversationMessages = [];
                        var removed = await store.DeleteConversationAsync(hash!, parts[1] == "purge-failed", cancellationToken).ConfigureAwait(false);
                        await NomadNetAttachments.DeleteAsync(dataDirectory, removed, cancellationToken).ConfigureAwait(false);
                        break;
                    case "export" when parts.Length == 4:
                        await SaveDownloadAsync(Encoding.UTF8.GetBytes(await store.ExportConversationAsync(hash!, cancellationToken).ConfigureAwait(false)),
                            parts[3], cancellationToken).ConfigureAwait(false);
                        break;
                    default: throw new ArgumentException("Invalid conversation command.");
                }
                Console.WriteLine("Conversation command complete.");
                return false;
            }
            if (command.StartsWith("send ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = Split(command, 3);
                if (parts.Length != 3) throw new ArgumentException("Usage: send <32-hex-hash> <message>");
                await SendAsync(parts[1], parts[2], cancellationToken).ConfigureAwait(false);
                return false;
            }
            if (command.Equals("messages", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("messages ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = command.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                await ShowMessagesAsync(parts.Length == 2 ? parts[1] : null, cancellationToken).ConfigureAwait(false);
                return false;
            }
            if (command.StartsWith("browse ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = Split(command, 3);
                if (parts.Length is < 2 or > 3) throw new ArgumentException("Usage: browse <destination-hash> [page-path]");
                await BrowseAsync(parts[1], parts.Length == 3 ? parts[2] : NomadNetAddress.DefaultPagePath, cancellationToken).ConfigureAwait(false);
                return false;
            }
            if (command.Equals("links", StringComparison.OrdinalIgnoreCase)) { ShowLinks(); return false; }
            if (command.Equals("page", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine((_page ?? throw new InvalidOperationException("Browse a page first.")).DisplayText);
                return false;
            }
            if (command.Equals("partials", StringComparison.OrdinalIgnoreCase))
            {
                var partials = (_page ?? throw new InvalidOperationException("Browse a page first.")).Partials;
                for (var index = 0; index < partials.Count; index++)
                    Console.WriteLine($"Partial {index + 1}: {partials[index].Id ?? "(unnamed)"} -> {partials[index].Target}");
                return false;
            }
            if (command.Equals("fields", StringComparison.OrdinalIgnoreCase))
            {
                var fields = (_page ?? throw new InvalidOperationException("Browse a page first.")).Fields;
                for (var index = 0; index < fields.Count; index++)
                    Console.WriteLine($"Field {index + 1}: {NomadNetTerminalText.Safe(fields[index].Name)} ({fields[index].Kind}) = " +
                        (fields[index].Kind == "field" ? fields[index].DisplayValue : fields[index].Selected ? "on" : "off"));
                Console.WriteLine("Fields complete.");
                return false;
            }
            if (command.StartsWith("field ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = Split(command, 3);
                var page = _page ?? throw new InvalidOperationException("Browse a page first.");
                if (parts.Length < 2) throw new ArgumentException("Use field <number> [value].");
                _browser!.SetField(ParseIndex(parts[1], page.Fields.Count), parts.Length == 3 ? parts[2].Replace("\\n", "\n", StringComparison.Ordinal) : "");
                Console.WriteLine("Field updated.");
                return false;
            }
            if (command.StartsWith("anchor ", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine((_browser ?? throw new InvalidOperationException("Browse a page first.")).AtAnchor(command[7..]));
                return false;
            }
            if (command.StartsWith("fold ", StringComparison.OrdinalIgnoreCase))
            {
                var page = _page ?? throw new InvalidOperationException("Browse a page first.");
                _browser!.ToggleFold(ParseIndex(command[5..], page.Folds.Count));
                Console.WriteLine(_inlineImages?.Render(page) ?? page.DisplayText);
                return false;
            }
            if (command.StartsWith("partial ", StringComparison.OrdinalIgnoreCase))
            {
                var page = _page ?? throw new InvalidOperationException("Browse a page first.");
                await _browser!.RefreshAsync(ParseIndex(command[8..], page.Partials.Count), cancellationToken).ConfigureAwait(false);
                return false;
            }
            if (command.Equals("images", StringComparison.OrdinalIgnoreCase) || command.StartsWith("images ", StringComparison.OrdinalIgnoreCase))
            {
                var action = command.Length > 6 ? command[7..].Trim().ToLowerInvariant() : "status";
                if (action == "status") Console.WriteLine($"Inline images: {propagation.Settings.ImageLoading.ToString().ToLowerInvariant()} (portable ASCII, not Kitty).");
                else if (action is "load" or "reload")
                {
                    if (propagation.Settings.ImageLoading == NomadNetImageLoading.Never)
                        throw new InvalidOperationException("Inline images are disabled; choose manual, auto or always first.");
                    await (_inlineImages ?? throw new InvalidOperationException("Browse a page first.")).LoadAsync(action == "reload", cancellationToken).ConfigureAwait(false);
                    Console.WriteLine("Inline image loading complete.");
                }
                else
                {
                    var mode = NomadNetImagePolicy.Parse(action);
                    await propagation.UpdateSettingsAsync(settings => settings with { ImageLoading = mode }, cancellationToken).ConfigureAwait(false);
                    if (_inlineImages is { } previousImages)
                    {
                        _inlineImages = null;
                        await previousImages.DisposeAsync().ConfigureAwait(false);
                    }
                    if (_page is { } currentPage && _currentNode is { } currentNode)
                        StartInlineImages(currentPage, currentNode);
                    await ShowConversationImagesAsync(_conversationMessages, false, false, cancellationToken).ConfigureAwait(false);
                    Console.WriteLine($"Inline images: {action}.");
                }
                return false;
            }
            if (command.StartsWith("media ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = Split(command, 3);
                var page = _page ?? throw new InvalidOperationException("Browse a page first.");
                if (parts.Length != 3) throw new ArgumentException("Use media <number> <name.webp>.");
                var media = page.Media[ParseIndex(parts[1], page.Media.Count) - 1];
                var target = NomadNetAddress.ParseUrl(media.Target, _currentNode, media: true);
                var bytes = await RequestAsync(Convert.FromHexString(target.DestinationHash), "/media",
                    new Dictionary<object, object?> { ["path"] = target.Path, ["key"] = null }, 16 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
                await SaveDownloadAsync(bytes, parts[2], cancellationToken).ConfigureAwait(false);
                return false;
            }
            if (command.StartsWith("show-media ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = Split(command, 3);
                if (parts.Length is < 2 or > 3 || parts.Length == 3 && parts[2] != "color")
                    throw new ArgumentException("Use show-media <number> [color].");
                var page = _page ?? throw new InvalidOperationException("Browse a page first.");
                var media = page.Media[ParseIndex(parts[1], page.Media.Count) - 1];
                var target = NomadNetAddress.ParseUrl(media.Target, _currentNode, media: true);
                var bytes = await RequestAsync(Convert.FromHexString(target.DestinationHash), "/media",
                    new Dictionary<object, object?> { ["path"] = target.Path, ["key"] = null }, NomadNetHost.MaximumFileBytes,
                    cancellationToken).ConfigureAwait(false);
                var dimensions = NomadNetImages.Dimensions(bytes);
                Console.WriteLine(await new NomadNetImages(propagation.Settings.MediaConverter, Path.Combine(dataDirectory, "storage", "image-work")).PreviewAsync(bytes,
                    color: parts.Length == 3, cancellationToken: cancellationToken).ConfigureAwait(false));
                Console.WriteLine($"Image preview complete: {dimensions.Width}x{dimensions.Height}");
                return false;
            }
            if (command.StartsWith("image-convert ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = Split(command, 3);
                if (parts.Length != 3) throw new ArgumentException("Use image-convert <file> <name.webp>.");
                var source = await NomadNetContent.ReadBoundedAsync(parts[1], NomadNetHost.MaximumFileBytes, cancellationToken).ConfigureAwait(false);
                var bytes = await new NomadNetImages(propagation.Settings.MediaConverter, Path.Combine(dataDirectory, "storage", "image-work")).ConvertAsync(source,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                await SaveDownloadAsync(bytes, parts[2], cancellationToken).ConfigureAwait(false);
                return false;
            }
            if (command.StartsWith("open ", StringComparison.OrdinalIgnoreCase))
            {
                var links = (_page ?? throw new InvalidOperationException("Browse a page first.")).Links;
                var number = ParseIndex(command["open ".Length..], links.Count);
                var link = links[number - 1];
                var target = link.Target;
                if (target.StartsWith('#'))
                {
                    Console.WriteLine(_browser!.AtAnchor(target[1..]));
                    return false;
                }
                if (target.StartsWith("p:", StringComparison.Ordinal))
                {
                    var page = _page ?? throw new InvalidOperationException("Browse a page first.");
                    foreach (var id in target[2..].Split(':'))
                    {
                        var index = page.Partials.ToList().FindIndex(item => item.Id == id);
                        if (index < 0) throw new ArgumentException("Unknown partial: " + id);
                        await _browser!.RefreshAsync(index + 1, cancellationToken).ConfigureAwait(false);
                    }
                    return false;
                }
                if (target.StartsWith("rrc://", StringComparison.Ordinal) || target.StartsWith("rrc@", StringComparison.Ordinal))
                {
                    var address = target[(target.StartsWith("rrc://", StringComparison.Ordinal) ? 6 : 4)..].Split('/', 2);
                    var hub = address[0].Split(':');
                    if (hub.Length > 1 && hub[1] != "rrc.hub") throw new ArgumentException("Only the primary rrc.hub destination is supported.");
                    await ExecuteRrcAsync("connect " + hub[0], cancellationToken).ConfigureAwait(false);
                    if (address.Length == 2 && address[1].TrimStart('#').Length > 0)
                        await _rrc!.JoinAsync(address[1].TrimStart('#'), cancellationToken: cancellationToken).ConfigureAwait(false);
                    return false;
                }
                var destination = NomadNetAddress.ParseUrl(target, _currentNode);
                await BrowseAsync(destination.DestinationHash, destination.Path, cancellationToken,
                    requestData: _browser?.Submission(link.Fields)).ConfigureAwait(false);
                return false;
            }
            if (command.Equals("reload", StringComparison.OrdinalIgnoreCase))
            {
                if (_currentNode is null) throw new InvalidOperationException("Browse a page first.");
                await BrowseAsync(_currentNode, _currentPath, cancellationToken, reload: true, requestData: _currentRequestData).ConfigureAwait(false);
                return false;
            }
            if (command.Equals("history", StringComparison.OrdinalIgnoreCase))
            {
                var entries = _navigation.Snapshot();
                for (var index = 0; index < entries.Count; index++)
                    Console.WriteLine($"History {index + 1}{(index + 1 == _navigation.Position ? "*" : "")}: {entries[index].DestinationHash}:{entries[index].Path}");
                Console.WriteLine($"History complete: {_navigation.Position}/{entries.Count}.");
                return false;
            }
            if (command.Equals("back", StringComparison.OrdinalIgnoreCase) || command.Equals("forward", StringComparison.OrdinalIgnoreCase))
            {
                var back = command.Equals("back", StringComparison.OrdinalIgnoreCase);
                var entry = back ? _navigation.Back() : _navigation.Forward();
                if (entry is null) Console.WriteLine(back ? "No back history." : "No forward history.");
                else await BrowseAsync(entry.DestinationHash, entry.Path, cancellationToken,
                    requestData: entry.RequestData, replay: true).ConfigureAwait(false);
                return false;
            }
            if (command.Equals("bookmark", StringComparison.OrdinalIgnoreCase) || command.StartsWith("bookmark ", StringComparison.OrdinalIgnoreCase))
            {
                var title = command.Length > "bookmark".Length ? command["bookmark".Length..].Trim() : $"{_currentNode}:{_currentPath}";
                if (_currentNode is null) throw new InvalidOperationException("Browse a page before bookmarking it.");
                await store.AddBookmarkAsync(new NomadNetBookmark(_currentNode, _currentPath, title), cancellationToken).ConfigureAwait(false);
                Console.WriteLine("Bookmark saved.");
                return false;
            }
            if (command.Equals("bookmarks", StringComparison.OrdinalIgnoreCase))
            {
                var bookmarks = await store.BookmarksAsync(cancellationToken).ConfigureAwait(false);
                for (var index = 0; index < bookmarks.Count; index++)
                    Console.WriteLine($"{index + 1}. {bookmarks[index].Title} ({bookmarks[index].DestinationHash}:{bookmarks[index].Path})");
                return false;
            }
            if (command.StartsWith("open-bookmark ", StringComparison.OrdinalIgnoreCase))
            {
                var bookmarks = await store.BookmarksAsync(cancellationToken).ConfigureAwait(false);
                var bookmark = bookmarks[ParseIndex(command["open-bookmark ".Length..], bookmarks.Count) - 1];
                await BrowseAsync(bookmark.DestinationHash, bookmark.Path, cancellationToken).ConfigureAwait(false);
                return false;
            }
            if (command.Equals("nodes", StringComparison.OrdinalIgnoreCase))
            {
                var entries = await store.KnownNodesAsync(cancellationToken).ConfigureAwait(false);
                for (var index = 0; index < entries.Count; index++)
                {
                    var entry = entries[index];
                    Console.WriteLine($"Node {index + 1}: {entry.DestinationHash} {NomadNetTerminalText.Safe(entry.Name)} " +
                        $"trust={entry.TrustLevel} rank={entry.SortRank?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"} identify={entry.IdentifyOnConnect}");
                }
                Console.WriteLine("Nodes complete.");
                return false;
            }
            if (command.StartsWith("open-node ", StringComparison.OrdinalIgnoreCase))
            {
                var entries = await store.KnownNodesAsync(cancellationToken).ConfigureAwait(false);
                var entry = entries[ParseIndex(command["open-node ".Length..], entries.Count) - 1];
                await BrowseAsync(entry.DestinationHash, NomadNetAddress.DefaultPagePath, cancellationToken).ConfigureAwait(false);
                return false;
            }
            if (command.StartsWith("fetch ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = Split(command, 4);
                if (parts.Length != 4) throw new ArgumentException("Usage: fetch <destination-hash> <remote-file-path> <output-name>");
                await FetchFileAsync(parts[1], parts[2], parts[3], cancellationToken).ConfigureAwait(false);
                return false;
            }
            throw new ArgumentException("Unknown command. Type help for available commands.");
        }

        private async Task ExecuteRrcAsync(string command, CancellationToken cancellationToken)
        {
            var rendering = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (rendering is ["render", "markdown" or "micron", "on" or "off"])
            {
                await propagation.UpdateSettingsAsync(current => rendering[1] == "markdown"
                    ? current with { RrcRenderMarkdown = rendering[2] == "on" }
                    : current with { RrcRenderMicron = rendering[2] == "on" }, cancellationToken).ConfigureAwait(false);
                Console.WriteLine("RRC rendering policy saved.");
                return;
            }
            var parts = Split(command, 3);
            if (parts.Length == 0) throw new ArgumentException("Use rrc connect <hub> [nick].");
            if (parts[0].Equals("connect", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length < 2) throw new ArgumentException("Use rrc connect <hub> [nick].");
                if (_rrc is not null) await _rrc.DisposeAsync().ConfigureAwait(false);
                _rrc = await RrcClient.OpenAsync(node, identity, dataDirectory, parts[1], cancellationToken).ConfigureAwait(false);
                _rrc.Log = message => Console.WriteLine(NomadNetTerminalText.Safe(message));
                _rrc.MessageReceived += message => Console.WriteLine($"RRC [{NomadNetTerminalText.Safe(message.Room ?? "hub")}] " +
                    $"{NomadNetTerminalText.Safe(message.Nick ?? message.Source)}: " + NomadNetPresentation.Rrc(message.Text,
                        propagation.Settings.RrcRenderMarkdown, propagation.Settings.RrcRenderMicron, message.Kind));
                if (parts.Length == 3) await _rrc.SetNickAsync(parts[2], cancellationToken).ConfigureAwait(false);
                await _rrc.ConnectAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var client = _rrc ?? throw new InvalidOperationException("Connect to an RRC hub first.");
                switch (parts[0].ToLowerInvariant())
                {
                    case "disconnect": await client.DisconnectAsync(cancellationToken).ConfigureAwait(false); break;
                    case "reconnect": await client.ConnectAsync(cancellationToken).ConfigureAwait(false); break;
                    case "status": Console.WriteLine(await client.StatusAsync(cancellationToken).ConfigureAwait(false)); break;
                    case "active" when parts.Length == 2:
                        await client.SetActiveRoomAsync(parts[1] == "off" ? null : parts[1], cancellationToken).ConfigureAwait(false); break;
                    case "read" when parts.Length == 2:
                        await client.MarkReadAsync(parts[1], cancellationToken).ConfigureAwait(false); break;
                    case "clear" when parts.Length == 2:
                        await client.ClearHistoryAsync(parts[1], cancellationToken).ConfigureAwait(false); break;
                    case "history-limit" when parts.Length == 2:
                        await client.SetHistoryPerRoomCapAsync(int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                            cancellationToken).ConfigureAwait(false); break;
                    case "auto-list" when parts.Length == 2 && parts[1] is "on" or "off":
                        await client.SetAutomaticDirectoryAsync(list: parts[1] == "on", cancellationToken: cancellationToken).ConfigureAwait(false); break;
                    case "auto-who" when parts.Length == 2 && parts[1] is "on" or "off":
                        await client.SetAutomaticDirectoryAsync(who: parts[1] == "on", cancellationToken: cancellationToken).ConfigureAwait(false); break;
                    case "nick" when parts.Length >= 2:
                        await client.SetNickAsync(command[5..], cancellationToken).ConfigureAwait(false); break;
                    case "auto-reconnect" when parts.Length == 2 && parts[1] is "on" or "off":
                        await client.SetAutoReconnectAsync(parts[1] == "on", cancellationToken).ConfigureAwait(false); break;
                    case "join" when parts.Length >= 2:
                        await client.JoinAsync(parts[1], parts.Length == 3 ? parts[2] : null, cancellationToken).ConfigureAwait(false); break;
                    case "part" when parts.Length == 2:
                        await client.PartAsync(parts[1], cancellationToken).ConfigureAwait(false); break;
                    case "send" or "action" when parts.Length == 3:
                        await client.SendMessageAsync(parts[1], parts[2], parts[0].Equals("action", StringComparison.OrdinalIgnoreCase), cancellationToken)
                            .ConfigureAwait(false); break;
                    case "send-json" when parts.Length == 3:
                        using (var json = System.Text.Json.JsonDocument.Parse(parts[2]))
                        {
                            if (json.RootElement.ValueKind != System.Text.Json.JsonValueKind.String)
                                throw new InvalidDataException("RRC JSON composition requires a string.");
                            await client.SendMessageAsync(parts[1], json.RootElement.GetString()!,
                                cancellationToken: cancellationToken).ConfigureAwait(false);
                        }
                        break;
                    case "list": await client.SendMessageAsync(null, "/list", cancellationToken: cancellationToken).ConfigureAwait(false); break;
                    case "rooms":
                        foreach (var (room, topic) in await client.RoomsAsync(cancellationToken).ConfigureAwait(false))
                            Console.WriteLine($"RRC room {NomadNetTerminalText.Safe(room)}: {NomadNetTerminalText.Safe(topic ?? "")}");
                        break;
                    case "members" when parts.Length == 2:
                        foreach (var member in await client.MembersAsync(parts[1], cancellationToken).ConfigureAwait(false))
                            Console.WriteLine($"RRC member {member.Hash}: {NomadNetTerminalText.Safe(member.Nick ?? "")}");
                        break;
                    case "who" when parts.Length == 2:
                        await client.SendMessageAsync(parts[1], "/who " + parts[1], cancellationToken: cancellationToken).ConfigureAwait(false); break;
                    case "ping": await client.PingAsync(cancellationToken).ConfigureAwait(false); break;
                    case "history":
                        foreach (var message in await client.HistoryAsync(parts.Length >= 2 ? parts[1] : null, cancellationToken).ConfigureAwait(false))
                            Console.WriteLine($"RRC history [{NomadNetTerminalText.Safe(message.Room ?? "hub")}] " +
                                $"{NomadNetTerminalText.Safe(message.Nick ?? message.Source)}: " + NomadNetPresentation.Rrc(message.Text,
                                    propagation.Settings.RrcRenderMarkdown, propagation.Settings.RrcRenderMicron, message.Kind));
                        break;
                    default: throw new ArgumentException("Unknown/incomplete RRC command; use help.");
                }
            }
            Console.WriteLine("RRC command complete.");
        }

        private void ExecuteFiles(string command)
        {
            var action = command.Length == 5 ? "" : command[6..].Trim();
            if (action.Length == 0 || action.StartsWith("browse ", StringComparison.OrdinalIgnoreCase))
            {
                if (_filePicker is not null) throw new InvalidOperationException("Complete or cancel the open file selection first.");
                _filePicker = new(action.Length == 0 ? null : action[7..].Trim(), _selectedAttachments)
                { Log = message => Console.Error.WriteLine(message) };
            }
            else if (action.Equals("selected", StringComparison.OrdinalIgnoreCase))
            {
                ShowSelectedFiles(_filePicker?.Selected ?? _selectedAttachments);
                return;
            }
            else if (action.Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                if (_filePicker is not null) throw new InvalidOperationException("Complete or cancel the open file selection first.");
                _selectedAttachments = [];
                Console.WriteLine("File selection cleared.");
                return;
            }
            else
            {
                var picker = _filePicker ?? throw new InvalidOperationException("Start a local selection with files browse <directory>.");
                if (action.Equals("done", StringComparison.OrdinalIgnoreCase))
                {
                    _selectedAttachments = picker.Done();
                    _filePicker = null;
                    Console.WriteLine($"File selection saved: {_selectedAttachments.Count}.");
                    return;
                }
                if (action.Equals("cancel", StringComparison.OrdinalIgnoreCase))
                {
                    picker.Cancel();
                    _filePicker = null;
                    Console.WriteLine($"File selection cancelled; saved: {_selectedAttachments.Count}.");
                    return;
                }
                if (action.Equals("up", StringComparison.OrdinalIgnoreCase))
                {
                    if (picker.Entries.FirstOrDefault(entry => entry.IsParent) is { } parent)
                        picker.Open(picker.Entries.ToList().IndexOf(parent));
                }
                else if (action.Equals("refresh", StringComparison.OrdinalIgnoreCase)) picker.Refresh();
                else if (action.StartsWith("open ", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(action[5..], out var number)) picker.Open(number - 1);
                else if (!action.Equals("list", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Use files list|up|refresh|selected|done|cancel|clear or files open <number>.");
            }
            if (_filePicker is not { } current) return;
            Console.WriteLine("Files directory: " + NomadNetTerminalText.Safe(current.CurrentDirectory));
            for (var index = 0; index < current.Entries.Count; index++)
            {
                var entry = current.Entries[index];
                Console.WriteLine($"File entry {index + 1}: {(entry.IsParent ? "parent" : entry.IsDirectory ? "directory" : "file")} {NomadNetTerminalText.Safe(entry.Name)}");
            }
            if (current.Notice is { } notice) Console.WriteLine("File selection notice: " + NomadNetTerminalText.Safe(notice));
            Console.WriteLine($"Files listed: {current.Entries.Count}; selected: {current.Selected.Count}.");
        }

        private static void ShowSelectedFiles(IReadOnlyList<string> files)
        {
            foreach (var file in files) Console.WriteLine("Selected file: " + NomadNetTerminalText.Safe(Path.GetFileName(file)));
            Console.WriteLine($"Selected files complete: {files.Count}.");
        }

        private async Task SendAsync(string destinationText, string content, CancellationToken cancellationToken,
            Dictionary<object, object?>? fields = null, LxmMethod? method = null)
        {
            var destination = Convert.FromHexString(NomadNetAddress.NormalizeHash(destinationText));
            var contact = await store.FindContactAsync(destinationText, cancellationToken).ConfigureAwait(false);
            method ??= contact?.PreferredDelivery switch { 2 => LxmMethod.Propagated, 3 => LxmMethod.Opportunistic, _ => LxmMethod.Direct };
            if (method == LxmMethod.Propagated)
            {
                if (router.OutboundPropagationNode is null) throw new InvalidOperationException("Select a propagation node first.");
                _ = await RecallAsync(destination, cancellationToken).ConfigureAwait(false);
            }
            fields = fields is null ? new() : new(fields);
            if (propagation.Settings.ComposeInMarkdown) fields[LxmField.Renderer] = NomadNetPresentation.MarkdownRenderer;
            var message = LxMessage.Create(destination, identity, content, "", fields, method.Value);
            var id = Convert.ToHexStringLower(message.ComputeHash());
            await NomadNetAttachments.SaveAsync(dataDirectory, id, fields, cancellationToken).ConfigureAwait(false);
            var stored = new NomadNetMessage(id, Convert.ToHexStringLower(destination), "outbound",
                DateTimeOffset.UtcNow, "", content, "queued")
                { Renderer = NomadNetPresentation.Renderer(fields), ImageAttachmentIndex = NomadNetAttachments.ImageIndex(fields) };
            await _delivery.QueueAsync(message, stored, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"Message queued: {message.State}");
            if (await store.ActiveConversationAsync(cancellationToken).ConfigureAwait(false) == stored.PeerHash)
                await ObserveConversationImageAsync(stored, cancellationToken).ConfigureAwait(false);
        }

        private async Task ShowMessagesAsync(string? peer, CancellationToken cancellationToken)
        {
            var messages = await store.MessagesAsync(peer, cancellationToken).ConfigureAwait(false);
            await ShowConversationImagesAsync(messages, false, false, cancellationToken).ConfigureAwait(false);
            var previews = _conversationImages?.Previews;
            foreach (var message in messages)
            {
                Console.WriteLine($"{message.Timestamp:u} {message.Direction} {message.PeerHash} [{message.Status}] {NomadNetTerminalText.Safe(message.Title)}");
                Console.WriteLine(NomadNetPresentation.Conversation(message.Content, message.Renderer));
                if (message.ImageAttachmentIndex is not null)
                {
                    Console.WriteLine($"Conversation image {NomadNetTerminalText.Safe(message.Id)}: stored attachment {message.ImageAttachmentIndex}; use conversation images load.");
                    if (propagation.Settings.ImageLoading != NomadNetImageLoading.Never && previews?.TryGetValue(message.Id, out var preview) == true)
                        Console.WriteLine(preview);
                }
            }
            if (messages.Count == 0) Console.WriteLine("No stored messages.");
            Console.WriteLine("Messages complete.");
        }

        public async Task ObserveConversationImageAsync(NomadNetMessage message, CancellationToken cancellationToken)
        {
            if (message.ImageAttachmentIndex is null) return;
            await ShowConversationImagesAsync(await store.MessagesAsync(message.PeerHash, cancellationToken).ConfigureAwait(false),
                false, false, cancellationToken).ConfigureAwait(false);
        }

        private Task ShowConversationImagesAsync(IReadOnlyList<NomadNetMessage> messages, bool explicitLoad,
            bool reload, CancellationToken cancellationToken)
        {
            NomadNetConversationImages images;
            lock (_conversationGate)
            {
                _conversationMessages = messages;
                if (_conversationImages is null)
                {
                    _conversationImages = new NomadNetConversationImages((message, token) =>
                    {
                        if (propagation.Settings.ImageLoading == NomadNetImageLoading.Never)
                            throw new InvalidOperationException("Conversation images are disabled.");
                        return NomadNetConversationImages.PreviewAsync(dataDirectory, message, _conversationConversions,
                            new NomadNetImages(propagation.Settings.MediaConverter,
                                Path.Combine(dataDirectory, "storage", "image-work")), token);
                    }) { Log = message => Console.Error.WriteLine(message) };
                    _conversationImages.Changed += (message, preview) =>
                        Console.WriteLine($"--- conversation image {NomadNetTerminalText.Safe(message.Id)} ---\n{preview}\n--- conversation image complete ---");
                }
                images = _conversationImages;
            }
            return images.ShowAsync(messages, propagation.Settings.ImageLoading, explicitLoad, reload, cancellationToken);
        }

        private async Task ShowPeersAsync(CancellationToken cancellationToken)
        {
            var peers = node.Router.Engine.Known.ExportSnapshot();
            if (peers.Count == 0) { Console.WriteLine("No announced destinations are known yet."); return; }
            foreach (var peer in peers.OrderByDescending(item => item.Entry.RememberedUnix).Take(256))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hash = Convert.ToHexStringLower(peer.DestinationHash);
                if (await store.IsIgnoredAsync(hash, cancellationToken).ConfigureAwait(false)) continue;
                var displayName = LxmfAppData.DisplayName(peer.Entry.AppData);
                Console.WriteLine($"{hash}  {NomadNetTerminalText.Safe(displayName ?? "(no display name)")} trust=" +
                    await store.TrustLevelAsync(hash, displayName, cancellationToken).ConfigureAwait(false));
            }
            if (peers.Count > 256) Console.WriteLine($"Showing 256 of {peers.Count} destinations.");
        }

        private async Task BrowseAsync(string hashText, string pathText, CancellationToken cancellationToken, bool reload = false, object? requestData = null, bool replay = false)
        {
            var hash = Convert.FromHexString(NomadNetAddress.NormalizeHash(hashText));
            var path = NomadNetAddress.NormalizePagePath(pathText);
            var url = Convert.ToHexStringLower(hash) + ":" + path;
            if (_browser is { } previous)
            {
                _browser = null;
                await previous.DisposeAsync().ConfigureAwait(false);
            }
            if (_inlineImages is { } previousImages)
            {
                _inlineImages = null;
                await previousImages.DisposeAsync().ConfigureAwait(false);
            }
            _pageMetrics = null;
            if (reload) _cache.Remove(url);
            var cached = requestData is null ? await _cache.ReadAsync(url, cancellationToken).ConfigureAwait(false) : null;
            var body = cached ?? await RequestAsync(hash, path, requestData, MicronText.MaximumPageBytes, cancellationToken,
                metrics => _pageMetrics = metrics).ConfigureAwait(false);
            if (cached is null && requestData is null) await _cache.StoreAsync(url, body, cancellationToken).ConfigureAwait(false);
            var page = MicronText.Render(body);
            _currentRequestData = requestData;
            _currentNode = Convert.ToHexStringLower(hash);
            _currentPath = path;
            if (!reload && !replay) _navigation.Visit(_currentNode, path, requestData);
            var origin = _currentNode;
            var browser = new NomadNetMicronBrowser(page, async (partial, data, token) =>
            {
                var target = NomadNetAddress.ParseUrl(partial.Target, origin);
                return await RequestAsync(Convert.FromHexString(target.DestinationHash), target.Path, data,
                    MicronText.MaximumPageBytes, token).ConfigureAwait(false);
            }) { Log = message => Console.Error.WriteLine(message) };
            _browser = browser;
            browser.Changed += updated =>
            {
                if (!ReferenceEquals(_browser, browser)) return;
                _inlineImages?.Show(updated, ShouldLoadInlineImages(origin));
                Console.WriteLine("--- partials updated ---");
                Console.WriteLine(_inlineImages?.Render(updated) ?? updated.DisplayText);
            };
            Console.WriteLine($"--- {_currentNode}:{_currentPath} ---");
            if (cached is not null) Console.WriteLine("(cached page)");
            Console.WriteLine(page.DisplayText);
            ShowLinks();
            StartInlineImages(page, origin);
            browser.Start();
        }

        private bool ShouldLoadInlineImages(string origin) => NomadNetImagePolicy.ShouldLoad(propagation.Settings.ImageLoading,
            host is not null && host.DestinationHash == origin, _pageMetrics);

        private void StartInlineImages(NomadNetRenderedPage page, string origin)
        {
            if (propagation.Settings.ImageLoading == NomadNetImageLoading.Never) return;
            var images = new NomadNetInlineMedia(async (image, reload, token) =>
            {
                var target = NomadNetAddress.ParseUrl(image.Target, origin, media: true);
                var url = target.DestinationHash + ":" + target.Path;
                var bytes = await _mediaCache.GetAsync(url, ct => RequestAsync(Convert.FromHexString(target.DestinationHash), "/media",
                    new Dictionary<object, object?> { ["path"] = target.Path, ["key"] = null }, NomadNetHost.MaximumFileBytes, ct),
                    reload, token).ConfigureAwait(false);
                return await new NomadNetImages(propagation.Settings.MediaConverter, Path.Combine(dataDirectory, "storage", "image-work"))
                    .PreviewAsync(bytes, cancellationToken: token).ConfigureAwait(false);
            }) { Log = message => Console.Error.WriteLine(message) };
            images.Changed += updated =>
            {
                if (!ReferenceEquals(_inlineImages, images)) return;
                Console.WriteLine("--- inline images updated ---");
                Console.WriteLine(images.Render(updated));
            };
            _inlineImages = images;
            images.Start();
            images.Show(page, ShouldLoadInlineImages(origin));
        }

        private async Task FetchFileAsync(string hashText, string remotePath, string outputName, CancellationToken cancellationToken)
        {
            var hash = Convert.FromHexString(NomadNetAddress.NormalizeHash(hashText));
            if (!remotePath.StartsWith("/file/", StringComparison.Ordinal) || remotePath.Length > 512 ||
                remotePath.Contains('\\') || remotePath.Split('/').Any(segment => segment is "." or ".."))
                throw new ArgumentException("Remote files must use a safe Nomad Network /file path.");
            var bytes = await RequestAsync(hash, remotePath, null, 16 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            await SaveDownloadAsync(bytes, outputName, cancellationToken).ConfigureAwait(false);
        }

        private async Task SaveDownloadAsync(byte[] bytes, string outputName, CancellationToken cancellationToken)
        {
            var name = Path.GetFileName(outputName);
            if (name.Length == 0 || name != outputName || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                name.Any(char.IsControl))
                throw new ArgumentException("Output name must be a plain file name.");
            var directory = Path.GetFullPath(propagation.Settings.DownloadsPath ?? Path.Combine(dataDirectory, "downloads"));
            Directory.CreateDirectory(directory);
            var output = Path.Combine(directory, name);
            var temporary = output + "." + Guid.NewGuid().ToString("N") + ".new";
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, output, overwrite: true);
                Console.WriteLine($"Saved {bytes.Length} bytes to {output}");
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private async Task<byte[]> RequestAsync(byte[] destinationHash, string path, object? data, long maximumBytes, CancellationToken cancellationToken,
            Action<NomadNetImageLinkMetrics>? observe = null)
        {
            var connected = await ClientLinks.ConnectAsync(node, destinationHash, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (!connected.Ok || connected.Link is null) throw new IOException(connected.Error ?? "Could not connect to the page node.");
            var link = connected.Link;
            try
            {
                var contact = await store.FindContactAsync(Convert.ToHexStringLower(destinationHash), cancellationToken).ConfigureAwait(false);
                if (NomadNetDirectoryPolicy.ShouldIdentify(contact)) link.Identify(identity);
                var requestedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                var receipt = link.Request(path, data, timeout: 60, maxResponseSize: maximumBytes,
                    cancellationToken: cancellationToken) ?? throw new IOException("The Reticulum request could not be started.");
                var response = await receipt.ResponseTask.WaitAsync(TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
                observe?.Invoke(new(link.Rtt, link.ExpectedRate, receipt.ResponseTransferSize,
                    System.Diagnostics.Stopwatch.GetElapsedTime(requestedAt).TotalSeconds));
                return response switch
                {
                    byte[] bytes when bytes.Length <= maximumBytes => bytes,
                    byte[] => throw new InvalidDataException("Remote response exceeds the configured bound."),
                    FileResponse file when file.Data.Length <= maximumBytes => file.Data,
                    FileResponse => throw new InvalidDataException("Remote file exceeds the configured bound."),
                    string text when Encoding.UTF8.GetByteCount(text) <= maximumBytes => Encoding.UTF8.GetBytes(text),
                    string => throw new InvalidDataException("Remote response exceeds the configured bound."),
                    null => throw new IOException("The remote node returned no response."),
                    _ => throw new InvalidDataException($"Unsupported page response type: {response.GetType().Name}."),
                };
            }

            finally { link.Teardown(); }
        }

        private Task SaveDirectoryContactAsync(NomadNetContact contact, CancellationToken cancellationToken)
        {
            var known = node.Router.Engine.Known.Recall(Convert.FromHexString(NomadNetAddress.NormalizeHash(contact.DestinationHash)));
            var associated = known is null ? null :
                Convert.ToHexStringLower(Destination.ComputeHashFromNameAndIdentity("nomadnetwork.node", known.Hash));
            return store.SaveContactAsync(contact, cancellationToken, associated);
        }

        private async Task<Identity> RecallAsync(byte[] hash, CancellationToken cancellationToken)
        {
            var known = node.Router.Engine.Known.Recall(hash);
            if (known is not null) return known;
            var connected = await ClientLinks.ConnectAsync(node, hash, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            connected.Link?.Teardown();
            return node.Router.Engine.Known.Recall(hash) ?? throw new IOException(connected.Error ?? "The destination identity is unknown.");
        }

        private void ShowLinks()
        {
            var links = _page?.Links ?? [];
            for (var index = 0; index < links.Count; index++)
                Console.WriteLine($"  {index + 1}. {links[index].Label} -> {links[index].Target}");
            if (links.Count > 0) Console.WriteLine("Use open <number> to follow a link.");
        }

        private static string[] Split(string text, int maximumParts)
        {
            var parts = new List<string>(maximumParts);
            var index = 0;
            while (parts.Count < maximumParts - 1)
            {
                while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
                if (index == text.Length) break;
                var start = index;
                while (index < text.Length && !char.IsWhiteSpace(text[index])) index++;
                parts.Add(text[start..index]);
            }
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            if (index < text.Length) parts.Add(text[index..]);
            return [.. parts];
        }

        private static int ParseIndex(string text, int count)
        {
            if (!int.TryParse(text.Trim(), out var index) || index < 1 || index > count)
                throw new ArgumentOutOfRangeException(nameof(text), "Select one of the displayed items.");
            return index;
        }
    }
}
