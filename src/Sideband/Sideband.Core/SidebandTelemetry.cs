namespace Sideband;

// IDs are factual contracts observed at primary d069122 in sbapp/sideband/sense.py.
public enum SidebandTelemetrySensorId : byte
{
    Time = 0x01, Location = 0x02, Pressure = 0x03, Battery = 0x04, PhysicalLink = 0x05,
    Acceleration = 0x06, Temperature = 0x07, Humidity = 0x08, MagneticField = 0x09,
    AmbientLight = 0x0a, Gravity = 0x0b, AngularVelocity = 0x0c, Proximity = 0x0e,
    Information = 0x0f, Received = 0x10, PowerConsumption = 0x11, PowerProduction = 0x12,
    Processor = 0x13, Ram = 0x14, Nvm = 0x15, Tank = 0x16, Fuel = 0x17,
    LxmfPropagation = 0x18, RnsTransport = 0x19, ConnectionMap = 0x1a, Interfaces = 0x1b, Custom = 0xff
}

public enum SidebandTelemetryStatus { Available, Partial, Unavailable, Malformed, Unknown }
public enum SidebandTelemetryCommand : byte { Plugin = 0, TelemetryRequest = 1, Ping = 2, Echo = 3, SignalReport = 4 }

public sealed record SidebandTelemetryLocation(double Latitude, double Longitude, double Altitude,
    double Speed, double Bearing, double Accuracy, double LastUpdateUtcSeconds);
public sealed record SidebandTelemetryField(string Name, SidebandTelemetryValue Value, string? Unit = null);
public sealed record SidebandTelemetryEntry(SidebandTelemetryValue Key, string Label,
    IReadOnlyList<SidebandTelemetryField> Fields);

/// <summary>Wire data stays authoritative; named fields are a presentation projection, not a sensor driver.</summary>
public sealed class SidebandTelemetryReading
{
    internal SidebandTelemetryReading(byte id, string name, SidebandTelemetryStatus status,
        SidebandTelemetryValue wire, SidebandTelemetryValue data,
        IReadOnlyList<SidebandTelemetryField> fields, IReadOnlyList<SidebandTelemetryEntry> entries,
        SidebandTelemetryLocation? location = null, string? error = null)
    {
        Id = id;
        Name = name;
        Status = status;
        WireValue = wire;
        Data = data;
        Fields = fields;
        Entries = entries;
        Location = location;
        Error = error;
    }

    public byte Id { get; }
    public string Name { get; }
    public bool IsKnown => Enum.IsDefined((SidebandTelemetrySensorId)Id);
    public SidebandTelemetryStatus Status { get; }
    public SidebandTelemetryValue WireValue { get; }
    /// <summary>Named sensor data (the unpacked representation), including unmodified self-describing nested maps.</summary>
    public SidebandTelemetryValue Data { get; }
    public IReadOnlyList<SidebandTelemetryField> Fields { get; }
    public IReadOnlyList<SidebandTelemetryEntry> Entries { get; }
    /// <summary>Altitude is the transmitted altitude, NOT a geoid/AAMSL conversion. Speed is km/h.</summary>
    public SidebandTelemetryLocation? Location { get; }
    /// <summary>Exact transmitted location timestamp, including integer width/precision beyond double's range.</summary>
    public SidebandTelemetryValue? LocationUpdatedValue => Location is null ? null : Data.Get("last_update");
    public string? Error { get; }
}

public sealed class SidebandTelemetryReport
{
    internal SidebandTelemetryReport(IEnumerable<SidebandTelemetryReading> sensors) =>
        Sensors = Array.AsReadOnly(sensors.ToArray());
    public IReadOnlyList<SidebandTelemetryReading> Sensors { get; }
    public SidebandTelemetryReading? Get(byte id) => Sensors.FirstOrDefault(s => s.Id == id);
    public SidebandTelemetryReading? Get(SidebandTelemetrySensorId id) => Get((byte)id);
    public SidebandTelemetryValue? TimestampValue => Get(SidebandTelemetrySensorId.Time)?.Data.Get("utc");
    /// <summary>Convenience numeric projection; use TimestampValue for exact large integer timestamps.</summary>
    public double? TimestampUtcSeconds => TimestampValue?.Number;
}
