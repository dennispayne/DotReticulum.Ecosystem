using System.Globalization;
using System.Text;
using System.Text.Json;
using LXMF;
using V = Sideband.SidebandTelemetryValue;

namespace Sideband;

public enum SidebandCommandQueueStatus { Queued, Empty, NoAddress, NotConnected, QueueFull }
public sealed record SidebandCommandQueueResult(SidebandCommandQueueStatus Status, Guid OperationId = default)
{
    public bool Queued => Status == SidebandCommandQueueStatus.Queued;
}

/// <summary>Request permissions are shared with telemetry, as in the primary application.</summary>
public sealed record SidebandCommandAuthorization(bool AllowRequestsFromAnyone = false,
    bool AllowRequestsFromTrusted = false, bool IgnoreUnknown = false);

/// <summary>Observed primary d069122 command contracts, using the existing LXMF command field.</summary>
public static class SidebandRemoteCommands
{
    public const int MaximumCommands = 64;
    public const int MaximumCommandBytes = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Null means the primary rejects an empty command. Unknown text is a remote plugin request, not local code.</summary>
    public static V? CreateCommandField(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (StrictUtf8.GetByteCount(content) > MaximumCommandBytes)
            throw new ArgumentException("Remote command text exceeds 4096 UTF-8 bytes.", nameof(content));
        if (content.Length == 0) return null;
        SidebandTelemetryCommand type;
        V argument;
        if (content.StartsWith("echo ", StringComparison.Ordinal))
        {
            var echo = content.Replace("echo ", "", StringComparison.Ordinal);
            if (echo.Length == 0) return null;
            type = SidebandTelemetryCommand.Echo;
            argument = V.From(StrictUtf8.GetBytes(echo));
        }
        else if (content.StartsWith("sig", StringComparison.Ordinal))
        {
            type = SidebandTelemetryCommand.SignalReport;
            argument = V.From(true);
        }
        else if (content.StartsWith("ping", StringComparison.Ordinal))
        {
            type = SidebandTelemetryCommand.Ping;
            argument = V.From(true);
        }
        else
        {
            type = SidebandTelemetryCommand.Plugin;
            argument = V.From(content);
        }
        return V.Array(V.Map(KeyValuePair.Create(V.From((long)type), argument)));
    }

    public static string? CreateBuiltinReply(V command, double? quality = null, double? rssi = null, double? snr = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Kind != SidebandTelemetryValueKind.Map)
            throw new InvalidDataException("A command must be a map.");
        if (command.Get((long)SidebandTelemetryCommand.TelemetryRequest) is not null) return null;
        if (command.Get((long)SidebandTelemetryCommand.Ping) is not null) return "Ping reply";
        if (command.Get((long)SidebandTelemetryCommand.Echo) is { } echo)
        {
            var data = echo.GetBinary() ?? throw new InvalidDataException("Echo payload must be binary UTF-8.");
            if (data.Length > MaximumCommandBytes) throw new InvalidDataException("Echo payload exceeds 4096 bytes.");
            return "Echo reply: " + StrictUtf8.GetString(data);
        }
        if (command.Get((long)SidebandTelemetryCommand.SignalReport) is null) return null;
        var lines = new List<string>(3);
        if (quality is { } q && double.IsFinite(q)) lines.Add("Link Quality: " + PhysicalNumber(q) + "%");
        if (rssi is { } r && double.IsFinite(r)) lines.Add("RSSI: " + PhysicalNumber(r) + " dBm");
        if (snr is { } s && double.IsFinite(s)) lines.Add("SNR: " + PhysicalNumber(s) + " dB");
        return lines.Count == 0 ? "No reception info available" : string.Join('\n', lines);
    }

    internal static SidebandCommandAuthorization Authorization(SidebandState state)
    {
        var ignore = false;
        if (state.ImportedSettings?.TryGetValue("lxmf_ignore_unknown", out var value) == true)
            ignore = value.ValueKind switch
            {
                JsonValueKind.True => true, JsonValueKind.False => false,
                _ => throw new InvalidDataException("lxmf_ignore_unknown must be boolean.")
            };
        return new(state.Telemetry.AllowRequestsFromAnyone, state.Telemetry.AllowRequestsFromTrusted, ignore);
    }

    private static string PhysicalNumber(double value)
    {
        var formatted = value.ToString("R", CultureInfo.InvariantCulture);
        var exponentAt = formatted.IndexOf('E');
        if (exponentAt >= 0)
        {
            var exponent = int.Parse(formatted.AsSpan(exponentAt + 1), CultureInfo.InvariantCulture);
            return formatted[..exponentAt] + "e" + (exponent < 0 ? "-" : "+") +
                Math.Abs(exponent).ToString("00", CultureInfo.InvariantCulture);
        }
        if (Math.Abs(value) >= 1e16)
        {
            var negative = formatted.StartsWith('-');
            var digits = negative ? formatted[1..] : formatted;
            var exponent = digits.Length - 1;
            var significant = digits.TrimEnd('0');
            return (negative ? "-" : "") + significant[0] +
                (significant.Length > 1 ? "." + significant[1..] : "") +
                "e+" + exponent.ToString("00", CultureInfo.InvariantCulture);
        }
        return formatted.Contains('.') ? formatted : formatted + ".0";
    }
}
