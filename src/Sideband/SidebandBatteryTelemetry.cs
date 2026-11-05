using System.Globalization;
using System.Text;
using V = Sideband.SidebandTelemetryValue;

namespace Sideband;

/// <summary>Caller-owned source; reads must honor cancellation and only run after explicit collection opt-in.</summary>
public interface ISidebandBatterySource
{
    Task<SidebandTelemetryReading> ReadAsync(CancellationToken cancellationToken);
}

internal sealed class SidebandBatterySource : ISidebandBatterySource
{
    internal const int MaximumUeventBytes = 8192;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public Task<SidebandTelemetryReading> ReadAsync(CancellationToken cancellationToken) =>
        ReadAtAsync(OperatingSystem.IsLinux() ? "/sys/class/power_supply" : null, cancellationToken);

    internal static async Task<SidebandTelemetryReading> ReadAtAsync(string? directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (directory is null) return Unavailable("The primary desktop battery provider is Linux-only; no device was opened.");
        string? selected = null;
        for (var index = 0; index < 10; index++)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, "BAT" + index.ToString(CultureInfo.InvariantCulture));
            if (Directory.Exists(path)) { selected = path; break; }
        }
        if (selected is null) return Unavailable("No Linux BAT0..BAT9 battery node is available.");
        try
        {
            await using var stream = new FileStream(Path.Combine(selected, "uevent"), new FileStreamOptions
            {
                Access = FileAccess.Read, Mode = FileMode.Open, Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
            var bytes = new byte[MaximumUeventBytes + 1];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(length), token).ConfigureAwait(false);
                if (read == 0) break;
                length += read;
            }
            if (length > MaximumUeventBytes) throw new InvalidDataException("Battery uevent exceeds8192 bytes.");
            return Parse(bytes.AsSpan(0, length));
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return Unavailable("Could not read the selected Linux battery uevent (" + error.GetType().Name + ").");
        }
    }

    internal static SidebandTelemetryReading Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumUeventBytes) throw new InvalidDataException("Battery uevent exceeds8192 bytes.");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = new StringReader(StrictUtf8.GetString(bytes));
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            var equals = trimmed.IndexOf('=');
            if (equals <= 0 || !fields.TryAdd(trimmed[..equals], trimmed[(equals + 1)..]))
                throw new InvalidDataException("Battery uevent has a malformed or duplicate field.");
        }
        if (!fields.TryGetValue("POWER_SUPPLY_STATUS", out var status) ||
            !fields.TryGetValue("POWER_SUPPLY_CAPACITY", out var capacity) ||
            !double.TryParse(capacity, NumberStyles.Float, CultureInfo.InvariantCulture, out var charge) ||
            !double.IsFinite(charge) || charge is < 0 or > 100)
            throw new InvalidDataException("Battery uevent requires a finite capacity0..100 and status.");
        return SidebandTelemetryCodec.FromData(SidebandTelemetrySensorId.Battery,
            V.Fields(("charge_percent", V.From(SidebandTelemetryCodec.RoundBinaryDecimal(charge, 1))),
                ("charging", V.From(status == "Charging")), ("temperature", V.Nil)));
    }

    internal static SidebandTelemetryReading Unavailable(string error) =>
        new((byte)SidebandTelemetrySensorId.Battery, "battery", SidebandTelemetryStatus.Unavailable,
            V.Nil, V.Nil, [], [], error: error);
}
