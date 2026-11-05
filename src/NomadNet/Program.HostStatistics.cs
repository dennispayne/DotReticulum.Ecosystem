namespace NomadNet;

internal static partial class Program
{
    private static async Task<bool> HandleHostedStatisticsAsync(string command, NomadNetHost? host, CancellationToken token)
    {
        if (!command.Equals("node stats", StringComparison.OrdinalIgnoreCase) &&
            !command.Equals("node stats reset", StringComparison.OrdinalIgnoreCase)) return false;
        var statistics = (host ?? throw new InvalidOperationException("Enable node hosting in settings.json.")).Statistics;
        if (command.Equals("node stats reset", StringComparison.OrdinalIgnoreCase))
        {
            await statistics.ResetAsync(token).ConfigureAwait(false);
            Console.WriteLine("Hosted statistics reset; media requests retained.");
        }
        PrintHostedStatistics(statistics.Snapshot);
        return true;
    }

    private static void PrintHostedStatistics(NomadNetHostedStatisticsSnapshot statistics) =>
        Console.WriteLine($"Hosted statistics: connections={statistics.NodeConnects} pages={statistics.ServedPageRequests} " +
            $"files={statistics.ServedFileRequests} media={statistics.ServedMediaRequests}");
}
