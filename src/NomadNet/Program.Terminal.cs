namespace NomadNet;

internal static partial class Program
{
    private sealed partial class TerminalSession
    {
        private string DisplayName(string value) => NomadNetTerminalControls.Name(value, propagation.Settings.SanitizeNames);
        private string Heading(string value) => NomadNetTerminalControls.Heading(value, propagation.Settings, !Console.IsOutputRedirected);

        public async Task StartTerminalAsync(bool firstRun, CancellationToken token)
        {
            await NomadNetTerminalControls.IntroAsync(propagation.Settings, Console.Out,
                !Console.IsOutputRedirected, token).ConfigureAwait(false);
            Console.WriteLine(Heading(NomadNetTerminalControls.MenuText(propagation.Settings)));
            if (firstRun) Console.WriteLine(Heading(_guide.Open("start")));
        }

        private async Task<string?> TerminalCommandAsync(string command, CancellationToken token)
        {
            if (command == "menu")
            {
                Console.WriteLine(Heading(NomadNetTerminalControls.MenuText(propagation.Settings)));
                Console.WriteLine("Use menu select <number>; menu watch <1–60 frames> refreshes current network status.");
                return null;
            }
            if (command.StartsWith("menu select ", StringComparison.Ordinal))
            {
                if (!int.TryParse(command["menu select ".Length..], out var selected))
                    throw new ArgumentException("Menu selection requires a number.");
                return NomadNetTerminalControls.Select(propagation.Settings, selected);
            }
            if (command.StartsWith("menu watch ", StringComparison.Ordinal))
            {
                if (!int.TryParse(command["menu watch ".Length..], out var frames))
                    throw new ArgumentException("Watch requires1–60 frames.");
                await NomadNetTerminalControls.WatchAsync(propagation.Settings, frames,
                    () => $"Network {DateTimeOffset.Now:HH:mm:ss}; " +
                        $"known={node.Router.Engine.Known.ExportSnapshot().Count()}; " +
                        $"utility={(_utilityWork.IsCompleted ? "idle" : "working")}",
                    Console.Out, !Console.IsOutputRedirected, token).ConfigureAwait(false);
                return null;
            }
            return command;
        }
    }
}
