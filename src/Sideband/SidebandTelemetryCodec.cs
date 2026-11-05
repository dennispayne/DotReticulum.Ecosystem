using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using DotReticulum.Core;
using V = Sideband.SidebandTelemetryValue;
using K = Sideband.SidebandTelemetryValueKind;
using S = Sideband.SidebandTelemetrySensorId;

namespace Sideband;

/// <summary>Sideband 2.2.0 sense.py telemetry, unrelated to the repository's .NET-only Telemetry format.</summary>
public static class SidebandTelemetryCodec
{
    public const int MaxEncodedBytes = 262_144;
    public const int MaxValueBytes = 65_536;
    public const int MaxContainerEntries = 1_024;
    public const int MaxValues = 16_384;
    public const int MaxDepth = 16;
    public const int MaxSensors = 256;
    public const int TelemetryField = 0x02;
    public const int TelemetryStreamField = 0x03;
    public const int CommandsField = 0x09;
    internal static MsgPackLimits Limits { get; } = new()
    {
        MaxEncodedBytes = MaxEncodedBytes, MaxValueBytes = MaxValueBytes,
        MaxContainerEntries = MaxContainerEntries, MaxValues = MaxValues, MaxDepth = MaxDepth
    };

    public static SidebandTelemetryReport Decode(ReadOnlySpan<byte> packed)
    {
        var root = V.Decode(packed);
        Require(root.Kind == K.Map && root.Members.Count <= MaxSensors, "Telemetry must be a bounded sensor map.");
        var sensors = new List<SidebandTelemetryReading>();
        foreach (var pair in root.Members)
        {
            var id = pair.Key.Integer is long n && n is >= 0 and <= 255 ? (byte)n :
                pair.Key.UnsignedInteger is ulong u && u <= 255 ? (byte)u :
                throw new FormatException("Telemetry sensor IDs must be byte integers.");
            sensors.Add(FromWireValue(id, pair.Value));
        }
        return new(sensors);
    }

    public static bool TryDecode(ReadOnlySpan<byte> packed, out SidebandTelemetryReport? report)
    {
        try { report = Decode(packed); return true; }
        catch (Exception e) when (e is FormatException or ArgumentException or OverflowException)
        { report = null; return false; }
    }

    /// <summary>Does not rewrite timestamps or local received/link metadata. Invalid known wire values remain losslessly retained.</summary>
    public static byte[] Encode(SidebandTelemetryReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Sensors.Count > MaxSensors) throw new ArgumentOutOfRangeException(nameof(report));
        return V.Map(report.Sensors.Select(s => KeyValuePair.Create(V.From((long)s.Id), s.WireValue)).ToArray()).Encode();
    }

    /// <summary>Explicit synthetic/manual report. No devices, threads, implicit clock or remote services are opened.</summary>
    public static SidebandTelemetryReport Create(double utcSeconds, params SidebandTelemetryReading[] sensors) =>
        CreateTimed(V.From(utcSeconds), sensors);

    public static SidebandTelemetryReport Create(long utcSeconds, params SidebandTelemetryReading[] sensors) =>
        CreateTimed(V.From(utcSeconds), sensors);

    private static SidebandTelemetryReport CreateTimed(V utcSeconds, SidebandTelemetryReading[] sensors)
    {
        ArgumentNullException.ThrowIfNull(sensors);
        if (sensors.Length >= MaxSensors) throw new ArgumentOutOfRangeException(nameof(sensors));
        if (sensors.Any(s => s is null || s.Id == (byte)S.Time)) throw new ArgumentException("Supply time only through utcSeconds.", nameof(sensors));
        return FromSensors(new[] { FromData(S.Time, V.Fields(("utc", utcSeconds))) }.Concat(sensors).ToArray());
    }

    public static SidebandTelemetryReport FromSensors(params SidebandTelemetryReading[] sensors)
    {
        ArgumentNullException.ThrowIfNull(sensors);
        if (sensors.Length > MaxSensors || sensors.Any(s => s is null))
            throw new ArgumentException("Supply at most 256 non-null sensor readings.", nameof(sensors));
        var report = new SidebandTelemetryReport(sensors);
        _ = Encode(report);
        return report;
    }

    public static bool IsValidTimebase(double utcSeconds, double nowUtcSeconds) =>
        double.IsFinite(utcSeconds) && double.IsFinite(nowUtcSeconds) && utcSeconds <= nowUtcSeconds + 240;

    /// <summary>Construct from named sensor data, in the representation exposed by Reading.Data.</summary>
    public static SidebandTelemetryReading FromData(S id, V data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var wire = PackData(id, data);
        var result = FromWireValue((byte)id, wire);
        if (result.Status == SidebandTelemetryStatus.Malformed) throw new ArgumentException(result.Error, nameof(data));
        return result;
    }

    public static SidebandTelemetryReading FromLocation(SidebandTelemetryLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return FromData(S.Location, V.Fields(
            ("latitude", V.From(location.Latitude)), ("longitude", V.From(location.Longitude)),
            ("altitude", V.From(location.Altitude)), ("speed", V.From(location.Speed)),
            ("bearing", V.From(location.Bearing)), ("accuracy", V.From(location.Accuracy)),
            ("last_update", V.From(location.LastUpdateUtcSeconds))));
    }

    public static SidebandTelemetryReading FromWireValue(byte id, V wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        _ = wire.Encode();
        var name = SensorName(id);
        if (!Enum.IsDefined((S)id))
            return new(id, name, SidebandTelemetryStatus.Unknown, wire, wire, [], []);
        if (wire.IsNil)
            return new(id, name, SidebandTelemetryStatus.Unavailable, wire, id == (byte)S.Interfaces ? V.Map() : V.Nil, [], []);
        try
        {
            var data = UnpackData((S)id, wire);
            var fields = PresentationFields((S)id, data);
            var entries = Entries((S)id, data);
            SidebandTelemetryLocation? location = id == (byte)S.Location ? new(
                Num(data, "latitude"), Num(data, "longitude"), Num(data, "altitude"), Num(data, "speed"),
                Num(data, "bearing"), Num(data, "accuracy"), Num(data, "last_update")) : null;
            var missing = MissingFields((S)id, data);
            return new(id, name, missing.Length == 0 ? SidebandTelemetryStatus.Available : SidebandTelemetryStatus.Partial,
                wire, data, fields, entries, location, missing.Length == 0 ? null : "Missing fields: " + string.Join(", ", missing));
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
        {
            return new(id, name, SidebandTelemetryStatus.Malformed, wire, V.Nil, [], [], error: e.Message);
        }
    }

    private static V UnpackData(S id, V wire)
    {
        switch (id)
        {
            case S.Time: Number(wire); return V.Fields(("utc", wire));
            case S.Information: Type(wire, K.Text); return V.Fields(("contents", wire));
            case S.Pressure: Number(wire); return V.Fields(("mbar", wire));
            case S.Temperature: Number(wire); return V.Fields(("c", wire));
            case S.Humidity: Number(wire); return V.Fields(("percent_relative", wire));
            case S.AmbientLight: Number(wire); return V.Fields(("lux", wire));
            case S.Proximity: Type(wire, K.Boolean); return wire;
            case S.Acceleration:
            case S.MagneticField:
            case S.Gravity:
            case S.AngularVelocity:
                Sequence(wire, 3); foreach (var value in wire.Items) Number(value, true);
                return Named(wire, "x", "y", "z");
            case S.PhysicalLink:
                Sequence(wire, 3); foreach (var value in wire.Items) Number(value, true);
                return Named(wire, "rssi", "snr", "q");
            case S.Battery:
                Require(wire.Kind == K.Array && wire.Items.Count is 2 or 3, "Battery requires two or three elements.");
                Number(wire.Items[0]); Type(wire.Items[1], K.Boolean);
                if (wire.Items.Count == 3) Number(wire.Items[2], true);
                return V.Fields(("charge_percent", Rounded(wire.Items[0], 1)), ("charging", wire.Items[1]),
                    ("temperature", wire.Items.Count == 3 ? wire.Items[2] : V.Nil));
            case S.Received:
                Sequence(wire, 4); Hash(wire.Items[0], true); Hash(wire.Items[1], true);
                Number(wire.Items[2], true); Number(wire.Items[3], true);
                return V.Fields(("by", wire.Items[0]), ("via", wire.Items[1]),
                    ("distance", V.Fields(("geodesic", wire.Items[2]), ("euclidian", wire.Items[3]))));
            case S.Location:
                Sequence(wire, 7);
                var location = new double[6];
                for (var i = 0; i < 6; i++)
                {
                    var bytes = wire.Items[i].GetBinary();
                    Require(bytes is not null && bytes.Length == (i == 5 ? 2 : 4), "Location fixed-width field length is invalid.");
                    location[i] = i switch
                    {
                        3 => BinaryPrimitives.ReadUInt32BigEndian(bytes),
                        5 => BinaryPrimitives.ReadUInt16BigEndian(bytes),
                        _ => BinaryPrimitives.ReadInt32BigEndian(bytes),
                    };
                    location[i] /= i < 2 ? 1e6 : 1e2;
                }
                Number(wire.Items[6]);
                return V.Fields(("latitude", V.From(location[0])), ("longitude", V.From(location[1])),
                    ("altitude", V.From(location[2])), ("speed", V.From(location[3])),
                    ("bearing", V.From(location[4])), ("accuracy", V.From(location[5])), ("last_update", wire.Items[6]));
            case S.PowerConsumption:
            case S.PowerProduction:
            case S.Processor:
            case S.Ram:
            case S.Nvm:
            case S.Tank:
            case S.Fuel:
            case S.Custom:
                Type(wire, K.Array);
                var values = new List<KeyValuePair<V, V>>();
                foreach (var entry in wire.Items)
                {
                    Sequence(entry, 2);
                    var label = entry.Items[0];
                    Require(label.Kind == K.Text || label.Integer == 0 || label.UnsignedInteger == 0, "Entry label must be text or integer zero.");
                    ValidateEntry(id, entry.Items[1]);
                    values.Add(KeyValuePair.Create(label, entry.Items[1]));
                }
                // Map packing also rejects duplicate labels, instead of silently overwriting observations.
                return V.Map(values.ToArray());
            case S.RnsTransport:
            case S.LxmfPropagation:
            case S.ConnectionMap:
                Type(wire, K.Map); ValidateStructured(id, wire); return wire;
            case S.Interfaces:
                Type(wire, K.Map);
                if (wire.Members.Count == 0) return V.Map();
                return V.Fields(("interfaces", UnpackInterfaces(wire)));
            default: throw new FormatException("Unknown sensor.");
        }
    }

    private static V PackData(S id, V data)
    {
        if (data.IsNil) return V.Nil;
        switch (id)
        {
            case S.Time: return Required(data, "utc");
            case S.Information: return Required(data, "contents");
            case S.Pressure: return Required(data, "mbar");
            case S.Temperature: return Required(data, "c");
            case S.Humidity: return Required(data, "percent_relative");
            case S.AmbientLight: return Required(data, "lux");
            case S.Proximity: return data;
            case S.Acceleration:
            case S.MagneticField:
            case S.Gravity:
            case S.AngularVelocity: return Values(data, "x", "y", "z");
            case S.PhysicalLink: return Values(data, "rssi", "snr", "q");
            case S.Battery: return V.Array(Rounded(Required(data, "charge_percent"), 1), Required(data, "charging"), data.Get("temperature") ?? V.Nil);
            case S.Received:
                return V.Array(Required(data, "by"), Required(data, "via"),
                    Required(Required(data, "distance"), "geodesic"), Required(Required(data, "distance"), "euclidian"));
            case S.Location:
                var latitude = Fixed(Num(data, "latitude"), 6, 1e6);
                var longitude = Fixed(Num(data, "longitude"), 6, 1e6);
                var altitude = Fixed(Num(data, "altitude"), 2, 1e2);
                var speed = Fixed(Num(data, "speed"), 2, 1e2);
                var bearing = Fixed(Num(data, "bearing"), 2, 1e2);
                var accuracy = Fixed(Num(data, "accuracy"), 2, 1e2);
                Number(Required(data, "last_update"));
                return V.Array(Signed(latitude), Signed(longitude), Signed(altitude),
                    Unsigned(speed), Signed(bearing), ShortUnsigned(accuracy), Required(data, "last_update"));
            case S.PowerConsumption:
            case S.PowerProduction:
            case S.Processor:
            case S.Ram:
            case S.Nvm:
            case S.Tank:
            case S.Fuel:
            case S.Custom:
                Type(data, K.Map);
                return V.Array(data.Members.Select(e => V.Array(e.Key, e.Value)).ToArray());
            case S.Interfaces: return PackInterfaces(data);
            case S.RnsTransport:
            case S.LxmfPropagation:
            case S.ConnectionMap: return data;
            default: throw new ArgumentOutOfRangeException(nameof(id));
        }
    }

    private static readonly (long Id, string Name)[] InterfaceKeys =
    [
        (0, "type"), (1, "transport"), (254, "transport_id"), (253, "network_id"),
        (2, "reachable_on"), (6, "port"), (9, "frequency"), (10, "bandwidth"),
        (13, "modulation"), (11, "spreadingfactor"), (12, "codingrate"), (14, "channel"),
        (3, "lat"), (4, "lon"), (5, "height"), (7, "ifac_netname"), (8, "ifac_netkey")
    ];

    private static V UnpackInterfaces(V wire)
    {
        var interfaces = new List<KeyValuePair<V, V>>();
        foreach (var pair in wire.Members)
        {
            Type(pair.Key, K.Text); Type(pair.Value, K.Map);
            var fields = new List<(string, V)> { ("name", pair.Key) };
            foreach (var (key, name) in InterfaceKeys)
            {
                var value = pair.Value.Get(key) ?? V.Nil;
                if (key is 254 or 253 && !value.IsNil)
                {
                    Hash(value);
                    value = V.From(Convert.ToHexStringLower(value.GetBinary()!));
                }
                else ValidateInterfaceField(name, value);
                fields.Add((name, value));
            }
            interfaces.Add(KeyValuePair.Create(pair.Key, V.Fields(fields.ToArray())));
        }
        return V.Map(interfaces.ToArray());
    }

    private static V PackInterfaces(V data)
    {
        if (data.Kind == K.Map && data.Members.Count == 0) return V.Map();
        var interfaces = Required(data, "interfaces"); Type(interfaces, K.Map);
        return V.Map(interfaces.Members.Select(pair =>
        {
            Type(pair.Key, K.Text); Type(pair.Value, K.Map);
            var fields = new List<KeyValuePair<V, V>>();
            foreach (var (key, name) in InterfaceKeys)
            {
                var value = pair.Value.Get(name) ?? V.Nil;
                if (!Truthy(value)) continue;
                if (key is 254 or 253)
                {
                    Type(value, K.Text);
                    value = V.From(Convert.FromHexString(value.Text!));
                    Hash(value);
                }
                fields.Add(KeyValuePair.Create(V.From(key), value));
            }
            return KeyValuePair.Create(pair.Key, V.Map(fields.ToArray()));
        }).ToArray());
    }

    private static void ValidateInterfaceField(string name, V value)
    {
        if (value.IsNil) return;
        if (name == "transport") Type(value, K.Boolean);
        else if (name is "type" or "reachable_on" or "modulation" or "ifac_netname" or "ifac_netkey") Type(value, K.Text);
        else Number(value);
    }

    private static void ValidateEntry(S id, V values)
    {
        var count = id == S.Processor ? 3 : id is S.Tank or S.Fuel ? 4 : 2;
        Sequence(values, count);
        if (id == S.Custom) { TextOrNil(values.Items[1]); return; }
        Number(values.Items[0], true);
        if (id is S.PowerConsumption or S.PowerProduction) { TextOrNil(values.Items[1]); return; }
        if (id == S.Processor)
        {
            if (!values.Items[1].IsNil)
            {
                Sequence(values.Items[1], 3);
                foreach (var average in values.Items[1].Items) Number(average, true);
            }
            Number(values.Items[2], true); return;
        }
        Number(values.Items[1], true);
        if (id is S.Tank or S.Fuel) { TextOrNil(values.Items[2]); TextOrNil(values.Items[3]); }
    }

    private static void ValidateStructured(S id, V data)
    {
        foreach (var pair in data.Members) Type(pair.Key, K.Text);
        if (id == S.RnsTransport)
        {
            foreach (var name in new[] { "transport_uptime", "traffic_rxb", "traffic_txb", "speed_rx", "speed_tx",
                "speed_rx_inst", "speed_tx_inst", "memory_used", "interface_count", "link_count", "path_count" })
                if (data.Get(name) is { } value) Number(value, true);
            if (data.Get("transport_enabled") is { IsNil: false } enabled) Type(enabled, K.Boolean);
            if (data.Get("transport_identity") is { } identity) Hash(identity, true);
            if (data.Get("ifstats") is { IsNil: false } stats) Type(stats, K.Map);
            if (data.Get("ifstats")?.Get("interfaces") is { IsNil: false } interfaces)
            {
                Type(interfaces, K.Array);
                foreach (var item in interfaces.Items)
                {
                    Type(item, K.Map);
                    if (item.Get("name") is { IsNil: false } name) Type(name, K.Text);
                }
            }
            if (data.Get("path_table") is { IsNil: false } paths)
            {
                Type(paths, K.Array);
                foreach (var path in paths.Items)
                {
                    Type(path, K.Map);
                    foreach (var field in new[] { "hash", "via" })
                        if (path.Get(field) is { } hash) Hash(hash, true);
                    if (path.Get("interface") is { IsNil: false } name) Type(name, K.Text);
                    if (path.Get("hops") is { } hops) Number(hops, true);
                }
            }
        }
        else if (id == S.LxmfPropagation)
        {
            foreach (var name in new[] { "destination_hash", "identity_hash" })
                if (data.Get(name) is { } hash) Hash(hash, true);
            foreach (var name in new[] { "uptime", "delivery_limit", "propagation_limit", "autopeer_maxdepth",
                "unpeered_propagation_incoming", "unpeered_propagation_rx_bytes", "static_peers", "total_peers", "max_peers" })
                if (data.Get(name) is { } value) Number(value, true);
            if (data.Get("from_static_only") is { IsNil: false } flag) Type(flag, K.Boolean);
            foreach (var name in new[] { "messagestore", "clients", "peers" })
                if (data.Get(name) is { IsNil: false } map) Type(map, K.Map);
            foreach (var (group, fields) in new (string, string[])[]
            {
                ("messagestore", ["count", "bytes", "limit"]),
                ("clients", ["client_propagation_messages_received", "client_propagation_messages_served"])
            })
                if (data.Get(group) is { } map)
                    foreach (var field in fields)
                        if (map.Get(field) is { } value) Number(value, true);
            if (data.Get("peers") is { } peers)
                foreach (var peer in peers.Members)
                {
                    Hash(peer.Key); Type(peer.Value, K.Map);
                    if (peer.Value.Get("alive") is { IsNil: false } alive) Type(alive, K.Boolean);
                    foreach (var name in new[] { "state", "last_heard", "next_sync_attempt", "last_sync_attempt",
                        "sync_backoff", "peering_timebase", "ler", "str", "transfer_limit", "network_distance", "rx_bytes", "tx_bytes" })
                        if (peer.Value.Get(name) is { } value) Number(value, true);
                    if (peer.Value.Get("messages") is { IsNil: false } messages)
                    {
                        Type(messages, K.Map);
                        foreach (var name in new[] { "offered", "outgoing", "incoming", "unhandled" })
                            if (messages.Get(name) is { } value) Number(value, true);
                    }
                }
        }
        else if (data.Get("maps") is { IsNil: false } maps)
        {
            Type(maps, K.Map);
            foreach (var map in maps.Members)
            {
                Type(map.Value, K.Map);
                if (map.Value.Get("points") is not { IsNil: false } points) continue;
                Type(points, K.Map);
                foreach (var point in points.Members)
                {
                    Hash(point.Key); Type(point.Value, K.Map);
                    foreach (var field in new[] { "latitude", "longitude", "altitude", "added" })
                        if (point.Value.Get(field) is { } number) Number(number, true);
                    if (point.Value.Get("signal") is { IsNil: false } signal)
                    {
                        Type(signal, K.Map);
                        foreach (var name in new[] { "rssi", "snr", "q" })
                            if (signal.Get(name) is { } value) Number(value, true);
                    }
                }
            }
        }
    }

    private static string[] MissingFields(S id, V data)
    {
        string[] required = id switch
        {
            S.RnsTransport => ["transport_enabled", "transport_identity", "transport_uptime", "traffic_rxb", "traffic_txb",
                "speed_rx", "speed_tx", "speed_rx_inst", "speed_tx_inst", "memory_used", "ifstats", "path_table",
                "interface_count", "link_count"],
            S.LxmfPropagation => ["destination_hash", "identity_hash", "uptime", "delivery_limit", "propagation_limit",
                "autopeer_maxdepth", "from_static_only", "messagestore", "clients", "unpeered_propagation_incoming",
                "unpeered_propagation_rx_bytes", "static_peers", "total_peers", "max_peers", "peers"],
            S.ConnectionMap => ["maps"],
            _ => []
        };
        return required.Where(name => data.Get(name) is null).ToArray();
    }

    private static IReadOnlyList<SidebandTelemetryField> Fields(V data) =>
        Array.AsReadOnly(data.Members.Where(e => e.Key.Text is not null)
            .Select(e => new SidebandTelemetryField(e.Key.Text!, e.Value, Unit(e.Key.Text!))).ToArray());

    private static IReadOnlyList<SidebandTelemetryField> PresentationFields(S id, V data)
    {
        var fields = Fields(data).ToList();
        if (id == S.Proximity) fields.Add(new("triggered", data));
        if (id == S.Received && data.Get("distance") is { } distance) fields.AddRange(Fields(distance));
        if (id == S.LxmfPropagation)
        {
            foreach (var name in new[] { "delivery_limit", "propagation_limit" })
                if (data.Get(name)?.Number is { } limit) fields.Add(new(name + "_bytes", Finite(limit * 1000), "bytes"));
            if (data.Get("messagestore") is { } store)
            {
                foreach (var entry in Fields(store)) fields.Add(entry with { Name = "messagestore_" + entry.Name });
                if (store.Get("limit")?.Number is { } limit && store.Get("bytes")?.Number is { } used)
                {
                    fields.Add(new("messagestore_free", Finite(limit - used), "bytes"));
                    fields.Add(new("messagestore_percent", limit == 0 ? V.Nil : Finite(Math.Min(used / limit * 100, 100)), "%"));
                }
            }
            if (data.Get("clients") is { } clients) fields.AddRange(Fields(clients));
            if (data.Get("peers") is { } peers)
            {
                var active = peers.Members.Count(e => e.Value.Get("alive")?.Boolean == true);
                fields.Add(new("active_peers", V.From((long)active)));
                if (data.Get("total_peers")?.Number is { } total) fields.Add(new("unreachable_peers", Finite(total - active)));
            }
        }
        return Array.AsReadOnly(fields.ToArray());
    }

    private static IReadOnlyList<SidebandTelemetryEntry> Entries(S id, V data)
    {
        if (id is S.PowerConsumption or S.PowerProduction or S.Processor or S.Ram or S.Nvm or S.Tank or S.Fuel or S.Custom)
        {
            string[] names = id switch
            {
                S.PowerConsumption or S.PowerProduction => ["w", "custom_icon"],
                S.Processor => ["current_load", "load_avgs", "clock"],
                S.Ram or S.Nvm => ["capacity", "used"],
                S.Tank or S.Fuel => ["capacity", "level", "unit", "custom_icon"],
                _ => ["value", "custom_icon"]
            };
            return Array.AsReadOnly(data.Members.Select(pair =>
            {
                var fields = names.Select((n, i) => new SidebandTelemetryField(n, pair.Value.Items[i], Unit(n))).ToList();
                if (id == S.Processor && pair.Value.Items[1].Kind == K.Array)
                    for (var i = 0; i < 3; i++)
                        fields.Add(new("load_average_" + new[] { "1m", "5m", "15m" }[i], pair.Value.Items[1].Items[i]));
                if (id is S.Tank or S.Fuel)
                    fields[2] = new("unit", pair.Value.Items[2].IsNil ? V.From("L") : pair.Value.Items[2]);
                if (id is S.Ram or S.Nvm or S.Tank or S.Fuel)
                {
                    var capacity = pair.Value.Items[0].Number; var used = pair.Value.Items[1].Number;
                    var unit = id is S.Ram or S.Nvm ? "bytes" : fields[2].Value.Text;
                    fields[0] = fields[0] with { Unit = unit };
                    fields[1] = fields[1] with { Unit = unit };
                    fields.Add(new("free", capacity is not null && used is not null ? Finite(capacity.Value - used.Value) : V.Nil, unit));
                    fields.Add(new("percent", capacity is not null && capacity != 0 && used is not null
                        ? Finite(used.Value / capacity.Value * 100) : V.Nil, "%"));
                }
                return new SidebandTelemetryEntry(pair.Key, Label(pair.Key, SensorName((byte)id)), Array.AsReadOnly(fields.ToArray()));
            }).ToArray());
        }
        if (id == S.Proximity)
            return Array.AsReadOnly(new[] { new SidebandTelemetryEntry(V.From(0L), "Proximity",
                Array.AsReadOnly(new[] { new SidebandTelemetryField("triggered", data) })) });
        if (id == S.Interfaces && data.Get("interfaces") is { } interfaces)
            return MapEntries(interfaces, "Interface");
        if (id == S.LxmfPropagation && data.Get("peers") is { } peers)
            return MapEntries(peers, "Peer");
        if (id == S.ConnectionMap && data.Get("maps") is { } maps)
        {
            var entries = new List<SidebandTelemetryEntry>();
            foreach (var map in maps.Members)
                if (map.Value.Get("points") is { } points)
                    foreach (var point in points.Members)
                    {
                        var fields = Fields(point.Value).ToList();
                        if (point.Value.Get("signal") is { } signal) fields.AddRange(Fields(signal));
                        entries.Add(new(point.Key, Label(map.Key, "Default") + ": " +
                            (point.Value.Get("name")?.Text ?? "Point"), Array.AsReadOnly(fields.ToArray())));
                    }
            return Array.AsReadOnly(entries.ToArray());
        }
        if (id == S.RnsTransport && data.Get("path_table") is { Kind: K.Array } paths)
            return Array.AsReadOnly(paths.Items.Select((path, i) =>
                new SidebandTelemetryEntry(V.From((long)i), path.Get("interface")?.Text ?? "Path", Fields(path))).ToArray());
        return Array.Empty<SidebandTelemetryEntry>();
    }

    private static IReadOnlyList<SidebandTelemetryEntry> MapEntries(V map, string fallback) =>
        Array.AsReadOnly(map.Members.Select(e => new SidebandTelemetryEntry(e.Key, Label(e.Key, fallback), Fields(e.Value))).ToArray());
    private static V Finite(double value) => double.IsFinite(value) ? V.From(value) : V.Nil;
    private static string Label(V key, string fallback) =>
        key.Text ?? (key.GetBinary() is { } binary ? Convert.ToHexStringLower(binary) : fallback);
    private static string? Unit(string name) => name switch
    {
        "utc" or "last_update" or "added" => "Unix seconds",
        "latitude" or "longitude" or "bearing" => "degrees",
        "altitude" or "accuracy" or "geodesic" or "euclidian" => "m",
        "speed" => "km/h", "mbar" => "mbar", "c" or "temperature" => "°C",
        "percent_relative" or "charge_percent" or "q" => "%", "rssi" => "dBm", "snr" => "dB",
        "lux" => "lx", "w" => "W", "clock" => "Hz", _ => null
    };

    public static string SensorName(byte id) => ((S)id) switch
    {
        S.Time => "time", S.Location => "location", S.Pressure => "pressure", S.Battery => "battery",
        S.PhysicalLink => "physical_link", S.Acceleration => "acceleration", S.Temperature => "temperature",
        S.Humidity => "humidity", S.MagneticField => "magnetic_field", S.AmbientLight => "ambient_light",
        S.Gravity => "gravity", S.AngularVelocity => "angular_velocity", S.Proximity => "proximity",
        S.Information => "information", S.Received => "received", S.PowerConsumption => "power_consumption",
        S.PowerProduction => "power_production", S.Processor => "processor", S.Ram => "ram", S.Nvm => "nvm",
        S.Tank => "tank", S.Fuel => "fuel", S.LxmfPropagation => "lxmf_propagation", S.RnsTransport => "rns_transport",
        S.ConnectionMap => "connection_map", S.Interfaces => "interfaces", S.Custom => "custom",
        _ => "unknown_" + id.ToString(CultureInfo.InvariantCulture)
    };

    private static V Named(V values, params string[] names) =>
        V.Fields(names.Select((name, i) => (name, values.Items[i])).ToArray());
    private static V Values(V data, params string[] names) => V.Array(names.Select(n => Required(data, n)).ToArray());
    private static V Required(V data, string key) => data.Get(key) ?? throw new FormatException($"Missing sensor field {key}.");
    private static double Num(V data, string key) => Required(data, key).Number ?? throw new FormatException($"Invalid numeric field {key}.");
    private static void Require(bool condition, string message) { if (!condition) throw new FormatException(message); }
    private static void Type(V value, K kind) => Require(value.Kind == kind, $"Expected telemetry {kind}.");
    private static void Number(V value, bool nullable = false) => Require(nullable && value.IsNil || value.Number is not null, "Expected finite telemetry number.");
    private static void TextOrNil(V value) => Require(value.IsNil || value.Kind == K.Text, "Expected text or nil.");
    private static void Sequence(V value, int count) => Require(value.Kind == K.Array && value.Items.Count == count, $"Expected {count} sensor elements.");
    private static void Hash(V value, bool nullable = false) => Require(nullable && value.IsNil || value.GetBinary() is { Length: 16 }, "Expected 16-byte hash or permitted nil.");
    private static bool Truthy(V value) => !value.IsNil && value.Boolean != false &&
        value.Number != 0 && value.Text != "" && (value.Kind != K.Binary || value.GetBinary()!.Length != 0);
    private static double Fixed(double value, int digits, double scale)
    {
        Require(double.IsFinite(value), "Location requires finite values.");
        return Math.Truncate(RoundBinaryDecimal(value, digits) * scale);
    }
    // Decimal rounding of the exact IEEE-754 value, avoiding a pre-rounded value*10^digits tie.
    internal static double RoundBinaryDecimal(double value, int digits)
    {
        Require(double.IsFinite(value), "Expected finite value.");
        if (Math.Abs(value) >= 1e16) return value;
        var bits = BitConverter.DoubleToUInt64Bits(value);
        var exponent = (int)((bits >> 52) & 0x7ff);
        var mantissa = new BigInteger(bits & 0x000f_ffff_ffff_ffffUL);
        if (exponent != 0) mantissa += BigInteger.One << 52;
        var shift = exponent == 0 ? -1074 : exponent - 1023 - 52;
        var scale = BigInteger.Pow(10, digits);
        var numerator = mantissa * scale;
        var denominator = BigInteger.One;
        if (shift >= 0) numerator <<= shift;
        else denominator <<= -shift;
        var integer = BigInteger.DivRem(numerator, denominator, out var remainder);
        var half = (remainder << 1).CompareTo(denominator);
        if (half > 0 || half == 0 && !integer.IsEven) integer++;
        var result = (double)integer / (double)scale;
        return (bits >> 63) == 0 ? result : -result;
    }
    private static V Rounded(V value, int digits)
    {
        Number(value);
        return value.Kind is K.Integer or K.UnsignedInteger ? value : V.From(RoundBinaryDecimal(value.Number!.Value, digits));
    }
    private static V Signed(double value)
    {
        var bytes = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, checked((int)value)); return V.From(bytes);
    }
    private static V Unsigned(double value)
    {
        var bytes = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, checked((uint)value)); return V.From(bytes);
    }
    private static V ShortUnsigned(double value)
    {
        var bytes = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, checked((ushort)value)); return V.From(bytes);
    }
}
