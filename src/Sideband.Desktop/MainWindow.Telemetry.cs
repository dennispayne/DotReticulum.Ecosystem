using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Sideband;

namespace Sideband.Desktop;

public sealed partial class MainWindow
{
    private IReadOnlyList<SidebandTelemetryStoredReport> _telemetryReports = [];
    private bool _refreshingTelemetry;
    private bool _refreshTelemetryAgain;
    private string? _telemetryPreview;

    private void LoadTelemetrySettings()
    {
        if (_state is null) return;
        var config = _state.Telemetry;
        TelemetryEnabledBox.IsChecked = config.Enabled;
        TelemetryInformationEnabledBox.IsChecked = config.InformationEnabled;
        TelemetryInformationBox.Text = config.Information;
        TelemetryFixedLocationBox.IsChecked = config.FixedLocationEnabled;
        TelemetryLatitudeBox.Text = config.FixedLocation.Latitude.ToString("G17", CultureInfo.InvariantCulture);
        TelemetryLongitudeBox.Text = config.FixedLocation.Longitude.ToString("G17", CultureInfo.InvariantCulture);
        TelemetryAltitudeBox.Text = config.FixedLocation.Altitude.ToString("G17", CultureInfo.InvariantCulture);
        TelemetrySendTrustedBox.IsChecked = config.SendToTrusted;
        TelemetryReceiveTrustedBox.IsChecked = config.ReceiveTrustedOnly;
        TelemetryDisplayTrustedBox.IsChecked = config.DisplayTrustedOnly;
        TelemetryRequestsAnyoneBox.IsChecked = config.AllowRequestsFromAnyone;
        TelemetryRequestsTrustedBox.IsChecked = config.AllowRequestsFromTrusted;
        TelemetryLatestOnlyBox.IsChecked = config.RequestsOnlySendLatest;
        TelemetryPropagatedBox.IsChecked = config.UsePropagationOnly;
        TelemetryFallbackBox.IsChecked = config.TryPropagationOnFail;
        TelemetryCollectorBox.Text = config.Collector;
        TelemetryCollectorEnabledBox.IsChecked = config.CollectorEnabled;
        TelemetrySendCollectorBox.IsChecked = config.SendToCollector;
        TelemetrySendAllCollectorBox.IsChecked = config.SendAllToCollector;
        TelemetryRequestCollectorBox.IsChecked = config.RequestFromCollector;
        TelemetrySendIntervalBox.Text = config.SendIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        TelemetryRequestIntervalBox.Text = config.RequestIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        TelemetryExcludedBox.Text = string.Join(Environment.NewLine, config.ExcludedSources);
        TelemetryManualSensorsBox.Text = string.Join(Environment.NewLine,
            config.ManualSensors.Select(sensor => $"{sensor.SensorId:x2}={sensor.WireHex}"));
        TelemetryAppearanceBox.IsChecked = config.SendAppearance;
        TelemetryIconBox.Text = config.Appearance.Icon;
        TelemetryForegroundBox.Text = config.Appearance.Foreground;
        TelemetryBackgroundBox.Text = config.Appearance.Background;
        if (_selectedContact is not null) TelemetryPeerBox.Text = _selectedContact;
        var contact = _state.Contacts.FirstOrDefault(peer => peer.DeliveryHash == TelemetryPeerBox.Text);
        ConversationSendTelemetryBox.IsChecked = contact?.SendTelemetry == true;
        ConversationAllowTelemetryRequestsBox.IsChecked = contact?.AllowTelemetryRequests == true;
        UpdateTelemetryTrackingView();
    }

    private void SaveTelemetryClicked(object? sender, RoutedEventArgs e) => RunTelemetryOperation(async () =>
    {
        _state = await _client.LoadAsync(_windowCancellation.Token);
        var config = _state.Telemetry with
        {
            Enabled = TelemetryEnabledBox.IsChecked == true,
            InformationEnabled = TelemetryInformationEnabledBox.IsChecked == true,
            Information = TelemetryInformationBox.Text ?? "",
            FixedLocationEnabled = TelemetryFixedLocationBox.IsChecked == true,
            FixedLocation = new(TelemetryNumber(TelemetryLatitudeBox.Text), TelemetryNumber(TelemetryLongitudeBox.Text),
                TelemetryNumber(TelemetryAltitudeBox.Text)),
            SendToTrusted = TelemetrySendTrustedBox.IsChecked == true,
            ReceiveTrustedOnly = TelemetryReceiveTrustedBox.IsChecked == true,
            DisplayTrustedOnly = TelemetryDisplayTrustedBox.IsChecked == true,
            AllowRequestsFromAnyone = TelemetryRequestsAnyoneBox.IsChecked == true,
            AllowRequestsFromTrusted = TelemetryRequestsTrustedBox.IsChecked == true,
            RequestsOnlySendLatest = TelemetryLatestOnlyBox.IsChecked == true,
            UsePropagationOnly = TelemetryPropagatedBox.IsChecked == true,
            TryPropagationOnFail = TelemetryFallbackBox.IsChecked == true,
            Collector = string.IsNullOrWhiteSpace(TelemetryCollectorBox.Text) ? null : TelemetryPeer(TelemetryCollectorBox.Text),
            CollectorEnabled = TelemetryCollectorEnabledBox.IsChecked == true,
            SendToCollector = TelemetrySendCollectorBox.IsChecked == true,
            SendAllToCollector = TelemetrySendAllCollectorBox.IsChecked == true,
            RequestFromCollector = TelemetryRequestCollectorBox.IsChecked == true,
            SendIntervalSeconds = TelemetrySeconds(TelemetrySendIntervalBox.Text),
            RequestIntervalSeconds = TelemetrySeconds(TelemetryRequestIntervalBox.Text),
            ExcludedSources = Lines(TelemetryExcludedBox.Text).Select(TelemetryPeer).ToArray(),
            ManualSensors = Lines(TelemetryManualSensorsBox.Text).Select(ManualSensor).ToArray(),
            SendAppearance = TelemetryAppearanceBox.IsChecked == true,
            Appearance = new(TelemetryIconBox.Text ?? "", TelemetryForegroundBox.Text ?? "", TelemetryBackgroundBox.Text ?? "")
        };
        await _client.ConfigureTelemetryAsync(config, _windowCancellation.Token);
        _state = await _client.LoadAsync(_windowCancellation.Token);
        _telemetryPreview = null;
        TelemetryStatusText.Text = "Telemetry settings saved. Automatic policies run only while connected; collection remains manual.";
        RequestTelemetryRefresh();
    });

    private void RefreshLocalTelemetryClicked(object? sender, RoutedEventArgs e) => RunTelemetryOperation(async () =>
    {
        var report = await _client.RefreshLocalTelemetryAsync(_windowCancellation.Token);
        _telemetryPreview = report is null ? "Local telemetry is disabled." :
            "LOCAL MANUAL PREVIEW (not proof of storage or network delivery)\n" + FormatTelemetry(report);
        TelemetryDetailsText.Text = _telemetryPreview;
        TelemetryStatusText.Text = report is null ? "Local telemetry disabled." :
            "Local report prepared without hardware or network access. Connect before sending.";
        RequestTelemetryRefresh();
        if (_smokeUi && Program.SmokeTelemetryMode && report is not null)
        {
            Console.WriteLine("SMOKE_UI_PREPARED_TELEMETRY_SENSORS=" +
                string.Join(",", report.Sensors.Select(sensor => sensor.Id.ToString("x2", CultureInfo.InvariantCulture))));
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                TelemetryPage.ScrollToEnd();
                InvalidateVisual();
            });
        }
    });

    private void SaveConversationTelemetryClicked(object? sender, RoutedEventArgs e) => RunTelemetryOperation(async () =>
    {
        var peer = TelemetryPeer(TelemetryPeerBox.Text);
        _state = await _client.LoadAsync(_windowCancellation.Token);
        if (!_state.Contacts.Any(contact => contact.DeliveryHash == peer))
            throw new InvalidOperationException("Add this LXMF address as a contact before setting conversation permissions.");
        await _client.SetConversationTelemetryAsync(peer, ConversationSendTelemetryBox.IsChecked == true,
            ConversationAllowTelemetryRequestsBox.IsChecked == true, _windowCancellation.Token);
        TelemetryStatusText.Text = "Conversation telemetry permissions saved.";
    });

    private void SendTelemetryClicked(object? sender, RoutedEventArgs e) => RunTelemetryOperation(async () =>
    {
        var result = await _client.SendLatestTelemetryAsync(TelemetryPeer(TelemetryPeerBox.Text), _windowCancellation.Token);
        TelemetryStatusText.Text = $"Send admission: {result.Status}. Queued is not a delivery confirmation.";
    });

    private void RequestTelemetryClicked(object? sender, RoutedEventArgs e) => RunTelemetryOperation(async () =>
    {
        var result = await _client.RequestTelemetryAsync(TelemetryPeer(TelemetryPeerBox.Text), _windowCancellation.Token);
        TelemetryStatusText.Text = $"Request admission: {result.Status}. The peer's authorization and available reports decide the response.";
    });

    private void TrackTelemetryClicked(object? sender, RoutedEventArgs e) => RunTelemetryOperation(async () =>
    {
        var peer = TelemetryPeer(TelemetryPeerBox.Text);
        var interval = TelemetrySeconds(TelemetryTrackIntervalBox.Text);
        var duration = TelemetrySeconds(TelemetryTrackDurationBox.Text);
        _state = await _client.LoadAsync(_windowCancellation.Token);
        await _client.ConfigureTelemetryAsync(_state.Telemetry with
        {
            Tracking = _state.Telemetry.Tracking.Where(entry => entry.DeliveryHash != peer)
                .Append(new(peer, interval, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + duration)).ToArray()
        }, _windowCancellation.Token);
        TelemetryStatusText.Text = "Bounded tracking schedule saved; requests run while connected and stop at expiry.";
    });

    private void StopTrackingTelemetryClicked(object? sender, RoutedEventArgs e) => RunTelemetryOperation(async () =>
    {
        var peer = TelemetryPeer(TelemetryPeerBox.Text);
        _state = await _client.LoadAsync(_windowCancellation.Token);
        await _client.ConfigureTelemetryAsync(_state.Telemetry with
        {
            Tracking = _state.Telemetry.Tracking.Where(entry => entry.DeliveryHash != peer).ToArray()
        }, _windowCancellation.Token);
        TelemetryStatusText.Text = "This peer's tracking schedule removed.";
    });

    private void ClearTelemetryClicked(object? sender, RoutedEventArgs e) => RunTelemetryOperation(async () =>
    {
        if (ConfirmClearTelemetryBox.IsChecked != true)
            throw new InvalidOperationException("Confirm deletion before clearing a source's reports.");
        var report = SelectedTelemetry() ?? throw new InvalidOperationException("Select a stored report first.");
        await _client.ClearTelemetryAsync(report.SourceHash, _windowCancellation.Token);
        ConfirmClearTelemetryBox.IsChecked = false;
        _telemetryPreview = null;
        TelemetryDetailsText.Text = "";
        TelemetryStatusText.Text = "Selected source's stored reports deleted.";
        RequestTelemetryRefresh();
    });

    private void ShowTelemetryOnMapClicked(object? sender, RoutedEventArgs e) => RunTelemetryOperation(() =>
    {
        var report = SelectedTelemetry() ?? throw new InvalidOperationException("Select a stored report first.");
        var location = MapCoordinate(report) ??
            throw new InvalidOperationException("This selected report contains no location.");
        if (MapViewport.IsCartesian) MapSourceSelector.SelectedIndex = 0;
        _restoredMap = true;
        MapViewport.SetView(location.Latitude, location.Longitude, Math.Max(12, MapViewport.Zoom));
        NavigateTo("Map");
        return Task.CompletedTask;
    });

    private void RefreshTelemetryClicked(object? sender, RoutedEventArgs e) => RequestTelemetryRefresh();

    private void RequestTelemetryRefresh()
    {
        if (_closing) return;
        _refreshTelemetryAgain = true;
        if (_refreshingTelemetry) return;
        _refreshingTelemetry = true;
        RunTelemetryOperation(async () =>
        {
            try
            {
                do
                {
                    _refreshTelemetryAgain = false;
                    var selected = SelectedTelemetry();
                    _telemetryReports = await _client.GetTelemetryAsync(cancellationToken: _windowCancellation.Token);
                    TelemetryReportsList.ItemsSource = _telemetryReports.Select(report =>
                        $"{report.SourceHash} | source time {FormatValue(report.TimestampValue)} | {report.Report.Sensors.Count} sensors").ToArray();
                    var selectedIndex = selected is null ? -1 : _telemetryReports.ToList().FindIndex(report =>
                        report.SourceHash == selected.SourceHash && report.TimestampUtcSeconds == selected.TimestampUtcSeconds);
                    TelemetryReportsList.SelectedIndex = selectedIndex;
                    if (selectedIndex < 0) TelemetryDetailsText.Text = _telemetryPreview ?? "";
                    TelemetryReportsSummaryText.Text = $"{_telemetryReports.Count} visible reports from the newest bounded 256-record query; source-trust display policy is applied.";
                    await RefreshMapDiscoveryAsync();
                    UpdateMapMarkers();
                } while (_refreshTelemetryAgain && !_closing);
            }
            finally { _refreshingTelemetry = false; }
        });
    }

    private void UpdateMapMarkers()
    {
        var earliest = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - MapHistorySeconds();
        var groups = _telemetryReports.Where(report => report.TimestampUtcSeconds >= earliest &&
            (report.SourceHash != _state?.OwnDeliveryHash || _state.Telemetry.Enabled))
            .GroupBy(report => report.SourceHash).ToArray();
        var markers = new List<SituationMapMarker>();
        var diagnostics = new List<string>();
        var omitted = 0;
        SidebandTelemetryAppearance Appearance(string source) => source == _state?.OwnDeliveryHash
            ? _state.Telemetry.Appearance : _state?.Contacts.FirstOrDefault(contact => contact.DeliveryHash == source)?.Appearance ?? new();
        foreach (var group in groups)
        {
            var report = group.FirstOrDefault(report => MapCoordinate(report) is not null);
            if (report is not null)
            {
                var location = MapCoordinate(report) ??
                    throw new InvalidOperationException("The selected map report has no displayable location.");
                var contact = _state?.Contacts.FirstOrDefault(contact => contact.DeliveryHash == report.SourceHash);
                var name = contact?.DisplayName;
                var appearance = Appearance(report.SourceHash);
                var label = string.IsNullOrWhiteSpace(name) ? report.SourceHash[..8] :
                    name[..Math.Min(name.Length, 60)] + " " + report.SourceHash[..8];
                if (contact?.IsObject == true) label = "[Object] " + label;
                var marker = new SidebandMapOverlayMarker("peer:" + report.SourceHash, report.SourceHash, location,
                    SidebandMapOverlayAppearance.FromTelemetry(appearance), Name: label);
                markers.Add(new(label, location, SourceHash: report.SourceHash, Overlay: marker));
            }
        }
        foreach (var group in groups)
        {
            var report = group.First();
            var projection = SidebandMapOverlays.ProjectTelemetry(report.SourceHash, report.Report,
                SidebandMapOverlayAppearance.FromTelemetry(Appearance(report.SourceHash)),
                new(MapInterfacesBox.IsChecked == true, MapConnectionsBox.IsChecked == true,
                    MapHideBlockedBox.IsChecked == true, MapOnlyBlockedBox.IsChecked == true));
            foreach (var error in projection.Errors)
                if (diagnostics.Count < 256) diagnostics.Add($"{report.SourceHash[..8]} {error.Scope}: {error.Message}");
            foreach (var point in projection.Markers)
            {
                if (markers.Count == SidebandMapOverlays.MaximumMarkers) { omitted++; continue; }
                var marker = point with { Key = $"{report.SourceHash}:{point.Kind}:{point.Key}" };
                var name = point.Name ?? point.Kind.ToString();
                var label = name[..Math.Min(name.Length, 60)] + " " + report.SourceHash[..8];
                markers.Add(new(label, marker.Coordinate, SourceHash: marker.SourceHash, Overlay: marker));
            }
        }
        if (MapInterfacesBox.IsChecked == true && _client.IsConnected)
        {
            foreach (var marker in SidebandMapDiscovery.Project(_mapDiscovery))
            {
                if (markers.Count == SidebandMapOverlays.MaximumMarkers) { omitted++; continue; }
                var name = marker.Name ?? "Discovered interface";
                var label = "Discovery: " + name[..Math.Min(name.Length, 60)];
                markers.Add(new(label, marker.Coordinate, SourceHash: marker.SourceHash, Overlay: marker));
            }
        }
        MapViewport.SetMarkers(markers);
        MapOverlayStatusText.Text = $"{markers.Count} reported markers; {omitted} overlays omitted by the 256-marker limit. " +
            $"History window {MapHistorySeconds()} seconds. " +
            (_state?.MapFixedLocations?.Count > 0 ? $"{_state.MapFixedLocations.Count} local fixed-location overrides configured. " : "") +
            (diagnostics.Count > 0 ? $"{diagnostics.Count} projection diagnostics: " + string.Join(" | ", diagnostics.Take(4)) : "");
    }

    private SidebandGeoCoordinate? MapCoordinate(SidebandTelemetryStoredReport report)
    {
        var fixedLocation = report.SourceHash != _state?.OwnDeliveryHash
            ? _state?.MapFixedLocations?.FirstOrDefault(location => location.DeliveryHash == report.SourceHash) : null;
        if (fixedLocation is not null) return new(fixedLocation.Latitude, fixedLocation.Longitude, 0);
        var location = report.Report.Get(SidebandTelemetrySensorId.Location)?.Location;
        return location is null ? null : new(location.Latitude, location.Longitude, location.Altitude);
    }

    private SidebandTelemetryStoredReport? SelectedTelemetry() =>
        TelemetryReportsList.SelectedIndex is var index && index >= 0 && index < _telemetryReports.Count ? _telemetryReports[index] : null;

    private void TelemetrySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SelectedTelemetry() is not { } report)
        {
            TelemetryDetailsText.Text = _telemetryPreview ?? "";
            return;
        }
        _telemetryPreview = null;
        TelemetryPeerBox.Text = report.SourceHash;
        TelemetryDetailsText.Text = "Reported source: " + report.SourceHash +
            "\nA collector's asserted inner source is not independent proof of the original sender. Received.by/via below retain the observed provenance.\n" +
            FormatTelemetry(report.Report);
    }

    private void TelemetryPeerChanged(object? sender, TextChangedEventArgs e)
    {
        if (_state is null) return;
        var contact = _state?.Contacts.FirstOrDefault(peer =>
            SidebandContact.TryNormalizeDeliveryHash(TelemetryPeerBox.Text ?? "", out var hash) && peer.DeliveryHash == hash);
        ConversationSendTelemetryBox.IsChecked = contact?.SendTelemetry == true;
        ConversationAllowTelemetryRequestsBox.IsChecked = contact?.AllowTelemetryRequests == true;
    }

    private void UpdateTelemetryTrackingView() => TelemetryTrackingText.Text = _state?.Telemetry.Tracking.Count > 0
        ? string.Join(Environment.NewLine, _state.Telemetry.Tracking.Select(entry =>
            $"{entry.DeliveryHash} | {entry.IntervalSeconds}s interval | expiry {entry.UntilUtcSeconds.ToString("G17", CultureInfo.InvariantCulture)} UTC seconds"))
        : "No persisted tracking schedules.";

    private void RunTelemetryOperation(Func<Task> operation) => TrackOperation(async () =>
    {
        try { await operation(); }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            TelemetryStatusText.Text = "Telemetry operation failed: " + exception.Message;
            ShowError(TelemetryStatusText.Text);
        }
    });

    private static double TelemetryNumber(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value : throw new ArgumentException("Enter finite telemetry coordinates using a decimal point.");
    private static int TelemetrySeconds(string? text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value is >= 1 and <= 31_536_000
            ? value : throw new ArgumentException("Enter a whole-number interval from 1 to 31536000 seconds.");
    private static string TelemetryPeer(string? text) =>
        SidebandContact.TryNormalizeDeliveryHash(text ?? "", out var peer) ? peer :
            throw new ArgumentException("Telemetry requires a 32-character LXMF delivery hash.");
    private static string[] Lines(string? text) =>
        (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static SidebandTelemetryManualSensor ManualSensor(string text)
    {
        var parts = text.Split('=', 2);
        if (parts.Length != 2 || !byte.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
            throw new ArgumentException("Manual sensor lines require hexadecimal sensor-id=MessagePack-wire-hex.");
        _ = Convert.FromHexString(parts[1]);
        return new(id, parts[1]);
    }

    private static string FormatTelemetry(SidebandTelemetryReport report)
    {
        var text = new StringBuilder();
        foreach (var sensor in report.Sensors)
        {
            text.Append(sensor.Name).Append(" [0x").Append(sensor.Id.ToString("x2", CultureInfo.InvariantCulture))
                .Append(", ").Append(sensor.Status).Append("]: ").AppendLine(FormatValue(sensor.Data));
            if (sensor.Error is not null) text.AppendLine("  Error: " + sensor.Error);
        }
        return text.ToString();
    }

    private static string FormatValue(SidebandTelemetryValue value, int depth = 0)
    {
        if (depth > 4) return "[nested value; wire preserved]";
        var result = value.Kind switch
        {
            SidebandTelemetryValueKind.Nil => "null",
            SidebandTelemetryValueKind.Boolean => value.Boolean == true ? "true" : "false",
            SidebandTelemetryValueKind.Integer => value.Integer!.Value.ToString(CultureInfo.InvariantCulture),
            SidebandTelemetryValueKind.UnsignedInteger => value.UnsignedInteger!.Value.ToString(CultureInfo.InvariantCulture),
            SidebandTelemetryValueKind.Number => value.Number!.Value.ToString("G17", CultureInfo.InvariantCulture),
            SidebandTelemetryValueKind.Text => value.Text ?? "",
            SidebandTelemetryValueKind.Binary => Binary(value),
            SidebandTelemetryValueKind.Array => "[" + string.Join(", ", value.Items.Take(8).Select(item => FormatValue(item, depth + 1))) +
                (value.Items.Count > 8 ? ", [more entries; wire preserved]" : "") + "]",
            SidebandTelemetryValueKind.Map => "{" + string.Join(", ", value.Members.Take(8).Select(member =>
                FormatValue(member.Key, depth + 1) + ": " + FormatValue(member.Value, depth + 1))) +
                (value.Members.Count > 8 ? ", [more entries; wire preserved]" : "") + "}",
            _ => throw new InvalidDataException("Unknown telemetry value kind.")
        };
        return result.Length <= 512 ? result : result[..512] + " [display truncated; wire preserved]";

        static string Binary(SidebandTelemetryValue input)
        {
            var bytes = input.GetBinary()!;
            return Convert.ToHexStringLower(bytes.AsSpan(0, Math.Min(bytes.Length, 64))) +
                (bytes.Length > 64 ? $" [{bytes.Length} bytes; display truncated]" : "");
        }
    }
}
