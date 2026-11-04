using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Sideband.Desktop;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandStampUiTests
{
    [AvaloniaFact]
    public async Task OriginalPreferencesPreserveIndependentStampControlsErrorsAndRestartWithoutDevices()
    {
        var root = Path.Combine(Path.GetTempPath(), "stamp-ui-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        var devices = new SidebandVoiceLifecycleTests.Devices();
        MainWindow? window = null;
        try
        {
            window = new MainWindow(root, Path.Combine(root, "unused-rns"), mediaBackend: devices);
            window.Show();
            await Ready();
            Click("PreferencesNavButton");
            Assert.False(Control<CheckBox>("RequireStampsBox").IsChecked);
            Assert.True(Control<CheckBox>("RejectInvalidStampsBox").IsChecked);
            Control<TextBox>("DisplayNameBox").Text = "Unsaved display edit";
            Control<CheckBox>("RequireStampsBox").IsChecked = true;
            Control<TextBox>("InboundStampCostBox").Text = "8";
            Control<CheckBox>("RejectInvalidStampsBox").IsChecked = false;
            Click("SaveStampPolicyButton");
            await Until(() => Status().StartsWith("Stamp policy saved", StringComparison.Ordinal));
            Assert.Equal(new(true, 8, false), await Policy());
            Control<TextBox>("InboundStampCostBox").Text = "255";
            Click("SaveStampPolicyButton");
            await Until(() => Status().Contains("below 255", StringComparison.Ordinal));
            Assert.Equal(new(true, 8, false), await Policy());
            Control<TextBox>("InboundStampCostBox").Text = "bad";
            Click("SaveStampPolicyButton");
            await Until(() => Status().Contains("whole stamp cost", StringComparison.Ordinal));
            Assert.Equal("bad", Control<TextBox>("InboundStampCostBox").Text);
            Control<TextBox>("InboundStampCostBox").Text = "12";
            Control<CheckBox>("RequireStampsBox").IsChecked = false;
            Control<CheckBox>("RejectInvalidStampsBox").IsChecked = true;
            Click("SaveStampPolicyButton");
            await Until(() => Status().StartsWith("Stamp policy saved", StringComparison.Ordinal));
            Assert.Equal(new(false, 12, true), await Policy());
            Assert.Equal("Sideband", (await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).DisplayName);
            await window.CloseAndWaitAsync();
            window = new MainWindow(root, Path.Combine(root, "unused-rns"), mediaBackend: devices);
            window.Show();
            await Ready();
            Click("PreferencesNavButton");
            Assert.False(Control<CheckBox>("RequireStampsBox").IsChecked);
            Assert.True(Control<CheckBox>("RejectInvalidStampsBox").IsChecked);
            Assert.Equal("12", Control<TextBox>("InboundStampCostBox").Text);
            Control<CheckBox>("RequireStampsBox").IsChecked = true;
            Control<TextBox>("InboundStampCostBox").Text = "";
            Click("SaveStampPolicyButton");
            await Until(() => Status().StartsWith("Stamp policy saved", StringComparison.Ordinal));
            Assert.Equal(new(true, null, true), await Policy());
            Assert.Equal(0, devices.Captures);
            Assert.Equal(0, devices.Playbacks);

            T Control<T>(string name) where T : Control => window.FindControl<T>(name)!;
            void Click(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            string Status() => Control<TextBlock>("StampPolicyStatusText").Text ?? "";
            async Task<SidebandStampConfiguration> Policy() =>
                SidebandStampConfiguration.FromState(await new SidebandStateStore(root).LoadAsync(cancellationToken: token));
            async Task Ready() => await Until(() => Control<TextBlock>("StatusText").Text?.StartsWith("Offline", StringComparison.Ordinal) == true);
            async Task Until(Func<bool> condition)
            {
                var until = DateTime.UtcNow.AddSeconds(15);
                while (!condition())
                {
                    if (DateTime.UtcNow >= until) throw new TimeoutException("Stamp UI did not reach its asserted state: " + Status());
                    Dispatcher.UIThread.RunJobs();
                    await Task.Delay(10, token);
                }
            }
        }
        finally
        {
            if (window is not null) await window.CloseAndWaitAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
