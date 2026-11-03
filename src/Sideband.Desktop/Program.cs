using Avalonia;
using Sideband;
using System.Runtime.InteropServices;

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
            if (options.ImportProfile is { } profile)
            {
                using var cancellation = new CancellationTokenSource();
                ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
                Console.CancelKeyPress += cancel;
                try { return ProfileImportCommand.RunAsync(profile, DataDirectory, Console.Out, cancellation.Token).GetAwaiter().GetResult(); }
                finally { Console.CancelKeyPress -= cancel; }
            }
            if (options.CheckMedia)
            {
                using var cancellation = new CancellationTokenSource();
                ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
                Console.CancelKeyPress += cancel;
                try { return CheckMediaAsync(cancellation.Token).GetAwaiter().GetResult(); }
                finally { Console.CancelKeyPress -= cancel; }
            }
            if (options.Initialize || options.Check || options.ImportSettings is not null || options.ExportSettings is not null)
                return RunMaintenanceAsync(options).GetAwaiter().GetResult();

            if (options.Daemon)
            {
                using var cancellation = new CancellationTokenSource();
                ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
                Console.CancelKeyPress += cancel;
                using var signal = PosixSignalRegistration.Create(PosixSignal.SIGTERM,
                    context => { context.Cancel = true; cancellation.Cancel(); });
                try
                {
                    return SidebandDaemon.RunAsync(DataDirectory, ReticulumConfigDirectory, options.Interactive,
                        options.SyntheticAudio, options.Verbose, Console.In, Console.Out, Console.Error, cancellation.Token).GetAwaiter().GetResult();
                }
                finally { Console.CancelKeyPress -= cancel; }
            }

            var exitCode = BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
            return SmokeUiMode && !MainWindow.SmokeUiSucceeded && exitCode == 0 ? 2 : exitCode;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("sideband: operation cancelled.");
            return 130;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or System.Text.Json.JsonException or DllNotFoundException or EntryPointNotFoundException or NotSupportedException)
        {
            Console.Error.WriteLine("sideband: " + ex.Message);
            return 1;
        }
    }

    private static async Task<int> CheckMediaAsync(CancellationToken cancellationToken)
    {
        var store = new SidebandStateStore(DataDirectory);
        if (!File.Exists(store.StatePath)) throw new FileNotFoundException("No existing Sideband state was found.");
        var state = await store.LoadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await using var client = new SidebandClient(store);
        var images = 0;
        var audio = 0;
        foreach (var message in state.Messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (message.Image is not null)
            {
                _ = await client.LoadImageAsync(message.PeerDeliveryHash, message.Id, cancellationToken).ConfigureAwait(false);
                images++;
            }
            if (message.Audio is not null)
            {
                _ = await client.LoadAudioAsync(message.PeerDeliveryHash, message.Id, cancellationToken).ConfigureAwait(false);
                audio++;
            }
        }
        Console.WriteLine($"Validated {images} images and {audio} audio messages without opening audio devices or Reticulum.");
        return 0;
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
        var checkMedia = false;
        var help = false;
        var version = false;
        var smokeUi = false;
        var daemon = false;
        var interactive = false;
        var syntheticAudio = false;
        var verbose = false;
        string? importSettings = null;
        string? exportSettings = null;
        string? importProfile = null;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "-h" or "--help": help = true; break;
                case "--version": version = true; break;
                case "--smoke-ui": smokeUi = true; break;
                case "--init": initialize = true; break;
                case "--check": check = true; break;
                case "--check-media": checkMedia = true; break;
                case "-d" or "--daemon": daemon = true; break;
                case "-i" or "--interactive": interactive = true; break;
                case "-v" or "--verbose": verbose = true; break;
                case "--synthetic-audio": syntheticAudio = true; break;
                case "-c" or "--config" or "--data-dir": dataDirectory = NextValue(args, ref index); break;
                case "--import-settings": importSettings = NextValue(args, ref index); break;
                case "--export-settings": exportSettings = NextValue(args, ref index); break;
                case "--import-profile": importProfile = NextValue(args, ref index); break;
                case "-r" or "--rnsconfig": rnsConfig = NextValue(args, ref index); break;
                case "--": break;
                default: throw new ArgumentException($"Unknown option '{args[index]}'. Use --help for usage.");
            }
        }

        if ((initialize ? 1 : 0) + (check ? 1 : 0) + (checkMedia ? 1 : 0) + (importSettings is null ? 0 : 1) + (exportSettings is null ? 0 : 1) + (importProfile is null ? 0 : 1) > 1)
            throw new ArgumentException("--init, --check, --check-media, --import-settings, --export-settings and --import-profile cannot be combined.");
        if (daemon && (initialize || check || checkMedia || importSettings is not null || exportSettings is not null || importProfile is not null || smokeUi))
            throw new ArgumentException("--daemon cannot be combined with maintenance or UI smoke modes.");
        if ((interactive || syntheticAudio || verbose) && !daemon)
            throw new ArgumentException("--interactive, --synthetic-audio and --verbose require --daemon.");
        if (checkMedia && (rnsConfig is not null || smokeUi))
            throw new ArgumentException("--check-media is offline and cannot be combined with --rnsconfig or --smoke-ui.");
        if (importProfile is not null && (string.IsNullOrWhiteSpace(dataDirectory) || rnsConfig is not null || smokeUi))
            throw new ArgumentException("--import-profile requires an explicit new --data-dir and cannot be combined with --rnsconfig or --smoke-ui.");
        if (initialize || check)
        {
            dataDirectory = Path.GetFullPath(dataDirectory ?? SidebandStateStore.DefaultDataDirectory());
            if (rnsConfig is not null) rnsConfig = Path.GetFullPath(rnsConfig);
        }
        else if (rnsConfig is not null)
        {
            rnsConfig = Path.GetFullPath(rnsConfig);
        }

        return new Options(dataDirectory, rnsConfig, initialize, check, help, version, smokeUi, importSettings, exportSettings, importProfile, checkMedia,
            daemon, interactive, syntheticAudio, verbose);
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
          sideband --check-media [--data-dir <path>]
          sideband --import-settings <json-path> [--config <path>]
          sideband --export-settings <json-path> [--config <path>]
          sideband --import-profile <upstream-profile> --data-dir <new-directory>
          sideband --smoke-ui [--data-dir <path>] [--rnsconfig <path>]
          sideband --daemon [--interactive] [--verbose] [--synthetic-audio] [--config <path>] [--rnsconfig <path>]
          sideband --help | --version

        --init       Initialize durable application state and the local identity.
        --check      Validate local state and report the existing identity.
        --check-media  Validate/decode stored media offline; requires native codecs, never opens devices.
        --data-dir   Select the Sideband data directory.
        --config     Alias for --data-dir (also -c).
        --import-settings  Import supported upstream JSON settings; retain and report unsupported keys.
        --export-settings  Export upstream-shaped JSON settings, not identities or message databases.
        --import-profile   Import an offline upstream desktop profile into an absent directory; never merge or overwrite.
        --rnsconfig  Select a Reticulum configuration directory or file.
        --smoke-ui   Open the normal desktop window and report actual render events for GUI acceptance.
        --daemon     Serve without opening the desktop; runs until cancellation, even when stdin closes.
        --interactive  Enable a bounded original console in daemon mode; EOF or quit stops it.
        --verbose    Print public voice-state events in daemon mode (also -v).
        --synthetic-audio  Explicit daemon-only synthetic/discard audio; never opens physical devices.
        """;

    private sealed record Options(string? DataDirectory, string? ReticulumConfigDirectory, bool Initialize, bool Check, bool Help, bool Version, bool SmokeUi,
        string? ImportSettings, string? ExportSettings, string? ImportProfile, bool CheckMedia,
        bool Daemon, bool Interactive, bool SyntheticAudio, bool Verbose);
}
