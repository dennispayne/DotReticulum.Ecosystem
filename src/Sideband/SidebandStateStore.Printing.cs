namespace Sideband;

public sealed partial class SidebandStateStore
{
    public Task ConfigurePrintingAsync(bool enabled, string command, bool explicitlyApproved,
        CancellationToken cancellationToken = default)
    {
        var configuration = new SidebandPrintingConfiguration(enabled, command).Validate();
        if (enabled && !explicitlyApproved)
            throw new InvalidOperationException("Activating a local print command requires explicit approval.");
        return MutateAsync(state => state with { Printing = configuration }, cancellationToken);
    }
}
