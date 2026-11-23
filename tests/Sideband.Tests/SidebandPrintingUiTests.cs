using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using LXMF;
using Sideband.Desktop;
using System.Diagnostics;
using System.Text.Json;

namespace Sideband.Tests;

public sealed partial class SidebandHeadlessUiTests
{
    public static bool PrintingPlatformUnsupported => !OperatingSystem.IsWindows() && !OperatingSystem.IsLinux();

    [AvaloniaFact(SkipWhen = nameof(PrintingPlatformUnsupported), Skip = "Printing process ownership is implemented on Windows and Linux only.")]
    [Trait("TestId", "T1006")]
    public async Task PrintingUiRequiresActivationAndSelectionBoundConsentWithoutChangingDrafts()
    {
        var root = SidebandPrintingIntegrationTests.NewRoot();
        MainWindow? window = null;
        try
        {
            var store = new SidebandStateStore(root);
            var original = await SidebandPrintingIntegrationTests.SeedAsync(store);
            await store.AddMessageAsync(original with
            {
                Id = Guid.NewGuid(),
                Content = "Other saved original paper",
                Timestamp = original.Timestamp.AddSeconds(1)
            }, cancellationToken: TestContext.Current.CancellationToken);
            window = new MainWindow(root, Path.Combine(root, "isolated-rns"));
            window.Show();
            await WaitForAsync(() => window.FindControl<ListBox>("MessageList")?.ItemCount == 2);
            var list = window.FindControl<ListBox>("MessageList")!;
            var print = window.FindControl<Button>("PrintPaperButton")!;
            var consent = window.FindControl<CheckBox>("ConfirmPaperPrintBox")!;
            var save = window.FindControl<Button>("SavePrintingButton")!;
            Assert.False(print.IsEnabled);
            Assert.False(consent.IsEnabled);
            window.FindControl<TextBox>("MessageBox")!.Text = "Unsent draft stays here";
            window.FindControl<TextBox>("AttachmentPathBox")!.Text = "Unsent attachment stays here";
            var command = SidebandPrintingIntegrationTests.Command(root, "capture");
            window.FindControl<TextBox>("PrintCommandBox")!.Text = command;
            window.FindControl<CheckBox>("EnablePaperPrintingBox")!.IsChecked = true;
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("StatusText")?.Text?.Contains("explicit approval", StringComparison.Ordinal) == true);
            Assert.False((await new SidebandStateStore(root).LoadAsync(cancellationToken: TestContext.Current.CancellationToken)).Printing.Enabled);
            window.FindControl<CheckBox>("ApprovePrintCommandBox")!.IsChecked = true;
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("StatusText")?.Text?.StartsWith("Local print command activated.", StringComparison.Ordinal) == true);
            Assert.False(window.FindControl<CheckBox>("ApprovePrintCommandBox")!.IsChecked);
            list.SelectedIndex = 0;
            await WaitForAsync(() => print.IsEnabled);
            Assert.Contains(command, window.FindControl<TextBlock>("PaperPrintCommandText")!.Text, StringComparison.Ordinal);
            print.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("StatusText")?.Text?.Contains("Explicit consent", StringComparison.Ordinal) == true);
            Assert.False(Directory.Exists(Path.Combine(root, "captures")));
            consent.IsChecked = true;
            list.SelectedIndex = 1;
            Assert.False(consent.IsChecked);
            list.SelectedIndex = 0;
            consent.IsChecked = true;
            print.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("StatusText")?.Text?.StartsWith("Paper QR command completed.", StringComparison.Ordinal) == true);
            Assert.False(consent.IsChecked);
            Assert.Single(Directory.GetFiles(Path.Combine(root, "captures"), "*.json"));
            Assert.Equal("Unsent draft stays here", window.FindControl<TextBox>("MessageBox")!.Text);
            Assert.Equal("Unsent attachment stays here", window.FindControl<TextBox>("AttachmentPathBox")!.Text);
            Assert.Equal(2, (await new SidebandStateStore(root).LoadAsync(cancellationToken: TestContext.Current.CancellationToken)).Messages.Count);
            await window.CloseAndWaitAsync();
            window = new MainWindow(root, Path.Combine(root, "isolated-rns"));
            window.Show();
            await WaitForAsync(() => window.FindControl<ListBox>("MessageList")?.ItemCount == 2);
            Assert.Equal(command, window.FindControl<TextBox>("PrintCommandBox")!.Text);
            Assert.True(window.FindControl<CheckBox>("EnablePaperPrintingBox")!.IsChecked);
            Assert.False(window.FindControl<CheckBox>("ApprovePrintCommandBox")!.IsChecked);
            Assert.False(window.FindControl<CheckBox>("ConfirmPaperPrintBox")!.IsChecked);
            Assert.Single(Directory.GetFiles(Path.Combine(root, "captures"), "*.json"));
            await window.CloseAndWaitAsync();
        }

        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            Directory.Delete(root, true);
        }
    }

    [AvaloniaFact(SkipWhen = nameof(PrintingPlatformUnsupported), Skip = "Printing process ownership is implemented on Windows and Linux only.")]
    [Trait("TestId", "T1015")]
    public async Task PrintingUiRecoversFromCommandFailureButRefusesHistoricalImports()
    {
        var root = SidebandPrintingIntegrationTests.NewRoot();
        var token = TestContext.Current.CancellationToken;
        MainWindow? window = null;
        try
        {
            var store = new SidebandStateStore(root);
            var historical = await SidebandPrintingIntegrationTests.SeedAsync(store);
            await store.AddMessageAsync(historical with
            {
                Id = Guid.NewGuid(),
                Content = "Public historical imported paper",
                ImportedPaperEnvelope = true,
                Timestamp = historical.Timestamp.AddSeconds(1),
            }, cancellationToken: token);
            await store.ConfigurePrintingAsync(true, SidebandPrintingIntegrationTests.Command(root, "fail"), true, token);
            window = new MainWindow(root, Path.Combine(root, "isolated-rns"));
            window.Show();
            await WaitForAsync(() => window.FindControl<ListBox>("MessageList")?.ItemCount == 2);
            var saved = historical;
            Assert.False(saved.Incoming);
            Assert.False(saved.ImportedPaperEnvelope);
            Assert.Equal("Paper", saved.DeliveryMethod);
            Assert.NotNull(saved.PaperUri);
            var list = window.FindControl<ListBox>("MessageList")!;
            var rows = list.ItemsSource!.Cast<string>().ToArray();
            list.SelectedIndex = Array.FindIndex(rows, row => row.Contains(saved.Content, StringComparison.Ordinal));
            var print = window.FindControl<Button>("PrintPaperButton")!;
            var consent = window.FindControl<CheckBox>("ConfirmPaperPrintBox")!;
            await WaitForAsync(() => print.IsEnabled);
            window.FindControl<TextBox>("MessageBox")!.Text = "Independent unsent draft";
            window.FindControl<TextBox>("AttachmentPathBox")!.Text = "Independent unsent attachment";
            consent.IsChecked = true;
            print.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("StatusText")?.Text?.Contains(
                "code 7", StringComparison.Ordinal) == true);
            Assert.False(consent.IsChecked);
            Assert.True(print.IsEnabled);
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "storage", "print-jobs")));
            var command = SidebandPrintingIntegrationTests.Command(root, "capture");
            window.FindControl<TextBox>("PrintCommandBox")!.Text = command;
            window.FindControl<CheckBox>("ApprovePrintCommandBox")!.IsChecked = true;
            window.FindControl<Button>("SavePrintingButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("StatusText")?.Text?.StartsWith(
                "Local print command activated.", StringComparison.Ordinal) == true);
            Assert.False(consent.IsChecked);
            consent.IsChecked = true;
            print.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("StatusText")?.Text?.StartsWith(
                "Paper QR command completed.", StringComparison.Ordinal) == true);
            using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(
                Assert.Single(Directory.GetFiles(Path.Combine(root, "captures"), "*.json")), token));
            Assert.False(File.Exists(receipt.RootElement.GetProperty("original").GetString()));
            Assert.Equal(LxPaperQr.Encode(saved.PaperUri!, cancellationToken: token).ToPng(cancellationToken: token),
                await File.ReadAllBytesAsync(receipt.RootElement.GetProperty("copy").GetString()!, token));
            list.SelectedIndex = Array.FindIndex(rows, row => row.Contains("Public historical imported paper", StringComparison.Ordinal));
            Assert.False(print.IsEnabled);
            Assert.False(consent.IsEnabled);
            Assert.False(consent.IsChecked);
            consent.IsChecked = true;
            print.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("StatusText")?.Text?.Contains(
                "Only original saved outgoing", StringComparison.Ordinal) == true);
            Assert.Single(Directory.GetFiles(Path.Combine(root, "captures"), "*.json"));
            Assert.Equal("Independent unsent draft", window.FindControl<TextBox>("MessageBox")!.Text);
            Assert.Equal("Independent unsent attachment", window.FindControl<TextBox>("AttachmentPathBox")!.Text);
            Assert.Equal(saved.PaperUri, (await new SidebandStateStore(root).LoadAsync(cancellationToken: token))
                .Messages.Single(message => message.Id == saved.Id).PaperUri);
            await window.CloseAndWaitAsync();
        }
        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            Directory.Delete(root, true);
        }
    }
    [AvaloniaFact(SkipWhen = nameof(PrintingPlatformUnsupported), Skip = "Printing process ownership is implemented on Windows and Linux only.")]
    [Trait("TestId", "T1007")]
    public async Task PrintingUiCloseCancelsAndJoinsItsActualPrivateCommandTree()
    {
        var root = SidebandPrintingIntegrationTests.NewRoot();
        MainWindow? window = null;
        try
        {
            var store = new SidebandStateStore(root);
            await SidebandPrintingIntegrationTests.SeedAsync(store);
            await store.ConfigurePrintingAsync(true, SidebandPrintingIntegrationTests.Command(root, "childhang"),
                true, TestContext.Current.CancellationToken);
            window = new MainWindow(root, Path.Combine(root, "isolated-rns"));
            window.Show();
            await WaitForAsync(() => window.FindControl<ListBox>("MessageList")?.ItemCount == 1);
            window.FindControl<ListBox>("MessageList")!.SelectedIndex = 0;
            await WaitForAsync(() => window.FindControl<Button>("PrintPaperButton")!.IsEnabled);
            window.FindControl<CheckBox>("ConfirmPaperPrintBox")!.IsChecked = true;
            window.FindControl<Button>("PrintPaperButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var marker = Path.Combine(root, "marker.child");
            await WaitForAsync(() => File.Exists(marker));
            var childId = int.Parse(await File.ReadAllTextAsync(marker, TestContext.Current.CancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
            using var child = Process.GetProcessById(childId);
            await window.CloseAndWaitAsync();
            Assert.True(child.HasExited);
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "storage", "print-jobs")));
        }
        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            Directory.Delete(root, true);
        }
    }
}
