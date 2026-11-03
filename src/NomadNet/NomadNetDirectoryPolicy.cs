namespace NomadNet;

public static class NomadNetDirectoryPolicy
{
    public static bool ShouldIdentify(NomadNetContact? contact) => contact?.IdentifyOnConnect == true;

    public static IReadOnlyList<NomadNetContact> KnownNodes(IEnumerable<NomadNetContact> contacts) =>
        contacts.Where(contact => contact.HostsNode)
            // The pinned primary uses Python XOR, 2^32 == 34, not exponentiation.
            .OrderBy(contact => contact.SortRank ?? 34)
            .ThenByDescending(contact => contact.TrustLevel)
            .ThenBy(contact => contact.Name, Comparer<string>.Create(CompareNames))
            .ToArray();

    private static int CompareNames(string? left, string? right)
    {
        var a = (left ?? "_").EnumerateRunes();
        var b = (right ?? "_").EnumerateRunes();
        while (true)
        {
            var nextA = a.MoveNext();
            var nextB = b.MoveNext();
            if (nextA != nextB) return nextA ? 1 : -1;
            if (!nextA) return 0;
            var comparison = a.Current.Value.CompareTo(b.Current.Value);
            if (comparison != 0) return comparison;
        }
    }
}
