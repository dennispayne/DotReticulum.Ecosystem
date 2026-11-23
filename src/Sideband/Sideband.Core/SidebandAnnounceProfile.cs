using DotReticulum.Core;

namespace Sideband;

internal static class SidebandAnnounceProfile
{
    private static readonly MsgPackLimits ExtraLimits = new()
    {
        MaxEncodedBytes = 4_096, MaxValueBytes = 1_024, MaxContainerEntries = 32, MaxValues = 128, MaxDepth = 4
    };

    internal static IReadOnlyList<SidebandAnnounce> Read(ProfileSqlite database, List<string> notices)
    {
        var table = database.Rows("SELECT type,sql FROM sqlite_master WHERE name='announce'", 1);
        if (table.Count == 0) return [];
        if (table[0][0] is not "table" || table[0][1] is not string sql ||
            !sql.StartsWith("CREATE TABLE ", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The primary announce store must be a SQLite table.");
        var columns = database.Rows("PRAGMA table_info(announce)", 7).Select(row => row[1] as string).ToArray();
        if (!columns.SequenceEqual(new[] { "id", "received", "source", "data", "dest_type" }) &&
            !columns.SequenceEqual(new[] { "id", "received", "source", "data", "dest_type", "extra" }))
            throw new InvalidDataException("Unsupported primary announce table schema.");
        var count = database.Rows("SELECT count(*) FROM announce", 1).Single()[0];
        if (count is not long total || total > 65_536)
            throw new InvalidDataException("The primary announce store exceeds the 65,536-row safety limit.");
        var extra = columns.Length == 6 ? "extra" : "NULL";
        var rows = database.Rows(
            "SELECT received,CASE WHEN typeof(source)='blob' AND length(source)=16 THEN source END," +
            "CASE WHEN typeof(data)='blob' AND length(data)<=4096 THEN data END,dest_type," +
            $"CASE WHEN {extra} IS NULL THEN NULL WHEN typeof({extra})='blob' AND length({extra})<=4096 THEN {extra} END," +
            $"CASE WHEN {extra} IS NULL OR (typeof({extra})='blob' AND length({extra})<=4096) THEN 1 ELSE 0 END " +
            "FROM (SELECT received,source,data,dest_type," + extra + " AS extra," +
            "ROW_NUMBER() OVER (PARTITION BY source ORDER BY received DESC,rowid DESC) AS position " +
            "FROM announce WHERE dest_type IN ('lxmf.delivery','lxmf.propagation')) " +
            $"WHERE position=1 ORDER BY received DESC LIMIT {SidebandAnnounce.MaximumHistory}", SidebandAnnounce.MaximumHistory);
        var result = new List<SidebandAnnounce>();
        foreach (var row in rows)
        {
            if (row[1] is not byte[] { Length: 16 } source || row[2] is not byte[] data ||
                row[3] is not string kind || row[5] is not 1L)
                throw new InvalidDataException("A selected primary announcement has invalid or oversized fields.");
            var seconds = Number(row[0]) ?? throw new InvalidDataException("A primary announcement lacks its receipt time.");
            DateTimeOffset received;
            try { received = DateTimeOffset.UnixEpoch.AddSeconds(seconds); }
            catch (ArgumentOutOfRangeException ex) { throw new InvalidDataException("A primary announce receipt time is outside the native date range.", ex); }
            SidebandAnnounce announce;
            try
            {
                announce = SidebandAnnounce.FromAppData(Convert.ToHexStringLower(source), kind, received, data);
                if (row[4] is byte[] packed)
                {
                    if (MsgPack.Unpack(packed, ExtraLimits) is not Dictionary<object, object?> extras)
                        throw new InvalidDataException("Primary announce extras must contain a MessagePack map.");
                    if (extras.GetValueOrDefault("link_stats") is { } stats)
                    {
                        if (stats is not Dictionary<object, object?> signal)
                            throw new InvalidDataException("Primary announce link statistics must contain a map or nil.");
                        announce = announce with { Signal = new(Number(signal.GetValueOrDefault("rssi")),
                            Number(signal.GetValueOrDefault("snr")), Number(signal.GetValueOrDefault("q"))) };
                    }
                }
                SidebandAnnounce.Validate(announce);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
            { throw new InvalidDataException("A selected primary announcement contains invalid metadata.", ex); }
            result.Add(announce);
        }
        result.Reverse();
        notices.Add($"Activated {result.Count} newest unique supported announcements from {total} archived rows; older/duplicate/other destination types and unrecognised extras remain archive-only.");
        notices.Add("Imported delivery stamp costs are unknown until the live LXMF router cache is available; the SQLite announce table alone does not retain those current costs. Historical signal values are observations, not live physical measurements.");
        return result.AsReadOnly();
    }

    private static double? Number(object? value) => value switch
    {
        null => null,
        long integer => integer,
        ulong integer => integer,
        double number when double.IsFinite(number) => number,
        _ => throw new InvalidDataException("A primary announcement measurement or timestamp must be finite numeric data or nil.")
    };
}
