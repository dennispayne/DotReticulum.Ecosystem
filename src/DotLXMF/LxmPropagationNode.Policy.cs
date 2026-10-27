using DotReticulum.Core;

namespace DotLXMF;

public sealed partial class LxmPropagationNode
{
    private readonly HashSet<string> _allowedIdentities = [];
    private readonly HashSet<string> _controlIdentities = [];
    private long _storageLimit;
    private bool _requiresAuthentication;
    private bool _enabled = true;
    private int StampCost => Math.Max(13, _options.StampCost);

    public bool Enabled { get { lock (_gate) return _enabled; } }
    public bool RequiresAuthentication { get { lock (_gate) return _requiresAuthentication; } }
    public long StorageLimit => Interlocked.Read(ref _storageLimit);

    private void InitializePolicy()
    {
        _storageLimit = _options.StorageLimit;
        _requiresAuthentication = _options.RequireAuthentication;
        foreach (var hash in _options.AllowedIdentities) _allowedIdentities.Add(Convert.ToHexStringLower(hash));
        foreach (var hash in _options.ControlAllowedIdentities) _controlIdentities.Add(Convert.ToHexStringLower(hash));
    }

    public void SetAuthentication(bool required)
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); _requiresAuthentication = required; }
    }

    public bool IdentityAllowed(Identity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (_gate) return !_requiresAuthentication || _allowedIdentities.Contains(Convert.ToHexStringLower(identity.Hash));
    }

    public void AllowIdentity(ReadOnlySpan<byte> hash) => ChangePolicy(_allowedIdentities, hash, true);
    public void DisallowIdentity(ReadOnlySpan<byte> hash) => ChangePolicy(_allowedIdentities, hash, false);
    public void AllowControl(ReadOnlySpan<byte> hash) => ChangePolicy(_controlIdentities, hash, true);
    public void DisallowControl(ReadOnlySpan<byte> hash) => ChangePolicy(_controlIdentities, hash, false);
    public void Prioritise(ReadOnlySpan<byte> hash) => ChangePolicy(_prioritised, hash, true);
    public void Unprioritise(ReadOnlySpan<byte> hash) => ChangePolicy(_prioritised, hash, false);

    private void ChangePolicy(HashSet<string> entries, ReadOnlySpan<byte> hash, bool add)
    {
        if (hash.Length != Identity.HashSize) throw new ArgumentException("Identity and destination hashes must be 16 bytes.", nameof(hash));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = Convert.ToHexStringLower(hash);
            if (!add) entries.Remove(key);
            else
            {
                if (!entries.Contains(key) && entries.Count >= 65_536) throw new InvalidOperationException("Propagation policy capacity reached.");
                entries.Add(key);
            }
        }
    }

    public void SetMessageStorageLimit(double? kilobytes = null, double? megabytes = null, double? gigabytes = null)
    {
        var bytes = (kilobytes ?? 0) * 1000 + (megabytes ?? 0) * 1_000_000 + (gigabytes ?? 0) * 1_000_000_000;
        if (!double.IsFinite(bytes) || bytes < 1 || bytes >= long.MaxValue) throw new ArgumentOutOfRangeException(nameof(kilobytes));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Interlocked.Exchange(ref _storageLimit, (long)bytes);
            CleanStore();
        }
    }

    internal void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ScheduleAnnounce();
            _enabled = enabled;
        }
    }
}
