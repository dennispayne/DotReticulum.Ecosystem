using System.Text;

namespace NomadNet;

public static class NomadNetClientProfile
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static void ValidateDisplayName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length is < 1 or > 80 || name.Any(char.IsControl))
            throw new InvalidDataException("Client display name must contain 1–80 printable characters.");
        _ = StrictUtf8.GetByteCount(name);
    }

    public static async Task SetDisplayNameAsync(NomadNetPropagation settings, Action<string> apply,
        string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(apply);
        ValidateDisplayName(name);
        await settings.UpdateSettingsAsync(current => current with { DisplayName = name }, cancellationToken).ConfigureAwait(false);
        apply(name);
    }
}
