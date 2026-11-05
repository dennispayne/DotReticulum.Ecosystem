using System.Globalization;
using System.Text;
using System.Text.Json;
using Sideband;
using S = Sideband.SidebandTelemetrySensorId;
using V = Sideband.SidebandTelemetryValue;

namespace Sideband.Tests;

// GenerateMqttVectors.py executes primary d069122af14880e43607aed0b71add4c7806fbe0.
// mqtt.py b83a9adff2fd1ab200901b03dfa44aaf5ab4929c58c8753406e89032006df3f0;
// sense.py 80418b1aa4de638f2c65f1bd64e5ea0466378a6f0bc682781672ef89b93a7dab.
// Actual handle/post_message and bundled-paho encoding, not broker/application interoperability.
[Trait("Category", "Unit")]
public sealed class SidebandMqttVectorTests
{
    [Fact]
    public void ActualBundledPahoScalarPayloadsIncludeIndependentFiniteBitPatterns()
    {
        using var fixture = Fixture();
        var root = fixture.RootElement;
        Assert.Equal("d069122af14880e43607aed0b71add4c7806fbe0", root.GetProperty("revision").GetString());
        Assert.Equal("a48f6d3ff640d2edcbf7c62aa1bdd88984c2fd951aa5e32cb8eaf60325dfd9f5",
            root.GetProperty("source_sha256").GetProperty("sbapp/pmqtt/client.py").GetString());
        Assert.True(root.GetProperty("scalars").GetArrayLength() > 2000);
        foreach (var value in root.GetProperty("scalars").EnumerateArray())
        {
            var actual = SidebandMqttPayload.Encode(V.Decode(Convert.FromHexString(value.GetProperty("value_hex").GetString()!)));
            Assert.True(actual.AsSpan().SequenceEqual(Convert.FromHexString(value.GetProperty("payload_hex").GetString()!)),
                value.GetProperty("value_repr").GetString());
        }
        Assert.Throws<FormatException>(() => SidebandMqttPayload.Encode(V.Array()));
        Assert.Throws<FormatException>(() => SidebandMqttPayload.Encode(V.Map()));
    }

    [Theory]
    [MemberData(nameof(NonLocationCases))]
    public void ActualNonLocationSensorTopicsPayloadsAndDrainOrder(string name, string json) =>
        Verify(name, json, null);

    public static IEnumerable<object[]> NonLocationCases() => Cases(location: false);
    internal static IEnumerable<object[]> LocationCases() => Cases(location: true);

    private static IEnumerable<object[]> Cases(bool location)
    {
        using var fixture = Fixture();
        return fixture.RootElement.GetProperty("cases").EnumerateArray().Where(item =>
        {
            var report = Report(item);
            return (report.Get(S.Location) is { Data.IsNil: false }) == location;
        }).Select(item => new object[] { item.GetProperty("name").GetString()!, item.GetRawText() }).ToArray();
    }

    internal static void Verify(string name, string json, SidebandGeoid? geoid)
    {
        using var document = JsonDocument.Parse(json);
        var item = document.RootElement;
        var report = Report(item);
        if (item.TryGetProperty("error", out var referenceError))
        {
            Assert.Equal("TypeError", referenceError.GetString());
            if (report.Get(S.Location) is { Data.IsNil: false })
            {
                var corrected = SidebandMqtt.Project(Context, report, geoid!.Offset);
                Assert.Empty(corrected.Errors);
                Assert.Contains(corrected.Messages, message => message.Topic.EndsWith("/location/altitude", StringComparison.Ordinal));
                return; // Primary polar lookup fails; valid native polar interpolation is an explicit intentional difference.
            }
            Assert.Throws<FormatException>(() => SidebandMqtt.Project(Context, report));
            return;
        }
        var actual = SidebandMqtt.Project(Context, report, geoid is null ? null : geoid.Offset);
        var expected = item.GetProperty("messages").EnumerateArray().Where(message => !message.TryGetProperty("error", out _)).ToArray();
        Assert.True(expected.Length == actual.Messages.Count, $"{name}: expected{expected.Length}, actual{actual.Messages.Count}; {string.Join("; ", actual.Errors)}");
        for (var index = 0; index < expected.Length; index++)
        {
            var topic = expected[index].GetProperty("topic").GetString()!;
            var bytes = Convert.FromHexString(expected[index].GetProperty("payload_hex").GetString()!);
            Assert.Equal(topic, actual.Messages[index].Topic);
            Assert.Equal(1, expected[index].GetProperty("qos").GetInt32());
            Assert.False(expected[index].GetProperty("retain").GetBoolean());
            var tolerance = topic.EndsWith("/location/altitude", StringComparison.Ordinal) ? 1e-8 :
                topic.EndsWith("/location/angle_to_horizon", StringComparison.Ordinal) ? 1e-10 :
                topic.EndsWith("/location/radio_horizon", StringComparison.Ordinal) ? 1e-6 : 0;
            if (tolerance > 0)
            {
                var want = double.Parse(Encoding.ASCII.GetString(bytes), CultureInfo.InvariantCulture);
                var got = double.Parse(Encoding.ASCII.GetString(actual.Messages[index].Payload.Span), CultureInfo.InvariantCulture);
                Assert.True(double.IsFinite(got) && Math.Abs(want - got) <= tolerance, $"{name}: {topic}; {want:R} != {got:R}");
            }
            else Assert.True(bytes.AsSpan().SequenceEqual(actual.Messages[index].Payload.Span),
                $"{name}: {topic}; expected'{Encoding.UTF8.GetString(bytes)}', actual'{Encoding.UTF8.GetString(actual.Messages[index].Payload.Span)}'");
        }
        foreach (var rejected in item.GetProperty("messages").EnumerateArray().Where(message => message.TryGetProperty("error", out _)))
        {
            Assert.NotEmpty(actual.Errors);
            Assert.DoesNotContain(actual.Messages, message => message.Topic == rejected.GetProperty("topic").GetString());
        }
    }

    private static SidebandTelemetryReport Report(JsonElement item) =>
        SidebandTelemetryCodec.Decode(Convert.FromHexString(item.GetProperty("wire_hex").GetString()!));
    internal const string Context = "000102030405060708090a0b0c0d0e0f";
    private static JsonDocument Fixture() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-mqtt.json")));
}

[Trait("Category", "Integration")]
public sealed class SidebandMqttGeoidVectorTests
{
    [Theory]
    [MemberData(nameof(Cases))]
    public void ActualLocationTopicsUseIndependentCubicGeoidWithExplicitPolarDifference(string name, string json)
    {
        var path = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_GEOID");
        Assert.SkipWhen(string.IsNullOrEmpty(path), "Set DOTRETICULUM_SIDEBAND_GEOID to the external EGM2008-5 grid; no grid is distributed.");
        using var geoid = SidebandGeoid.Open(path!);
        SidebandMqttVectorTests.Verify(name, json, geoid);
    }

    public static IEnumerable<object[]> Cases() => SidebandMqttVectorTests.LocationCases();
}
