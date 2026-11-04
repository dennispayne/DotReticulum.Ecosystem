using System.Text.Json;

namespace Sideband.Tests;

[Trait("Category", "Unit")]
public sealed class SidebandExtensionReferenceTests
{
    [Fact]
    public void ReferenceEvidenceRecordsPrimaryRevisionAndUnmodifiedSourceHashes()
    {
        using var fixture = Load();
        var root = fixture.RootElement;
        Assert.Equal("d069122af14880e43607aed0b71add4c7806fbe0", root.GetProperty("sideband_sha").GetString());
        var hashes = root.GetProperty("source_sha256");
        Assert.Equal("8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25",
            hashes.GetProperty("sbapp/sideband/core.py").GetString());
        Assert.Equal("47a051a26a21d53aa8d6232cfc438dfac5e9396814e4bbc6c0149fb74643b6ac",
            hashes.GetProperty("sbapp/sideband/plugins.py").GetString());
        Assert.Equal(13, root.GetProperty("examples").GetArrayLength());
        Assert.All(root.GetProperty("examples").EnumerateArray(),
            example => Assert.False(example.GetProperty("executed").GetBoolean()));
    }

    [Theory]
    [InlineData("ping", "8109918102c3")]
    [InlineData("ping anything", "8109918102c3")]
    [InlineData("signal", "8109918104c3")]
    [InlineData("signature", "8109918104c3")]
    [InlineData("echo hello echo world", "8109918103c40b68656c6c6f20776f726c64")]
    [InlineData("telemetry collector=False", "810991810192ce6553f09cc2")]
    [InlineData("telemetry collector=True", "810991810192ce6553f09cc3")]
    public void ActualPrimarySendMethodsRecordCommandFields(string input, string fieldsHex)
    {
        // GenerateSidebandExtensionObservations.py executes pinned methods and actual LXMF/MessagePack.
        // These observations do not test a native implementation or establish live interoperability.
        using var fixture = Load();
        var observation = fixture.RootElement.GetProperty("outgoing").EnumerateArray()
            .Single(item => item.GetProperty("input").GetString() == input);
        Assert.Equal(fieldsHex, observation.GetProperty("fields_hex").GetString());
    }

    [Fact]
    public void ActualPrimaryAuthorizationHasTrustedSettingPrecedenceAndSignatureGate()
    {
        using var fixture = Load();
        var root = fixture.RootElement;
        Assert.Equal([false, true, true, false, true],
            root.GetProperty("permissions").EnumerateArray()
                .Select(item => item.GetProperty("allowed").GetBoolean()).ToArray());
        var gate = root.GetProperty("delivery_gate").EnumerateArray().ToArray();
        Assert.Equal("commands", gate[0].GetProperty("effects")[0].GetString());
        Assert.Empty(gate[1].GetProperty("effects").EnumerateArray());
        Assert.Equal("ingest", gate[2].GetProperty("effects")[0].GetString());
        Assert.Empty(gate[3].GetProperty("effects").EnumerateArray());
    }

    [Fact]
    public void ActualPrimaryLoaderExecutesDisabledCommandStartAndExposesFullOwner()
    {
        using var fixture = Load();
        var root = fixture.RootElement;
        var loader = root.GetProperty("plugin_loader").EnumerateArray().ToArray();
        Assert.Empty(loader[0].GetProperty("observations").EnumerateArray());
        Assert.Empty(loader[0].GetProperty("registered").EnumerateArray());
        Assert.Single(loader[1].GetProperty("observations").EnumerateArray());
        Assert.Empty(loader[1].GetProperty("registered").EnumerateArray());
        Assert.Single(loader[1].GetProperty("services").EnumerateArray());
        Assert.Single(loader[1].GetProperty("telemetry").EnumerateArray());
        Assert.True(loader[2].GetProperty("same_owner").GetBoolean());
        Assert.True(loader[2].GetProperty("reload_stopped_previous").GetBoolean());
        Assert.Equal("KeyError", root.GetProperty("plugin_missing_class").GetString());
    }

    [Theory]
    [InlineData("ping", "Ping reply")]
    [InlineData("echo_unicode", "Echo reply: hello \u2603")]
    [InlineData("signal", "Link Quality: 91%\nRSSI: -87.5 dBm\nSNR: 7.25 dB")]
    [InlineData("signal_absent", "No reception info available")]
    public void ActualPrimaryCommandRepliesArePlainText(string name, string reply)
    {
        using var fixture = Load();
        var observation = fixture.RootElement.GetProperty("incoming").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == name);
        var effect = observation.GetProperty("effects")[0];
        Assert.Equal(reply, effect.GetProperty("args")[0].GetString());
        Assert.True(effect.GetProperty("kwargs").GetProperty("skip_fields").GetBoolean());
        Assert.True(effect.GetProperty("kwargs").GetProperty("no_display").GetBoolean());
    }

    [Fact]
    public void ActualPrimaryRepositoryRoutesAndCertificateAreRecordedWithoutHosting()
    {
        using var fixture = Load();
        var root = fixture.RootElement;
        var routes = root.GetProperty("repository_routes").EnumerateArray().ToArray();
        var packages = routes.Single(item => item.GetProperty("path").GetString() == "/pkglist");
        Assert.Equal("[\"a.txt\", \"z.apk\"]", packages.GetProperty("body_utf8").GetString());
        var missing = routes.Single(item => item.GetProperty("path").GetString() == "/missing");
        Assert.Equal(500, missing.GetProperty("statuses")[0].GetInt32());
        var certificate = root.GetProperty("certificate");
        Assert.Equal("Sideband Repository", certificate.GetProperty("common_name").GetString());
        Assert.Equal("localhost", certificate.GetProperty("san_dns")[0].GetString());
        Assert.Equal("secp256r1", certificate.GetProperty("key_curve").GetString());
        Assert.Equal(3666, certificate.GetProperty("validity_days").GetInt32());
        Assert.True(certificate.GetProperty("restart_reuses_public_key").GetBoolean());
        Assert.True(certificate.GetProperty("restart_regenerates_certificate").GetBoolean());
        Assert.False(certificate.GetProperty("private_material_recorded").GetBoolean());
    }

    private static JsonDocument Load() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-extensions.json")));
}
