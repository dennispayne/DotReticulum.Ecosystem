using System.Globalization;
using System.Text;
using V = Sideband.SidebandTelemetryValue;
using K = Sideband.SidebandTelemetryValueKind;

namespace Sideband;

internal static class SidebandMqttPayload
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static byte[] Encode(V value) => value.Kind switch
    {
        K.Nil => [],
        K.Boolean => Utf8.GetBytes(value.Boolean == true ? "True" : "False"),
        K.Integer => Utf8.GetBytes(value.Integer!.Value.ToString(CultureInfo.InvariantCulture)),
        K.UnsignedInteger => Utf8.GetBytes(value.UnsignedInteger!.Value.ToString(CultureInfo.InvariantCulture)),
        K.Number => Utf8.GetBytes(Float(value.Number!.Value)),
        K.Text => Utf8.GetBytes(value.Text!),
        K.Binary => value.GetBinary()!,
        _ => throw new FormatException("Primary MQTT payloads cannot encode arrays or maps.")
    };

    internal static string Float(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        var text = value.ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant();
        var negative = text.StartsWith('-');
        if (negative) text = text[1..];
        var e = text.IndexOf('e');
        var exponent = e < 0 ? 0 : int.Parse(text.AsSpan(e + 1), CultureInfo.InvariantCulture);
        var mantissa = e < 0 ? text : text[..e];
        var point = mantissa.IndexOf('.');
        var digits = mantissa.Replace(".", "", StringComparison.Ordinal);
        var decimalPosition = (point < 0 ? mantissa.Length : point) + exponent;
        var leading = 0;
        while (leading < digits.Length - 1 && digits[leading] == '0') leading++;
        digits = digits[leading..].TrimEnd('0');
        decimalPosition -= leading;
        if (digits.Length == 0) return negative ? "-0.0" : "0.0";
        exponent = decimalPosition - 1;
        string result;
        if (exponent is < -4 or >= 16)
        {
            var significand = digits.Length == 1 ? digits : digits[0] + "." + digits[1..];
            result = significand + "e" + (exponent < 0 ? "-" : "+") +
                Math.Abs(exponent).ToString("D2", CultureInfo.InvariantCulture);
        }
        else if (decimalPosition <= 0) result = "0." + new string('0', -decimalPosition) + digits;
        else if (decimalPosition >= digits.Length) result = digits + new string('0', decimalPosition - digits.Length) + ".0";
        else result = digits[..decimalPosition] + "." + digits[decimalPosition..];
        return negative ? "-" + result : result;
    }
}
