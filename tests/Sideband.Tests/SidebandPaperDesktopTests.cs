using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Sideband.Desktop;
using System.Text.Json;
using Avalonia.Media.Imaging;
using System.Reflection;

namespace Sideband.Tests;

public sealed partial class SidebandHeadlessUiTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedReferencePaperShowsExportsAndReopensWithoutNetworkOrDeviceAccess(bool imported)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "sideband-paper-saved-ui-" + Guid.NewGuid().ToString("N"));
        MainWindow? window = null;
        try
        {
            using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-paper.json")));
            var peer = fixture.RootElement.GetProperty("destination").GetString()!;
            var paper = fixture.RootElement.GetProperty("cases")[0];
            var uri = paper.GetProperty("uri").GetString()!;
            var store = new SidebandStateStore(path);
            await store.AddContactAsync(SidebandContact.Create("Reference paper peer", peer));
            var message = new SidebandChatMessage(Guid.NewGuid(), peer, false, "", paper.GetProperty("content").GetString()!,
                DateTimeOffset.UtcNow, "Paper", DeliveryMethod: "Paper", PaperUri: uri, ImportedPaperEnvelope: imported);
            await store.AddMessageAsync(message);
            await store.SetConversationReadAsync(peer, true);
            var backend = new UiMediaBackend();
            for (var restart = 0; restart < 2; restart++)
            {
                window = new MainWindow(path, Path.Combine(path, "isolated-rns"), mediaBackend: backend);
                window.Show();
                await WaitForAsync(() => window.FindControl<ListBox>("MessageList")?.ItemCount == 1);
                await WaitForAsync(() => window.FindControl<TextBlock>("StatusText")?.Text?.StartsWith("Offline", StringComparison.Ordinal) == true);
                window.FindControl<ListBox>("MessageList")!.SelectedIndex = 0;
                window.FindControl<Button>("ShowPaperButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitForAsync(() => window.FindControl<PaperQrView>("PaperQrPreview")?.Source is not null);
                Assert.Equal(uri, window.FindControl<TextBox>("PaperUriBox")!.Text);
                Assert.Equal(imported, window.FindControl<TextBlock>("StatusText")!.Text!.Contains("not authenticated", StringComparison.Ordinal));
                window.FindControl<CheckBox>("TrustConversationBox")!.IsChecked = restart == 0;
                window.FindControl<Button>("SaveConversationTrustButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitForAsync(() => window.FindControl<TextBlock>("StatusText")?.Text?.StartsWith("Conversation trust saved.", StringComparison.Ordinal) == true);
                Assert.NotNull(window.FindControl<PaperQrView>("PaperQrPreview")!.Source);
                Assert.Equal(uri, window.FindControl<TextBox>("PaperUriBox")!.Text);
                var png = Path.Combine(path, $"qr-{restart}.png");
                window.FindControl<TextBox>("AttachmentExportPathBox")!.Text = png;
                window.FindControl<Button>("ExportPaperQrButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitForAsync(() => window.FindControl<TextBlock>("StatusText")?.Text?.StartsWith("Paper QR PNG exported", StringComparison.Ordinal) == true);
                Assert.Equal(imported, window.FindControl<TextBlock>("StatusText")!.Text!.Contains("not authenticated", StringComparison.Ordinal));
                using (var bitmap = new Bitmap(png)) Assert.True(bitmap.PixelSize.Width > 0);
                var original = await File.ReadAllBytesAsync(png);
                window.FindControl<Button>("ExportPaperQrButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitForAsync(() => window.FindControl<TextBlock>("StatusText")?.Text?.StartsWith("Paper operation failed:", StringComparison.Ordinal) == true);
                Assert.Equal(original, await File.ReadAllBytesAsync(png));
                using var output = new StringWriter();
                var exported = Path.Combine(path, $"paper-{restart}.txt");
                Assert.Equal(0, await PaperExportCommand.RunAsync(path, message.Id, exported, false, output));
                Assert.Equal(imported, output.ToString().Contains("not authenticated", StringComparison.Ordinal));
                Assert.Equal(uri, await File.ReadAllTextAsync(exported));
                await Assert.ThrowsAsync<IOException>(() => PaperExportCommand.RunAsync(path, message.Id, exported, false, output));
                Assert.Equal(uri, await File.ReadAllTextAsync(exported));
                window.FindControl<ListBox>("MessageList")!.SelectedIndex = -1;
                Assert.Null(window.FindControl<PaperQrView>("PaperQrPreview")!.Source);
                Assert.Equal("", window.FindControl<TextBox>("PaperUriBox")!.Text);
                Assert.Equal(0, backend.Opens);
                await window.CloseAndWaitAsync();
                Assert.Null(window.FindControl<PaperQrView>("PaperQrPreview")!.Source);
            }
            Assert.Null((await store.LoadAsync()).OwnDeliveryHash);
            Assert.False(Directory.Exists(Path.Combine(path, "isolated-rns")));
        }
        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task PaperControlsRejectOfflineCreationAttachmentsAndMalformedImportsExplicitly()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "sideband-paper-ui-" + Guid.NewGuid().ToString("N"));
        const string peer = "00112233445566778899aabbccddeeff";
        MainWindow? window = null;
        try
        {
            var store = new SidebandStateStore(path);
            await store.AddContactAsync(SidebandContact.Create("Paper peer", peer));
            await store.AddMessageAsync(new(Guid.NewGuid(), peer, false, "", "Ordinary text",
                DateTimeOffset.UtcNow, "Delivered"));
            var backend = new UiMediaBackend();
            window = new MainWindow(path, Path.Combine(path, "isolated-rns"), mediaBackend: backend);
            window.Show();
            await WaitForAsync(() => window.FindControl<ListBox>("MessageList")?.ItemCount == 1);
            Assert.Null(window.FindControl<PaperQrView>("PaperQrPreview")!.Source);
            window.FindControl<TextBox>("MessageBox")!.Text = "Keep this text";
            window.FindControl<TextBox>("AttachmentPathBox")!.Text = "unread-file.txt";
            window.FindControl<Button>("CreatePaperButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("StatusText")?.Text?.Contains("text only", StringComparison.Ordinal) == true);
            Assert.Equal("Keep this text", window.FindControl<TextBox>("MessageBox")!.Text);
            window.FindControl<TextBox>("AttachmentPathBox")!.Text = "";
            window.FindControl<Button>("CreatePaperButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("StatusText")?.Text?.Contains("Connect to Reticulum", StringComparison.Ordinal) == true);
            window.FindControl<ListBox>("MessageList")!.SelectedIndex = 0;
            var windowStore = Assert.IsType<SidebandStateStore>(typeof(MainWindow)
                .GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
            var gate = Assert.IsType<SemaphoreSlim>(typeof(SidebandStateStore)
                .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(windowStore));
            await gate.WaitAsync(TestContext.Current.CancellationToken);
            try
            {
                window.FindControl<Button>("ShowPaperButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("Paper operation pending.", window.FindControl<TextBlock>("StatusText")!.Text);
                Assert.Equal("Paper operation pending.", window.FindControl<TextBlock>("PaperImportStatusText")!.Text);
                Assert.False(window.FindControl<Button>("ImportPaperButton")!.IsEnabled);
            }
            finally { gate.Release(); }
            await WaitForAsync(() => window.FindControl<TextBlock>("StatusText")?.Text?.StartsWith("Paper operation failed:", StringComparison.Ordinal) == true);
            Assert.Null(window.FindControl<PaperQrView>("PaperQrPreview")!.Source);
            window.FindControl<Button>("UtilitiesNavButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.FindControl<TextBox>("PaperImportBox")!.Text = "lxm://bad";
            window.FindControl<Button>("ImportPaperButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("PaperImportStatusText")?.Text == "Rejected paper URI; no message accepted.");
            Assert.True(window.FindControl<Button>("ImportPaperButton")!.IsEnabled);
            Assert.Equal(0, backend.Opens);
            Assert.Single((await store.LoadAsync()).Messages);
            await window.CloseAndWaitAsync();
        }
        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }
}
