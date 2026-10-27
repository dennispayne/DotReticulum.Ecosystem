using System.Globalization;
using DotReticulum.Applications;
using DotReticulum.Applications.Runtime;
using DotReticulum.Core;

namespace DotTelemetry;

/// <summary>Small command line helpers, matching the behavior of DotReticulum's internal CLI helpers.</summary>
internal static class CliSupport
{
    private static readonly string[] LevelNames = ["CRITICAL", "ERROR", "WARNING", "NOTICE", "INFO", "VERBOSE", "DEBUG", "PATHING", "EXTREME"];

    public const int HashHexLength = Identity.HashSize * 2;

    public static bool TryParseHash(string? text, out byte[] hash)
    {
        hash = [];
        if (text is null || text.Length != HashHexLength) return false;
        try { hash = Convert.FromHexString(text); return true; }
        catch (FormatException) { return false; }
    }

    /// <summary>Loads the identity at <paramref name="path"/>, creating it when missing; without a path uses <c>storage/identities/&lt;name&gt;</c> of the node.</summary>
    public static Identity LoadOrCreateIdentity(ReticulumNode node, string? path, string name)
    {
        if (path is null) return node.LoadOrCreateIdentity(Path.Combine("identities", name));
        return RuntimeStateStore.LoadOrCreateIdentity(Path.GetFullPath(ExpandHome(path)));
    }

    /// <summary>Returns a thread-safe logger writing "[time] [LEVEL] message" lines for messages up to <paramref name="level"/>.</summary>
    public static Action<int, string> Logger(TextWriter writer, int level)
    {
        var gate = new object();
        return (messageLevel, message) =>
        {
            if (messageLevel > level) return;
            lock (gate) writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{LevelNames[Math.Clamp(messageLevel, 0, 8)]}] {message}");
        };
    }

    public static string SizeString(double bytes, string suffix = "B")
    {
        string[] units = ["", "K", "M", "G", "T", "P", "E", "Z"];
        var value = bytes;
        foreach (var unit in units)
        {
            if (Math.Abs(value) < 1000.0) return unit.Length == 0 ? value.ToString("0", CultureInfo.InvariantCulture) + " " + suffix : value.ToString("0.00", CultureInfo.InvariantCulture) + " " + unit + suffix;
            value /= 1000.0;
        }

        return value.ToString("0.00", CultureInfo.InvariantCulture) + "Y" + suffix;
    }

    private static string ExpandHome(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path == "~") return home;
        if (path.StartsWith("~/", StringComparison.Ordinal) || OperatingSystem.IsWindows() && path.StartsWith("~\\", StringComparison.Ordinal))
        {
            var suffix = OperatingSystem.IsWindows() ? path[1..].Replace('/', '\\') : path[1..];
            return home.TrimEnd(Path.DirectorySeparatorChar) + suffix;
        }

        return path;
    }
}
