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
            `[Create a page`guide:authoring]
            `[Messages and files`guide:messages]
            `[Hosting and access`guide:hosting]
            `[Configuration and restart`guide:configuration]
            `[Micron samples`guide:micron]
            `[Channels and network tools`guide:network]
            `[Safety and limitations`guide:safety]
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
            `[Start`guide:start]
            """,
        ["hosting"] = """
            >Hosting documents
            edit list pages and edit list files show immediate children; add a relative folder to browse it.
            edit mkdir pages chapter creates a folder. edit open pages chapter/notice.mu opens a document.
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
            The native JSON is not ConfigObj/INI. Import supported primary keys using offline --import-config.
            Unsupported primary keys are reported; this is not complete configuration migration.
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
            utility help describes explicit local/network utilities and authorization requirements.
            sync status shows propagation retrieval; sync cancel cancels owned retrieval.
            A local guide or editor test is not evidence of live messaging or transport interoperability.
            `[Start`guide:start]
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

    private readonly List<string> _history = [];
    public string CurrentTopic { get; private set; } = "start";
    public NomadNetRenderedPage Page => MicronText.Render(Encoding.UTF8.GetBytes(Topics[CurrentTopic]));

    public string Open(string target)
    {
        if (target.Length > 256) throw new ArgumentException("Guide target is too long.");
        if (target.StartsWith("guide:", StringComparison.Ordinal)) target = target[6..];
        var parts = target.Split('#', 2);
        var topic = parts[0].Length == 0 ? CurrentTopic : parts[0];
        if (!Topics.ContainsKey(topic)) throw new ArgumentException("Unknown local guide topic. Use guide topics.");
        var rendered = MicronText.Render(Encoding.UTF8.GetBytes(Topics[topic]));
        var text = rendered.Text;
        if (parts.Length == 2)
        {
            if (!rendered.Anchors.TryGetValue(parts[1], out var line)) throw new ArgumentException("Unknown guide anchor.");
            text = string.Join('\n', rendered.Text.Split('\n').Skip(line));
        }
        if (topic != CurrentTopic)
        {
            if (_history.Count == 32) _history.RemoveAt(0);
            _history.Add(CurrentTopic);
        }
        CurrentTopic = topic;
        return text;
    }

    public string Back()
    {
        if (_history.Count == 0) throw new InvalidOperationException("No previous guide topic.");
        CurrentTopic = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        return Page.Text;
    }

    public string Follow(int number)
    {
        var links = Page.Links;
        if (number < 1 || number > links.Count) throw new ArgumentOutOfRangeException(nameof(number));
        return Open(links[number - 1].Target);
    }
}
