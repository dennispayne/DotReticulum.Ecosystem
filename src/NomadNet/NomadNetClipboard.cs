using System.Text;

namespace NomadNet;

public static class NomadNetClipboard
{
    public const int MaximumBytes = 1_000_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool TryCopy(string? text, TextWriter output, Action<string> log, bool enabled = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(log);
        if (!enabled)
        {
            log("Clipboard copying is disabled; explicitly enable OSC52 in a trusted terminal.");
            return false;
        }
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            if (text.Length > MaximumBytes || StrictUtf8.GetByteCount(text) > MaximumBytes)
                throw new InvalidDataException("Clipboard text exceeds the one-megabyte limit.");
            output.Write("\u001b]52;c;" + Convert.ToBase64String(StrictUtf8.GetBytes(text)) + "\a");
            output.Flush();
            return true;
        }
        catch (Exception exception) when (exception is EncoderFallbackException or InvalidDataException or IOException or ObjectDisposedException)
        {
            log("Clipboard copy failed: " + exception.Message);
            return false;
        }
    }

    public static string BrowserUrl(string? destinationHash, string? path,
        IReadOnlyDictionary<object, object?>? requestData = null)
    {
        if (destinationHash is null) return "";
        var result = new StringBuilder(NomadNetAddress.NormalizeHash(destinationHash) + ":");
        var bytes = result.Length;
        Append(path ?? "");
        if (requestData is not null)
        {
            if (requestData.Count > 1024) throw new InvalidDataException("Browser URL exceeds the form-field limit.");
            var separator = '`';
            foreach (var (key, value) in requestData)
            {
                if (key is not string name || !name.StartsWith("var_", StringComparison.Ordinal)) continue;
                if (value is not string text) throw new ArgumentException("Browser URL requires string form values.", nameof(requestData));
                Append(separator.ToString());
                Append(name[4..]);
                Append("=");
                Append(text);
                separator = '|';
            }
        }
        return result.ToString();

        void Append(string text)
        {
            if (text.Length > MaximumBytes || StrictUtf8.GetByteCount(text) > MaximumBytes - bytes)
                throw new InvalidDataException("Browser URL exceeds its byte limit.");
            bytes += StrictUtf8.GetByteCount(text);
            result.Append(text);
        }
    }
}
