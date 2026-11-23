using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using DotReticulum.Core;
using Sideband.Desktop;

namespace Sideband.Tests;

public sealed partial class SidebandHeadlessUiTests
{
    [AvaloniaFact]
    public async Task VoiceOnlyNamesTrustAndDeletionAreUsableOfflineAcrossDesktopRestartWithoutChangingDeliveryTrust()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "sideband-voice-peer-ui-" + Guid.NewGuid().ToString("N"));
        var config = Path.Combine(root, "unopened-rns");
        const string identity = "000102030405060708090a0b0c0d0e0f";
        var delivery = Convert.ToHexStringLower(Destination.ComputeHashFromNameAndIdentity("lxmf.delivery",
            Convert.FromHexString(identity)));
        var token = TestContext.Current.CancellationToken;
        var devices = new SidebandVoiceLifecycleTests.Devices();
        MainWindow? window = null;
        try
        {
            var store = new SidebandStateStore(root);
            await store.ConfigureVoicePeerAsync(identity, "Saved voice alias", true, token);
            await store.AddContactAsync(SidebandContact.Create("Delivery override", delivery), token);
            await store.AddCallEventAsync(new(DateTimeOffset.UtcNow, "incoming-missed", identity, Imported: true), token);
            foreach (var restart in Enumerable.Range(0, 2))
            {
                window = new MainWindow(root, config, mediaBackend: devices);
                window.Show();
                await WaitForAsync(() => window.FindControl<ListBox>("VoicePeerList")!.ItemCount == 1);
                window.FindControl<Button>("VoiceNavButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.FindControl<ListBox>("VoicePeerList")!.SelectedIndex = 0;
                Assert.Equal(identity, window.FindControl<TextBox>("VoiceIdentityBox")!.Text);
                Assert.Equal(restart == 0 ? "Saved voice alias" : "Renamed voice alias",
                    window.FindControl<TextBox>("VoicePeerNameBox")!.Text);
                Assert.Contains("Delivery override", Assert.Single(window.FindControl<ListBox>("VoiceHistoryList")!.Items.Cast<string>()));
                if (restart == 0)
                {
                    window.FindControl<TextBox>("VoicePeerNameBox")!.Text = "Renamed voice alias";
                    window.FindControl<CheckBox>("VoicePeerTrustedBox")!.IsChecked = false;
                    window.FindControl<Button>("SaveVoicePeerButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await WaitForAsync(() => window.FindControl<ListBox>("VoicePeerList")!.Items.Cast<string>().Any(item =>
                        item.Contains("Renamed voice alias", StringComparison.Ordinal) &&
                        item.EndsWith("voice-only untrusted", StringComparison.Ordinal)));
                }
                else
                {
                    Assert.False(window.FindControl<CheckBox>("VoicePeerTrustedBox")!.IsChecked);
                    window.FindControl<CheckBox>("VoicePeerTrustedBox")!.IsChecked = true;
                    window.FindControl<Button>("SaveVoicePeerTrustButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await WaitForAsync(() => window.FindControl<ListBox>("VoicePeerList")!.Items.Cast<string>().Any(item =>
                        item.EndsWith("voice-only trusted", StringComparison.Ordinal)));
                    window.FindControl<Button>("DeleteVoicePeerButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await WaitForAsync(() => window.FindControl<ListBox>("VoicePeerList")!.ItemCount == 0);
                }
                await window.CloseAndWaitAsync();
                var state = await new SidebandStateStore(root).LoadAsync(cancellationToken: token);
                Assert.Equal("Delivery override", Assert.Single(state.Contacts).DisplayName);
                Assert.False(state.Contacts[0].Trusted);
                Assert.False(state.VoiceEnabled);
                if (restart == 0)
                {
                    var peer = Assert.Single(state.VoicePeers!);
                    Assert.Equal("Renamed voice alias", peer.DisplayName);
                    Assert.False(peer.VoiceTrusted);
                }
                else Assert.Empty(state.VoicePeers!);
                Assert.False(Directory.Exists(config));
                Assert.Equal(0, devices.Captures);
                Assert.Equal(0, devices.Playbacks);
            }
        }
        finally
        {
            if (window is not null) await window.CloseAndWaitAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
