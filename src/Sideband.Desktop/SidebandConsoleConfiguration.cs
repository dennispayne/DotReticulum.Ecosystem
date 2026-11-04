using System.Globalization;
using System.Text.Json;
using Sideband;

namespace Sideband.Desktop;

public sealed record SidebandConsoleSetting(string Category, string Alias, string Type,
    Func<SidebandState, string> Read, Func<SidebandState, string, CancellationToken, Task> Write);

/// <summary>Only aliases that activate native behavior are writable; archive keys are never treated as settings.</summary>
public sealed class SidebandConsoleConfiguration
{
    private readonly SidebandClient _client;
    private readonly List<SidebandConsoleSetting> _settings = [];

    public SidebandConsoleConfiguration(SidebandClient client)
    {
        _client = client;
        Text("general", "display_name", s => s.DisplayName, (s, v, t) => client.ConfigurePreferencesAsync(v, s.Theme, t));
        Flag("general", "dark_ui", s => s.Theme == "Dark", (s, v, t) => client.ConfigurePreferencesAsync(s.DisplayName, v ? "Dark" : "Light", t));
        Flag("general", "start_announce", s => SidebandAnnounceConfiguration.FromState(s).AnnounceOnStartup,
            (_, v, t) => client.ConfigureAnnouncementAsync(new(v), t));
        Flag("general", "lxm_limit_1mb", s => SidebandDeliveryConfiguration.FromState(s).LimitIncomingToOneMegabyte,
            (_, v, t) => client.ConfigureDeliveryAsync(new(v), t));
        Text("lxmf", "propagation_node", s => s.PropagationNode ?? "None",
            (s, v, t) => client.ConfigurePropagationAsync(NullableText(v), s.PropagationByDefault, t));
        Flag("lxmf", "propagation_by_default", s => s.PropagationByDefault,
            (s, v, t) => client.ConfigurePropagationAsync(s.PropagationNode, v, t));
        Flag("lxmf", "periodic_sync", s => s.PeriodicSynchronization,
            (s, v, t) => client.ConfigurePeriodicSynchronizationAsync(v, s.SynchronizationIntervalSeconds, t));
        Integer("lxmf", "sync_interval", s => s.SynchronizationIntervalSeconds,
            (s, v, t) => client.ConfigurePeriodicSynchronizationAsync(s.PeriodicSynchronization, v, t));
        Flag("lxmf", "try_propagation_on_fail", s => s.TryPropagationOnFail, (_, v, t) => client.ConfigurePropagationFallbackAsync(v, t));
        Flag("lxmf", "ignore_unknown", s => s.ImportedSettings?.TryGetValue("lxmf_ignore_unknown", out var value) == true && value.ValueKind == JsonValueKind.True,
            async (_, v, t) => await client.ConfigureCommandAuthorizationAsync(
                (await client.GetCommandAuthorizationAsync(t).ConfigureAwait(false)) with { IgnoreUnknown = v }, t).ConfigureAwait(false));
        Flag("lxmf", "require_stamps", s => SidebandStampConfiguration.FromState(s).RequireStamps,
            (s, v, t) => client.ConfigureStampPolicyAsync(SidebandStampConfiguration.FromState(s) with { RequireStamps = v }, t));
        Flag("lxmf", "ignore_invalid_stamps", s => SidebandStampConfiguration.FromState(s).RejectInvalidStamps,
            (s, v, t) => client.ConfigureStampPolicyAsync(SidebandStampConfiguration.FromState(s) with { RejectInvalidStamps = v }, t));
        _settings.Add(new("lxmf", "inbound_stamp_cost", "int",
            s => Format(SidebandStampConfiguration.FromState(s).InboundStampCost),
            (s, v, t) => client.ConfigureStampPolicyAsync(SidebandStampConfiguration.FromState(s) with { InboundStampCost = NullableInteger(v) }, t)));
        Integer("lxmf", "sync_max", s => SidebandSynchronizationConfiguration.FromState(s).MaximumMessages,
            (s, v, t) => client.ConfigureSynchronizationAsync(SidebandSynchronizationConfiguration.FromState(s) with { MaximumMessages = v }, t));
        _settings.Add(new("lxmf", "sync_limit", "primary int metadata / core bool-or-null",
            s => Format(SidebandSynchronizationConfiguration.FromState(s).LimitManualFetch),
            (s, v, t) => client.ConfigureSynchronizationAsync(SidebandSynchronizationConfiguration.FromState(s) with
            {
                LimitManualFetch = NullableInteger(v) is null ? null :
                    throw new NotSupportedException("Primary CLI declares sync_limit as int, but the active core requires bool-or-null. Use the supported synchronization Preferences control; no integer is stored as activated behavior.")
            }, t)));
        Flag("voice", "enabled", s => s.VoiceEnabled, (s, v, t) => client.ConfigureVoiceAsync(v, s.Voice ?? new(), t));
        Flag("voice", "trusted_only", s => s.Voice?.TrustedOnly ?? false,
            (s, v, t) => client.ConfigureVoiceAsync(s.VoiceEnabled, (s.Voice ?? new()) with { TrustedOnly = v }, t));
        Text("voice", "input", s => s.Voice?.Microphone ?? "None",
            (s, v, t) => client.ConfigureVoiceAsync(s.VoiceEnabled, (s.Voice ?? new()) with { Microphone = NullableText(v) }, t));
        Text("voice", "ringer", s => s.Voice?.Ringer ?? "None",
            (s, v, t) => client.ConfigureVoiceAsync(s.VoiceEnabled, (s.Voice ?? new()) with { Ringer = NullableText(v) }, t));
        TelemetryFlag("enabled", c => c.Enabled, (c, v) => c with { Enabled = v });
        TelemetryFlag("send_to_trusted", c => c.SendToTrusted, (c, v) => c with { SendToTrusted = v });
        TelemetryFlag("send_to_collector", c => c.SendToCollector, (c, v) => c with { SendToCollector = v });
        TelemetryFlag("request_from_collector", c => c.RequestFromCollector, (c, v) => c with { RequestFromCollector = v });
        TelemetryFlag("collector_enabled", c => c.CollectorEnabled, (c, v) => c with { CollectorEnabled = v });
        TelemetryFlag("send_all_to_collector", c => c.SendAllToCollector, (c, v) => c with { SendAllToCollector = v });
        TelemetryFlag("allow_requests_from_anyone", c => c.AllowRequestsFromAnyone, (c, v) => c with { AllowRequestsFromAnyone = v });
        TelemetryFlag("allow_requests_from_trusted", c => c.AllowRequestsFromTrusted, (c, v) => c with { AllowRequestsFromTrusted = v });
        TelemetryFlag("receive_trusted_only", c => c.ReceiveTrustedOnly, (c, v) => c with { ReceiveTrustedOnly = v });
        TelemetryFlag("display_trusted_only", c => c.DisplayTrustedOnly, (c, v) => c with { DisplayTrustedOnly = v });
        TelemetryFlag("send_appearance", c => c.SendAppearance, (c, v) => c with { SendAppearance = v });
        TelemetryFlag("use_propagation_only", c => c.UsePropagationOnly, (c, v) => c with { UsePropagationOnly = v });
        TelemetryFlag("try_propagation_on_fail", c => c.TryPropagationOnFail, (c, v) => c with { TryPropagationOnFail = v });
        TelemetryFlag("requests_only_send_latest", c => c.RequestsOnlySendLatest, (c, v) => c with { RequestsOnlySendLatest = v });
        Text("telemetry", "collector", s => s.Telemetry.Collector ?? "None",
            (s, v, t) => client.ConfigureTelemetryAsync(s.Telemetry with { Collector = NullableText(v) }, t));
        Integer("telemetry", "send_interval", s => s.Telemetry.SendIntervalSeconds,
            (s, v, t) => client.ConfigureTelemetryAsync(s.Telemetry with { SendIntervalSeconds = v }, t));
        Integer("telemetry", "request_interval", s => s.Telemetry.RequestIntervalSeconds,
            (s, v, t) => client.ConfigureTelemetryAsync(s.Telemetry with { RequestIntervalSeconds = v }, t));
        Text("telemetry", "icon", s => s.Telemetry.Appearance.Icon,
            (s, v, t) => client.ConfigureTelemetryAsync(s.Telemetry with { Appearance = s.Telemetry.Appearance with { Icon = v } }, t));
        foreach (var foreground in new[] { true, false })
        {
            var alias = foreground ? "fg" : "bg";
            _settings.Add(new("telemetry", alias, "list",
                s => ColorText(foreground ? s.Telemetry.Appearance.Foreground : s.Telemetry.Appearance.Background),
                (s, v, t) => client.ConfigureTelemetryAsync(s.Telemetry with
                {
                    Appearance = foreground ? s.Telemetry.Appearance with { Foreground = Color(v) } :
                        s.Telemetry.Appearance with { Background = Color(v) }
                }, t)));
        }
        Flag("sensors", "rns_transport", s => s.Telemetry.RnsTransportEnabled,
            (s, v, t) => client.ConfigureTelemetryAsync(s.Telemetry with { RnsTransportEnabled = v }, t));
        Flag("sensors", "information", s => s.Telemetry.InformationEnabled,
            (s, v, t) => client.ConfigureTelemetryAsync(s.Telemetry with { InformationEnabled = v }, t));
        Text("sensors", "information_text", s => s.Telemetry.Information,
            (s, v, t) => client.ConfigureTelemetryAsync(s.Telemetry with { Information = v }, t));
        Flag("sensors", "fixed_location", s => s.Telemetry.FixedLocationEnabled,
            (s, v, t) => client.ConfigureTelemetryAsync(s.Telemetry with { FixedLocationEnabled = v }, t));
        _settings.Add(new("sensors", "fixed_latlon", "list",
            s => "[" + Number(s.Telemetry.FixedLocation.Latitude) + "," + Number(s.Telemetry.FixedLocation.Longitude) + "]",
            (s, v, t) => client.ConfigureTelemetryAsync(s.Telemetry with
            {
                FixedLocation = s.Telemetry.FixedLocation with { Latitude = Numbers(v, 2)[0], Longitude = Numbers(v, 2)[1] }
            }, t)));
        _settings.Add(new("sensors", "fixed_altitude", "float", s => Number(s.Telemetry.FixedLocation.Altitude),
            (s, v, t) => client.ConfigureTelemetryAsync(s.Telemetry with
            {
                FixedLocation = s.Telemetry.FixedLocation with { Altitude = FiniteNumber(v) }
            }, t)));
    }

    public IReadOnlyList<SidebandConsoleSetting> Settings => _settings.AsReadOnly();

    public SidebandConsoleSetting Find(string category, string alias) =>
        _settings.SingleOrDefault(s => s.Category == category && s.Alias == alias) ??
        throw new NotSupportedException($"Configuration alias '{category} {alias}' is not activated by this native console. Archive-only, hardware, plugin and unsupported core settings cannot be written.");

    private void Flag(string category, string alias, Func<SidebandState, bool> read, Func<SidebandState, bool, CancellationToken, Task> write) =>
        _settings.Add(new(category, alias, "bool", s => Format(read(s)), (s, value, token) => write(s, Boolean(value), token)));

    private void Text(string category, string alias, Func<SidebandState, string> read, Func<SidebandState, string, CancellationToken, Task> write) =>
        _settings.Add(new(category, alias, "str", read, write));

    private void Integer(string category, string alias, Func<SidebandState, int> read, Func<SidebandState, int, CancellationToken, Task> write) =>
        _settings.Add(new(category, alias, "int", s => Format(read(s)),
            (s, value, token) => write(s, NullableInteger(value) ?? throw new ArgumentException("This active setting cannot be null."), token)));

    private void TelemetryFlag(string alias, Func<SidebandTelemetryConfiguration, bool> read,
        Func<SidebandTelemetryConfiguration, bool, SidebandTelemetryConfiguration> write) =>
        Flag("telemetry", alias, s => read(s.Telemetry), (s, value, token) => _client.ConfigureTelemetryAsync(write(s.Telemetry, value), token));

    public static bool Boolean(string value) => value.Trim().ToLowerInvariant() switch
    {
        "true" or "yes" or "on" or "1" or "enable" or "enabled" => true,
        "false" or "no" or "off" or "0" or "disable" or "disabled" => false,
        _ => throw new ArgumentException("Invalid boolean. Use true/false, yes/no, on/off or 1/0.")
    };

    public static int? NullableInteger(string value) => value.Trim().ToLowerInvariant() is "none" or "null" or "" ? null :
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result :
            throw new ArgumentException("Invalid 32-bit integer.");

    private static string? NullableText(string value) => value.Trim().ToLowerInvariant() is "none" or "null" ? null : value;
    private static string Format(object? value) => value switch
    {
        null => "None", bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static double FiniteNumber(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number :
            throw new ArgumentException("A finite number is required.");
    private static double[] Numbers(string value, int count)
    {
        double[] numbers;
        if (value.TrimStart().StartsWith('['))
        {
            using var document = JsonDocument.Parse(value, new() { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new ArgumentException("A numeric list is required.");
            numbers = document.RootElement.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Number ? v.GetDouble() :
                throw new ArgumentException("Numeric list entries are required.")).ToArray();
        }
        else numbers = value.Split(',').Select(FiniteNumber).ToArray();
        if (numbers.Length != count || numbers.Any(v => !double.IsFinite(v))) throw new ArgumentException($"Exactly {count} finite numbers are required.");
        return numbers;
    }
    private static string Color(string value)
    {
        var numbers = Numbers(value, 4);
        if (numbers.Any(v => v is < 0 or > 1)) throw new ArgumentException("Color channels must be between zero and one.");
        return Convert.ToHexStringLower(numbers.Take(3).Select(v => checked((byte)(v / (1.0 / 255)))).ToArray());
    }
    private static string ColorText(string value) => "[" + string.Join(',', Convert.FromHexString(value).Select(v => Number(v / 255d))) + ",1]";
}
