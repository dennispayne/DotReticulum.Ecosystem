using System.Collections.ObjectModel;
using DotReticulum.Core;

namespace Sideband;

public enum SidebandTelemetryValueKind { Nil, Boolean, Integer, UnsignedInteger, Number, Text, Binary, Array, Map }

/// <summary>Owned, immutable, bounded MessagePack data. Extensions and nonfinite numbers are not safe telemetry data.</summary>
public sealed class SidebandTelemetryValue
{
    private readonly object? _scalar;
    private readonly ReadOnlyCollection<SidebandTelemetryValue> _items;
    private readonly ReadOnlyCollection<KeyValuePair<SidebandTelemetryValue, SidebandTelemetryValue>> _members;

    private SidebandTelemetryValue(SidebandTelemetryValueKind kind, object? scalar = null,
        SidebandTelemetryValue[]? items = null,
        KeyValuePair<SidebandTelemetryValue, SidebandTelemetryValue>[]? members = null)
    {
        Kind = kind;
        _scalar = scalar;
        _items = System.Array.AsReadOnly(items ?? []);
        _members = System.Array.AsReadOnly(members ?? []);
    }

    public SidebandTelemetryValueKind Kind { get; }
    public IReadOnlyList<SidebandTelemetryValue> Items => _items;
    public IReadOnlyList<KeyValuePair<SidebandTelemetryValue, SidebandTelemetryValue>> Members => _members;
    public bool IsNil => Kind == SidebandTelemetryValueKind.Nil;
    public string? Text => _scalar as string;
    public bool? Boolean => _scalar is bool b ? b : null;
    public long? Integer => _scalar is long n ? n : null;
    public ulong? UnsignedInteger => _scalar is ulong n ? n : null;
    public double? Number => _scalar switch { long n => n, ulong n => n, double n => n, _ => null };
    public byte[]? GetBinary() => _scalar is byte[] data ? data.ToArray() : null;
    public static SidebandTelemetryValue Nil { get; } = new(SidebandTelemetryValueKind.Nil);
    public static SidebandTelemetryValue From(bool value) => new(SidebandTelemetryValueKind.Boolean, value);
    public static SidebandTelemetryValue From(long value) => new(SidebandTelemetryValueKind.Integer, value);
    public static SidebandTelemetryValue From(ulong value) => new(SidebandTelemetryValueKind.UnsignedInteger, value);
    public static SidebandTelemetryValue From(double value) => double.IsFinite(value)
        ? new(SidebandTelemetryValueKind.Number, value) : throw new ArgumentOutOfRangeException(nameof(value));
    public static SidebandTelemetryValue From(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _ = MsgPack.Pack(value, SidebandTelemetryCodec.Limits);
        return new(SidebandTelemetryValueKind.Text, value);
    }

    public static SidebandTelemetryValue From(ReadOnlySpan<byte> value)
    {
        if (value.Length > SidebandTelemetryCodec.MaxValueBytes) throw new ArgumentOutOfRangeException(nameof(value));
        return new(SidebandTelemetryValueKind.Binary, value.ToArray());
    }

    public static SidebandTelemetryValue Array(params SidebandTelemetryValue[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length > SidebandTelemetryCodec.MaxContainerEntries) throw new ArgumentOutOfRangeException(nameof(values));
        if (values.Any(v => v is null)) throw new ArgumentException("Use Nil, not a null value.", nameof(values));
        var result = new SidebandTelemetryValue(SidebandTelemetryValueKind.Array, items: values.ToArray());
        _ = result.Encode();
        return result;
    }

    public static SidebandTelemetryValue Map(params KeyValuePair<SidebandTelemetryValue, SidebandTelemetryValue>[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length > SidebandTelemetryCodec.MaxContainerEntries) throw new ArgumentOutOfRangeException(nameof(values));
        if (values.Any(v => v.Key is null || v.Value is null)) throw new ArgumentException("Use Nil, not a null value.", nameof(values));
        var result = new SidebandTelemetryValue(SidebandTelemetryValueKind.Map, members: values.ToArray());
        _ = result.Encode();
        return result;
    }

    public static SidebandTelemetryValue Fields(params (string Name, SidebandTelemetryValue Value)[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length > SidebandTelemetryCodec.MaxContainerEntries) throw new ArgumentOutOfRangeException(nameof(values));
        return Map(values.Select(v => KeyValuePair.Create(From(v.Name), v.Value)).ToArray());
    }

    public SidebandTelemetryValue? Get(string name) =>
        _members.FirstOrDefault(e => e.Key.Text == name).Value;

    public SidebandTelemetryValue? Get(long key) =>
        _members.FirstOrDefault(e => e.Key.Integer == key || key >= 0 && e.Key.UnsignedInteger == (ulong)key).Value;

    public byte[] Encode() => MsgPack.Pack(ToObject(0, new WorkBudget()), SidebandTelemetryCodec.Limits);

    public static SidebandTelemetryValue Decode(ReadOnlySpan<byte> encoded) =>
        FromObject(MsgPack.UnpackApplicationValue(encoded, SidebandTelemetryCodec.Limits));

    internal static SidebandTelemetryValue FromObject(object? value) => value switch
    {
        null => Nil,
        bool b => From(b),
        long n => From(n),
        ulong n => From(n),
        double n when double.IsFinite(n) => From(n),
        string s => new(SidebandTelemetryValueKind.Text, s),
        byte[] bytes => new(SidebandTelemetryValueKind.Binary, bytes),
        object?[] items => new(SidebandTelemetryValueKind.Array, items: items.Select(FromObject).ToArray()),
        Dictionary<object, object?> map => new(SidebandTelemetryValueKind.Map,
            members: map.Select(e => KeyValuePair.Create(FromObject(e.Key), FromObject(e.Value))).ToArray()),
        _ => throw new FormatException("Telemetry accepts only nil, finite scalars, binary, arrays and scalar-keyed maps.")
    };

    private sealed class WorkBudget
    {
        private int _values;
        public void Visit()
        {
            if (++_values > SidebandTelemetryCodec.MaxValues) throw new ArgumentException("Telemetry value-count limit exceeded.");
        }
    }

    private object? ToObject(int depth, WorkBudget budget)
    {
        budget.Visit();
        if (depth > SidebandTelemetryCodec.MaxDepth) throw new ArgumentException("Telemetry nesting limit exceeded.");
        return Kind switch
        {
            SidebandTelemetryValueKind.Nil => null,
            SidebandTelemetryValueKind.Array => _items.Select(v => v.ToObject(depth + 1, budget)).ToArray(),
            SidebandTelemetryValueKind.Map => _members.Select(e =>
                new KeyValuePair<object, object?>(e.Key.ToObject(depth + 1, budget)
                    ?? throw new ArgumentException("Nil telemetry map keys are unsupported."), e.Value.ToObject(depth + 1, budget))).ToArray(),
            _ => _scalar,
        };
    }
}
