using System.Text;
using System.Text.Json;

namespace NomadNet;

public sealed record NomadNetEditorTreeEntry(string Scope, string Path, int Depth, bool Directory, bool Expanded);

public sealed partial class NomadNetEditor
{
    private NomadNetEditorPreview? _preview;
    private bool _presentationActivated;
    private bool _filterMu;
    private HashSet<string> _expanded = new(StringComparer.Ordinal) { "pages", "files" };
    private HashSet<string> _panes = new(StringComparer.Ordinal) { "files", "editor", "preview" };
    private string _focusedPane = "files";
    private static readonly string[] PaneOrder = ["files", "editor", "preview"];
    public Action<string>? PresentationLog { get; set; }
    public bool FilterMu => _filterMu;
    public IReadOnlyList<string> VisiblePanes => PaneOrder.Where(_panes.Contains).ToArray();
    public string FocusedPane => _focusedPane;
    public NomadNetEditorPreviewResult? AutomaticPreview => _preview?.Current;
    public long PreviewRevision => _preview?.Revision ?? 0;

    public async Task ActivatePresentationAsync(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_presentationActivated) return;
        var state = await ReadPresentationStateAsync(token).ConfigureAwait(false);
        if (state is not null)
        {
            ValidatePresentationState(state);
            _filterMu = state.FilterMu;
            _expanded = new(state.Expanded ?? ["pages", "files"], StringComparer.Ordinal);
            _panes = new(state.VisiblePanes ?? PaneOrder, StringComparer.Ordinal);
        }
        _focusedPane = _panes.Contains("files") ? "files" : VisiblePanes[0];
        _preview = new(RenderSourcePreview) { Log = ReportPresentation };
        try
        {
            if (!IsOpen && state?.Scope is { } scope && state.Path is { } path)
            {
                try { await OpenAsync(scope, path, cancellationToken: token).ConfigureAwait(false); }
                catch (FileNotFoundException) { PresentationLog?.Invoke("Editor saved selection no longer exists; presentation remains available."); }
            }
            RefreshPresentationSelection();
            _presentationActivated = true;
        }
        catch
        {
            await _preview.DisposeAsync().ConfigureAwait(false);
            _preview = null;
            throw;
        }
    }

    private static string RenderSourcePreview(string text) => text.Length > 100_000
        ? "Preview disabled above 100000 characters; editing remains available."
        : MicronText.Render(Encoding.UTF8.GetBytes(text)).Text;

    private void ReportPresentation(string message)
    {
        if (PresentationLog is { } log) log(message);
        else System.Diagnostics.Trace.TraceError(message);
    }

    private void RefreshPresentationSelection()
    {
        if (_preview is null) return;
        _preview.Select(!IsOpen ? "" : Preview());
    }

    private void PresentationBufferChanged()
    {
        if (_preview is null || !IsOpen) return;
        if (!Buffer.Editable || IsConfiguration || Executable) _preview.Select(Preview());
        else _preview.Change(Buffer.Text);
    }

    public async Task FilterAsync(bool enabled, CancellationToken token = default)
    {
        await ActivatePresentationAsync(token).ConfigureAwait(false);
        var previous = _filterMu;
        _filterMu = enabled;
        try { _ = Tree(token); await SavePresentationStateAsync(token).ConfigureAwait(false); }
        catch { _filterMu = previous; throw; }
    }

    public async Task ExpandAsync(string scope, string? relative, bool expanded, CancellationToken token = default)
    {
        await ActivatePresentationAsync(token).ConfigureAwait(false);
        RequireFolderScope(scope);
        var path = relative is null ? _roots[scope] : ResolveFolder(scope, relative);
        Guard(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("Choose an existing editor folder.");
        var key = TreeKey(scope, relative);
        if (key.Count(character => character == '/') > MaximumFolderDepth) throw new InvalidDataException("Tree expansion exceeds16 levels.");
        if (expanded && !_expanded.Contains(key) && _expanded.Count == 256) throw new InvalidDataException("Tree expansion exceeds256 folders.");
        var previous = new HashSet<string>(_expanded, StringComparer.Ordinal);
        if (expanded) _expanded.Add(key); else _expanded.Remove(key);
        try { _ = Tree(token); await SavePresentationStateAsync(token).ConfigureAwait(false); }
        catch { _expanded = previous; throw; }
    }

    public async Task PaneAsync(string pane, CancellationToken token = default)
    {
        await ActivatePresentationAsync(token).ConfigureAwait(false);
        if (!PaneOrder.Contains(pane, StringComparer.Ordinal)) throw new ArgumentException("Choose files, editor or preview.");
        if (_panes.Contains(pane) && _panes.Count == 1) return;
        var previous = new HashSet<string>(_panes, StringComparer.Ordinal);
        var focus = _focusedPane;
        if (!_panes.Remove(pane)) _panes.Add(pane);
        if (!_panes.Contains(_focusedPane)) _focusedPane = _panes.Contains("files") ? "files" : VisiblePanes[0];
        try { await SavePresentationStateAsync(token).ConfigureAwait(false); }
        catch { _panes = previous; _focusedPane = focus; throw; }
    }

    public void FocusPane(bool backwards = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var panes = VisiblePanes;
        var index = Array.IndexOf(panes.ToArray(), _focusedPane);
        _focusedPane = panes[(index + (backwards ? panes.Count - 1 : 1)) % panes.Count];
    }

    public IReadOnlyList<NomadNetEditorTreeEntry> Tree(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = new List<NomadNetEditorTreeEntry>();
        var scanned = 0;
        var searchable = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var scope in new[] { "pages", "files" })
        {
            Guard(_roots[scope]);
            if (Directory.Exists(_roots[scope])) Visit(scope, "", 0);
        }
        return result.AsReadOnly();

        string[] Entries(string folder)
        {
            token.ThrowIfCancellationRequested();
            Guard(folder);
            var entries = new List<string>();
            foreach (var item in Directory.EnumerateFileSystemEntries(folder))
            {
                token.ThrowIfCancellationRequested();
                if (++scanned > 4096) throw new InvalidDataException("Editor tree exceeds4096 inspected entries.");
                var name = System.IO.Path.GetFileName(item);
                if (name.StartsWith('.') || name == "__pycache__" || name.EndsWith(".allowed", StringComparison.OrdinalIgnoreCase)) continue;
                if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) continue;
                entries.Add(item);
            }
            return entries.OrderBy(item => System.IO.Path.GetFileName(item), StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => System.IO.Path.GetFileName(item), StringComparer.Ordinal).ToArray();
        }
        bool ContainsMu(string folder, int depth)
        {
            if (searchable.TryGetValue(folder, out var found)) return found;
            if (depth > MaximumFolderDepth) throw new InvalidDataException("Editor tree exceeds16 levels.");
            var entries = Entries(folder);
            var answer = entries.Any(item => !Directory.Exists(item) && item.EndsWith(".mu", StringComparison.Ordinal));
            foreach (var child in entries.Where(Directory.Exists))
                answer = ContainsMu(child, depth + 1) || answer;
            searchable[folder] = answer;
            return answer;
        }
        void Visit(string scope, string relative, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (depth > MaximumFolderDepth) throw new InvalidDataException("Editor tree exceeds16 levels.");
            var path = relative.Length == 0 ? _roots[scope] : ResolveFolder(scope, relative);
            var expanded = _expanded.Contains(TreeKey(scope, relative));
            Add(new(scope, relative, depth, true, expanded));
            if (!expanded) return;
            var entries = Entries(path);
            foreach (var item in entries.Where(Directory.Exists))
            {
                if (_filterMu && !ContainsMu(item, depth + 1)) continue;
                Visit(scope, System.IO.Path.GetRelativePath(_roots[scope], item), depth + 1);
            }
            foreach (var item in entries.Where(item => !Directory.Exists(item)))
            {
                if (_filterMu && !item.EndsWith(".mu", StringComparison.Ordinal)) continue;
                var selected = System.IO.Path.GetRelativePath(_roots[scope], item);
                _ = Resolve(scope, selected);
                Add(new(scope, selected, depth + 1, false, false));
            }
        }
        void Add(NomadNetEditorTreeEntry entry)
        {
            if (result.Count == 2048) throw new InvalidDataException("Editor tree exceeds2048 displayed entries.");
            result.Add(entry);
        }
    }

    public string View(int width = 120, int rows = 20, NomadNetSettings? settings = null, bool ansi = false,
        CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_presentationActivated) throw new InvalidOperationException("Activate editor presentation first.");
        if (width is < 40 or > 240 || rows is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(width), "View width40–240, rows1–60.");
        settings ??= _configurationBasis;
        var panes = VisiblePanes;
        var weights = panes.Select(pane => pane switch { "files" => 22, "editor" => 40, _ => 38 }).ToArray();
        var widths = PaneWidths(width);
        _viewWidth = width;
        _viewRows = rows;
        var output = new StringBuilder("Editor panes: " + string.Join(", ", panes.Select((pane, index) =>
            $"{pane}={weights[index]}%/{widths[index]}cols")) + $"; focus={_focusedPane}; filter.mu={_filterMu}\n");
        var columns = new List<IReadOnlyList<string>>();
        for (var index = 0; index < panes.Count; index++)
        {
            var size = widths[index];
            switch (panes[index])
            {
                case "files":
                    columns.Add(Tree(token).Take(rows).Select(entry =>
                        Cell(new string(' ', entry.Depth * 2) + (entry.Directory ? entry.Expanded ? "[-] " : "[+] " : " .  ") +
                            (entry.Path.Length == 0 ? entry.Scope : System.IO.Path.GetFileName(entry.Path)), size)).ToArray());
                    break;
                case "editor":
                    _sourceScroll.Clamp(NomadNetEditorSyntax.CountRows(Buffer.Text, size), rows);
                    columns.Add(NomadNetEditorSyntax.Rows(Buffer.Text, size, rows, SourceScrollPosition)
                        .Select(row => NomadNetEditorSyntax.Color(row, settings, ansi)).ToArray());
                    break;
                default:
                    var text = PreviewViewportText();
                    _previewScroll.Clamp(text.Count(character => character == '\n') + 1, rows);
                    var styled = AutomaticPreview is { Error: null } preview && preview.Revision == PreviewRevision &&
                        IsOpen && Buffer.Editable && !IsConfiguration && !Executable && Buffer.Text.Length <= 100_000;
                    columns.Add(styled
                        ? MicronText.Render(Encoding.UTF8.GetBytes(Buffer.Text)).PresentLines(settings, ansi, size, pad: true, wrap: false)
                            .Skip(PreviewScrollPosition).Take(rows).ToArray()
                        : text.Split('\n').Skip(PreviewScrollPosition).Take(rows).Select(line => Cell(line, size)).ToArray());
                    break;
            }
        }
        output.AppendLine($"Scroll rows: editor={SourceScrollPosition}; preview={PreviewScrollPosition}; " +
            $"height={rows}; preview={(AutomaticPreview is { } current && current.Revision == PreviewRevision ? "ready" : "pending")}");
        output.AppendLine(string.Join(" | ", panes.Select((pane, index) => Cell(pane.ToUpperInvariant(), widths[index]))));
        for (var row = 0; row < Math.Min(rows, columns.Max(column => column.Count)); row++)
            output.AppendLine(string.Join(" | ", columns.Select((column, index) => row < column.Count ? column[row] : new string(' ', widths[index]))));
        return output.ToString();
    }

    private static string Cell(string text, int width)
    {
        text = NomadNetEditorSyntax.SafeCells(text);
        if (text.Length > width) text = text[..(char.IsHighSurrogate(text[width - 1]) ? width - 1 : width)];
        return text.PadRight(width);
    }

    private static string TreeKey(string scope, string? relative) =>
        string.IsNullOrEmpty(relative) ? scope : scope + "/" + relative.Replace('\\', '/');

    private async Task<NomadNetEditorState?> ReadPresentationStateAsync(CancellationToken token)
    {
        var bytes = await SnapshotAsync(System.IO.Path.Combine(_profile, "editor_state.json"), token).ConfigureAwait(false);
        if (bytes is null) return null;
        if (bytes.Length > 32768) throw new InvalidDataException("Editor state exceeds32 KiB.");
        return JsonSerializer.Deserialize(bytes, NomadNetEditorJsonContext.Default.NomadNetEditorState)
            ?? throw new InvalidDataException("Invalid editor state.");
    }

    private void ValidatePresentationState(NomadNetEditorState state)
    {
        if (state.SchemaVersion != 1 || (state.Scope is null) != (state.Path is null))
            throw new InvalidDataException("Unsupported or incomplete editor state.");
        if (state.Scope is not null) _ = Resolve(state.Scope, state.Path!);
        if (state.VisiblePanes is { } panes && (panes.Length is < 1 or > 3 ||
            panes.Distinct(StringComparer.Ordinal).Count() != panes.Length || panes.Any(pane => !PaneOrder.Contains(pane, StringComparer.Ordinal))))
            throw new InvalidDataException("Invalid editor pane state.");
        if (state.Expanded is { } expanded)
        {
            if (expanded.Length > 256 || expanded.Distinct(StringComparer.Ordinal).Count() != expanded.Length)
                throw new InvalidDataException("Invalid editor expansion state.");
            foreach (var key in expanded)
            {
                if (key is null) throw new InvalidDataException("Null editor expansion.");
                var pieces = key.Split('/', 2);
                RequireFolderScope(pieces[0]);
                if (pieces.Length == 2) _ = ResolveFolder(pieces[0], pieces[1]);
                if (key.Count(character => character == '/') > MaximumFolderDepth)
                    throw new InvalidDataException("Editor expansion exceeds16 levels.");
            }
        }
    }

    private async Task SavePresentationStateAsync(CancellationToken token)
    {
        var previous = await ReadPresentationStateAsync(token).ConfigureAwait(false);
        await WriteStateAsync(new(Scope: Scope ?? previous?.Scope, Path: RelativePath ?? previous?.Path), token).ConfigureAwait(false);
    }
}
