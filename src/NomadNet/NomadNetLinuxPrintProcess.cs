namespace NomadNet;

internal static class NomadNetLinuxPrintProcess
{
    public static Task<int?> TryRunSupervisorAsync(string[] arguments) =>
        SharedPrinting.LinuxPrintProcess.TryRunSupervisorAsync(arguments);
}
