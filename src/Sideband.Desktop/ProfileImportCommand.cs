using Sideband;

namespace Sideband.Desktop;

/// <summary>Offline maintenance entry point; invoke before opening the desktop state store or starting Avalonia.</summary>
public static class ProfileImportCommand
{
    public static async Task<int> RunAsync(string sourceDirectory, string targetDirectory,
        TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        try
        {
            var result = await SidebandProfileImporter.ImportAsync(sourceDirectory, targetDirectory, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync($"Imported {result.Conversations} conversations and {result.Messages} messages.").ConfigureAwait(false);
            foreach (var notice in result.Notices) await output.WriteLineAsync("Notice: " + notice).ConfigureAwait(false);
            if (OperatingSystem.IsWindows())
                await output.WriteLineAsync("Protect the destination directory with your Windows account ACLs; it contains the imported private identity.").ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await output.WriteLineAsync("Profile import cancelled; no destination profile was committed.").ConfigureAwait(false);
            return 130;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or FormatException or ArithmeticException or
            InvalidOperationException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        {
            // Input keys/values and SQLite diagnostics can contain secrets. Report a type, not source content.
            await output.WriteLineAsync($"Profile import failed ({exception.GetType().Name}); no existing profile was replaced. Check the source schema, bounds, permissions and native SQLite availability.").ConfigureAwait(false);
            return 1;
        }
    }
}
