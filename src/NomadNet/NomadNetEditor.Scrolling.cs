namespace NomadNet;

/// <summary>Original bounded row viewport; no cursor or physical-terminal column contract.</summary>
public sealed class NomadNetEditorViewport
{
    public int Position { get; private set; }

    public void Clamp(int totalRows, int visibleRows)
    {
        if (totalRows < 0 || visibleRows is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(totalRows));
        Position = Math.Clamp(Position, 0, Math.Max(0, totalRows - visibleRows));
    }

    public void Scroll(string movement, int totalRows, int visibleRows)
    {
        var target = movement switch
        {
            "up" => (long)Position - 1,
            "down" => (long)Position + 1,
            "page-up" => (long)Position - Math.Max(1, visibleRows - 1),
            "page-down" => (long)Position + Math.Max(1, visibleRows - 1),
            "home" => 0,
            "end" => (long)totalRows,
            _ when int.TryParse(movement, System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out var delta) => (long)Position + delta,
            _ => throw new ArgumentException("Scroll up|down|page-up|page-down|home|end or a signed Int32 row delta.")
        };
        if (totalRows < 0 || visibleRows is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(totalRows));
        Position = (int)Math.Clamp(target, 0, Math.Max(0, totalRows - visibleRows));
    }
}

public sealed partial class NomadNetEditor
{
    private readonly NomadNetEditorViewport _sourceScroll = new();
    private readonly NomadNetEditorViewport _previewScroll = new();
    private int _viewWidth = 120;
    private int _viewRows = 20;
    public int SourceScrollPosition => _sourceScroll.Position;
    public int PreviewScrollPosition => _previewScroll.Position;
    public int ViewWidth => _viewWidth;
    public int ViewRows => _viewRows;

    private int[] PaneWidths(int width)
    {
        var weights = VisiblePanes.Select(pane => pane switch { "files" => 22, "editor" => 40, _ => 38 }).ToArray();
        var available = width - (weights.Length - 1) * 3;
        var widths = weights.Select(weight => available * weight / weights.Sum()).ToArray();
        widths[^1] += available - widths.Sum();
        return widths;
    }

    private string PreviewViewportText() => AutomaticPreview?.Text ?? "Preview pending.";

    public void Scroll(string pane, string movement)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_presentationActivated) throw new InvalidOperationException("Activate editor presentation first.");
        if (pane == "focused") pane = _focusedPane;
        if (pane is not ("editor" or "preview")) throw new ArgumentException("Scroll editor, preview or a focused text pane.");
        if (!_panes.Contains(pane)) throw new InvalidOperationException("Show the selected text pane before scrolling.");
        var width = PaneWidths(_viewWidth)[Array.IndexOf(VisiblePanes.ToArray(), pane)];
        if (pane == "editor") _sourceScroll.Scroll(movement, NomadNetEditorSyntax.CountRows(Buffer.Text, width), _viewRows);
        else _previewScroll.Scroll(movement, PreviewViewportText().Count(character => character == '\n') + 1, _viewRows);
    }

    private void ResetScroll()
    {
        _sourceScroll.Scroll("home", 0, _viewRows);
        _previewScroll.Scroll("home", 0, _viewRows);
    }
}
