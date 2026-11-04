using System.Text;
using DotReticulum.Core;

namespace NomadNet;

public sealed record NomadNetDirectoryImportData(IReadOnlyList<NomadNetContact> Contacts,
    IReadOnlyList<string> IgnoredDestinations, int SkippedAnnounces, IReadOnlyList<string> Warnings);

public sealed record NomadNetDirectoryImportResult(int AddedContacts, int AddedIgnoredDestinations,
    int SkippedAnnounces, IReadOnlyList<string> Warnings);

/// <summary>Explicit, additive import of a primary NomadNet directory and its independent ignored list.</summary>
public static class NomadNetDirectoryImport
{
    public const int MaximumDirectoryBytes = 5 * 1024 * 1024;
    public const int MaximumIgnoredBytes = 512 * 1024;
    public const int MaximumEntries = 10_000;

    public static NomadNetDirectoryImportData Parse(ReadOnlySpan<byte> directory, ReadOnlySpan<byte> ignored = default)
    {
        if (directory.Length > MaximumDirectoryBytes || ignored.Length > MaximumIgnoredBytes)
            throw new InvalidDataException("Primary directory or ignored list exceeds the import size limit.");
        object? decoded;
        try
        {
            decoded = MsgPack.UnpackApplicationValue(directory, new MsgPackLimits
            {
                MaxEncodedBytes = MaximumDirectoryBytes,
                MaxValueBytes = MaximumDirectoryBytes,
                MaxContainerEntries = MaximumEntries,
                MaxValues = 150_000,
                MaxDepth = 8,
            });
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException("Invalid primary directory MessagePack.", exception);
        }
        if (decoded is not Dictionary<object, object?> map || map.Count != 2 ||
            map.GetValueOrDefault("entry_list") is not object?[] entries ||
            map.GetValueOrDefault("announce_stream") is not object?[] announces)
            throw new InvalidDataException("Expected a primary directory with entry_list and announce_stream arrays.");
        var contacts = new Dictionary<string, NomadNetContact>(StringComparer.Ordinal);
        for (var index = 0; index < entries.Length; index++)
        {
            try
            {
                if (entries[index] is not object?[] { Length: >= 3 and <= 8 } entry ||
                    entry[0] is not byte[] { Length: 16 } hash)
                    throw new InvalidDataException("Expected 3–8 fields and a 16-byte source hash.");
                var name = entry[1] is null ? "Undefined" : Text(entry[1], "name");
                var contact = new NomadNetContact(Convert.ToHexStringLower(hash), name)
                {
                    TrustLevel = Integer(entry[2]),
                    HostsNode = entry.Length > 3 && Boolean(entry[3]),
                    PreferredDelivery = entry.Length > 4 && entry[4] is not null ? Integer(entry[4]) : 1,
                    IdentifyOnConnect = entry.Length > 5 && Boolean(entry[5]),
                    SortRank = entry.Length > 6 && entry[6] is not null ? Integer(entry[6]) : null,
                    Notes = entry.Length > 7 && entry[7] is not null ? Text(entry[7], "notes") : "",
                };
                // Persisted primary duplicates resolve to the last entry, not an implicit merge of policy.
                NomadNetStore.ValidateImportedContact(contact);
                contacts[contact.DestinationHash] = contact;
            }
            catch (Exception exception) when (exception is InvalidDataException or FormatException or OverflowException)
            {
                throw new InvalidDataException($"Invalid primary directory entry {index + 1}: {exception.Message}", exception);
            }
        }
        var ignoredHashes = new HashSet<string>(StringComparer.Ordinal);
        var invalidLines = 0;
        var lineCount = 0;
        var start = 0;
        for (var index = 0; index <= ignored.Length; index++)
        {
            if (index != ignored.Length && ignored[index] is not (10 or 13)) continue;
            var line = ignored[start..index];
            if (index < ignored.Length && ignored[index] == 13 && index + 1 < ignored.Length && ignored[index + 1] == 10) index++;
            start = index + 1;
            if (line.Length == 0) continue;
            if (++lineCount > MaximumEntries) throw new InvalidDataException("Primary ignored list exceeds 10,000 records.");
            if (line.Length != 32 || !IsHex(line)) { invalidLines++; continue; }
            ignoredHashes.Add(Encoding.ASCII.GetString(line).ToLowerInvariant());
        }
        IReadOnlyList<string> warnings = invalidLines == 0 ? [] :
            new[] { $"Skipped {invalidLines} malformed ignored-list lines (expected exactly 32 hexadecimal ASCII bytes)." };
        return new(Array.AsReadOnly(contacts.Values.ToArray()), Array.AsReadOnly(ignoredHashes.Order(StringComparer.Ordinal).ToArray()),
            announces.Length, warnings);
    }

    public static async Task<NomadNetDirectoryImportData> ReadAsync(string primaryProfileDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryProfileDirectory);
        var root = Path.GetFullPath(primaryProfileDirectory);
        var directory = await NomadNetContent.ReadBoundedAsync(Path.Combine(root, "storage", "directory"),
            MaximumDirectoryBytes, cancellationToken).ConfigureAwait(false);
        var ignoredPath = Path.Combine(root, "ignored");
        var ignored = File.Exists(ignoredPath)
            ? await NomadNetContent.ReadBoundedAsync(ignoredPath, MaximumIgnoredBytes, cancellationToken).ConfigureAwait(false)
            : [];
        cancellationToken.ThrowIfCancellationRequested();
        return Parse(directory, ignored);
    }

    private static bool IsHex(ReadOnlySpan<byte> value)
    {
        foreach (var character in value)
            if (character is not (>= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F'))
                return false;
        return true;
    }

    private static int Integer(object? value) => value is long number ? checked((int)number) :
        throw new InvalidDataException("Expected a bounded integer, not a coerced number.");

    private static bool Boolean(object? value) => value is bool boolean ? boolean :
        throw new InvalidDataException("Expected a boolean policy flag.");

    private static string Text(object? value, string name) => value is string text ? text :
        throw new InvalidDataException($"Expected a string {name}.");
}
