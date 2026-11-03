using Avalonia;
using Sideband;

namespace Sideband.Desktop;

internal static class Program
{
    internal static string DataDirectory { get; private set; } = SidebandStateStore.DefaultDataDirectory();
    internal static string? ReticulumConfigDirectory { get; private set; }
    internal static bool SmokeUiMode { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var options = Parse(args);
            if (options.Help)
            {
                Console.WriteLine(Usage);
                return 0;
            }

            if (options.Version)
            {
                Console.WriteLine($"sideband {typeof(Program).Assembly.GetName().Version}");
                return 0;
            }

            DataDirectory = options.DataDirectory ?? DataDirectory;
            ReticulumConfigDirectory = options.ReticulumConfigDirectory;
            SmokeUiMode = options.SmokeUi;
            if (options.Initialize || options.Check || options.ImportSettings is not null || options.ExportSettings is not null)
                return RunMaintenanceAsync(options).GetAwaiter().GetResult();

            var exitCode = BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
            return SmokeUiMode && !MainWindow.SmokeUiSucceeded && exitCode == 0 ? 2 : exitCode;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine("sideband: " + ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunMaintenanceAsync(Options options)
    {
        var store = new SidebandStateStore(DataDirectory);
        if (options.ImportSettings is { } import)
        {
            var unsupported = await store.ImportSettingsAsync(import).ConfigureAwait(false);
            Console.WriteLine("Application settings imported. Identity and message databases were not imported.");
            if (unsupported.Count > 0) Console.WriteLine("Preserved but not applied: " + string.Join(", ", unsupported));
            return 0;
        }
        if (options.ExportSettings is { } export)
        {
            await store.ExportSettingsAsync(export).ConfigureAwait(false);
            Console.WriteLine("Application settings written to " + Path.GetFullPath(export));
            return 0;
        }
        var state = await store.LoadAsync(options.ReticulumConfigDirectory).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(options.ReticulumConfigDirectory))
        {
            await store.SetReticulumConfigDirectoryAsync(options.ReticulumConfigDirectory).ConfigureAwait(false);
            state = await store.LoadAsync().ConfigureAwait(false);
        }

        await using var node = DotReticulum.Applications.ReticulumNode.Open(state.ReticulumConfigDirectory);
        using var identity = node.LoadOrCreateIdentity("sideband");
        Console.WriteLine(options.Initialize ? $"Initialized Sideband data at {DataDirectory}" : $"Sideband data is valid at {DataDirectory}");
        Console.WriteLine($"Identity: {identity.HexHash}");
        return 0;
    }

    private static Options Parse(string[] args)
    {
        string? dataDirectory = null;
        string? rnsConfig = null;
        var initialize = false;
        var check = false;
        var help = false;
        var version = false;
        var smokeUi = false;
        string? importSettings = null;
        string? exportSettings = null;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "-h" or "--help": help = true; break;
                case "--version": version = true; break;
                case "--smoke-ui": smokeUi = true; break;
                case "--init": initialize = true; break;
                case "--check": check = true; break;
                case "-c" or "--config" or "--data-dir": dataDirectory = NextValue(args, ref index); break;
                case "--import-settings": importSettings = NextValue(args, ref index); break;
                case "--export-settings": exportSettings = NextValue(args, ref index); break;
                case "-r" or "--rnsconfig": rnsConfig = NextValue(args, ref index); break;
                case "--": break;
                default: throw new ArgumentException($"Unknown option '{args[index]}'. Use --help for usage.");
            }
        }

        if ((initialize ? 1 : 0) + (check ? 1 : 0) + (importSettings is null ? 0 : 1) + (exportSettings is null ? 0 : 1) > 1)
            throw new ArgumentException("--init, --check, --import-settings and --export-settings cannot be combined.");
        if (initialize || check)
        {
            dataDirectory = Path.GetFullPath(dataDirectory ?? SidebandStateStore.DefaultDataDirectory());
            if (rnsConfig is not null) rnsConfig = Path.GetFullPath(rnsConfig);
        }
        else if (rnsConfig is not null)
        {
            rnsConfig = Path.GetFullPath(rnsConfig);
        }

        return new Options(dataDirectory, rnsConfig, initialize, check, help, version, smokeUi, importSettings, exportSettings);
    }

    private static string NextValue(string[] args, ref int index)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith('-'))
            throw new ArgumentException($"{args[index]} requires a path.");
        return args[++index];
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    private const string Usage = """
        sideband - experimental Reticulum messaging desktop client

        Usage:
            sideband [--data-dir <path>] [--rnsconfig <path>]
          sideband --init [--data-dir <path>] [--rnsconfig <path>]
          sideband --check [--data-dir <path>] [--rnsconfig <path>]
          sideband --import-settings <json-path> [--config <path>]
          sideband --export-settings <json-path> [--config <path>]
          sideband --smoke-ui [--data-dir <path>] [--rnsconfig <path>]
          sideband --help | --version

        --init       Initialize durable application state and the local identity.
        --check      Validate local state and report the existing identity.
        --data-dir   Select the Sideband data directory.
        --config     Alias for --data-dir (also -c).
        --import-settings  Import supported upstream JSON settings; retain and report unsupported keys.
        --export-settings  Export upstream-shaped JSON settings, not identities or message databases.
        --rnsconfig  Select a Reticulum configuration directory or file.
        --smoke-ui   Open the normal desktop window and report actual render events for GUI acceptance.
        """;

    private sealed record Options(string? DataDirectory, string? ReticulumConfigDirectory, bool Initialize, bool Check, bool Help, bool Version, bool SmokeUi,
        string? ImportSettings, string? ExportSettings);
}
