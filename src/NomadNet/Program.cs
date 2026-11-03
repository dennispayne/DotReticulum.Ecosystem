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
        return new Options(dataDirectory, rnsConfig, initialize, showStatus, daemon);
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
        router.Start();
        if (settings.PropagationNode is not null) router.OutboundPropagationNode = Convert.FromHexString(settings.PropagationNode);
        var propagation = settings.EnablePropagation ? router.EnablePropagationNode() : null;
        await using var host = settings.EnableNode ? new NomadNetHost(node.Router, identity, options.DataDirectory, settings)
        {
            Log = message => Console.Error.WriteLine(message),
        } : null;
        host?.Start();
        await router.AnnounceAsync(cancellationToken).ConfigureAwait(false);
        if (propagation is not null) await propagation.AnnounceAsync(cancellationToken).ConfigureAwait(false);
        var receiver = PersistIncomingAsync(inbox.Reader, state, options.DataDirectory, cancellationToken);
        var session = new TerminalSession(node, identity, router, state, options.DataDirectory, settings, host);

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

    private static async Task PersistIncomingAsync(ChannelReader<ReceivedMessage> reader, NomadNetStore store, string directory, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var received in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var message = received.Message;
                var content = message.Content ?? "[non-text LXMF content]";
                var id = message.Hash is { Length: > 0 } hash ? Convert.ToHexStringLower(hash) : Guid.NewGuid().ToString("N");
                try
                {
                    try
                    {
                        await NomadNetAttachments.SaveAsync(directory, id, message.Fields, cancellationToken).ConfigureAwait(false);
                        var attachments = NomadNetAttachments.Extract(message.Fields);
                        if (attachments.Count > 0) content += "\nAttachments: " + string.Join(", ", attachments.Select(attachment => attachment.Name));
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        Console.Error.WriteLine($"Could not save incoming attachments for {id}: {exception.Message}");
                        content += "\n[attachments could not be saved]";
                    }
                    await store.SaveMessageAsync(new NomadNetMessage(id, Convert.ToHexStringLower(message.SourceHash), "inbound",
                        DateTimeOffset.FromUnixTimeMilliseconds((long)(message.Timestamp * 1000)), message.Title, content, "received"),
                        cancellationToken).ConfigureAwait(false);
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
                   nomadnet [--console] [--textui]
                   nomadnet --help | --version

            Interactive commands:
              status                         Show identity and Reticulum connectivity
              peers                          List announced Reticulum destinations
              contact add <hash> <name>      Save a known LXMF destination
              contacts | contact remove <hash>
              send <hash> <message>          Send a text LXMF message
              send-propagated <hash> <text>  Send through selected propagation node
              attach <hash> <file>           Send a file attachment
              image|audio <hash> <file>      Send encoded image/audio (no transcoding)
              paper <hash> <message>         Generate an encrypted lxm:// URI
              ingest <lxm://uri>             Ingest a paper URI for this identity
              propagation <hash|off>         Select and persist propagation node
              sync                           Retrieve messages from selected node
              messages [hash]                 Show durable conversation history
              browse <hash> [page-path]       Fetch a bounded Nomad Network page
              links | open <number>            Follow a displayed Micron link
              reload                          Fetch current page without cache
              bookmark [title] | bookmarks | open-bookmark <number>
              fetch <hash> <file-path> <name> Download a bounded /file response
              node refresh | node announce    Refresh or announce hosted content
              help | quit

            The app stores its identity, contacts, conversations, and bookmarks below --config.
            """);
    }

    private sealed record Options(string DataDirectory, string? RnsConfig, bool Initialize, bool ShowStatus, bool Daemon);

    private sealed class TerminalSession(ReticulumNode node, Identity identity, LxmRouter router, NomadNetStore store,
        string dataDirectory, NomadNetSettings settings, NomadNetHost? host)
    {
        private NomadNetSettings _settings = settings;
        private readonly NomadNetPageCache _cache = new(dataDirectory);
        private readonly List<NomadNetPageLink> _lastLinks = [];
        private string? _currentNode;
        private string _currentPath = NomadNetAddress.DefaultPagePath;

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
            if (command.Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"LXMF delivery address: {router.DeliveryHashHex}");
                Console.WriteLine($"Reticulum interfaces: {node.Interfaces.Count}; known destinations: {node.Router.Engine.Known.Count}");
                Console.WriteLine(node.IsConnectedToSharedInstance ? "Connected to shared Reticulum instance." : "Using local Reticulum instance.");
                return false;
            }
            if (command.Equals("peers", StringComparison.OrdinalIgnoreCase)) { await ShowPeersAsync(cancellationToken).ConfigureAwait(false); return false; }
            if (command.Equals("node refresh", StringComparison.OrdinalIgnoreCase))
            {
                (host ?? throw new InvalidOperationException("Enable node hosting in settings.json.")).Refresh();
                Console.WriteLine("Hosted content refreshed.");
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
                var hash = value.Equals("off", StringComparison.OrdinalIgnoreCase) ? null : NomadNetAddress.NormalizeHash(value);
                var changed = _settings with { PropagationNode = hash };
                await changed.SaveAsync(dataDirectory, cancellationToken).ConfigureAwait(false);
                router.OutboundPropagationNode = hash is null ? null : Convert.FromHexString(hash);
                _settings = changed;
                Console.WriteLine("Propagation selection saved.");
                return false;
            }
            if (command.Equals("sync", StringComparison.OrdinalIgnoreCase))
            {
                var count = await router.RequestMessagesFromPropagationNodeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"Propagation sync: {count} messages ({router.PropagationTransferState}).");
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
                await using var input = File.OpenRead(parts[2]);
                if (input.Length > NomadNetAttachments.MaximumBytes) throw new InvalidDataException("Attachment exceeds 16 MiB.");
                var bytes = new byte[checked((int)input.Length)];
                await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
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
                    Console.WriteLine($"{contact.DestinationHash}  {NomadNetTerminalText.Safe(contact.Name)}");
                return false;
            }
            if (command.StartsWith("contact add ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = Split(command["contact add ".Length..], 2);
                if (parts.Length != 2) throw new ArgumentException("Usage: contact add <32-hex-hash> <name>");
                await store.AddContactAsync(parts[0], parts[1], cancellationToken).ConfigureAwait(false);
                Console.WriteLine("Contact saved.");
                return false;
            }
            if (command.StartsWith("contact remove ", StringComparison.OrdinalIgnoreCase))
            {
                var hash = command["contact remove ".Length..].Trim();
                Console.WriteLine(await store.RemoveContactAsync(hash, cancellationToken).ConfigureAwait(false) ? "Contact removed." : "Contact not found.");
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
            if (command.StartsWith("open ", StringComparison.OrdinalIgnoreCase))
            {
                var number = ParseIndex(command["open ".Length..], _lastLinks.Count);
                var target = _lastLinks[number - 1].Target;
                var destination = NomadNetAddress.ParseUrl(target, _currentNode);
                await BrowseAsync(destination.DestinationHash, destination.Path, cancellationToken).ConfigureAwait(false);
                return false;
            }
            if (command.Equals("reload", StringComparison.OrdinalIgnoreCase))
            {
                if (_currentNode is null) throw new InvalidOperationException("Browse a page first.");
                await BrowseAsync(_currentNode, _currentPath, cancellationToken, reload: true).ConfigureAwait(false);
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
            if (command.StartsWith("fetch ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = Split(command, 4);
                if (parts.Length != 4) throw new ArgumentException("Usage: fetch <destination-hash> <remote-file-path> <output-name>");
                await FetchFileAsync(parts[1], parts[2], parts[3], cancellationToken).ConfigureAwait(false);
                return false;
            }
            throw new ArgumentException("Unknown command. Type help for available commands.");
        }

        private async Task SendAsync(string destinationText, string content, CancellationToken cancellationToken,
            Dictionary<object, object?>? fields = null, LxmMethod method = LxmMethod.Direct)
        {
            var destination = Convert.FromHexString(NomadNetAddress.NormalizeHash(destinationText));
            var message = LxMessage.Create(destination, identity, content, "", fields, method);
            var id = Guid.NewGuid().ToString("N");
            await store.SaveMessageAsync(new NomadNetMessage(id, Convert.ToHexStringLower(destination), "outbound",
                DateTimeOffset.UtcNow, "", content, "queued"), cancellationToken).ConfigureAwait(false);
            message.StateChanged += changed =>
            {
                _ = UpdateDeliveryStateAsync(id, changed.State);
            };
            await router.SendAsync(message, cancellationToken).ConfigureAwait(false);
            await UpdateDeliveryStateAsync(id, message.State).ConfigureAwait(false);
            Console.WriteLine($"Message queued: {message.State}");
        }

        private async Task UpdateDeliveryStateAsync(string id, LxmState state)
        {
            try
            {
                await store.UpdateMessageStatusAsync(id, state.ToString().ToLowerInvariant()).ConfigureAwait(false);
            }
            catch (Exception exception) { Console.Error.WriteLine($"Could not update delivery status: {exception.Message}"); }
        }

        private async Task ShowMessagesAsync(string? peer, CancellationToken cancellationToken)
        {
            var messages = await store.MessagesAsync(peer, cancellationToken).ConfigureAwait(false);
            foreach (var message in messages)
            {
                Console.WriteLine($"{message.Timestamp:u} {message.Direction} {message.PeerHash} [{message.Status}] {NomadNetTerminalText.Safe(message.Title)}");
                Console.WriteLine(NomadNetTerminalText.Safe(message.Content));
            }
            if (messages.Count == 0) Console.WriteLine("No stored messages.");
        }

        private Task ShowPeersAsync(CancellationToken cancellationToken)
        {
            var peers = node.Router.Engine.Known.ExportSnapshot();
            if (peers.Count == 0) { Console.WriteLine("No announced destinations are known yet."); return Task.CompletedTask; }
            foreach (var peer in peers.OrderByDescending(item => item.Entry.RememberedUnix).Take(256))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hash = Convert.ToHexStringLower(peer.DestinationHash);
                var displayName = LxmfAppData.DisplayName(peer.Entry.AppData);
                Console.WriteLine($"{hash}  {NomadNetTerminalText.Safe(displayName ?? "(no display name)")}");
            }
            if (peers.Count > 256) Console.WriteLine($"Showing 256 of {peers.Count} destinations.");
            return Task.CompletedTask;
        }

        private async Task BrowseAsync(string hashText, string pathText, CancellationToken cancellationToken, bool reload = false)
        {
            var hash = Convert.FromHexString(NomadNetAddress.NormalizeHash(hashText));
            var path = NomadNetAddress.NormalizePagePath(pathText);
            var url = Convert.ToHexStringLower(hash) + ":" + path;
            if (reload) _cache.Remove(url);
            var cached = await _cache.ReadAsync(url, cancellationToken).ConfigureAwait(false);
            var body = cached ?? await RequestAsync(hash, path, null, MicronText.MaximumPageBytes, cancellationToken).ConfigureAwait(false);
            if (cached is null) await _cache.StoreAsync(url, body, cancellationToken).ConfigureAwait(false);
            var page = MicronText.Render(body);
            _currentNode = Convert.ToHexStringLower(hash);
            _currentPath = path;
            _lastLinks.Clear();
            _lastLinks.AddRange(page.Links);
            Console.WriteLine($"--- {_currentNode}:{_currentPath} ---");
            if (cached is not null) Console.WriteLine("(cached page)");
            Console.WriteLine(page.Text);
            ShowLinks();
        }

        private async Task FetchFileAsync(string hashText, string remotePath, string outputName, CancellationToken cancellationToken)
        {
            var hash = Convert.FromHexString(NomadNetAddress.NormalizeHash(hashText));
            if (!remotePath.StartsWith("/file/", StringComparison.Ordinal) || remotePath.Length > 512 ||
                remotePath.Contains('\\') || remotePath.Split('/').Any(segment => segment is "." or ".."))
                throw new ArgumentException("Remote files must use a safe Nomad Network /file path.");
            var name = Path.GetFileName(outputName);
            if (name.Length == 0 || name != outputName || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                name.Any(char.IsControl))
                throw new ArgumentException("Output name must be a plain file name.");
            var bytes = await RequestAsync(hash, remotePath, null, 16 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            var directory = Path.Combine(dataDirectory, "downloads");
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

        private async Task<byte[]> RequestAsync(byte[] destinationHash, string path, object? data, long maximumBytes, CancellationToken cancellationToken)
        {
            var connected = await ClientLinks.ConnectAsync(node, destinationHash, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (!connected.Ok || connected.Link is null) throw new IOException(connected.Error ?? "Could not connect to the page node.");
            var link = connected.Link;
            try
            {
                link.Identify(identity);
                var receipt = link.Request(path, data, timeout: 60, maxResponseSize: maximumBytes,
                    cancellationToken: cancellationToken) ?? throw new IOException("The Reticulum request could not be started.");
                var response = await receipt.ResponseTask.WaitAsync(TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
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
            for (var index = 0; index < _lastLinks.Count; index++)
                Console.WriteLine($"  {index + 1}. {_lastLinks[index].Label} -> {_lastLinks[index].Target}");
            if (_lastLinks.Count > 0) Console.WriteLine("Use open <number> to follow a link.");
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
