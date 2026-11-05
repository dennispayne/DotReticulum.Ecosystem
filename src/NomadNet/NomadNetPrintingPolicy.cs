using System.Globalization;
using System.Text;

namespace NomadNet;

/// <summary>Original text presentation and bounded factual primary printing contracts.</summary>
public static class NomadNetPrintingPolicy
{
    public const int MaximumTemplateBytes = 16 * 1024;
    public const int MaximumJobBytes = 2 * 1024 * 1024;
    public const string DefaultTemplate = "Message from {origin}\nSent: {stime}\nReceived: {rtime}\nTitle: {mtitle}\n\n{mbody}\n";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static bool ShouldPrint(NomadNetSettings settings, string sourceHash, int savedTrustLevel) =>
        settings.PrintMessages && (settings.PrintAllMessages ||
            settings.PrintTrustedMessages && savedTrustLevel == 255 ||
            settings.AllowedMessagePrintDestinations.Contains(NomadNetAddress.NormalizeHash(sourceHash), StringComparer.Ordinal));

    public static string Origin(string sourceHash, NomadNetContact? contact)
    {
        var hash = NomadNetAddress.NormalizeHash(sourceHash);
        if (contact is null) return "<" + hash + ">";
        var name = NomadNetTerminalText.Safe(contact.Name);
        if (string.IsNullOrWhiteSpace(name)) return "<" + hash + ">";
        return contact.TrustLevel is 0 or 1 ? name + " <" + hash + ">" : name;
    }

    public static string Render(string template, NomadNetMessage message, string origin, DateTimeOffset received)
    {
        ValidateTemplate(template);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["origin"] = origin,
            ["stime"] = message.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ["rtime"] = received.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ["mtitle"] = message.Title.Length == 0 ? "None" : message.Title,
            ["mbody"] = message.PrintingContent ?? message.Content,
        };
        var rendered = Format(template, values);
        if (Utf8.GetByteCount(rendered) > MaximumJobBytes)
            throw new InvalidDataException("Rendered print message exceeds 2 MiB.");
        return rendered;
    }

    public static void ValidateTemplate(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (Utf8.GetByteCount(template) > MaximumTemplateBytes || template.Contains('\0'))
            throw new InvalidDataException("Print template exceeds 16 KiB or contains NUL.");
        _ = Format(template, new Dictionary<string, string>(StringComparer.Ordinal)
        { ["origin"] = "", ["stime"] = "", ["rtime"] = "", ["mtitle"] = "", ["mbody"] = "" });
    }

    public static async Task<string> ReadTemplateAsync(string path, CancellationToken cancellationToken = default)
    {
        var bytes = await NomadNetLocalFiles.ReadAsync(NomadNetConfigImport.ExpandHome(path), MaximumTemplateBytes,
            cancellationToken).ConfigureAwait(false);
        var template = Utf8.GetString(bytes);
        ValidateTemplate(template);
        return template;
    }

    private static string Format(string template, Dictionary<string, string> values)
    {
        var result = new StringBuilder();
        for (var index = 0; index < template.Length; index++)
        {
            var character = template[index];
            if (character is not ('{' or '}')) { result.Append(character); continue; }
            if (index + 1 < template.Length && template[index + 1] == character)
            {
                result.Append(character);
                index++;
                continue;
            }
            if (character == '}') throw new InvalidDataException("Unmatched print template brace.");
            var end = template.IndexOf('}', index + 1);
            if (end < 0 || !values.TryGetValue(template[(index + 1)..end], out var value))
                throw new InvalidDataException("Print templates support only origin, stime, rtime, mtitle, mbody and doubled braces; Python expressions/format specifiers are unsupported.");
            if (value.Length > MaximumJobBytes || result.Length > MaximumJobBytes - value.Length)
                throw new InvalidDataException("Rendered print message exceeds its size bound.");
            result.Append(value);
            index = end;
        }
        return result.ToString();
    }

    /// <summary>POSIX-style argv parsing, not shell evaluation; final job path is added separately.</summary>
    public static IReadOnlyList<string> CommandArguments(string command)
        => SharedPrinting.PrintCommand.Arguments(command);
}
