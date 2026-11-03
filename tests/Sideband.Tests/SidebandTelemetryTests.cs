using System.Text.Json;
using Sideband;
using V = Sideband.SidebandTelemetryValue;
using S = Sideband.SidebandTelemetrySensorId;
using K = Sideband.SidebandTelemetryValueKind;

namespace Sideband.Tests;

// GenerateTelemetryVectors.py executes primary d069122af14880e43607aed0b71add4c7806fbe0.
// sense.py SHA-256 80418b1aa4de638f2c65f1bd64e5ea0466378a6f0bc682781672ef89b93a7dab.
// Clock-controlled, hardware-free sensor.pack/unpack and Telemeter.from_packed/read_all, NOT live-node evidence.
[Trait("Category", "Unit")]
public sealed class SidebandTelemetryTests
{
    private static JsonDocument Fixture() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-telemetry.json")));

    public static IEnumerable<object[]> ReferenceCases()
    {
        using var fixture = Fixture();
        return fixture.RootElement.GetProperty("cases").EnumerateArray().Select(c => new object[]
        {
            c.GetProperty("name").GetString()!, c.GetProperty("sid").GetByte(),
            c.GetProperty("wire_hex").GetString()!, c.GetProperty("unpacked_value_hex").GetString()!
        }).ToArray();
    }

    [Theory]
    [MemberData(nameof(ReferenceCases))]
    public void ActualSensorVectorsDecodeAndPreserveWire(string name, byte id, string wireHex, string dataHex)
    {
        Assert.NotEmpty(name);
        var wire = Convert.FromHexString(wireHex);
        var report = SidebandTelemetryCodec.Decode(wire);
        var reading = Assert.IsType<SidebandTelemetryReading>(report.Get(id));
        Assert.NotEqual(SidebandTelemetryStatus.Malformed, reading.Status);
        Assert.NotEqual(SidebandTelemetryStatus.Unknown, reading.Status);
        EqualValue(V.Decode(Convert.FromHexString(dataHex)), reading.Data);
        Assert.Equal(wire, SidebandTelemetryCodec.Encode(report));
        Assert.Equal(SidebandTelemetryCodec.SensorName(id), reading.Name);
    }

    [Theory]
    [MemberData(nameof(ReferenceCases))]
    public void ManualNamedDataEncodesForEverySensor(string name, byte id, string wireHex, string dataHex)
    {
        var expected = SidebandTelemetryCodec.Decode(Convert.FromHexString(wireHex)).Get(id)!;
        var manual = SidebandTelemetryCodec.FromData((S)id, V.Decode(Convert.FromHexString(dataHex)));
        EqualValue(expected.Data, manual.Data);
        if (name.EndsWith("-nil") && id == (byte)S.Interfaces)
            Assert.Empty(manual.Data.Members); // Upstream Interfaces.unpack(nil) is {}, not nil.
        Assert.NotEqual(SidebandTelemetryStatus.Malformed, manual.Status);
    }

    [Fact]
    public void CompleteTelemeterHasAll27SensorsAndNamedPresentations()
    {
        using var fixture = Fixture();
        var report = SidebandTelemetryCodec.Decode(Convert.FromHexString(fixture.RootElement.GetProperty("all_wire_hex").GetString()!));
        Assert.Equal(27, report.Sensors.Count);
        Assert.Equal(27, Enum.GetValues<S>().Length);
        var expected = V.Decode(Convert.FromHexString(fixture.RootElement.GetProperty("all_readings_hex").GetString()!));
        foreach (var sensor in report.Sensors)
        {
            Assert.Equal(SidebandTelemetryStatus.Available, sensor.Status);
            EqualValue(expected.Get(sensor.Name)!, sensor.Data);
            Assert.True(sensor.Fields.Count != 0 || sensor.Entries.Count != 0, sensor.Name);
        }
        Assert.Equal(1700000000, report.TimestampUtcSeconds);
        var location = Assert.IsType<SidebandTelemetryLocation>(report.Get(S.Location)!.Location);
        Assert.Equal(new(51.5, -0.125, -12.5, 36, 270, 3.25, 1699999993), location);
        Assert.Equal("km/h", report.Get(S.Location)!.Fields.Single(f => f.Name == "speed").Unit);
        Assert.Equal(1250.5, report.Get(S.Received)!.Data.Get("distance")!.Get("geodesic")!.Number);
        Assert.Equal("Radio", report.Get(S.PowerConsumption)!.Entries[1].Label);
        Assert.Equal(75, report.Get(S.Ram)!.Entries[0].Fields.Single(f => f.Name == "free").Value.Number / 8192 * 100);
        Assert.Equal(25, report.Get(S.Tank)!.Entries[0].Fields.Single(f => f.Name == "percent").Value.Number);
        Assert.Equal("L", report.Get(S.Tank)!.Entries[0].Fields.Single(f => f.Name == "unit").Value.Text);
        Assert.Equal(1_000_000, report.Get(S.LxmfPropagation)!.Fields.Single(f => f.Name == "delivery_limit_bytes").Value.Number);
        Assert.Equal(1, report.Get(S.LxmfPropagation)!.Fields.Single(f => f.Name == "active_peers").Value.Integer);
        Assert.NotEmpty(report.Get(S.ConnectionMap)!.Entries);
        Assert.NotEmpty(report.Get(S.Interfaces)!.Entries);
        Assert.NotEmpty(report.Get(S.LxmfPropagation)!.Entries);
        Assert.NotEmpty(report.Get(S.RnsTransport)!.Entries);
    }

    [Fact]
    public void UnknownSafeSensorsAndBinaryMapKeysSurviveWithoutPretendingToRender()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var value = V.Map(KeyValuePair.Create(V.From(bytes),
            V.Array(V.Nil, V.From(true), V.From("future"), V.From(ulong.MaxValue))));
        bytes[0] = 9;
        var future = SidebandTelemetryCodec.FromWireValue(0x0d, value);
        var report = SidebandTelemetryCodec.Create(1234, future);
        var decoded = SidebandTelemetryCodec.Decode(SidebandTelemetryCodec.Encode(report));
        Assert.Equal(SidebandTelemetryStatus.Unknown, decoded.Get(0x0d)!.Status);
        Assert.False(decoded.Get(0x0d)!.IsKnown);
        Assert.Empty(decoded.Get(0x0d)!.Fields);
        Assert.Equal(new byte[] { 1, 2, 3 }, decoded.Get(0x0d)!.Data.Members[0].Key.GetBinary());
        var detached = decoded.Get(0x0d)!.Data.Members[0].Key.GetBinary()!;
        detached[0] = 8;
        EqualValue(value, decoded.Get(0x0d)!.Data);
    }

    [Fact]
    public void MalformedKnownSensorIsIsolatedAndRetainedWhileHealthySensorsDecode()
    {
        foreach (var id in Enum.GetValues<S>())
        {
            var bad = SidebandTelemetryCodec.FromWireValue((byte)id, V.From(new byte[] { 1 }));
            Assert.Equal(SidebandTelemetryStatus.Malformed, bad.Status);
            Assert.NotNull(bad.Error);
            var report = SidebandTelemetryCodec.FromSensors(bad,
                SidebandTelemetryCodec.FromWireValue(0xd0, V.From("safe future value")));
            var decoded = SidebandTelemetryCodec.Decode(SidebandTelemetryCodec.Encode(report));
            Assert.Equal(SidebandTelemetryStatus.Malformed, decoded.Get(id)!.Status);
            Assert.Equal("safe future value", decoded.Get(0xd0)!.Data.Text);
        }
    }

    [Fact]
    public void MissingTimeAndPartialStructuredSensorsAreExplicit()
    {
        var report = SidebandTelemetryCodec.FromSensors(
            SidebandTelemetryCodec.FromWireValue((byte)S.RnsTransport, V.Fields(("speed_rx", V.From(100.0)))),
            SidebandTelemetryCodec.FromWireValue((byte)S.LxmfPropagation, V.Map()),
            SidebandTelemetryCodec.FromWireValue((byte)S.ConnectionMap, V.Map()));
        Assert.Null(report.TimestampUtcSeconds);
        Assert.All(report.Sensors, s => Assert.Equal(SidebandTelemetryStatus.Partial, s.Status));
        Assert.Contains("transport_identity", report.Get(S.RnsTransport)!.Error);
        var decoded = SidebandTelemetryCodec.Decode(SidebandTelemetryCodec.Encode(report));
        Assert.Equal(100.0, decoded.Get(S.RnsTransport)!.Fields.Single().Value.Number);
        Assert.Empty(SidebandTelemetryCodec.Decode(Convert.FromHexString("80")).Sensors);
    }

    [Theory]
    [InlineData("")]
    [InlineData("c0")]
    [InlineData("90")]
    [InlineData("81010100")]
    [InlineData("8201010102")] // Duplicate sensor ID.
    [InlineData("81a13101")]
    [InlineData("81c301")] // Booleans are not sensor IDs.
    [InlineData("81cd010001")]
    [InlineData("8101c1")]
    [InlineData("8101caff800000")] // Nonfinite.
    [InlineData("8101d40100")] // Extensions are not a safe Sideband sensor value.
    [InlineData("8101a1ff")]
    [InlineData("810191")]
    [InlineData("8101df7fffffff")]
    [InlineData("8101c6ffffffff")]
    public void InvalidMessagePackAndRootContractsFailExplicitly(string hex)
    {
        Assert.False(SidebandTelemetryCodec.TryDecode(Convert.FromHexString(hex), out var report));
        Assert.Null(report);
    }

    [Fact]
    public void BoundsApplyToBothParsingAndSyntheticSerialization()
    {
        Assert.False(SidebandTelemetryCodec.TryDecode(new byte[SidebandTelemetryCodec.MaxEncodedBytes + 1], out _));
        var atLimit = V.From(new byte[SidebandTelemetryCodec.MaxValueBytes]);
        Assert.Equal(SidebandTelemetryCodec.MaxValueBytes, V.Decode(atLimit.Encode()).GetBinary()!.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => V.From(new byte[SidebandTelemetryCodec.MaxValueBytes + 1]));
        var maxArray = V.Array(Enumerable.Repeat(V.Nil, SidebandTelemetryCodec.MaxContainerEntries).ToArray());
        Assert.Equal(SidebandTelemetryCodec.MaxContainerEntries, V.Decode(maxArray.Encode()).Items.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => V.Array(new V[SidebandTelemetryCodec.MaxContainerEntries + 1]));
        var nested = V.Nil;
        for (var i = 0; i < SidebandTelemetryCodec.MaxDepth; i++) nested = V.Array(nested);
        Assert.Throws<ArgumentException>(() => V.Array(nested));
        Assert.Throws<FormatException>(() => V.Decode(Enumerable.Repeat((byte)0x91, SidebandTelemetryCodec.MaxDepth + 1).Append((byte)0xc0).ToArray()));
        Assert.Throws<ArgumentException>(() => V.Array(Enumerable.Repeat(maxArray, 17).ToArray()));
        var raw = DotReticulum.Core.MsgPack.Pack(Enumerable.Range(0, 17).Select(_ => (object?)new object?[1024]).ToArray());
        Assert.Throws<FormatException>(() => V.Decode(raw));
        Assert.Throws<ArgumentException>(() => V.Map(
            KeyValuePair.Create(V.From("same"), V.Nil), KeyValuePair.Create(V.From("same"), V.Nil)));
        Assert.Throws<ArgumentException>(() => V.Map(KeyValuePair.Create(V.Nil, V.Nil)));
        Assert.Throws<ArgumentException>(() => V.From("\ud800"));
        Assert.Equal(65_535, V.From(new string('\u20ac', 21_845)).Encode().Length - 3);
        Assert.Throws<ArgumentException>(() => V.From(new string('\u20ac', 21_846)));
        Assert.Equal(3, V.Decode(V.Array(atLimit, atLimit, atLimit).Encode()).Items.Count);
        Assert.Throws<ArgumentException>(() => V.Array(atLimit, atLimit, atLimit, atLimit));
        var allIds = Enumerable.Range(0, 256).Select(id => SidebandTelemetryCodec.FromWireValue((byte)id, V.Nil)).ToArray();
        Assert.Equal(256, SidebandTelemetryCodec.Decode(SidebandTelemetryCodec.Encode(SidebandTelemetryCodec.FromSensors(allIds))).Sensors.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => SidebandTelemetryCodec.Create(1, allIds));
    }

    [Fact]
    public void LocationWireWidthsSignednessTimestampsAndOverflowAreExplicit()
    {
        var location = new SidebandTelemetryLocation(-90, 180, -10.25, 42949672.95, -2.5, 655.35, 1700000000.5);
        var reading = SidebandTelemetryCodec.FromLocation(location);
        var decoded = SidebandTelemetryCodec.Decode(SidebandTelemetryCodec.Encode(SidebandTelemetryCodec.FromSensors(reading)));
        Assert.Equal(location, decoded.Get(S.Location)!.Location);
        Assert.Equal(2, reading.WireValue.Items[5].GetBinary()!.Length);
        var bad = V.Array(reading.WireValue.Items.Select((v, i) => i == 0 ? V.From(new byte[3]) : v).ToArray());
        Assert.Equal(SidebandTelemetryStatus.Malformed, SidebandTelemetryCodec.FromWireValue((byte)S.Location, bad).Status);
        Assert.Throws<OverflowException>(() => SidebandTelemetryCodec.FromLocation(location with { Accuracy = 655.36 }));
        Assert.Throws<OverflowException>(() => SidebandTelemetryCodec.FromLocation(location with { Speed = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => SidebandTelemetryCodec.FromLocation(location with { Latitude = double.NaN }));
        var noGeo = SidebandTelemetryCodec.FromLocation(location with { Latitude = 123 });
        Assert.Equal(123, noGeo.Location!.Latitude); // Wire codec does not silently impose map/GPS policy.
        var exact = SidebandTelemetryCodec.FromData(S.Location,
            V.Fields(reading.Data.Members.Select(e => (e.Key.Text!,
                e.Key.Text == "last_update" ? V.From(9_007_199_254_740_993L) : e.Value)).ToArray()));
        var exactReport = SidebandTelemetryCodec.Decode(SidebandTelemetryCodec.Encode(SidebandTelemetryCodec.Create(9_007_199_254_740_993L, exact)));
        Assert.Equal(9_007_199_254_740_993L, exactReport.TimestampValue!.Integer);
        Assert.Equal(9_007_199_254_740_993L, exactReport.Get(S.Location)!.LocationUpdatedValue!.Integer);
    }

    [Fact]
    public void ZeroCapacityDoesNotCrashPresentationAndDuplicateLabelsFail()
    {
        var ram = SidebandTelemetryCodec.FromData(S.Ram,
            V.Map(KeyValuePair.Create(V.From(0L), V.Array(V.From(0L), V.From(0L)))));
        Assert.True(ram.Entries.Single().Fields.Single(f => f.Name == "percent").Value.IsNil);
        var duplicate = V.Array(V.Array(V.From(0L), V.Array(V.From(1L), V.From(0L))),
            V.Array(V.From(0L), V.Array(V.From(2L), V.From(0L))));
        Assert.Equal(SidebandTelemetryStatus.Malformed, SidebandTelemetryCodec.FromWireValue((byte)S.Ram, duplicate).Status);
        Assert.Throws<ArgumentException>(() => SidebandTelemetryCodec.FromSensors(ram, ram));
    }

    [Fact]
    public void TimebaseCommandsAndLxmfFieldIdsMatchExecutedReference()
    {
        using var fixture = Fixture();
        var root = fixture.RootElement;
        Assert.Equal("d069122af14880e43607aed0b71add4c7806fbe0", root.GetProperty("revision").GetString());
        Assert.Equal(SidebandTelemetryCodec.TelemetryField, root.GetProperty("lxmf_fields").GetProperty("FIELD_TELEMETRY").GetInt32());
        Assert.Equal(SidebandTelemetryCodec.TelemetryStreamField, root.GetProperty("lxmf_fields").GetProperty("FIELD_TELEMETRY_STREAM").GetInt32());
        Assert.Equal(SidebandTelemetryCodec.CommandsField, root.GetProperty("lxmf_fields").GetProperty("FIELD_COMMANDS").GetInt32());
        Assert.Equal(1, (int)SidebandTelemetryCommand.TelemetryRequest);
        Assert.Equal(4, (int)SidebandTelemetryCommand.SignalReport);
        Assert.Equal(0, root.GetProperty("commands").GetProperty("PLUGIN_COMMAND").GetInt32());
        Assert.Equal(2, root.GetProperty("commands").GetProperty("PING").GetInt32());
        Assert.Equal(3, root.GetProperty("commands").GetProperty("ECHO").GetInt32());
        foreach (var item in root.GetProperty("timebase_cases").EnumerateArray())
            if (item.GetProperty("value").ValueKind == JsonValueKind.Number)
                Assert.Equal(item.GetProperty("valid").GetBoolean(),
                    SidebandTelemetryCodec.IsValidTimebase(item.GetProperty("value").GetDouble(), 1700000000));
        Assert.False(SidebandTelemetryCodec.IsValidTimebase(double.NaN, 1700000000));
        Assert.False(SidebandTelemetryCodec.IsValidTimebase(double.PositiveInfinity, 1700000000));
    }

    [Fact]
    public void SyntheticEncodingMatchesActualPythonBinaryDecimalRounding()
    {
        using var fixture = Fixture();
        var root = fixture.RootElement;
        var cases = root.GetProperty("cases").EnumerateArray().ToArray();
        foreach (var input in root.GetProperty("observations").GetProperty("battery_rounding_inputs").EnumerateArray())
        {
            var value = input.GetDouble();
            var expected = cases.Single(c => c.GetProperty("name").GetString() ==
                "battery-rounding-" + (value == 100 ? "100.0" : value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            var battery = SidebandTelemetryCodec.FromData(S.Battery, V.Fields(
                ("charge_percent", V.From(value)), ("charging", V.From(false)), ("temperature", V.Nil)));
            Assert.Equal(Convert.FromHexString(expected.GetProperty("packed_value_hex").GetString()!), battery.WireValue.Encode());
        }
        var locationData = V.Decode(Convert.FromHexString(root.GetProperty("observations").GetProperty("location_rounding_input_hex").GetString()!));
        var location = SidebandTelemetryCodec.FromData(S.Location, locationData);
        var expectedLocation = cases.Single(c => c.GetProperty("name").GetString() == "location-rounding");
        Assert.Equal(Convert.FromHexString(expectedLocation.GetProperty("packed_value_hex").GetString()!), location.WireValue.Encode());
    }

    [Fact]
    public void UpstreamReceivedRepackAndZeroCapacityDefectsAreNotSilentlyInventedAway()
    {
        using var fixture = Fixture();
        var observations = fixture.RootElement.GetProperty("observations");
        var initial = V.Decode(Convert.FromHexString(observations.GetProperty("received_initial_unpack_hex").GetString()!));
        var upstreamRepack = V.Decode(Convert.FromHexString(observations.GetProperty("received_repack_hex").GetString()!));
        Assert.Equal(1250.5, initial.Get("distance")!.Get("geodesic")!.Number);
        Assert.True(upstreamRepack.Items[2].IsNil); // Upstream unpack leaves separate distance attributes unset.
        Assert.True(upstreamRepack.Items[3].IsNil);
        var original = SidebandTelemetryCodec.FromData(S.Received, initial);
        Assert.Equal(1250.5, original.WireValue.Items[2].Number);
        Assert.Equal("ZeroDivisionError", observations.GetProperty("zero_capacity_ram_render").GetString());
        Assert.Equal(27, observations.GetProperty("malformed_sensor_unpack").GetArrayLength());
        Assert.Equal("TypeError", observations.GetProperty("base_sensor_unpackb").GetString());
        Assert.Equal("accepted", observations.GetProperty("longitude_above_180").GetString());
        Assert.Equal("TypeError", observations.GetProperty("longitude_without_latitude").GetString());
    }

    [Fact]
    public void ActualCoreReceiptAndCommandObservationsExposeProvenanceWithoutIntegratingServices()
    {
        using var fixture = Fixture();
        var root = fixture.RootElement;
        var receipts = root.GetProperty("receipt_observations").EnumerateArray().ToArray();
        var direct = SidebandTelemetryCodec.Decode(Convert.FromHexString(receipts[0].GetProperty("wire_hex").GetString()!));
        Assert.Equal(-40, direct.Get(S.PhysicalLink)!.Data.Get("rssi")!.Number);
        Assert.True(direct.Get(S.PhysicalLink)!.Data.Get("snr")!.IsNil);
        Assert.Equal(0, direct.Get(S.PhysicalLink)!.Data.Get("q")!.Number);
        Assert.Equal(Enumerable.Range(32, 16).Select(n => (byte)n).ToArray(), direct.Get(S.Received)!.Data.Get("by")!.GetBinary());
        Assert.Equal(Enumerable.Range(48, 16).Select(n => (byte)n).ToArray(), direct.Get(S.Received)!.Data.Get("via")!.GetBinary());
        Assert.True(direct.Get(S.Received)!.Data.Get("distance")!.Get("geodesic")!.IsNil);
        var forwarded = SidebandTelemetryCodec.Decode(Convert.FromHexString(receipts[1].GetProperty("wire_hex").GetString()!));
        Assert.Equal(Enumerable.Range(64, 16).Select(n => (byte)n).ToArray(), forwarded.Get(S.Received)!.Data.Get("via")!.GetBinary());
        Assert.Equal(1250.5, forwarded.Get(S.Received)!.Data.Get("distance")!.Get("geodesic")!.Number);
        var observations = root.GetProperty("command_observations");
        var commands = V.Decode(Convert.FromHexString(observations.GetProperty("wire_hex").GetString()!));
        Assert.Equal(6, commands.Items.Count);
        Assert.Equal(2, commands.Items[0].Get(1)!.Items.Count);
        Assert.False(commands.Items[0].Get(1)!.Items[1].Boolean);
        Assert.Equal(new[] { "latest", "collector", "send_message", "send_message", "send_message", "plugin" },
            observations.GetProperty("callbacks").EnumerateArray().Select(c => c.GetProperty("method").GetString()));
        Assert.Equal(27, root.GetProperty("contracts").GetArrayLength());
    }

    [Fact]
    public void MalformedNestedStructuredValuesFailPerSensorWithoutHidingOtherReadings()
    {
        var rns = SidebandTelemetryCodec.FromWireValue((byte)S.RnsTransport,
            V.Fields(("path_table", V.Array(V.From(1L)))));
        Assert.Equal(SidebandTelemetryStatus.Malformed, rns.Status);
        var lxmf = SidebandTelemetryCodec.FromWireValue((byte)S.LxmfPropagation,
            V.Fields(("peers", V.Map(KeyValuePair.Create(V.From("not a hash"), V.Map())))));
        Assert.Equal(SidebandTelemetryStatus.Malformed, lxmf.Status);
        var map = SidebandTelemetryCodec.FromWireValue((byte)S.ConnectionMap,
            V.Fields(("maps", V.Map(KeyValuePair.Create(V.From(0L),
                V.Fields(("points", V.Map(KeyValuePair.Create(V.From(new byte[16]),
                    V.Fields(("signal", V.Fields(("rssi", V.From("not a number"))))))))))))));
        Assert.Equal(SidebandTelemetryStatus.Malformed, map.Status);
        var interfaces = SidebandTelemetryCodec.FromWireValue((byte)S.Interfaces,
            V.Map(KeyValuePair.Create(V.From("bad"), V.Map(KeyValuePair.Create(V.From(254L), V.From(new byte[15]))))));
        Assert.Equal(SidebandTelemetryStatus.Malformed, interfaces.Status);
        var report = SidebandTelemetryCodec.FromSensors(rns, lxmf, map, interfaces,
            SidebandTelemetryCodec.FromData(S.Temperature, V.Fields(("c", V.From(20.0)))));
        Assert.Equal(20, SidebandTelemetryCodec.Decode(SidebandTelemetryCodec.Encode(report)).Get(S.Temperature)!.Data.Get("c")!.Number);
    }

    private static void EqualValue(V expected, V actual)
    {
        if (expected.Number is { } number) { Assert.Equal(number, actual.Number); return; }
        Assert.Equal(expected.Kind, actual.Kind);
        switch (expected.Kind)
        {
            case K.Nil: break;
            case K.Boolean: Assert.Equal(expected.Boolean, actual.Boolean); break;
            case K.Text: Assert.Equal(expected.Text, actual.Text); break;
            case K.Binary: Assert.Equal(expected.GetBinary(), actual.GetBinary()); break;
            case K.Array:
                Assert.Equal(expected.Items.Count, actual.Items.Count);
                for (var i = 0; i < expected.Items.Count; i++) EqualValue(expected.Items[i], actual.Items[i]);
                break;
            case K.Map:
                Assert.Equal(expected.Members.Count, actual.Members.Count);
                foreach (var pair in expected.Members)
                {
                    var keyBytes = pair.Key.Encode();
                    var match = Assert.Single(actual.Members, p => p.Key.Encode().SequenceEqual(keyBytes));
                    EqualValue(pair.Value, match.Value);
                }
                break;
            default: throw new InvalidOperationException();
        }
    }
}
