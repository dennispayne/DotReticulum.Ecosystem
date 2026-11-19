using System.Diagnostics;
using System.Reflection.PortableExecutable;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandDesktopProcessTests
{
    public static bool WindowsExecutableContractIsUnavailable => !OperatingSystem.IsWindows();

    [Fact(SkipWhen = nameof(WindowsExecutableContractIsUnavailable),
        Skip = "The WinExe PE subsystem and redirected Windows console contract require Windows.")]
    public async Task DesktopUsesGuiSubsystemAndCommandLineHelpKeepsRedirectedOutput()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "sideband.exe");
        Assert.True(File.Exists(executable), $"Expected the referenced Windows apphost at {executable}.");

        using (var stream = File.OpenRead(executable))
        using (var pe = new PEReader(stream))
        {
            Assert.Equal(Subsystem.WindowsGui, pe.PEHeaders.PEHeader?.Subsystem);
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("--help");
        Assert.True(process.Start());

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("sideband", await outputTask, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await errorTask);
    }
}
