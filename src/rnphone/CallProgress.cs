namespace Rnphone;

public static class CallProgress
{
    public static TimeSpan UpdateInterval(bool hasHardwareDisplay = false) =>
        TimeSpan.FromSeconds(hasHardwareDisplay ? 1 : .25);

    public static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        var seconds = checked((long)Math.Round(elapsed.TotalSeconds));
        var parts = new List<string>(4);
        foreach (var (size, suffix) in new[] { (86400L, "d"), (3600L, "h"), (60L, "m"), (1L, "s") })
        {
            var value = seconds / size;
            seconds %= size;
            if (value != 0) parts.Add(value.ToString(System.Globalization.CultureInfo.InvariantCulture) + suffix);
        }
        return parts.Count switch
        {
            0 => "0s",
            1 => parts[0],
            _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
        };
    }

    public static async Task DisplayAsync(Func<TimeSpan> elapsed, TextWriter output,
        CancellationToken cancellationToken, TimeProvider? timeProvider = null, bool hasHardwareDisplay = false)
    {
        ArgumentNullException.ThrowIfNull(elapsed);
        ArgumentNullException.ThrowIfNull(output);
        using var timer = new PeriodicTimer(UpdateInterval(hasHardwareDisplay), timeProvider ?? TimeProvider.System);
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            await output.WriteAsync("\rCall duration: " + FormatElapsed(elapsed()) + "    ");
            await output.FlushAsync(cancellationToken);
        } while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }
}
