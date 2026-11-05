using Sideband;

namespace Sideband.Desktop;

/// <summary>Explicit native operator path over the same saved-message printing service as the desktop.</summary>
public static class SidebandPrintingCommand
{
    public const string Usage = "--print-paper <saved-message-id> --data-dir <profile> --confirm-print-command <exact-reviewed-local-command>";

    public static async Task<int?> TryRunAsync(string[] arguments, TextWriter output,
        CancellationToken cancellationToken = default)
    {
        if (arguments.Length == 0 || arguments[0] != "--print-paper") return null;
        if (arguments.Length != 6 || !Guid.TryParse(arguments[1], out var id) ||
            arguments[2] != "--data-dir" || string.IsNullOrWhiteSpace(arguments[3]) ||
            arguments[4] != "--confirm-print-command")
            throw new ArgumentException("Paper printing requires explicit saved-message and local-command consent: " + Usage);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            await using var printer = new SidebandPrintingService(new(arguments[3]));
            printer.Start();
            var result = await printer.PrintPaperAsync(id, arguments[5], true, stop.Token).ConfigureAwait(false);
            await output.WriteLineAsync(result.Success
                ? "Paper QR command completed. Software success is not confirmation of physical printing or delivery."
                : "Paper QR print failed: " + result.Detail).ConfigureAwait(false);
            return result.Success ? 0 : 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }
}
