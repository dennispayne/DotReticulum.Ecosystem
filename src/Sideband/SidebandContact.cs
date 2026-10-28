namespace Sideband;

public sealed record SidebandContact(string DeliveryHash, string DisplayName)
{
    public static bool TryNormalizeDeliveryHash(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null || value.Length != 32) return false;
        try
        {
            var bytes = Convert.FromHexString(value);
            normalized = Convert.ToHexStringLower(bytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static SidebandContact Create(string displayName, string deliveryHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        var name = displayName.Trim();
        if (name.Length > 80) throw new ArgumentException("Contact names may contain at most 80 characters.", nameof(displayName));
        if (!TryNormalizeDeliveryHash(deliveryHash, out var normalized))
            throw new ArgumentException("An LXMF delivery address must be exactly 32 hexadecimal characters.", nameof(deliveryHash));
        return new SidebandContact(normalized, name);
    }
}
