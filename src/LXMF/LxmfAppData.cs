using DotReticulum.Core;

namespace LXMF;

/// <summary>Helpers for <c>lxmf.delivery</c> announce app_data: <c>msgpack([display_name_bytes|nil, stamp_cost|nil, [supported_functionality]])</c>, with a legacy raw display name form.</summary>
public static class LxmfAppData
{
    /// <summary>Upstream <c>SF_COMPRESSION</c> supported-functionality flag.</summary>
    public const long SupportsCompression = 0;

    public static byte[] Build(string? displayName, int? stampCost = null) =>
        MsgPack.Pack(new object?[]
        {
            displayName is null ? null : System.Text.Encoding.UTF8.GetBytes(displayName),
            stampCost is > 0 and < 255 ? (long)stampCost.Value : null,
            new object?[] { SupportsCompression },
        });

    public static string? DisplayName(byte[]? appData)
    {
        if (appData is not { Length: > 0 }) return null;
        if (IsList(appData))
        {
            return MsgPack.TryUnpack(appData, out var value) && value is object?[] { Length: > 0 } list && list[0] is byte[] name
                ? System.Text.Encoding.UTF8.GetString(name).Replace("\0", "").Trim()
                : null;
        }

        return System.Text.Encoding.UTF8.GetString(appData);
    }

    public static int? StampCost(byte[]? appData)
    {
        if (appData is not { Length: > 0 } || !IsList(appData)) return null;
        if (MsgPack.TryUnpack(appData, out var value) && value is object?[] { Length: > 1 } list && list[1] is long cost && cost is > 0 and < 255) return (int)cost;
        return null;
    }

    /// <summary>Absent capability lists mean compression is supported; an explicit list must contain <c>SF_COMPRESSION</c>.</summary>
    public static bool? CompressionSupported(byte[]? appData)
    {
        if (appData is not { Length: > 0 }) return null;
        if (!IsList(appData)) return true;
        if (!MsgPack.TryUnpack(appData, out var value) || value is not object?[] list) return null;
        return list.Length < 3 || list[2] is not object?[] features || features.Contains(SupportsCompression);
    }

    private static bool IsList(byte[] data) => (data[0] >= 0x90 && data[0] <= 0x9f) || data[0] == 0xdc;
}
