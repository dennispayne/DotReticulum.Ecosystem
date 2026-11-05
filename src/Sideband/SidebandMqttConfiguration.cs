using System.Text.Json.Serialization;
using System.Text;

namespace Sideband;

public sealed record SidebandMqttConfiguration
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public bool Enabled { get; init; }
    public string? Host { get; init; }
    public int? Port { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    [JsonIgnore]
    public int EffectivePort => Port ?? 1883;

    internal SidebandMqttConfiguration Validate()
    {
        if (Host is { Length: > 253 } || Host?.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '/' or '\\') == true ||
            Port is < 1 or > 65535 || Username is { Length: > 256 } ||
            Username?.Any(char.IsControl) == true || Password is { Length: > 1024 })
            throw new InvalidDataException("MQTT requires a bounded plain hostname, port1..65535 and bounded credentials.");
        if (Enabled && string.IsNullOrEmpty(Host))
            throw new InvalidDataException("Set an MQTT broker host before enabling telemetry forwarding.");
        try
        {
            if (Host is not null) StrictUtf8.GetByteCount(Host);
            if (Username is not null) StrictUtf8.GetByteCount(Username);
            if (Password is not null) StrictUtf8.GetByteCount(Password);
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidDataException("MQTT host and credentials must contain valid Unicode text.", exception);
        }
        return this;
    }

    public override string ToString() => $"MQTT enabled={Enabled}; port={EffectivePort}; credentials=[redacted]";
}
