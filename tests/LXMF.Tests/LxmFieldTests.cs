using System.Text.Json;
using DotReticulum.Core;

namespace LXMF.Tests;

[Trait("Category", "Unit")]
public class LxmFieldTests
{
    // GenerateLxmfFieldVectors.py executes LXMessage.py at e8ba02483217af43be284a72f1c059c730b7d047.
    [Theory]
    [InlineData("nil-key")]
    [InlineData("tuple-key")]
    [InlineData("infinite-keys")]
    [InlineData("nan-key")]
    [InlineData("nested-map")]
    public void ExtendedFields_PackAndUnpackActualSourceBytes(string label)
    {
        using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxmf-field-vectors.json")));
        var row = vectors.RootElement.GetProperty("cases").EnumerateArray().Single(value => value.GetProperty("label").GetString() == label);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("error").ValueKind);
        using var source = Identity.FromPrivateKey(Enumerable.Range(1, 32).Concat(Enumerable.Range(101, 32)).Select(value => (byte)value).ToArray());
        var wire = Convert.FromHexString(row.GetProperty("packed").GetString()!);
        var parsed = LxMessage.Unpack(wire, _ => source);
        Assert.True(parsed.SignatureValidated);
        Assert.Equal(Convert.FromHexString(row.GetProperty("hash").GetString()!), parsed.Hash);
        var fields = Fields(label);
        Assert.Equal(MsgPack.Pack(fields), MsgPack.Pack(parsed.FieldsValue));
        var message = LxMessage.Create(parsed.DestinationHash, source, "Fields", "F", timestamp: 1700000108.0);
        message.SetFieldMap(fields);
        Assert.Equal(wire, message.Pack());
    }

    [Fact]
    public void KnownFieldMutation_PreservesExtendedEntriesAndNumericKeyEquality()
    {
        using var source = Identity.Generate();
        var message = LxMessage.Create(new byte[16], source, "");
        message.SetFieldMap(new MsgPackMap(new(null, "opaque"), new(12.0, "old")));
        message.SetField(12L, "new");
        Assert.True(message.TryGetField(12L, out var value));
        Assert.Equal("new", value);
        var fields = Assert.IsType<MsgPackMap>(message.FieldsValue);
        Assert.Equal(2, fields.Count);
        Assert.Null(fields[0].Key);
        Assert.Equal("opaque", fields[0].Value);
        Assert.Throws<InvalidOperationException>(() => message.Fields);
        message.SetFieldMap(new MsgPackMap(new(null, "opaque"), new(9007199254740992d, "neighbor")));
        Assert.False(message.TryGetField(9007199254740993L, out _));
        message.SetField(9007199254740993L, "exact integer");
        Assert.True(message.TryGetField(9007199254740992L, out value));
        Assert.Equal("neighbor", value);
        Assert.True(message.TryGetField(9007199254740993L, out value));
        Assert.Equal("exact integer", value);
        Assert.Equal(3, Assert.IsType<MsgPackMap>(message.FieldsValue).Count);
        message.Pack();
        Assert.Throws<InvalidOperationException>(() => message.SetField(12L, "changed"));
    }

    [Fact]
    public void NonMapFields_MatchSourceNilNormalizationAndListRejection()
    {
        using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxmf-field-vectors.json")));
        var rows = vectors.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        using var source = Identity.FromPrivateKey(Enumerable.Range(1, 32).Concat(Enumerable.Range(101, 32)).Select(value => (byte)value).ToArray());
        var nil = rows.Single(value => value.GetProperty("label").GetString() == "nil-fields");
        var message = LxMessage.Unpack(Convert.FromHexString(nil.GetProperty("packed").GetString()!), _ => source);
        Assert.True(message.SignatureValidated);
        Assert.Empty(message.Fields);
        var invalid = rows.Single(value => value.GetProperty("label").GetString() == "list-fields");
        Assert.Equal("ValueError", invalid.GetProperty("error").GetString());
        Assert.Throws<FormatException>(() => LxMessage.Unpack(Convert.FromHexString(invalid.GetProperty("packed").GetString()!), _ => source));
    }

    private static MsgPackMap Fields(string label) => label switch
    {
        "nil-key" => new([new(null, "nil")]),
        "tuple-key" => new([new(new object?[] { 1L, new byte[] { 116, 119, 111 }, null }, "tuple")]),
        "infinite-keys" => new(new(double.PositiveInfinity, "positive"), new(double.NegativeInfinity, "negative")),
        "nan-key" => new([new(double.CopySign(double.NaN, 1), "nan")]),
        "nested-map" => new([new(0xfcL, new MsgPackMap(new(null, "nested"), new(new object?[] { 1L, 2L }, new byte[] { 1 })))]),
        _ => throw new ArgumentOutOfRangeException(nameof(label)),
    };
}
