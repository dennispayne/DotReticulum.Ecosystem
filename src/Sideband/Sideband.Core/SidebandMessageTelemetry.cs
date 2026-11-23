using System.Globalization;
using System.Text;
using LXMF;
using V = Sideband.SidebandTelemetryValue;
using K = Sideband.SidebandTelemetryValueKind;

namespace Sideband;

public static class SidebandMessageTelemetry
{
    public const int MaximumCopyCharacters = 1_048_576;

    public static string CopyText(string packedHex)
    {
        var report = Decode(packedHex);
        var value = V.Map(report.Sensors.Where(sensor => sensor.IsKnown).Select(sensor =>
            KeyValuePair.Create(V.From(sensor.Name), sensor.Data)).ToArray());
        return Format(value);
    }

    internal static SidebandTelemetryReport Decode(string packedHex)
    {
        ArgumentNullException.ThrowIfNull(packedHex);
        if (packedHex.Length > SidebandTelemetryCodec.MaxEncodedBytes * 2)
            throw new InvalidDataException("Selected-message telemetry exceeds the packed size limit.");
        return SidebandTelemetryCodec.Decode(Convert.FromHexString(packedHex));
    }

    internal static string? Capture(LxMessage message)
    {
        if (!message.TryGetField(LxmField.Telemetry, out var field)) return null;
        if (field is not byte[] data) throw new InvalidDataException("Message telemetry must be packed binary.");
        _ = SidebandTelemetryCodec.Decode(data);
        return Convert.ToHexStringLower(data);
    }

    internal static string? Import(LxMessage message, object? extra, List<string> notices)
    {
        string? snapshot = null;
        try { snapshot = Capture(message); }
        catch (Exception error) when (error is FormatException or ArgumentException or InvalidDataException or OverflowException)
        { notices.Add("A message telemetry field remains archive-only: " + error.GetType().Name + "."); }
        if (extra is null) return snapshot;
        try
        {
            if (extra is not byte[] data) throw new InvalidDataException("Message extras must be packed binary.");
            var extras = V.Decode(data);
            if (extras.Kind != K.Map) throw new InvalidDataException("Message extras must be a map.");
            if (extras.Get("packed_telemetry") is { } packed)
            {
                var bytes = packed.GetBinary() ?? throw new InvalidDataException("Packed message telemetry must be binary.");
                _ = SidebandTelemetryCodec.Decode(bytes);
                snapshot = Convert.ToHexStringLower(bytes);
            }
            if (snapshot is not null)
            {
                var (rssi, snr, quality) = (Metric("rssi"), Metric("snr"), Metric("q"));
                if (rssi is not null || snr is not null || quality is not null)
                    snapshot = Convert.ToHexStringLower(SidebandTelemetryCodec.Encode(
                        WithLink(Decode(snapshot), rssi, snr, quality)));
            }

            double? Metric(string name)
            {
                var value = extras.Get(name);
                return value is null || value.IsNil ? null :
                    value.Number ?? throw new InvalidDataException("A saved link observation is not numeric.");
            }
        }
        catch (Exception error) when (error is FormatException or ArgumentException or InvalidDataException or OverflowException)
        { notices.Add("Message telemetry extras could not fully activate; valid captured telemetry is retained: " + error.GetType().Name + "."); }
        return snapshot;
    }

    internal static SidebandTelemetryReport WithLink(SidebandTelemetryReport report, double? rssi, double? snr, double? quality)
    {
        if (rssi is null && snr is null && quality is null) return report;
        var physical = SidebandTelemetryCodec.FromData(SidebandTelemetrySensorId.PhysicalLink,
            V.Fields(("rssi", Number(rssi)), ("snr", Number(snr)), ("q", Number(quality))));
        var sensors = report.Sensors.Select(sensor => sensor.Id == physical.Id ? physical : sensor);
        if (report.Get(physical.Id) is null) sensors = sensors.Append(physical);
        return SidebandTelemetryCodec.FromSensors(sensors.ToArray());

        static V Number(double? value) => value is { } number
            ? V.From(SidebandTelemetryCodec.RoundBinaryDecimal(number, 1)) : V.Nil;
    }

    internal static string CaptureObserved(SidebandTelemetryReport report, LxMessage message)
    {
        if (message.Rssi is not null and not 0 || message.Snr is not null and not 0 || message.Quality is not null and not 0)
            report = WithLink(report, message.Rssi, message.Snr, message.Quality);
        return Convert.ToHexStringLower(SidebandTelemetryCodec.Encode(report));
    }

    internal static string Format(V value)
    {
        var output = new StringBuilder();
        Write(value, 0);
        return output.ToString();

        void Add(string text)
        {
            if (text.Length > MaximumCopyCharacters - output.Length)
                throw new InvalidDataException("Selected-message telemetry text exceeds the copy limit.");
            output.Append(text);
        }

        void Quote(string text, bool binary)
        {
            var quote = text.Contains('\'') && !text.Contains('"') ? '"' : '\'';
            Add(binary ? "b" : "");
            Add(quote.ToString());
            foreach (var rune in text.EnumerateRunes())
            {
                var number = rune.Value;
                if (number == quote || number == '\\') Add("\\" + (char)number);
                else if (number == '\t') Add("\\t");
                else if (number == '\n') Add("\\n");
                else if (number == '\r') Add("\\r");
                else if (binary ? number is >= 32 and <= 126 : Printable(rune)) Add(rune.ToString());
                else Add(number <= 255 ? "\\x" + number.ToString("x2", CultureInfo.InvariantCulture) :
                    number <= 65535 ? "\\u" + number.ToString("x4", CultureInfo.InvariantCulture) :
                    "\\U" + number.ToString("x8", CultureInfo.InvariantCulture));
            }
            Add(quote.ToString());
        }

        void Write(V item, int depth)
        {
            if (depth > SidebandTelemetryCodec.MaxDepth)
                throw new InvalidDataException("Selected-message telemetry exceeds the nesting limit.");
            switch (item.Kind)
            {
                case K.Nil: Add("None"); break;
                case K.Boolean: Add(item.Boolean == true ? "True" : "False"); break;
                case K.Integer: Add(item.Integer!.Value.ToString(CultureInfo.InvariantCulture)); break;
                case K.UnsignedInteger: Add(item.UnsignedInteger!.Value.ToString(CultureInfo.InvariantCulture)); break;
                case K.Number: Add(Float(item.Number!.Value)); break;
                case K.Text: Quote(item.Text!, false); break;
                case K.Binary: Quote(Encoding.Latin1.GetString(item.GetBinary()!), true); break;
                case K.Array:
                    Add("[");
                    for (var index = 0; index < item.Items.Count; index++)
                    {
                        if (index != 0) Add(", ");
                        Write(item.Items[index], depth + 1);
                    }
                    Add("]");
                    break;
                case K.Map:
                    Add("{");
                    for (var index = 0; index < item.Members.Count; index++)
                    {
                        if (index != 0) Add(", ");
                        Write(item.Members[index].Key, depth + 1);
                        Add(": ");
                        Write(item.Members[index].Value, depth + 1);
                    }
                    Add("}");
                    break;
                default: throw new InvalidDataException("Unknown selected-message telemetry value.");
            }
        }
    }

    private static bool Printable(Rune rune) => rune.Value == 32 || Rune.GetUnicodeCategory(rune) is not
        (UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or
         UnicodeCategory.OtherNotAssigned or UnicodeCategory.PrivateUse or UnicodeCategory.SpaceSeparator or
         UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator);

    private static string Float(double value)
    {
        var raw = value.ToString("R", CultureInfo.InvariantCulture);
        var negative = raw.StartsWith('-');
        var unsigned = negative ? raw[1..] : raw;
        var exponentIndex = unsigned.IndexOf('E');
        var exponent = exponentIndex < 0 ? 0 : int.Parse(unsigned[(exponentIndex + 1)..], CultureInfo.InvariantCulture);
        var mantissa = exponentIndex < 0 ? unsigned : unsigned[..exponentIndex];
        var dot = mantissa.IndexOf('.');
        var position = (dot < 0 ? mantissa.Length : dot) + exponent;
        var digits = mantissa.Replace(".", "", StringComparison.Ordinal);
        while (digits.Length > 1 && digits[0] == '0') { digits = digits[1..]; position--; }
        var sign = negative ? "-" : "";
        if (value != 0 && (Math.Abs(value) < 1e-4 || Math.Abs(value) >= 1e16))
        {
            digits = digits.TrimEnd('0');
            var tail = digits.Length > 1 ? "." + digits[1..] : "";
            var scientificExponent = position - 1;
            return sign + digits[0] + tail + "e" + (scientificExponent < 0 ? "-" : "+") +
                Math.Abs(scientificExponent).ToString("D2", CultureInfo.InvariantCulture);
        }
        return sign + (position <= 0 ? "0." + new string('0', -position) + digits :
            position >= digits.Length ? digits + new string('0', position - digits.Length) + ".0" :
            digits[..position] + "." + digits[position..]);
    }
}
