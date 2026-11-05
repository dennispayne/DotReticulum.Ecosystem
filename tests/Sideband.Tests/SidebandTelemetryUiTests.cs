using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Sideband.Desktop;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandTelemetryUiTests
{
    private const string Peer = "00112233445566778899aabbccddeeff";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    [Trait("TestId", "T920")]
    public async Task PropagationSourceRequiresExplicitSelectionAndPersistsOfflineWithoutEnablingCollectionOrChangingDraft()
    {
        var path = TemporaryDirectory();
        MainWindow? window = null;
        try
        {
            window = new MainWindow(path);
            window.Show();
            await ReadyAsync(window);
            Click(window, "TelemetryNavButton");
            Assert.False(window.FindControl<CheckBox>("TelemetryPropagationBox")!.IsChecked);
            Assert.True(string.IsNullOrEmpty(window.FindControl<TextBox>("TelemetryPropagationIdentityBox")!.Text));
            window.FindControl<TextBox>("MessageBox")!.Text = "unsent draft remains owned by the composer";
            window.FindControl<CheckBox>("TelemetryPropagationBox")!.IsChecked = true;
            Click(window, "SaveTelemetryButton");
            await WaitForAsync(() => Status(window).StartsWith("Telemetry operation failed:", StringComparison.Ordinal));
            Assert.False((await ReadStateAsync(path)).Telemetry.PropagationEnabled);
            var identityPath = Path.Combine(path, "explicit-signing-identity");
            window.FindControl<TextBox>("TelemetryPropagationIdentityBox")!.Text = identityPath;
            Click(window, "SaveTelemetryButton");
            await WaitForAsync(() => Status(window).StartsWith("Telemetry settings saved.", StringComparison.Ordinal));
            var config = (await ReadStateAsync(path)).Telemetry;
            Assert.True(config.PropagationEnabled);
            Assert.Equal(identityPath, config.PropagationIdentityPath);
            Assert.False(config.Enabled);
            Assert.False(File.Exists(identityPath));
            Assert.Equal("unsent draft remains owned by the composer", window.FindControl<TextBox>("MessageBox")!.Text);
            Click(window, "RefreshPropagationTelemetryStatusButton");
            Assert.Contains("No propagation telemetry provider is active", window.FindControl<TextBlock>("TelemetryPropagationStatusText")!.Text);
            Assert.DoesNotContain(identityPath, window.FindControl<TextBlock>("TelemetryPropagationStatusText")!.Text);
            Assert.DoesNotContain(identityPath, Status(window));
            await window.CloseAndWaitAsync();
            window = new MainWindow(path);
            window.Show();
            await ReadyAsync(window);
            Assert.True(window.FindControl<CheckBox>("TelemetryPropagationBox")!.IsChecked);
            Assert.Equal(identityPath, window.FindControl<TextBox>("TelemetryPropagationIdentityBox")!.Text);
            Assert.False(window.FindControl<CheckBox>("TelemetryEnabledBox")!.IsChecked);
            Assert.False(File.Exists(identityPath));
        }
        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            Directory.Delete(path, true);
        }
    }

    [AvaloniaFact]
    public async Task BatteryPreferencePersistsWithoutImplicitlyEnablingCollectionOrOpeningDevices()
    {
        var path = TemporaryDirectory();
        MainWindow? window = null;
        try
        {
            window = new MainWindow(path);
            window.Show();
            await ReadyAsync(window);
            Click(window, "TelemetryNavButton");
            Assert.False(window.FindControl<CheckBox>("TelemetryBatteryBox")!.IsChecked);
            Assert.Contains("Linux", window.FindControl<CheckBox>("TelemetryBatteryBox")!.Content!.ToString());
            window.FindControl<CheckBox>("TelemetryBatteryBox")!.IsChecked = true;
            Click(window, "SaveTelemetryButton");
            await WaitForAsync(() => Status(window).StartsWith("Telemetry settings saved.", StringComparison.Ordinal));
            Assert.True((await ReadStateAsync(path)).Telemetry.BatteryEnabled);
            Assert.False((await ReadStateAsync(path)).Telemetry.Enabled);
            await window.CloseAndWaitAsync();
            window = new MainWindow(path);
            window.Show();
            await ReadyAsync(window);
            Assert.True(window.FindControl<CheckBox>("TelemetryBatteryBox")!.IsChecked);
            Assert.False(window.FindControl<CheckBox>("TelemetryEnabledBox")!.IsChecked);
        }
        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            Directory.Delete(path, true);
        }
    }

    [AvaloniaFact]
    public async Task ExplicitMqttSettingsAreMaskedWarnAboutPlainTcpAndPersistWithoutOpeningHardware()
    {
        var path = TemporaryDirectory();
        MainWindow? window = null;
        try
        {
            window = new MainWindow(path);
            window.Show();
            await ReadyAsync(window);
            Click(window, "TelemetryNavButton");
            Assert.False(window.FindControl<CheckBox>("TelemetryMqttEnabledBox")!.IsChecked);
            var warning = window.FindControl<TextBlock>("TelemetryMqttWarningText")!.Text!;
            Assert.Contains("plain TCP", warning);
            Assert.Contains("private locations", warning);
            Assert.Contains("unchanged", warning);
            window.FindControl<TextBox>("TelemetryMqttHostBox")!.Text = "example.invalid";
            window.FindControl<TextBox>("TelemetryMqttPortBox")!.Text = "1884";
            window.FindControl<TextBox>("TelemetryMqttUserBox")!.Text = "synthetic-private-user";
            window.FindControl<TextBox>("TelemetryMqttPasswordBox")!.Text = "synthetic-private-password";
            Assert.Equal('*', window.FindControl<TextBox>("TelemetryMqttPasswordBox")!.PasswordChar);
            window.FindControl<CheckBox>("TelemetryMqttEnabledBox")!.IsChecked = true;
            Click(window, "SaveTelemetryButton");
            await WaitForAsync(() => Status(window).StartsWith("Telemetry settings saved.", StringComparison.Ordinal));
            var settings = (await ReadStateAsync(path)).Telemetry;
            Assert.True(settings.Mqtt.Enabled);
            Assert.False(settings.Enabled);
            Assert.Equal(1884, settings.Mqtt.Port);
            Assert.DoesNotContain("synthetic-private", Status(window));
            await window.CloseAndWaitAsync();
            window = new MainWindow(path);
            window.Show();
            await ReadyAsync(window);
            Assert.True(window.FindControl<CheckBox>("TelemetryMqttEnabledBox")!.IsChecked);
            Assert.Equal("1884", window.FindControl<TextBox>("TelemetryMqttPortBox")!.Text);
            Assert.Equal("synthetic-private-password", window.FindControl<TextBox>("TelemetryMqttPasswordBox")!.Text);
        }
        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            Directory.Delete(path, true);
        }
    }

    [AvaloniaFact]
    public async Task RuntimeOptInAndTableDisclosurePersistWhileOfflineCollectionIsExplicitlyUnavailable()
    {
        var path = TemporaryDirectory();
        MainWindow? window = null;
        try
        {
            window = new MainWindow(path);
            window.Show();
            await ReadyAsync(window);
            Click(window, "TelemetryNavButton");
            Assert.False(window.FindControl<CheckBox>("TelemetryRnsTransportBox")!.IsChecked);
            Assert.False(window.FindControl<CheckBox>("TelemetryRnsTransportTablesBox")!.IsChecked);
            window.FindControl<CheckBox>("TelemetryEnabledBox")!.IsChecked = true;
            window.FindControl<CheckBox>("TelemetryRnsTransportBox")!.IsChecked = true;
            window.FindControl<CheckBox>("TelemetryRnsTransportTablesBox")!.IsChecked = true;
            Click(window, "SaveTelemetryButton");
            await WaitForAsync(() => Status(window).StartsWith("Telemetry settings saved.", StringComparison.Ordinal));
            Click(window, "RefreshLocalTelemetryButton");
            await WaitForAsync(() => window.FindControl<TextBox>("TelemetryDetailsText")!.Text!.Contains("rns_transport", StringComparison.Ordinal));
            Assert.Contains("Unavailable", window.FindControl<TextBox>("TelemetryDetailsText")!.Text);
            Assert.Contains("Connect before collecting", window.FindControl<TextBox>("TelemetryDetailsText")!.Text);
            await window.CloseAndWaitAsync();
            window = new MainWindow(path);
            window.Show();
            await ReadyAsync(window);
            Click(window, "TelemetryNavButton");
            Assert.True(window.FindControl<CheckBox>("TelemetryRnsTransportBox")!.IsChecked);
            Assert.True(window.FindControl<CheckBox>("TelemetryRnsTransportTablesBox")!.IsChecked);
        }
        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            Directory.Delete(path, true);
        }
    }

    [AvaloniaFact]
    public async Task ManualCollectionPrivacyPermissionsTrackingAndAdmissionPersistWithoutConnecting()
    {
        var path = TemporaryDirectory();
        MainWindow? window = null;
        try
        {
            var store = new SidebandStateStore(path);
            await store.AddContactAsync(SidebandContact.Create("Telemetry peer", Peer), Token);
            window = new MainWindow(path);
            window.Show();
            await ReadyAsync(window);
            Click(window, "TelemetryNavButton");
            Assert.False(window.FindControl<CheckBox>("TelemetryEnabledBox")!.IsChecked);
            window.FindControl<CheckBox>("TelemetryEnabledBox")!.IsChecked = true;
            window.FindControl<CheckBox>("TelemetryInformationEnabledBox")!.IsChecked = true;
            window.FindControl<TextBox>("TelemetryInformationBox")!.Text = "Explicitly supplied acceptance information";
            window.FindControl<CheckBox>("TelemetryFixedLocationBox")!.IsChecked = true;
            window.FindControl<TextBox>("TelemetryLatitudeBox")!.Text = "51.5";
            window.FindControl<TextBox>("TelemetryLongitudeBox")!.Text = "-0.125";
            window.FindControl<TextBox>("TelemetryAltitudeBox")!.Text = "120";
            window.FindControl<CheckBox>("TelemetryReceiveTrustedBox")!.IsChecked = true;
            Click(window, "SaveTelemetryButton");
            await WaitForAsync(() => Status(window).StartsWith("Telemetry settings saved.", StringComparison.Ordinal));
            var state = await ReadStateAsync(path);
            Assert.True(state.Telemetry.Enabled);
            Assert.True(state.Telemetry.ReceiveTrustedOnly);
            Assert.Equal(51.5, state.Telemetry.FixedLocation.Latitude);
            Assert.Empty(state.Messages);
            Assert.Null(state.OwnDeliveryHash);
            Click(window, "RefreshLocalTelemetryButton");
            await WaitForAsync(() => window.FindControl<TextBox>("TelemetryDetailsText")!.Text!.Contains("Explicitly supplied acceptance information", StringComparison.Ordinal));
            Assert.Contains("LOCAL MANUAL PREVIEW", window.FindControl<TextBox>("TelemetryDetailsText")!.Text);
            Assert.Contains("location [0x02", window.FindControl<TextBox>("TelemetryDetailsText")!.Text);
            window.FindControl<TextBox>("TelemetryPeerBox")!.Text = Peer;
            window.FindControl<CheckBox>("ConversationSendTelemetryBox")!.IsChecked = true;
            window.FindControl<CheckBox>("ConversationAllowTelemetryRequestsBox")!.IsChecked = true;
            Click(window, "SaveConversationTelemetryButton");
            await WaitForAsync(() => Status(window).StartsWith("Conversation telemetry permissions saved.", StringComparison.Ordinal));
            var contact = Assert.Single((await ReadStateAsync(path)).Contacts);
            Assert.True(contact.SendTelemetry);
            Assert.True(contact.AllowTelemetryRequests);
            Click(window, "SendTelemetryButton");
            await WaitForAsync(() => Status(window).StartsWith("Send admission: NotConnected.", StringComparison.Ordinal));
            Click(window, "RequestTelemetryButton");
            await WaitForAsync(() => Status(window).StartsWith("Request admission: NotConnected.", StringComparison.Ordinal));
            Click(window, "TrackTelemetryButton");
            await WaitForAsync(() => Status(window).StartsWith("Bounded tracking schedule saved;", StringComparison.Ordinal));
            Assert.Equal(Peer, Assert.Single((await ReadStateAsync(path)).Telemetry.Tracking).DeliveryHash);
            await window.CloseAndWaitAsync();
            window = new MainWindow(path);
            window.Show();
            await ReadyAsync(window);
            Click(window, "TelemetryNavButton");
            Assert.True(window.FindControl<CheckBox>("TelemetryEnabledBox")!.IsChecked);
            Assert.True(window.FindControl<CheckBox>("TelemetryReceiveTrustedBox")!.IsChecked);
            Assert.Contains(Peer, window.FindControl<TextBlock>("TelemetryTrackingText")!.Text);
            Click(window, "StopTrackingTelemetryButton");
            await WaitForAsync(() => Status(window).StartsWith("This peer's tracking schedule removed.", StringComparison.Ordinal));
            Assert.Empty((await ReadStateAsync(path)).Telemetry.Tracking);
            window.FindControl<TextBox>("TelemetryLatitudeBox")!.Text = "NaN";
            Click(window, "SaveTelemetryButton");
            await WaitForAsync(() => Status(window).StartsWith("Telemetry operation failed:", StringComparison.Ordinal));
            Assert.Equal(51.5, (await ReadStateAsync(path)).Telemetry.FixedLocation.Latitude);
            await window.CloseAndWaitAsync();
        }
        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            Directory.Delete(path, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task StoredReportsShowProvenanceMapMarkersRespectTrustAndConfirmedDeletionSurvivesRestart()
    {
        var path = TemporaryDirectory();
        MainWindow? window = null;
        try
        {
            var store = new SidebandStateStore(path);
            await store.AddContactAsync(SidebandContact.Create("Reported position", Peer), Token);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var report = SidebandTelemetryCodec.Create(now,
                SidebandTelemetryCodec.FromLocation(new(51.5, -0.125, 120, 0, 0, 10, now)));
            await using (var telemetry = await SidebandTelemetryStore.OpenAsync(Path.Combine(path, "telemetry"), cancellationToken: Token))
                Assert.Equal(SidebandTelemetryAppendResult.Added,
                    await telemetry.AppendAsync(Peer, SidebandTelemetryCodec.Encode(report), now, Token));
            window = new MainWindow(path);
            window.Show();
            await ReadyAsync(window);
            Click(window, "TelemetryNavButton");
            await WaitForAsync(() => window.FindControl<ListBox>("TelemetryReportsList")!.ItemCount == 1);
            window.FindControl<ListBox>("TelemetryReportsList")!.SelectedIndex = 0;
            Assert.Contains("collector's asserted inner source", window.FindControl<TextBox>("TelemetryDetailsText")!.Text);
            Click(window, "ShowTelemetryOnMapButton");
            await WaitForAsync(() => window.FindControl<SituationMap>("MapViewport")!.MarkerCount == 1);
            Assert.Equal(51.5, window.FindControl<SituationMap>("MapViewport")!.Latitude);
            Assert.False(window.FindControl<CheckBox>("MapOnlineConsentBox")!.IsChecked);
            Click(window, "TelemetryNavButton");
            window.FindControl<CheckBox>("TelemetryDisplayTrustedBox")!.IsChecked = true;
            Click(window, "SaveTelemetryButton");
            await WaitForAsync(() => window.FindControl<ListBox>("TelemetryReportsList")!.ItemCount == 0);
            Assert.Equal(0, window.FindControl<SituationMap>("MapViewport")!.MarkerCount);
            Assert.Equal("", window.FindControl<TextBox>("TelemetryDetailsText")!.Text);
            window.FindControl<CheckBox>("TelemetryDisplayTrustedBox")!.IsChecked = false;
            Click(window, "SaveTelemetryButton");
            await WaitForAsync(() => window.FindControl<ListBox>("TelemetryReportsList")!.ItemCount == 1);
            window.FindControl<ListBox>("TelemetryReportsList")!.SelectedIndex = 0;
            Click(window, "ClearTelemetryButton");
            await WaitForAsync(() => Status(window).StartsWith("Telemetry operation failed: Confirm deletion", StringComparison.Ordinal));
            Assert.Equal(1, window.FindControl<ListBox>("TelemetryReportsList")!.ItemCount);
            window.FindControl<CheckBox>("ConfirmClearTelemetryBox")!.IsChecked = true;
            Click(window, "ClearTelemetryButton");
            await WaitForAsync(() => window.FindControl<ListBox>("TelemetryReportsList")!.ItemCount == 0);
            await window.CloseAndWaitAsync();
            window = new MainWindow(path);
            window.Show();
            await ReadyAsync(window);
            Click(window, "TelemetryNavButton");
            await WaitForAsync(() => window.FindControl<TextBlock>("TelemetryReportsSummaryText")!.Text?.StartsWith("0 visible reports", StringComparison.Ordinal) == true);
            Assert.Empty((await ReadStateAsync(path)).Messages);
            await window.CloseAndWaitAsync();
        }
        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            Directory.Delete(path, recursive: true);
        }
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sideband-telemetry-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
    private static Task<SidebandState> ReadStateAsync(string path) =>
        new SidebandStateStore(path).LoadAsync(cancellationToken: Token);
    private static string Status(MainWindow window) => window.FindControl<TextBlock>("TelemetryStatusText")!.Text ?? "";
    private static void Click(MainWindow window, string name) =>
        window.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static Task ReadyAsync(MainWindow window) =>
        WaitForAsync(() => window.FindControl<TextBlock>("StatusText")!.Text?.Contains("Offline · local data:", StringComparison.Ordinal) == true);
    private static async Task WaitForAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The telemetry desktop workflow did not reach its expected state.");
            await Task.Delay(20, Token);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
