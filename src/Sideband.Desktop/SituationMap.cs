using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Sideband;

namespace Sideband.Desktop;

public sealed record SituationMapMarker(string Label, SidebandGeoCoordinate Coordinate,
    string Foreground = "000000", string Background = "ffffff");

/// <summary>Original bounded raster viewport. Reported locations are not authenticated physical positions.</summary>
public sealed class SituationMap : Control
{
    private IReadOnlyDictionary<SidebandMapTile, Bitmap> _tiles = new Dictionary<SidebandMapTile, Bitmap>();
    private IReadOnlyList<SituationMapMarker> _markers = [];
    private Point? _drag;
    public event EventHandler? ViewChanged;
    public double Latitude { get; private set; }
    public double Longitude { get; private set; }
    public int Zoom { get; private set; } = 1;
    public int MinimumZoom { get; private set; }
    public int MaximumZoom { get; private set; } = SidebandMapProjection.MaximumZoom;
    public bool IsCartesian { get; private set; }
    public int LoadedTileCount => _tiles.Count;
    public int RenderedTileCount { get; private set; }
    public int MarkerCount => _markers.Count;

    public void SetZoomRange(int minimum, int maximum)
    {
        _ = SidebandMapProjection.WorldSize(minimum);
        _ = SidebandMapProjection.WorldSize(maximum);
        if (minimum > maximum) throw new ArgumentOutOfRangeException(nameof(minimum));
        MinimumZoom = minimum;
        MaximumZoom = maximum;
        SetView(Latitude, Longitude, Zoom, IsCartesian);
    }

    public void SetView(double latitude, double longitude, int zoom, bool cartesian = false)
    {
        _ = SidebandMapProjection.WorldSize(zoom);
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude))
            throw new ArgumentOutOfRangeException(nameof(latitude));
        Latitude = cartesian ? latitude : SidebandMapProjection.ClipDisplayLatitude(latitude);
        Longitude = cartesian ? longitude : Math.Clamp(longitude, -180, 180);
        IsCartesian = cartesian;
        Zoom = Math.Clamp(zoom, MinimumZoom, MaximumZoom);
        InvalidateVisual();
        RaiseViewChanged();
    }

    public void SetMarkers(IEnumerable<SituationMapMarker> markers)
    {
        ArgumentNullException.ThrowIfNull(markers);
        var bounded = markers.Take(257).ToArray();
        if (bounded.Length > 256) throw new ArgumentOutOfRangeException(nameof(markers));
        foreach (var marker in bounded)
        {
            _ = SidebandGeo.ToCartesian(marker.Coordinate);
            if (string.IsNullOrWhiteSpace(marker.Label) || marker.Label.Length > 80 || marker.Label.Any(char.IsControl))
                throw new ArgumentException("Map marker labels must contain 1-80 printable characters.", nameof(markers));
            if (marker.Foreground is not { Length: 6 } || marker.Background is not { Length: 6 } ||
                !marker.Foreground.All(Uri.IsHexDigit) || !marker.Background.All(Uri.IsHexDigit))
                throw new ArgumentException("Map marker appearance requires two six-digit RGB values.", nameof(markers));
        }
        _markers = bounded;
        InvalidateVisual();
    }

    public void ReplaceTiles(IReadOnlyDictionary<SidebandMapTile, Bitmap> tiles)
    {
        var previous = _tiles;
        _tiles = tiles;
        foreach (var bitmap in previous.Values) bitmap.Dispose();
        InvalidateVisual();
    }

    public IReadOnlyList<SidebandMapTile> VisibleTiles()
    {
        var center = Center();
        var count = 1 << Zoom;
        var left = Math.Clamp((int)Math.Floor((center.X - Bounds.Width / 2) / 256), 0, count - 1);
        var right = Math.Clamp((int)Math.Floor((center.X + Bounds.Width / 2) / 256), 0, count - 1);
        var bottom = Math.Clamp((int)Math.Floor((center.Y - Bounds.Height / 2) / 256), 0, count - 1);
        var top = Math.Clamp((int)Math.Floor((center.Y + Bounds.Height / 2) / 256), 0, count - 1);
        var tiles = new List<SidebandMapTile>();
        for (var y = bottom; y <= top && tiles.Count < 64; y++)
            for (var x = left; x <= right && tiles.Count < 64; x++)
                tiles.Add(new(Zoom, x, y));
        return tiles;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        using var clip = context.PushClip(new Rect(Bounds.Size));
        context.FillRectangle(Brushes.DarkSlateGray, new Rect(Bounds.Size));
        var center = Center();
        RenderedTileCount = 0;
        foreach (var tile in VisibleTiles())
        {
            var rectangle = new Rect(tile.X * 256d - center.X + Bounds.Width / 2,
                center.Y - (tile.TmsY + 1) * 256d + Bounds.Height / 2, 256, 256);
            if (_tiles.TryGetValue(tile, out var bitmap))
            {
                context.DrawImage(bitmap, new Rect(bitmap.Size), rectangle);
                RenderedTileCount++;
            }
            else context.DrawRectangle(null, new Pen(Brushes.SlateGray), rectangle);
        }
        if (IsCartesian) return;
        foreach (var marker in _markers)
        {
            var world = SidebandMapProjection.ToWorld(Zoom, SidebandMapProjection.ClipDisplayLatitude(marker.Coordinate.Latitude),
                marker.Coordinate.Longitude);
            var point = new Point(world.X - center.X + Bounds.Width / 2, center.Y - world.Y + Bounds.Height / 2);
            var foreground = new SolidColorBrush(Color.Parse("#" + marker.Foreground));
            var background = new SolidColorBrush(Color.Parse("#" + marker.Background));
            context.DrawEllipse(background, new Pen(foreground, 2), point, 6, 6);
            var text = new FormattedText(marker.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typeface.Default, 12, foreground);
            context.FillRectangle(background, new Rect(point + new Vector(8, -9), new Size(text.Width + 2, text.Height)));
            context.DrawText(text, point + new Vector(9, -9));
        }
    }

    private SidebandMapPoint Center() => IsCartesian ? new(Longitude, Latitude) :
        SidebandMapProjection.ToWorld(Zoom, Latitude, Longitude);

    private void Pan(Vector delta)
    {
        var center = Center();
        var world = SidebandMapProjection.WorldSize(Zoom);
        var moved = new SidebandMapPoint(Math.Clamp(center.X - delta.X, 0, world), Math.Clamp(center.Y + delta.Y, 0, world));
        if (IsCartesian) { Longitude = moved.X; Latitude = moved.Y; }
        else
        {
            var coordinate = SidebandMapProjection.FromWorld(Zoom, moved);
            Longitude = coordinate.Longitude;
            Latitude = SidebandMapProjection.ClipDisplayLatitude(coordinate.Latitude);
        }
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _drag = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is not { } previous) return;
        var current = e.GetPosition(this);
        Pan(current - previous);
        _drag = current;
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is null) return;
        _drag = null;
        e.Pointer.Capture(null);
        RaiseViewChanged();
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_drag is null) return;
        _drag = null;
        RaiseViewChanged();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Delta.Y == 0) return;
        SetView(Latitude, Longitude, Math.Clamp(Zoom + Math.Sign(e.Delta.Y), 0, SidebandMapProjection.MaximumZoom), IsCartesian);
        e.Handled = true;
    }

    private void RaiseViewChanged()
    {
        if (ViewChanged is not { } changed) return;
        foreach (EventHandler observer in changed.GetInvocationList())
        {
            try { observer(this, EventArgs.Empty); }
            catch (Exception exception) { System.Diagnostics.Trace.TraceError("Situation map view observer failed: {0}", exception); }
        }
    }
}
