using System.Globalization;
using System.Numerics;
using System.Text;
using V = Sideband.SidebandTelemetryValue;
using K = Sideband.SidebandTelemetryValueKind;
using S = Sideband.SidebandTelemetrySensorId;

namespace Sideband;

public sealed class SidebandMqttMessage
{
    private readonly byte[] _payload;
    internal SidebandMqttMessage(string topic, byte[] payload) { Topic = topic; _payload = payload; }
    public string Topic { get; }
    public ReadOnlyMemory<byte> Payload => _payload;
}

public sealed record SidebandMqttProjection(IReadOnlyList<SidebandMqttMessage> Messages, IReadOnlyList<string> Errors);

/// <summary>Executed primary sensor projections, independent of broker connections and sensor/device collection.</summary>
public static partial class SidebandMqtt
{
    public const int MaximumMessages = 4096;
    public const int MaximumTopicBytes = 1024;
    public const int MaximumPayloadBytes = 65536;
    public const int MaximumBatchBytes = 1_048_576;

    public static SidebandMqttProjection Project(string sourceHash, SidebandTelemetryReport report,
        Func<double, double, double>? geoidOffset = null)
    {
        if (!SidebandContact.TryNormalizeDeliveryHash(sourceHash, out var source))
            throw new ArgumentException("MQTT telemetry context must be a delivery hash.", nameof(sourceHash));
        ArgumentNullException.ThrowIfNull(report);
        if (report.TimestampValue?.Number is null) throw new FormatException("MQTT telemetry requires a numeric source timestamp.");
        var root = new ProjectionWriter("lxmf/telemetry/" + source);
        foreach (var reading in report.Sensors)
        {
            var sensor = new ProjectionWriter(root.Root);
            try
            {
                if (reading.Status is SidebandTelemetryStatus.Unknown or SidebandTelemetryStatus.Malformed)
                    throw new FormatException("Unknown or malformed sensor data.");
                ProjectSensor(sensor, reading, geoidOffset);
                root.Merge(sensor);
            }
            catch (Exception error) when (error is FormatException or ArgumentException or InvalidDataException or
                OverflowException or IOException or NotSupportedException)
            {
                root.Error($"MQTT sensor0x{reading.Id:x2} rejected: {error.GetType().Name}.");
            }
        }
        return new(Array.AsReadOnly(root.Messages.AsEnumerable().Reverse().ToArray()),
            Array.AsReadOnly(root.Errors.ToArray()));
    }

    private static void ProjectSensor(ProjectionWriter output, SidebandTelemetryReading reading,
        Func<double, double, double>? geoidOffset)
    {
        var id = (S)reading.Id;
        var data = reading.Data;
        var topic = reading.Name;
        if (data.IsNil)
        {
            if (id == S.Information) data = V.Fields(("contents", V.From("")));
            else if (id == S.PhysicalLink) data = V.Fields(("rssi", V.Nil), ("snr", V.Nil), ("q", V.Nil));
            else if (id == S.Received)
                data = V.Fields(("by", V.Nil), ("via", V.Nil),
                    ("distance", V.Fields(("geodesic", V.Nil), ("euclidian", V.Nil))));
            else if (id == S.ConnectionMap) data = V.Fields(("maps", V.Map()));
            else return;
        }
        var (name, icon) = Metadata(id);
        if (id == S.Battery)
        {
            var percent = Number(Required(data, "charge_percent"));
            var charging = Required(data, "charging").Boolean == true;
            var step = Math.Min(100, Math.Floor(percent / 10) * 10);
            icon = step < 10 ? "battery-outline" : charging ? "battery-charging-" + step :
                percent >= 97 ? "battery" : "battery-" + step;
        }
        else if (id == S.PhysicalLink)
        {
            var quality = Required(data, "q").Number;
            icon = quality > 90 ? "network-strength-4" : quality > 75 ? "network-strength-3" :
                quality > 40 ? "network-strength-2" : quality > 20 ? "network-strength-1" : "network-strength-outline";
        }
        if (id == S.RnsTransport) topic += "/" + HashPath(Required(data, "transport_identity"));
        if (id == S.LxmfPropagation) topic += "/" + HashPath(Required(data, "destination_hash"));
        output.Add(topic + "/name", V.From(name));
        output.Add(topic + "/icon", V.From(icon));
        switch (id)
        {
            case S.Time: output.Add(topic + "/utc", Required(data, "utc")); break;
            case S.Information: output.Add(topic + "/text", Required(data, "contents")); break;
            case S.Received:
                output.Add(topic + "/by", Hash(Required(data, "by")));
                output.Add(topic + "/via", Hash(Required(data, "via")));
                output.Fields(topic + "/distance", Required(data, "distance"), "geodesic euclidian");
                break;
            case S.Battery:
                output.Add(topic + "/percent", Required(data, "charge_percent"));
                output.Add(topic + "/temperature", Required(data, "temperature"));
                output.Add(topic + "/meta", V.From(Required(data, "charging").Boolean == true ? "charging" : "discharging"));
                break;
            case S.Pressure: output.Fields(topic, data, "mbar"); break;
            case S.Temperature: output.Fields(topic, data, "c"); break;
            case S.Humidity: output.Fields(topic, data, "percent_relative"); break;
            case S.AmbientLight: output.Fields(topic, data, "lux"); break;
            case S.Proximity: output.Add(topic + "/triggered", data); break;
            case S.PhysicalLink: output.Fields(topic, data, "rssi snr q"); break;
            case S.Acceleration:
            case S.AngularVelocity:
            case S.Gravity:
            case S.MagneticField: output.Fields(topic, data, "x y z"); break;
            case S.Location: Location(output, topic, data, geoidOffset); break;
            case S.PowerConsumption:
            case S.PowerProduction:
            case S.Processor:
            case S.Ram:
            case S.Nvm:
            case S.Tank:
            case S.Fuel:
            case S.Custom: Entries(output, topic, id, data); break;
            case S.Interfaces: Interfaces(output, topic, data); break;
            case S.ConnectionMap: ConnectionMap(output, topic, data); break;
            case S.RnsTransport: Transport(output, topic, data); break;
            case S.LxmfPropagation: Propagation(output, topic, data); break;
            default: throw new NotSupportedException("No primary MQTT renderer exists for this sensor.");
        }
    }

    private static void Location(ProjectionWriter output, string topic, V data, Func<double, double, double>? geoidOffset)
    {
        if (geoidOffset is null) throw new NotSupportedException("Location MQTT requires an external EGM2008-5 geoid grid.");
        var height = Number(Required(data, "altitude")) -
            geoidOffset(Number(Required(data, "latitude")), Number(Required(data, "longitude")));
        if (!double.IsFinite(height)) throw new FormatException("Invalid geoid-corrected altitude.");
        output.Fields(topic, data, "latitude longitude");
        output.Add(topic + "/altitude", height <= 0 ? V.From(0L) : V.From(height));
        output.Fields(topic, data, "speed");
        output.Add(topic + "/heading", Required(data, "bearing"));
        output.Fields(topic, data, "accuracy");
        output.Add(topic + "/updated", Required(data, "last_update"));
        output.Add(topic + "/angle_to_horizon", V.From(SidebandGeo.HorizonAngle(Math.Max(0, height))));
        output.Add(topic + "/radio_horizon", V.From(SidebandGeo.RadioHorizon(height > 0 ? height : 1)));
    }

    private static void Entries(ProjectionWriter output, string topic, S id, V data)
    {
        if (data.Kind != K.Map) throw new FormatException("Entry sensor must contain a map.");
        foreach (var entry in data.Members)
        {
            var fallback = id switch
            {
                S.PowerConsumption => "Power consumption", S.PowerProduction => "Power Production",
                S.Processor or S.Ram or S.Nvm => "Storage", S.Tank => "Tank", S.Fuel => "Fuel", _ => "Custom"
            };
            var prefix = topic + "/" + Label(entry.Key, fallback);
            var values = entry.Value.Items;
            if (id is S.PowerConsumption or S.PowerProduction or S.Custom)
            {
                output.Add(prefix + (id == S.Custom ? "/value" : "/w"), values[0]);
                output.Add(prefix + "/icon", values[1]);
            }
            else if (id == S.Processor)
            {
                output.Add(prefix + "/current_load", values[0]);
                if (!values[1].IsNil)
                    for (var index = 0; index < 3; index++)
                        output.Add(prefix + "/avgs/" + new[] { "1m", "5m", "15m" }[index], values[1].Items[index]);
                output.Add(prefix + "/clock", values[2]);
            }
            else
            {
                var liquid = id is S.Tank or S.Fuel;
                var capacity = Number(values[0]);
                if (capacity == 0) throw new FormatException("Primary percentage calculation divides by zero capacity.");
                if (liquid) output.Add(prefix + "/unit", values[2].IsNil ? V.From("L") : values[2]);
                output.Add(prefix + "/capacity", values[0]);
                output.Add(prefix + (liquid ? "/level" : "/used"), values[1]);
                output.Add(prefix + "/free", Difference(values[0], values[1]));
                output.Add(prefix + "/percent", V.From(Number(values[1]) / capacity * 100));
                if (liquid) output.Add(prefix + "/icon", values[3]);
            }
        }
    }

    private static void Interfaces(ProjectionWriter output, string topic, V data)
    {
        foreach (var entry in Required(data, "interfaces").Members)
            foreach (var key in "name type reachable_on port frequency bandwidth modulation spreadingfactor codingrate channel lat lon height transport transport_id ifac_netname ifac_netkey network_id".Split(' '))
                if (entry.Value.Get(key) is { } value && Truthy(value))
                    output.Add(topic + "/connectable/" + Label(entry.Key, "") + "/" + key, value);
    }

    private static void ConnectionMap(ProjectionWriter output, string topic, V data)
    {
        foreach (var map in Required(data, "maps").Members)
            foreach (var point in Required(map.Value, "points").Members)
            {
                var p = point.Value;
                var prefix = topic + "/maps/" + Label(map.Key, "default") + "/points/" +
                    ScalarPath(Required(p, "type_label")) + "/" + ScalarPath(Required(p, "name")) + "/" + HashPath(point.Key);
                foreach (var (target, key) in new[] { ("lat", "latitude"), ("lon", "longitude"), ("alt", "altitude") })
                    output.Add(prefix + "/" + target, Required(p, key));
                output.Fields(prefix, Required(p, "signal"), "rssi snr q");
            }
    }

    private static (string Name, string Icon) Metadata(S id) => id switch
    {
        S.Time => ("Timestamp", "clock-time-ten-outline"), S.Information => ("Information", "information-variant"),
        S.Received => ("Received", "arrow-down-bold-hexagon-outline"), S.Battery => ("Battery", "battery-outline"),
        S.Pressure => ("Ambient Pressure", "weather-cloudy"), S.Location => ("Location", "map-marker"),
        S.PhysicalLink => ("Physical Link", "network-strength-4"), S.Temperature => ("Temperature", "thermometer"),
        S.Humidity => ("Relative Humidity", "water-percent"), S.MagneticField => ("Magnetic Field", "magnet"),
        S.AmbientLight => ("Ambient Light", "white-balance-sunny"), S.Gravity => ("Gravity", "arrow-down-thin-circle-outline"),
        S.AngularVelocity => ("Angular Velocity", "orbit"), S.Acceleration => ("Acceleration", "arrow-right-thick"),
        S.Proximity => ("Proximity", "signal-distance-variant"), S.PowerConsumption => ("Power Consumption", "power-plug-outline"),
        S.PowerProduction => ("Power Production", "lightning-bolt"), S.Processor => ("Processor", "chip"),
        S.Ram => ("Random Access Memory", "memory"), S.Nvm => ("Non-Volatile Memory", "harddisk"),
        S.Tank => ("Tank", "storage-tank"), S.Fuel => ("Fuel", "fuel"), S.Custom => ("Custom", "ruler"),
        S.RnsTransport => ("Reticulum Transport", "transit-connection-variant"),
        S.LxmfPropagation => ("LXMF Propagation", "email-fast-outline"), S.ConnectionMap => ("Connection Map", "map-check-outline"),
        S.Interfaces => ("Connectable Interfaces", "transit-connection-horizontal"),
        _ => throw new NotSupportedException("No primary MQTT metadata exists.")
    };

    private static V Required(V data, string key) => data.Get(key) ?? throw new FormatException("Missing sensor field: " + key);
    private static double Number(V value) => value.Number ?? throw new FormatException("Expected numeric sensor value.");
    private static V Hash(V value) => value.GetBinary() is { } bytes ? V.From(Convert.ToHexStringLower(bytes)) : V.Nil;
    private static string HashPath(V value) => Hash(value).Text ?? "None";
    private static string ScalarPath(V value) => value.IsNil ? "None" : Encoding.UTF8.GetString(SidebandMqttPayload.Encode(value));
    private static string Label(V value, string fallback) => value.Integer == 0 || value.UnsignedInteger == 0
        ? fallback : ScalarPath(value);
    private static bool Truthy(V value) => value.Kind switch
    {
        K.Nil => false, K.Boolean => value.Boolean == true, K.Text => value.Text!.Length != 0,
        K.Binary => value.GetBinary()!.Length != 0, K.Array => value.Items.Count != 0,
        K.Map => value.Members.Count != 0, _ => value.Number != 0
    };

    private static bool Integral(V value, out BigInteger integer)
    {
        integer = value.Integer is { } signed ? new(signed) : value.UnsignedInteger is { } unsigned ? new(unsigned) : default;
        return value.Kind is K.Integer or K.UnsignedInteger;
    }

    private static V Difference(V first, V second) => Integral(first, out var a) && Integral(second, out var b)
        ? V.From((a - b).ToString(CultureInfo.InvariantCulture)) : V.From(Number(first) - Number(second));

    private sealed class ProjectionWriter(string root)
    {
        private readonly Dictionary<string, int> _positions = new(StringComparer.Ordinal);
        private int _bytes;
        internal string Root { get; } = root;
        internal List<SidebandMqttMessage> Messages { get; } = [];
        internal List<string> Errors { get; } = [];

        internal void Error(string message)
        {
            if (Errors.Count < 64) Errors.Add(message);
            else if (Errors.Count == 64) Errors.Add("Additional MQTT projection diagnostics were suppressed after64 errors.");
        }

        internal void Fields(string prefix, V data, string keys)
        {
            foreach (var key in keys.Split(' ')) Add(prefix + "/" + key, Required(data, key));
        }

        internal void Add(string path, V value)
        {
            try { Add(new(Root + "/" + path, SidebandMqttPayload.Encode(value))); }
            catch (FormatException error) { Error("MQTT payload rejected: " + error.GetType().Name); }
        }

        private void Add(SidebandMqttMessage message)
        {
            var topicBytes = Encoding.UTF8.GetByteCount(message.Topic);
            if (topicBytes > MaximumTopicBytes || message.Topic.IndexOfAny(['\0', '+', '#']) >= 0 ||
                message.Payload.Length > MaximumPayloadBytes)
                throw new FormatException("Invalid or oversized MQTT topic/payload.");
            var size = checked(topicBytes + message.Payload.Length);
            if (_positions.TryGetValue(message.Topic, out var existing))
            {
                _bytes -= Encoding.UTF8.GetByteCount(Messages[existing].Topic) + Messages[existing].Payload.Length;
                Messages[existing] = message;
            }
            else
            {
                if (Messages.Count == MaximumMessages) throw new InvalidDataException("MQTT batch message limit reached.");
                _positions.Add(message.Topic, Messages.Count);
                Messages.Add(message);
            }
            _bytes = checked(_bytes + size);
            if (_bytes > MaximumBatchBytes) throw new InvalidDataException("MQTT batch byte limit reached.");
        }

        internal void Merge(ProjectionWriter sensor)
        {
            if (Messages.Count + sensor.Messages.Count > MaximumMessages || _bytes + sensor._bytes > MaximumBatchBytes)
                throw new InvalidDataException("MQTT batch limit reached.");
            foreach (var message in sensor.Messages) Add(message);
            foreach (var error in sensor.Errors) Error(error);
        }
    }
}
