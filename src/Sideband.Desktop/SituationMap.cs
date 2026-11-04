using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Sideband;

namespace Sideband.Desktop;

public sealed record SituationMapMarker(string Label, SidebandGeoCoordinate Coordinate,
    string Foreground = "000000", string Background = "ffffff",
    string? SourceHash = null, SidebandMapOverlayMarker? Overlay = null);

/// <summary>Original bounded raster viewport. Reported locations are not authenticated physical positions.</summary>
public sealed class SituationMap : Control
{
    private IReadOnlyDictionary<SidebandMapTile, Bitmap> _tiles = new Dictionary<SidebandMapTile, Bitmap>();
    private IReadOnlyList<SituationMapMarker> _markers = [];
    private IReadOnlyList<SidebandMapOverlayMarker> _overlays = [];
    private IReadOnlyDictionary<string, string> _markerLabels = new Dictionary<string, string>();
    private readonly List<(Point Point, SidebandMapOverlayCluster Cluster)> _hitTargets = [];
    private Point? _drag;
    private Point _dragStart;
    private bool _dragMoved;
    private bool _clusterMarkers = true;
    public event EventHandler? ViewChanged;
    public event EventHandler<SidebandMapOverlayMarker>? MarkerActivated;
    public bool ClusterMarkers
    {
        get => _clusterMarkers;
        set { _clusterMarkers = value; _hitTargets.Clear(); InvalidateVisual(); }
    }
    public double Latitude { get; private set; }
    public double Longitude { get; private set; }
    public int Zoom { get; private set; } = 1;
    public int MinimumZoom { get; private set; }
    public int MaximumZoom { get; private set; } = SidebandMapProjection.MaximumZoom;
    public bool IsCartesian { get; private set; }
    public int LoadedTileCount => _tiles.Count;
    public int RenderedTileCount { get; private set; }
    public int MarkerCount => _markers.Count;
    public int RenderedMarkerCount { get; private set; }
    public int RenderedClusterCount { get; private set; }

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
        _hitTargets.Clear();
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
        var overlays = bounded.Select((marker, index) => marker.Overlay ?? new(
            index.ToString(CultureInfo.InvariantCulture), marker.SourceHash ?? index.ToString("x32", CultureInfo.InvariantCulture),
            marker.Coordinate, SidebandMapOverlayAppearance.FromTelemetry(new("map-marker", marker.Foreground, marker.Background)),
            Name: marker.Label)).ToArray();
        _ = SidebandMapOverlays.Cluster(overlays, SidebandMapProjection.MaximumZoom);
        for (var index = 0; index < bounded.Length; index++)
            if (bounded[index].Coordinate != overlays[index].Coordinate)
                throw new ArgumentException("Overlay and display marker coordinates must match.", nameof(markers));
        _overlays = overlays;
        _markerLabels = overlays.Select((marker, index) => (marker.Key, bounded[index].Label))
            .ToDictionary(item => item.Key, item => item.Label, StringComparer.Ordinal);
        _markers = bounded;
        _hitTargets.Clear();
        InvalidateVisual();
    }

    public IReadOnlyList<SidebandMapOverlayCluster> DisplayClusters() => ClusterMarkers
        ? SidebandMapOverlays.Cluster(_overlays, Zoom)
        : _overlays.Select(marker => new SidebandMapOverlayCluster(marker.Coordinate,
            new(marker.Coordinate.Longitude, marker.Coordinate.Latitude, marker.Coordinate.Longitude, marker.Coordinate.Latitude),
            Array.AsReadOnly(new[] { marker }))).ToArray();

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
        _hitTargets.Clear();
        RenderedMarkerCount = RenderedClusterCount = 0;
        if (IsCartesian) return;
        var clusters = DisplayClusters();
        var singles = clusters.Where(cluster => !cluster.IsCluster).Select(cluster => cluster.Members[0]).ToArray();
        var positions = singles.Select(marker => Position(marker.Coordinate, center)).ToArray();
        var offsets = SidebandMapOverlays.Scatter(singles, positions.Select(point => new SidebandMapPoint(point.X, point.Y)).ToArray(), Zoom);
        var singleIndex = 0;
        foreach (var cluster in clusters)
        {
            var marker = cluster.Members[0];
            var point = Position(cluster.Coordinate, center);
            if (!cluster.IsCluster)
            {
                var offset = offsets[singleIndex++];
                point += new Vector(offset.X, offset.Y);
            }
            if (point.X < -100 || point.X > Bounds.Width + 100 || point.Y < -100 || point.Y > Bounds.Height + 100) continue;
            var foreground = Brush(marker.Appearance.Foreground);
            var background = Brush(marker.Appearance.Background);
            var radius = cluster.IsCluster ? 14 : marker.Small ? 4 : 6;
            context.DrawEllipse(background, new Pen(foreground, 2), point, radius, radius);
            var label = cluster.IsCluster ? cluster.Count.ToString(CultureInfo.InvariantCulture) : _markerLabels[marker.Key];
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typeface.Default, 12, foreground);
            context.FillRectangle(background, new Rect(point + new Vector(8, -9), new Size(text.Width + 2, text.Height)));
            context.DrawText(text, point + new Vector(9, -9));
            _hitTargets.Add((point, cluster));
            RenderedMarkerCount++;
            if (cluster.IsCluster) RenderedClusterCount++;
        }
    }

    private Point Position(SidebandGeoCoordinate coordinate, SidebandMapPoint center)
    {
        var world = SidebandMapProjection.ToWorld(Zoom, SidebandMapProjection.ClipDisplayLatitude(coordinate.Latitude), coordinate.Longitude);
        return new(world.X - center.X + Bounds.Width / 2, center.Y - world.Y + Bounds.Height / 2);
    }

    private static IBrush Brush(SidebandMapColor color) => new SolidColorBrush(Color.FromArgb(
        (byte)Math.Round(color.Alpha * 255), (byte)Math.Round(color.Red * 255),
        (byte)Math.Round(color.Green * 255), (byte)Math.Round(color.Blue * 255)));

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
        _hitTargets.Clear();
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _drag = e.GetPosition(this);
        _dragStart = _drag.Value;
        _dragMoved = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is not { } previous) return;
        var current = e.GetPosition(this);
        if (DistanceSquared(current, _dragStart) > 9) _dragMoved = true;
        Pan(current - previous);
        _drag = current;
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is null) return;
        var clicked = !_dragMoved;
        _drag = null;
        e.Pointer.Capture(null);
        if (clicked) ActivateMarker(e.GetPosition(this));
        RaiseViewChanged();
        e.Handled = true;
    }

    private void ActivateMarker(Point point)
    {
        var target = _hitTargets.LastOrDefault(target => DistanceSquared(target.Point, point) <= 324).Cluster;
        if (target is null) return;
        if (target.IsCluster)
        {
            var view = SidebandMapOverlays.FitBounds(target.Bounds, Math.Max(1, Bounds.Width), Math.Max(1, Bounds.Height),
                MinimumZoom, Math.Min(MaximumZoom, 19));
            SetView(view.Latitude, view.Longitude, (int)view.Zoom);
        }
        else if (MarkerActivated is { } activated)
            foreach (EventHandler<SidebandMapOverlayMarker> observer in activated.GetInvocationList())
            {
                try { observer(this, target.Members[0]); }
                catch (Exception exception) { System.Diagnostics.Trace.TraceError("Situation map marker observer failed: {0}", exception); }
            }
    }

    private static double DistanceSquared(Point first, Point second) =>
        (first.X - second.X) * (first.X - second.X) + (first.Y - second.Y) * (first.Y - second.Y);

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
