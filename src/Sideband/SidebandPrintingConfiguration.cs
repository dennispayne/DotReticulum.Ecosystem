namespace Sideband;

/// <summary>Local explicit activation only; passive primary settings never authorize process execution.</summary>
public sealed record SidebandPrintingConfiguration(bool Enabled = false, string Command = "lp")
{
    public SidebandPrintingConfiguration Validate()
    {
        _ = CommandArguments(Command);
        return this;
    }

    public static IReadOnlyList<string> CommandArguments(string command) =>
        SharedPrinting.PrintCommand.Arguments(command);

    public static bool CanPrint(SidebandChatMessage message) =>
        !message.Incoming && !message.ImportedPaperEnvelope && message.DeliveryMethod == "Paper" &&
        message.PaperUri is not null;
}
