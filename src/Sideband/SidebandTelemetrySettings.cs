using System.Text.Json;
using System.Globalization;

namespace Sideband;

internal static class SidebandTelemetrySettings
{
    internal static bool IsSupported(string key) => key is
        "telemetry_enabled" or "telemetry_send_to_trusted" or "telemetry_send_to_collector" or "telemetry_request_from_collector" or
        "telemetry_collector_enabled" or "telemetry_send_all_to_collector" or "telemetry_allow_requests_from_anyone" or
        "telemetry_allow_requests_from_trusted" or "telemetry_receive_trusted_only" or "telemetry_display_trusted_only" or
        "telemetry_send_appearance" or "telemetry_use_propagation_only" or "telemetry_try_propagation_on_fail" or
        "telemetry_requests_only_send_latest" or "telemetry_collector" or "telemetry_send_interval" or "telemetry_request_interval" or
        "telemetry_s_information" or "telemetry_s_information_text" or "telemetry_s_fixed_location" or "telemetry_s_fixed_latlon" or
        "telemetry_s_fixed_altitude" or "telemetry_s_rns_transport" or "telemetry_s_battery" or "dotreticulum_runtime_telemetry_tables" or
        "telemetry_icon" or "telemetry_fg" or "telemetry_bg" or "telemetry_to_mqtt" or
        "telemetry_mqtt_host" or "telemetry_mqtt_port" or "telemetry_mqtt_user" or "telemetry_mqtt_pass";

    internal static SidebandTelemetryConfiguration Import(IReadOnlyDictionary<string, JsonElement> settings, SidebandTelemetryConfiguration prior)
    {
        bool Flag(string key, bool fallback) => settings.TryGetValue(key, out var value) ? value.GetBoolean() : fallback;
        int Interval(string key, int fallback) => settings.TryGetValue(key, out var value) ? value.GetInt32() : fallback;
        string Text(string key, string fallback) => settings.TryGetValue(key, out var value) ? value.GetString() ?? fallback : fallback;
        int? MqttPort()
        {
            if (!settings.TryGetValue("telemetry_mqtt_port", out var value)) return prior.Mqtt.Port;
            if (value.ValueKind == JsonValueKind.Null) return null;
            if (value.ValueKind == JsonValueKind.String && value.GetString() == "") return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String &&
                int.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number)) return number;
            throw new InvalidDataException("MQTT port must be null or a whole port number; decimal primary GUI strings are accepted.");
        }
        string Color(string key, string fallback)
        {
            if (!settings.TryGetValue(key, out var value)) return fallback;
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 4) throw new InvalidDataException("Appearance color must be RGBA.");
            var channels = value.EnumerateArray().Select(c => c.GetDouble()).ToArray();
            if (channels.Any(c => !double.IsFinite(c) || c is < 0 or > 1)) throw new InvalidDataException("Invalid RGBA color.");
            // Match the reference's truncating division; rounding or multiplying by 255 differs for some inputs.
            return Convert.ToHexStringLower(channels.Take(3).Select(c => checked((byte)(c / (1.0 / 255)))).ToArray());
        }
        var location = prior.FixedLocation;
        if (settings.TryGetValue("telemetry_s_fixed_latlon", out var latlon))
        {
            if (latlon.ValueKind != JsonValueKind.Array || latlon.GetArrayLength() != 2) throw new InvalidDataException("Fixed location must be latitude/longitude.");
            location = location with { Latitude = latlon[0].GetDouble(), Longitude = latlon[1].GetDouble() };
        }
        if (settings.TryGetValue("telemetry_s_fixed_altitude", out var altitude)) location = location with { Altitude = altitude.GetDouble() };
        return (prior with
        {
            Enabled = Flag("telemetry_enabled", prior.Enabled), SendToTrusted = Flag("telemetry_send_to_trusted", prior.SendToTrusted),
            Mqtt = new()
            {
                Enabled = Flag("telemetry_to_mqtt", prior.Mqtt.Enabled),
                Host = settings.TryGetValue("telemetry_mqtt_host", out var mqttHost) ? mqttHost.GetString() : prior.Mqtt.Host,
                Port = MqttPort(),
                Username = settings.TryGetValue("telemetry_mqtt_user", out var mqttUser) ? mqttUser.GetString() : prior.Mqtt.Username,
                Password = settings.TryGetValue("telemetry_mqtt_pass", out var mqttPassword) ? mqttPassword.GetString() : prior.Mqtt.Password
            },
            RnsTransportEnabled = Flag("telemetry_s_rns_transport", prior.RnsTransportEnabled),
            BatteryEnabled = Flag("telemetry_s_battery", prior.BatteryEnabled),
            RnsTransportIncludeTables = Flag("dotreticulum_runtime_telemetry_tables", prior.RnsTransportIncludeTables),
            SendToCollector = Flag("telemetry_send_to_collector", prior.SendToCollector),
            RequestFromCollector = Flag("telemetry_request_from_collector", prior.RequestFromCollector),
            CollectorEnabled = Flag("telemetry_collector_enabled", prior.CollectorEnabled),
            SendAllToCollector = Flag("telemetry_send_all_to_collector", prior.SendAllToCollector),
            AllowRequestsFromAnyone = Flag("telemetry_allow_requests_from_anyone", prior.AllowRequestsFromAnyone),
            AllowRequestsFromTrusted = Flag("telemetry_allow_requests_from_trusted", prior.AllowRequestsFromTrusted),
            ReceiveTrustedOnly = Flag("telemetry_receive_trusted_only", prior.ReceiveTrustedOnly),
            DisplayTrustedOnly = Flag("telemetry_display_trusted_only", prior.DisplayTrustedOnly),
            SendAppearance = Flag("telemetry_send_appearance", prior.SendAppearance),
            UsePropagationOnly = Flag("telemetry_use_propagation_only", prior.UsePropagationOnly),
            TryPropagationOnFail = Flag("telemetry_try_propagation_on_fail", prior.TryPropagationOnFail),
            RequestsOnlySendLatest = Flag("telemetry_requests_only_send_latest", prior.RequestsOnlySendLatest),
            Collector = settings.TryGetValue("telemetry_collector", out var collector) ? collector.GetString() : prior.Collector,
            SendIntervalSeconds = Interval("telemetry_send_interval", prior.SendIntervalSeconds),
            RequestIntervalSeconds = Interval("telemetry_request_interval", prior.RequestIntervalSeconds),
            InformationEnabled = Flag("telemetry_s_information", prior.InformationEnabled),
            Information = Text("telemetry_s_information_text", prior.Information),
            FixedLocationEnabled = Flag("telemetry_s_fixed_location", prior.FixedLocationEnabled), FixedLocation = location,
            Appearance = new(Text("telemetry_icon", prior.Appearance.Icon),
                Color("telemetry_fg", prior.Appearance.Foreground), Color("telemetry_bg", prior.Appearance.Background))
        }).Validate();
    }

    internal static void Export(Dictionary<string, JsonElement> settings, SidebandTelemetryConfiguration config)
    {
        void Flag(string key, bool value) => settings[key] = JsonSerializer.SerializeToElement(value, SidebandStateJsonContext.Default.Boolean);
        void Text(string key, string? value) => settings[key] = JsonSerializer.SerializeToElement(value, SidebandStateJsonContext.Default.String);
        void Interval(string key, int value) => settings[key] = JsonSerializer.SerializeToElement(value, SidebandStateJsonContext.Default.Int32);
        void Numbers(string key, double[] value) => settings[key] = JsonSerializer.SerializeToElement(value, SidebandStateJsonContext.Default.DoubleArray);
        Flag("telemetry_enabled", config.Enabled); Flag("telemetry_send_to_trusted", config.SendToTrusted);
        Flag("telemetry_to_mqtt", config.Mqtt.Enabled);
        Text("telemetry_mqtt_host", config.Mqtt.Host); Text("telemetry_mqtt_user", config.Mqtt.Username);
        Text("telemetry_mqtt_pass", config.Mqtt.Password);
        settings["telemetry_mqtt_port"] = JsonSerializer.SerializeToElement(config.Mqtt.Port,
            SidebandStateJsonContext.Default.NullableInt32);
        Flag("telemetry_s_rns_transport", config.RnsTransportEnabled);
        Flag("telemetry_s_battery", config.BatteryEnabled);
        Flag("dotreticulum_runtime_telemetry_tables", config.RnsTransportIncludeTables);
        Flag("telemetry_send_to_collector", config.SendToCollector); Flag("telemetry_request_from_collector", config.RequestFromCollector);
        Flag("telemetry_collector_enabled", config.CollectorEnabled); Flag("telemetry_send_all_to_collector", config.SendAllToCollector);
        Flag("telemetry_allow_requests_from_anyone", config.AllowRequestsFromAnyone);
        Flag("telemetry_allow_requests_from_trusted", config.AllowRequestsFromTrusted);
        Flag("telemetry_receive_trusted_only", config.ReceiveTrustedOnly); Flag("telemetry_display_trusted_only", config.DisplayTrustedOnly);
        Flag("telemetry_send_appearance", config.SendAppearance); Flag("telemetry_use_propagation_only", config.UsePropagationOnly);
        Flag("telemetry_try_propagation_on_fail", config.TryPropagationOnFail); Flag("telemetry_requests_only_send_latest", config.RequestsOnlySendLatest);
        Flag("telemetry_s_information", config.InformationEnabled); Text("telemetry_s_information_text", config.Information);
        Flag("telemetry_s_fixed_location", config.FixedLocationEnabled); Numbers("telemetry_s_fixed_latlon", [config.FixedLocation.Latitude, config.FixedLocation.Longitude]);
        settings["telemetry_s_fixed_altitude"] = JsonSerializer.SerializeToElement(config.FixedLocation.Altitude, SidebandStateJsonContext.Default.Double);
        Text("telemetry_collector", config.Collector); Text("telemetry_icon", config.Appearance.Icon);
        Interval("telemetry_send_interval", config.SendIntervalSeconds); Interval("telemetry_request_interval", config.RequestIntervalSeconds);
        Numbers("telemetry_fg", [.. Convert.FromHexString(config.Appearance.Foreground).Select(b => b / 255d), 1]);
        Numbers("telemetry_bg", [.. Convert.FromHexString(config.Appearance.Background).Select(b => b / 255d), 1]);
    }
}
