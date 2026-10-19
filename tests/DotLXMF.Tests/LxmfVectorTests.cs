using System.Text.Json;
using DotReticulum.Core;

namespace DotReticulum.Lxmf.Tests;

// Vectors produced by executing upstream LXMF 1.2.1 (see GenerateLxmfVectors.py): our pack must reproduce upstream's bytes exactly (Ed25519 is deterministic)
// and our unpack must agree with upstream on every field, hash and signature.
public class LxmfVectorTests
{
    private static readonly JsonElement Root =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxmf-vectors.json"))).RootElement;

    private static byte[] Hex(JsonElement e) => Convert.FromHexString(e.GetString()!);

    private static Identity SourceIdentity => Identity.FromPrivateKey(Hex(Root.GetProperty("source_private_key")));

    private static Identity DestinationIdentity => Identity.FromPrivateKey(Hex(Root.GetProperty("destination_private_key")));

    public static IEnumerable<object[]> Labels() =>
        Root.GetProperty("messages").EnumerateArray().Select(m => new object[] { m.GetProperty("label").GetString()! });

    private static JsonElement Vector(string label) =>
        Root.GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("label").GetString() == label);

    private static Dictionary<object, object?> FieldsFor(string label) => label switch
    {
        "fields" => new()
        {
            [1L] = new object?[] { "embedded"u8.ToArray() },
            [5L] = new object?[] { new object?[] { "a.txt", "file-bytes"u8.ToArray() } },
            [0x0CL] = new object?[] { 1800000000.0, Enumerable.Range(0, 16).Select(i => (byte)i).ToArray() },
            [255L] = "dbg",
        },
        _ => [],
    };

    private static Identity? Recall(byte[] hash)
    {
        var source = SourceIdentity;
        return hash.AsSpan().SequenceEqual(LxMessage.DeliveryDestinationHash(source)) ? source : null;
    }

    [Fact]
    public void Constants_MatchUpstream()
    {
        Assert.Equal(LxMessage.Overhead, Root.GetProperty("overhead").GetInt32());
        Assert.Equal(LxMessage.EncryptedPacketMaxContent, Root.GetProperty("encrypted_packet_max_content").GetInt32());
        Assert.Equal(Root.GetProperty("link_packet_max_content").GetInt32(), LxMessage.LinkPacketMaxContent);
    }

    [Theory]
    [MemberData(nameof(Labels))]
    public void Unpack_MatchesUpstream(string label)
    {
        var v = Vector(label);
        var message = LxMessage.Unpack(Hex(v.GetProperty("packed")), Recall);
        Assert.Equal(Hex(v.GetProperty("destination_hash")), message.DestinationHash);
        Assert.Equal(Hex(v.GetProperty("source_hash")), message.SourceHash);
        Assert.Equal(Hex(v.GetProperty("hash")), message.Hash);
        Assert.Equal(Hex(v.GetProperty("signature")), message.Signature);
        Assert.Equal(v.GetProperty("timestamp").GetDouble(), message.Timestamp);
        Assert.Equal(Hex(v.GetProperty("title")), message.TitleBytes);
        Assert.Equal(Hex(v.GetProperty("content")), message.ContentBytes);
        Assert.Equal(Hex(v.GetProperty("fields_packed")), MsgPack.Pack(message.Fields));
        Assert.True(message.SignatureValidated);
        Assert.Equal(v.GetProperty("stamp").ValueKind == JsonValueKind.Null ? null : Hex(v.GetProperty("stamp")), message.Stamp);
    }

    [Theory]
    [MemberData(nameof(Labels))]
    public void Pack_ReproducesUpstreamBytes(string label)
    {
        if (label == "stamped") return; // covered by Stamped_PackWithUpstreamStamp_IsByteIdentical
        var v = Vector(label);
        var message = LxMessage.Create(Hex(v.GetProperty("destination_hash")), SourceIdentity, System.Text.Encoding.UTF8.GetString(Hex(v.GetProperty("content"))),
            System.Text.Encoding.UTF8.GetString(Hex(v.GetProperty("title"))), FieldsFor(label), (LxmMethod)v.GetProperty("desired_method").GetInt32(),
            v.GetProperty("timestamp").GetDouble());
        var packed = message.Pack();
        Assert.Equal(Hex(v.GetProperty("packed")), packed);
        Assert.Equal(Hex(v.GetProperty("hash")), message.Hash);
        Assert.Equal((LxmMethod)v.GetProperty("method").GetInt32(), message.Method);
        Assert.Equal(v.GetProperty("representation").GetInt32() == 2, message.IsResource);
    }

    [Fact]
    public void Stamped_PackWithUpstreamStamp_IsByteIdentical()
    {
        var v = Vector("stamped");
        var message = LxMessage.Create(Hex(v.GetProperty("destination_hash")), SourceIdentity, "Stamped", "S", null, LxmMethod.Direct, 1700000005.0);
        Assert.Equal(Hex(v.GetProperty("packed")), message.Pack(Hex(v.GetProperty("stamp"))));
        Assert.Equal(Hex(v.GetProperty("hash")), message.ComputeHash());
    }

    [Fact]
    public void Unpack_UnknownSource_IsUnverified_AndTamperedMessageIsInvalid()
    {
        var bytes = Hex(Vector("short").GetProperty("packed"));
        var unknown = LxMessage.Unpack(bytes, _ => null);
        Assert.False(unknown.SignatureValidated);
        Assert.Equal(LxmUnverifiedReason.SourceUnknown, unknown.UnverifiedReason);
        Assert.True(unknown.Validate(SourceIdentity));

        var tampered = (byte[])bytes.Clone();
        tampered[40] ^= 0x01;
        var invalid = LxMessage.Unpack(tampered, Recall);
        Assert.False(invalid.SignatureValidated);
        Assert.Equal(LxmUnverifiedReason.SignatureInvalid, invalid.UnverifiedReason);
    }

    [Fact]
    public void Stamp_WorkblockValueAndValidity_MatchUpstream()
    {
        var s = Root.GetProperty("stamp");
        var id = Hex(s.GetProperty("message_id"));
        var stamp = Hex(s.GetProperty("stamp"));
        Assert.Equal(s.GetProperty("workblock_sha256").GetString(), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(LxStamper.Workblock(id))));
        Assert.Equal(s.GetProperty("workblock_pn_sha256").GetString(),
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(LxStamper.Workblock(id, LxStamper.WorkblockExpandRoundsPn))));
        var workblock = LxStamper.Workblock(id);
        Assert.Equal(s.GetProperty("value").GetInt32(), LxStamper.Value(workblock, stamp));
        Assert.True(LxStamper.IsValid(stamp, 6, workblock));
        Assert.False(LxStamper.IsValid(stamp, 30, workblock));
    }

    [Fact]
    public void Stamp_GenerateProducesValidStamp()
    {
        var id = Identity.FullHash("stamp-me"u8);
        var stamp = LxStamper.Generate(id, 5);
        Assert.True(LxStamper.Validate(id, stamp, 5));
    }

    [Fact]
    public void AppData_BuildAndParse_RoundTrip()
    {
        var data = LxmfAppData.Build("Alice", 8);
        Assert.Equal("Alice", LxmfAppData.DisplayName(data));
        Assert.Equal(8, LxmfAppData.StampCost(data));
        Assert.Equal("Legacy", LxmfAppData.DisplayName("Legacy"u8.ToArray()));
        Assert.Null(LxmfAppData.StampCost("Legacy"u8.ToArray()));
        Assert.Null(LxmfAppData.StampCost(LxmfAppData.Build("x")));
    }

    [Fact]
    public void Pack_Twice_Throws_AndOversizedOpportunisticFallsBackToDirect()
    {
        var m = LxMessage.Create(new byte[16], DestinationIdentity, new string('x', 400), method: LxmMethod.Opportunistic);
        m.Pack();
        Assert.Equal(LxmMethod.Direct, m.Method);
        Assert.Throws<InvalidOperationException>(() => m.Pack());
    }
}
