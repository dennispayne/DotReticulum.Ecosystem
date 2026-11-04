using System.Text;
using System.Text.Json;
using V = Sideband.SidebandTelemetryValue;

namespace Sideband.Tests;

/// <summary>Actual imported primary d069122 methods; generator records exact core/sense SHA-256 and disclosed seams.</summary>
[Trait("Category", "Unit")]
public sealed class SidebandRemoteCommandReferenceTests
{
    [Fact]
    public void ActualPrimaryOutboundCommandsMatchAllTwelveObservations()
    {
        using var fixture = Fixture();
        foreach (var item in fixture.RootElement.GetProperty("sends").EnumerateArray())
        {
            var actual = SidebandRemoteCommands.CreateCommandField(item.GetProperty("input").GetString()!);
            Assert.Equal(item.GetProperty("accepted").GetBoolean(), actual is not null);
            if (actual is not null) Assert.Equal(Value(item.GetProperty("commands")).Encode(), actual.Encode());
        }
    }

    [Fact]
    public void ActualPrimaryRepliesMatchObservedFormattingIncludingMalformedBatchAbort()
    {
        using var fixture = Fixture();
        foreach (var item in fixture.RootElement.GetProperty("responses").EnumerateArray())
        {
            var replies = new List<string>();
            foreach (var command in Value(item.GetProperty("commands")).Items)
            {
                try
                {
                    if (SidebandRemoteCommands.CreateBuiltinReply(command,
                        Number(item, "quality"), Number(item, "rssi"), Number(item, "snr")) is { } reply) replies.Add(reply);
                }
                catch (DecoderFallbackException) { break; }
            }
            Assert.Equal(item.GetProperty("replies").EnumerateArray().Select(reply => reply.GetProperty("content").GetString()), replies);
            foreach (var reply in item.GetProperty("replies").EnumerateArray())
            {
                Assert.False(reply.GetProperty("propagation").GetBoolean());
                Assert.True(reply.GetProperty("skip_fields").GetBoolean());
                Assert.True(reply.GetProperty("no_display").GetBoolean());
            }
        }
    }

    [Fact]
    public void ActualPrimaryAll96AdmissionObservationsReuseExistingTelemetryPolicy()
    {
        using var fixture = Fixture();
        foreach (var item in fixture.RootElement.GetProperty("admissions").EnumerateArray())
        {
            var mode = item.GetProperty("mode").GetString();
            var disposition = SidebandTelemetryPolicy.DeliveryDisposition(new()
            {
                AllowRequestsFromAnyone = mode == "anyone", AllowRequestsFromTrusted = mode == "trusted"
            }, new(item.GetProperty("exists").GetBoolean(), item.GetProperty("trusted").GetBoolean() ? 1 : 0,
                AllowRequests: item.GetProperty("allow").GetBoolean()),
                item.GetProperty("signature").GetBoolean(), true, item.GetProperty("ignore_unknown").GetBoolean());
            var expected = item.GetProperty("events").EnumerateArray().Select(e => e.GetString()).SingleOrDefault();
            Assert.Equal(expected switch
            {
                "handled" => SidebandTelemetryDeliveryDisposition.HandleCommands,
                "ingested" => SidebandTelemetryDeliveryDisposition.IngestMessage,
                _ => SidebandTelemetryDeliveryDisposition.Drop
            }, disposition);
        }
    }

    [Fact]
    public void BoundsStrictUnicodeAndNonfiniteObservationsDoNotInventResponses()
    {
        Assert.Null(SidebandRemoteCommands.CreateCommandField("echo "));
        Assert.NotNull(SidebandRemoteCommands.CreateCommandField(new string('x', 4096)));
        Assert.Throws<ArgumentException>(() => SidebandRemoteCommands.CreateCommandField(new string('x', 4097)));
        Assert.Throws<ArgumentException>(() => SidebandRemoteCommands.CreateCommandField(new string('✓', 1366)));
        Assert.Throws<EncoderFallbackException>(() => SidebandRemoteCommands.CreateCommandField("\ud800"));
        Assert.Throws<InvalidDataException>(() => SidebandRemoteCommands.CreateBuiltinReply(V.From(true)));
        Assert.Throws<InvalidDataException>(() => SidebandRemoteCommands.CreateBuiltinReply(Command(3, V.From("not binary"))));
        Assert.Throws<InvalidDataException>(() => SidebandRemoteCommands.CreateBuiltinReply(Command(3, V.From(new byte[4097]))));
        Assert.Throws<DecoderFallbackException>(() => SidebandRemoteCommands.CreateBuiltinReply(Command(3, V.From(new byte[] { 255 }))));
        Assert.Equal("No reception info available", SidebandRemoteCommands.CreateBuiltinReply(Command(4, V.Nil),
            double.NaN, double.PositiveInfinity, double.NegativeInfinity));
    }

    private static V Command(long command, V value) => V.Map(KeyValuePair.Create(V.From(command), value));
    private static double? Number(JsonElement item, string key) => item.GetProperty(key) is { ValueKind: JsonValueKind.Number } number
        ? number.GetDouble() : null;
    private static JsonDocument Fixture() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-remote.json")));
    private static V Value(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => V.Array(value.EnumerateArray().Select(Value).ToArray()),
        JsonValueKind.Object when value.TryGetProperty("binary", out var binary) => V.From(Convert.FromHexString(binary.GetString()!)),
        JsonValueKind.Object => V.Map(value.EnumerateObject().Select(pair =>
            KeyValuePair.Create(V.From(long.Parse(pair.Name, System.Globalization.CultureInfo.InvariantCulture)), Value(pair.Value))).ToArray()),
        JsonValueKind.String => V.From(value.GetString()!),
        JsonValueKind.True => V.From(true), JsonValueKind.False => V.From(false),
        JsonValueKind.Number => V.From(value.GetInt64()),
        _ => V.Nil
    };
}
