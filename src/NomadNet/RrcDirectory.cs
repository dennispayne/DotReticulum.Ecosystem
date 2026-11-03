using System.Text.RegularExpressions;

namespace NomadNet;

public sealed record RrcWhoEntry(string? Nick, string Hash);
public sealed record RrcWhoResult(string Room, IReadOnlyList<RrcWhoEntry> Entries);

public static partial class RrcDirectory
{
    public static RrcWhoResult? ParseWho(string text)
    {
        const string prefix = "members in ";
        if (!text.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var separator = text.IndexOf(": ", prefix.Length, StringComparison.Ordinal);
        if (separator < 0) return null;
        var room = text[prefix.Length..separator].Trim().ToLowerInvariant();
        if (room.Length == 0) return null;
        var body = text[(separator + 2)..].Trim();
        var entries = new List<RrcWhoEntry>();
        foreach (Match match in WhoEntryPattern().Matches(body))
        {
            if (entries.Count >= 4096) throw new InvalidDataException("RRC membership notice exceeds the local bound.");
            entries.Add(match.Groups["nick"].Success
                ? new(match.Groups["nick"].Value.Trim(), match.Groups["prefix"].Value.ToLowerInvariant())
                : new(null, match.Groups["hash"].Value.ToLowerInvariant()));
        }
        return new(room, entries);
    }

    public static IReadOnlyDictionary<string, string?>? ParseRooms(string text)
    {
        if (text.Trim() == "No public rooms registered") return new Dictionary<string, string?>();
        var lines = text.Split('\n');
        if (!lines[0].TrimStart().StartsWith("Registered public rooms", StringComparison.Ordinal)) return null;
        var rooms = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1))
        {
            var value = line.Trim();
            if (value.Length == 0) continue;
            var separator = value.IndexOf(" - ", StringComparison.Ordinal);
            var name = separator >= 0 ? value[..separator].Trim().ToLowerInvariant() : value.TrimStart('#').ToLowerInvariant();
            var topic = separator >= 0 ? value[(separator + 3)..].Trim() : "";
            if (rooms.Count >= 4096) throw new InvalidDataException("RRC room-list notice exceeds the local bound.");
            rooms[name] = topic.Length == 0 ? null : topic;
        }
        return rooms;
    }

    internal static (string Name, string? Topic)? ParseRoomFragment(string text)
    {
        var match = RoomFragmentPattern().Match(text);
        if (!match.Success) return null;
        var topic = match.Groups["topic"].Value.Trim();
        return (match.Groups["name"].Value.ToLowerInvariant(), topic.Length == 0 ? null : topic);
    }

    [GeneratedRegex(@"(?:^|,\s)(?:(?<hash>[0-9a-fA-F]{64})|(?<nick>.+?)\s\((?<prefix>[0-9a-fA-F]{12})\))(?=,\s|$)")]
    private static partial Regex WhoEntryPattern();
    [GeneratedRegex(@"^  (?<name>\S+?)(?: - (?<topic>.*))?$")]
    private static partial Regex RoomFragmentPattern();
}
