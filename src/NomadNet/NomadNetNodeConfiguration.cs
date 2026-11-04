using System.Globalization;
using LXMF;

namespace NomadNet;

/// <summary>Application configuration mapped onto the existing LXMF propagation node.</summary>
public static class NomadNetNodeConfiguration
{
    public static LxmPropagationOptions CreateOptions(NomadNetSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.ValidateNode();
        return new LxmPropagationOptions
        {
            TransferLimit = settings.PropagationTransferLimitBytes,
            SyncLimit = Math.Max(settings.PropagationTransferLimitBytes, settings.PropagationSyncLimitBytes),
            StorageLimit = settings.PropagationStorageLimitBytes,
            StampCost = settings.PropagationStampCost,
            MaxPeers = settings.PropagationMaxPeers ?? new LxmPropagationOptions().MaxPeers,
            StaticPeers = settings.PropagationStaticPeers.Select(Convert.FromHexString).ToArray(),
            PrioritisedDestinations = settings.PrioritisedDestinations.Select(Convert.FromHexString).ToArray(),
        };
    }

    public static NomadNetSettings Change(NomadNetSettings settings, params string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(arguments);
        var changed = settings.Copy();
        switch (arguments)
        {
            case ["transfer", var value]: changed.PropagationTransferLimitBytes = Bytes(value, 1000, 1); break;
            case ["sync", var value]:
                changed.PropagationSyncLimitBytes = Math.Max(changed.PropagationTransferLimitBytes, Bytes(value, 1000, 1));
                break;
            case ["storage", var value]: changed.PropagationStorageLimitBytes = Bytes(value, 1_000_000, 0.005); break;
            case ["cost", var value]: changed.PropagationStampCost = Math.Max(13, Integer(value)); break;
            case ["peers", "default"]: changed.PropagationMaxPeers = null; break;
            case ["peers", var value]: changed.PropagationMaxPeers = Math.Max(0, Integer(value)); break;
            case ["static" or "priority", var hash, "on" or "off"]:
                var canonical = NomadNetAddress.NormalizeHash(hash);
                var list = arguments[0] == "static" ? changed.PropagationStaticPeers : changed.PrioritisedDestinations;
                if (arguments[2] == "on")
                {
                    if (!list.Contains(canonical, StringComparer.OrdinalIgnoreCase)) list.Add(canonical);
                }
                else list.RemoveAll(value => value.Equals(canonical, StringComparison.OrdinalIgnoreCase));
                break;
            default:
                throw new ArgumentException("Usage: node-config transfer|sync <KB>, storage <MB>, cost <13-256>, peers <default|0-65536>, static|priority <hash> <on|off>.");
        }
        changed.Validate();
        return changed;
    }

    internal static long Bytes(string value, int multiplier, double minimum)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var units) || !double.IsFinite(units))
            throw new InvalidDataException("Expected a finite node size.");
        var bytes = Math.Max(minimum, units) * multiplier;
        if (!double.IsFinite(bytes) || bytes >= long.MaxValue)
            throw new InvalidDataException("Node size exceeds the native byte range.");
        return (long)bytes;
    }

    internal static int Integer(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result : throw new InvalidDataException("Expected a bounded node integer.");
}
