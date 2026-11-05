using System.Text;

namespace NomadNet;

/// <summary>Original local documentation, not the primary application's guide or assets.</summary>
public sealed class NomadNetGuide
{
    public static IReadOnlyDictionary<string, string> Topics { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["start"] = """
            >Local application guide
            This experimental .NET application is unofficial and has incomplete compatibility.
            These instructions and samples are original; they are not the upstream NomadNet guide.
            Use help to list terminal commands. Use guide topics to list local chapters.
            Commands below are typed at nomadnet>; angle-bracket arguments are placeholders, not literal values.
            guide links lists numbered local links. guide open 1 follows one. guide back/forward restore visits.
            guide view 12 shows a bounded viewport; guide scroll 6 moves six logical lines.
            guide next moves to the next heading. guide find <words> searches this original guide.
            Navigation only reads local built-in text: no command, external program or network request is executed.
            `[Create a page`guide:authoring]
            `[First session checklist`guide:first-run]
            `[Addresses, identity and trust`guide:concepts]
            `[Messages and files`guide:messages]
            `[Hosting and access`guide:hosting]
            `[Configuration and restart`guide:configuration]
            `[Micron samples`guide:micron]
            `[Channels and network tools`guide:network]
            `[Safety and limitations`guide:safety]
            `[All topics and controls`guide:keyboard]
            """,
        ["authoring"] = """
            >Authoring local documents
            edit mkdir pages
            edit new pages welcome.mu
            edit text ">My local notice\nA synthetic example.\n"
            edit preview
            edit save
            Text arguments are JSON strings: escape newlines as \n and quotes as \".
            edit insert <UTF-16-offset> "<text>" inserts text without replacing the buffer.
            edit find "<term>" wraps around. edit replace ["term","replacement"] replaces the last match;
            if it is not selected it finds the next one. edit replace-all ["term","replacement"] changes all.
            edit snippets lists original samples. edit snippet heading inserts at the cursor.
            edit close save, edit close discard and edit close cancel make the unsaved decision explicit.
            Opening another document or quitting with unsaved edits is refused.
            edit resume reopens the last selected saved path after restart, not an unsaved draft.
            edit rename files note.txt moves a saved document and its access list without overwriting.
            edit delete confirm removes the saved document and its access list. No folder recursion.
            `[Access restrictions`guide:hosting#access]
            """,
        ["messages"] = """
            >Messages and attachments
            status shows your delivery address. Save another peer with contact add <address> <name>.
            send <address> <text> sends direct text. send-propagated uses the selected propagation node.
            conversations and messages show persisted local history; conversation active <address> marks it read.
            attach <address> <path> sends one file. files browse <directory> starts the bounded picker.
            files done commits picker selection; send-files <address> <text> sends it.
            attachment <message-id> <index> exports without overwriting.
            paper <address> <text> creates an encrypted URI; ingest <uri> accepts one for this identity.
            Explicitly verify addresses and trust. Imported historical assertions are not authentication.
            contact delivery <address> opportunistic selects small packet delivery; direct and propagated are separate.
            conversation read <address> marks local history read; deleting history is not remote recall.
            conversation export <address> <name.txt> writes a plain-text copy; review it before sharing.
            An encoded image/audio attachment is not a native microphone, audio player or transcoding service.
            Paper QR save/display is software output. Physical scanning and printing need separate equipment.
            `[Start`guide:start]
            """,
        ["hosting"] = """
            >Hosting documents
            edit list pages and edit list files show immediate children; add a relative folder to browse it.
            edit mkdir pages chapter creates a folder. edit open pages chapter/notice.mu opens a document.
            edit folder inspect pages chapter snapshots its bounded tree and prints a one-use token.
            edit folder move TOKEN files archive moves that inspected folder without overwriting.
            edit folder rename TOKEN pages renamed is the same operation with an explicit destination.
            Create the destination parent first; an affected open document must be clean.
            edit folder remove TOKEN confirm recursively removes exactly the inspected tree.
            edit folder cancel invalidates the plan without changing files. Inspect again after any conflict.
            Roots, hidden entries, links, special files and folder-root .allowed sidecars are refused.
            File permissions inside a moved folder are retained.
            edit folder permissions inspect pages chapter prints public/restricted counts and a one-use token.
            edit folder permissions apply TOKEN confirm ["0123456789abcdef0123456789abcdef"]
            replaces each targeted file's access list, including descendants. Review all recipients first.
            edit folder permissions public TOKEN confirm removes those restrictions after explicit confirmation.
            Even expanding a nonempty access list requires confirm. Cancel with edit folder cancel.
            Inspection includes all entries in safety limits; __pycache__ content and orphan policies stay unchanged.
            Affected open buffers must be clean. Ordinary conflicts refuse; cancellation/failure attempts rollback.
            Retained transaction/cleanup errors need manual reconciliation; this is not crash-atomic.
            Native editor paths are relative to the selected configured pages or files root.
            node paths shows registered handlers; node refresh rescans after creating or renaming files.
            Enable hosting in settings.json and restart before expecting remote access.
            `:access
            >>Access lists
            edit permissions ["0123456789abcdef0123456789abcdef"] restricts the current saved document.
            Entries are identity hashes, not delivery addresses. Obtain them through a trusted channel.
            edit permissions public explicitly removes the restriction.
            Save-as refuses restricted documents rather than accidentally creating a public copy.
            Renaming keeps the companion .allowed file. Hidden files are not advertised.
            Editor preview never executes a page or an executable access-list generator.
            Static page and file hosting work independently of executable pages.
            Executable pages require explicit opt-in and a supported Linux process environment.
            Windows does not execute hosted pages; this reader does not invent an interpreter or bypass permissions.
            Hosting defaults and authorization are startup settings; do not assume an editor save enables a service.
            `[Safety`guide:safety]
            """,
        ["configuration"] = """
            >Configuration
            config edit native opens settings.json; config edit rns opens the actual selected RNS file.
            config show native and config show rns display saved text, not effective running service options.
            Use edit text "<JSON document>" or edit replace ["old","new"], then edit save.
            All native settings are validated before replacement; duplicate and unknown keys are refused.
            config validate checks the buffer without saving. Invalid saves leave the previous file intact.
            Saved configuration takes effect after a full application restart, not by mutating active services.
            Later runtime setting edits are refused until restart; background timestamps remain memory-only,
            so active services cannot overwrite the saved restart configuration.
            Native JSON remains the default. config sections lists all eight primary form sections.
            config primary enable confirm explicitly selects the owned profile/config ConfigObj source.
            If absent, that file is exported from current native settings; an existing source is not overwritten.
            Export refuses fractional page/file refresh minutes or an embedded custom print template.
            Reconcile those native settings explicitly; no rounding or foreign template file is invented.
            config section client opens its form; config fields shows typed values and dispositions.
            config set announce_interval 120 stages a whole number; config set notify_on_new_message false
            stages a checkbox. Text uses JSON strings and list fields use JSON string arrays.
            config reset confirm stages primary form defaults only; config cancel discards changes.
            config save confirm validates and saves; restart the application to apply supported runtime fields.
            config fields reports runtime, restart, primary-placeholder or safety dispositions for each field.
            The panic-on-interface-error toggle is deliberately refused; failure isolation remains enabled.
            config section reticulum edits the actual selected RNS file, not the profile or native JSON.
            config primary disable confirm freezes effective supported values into native JSON and retains text.
            Close the configuration buffer before switching sources. Active primary source forbids competing
            runtime settings writes; use section editing and restart. Raw config edit primary remains available.
            ConfigObj quotes, lists, nesting, multiline text, interpolation and comments have bounded support.
            Unknown text is retained, not silently executed. This is not complete primary UI or RNS grammar parity.
            Offline --import-config remains available for supported primary keys without activating a source.
            Set editor to one installed executable (default nano), without shell arguments, and restart.
            config external native or config external rns launches it on a working copy in a real terminal.
            Closing with a successful exit validates and commits changes; Ctrl+] cancels and discards the copy.
            Redirected input/output is unsupported for external editing. Built-in editing still accepts pipes.
            The RNS parser validates without reformatting comments. Shared-instance owners need their own restart.
            `[Authoring`guide:authoring]
            """,
        ["micron"] = """
            >Original Micron exercises
            A line starting with > becomes a heading. A hyphen-only line is a divider.
            Formatting: `!important`!, `*emphasis`*, and `_underlined`_ text.
            A link uses \`[label\`target] with the leading backtick. Native node URLs use <node>:/page/<name.mu>.
            Use a relative :/page/ path when browsing the same node. Guide links instead use guide:<topic>.
            An explicit anchor line starts with \`:name; a link to #name jumps within that document.
            Heading slugs are anchors too. Use anchor <name> in the browser or guide <topic>#<name> here.
            edit snippet literal inserts a literal block bounded by lines containing \`=.
            A comment line begins with #. A table may remain readable text, not primary column layout.
            Native text/password/multiline and checkbox/radio forms are edited with fields and field commands.
            Form values are submitted only by an explicit submit link; review targets and identity consent.
            Variables and selected fields can affect requests; do not put credentials in a public page.
            Partials and remote images are browser features, not executed by local editor/guide previews.
            Copy this original source into a saved local .mu document to try heading and anchor navigation:
            `=
            >Example notice
            An original local paragraph.
            `[Read details`#details]
            `:details
            >>Details
            Add your own information here.
            `=
            `[Return to authoring`guide:authoring]
            `+>Expandable example
            Local preview uses the existing textual Micron renderer; it does not fetch links, partials or images.
            <
            Use edit snippet link for a synthetic local-page link, or edit snippet field for a text field.
            The native renderer is intentionally not pixel-identical to Urwid and does not provide Kitty graphics.
            """,
        ["network"] = """
            >Channels and tools
            rrc connect <hub-address> <nick> opens an identified channel session.
            rrc join <room>, rrc send <room> <text>, rrc who <room> and rrc part <room> manage rooms.
            rrc history shows retained history; rrc disconnect closes the connection.
            rrc list requests available rooms; rrc rooms shows the learned directory.
            rrc members <room> shows learned membership; rrc active <room> chooses local read tracking.
            rrc action <room> <text> sends an action; rrc nick <name> requests a nickname change.
            rrc read <room> clears local unread state. This does not erase another participant's history.
            rrc auto-reconnect on is opt-in recovery; rrc reconnect is an explicit manual attempt.
            A hub can reject a join or nickname. Do not retry endlessly or mistake a nickname for identity proof.
            Room keys, mentions and roster data do not make group traffic equivalent to private LXMF delivery.
            utility help describes explicit local/network utilities and authorization requirements.
            sync status shows propagation retrieval; sync cancel cancels owned retrieval.
            A local guide or editor test is not evidence of live messaging or transport interoperability.
            `[Start`guide:start]
            """,
        ["first-run"] = """
            >First session checklist
            Start with nomadnet --init --config <new-directory>. This creates native settings without networking.
            Keep the profile and selected RNS directory separate. Back up both before changing identity settings.
            A fresh native identity is selected-RNS/storage/nomadnet_identity; an imported primary uses profile/identity.
            Retain the same selected RNS directory on restart. Never publish either private identity file.
            Start nomadnet --config <profile> --rnsconfig <RNS-directory>. status shows the local addresses.
            Use config sections to inspect supported settings; save changes explicitly and restart to apply them.
            A blank offline RNS interface list is useful for local authoring; it cannot reach remote peers.
            Never paste private identity material into a chat, a page, a terminal transcript or a bug report.
            A local saved display name is not proof of who controls an address. Verify addresses independently.
            This guide appears when client state is first created. After restart use guide to open it again.
            hide_guide changes only menu visibility. It does not remove these explicit guide commands.
            `[Connect interfaces`guide:interfaces]
            `[Send and receive`guide:messages]
            `[When something fails`guide:troubleshooting]
            """,
        ["concepts"] = """
            >Addresses and trust
            Reticulum transports packets and links. LXMF carries messages. A Nomad node serves pages and files.
            One identity can own several destinations; a delivery address is not a node address or identity hash.
            Native address arguments are 32 hexadecimal characters. Copy the correct kind from status or a peer.
            Announcements help discover destinations; their reported names do not establish personal trust.
            Save a contact with contact add <delivery-address> <name>, then review it with contacts.
            contact trust <address> trusted records your decision; it does not verify a person for you.
            contact ignore <address> on enables the local incoming blacklist; off removes that policy.
            Node identification is explicit consent: contact identify <node-address> on permits that disclosure.
            A propagation node stores encrypted messages for later retrieval. It is not the recipient's identity.
            A channel hub carries a live group conversation; it is not an LXMF propagation node.
            `[Deferred messages`guide:propagation]
            `[Browse a node`guide:browser]
            `[Channels`guide:network]
            """,
        ["interfaces"] = """
            >Connect Reticulum interfaces
            config show rns displays the selected RNS configuration, not a second application interface file.
            config edit rns opens a validated local working buffer. Save, close and restart the owning service.
            To connect to a cooperating TCP server, add a named nested entry under [interfaces]:
            [[local-peer]]
            type = TCPClientInterface
            enabled = yes
            target_host = 127.0.0.1
            target_port = 4242
            Replace the host and port with values agreed with the operator; no public bootstrap is assumed.
            Do not overwrite an existing interface section. Check the owner configuration before changing it.
            status reports native interfaces and connectivity; peers and announces report learned destinations.
            Local automatic discovery depends on the network and platform. RF/serial devices need real setup.
            RNode, serial, KISS, I2P and multi-interface hardware are not configured or tested by reading this guide.
            Interface access codes and modes are Reticulum controls, not contact names or authentication prompts.
            Use the exact stack's documentation for supported interface keys, modes and credentials.
            `[Independent network choices`guide:network-configuration]
            `[Troubleshooting`guide:troubleshooting]
            """,
        ["network-configuration"] = """
            >Network configuration decisions
            An interface connects this Reticulum instance to another medium; a transport node routes for others.
            Neither hosting a Nomad page nor enabling an LXMF store requires enabling Reticulum transport.
            config section reticulum edits supported flags in the actual selected RNS file.
            Enable transport only when you intend to route and understand the capacity and trust implications.
            A shared-instance client depends on its owner. Restart that owner to activate its RNS changes.
            Keep test profiles separate from normal identities. Inspect the selected file with config show rns.
            First prove local connectivity, then discovery, then a short message, before large attachments.
            status, peers, announces and utility help expose different local/network diagnostics.
            Utilities are explicit operations, not automatic guide actions; remote execution needs authorization.
            IFAC credentials must be shared privately with the intended network. Never publish them in pages.
            This local guide does not establish reachability, RF legality, physical range or network endurance.
            `[Interfaces`guide:interfaces]
            `[Propagation`guide:propagation]
            """,
        ["propagation"] = """
            >Deferred delivery and retrieval
            Ask the operator for an LXMF propagation-node address; do not substitute its Nomad page address.
            propagation <address> saves a manual choice. propagation auto uses eligible trusted discovery.
            propagation off disables selection. trust-node <node-address> on is a separate explicit trust action.
            send-propagated <delivery-address> <text> queues encrypted delivery through the selected node.
            sync 10 requests at most ten messages. sync status reports progress; sync wait waits for completion.
            sync cancel requests cancellation of this client's retrieval, not recall of already delivered messages.
            sync schedule on 60 10 enables periodic limited retrieval; sync schedule off disables scheduling.
            fallback on allows an eligible failed direct message to retry through the chosen propagation node.
            fallback off stops that policy. Cancellation and preparation/rejection failures are not fallback proof.
            Hosting a propagation store is a separate node configuration and authorization/storage responsibility.
            Restart after saved node-policy changes; node paths inspects page/file handlers, not propagation peers.
            A local guide exercise proves none of the remote transfers; check actual message status and receipts.
            `[Messages`guide:messages]
            `[Hosting`guide:hosting]
            """,
        ["browser"] = """
            >Read pages and download files
            browse <node-address> /page/index.mu retrieves a page; nodes and open-node <number> use saved nodes.
            page displays the current composed text. links lists numbered links; open <number> follows one.
            These browser links are network actions, unlike guide open. Review their targets before following.
            history, back and forward belong to the browser; guide back and guide forward belong to this guide.
            fields lists form controls. field <number> <value> edits a text field or toggles a choice with on/off.
            anchor <name> and fold <number> navigate the current page; reload explicitly bypasses page cache.
            partials lists update targets; partial <number> explicitly requests one with current form values.
            fetch <node-address> /file/<relative-path> <local-name> saves a bounded file into downloads.
            images manual chooses explicit ASCII previews; images load starts owned image loading.
            Navigation disposes owned browser image work. No copied primary image or Kitty graphics is included.
            Some pages require identifying this client. Consent and identity restrictions are separate from trust.
            A failed fetch does not authorize executing scripts locally or relaxing a node's access policy.
            `[Write a page`guide:authoring]
            `[Micron`guide:micron]
            """,
        ["keyboard"] = """
            >Original console controls
            Type one command and press Enter. help lists the complete native command grammar.
            Guide topics use stable IDs: guide topics, guide messages, guide hosting#access.
            guide select <number> opens a topic from the numbered list, not a page or remote node.
            guide links and guide open <number> follow the current topic's original local links.
            guide back and guide forward restore bounded session positions, including anchors and scrolling.
            guide next moves to the next local heading. guide view [1-100] sets the viewport height.
            guide scroll <-10000..10000> moves logical lines; it does not wrap at a terminal-specific width.
            guide find <words> reports at most 64 matching local lines; it never searches private profiles.
            Ctrl+C requests application shutdown and disposal. Local guide work has no worker left to cancel.
            Menu selection and terminal mouse handling are separate original controls, not Urwid pane focus.
            Upstream reader Enter/Space/left-click and two-column focus are not emulated as fullscreen keys.
            Browser/editor controls have their own commands; this slice does not implement full keyboard/mouse parity.
            Use the terminal's own scrollback for whole-topic output; guide view works with redirected output too.
            `[Display exercise`guide:display]
            `[Safety`guide:safety]
            """,
        ["display"] = """
            >Original display exercise
            This local topic contains synthetic text only; no primary display/demo/font/image assets are bundled.
            >>Heading levels
            Plain ASCII: ABC xyz 0123 +-*/ [] {}.
            Optional Unicode: café, Δ, 箱. Your terminal and font determine their appearance and width.
            Marked words: `!important`!, `*emphasis`*, `_underlined`_.
            --------------------------------
            `:sample
            >>Anchor exercise
            `[Jump to this exercise`guide:display#sample]
            `[Return to controls`guide:keyboard]
            guide display#sample selects the exercise; guide back returns to the preceding position.
            Color mode, theme and glyph configuration apply after restart. Monochrome emits no color.
            This reader uses safe original text, not primary columns, interactive image widgets or Kitty output.
            Physical terminal/assistive technology/font behavior and other platforms remain separately unverified.
            """,
        ["licenses"] = """
            >Provenance and licenses
            This is experimental LLM-built .NET software, not an official or endorsed NomadNet application.
            Mark Qvist's Python projects are the reference; this guide's instructions and samples are original.
            The observed NomadNet target is 1.4.4 at f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2.
            Upstream's LICENSE is GPLv3 while setup metadata says MIT; that conflict does not authorize copying.
            No upstream guide prose, demos, display assets, fonts, UI templates or license text is reproduced here.
            The repository's LICENSE and copyright notices remain authoritative and unchanged.
            Reference dependency minima are newer than the pinned RNS1.5.7/LXMF1.2.1 test environment.
            Selected passing vectors and local native flows do not establish complete upstream compatibility.
            Consult README.md and docs/nomadnet.md for current scope, exact evidence and unresolved software gaps.
            `[Safety`guide:safety]
            """,
        ["troubleshooting"] = """
            >Recover without weakening safety
            Unknown topic or anchor? Use guide topics or guide links. A failed navigation leaves your position intact.
            At the first/last history entry, back/forward reports an error without changing reader state.
            Guide history and viewport are session-only. Restart reopens start; no private text is indexed.
            An interrupted local guide command has no network/background work to recover. Reissue it after restart.
            No messages? Check status, selected RNS interfaces, the exact delivery address and discovery first.
            No page? Verify the node address, /page/ path, host configuration and identification policy.
            Saved settings not active? Close the clean configuration buffer and restart the owning application.
            Write refused? Reopen changed files, resolve dirty buffers and inspect permissions; do not bypass checks.
            Logs can contain addresses and metadata. Review and redact diagnostics before sharing them.
            Linux replay is currently blocked by read-only WSL/storage errors; this is not Linux acceptance.
            Physical audio, printers, radios and full terminal behavior require separate equipment and evidence.
            `[Start`guide:start]
            `[Configuration`guide:configuration]
            """,
        ["safety"] = """
            >Safety boundaries
            Back up valuable profiles. This is experimental LLM-built software, not production infrastructure.
            Documents/RNS text are bounded to one megabyte, native JSON to 64 KiB, listings to 2048 entries.
            Invalid UTF-8 is read-only. Source previews do not execute scripts or load remote resources.
            Ordinary external changes are rejected before writes; reopen and reconcile rather than overwrite.
            The exclusive application profile lease protects cooperating native processes.
            A hostile local administrator racing filesystem checks is not contained by this editor.
            Symlinks/reparse points, traversal, special files, device names and hidden paths are refused.
            Saved document contents are not stored in editor_state.json. Unsaved drafts do not survive a crash.
            Pair rename/delete has in-process rollback, not a crash-atomic multi-file filesystem transaction.
            Linux replay, physical terminal rendering and external interactive editors have separate boundaries.
            `[Start`guide:start]
            """,
    }.AsReadOnly();

    public static IReadOnlyDictionary<string, string> Snippets { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["heading"] = ">Local notice\nWrite your own text here.\n",
        ["link"] = "`[Read the local notice`:/page/welcome.mu]",
        ["field"] = "`<24|query`>",
        ["section"] = "`+>Local details\nAdd explanatory text.\n<\n",
        ["literal"] = "`=\nUnformatted local example\n`=\n",
    }.AsReadOnly();

    public const int MaximumHistory = 32;
    public const int MaximumTargetCharacters = 256;
    public const int MaximumSearchCharacters = 128;
    public const int MaximumSearchResults = 64;

    public static IReadOnlyDictionary<string, string> PrimaryTopicCoverage { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Introduction"] = "start", ["Concepts & Terminology"] = "concepts",
            ["Channels & RRC"] = "network", ["Conversations"] = "messages",
            ["Interfaces"] = "interfaces", ["Hosting a Node"] = "hosting",
            ["Configuration Options"] = "configuration", ["Keyboard Shortcuts"] = "keyboard",
            ["Micron Markup"] = "micron", ["Display Test"] = "display",
            ["Network Configuration"] = "network-configuration", ["Credits & Licenses"] = "licenses",
            ["First Run"] = "first-run",
        }.AsReadOnly();

    private readonly List<Position> _history = [];
    private readonly List<Position> _forward = [];
    private int _viewportRows = 20;
    public string CurrentTopic { get; private set; } = "start";
    public int CurrentLine { get; private set; }
    public int BackCount => _history.Count;
    public int ForwardCount => _forward.Count;
    public NomadNetRenderedPage Page => MicronText.Render(Encoding.UTF8.GetBytes(Topics[CurrentTopic]));

    public string Open(string target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        if (target.Length > MaximumTargetCharacters || target.Any(char.IsControl))
            throw new ArgumentException("Guide target is too long or contains control characters.");
        if (target.StartsWith("guide:", StringComparison.Ordinal)) target = target[6..];
        var parts = target.Split('#', 2);
        var topic = parts[0].Length == 0 ? CurrentTopic : parts[0];
        if (!Topics.ContainsKey(topic)) throw new ArgumentException("Unknown local guide topic. Use guide topics.");
        var rendered = MicronText.Render(Encoding.UTF8.GetBytes(Topics[topic]));
        var line = 0;
        if (parts.Length == 2)
        {
            if (parts[1].Length == 0)
                line = HeadingLines(topic, rendered).Where(value => value > (topic == CurrentTopic ? CurrentLine : -1))
                    .Order().DefaultIfEmpty(topic == CurrentTopic ? CurrentLine : 0).First();
            else if (!rendered.Anchors.TryGetValue(parts[1], out line)) throw new ArgumentException("Unknown guide anchor.");
        }
        var text = TextFrom(rendered, line);
        cancellationToken.ThrowIfCancellationRequested();
        if (topic != CurrentTopic || line != CurrentLine)
        {
            Push(_history, new(CurrentTopic, CurrentLine));
            _forward.Clear();
        }
        CurrentTopic = topic;
        CurrentLine = line;
        return text;
    }

    public string Back(CancellationToken cancellationToken = default) => NavigateHistory(_history, _forward, cancellationToken);
    public string Forward(CancellationToken cancellationToken = default) => NavigateHistory(_forward, _history, cancellationToken);

    private string NavigateHistory(List<Position> source, List<Position> destination, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (source.Count == 0) throw new InvalidOperationException("No " + (source == _history ? "previous" : "next") + " guide topic.");
        var position = source[^1];
        var rendered = MicronText.Render(Encoding.UTF8.GetBytes(Topics[position.Topic]));
        var text = (position.Line == 0 ? "" : rendered.Text.Split('\n')[0] + "\n") + TextFrom(rendered, position.Line);
        token.ThrowIfCancellationRequested();
        Push(destination, new(CurrentTopic, CurrentLine));
        source.RemoveAt(source.Count - 1);
        CurrentTopic = position.Topic;
        CurrentLine = position.Line;
        return text;
    }

    public string Follow(int number, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var links = Page.Links;
        if (number < 1 || number > links.Count) throw new ArgumentOutOfRangeException(nameof(number));
        return Open(links[number - 1].Target, cancellationToken);
    }

    public string Select(int number, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (number < 1 || number > Topics.Count) throw new ArgumentOutOfRangeException(nameof(number));
        return Open(Topics.Keys.ElementAt(number - 1), cancellationToken);
    }

    public string View(int? rows = null, int scroll = 0, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (rows is < 1 or > 100 || scroll is < -10000 or > 10000)
            throw new ArgumentOutOfRangeException(nameof(rows), "Guide view requires1–100 rows and scroll -10000..10000.");
        var lines = Page.Text.Split('\n');
        var line = Math.Clamp(CurrentLine + scroll, 0, Math.Max(0, lines.Length - 1));
        var height = rows ?? _viewportRows;
        var text = string.Join('\n', lines.Skip(line).Take(height)) +
            $"\nGuide {CurrentTopic}; lines {line + 1}-{Math.Min(lines.Length, line + height)}/{lines.Length}; back={BackCount}; forward={ForwardCount}.";
        cancellationToken.ThrowIfCancellationRequested();
        CurrentLine = line;
        _viewportRows = height;
        return text;
    }

    public IReadOnlyList<NomadNetGuideMatch> Find(string term, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(term);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(term) || term.Length > MaximumSearchCharacters || term.Any(char.IsControl))
            throw new ArgumentException("Guide search requires1–128 characters without controls.");
        var matches = new List<NomadNetGuideMatch>();
        foreach (var (topic, content) in Topics)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = MicronText.Render(Encoding.UTF8.GetBytes(content));
            foreach (var (line, index) in page.Text.Split('\n').Select((text, index) => (text, index)))
                if (line.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    var anchor = page.Anchors.Where(pair => pair.Value <= index).OrderByDescending(pair => pair.Value).FirstOrDefault().Key;
                    matches.Add(new("guide:" + topic + (anchor is null ? "" : "#" + anchor), index + 1, line));
                    if (matches.Count == MaximumSearchResults) return matches.AsReadOnly();
                }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return matches.AsReadOnly();
    }

    private static string TextFrom(NomadNetRenderedPage page, int line) => string.Join('\n', page.Text.Split('\n').Skip(line));
    private static IEnumerable<int> HeadingLines(string topic, NomadNetRenderedPage page) =>
        Topics[topic].Split('\n').Where(line => line.StartsWith('>') || line.StartsWith("`+>", StringComparison.Ordinal))
            .Select(line => NomadNetRenderedPage.HeadingSlug(line.TrimStart('`', '+', '>')))
            .Where(page.Anchors.ContainsKey).Select(slug => page.Anchors[slug]).Distinct();
    private static void Push(List<Position> history, Position position)
    {
        if (history.Count == MaximumHistory) history.RemoveAt(0);
        history.Add(position);
    }
    private sealed record Position(string Topic, int Line);
}

public sealed record NomadNetGuideMatch(string Target, int Line, string Text);
