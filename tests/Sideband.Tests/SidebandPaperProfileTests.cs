using System.Text.Json;
using DotReticulum.Core;
using LXMF;

namespace Sideband.Tests;

// GenerateSidebandProfile.py executes primary Sideband d069122af14880e43607aed0b71add4c7806fbe0
// lxm_ingest/_db_message and LXMF e8ba02483217af43be284a72f1c059c730b7d047 pack/as_uri.
// The public synthetic fixture records core.py SHA-256 and contains no private identities.
[Trait("Category", "Unit")]
public sealed class SidebandPaperProfileTests
{
    [Fact]
    public void ActualPrimaryStoredPairRestoresExactOpaqueEnvelopeAndSignedPlaintext()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-paper-profile.json")));
        var row = fixture.RootElement.GetProperty("rows")[0];
        using var source = Identity.FromPublicKey(Convert.FromHexString(row.GetProperty("sourcePublicKey").GetString()!));
        var (wire, uri) = SidebandPaperProfile.Read(Convert.FromHexString(row.GetProperty("storedHex").GetString()!),
            Convert.FromHexString(row.GetProperty("destination").GetString()!));
        Assert.Equal(row.GetProperty("wireHex").GetString(), Convert.ToHexStringLower(wire));
        Assert.Equal(row.GetProperty("uri").GetString(), uri);
        var message = LxMessage.Unpack(wire, _ => source, LxmMethod.Paper);
        Assert.True(message.SignatureValidated);
        Assert.Equal(row.GetProperty("content").GetString(), message.Content);
        Assert.Equal(row.GetProperty("hash").GetString(), Convert.ToHexStringLower(message.Hash!));
    }

    [Fact]
    public void EnvelopeRestorationDoesNotAssertItsCiphertextMatchesTheStoredPlaintext()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-paper-profile.json")));
        var row = fixture.RootElement.GetProperty("rows")[0];
        var wire = Convert.FromHexString(row.GetProperty("wireHex").GetString()!);
        var envelope = Convert.FromHexString(row.GetProperty("paperHex").GetString()!);
        envelope[^1] ^= 1;
        var restored = SidebandPaperProfile.Read(MsgPack.Pack(new object?[] { wire, envelope }),
            Convert.FromHexString(row.GetProperty("destination").GetString()!));
        Assert.Equal(wire, restored.Wire);
        Assert.NotEqual(row.GetProperty("uri").GetString(), restored.Uri);
        Assert.Equal(LxMessage.PaperUriFromBytes(envelope), restored.Uri);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("short-pair")]
    [InlineData("long-pair")]
    [InlineData("wire-type")]
    [InlineData("empty-wire")]
    [InlineData("envelope-type")]
    [InlineData("short-envelope")]
    [InlineData("oversized-envelope")]
    [InlineData("destination")]
    [InlineData("trailing")]
    public void MalformedStoredPairFailsExplicitly(string scenario)
    {
        byte[] destination = new byte[16];
        object? wire = new byte[] { 1 };
        object? envelope = new byte[17];
        if (scenario == "wire-type") wire = "not binary";
        if (scenario == "empty-wire") wire = Array.Empty<byte>();
        if (scenario == "envelope-type") envelope = null;
        if (scenario == "short-envelope") envelope = new byte[16];
        if (scenario == "oversized-envelope") envelope = new byte[LxMessage.PaperMdu + 1];
        if (scenario == "destination") destination[0] = 1;
        var packed = scenario switch
        {
            "root" => MsgPack.Pack(1),
            "short-pair" => MsgPack.Pack(new object?[] { wire }),
            "long-pair" => MsgPack.Pack(new object?[] { wire, envelope, null }),
            _ => MsgPack.Pack(new object?[] { wire, envelope }),
        };
        if (scenario == "trailing") packed = [.. packed, 0];
        Assert.Throws<InvalidDataException>(() => SidebandPaperProfile.Read(packed, destination));
    }
}
