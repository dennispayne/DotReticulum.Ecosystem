using System.Security.Cryptography;
using DotReticulum.Core;

namespace Sideband;

public sealed record SidebandPersistentProfileSnapshot(
    double? LastSynchronization, bool SynchronizationRetrying, IReadOnlyList<string> Notices);

/// <summary>Reads supported schedule state from the caller's private, consistent SQLite/WAL snapshot.</summary>
public static class SidebandPersistentProfile
{
    private const long MaximumDatabaseBytes = 128L * 1024 * 1024;
    private const int MaximumRows = 65_536;
    private static readonly string LastSync = Convert.ToHexString("lxmf.lastsync"u8);
    private static readonly string Retrying = Convert.ToHexString("lxmf.syncretrying"u8);
    private static readonly MsgPackLimits ScalarLimits = new()
    {
        MaxEncodedBytes = 9, MaxValueBytes = 0, MaxContainerEntries = 0, MaxValues = 1, MaxDepth = 0,
    };

    /// <summary>
    /// The caller must supply an already isolated snapshot, not an active upstream profile.
    /// Unknown properties stay archive-only. Missing/nil values use native defaults without deleting archive rows.
    /// </summary>
    public static Task<SidebandPersistentProfileSnapshot> ReadAsync(
        string databasePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var path = Path.GetFullPath(databasePath);
        return Task.Run(() =>
        {
            try { return Read(path, cancellationToken); }
            catch (InvalidDataException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }, cancellationToken);
    }

    private static SidebandPersistentProfileSnapshot Read(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidateFile(path, MaximumDatabaseBytes, required: true);
        ValidateFile(path + "-wal", MaximumDatabaseBytes, required: false);
        ValidateFile(path + "-shm", 8 * 1024 * 1024, required: false);
        using var database = new ProfileSqlite(path, token);
        var tables = database.Rows("SELECT type FROM sqlite_master WHERE name='persistent'", 1);
        if (tables.Count == 0)
            return new(null, false, ["No upstream persistent table exists; native synchronization state starts unset."]);
        if (tables[0][0] is not string type || type != "table")
            throw new InvalidDataException("The upstream persistent state must be a SQLite table.");
        var columns = database.Rows("PRAGMA table_info(persistent)", 3);
        if (columns.Count != 2 || !IsColumn(columns[0], "property", 1) || !IsColumn(columns[1], "value", 0))
            throw new InvalidDataException("Unsupported upstream persistent table schema.");
        var count = database.Rows("SELECT count(*) FROM persistent", 1).Single()[0];
        if (count is not long total || total > MaximumRows)
            throw new InvalidDataException("The upstream persistent table exceeds its row safety limit.");
        // Select only the two actual binary keys. SQL bounds the known cells before the native
        // wrapper allocates them; unknown properties and their potentially private values are never read.
        var rows = database.Rows(
            $"SELECT property, CASE WHEN typeof(value)='blob' AND length(value) BETWEEN 1 AND 9 THEN value END " +
            $"FROM persistent WHERE property IN (X'{LastSync}', X'{Retrying}')", 2);
        double? last = null;
        var retrying = false;
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            if (row[0] is not byte[] key || row[1] is not byte[] encoded)
                throw new InvalidDataException("A supported persistent schedule value is not a bounded MessagePack scalar.");
            try
            {
                object? value;
                try { value = MsgPack.Unpack(encoded, ScalarLimits); }
                catch (Exception exception) when (exception is FormatException or ArgumentException or OverflowException)
                {
                    throw new InvalidDataException("A supported persistent schedule value contains invalid MessagePack.", exception);
                }
                if (Convert.ToHexString(key) == LastSync)
                {
                    last = value switch
                    {
                        null => null,
                        long number => number,
                        ulong number => number,
                        double number when double.IsFinite(number) => number,
                        _ => throw new InvalidDataException("lxmf.lastsync must be nil or a finite numeric timestamp."),
                    };
                }
                else
                {
                    retrying = value switch
                    {
                        null => false,
                        bool flag => flag,
                        _ => throw new InvalidDataException("lxmf.syncretrying must be nil or a MessagePack boolean."),
                    };
                }
            }
            finally { CryptographicOperations.ZeroMemory(encoded); }
        }
        token.ThrowIfCancellationRequested();
        IReadOnlyList<string> notices = total == rows.Count ? [] :
            [$"{total - rows.Count} unsupported-by-synchronization-reader properties are retained; telemetry activation is handled separately by the profile importer."];
        return new(last, retrying, notices);
    }

    private static bool IsColumn(object?[] row, string name, long primaryKey) =>
        row.Length == 6 && row[1] is string column && column == name &&
        row[2] is string type && type.Equals("BLOB", StringComparison.OrdinalIgnoreCase) &&
        row[5] is long key && key == primaryKey;

    private static void ValidateFile(string path, long limit, bool required)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            if (required) throw new FileNotFoundException("The private profile database snapshot does not exist.", path);
            return;
        }
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length > limit)
            throw new InvalidDataException("The private profile database snapshot exceeds its file safety limits or uses a symbolic link.");
    }
}
