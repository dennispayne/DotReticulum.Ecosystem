using DotReticulum.Core;

namespace DotLXST;

/// <summary>Immutable identity policy; blocked identities always take precedence.</summary>
public sealed class TelephoneCallerPolicy
{
    private readonly HashSet<string>? _allowed;
    private readonly HashSet<string> _blocked;
    private readonly Func<Identity, bool>? _authorize;

    public TelephoneCallerPolicy(IEnumerable<ReadOnlyMemory<byte>>? allowedIdentityHashes = null,
        IEnumerable<ReadOnlyMemory<byte>>? blockedIdentityHashes = null, Func<Identity, bool>? authorize = null)
    {
        _allowed = allowedIdentityHashes is null ? null : Snapshot(allowedIdentityHashes);
        _blocked = blockedIdentityHashes is null ? [] : Snapshot(blockedIdentityHashes);
        _authorize = authorize;
    }

    public bool IsAllowed(Identity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var hash = Convert.ToHexString(identity.Hash);
        return !_blocked.Contains(hash) && (_allowed?.Contains(hash) ?? true) && (_authorize?.Invoke(identity) ?? true);
    }

    private static HashSet<string> Snapshot(IEnumerable<ReadOnlyMemory<byte>> hashes)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hash in hashes)
        {
            if (hash.Length != 16) throw new ArgumentException("Caller identity hashes must contain 16 bytes.", nameof(hashes));
            result.Add(Convert.ToHexString(hash.Span));
        }
        return result;
    }
}
