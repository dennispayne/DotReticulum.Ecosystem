using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;

namespace Sideband.Desktop;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(Program.DataDirectory, Program.ReticulumConfigDirectory, Program.SmokeUiMode);
        }

        base.OnFrameworkInitializationCompleted();
    }

    public void SetTheme(string theme)
    {
        var light = theme == "Light";
        RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        Resources["AppBackgroundBrush"] = Brush(light ? "#F3F6F7" : "#0D161A");
        Resources["NavigationBackgroundBrush"] = Brush(light ? "#E7EEF0" : "#111F24");
        Resources["CardBackgroundBrush"] = Brush(light ? "#FFFFFF" : "#17272C");
        Resources["DividerBrush"] = Brush(light ? "#C6D3D6" : "#284046");
        Resources["AccentBrush"] = Brush(light ? "#007D83" : "#008F95");
        Resources["PrimaryTextBrush"] = Brush(light ? "#17272C" : "#EAF2F3");
        Resources["SecondaryTextBrush"] = Brush(light ? "#4E656B" : "#A5B9BC");
    }

    private static SolidColorBrush Brush(string color) => new(Color.Parse(color));
}
