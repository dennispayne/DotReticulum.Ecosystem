using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Sideband.Desktop;
using System.Text.Json;
using LXST;

[assembly: AvaloniaTestApplication(typeof(Sideband.Tests.TestAppBuilder))]

namespace Sideband.Tests;

public sealed class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

[Trait("Category", "Integration")]
public sealed class SidebandHeadlessUiTests
{
    public static bool MediaNativeIsDisabled => Environment.GetEnvironmentVariable("DOTLXST_NATIVE") != "1";

    [AvaloniaFact]
    public async Task ImageSelectionExportCorruptionAndRestartNeverOpenAudioDevices()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "sideband-media-ui-" + Guid.NewGuid().ToString("N"));
        const string peer = "00112233445566778899aabbccddeeff";
        MainWindow? window = null;
        try
        {
            using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-media.json")));
            var bytes = Convert.FromBase64String(fixture.RootElement.GetProperty("images")[0].GetProperty("encoded").GetString()!);
            var image = await SeedMediaAsync(path, "image.webp", bytes);
            var store = new SidebandStateStore(path);
            await store.AddContactAsync(SidebandContact.Create("Image peer", peer));
            await store.AddMessageAsync(new(Guid.NewGuid(), peer, true, "", "Reference image",
                DateTimeOffset.UtcNow, "Received", Image: new("webp", image)));
            var backend = new UiMediaBackend();
            window = new MainWindow(path, Path.Combine(path, "isolated-rns"), mediaBackend: backend);
            window.Show();
            await WaitForAsync(() => window.FindControl<ListBox>("MessageList")?.ItemCount == 1);
            Assert.Equal(0, backend.Opens);
            Assert.Null(window.FindControl<Image>("MediaPreview")!.Source);
            window.FindControl<ListBox>("MessageList")!.SelectedIndex = 0;
            await WaitForAsync(() => window.FindControl<Image>("MediaPreview")?.Source is not null);
            Assert.Contains("Image: webp", Assert.Single(window.FindControl<ListBox>("MessageList")!.ItemsSource!.Cast<string>()));
            await using (var client = new SidebandClient(store))
            {
                var message = Assert.Single(await client.GetMessagesAsync(peer));
                var export = Path.Combine(path, "export.webp");
                await client.ExportMediaAsync(peer, message.Id, false, export, TestContext.Current.CancellationToken);
                Assert.Equal(bytes, await File.ReadAllBytesAsync(export));
                await File.WriteAllBytesAsync(Path.Combine(path, "attachments", image.ContentHash), "corrupt"u8.ToArray());
            }
            await window.CloseAndWaitAsync();
            window = new MainWindow(path, Path.Combine(path, "isolated-rns"), mediaBackend: backend);
            window.Show();
            await WaitForAsync(() => window.FindControl<ListBox>("MessageList")?.ItemCount == 1);
            window.FindControl<ListBox>("MessageList")!.SelectedIndex = 0;
            await WaitForAsync(() => window.FindControl<TextBlock>("StatusText")?.Text?.StartsWith("Could not display image:", StringComparison.Ordinal) == true);
            Assert.Null(window.FindControl<Image>("MediaPreview")!.Source);
            Assert.Equal(0, backend.Opens);
            await window.CloseAndWaitAsync();
        }
        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    [AvaloniaFact(SkipWhen = nameof(MediaNativeIsDisabled), Skip = "Sideband audio UI requires DOTLXST_NATIVE=1 and installed native codecs; devices are fakes.")]
    public async Task ExplicitAudioPlaybackRecordingCancellationAndCloseOwnTheirDeviceLeases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "sideband-audio-ui-" + Guid.NewGuid().ToString("N"));
        const string peer = "00112233445566778899aabbccddeeff";
        MainWindow? window = null;
        try
        {
            using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-media.json")));
            var bytes = Convert.FromBase64String(fixture.RootElement.GetProperty("opus").GetProperty("encoded").GetString()!);
            var data = await SeedMediaAsync(path, "audio.ogg", bytes);
            var store = new SidebandStateStore(path);
            await store.AddContactAsync(SidebandContact.Create("Audio peer", peer));
            await store.AddMessageAsync(new(Guid.NewGuid(), peer, true, "", "Reference audio",
                DateTimeOffset.UtcNow, "Received", Audio: new(SidebandMediaCodec.OpusOggMode, data)));
            var backend = new UiMediaBackend();
            window = new MainWindow(path, Path.Combine(path, "isolated-rns"), mediaBackend: backend);
            window.Show();
            await WaitForAsync(() => window.FindControl<ListBox>("MessageList")?.ItemCount == 1);
            window.FindControl<ListBox>("MessageList")!.SelectedIndex = 0;
            Assert.Equal(0, backend.Opens);
            window.FindControl<Button>("PlayAudioButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => backend.Opens == 1);
            window.FindControl<Button>("StopAudioButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => backend.Disposals == 1);
            await WaitForAsync(() => window.FindControl<Button>("RecordAudioButton")?.IsEnabled == true);
            window.FindControl<Button>("RecordAudioButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => backend.Opens == 2);
            window.FindControl<Button>("StopAudioButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => backend.Disposals == 2);
            await WaitForAsync(() => window.FindControl<Button>("RecordAudioButton")?.IsEnabled == true);
            Assert.Equal(string.Empty, window.FindControl<TextBlock>("RecordingStatusText")!.Text);
            window.FindControl<Button>("RecordAudioButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => backend.Opens == 3);
            await window.CloseAndWaitAsync();
            Assert.Equal(3, backend.Disposals);
        }
        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    private static async Task<SidebandAttachment> SeedMediaAsync(string path, string name, byte[] bytes)
    {
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        Directory.CreateDirectory(Path.Combine(path, "attachments"));
        await File.WriteAllBytesAsync(Path.Combine(path, "attachments", hash), bytes, TestContext.Current.CancellationToken);
        return new(name, hash, bytes.Length);
    }

    private sealed class UiMediaBackend : IAudioDeviceBackend
    {
        internal int Opens;
        internal int Disposals;
        public ValueTask<IReadOnlyList<AudioDeviceInfo>> ListInputsAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The UI must not enumerate devices automatically.");
        public ValueTask<IReadOnlyList<AudioDeviceInfo>> ListOutputsAsync(CancellationToken cancellationToken = default) =>
            ListInputsAsync(cancellationToken);
        public ValueTask<IAudioCapture> OpenCaptureAsync(string? device, AudioDeviceFormat format, CancellationToken cancellationToken = default)
        {
            Opens++;
            return ValueTask.FromResult<IAudioCapture>(new Capture(this));
        }
        public ValueTask<IAudioPlayback> OpenPlaybackAsync(string? device, AudioDeviceFormat format, CancellationToken cancellationToken = default)
        {
            Opens++;
            return ValueTask.FromResult<IAudioPlayback>(new Playback(this));
        }
        private sealed class Capture(UiMediaBackend owner) : IAudioCapture
        {
            public async IAsyncEnumerable<AudioFrame> CaptureAsync(
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                yield break;
            }
            public ValueTask DisposeAsync() { owner.Disposals++; return ValueTask.CompletedTask; }
        }
        private sealed class Playback(UiMediaBackend owner) : IAudioPlayback
        {
            public async ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken = default) =>
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            public ValueTask DisposeAsync() { owner.Disposals++; return ValueTask.CompletedTask; }
        }
    }

    [AvaloniaFact]
    public async Task NavigationPreferencesAndConnectivityUseSavedApplicationState()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "sideband-ui-test-" + Guid.NewGuid().ToString("N"));
        var rnsConfig = Path.Combine(path, "isolated-rns");
        MainWindow? window = null;
        try
        {
            Directory.CreateDirectory(rnsConfig);
            await File.WriteAllTextAsync(Path.Combine(rnsConfig, "config"),
                "[reticulum]\n  enable_transport = no\n  share_instance = no\n\n[logging]\n  loglevel = 4\n");
            window = new MainWindow(path, rnsConfig);
            window.Show();
            await WaitForAsync(() => window.FindControl<TextBox>("DisplayNameBox")?.Text == "Sideband");

            Assert.True(window.IsVisible);
            Assert.NotNull(window.FindControl<ListBox>("ContactList"));
            Assert.NotNull(window.FindControl<ListBox>("MessageList"));
            Assert.True(window.FindControl<Button>("VoiceNavButton")!.IsEnabled);
            window.FindControl<Button>("VoiceNavButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(window.FindControl<ScrollViewer>("VoicePage")!.IsVisible);
            Assert.False(window.FindControl<CheckBox>("VoiceEnabledBox")!.IsChecked);
            Assert.False(window.FindControl<Button>("CallIdentityButton")!.IsEnabled);
            Assert.True(window.FindControl<Button>("SendButton")!.IsEnabled);
            window.FindControl<Button>("ContactsNavButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(window.FindControl<ScrollViewer>("ContactsPage")!.IsVisible);

            var connectivityNav = window.FindControl<Button>("ConnectivityNavButton")!;
            connectivityNav.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(window.FindControl<ScrollViewer>("ConnectivityPage")!.IsVisible);
            Assert.Contains(rnsConfig, window.FindControl<TextBlock>("EffectiveConfigPathText")!.Text);
            Assert.Contains("unknown until a connection", window.FindControl<TextBlock>("RuntimeModeText")!.Text);
            window.FindControl<TextBox>("PropagationNodeBox")!.Text = "00112233445566778899aabbccddeeff";
            window.FindControl<CheckBox>("PropagationDefaultBox")!.IsChecked = true;
            window.FindControl<Button>("SavePropagationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("SyncStatusText")?.Text?.StartsWith("Propagation settings saved", StringComparison.Ordinal) == true);
            var propagationState = await new SidebandStateStore(path).LoadAsync();
            Assert.Equal("00112233445566778899aabbccddeeff", propagationState.PropagationNode);
            Assert.True(propagationState.PropagationByDefault);
            window.FindControl<Button>("ConnectButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<Button>("DisconnectButton")?.IsEnabled == true);
            Assert.Contains("standalone Reticulum node", window.FindControl<TextBlock>("RuntimeModeText")!.Text);
            Assert.Contains(Path.Combine(rnsConfig, "config"), window.FindControl<TextBlock>("EffectiveConfigPathText")!.Text);
            Assert.Contains("Public identity hash:", window.FindControl<TextBlock>("IdentityHashText")!.Text);
            Assert.Contains("LXMF delivery address:", window.FindControl<TextBlock>("DeliveryHashText")!.Text);
            window.FindControl<Button>("DisconnectButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<Button>("ConnectButton")?.IsEnabled == true);

            var preferencesNav = window.FindControl<Button>("PreferencesNavButton")!;
            preferencesNav.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.FindControl<TextBox>("DisplayNameBox")!.Text = "Alice";
            window.FindControl<ComboBox>("ThemeSelector")!.SelectedItem = "Light";
            window.FindControl<Button>("SavePreferencesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("PreferencesStatusText")?.Text == "Preferences saved.");

            var state = await new SidebandStateStore(path).LoadAsync();
            Assert.Equal("Alice", state.DisplayName);
            Assert.Equal("Light", state.Theme);
            Assert.Equal(Path.GetFullPath(rnsConfig), state.ReticulumConfigDirectory);
            Assert.Equal(ThemeVariant.Light, Application.Current!.RequestedThemeVariant);

            window.FindControl<TextBox>("DisplayNameBox")!.Text = string.Empty;
            window.FindControl<Button>("SavePreferencesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("PreferencesStatusText")?.Text?.Contains("displayName") == true);
            Assert.Contains("Could not save preferences", window.FindControl<TextBlock>("StatusText")!.Text);

            await window.CloseAndWaitAsync();
        }
        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task ConversationTrustUnreadAndReadingSurviveWindowRestart()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "sideband-conversation-ui-" + Guid.NewGuid().ToString("N"));
        const string peer = "00112233445566778899aabbccddeeff";
        MainWindow? window = null;
        try
        {
            var store = new SidebandStateStore(path);
            await store.AddContactAsync(SidebandContact.Create("Policy peer", peer));
            await store.AddMessageAsync(new SidebandChatMessage(Guid.NewGuid(), peer, true, "", "Unread note",
                DateTimeOffset.UtcNow, "Received"));
            window = new MainWindow(path, Path.Combine(path, "isolated-rns"));
            window.Show();
            await WaitForAsync(() => window.FindControl<ListBox>("MessageList")?.ItemCount == 1);
            window.Activate();
            await WaitForAsync(() => window.IsActive);
            await WaitForAsync(() => window.FindControl<ListBox>("ContactList")?.ItemsSource?.Cast<string>().All(
                item => !item.Contains("[Unread]", StringComparison.Ordinal)) == true);
            window.FindControl<CheckBox>("TrustConversationBox")!.IsChecked = true;
            window.FindControl<Button>("SaveConversationTrustButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("StatusText")?.Text?.StartsWith("Conversation trust saved", StringComparison.Ordinal) == true);
            window.FindControl<Button>("MarkConversationUnreadButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("StatusText")?.Text == "Conversation marked unread.");
            Assert.True(window.FindControl<ScrollViewer>("ContactsPage")!.IsVisible);
            Assert.Contains("[Unread]", Assert.Single(window.FindControl<ListBox>("ExpandedContactList")!.ItemsSource!.Cast<string>()));
            await window.CloseAndWaitAsync();
            var persisted = Assert.Single((await new SidebandStateStore(path).LoadAsync()).Contacts);
            Assert.True(persisted.Trusted);
            Assert.True(persisted.Unread);
            window = new MainWindow(path, Path.Combine(path, "isolated-rns"));
            window.Show();
            window.Activate();
            await WaitForAsync(() => window.FindControl<CheckBox>("TrustConversationBox")?.IsChecked == true);
            await WaitForAsync(() => window.FindControl<ListBox>("ContactList")?.ItemsSource?.Cast<string>().All(
                item => !item.Contains("[Unread]", StringComparison.Ordinal)) == true);
            await window.CloseAndWaitAsync();
            Assert.False(Assert.Single((await new SidebandStateStore(path).LoadAsync()).Contacts).Unread);
        }
        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task WindowPresentsChatControlsAndReportsSendWithoutSelection()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "sideband-ui-test-" + Guid.NewGuid().ToString("N"));
        MainWindow? window = null;
        try
        {
            window = new MainWindow(path, Path.Combine(path, "isolated-rns"));
            window.Show();
            var status = window.FindControl<TextBlock>("StatusText")!;
            var send = window.FindControl<Button>("SendButton")!;
            send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal("Select a contact before sending.", status.Text);
            await window.CloseAndWaitAsync();
        }
        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task ClosingDuringInitialStateSaveWaitsForStartupAndLeavesNoTemporaryFile()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "sideband-ui-test-" + Guid.NewGuid().ToString("N"));
        MainWindow? window = null;
        try
        {
            window = new MainWindow(path, Path.Combine(path, "isolated-rns"));
            window.Show();
            await window.CloseAndWaitAsync();

            Assert.False(window.IsVisible);
            Assert.Empty(Directory.GetFiles(path, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task ClosingWaitsForAnInFlightPreferencesSave()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "sideband-ui-test-" + Guid.NewGuid().ToString("N"));
        var rnsConfig = Path.Combine(path, "isolated-rns");
        MainWindow? window = null;
        try
        {
            Directory.CreateDirectory(path);
            var messages = Enumerable.Range(0, 120)
                .Select(index => new Sideband.SidebandChatMessage(
                    Guid.NewGuid(),
                    "aabbccddeeff00112233445566778899",
                    false,
                    "",
                    new string('x', 50_000),
                    DateTimeOffset.UnixEpoch.AddSeconds(index),
                    "Delivered"))
                .ToArray();
            var state = new Sideband.SidebandState(
                Sideband.SidebandState.CurrentSchemaVersion,
                rnsConfig,
                null,
                [],
                messages);
            var stateStore = new Sideband.SidebandStateStore(path);
            await File.WriteAllTextAsync(stateStore.StatePath, JsonSerializer.Serialize(state));

            window = new MainWindow(path, rnsConfig);
            window.Show();
            await WaitForAsync(() => window.FindControl<TextBox>("DisplayNameBox")?.Text == "Sideband");
            window.FindControl<TextBox>("DisplayNameBox")!.Text = "Alice";
            window.FindControl<ComboBox>("ThemeSelector")!.SelectedItem = "Light";
            window.FindControl<Button>("SavePreferencesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            await window.CloseAndWaitAsync();

            var persisted = await new Sideband.SidebandStateStore(path).LoadAsync();
            Assert.Equal("Alice", persisted.DisplayName);
            Assert.Equal("Light", persisted.Theme);
            Assert.Empty(Directory.GetFiles(path, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task VoiceSettingsDevicesTrustAndDialCancellationWorkWithoutOpeningCapture()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "sideband-ui-call-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(root, "rns");
        var token = TestContext.Current.CancellationToken;
        var devices = new SidebandVoiceLifecycleTests.Devices();
        MainWindow? window = null;
        try
        {
            Directory.CreateDirectory(config);
            await File.WriteAllTextAsync(Path.Combine(config, "config"),
                "[reticulum]\n  enable_transport = no\n  share_instance = no\n\n[logging]\n  loglevel = 4\n", token);
            var store = new SidebandStateStore(root);
            var delivery = new string('b', 32);
            await store.AddContactAsync(SidebandContact.Create("Call peer", delivery, trusted: true), token);
            await store.AddCallEventAsync(new(DateTimeOffset.UtcNow, "incoming-missed", new string('a', 32)), token);
            window = new MainWindow(root, config, mediaBackend: devices);
            window.Show();
            await WaitForAsync(() => window.FindControl<TextBox>("DisplayNameBox")?.Text == "Sideband");
            window.FindControl<Button>("VoiceNavButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.FindControl<CheckBox>("VoiceEnabledBox")!.IsChecked = true;
            window.FindControl<CheckBox>("VoiceTrustedOnlyBox")!.IsChecked = true;
            window.FindControl<CheckBox>("VoiceDirectPttBox")!.IsChecked = true;
            window.FindControl<TextBox>("VoiceMicrophoneBox")!.Text = "fake-input";
            window.FindControl<TextBox>("VoiceSpeakerBox")!.Text = "fake-output";
            window.FindControl<Button>("SaveVoiceButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("VoiceStatusText")!.Text!.Contains("connect to Reticulum", StringComparison.Ordinal));
            window.FindControl<CheckBox>("ConversationDirectPttBox")!.IsChecked = true;
            window.FindControl<Button>("SaveConversationPttButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.FindControl<Button>("ConnectButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<Button>("CallIdentityButton")!.IsEnabled);
            window.FindControl<Button>("ListVoiceDevicesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBox>("VoiceDevicesText")!.Text?.Contains("fake-input", StringComparison.Ordinal) == true);
            Assert.Contains("fake-output", window.FindControl<TextBox>("VoiceDevicesText")!.Text);
            Assert.Equal(0, devices.Captures);
            Assert.Equal(0, devices.Playbacks);
            var peer = new string('a', 32);
            window.FindControl<TextBox>("VoiceIdentityBox")!.Text = peer;
            window.FindControl<CheckBox>("VoicePeerTrustedBox")!.IsChecked = true;
            window.FindControl<Button>("SaveVoicePeerTrustButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(async () => (await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).VoicePeers?.Count == 1);
            window.FindControl<Button>("CallIdentityButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<Button>("HangupCallButton")!.IsEnabled);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            window.FindControl<Button>("HangupCallButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<Button>("CallIdentityButton")!.IsEnabled);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2));
            Assert.Equal(0, devices.Captures);
            var persisted = await new SidebandStateStore(root).LoadAsync(cancellationToken: token);
            Assert.True(persisted.VoiceEnabled);
            Assert.True(persisted.Voice!.TrustedOnly);
            Assert.True(persisted.Voice.DirectPushToTalk);
            Assert.True(Assert.Single(persisted.Contacts).DirectPushToTalk);
            Assert.Equal(peer, Assert.Single(persisted.VoicePeers!).IdentityHash);
            Assert.Single(window.FindControl<ListBox>("VoiceHistoryList")!.Items);
            window.FindControl<Button>("ClearVoiceHistoryButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<ListBox>("VoiceHistoryList")!.Items.Count == 0);
            Assert.Empty((await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).Calls!);
            await window.CloseAndWaitAsync();
            Assert.Equal(devices.Captures, devices.CaptureDisposals);
            Assert.Equal(devices.Playbacks, devices.PlaybackDisposals);
        }
        finally
        {
            if (window is not null) await window.CloseAndWaitAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!await condition()) await Task.Delay(20, timeout.Token);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
            await Dispatcher.UIThread.InvokeAsync(() => { });
        }

        Assert.True(condition(), "The expected UI state was not reached.");
    }
}
