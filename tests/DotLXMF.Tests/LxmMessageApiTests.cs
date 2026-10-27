using System.Text;
using System.Text.Json;
using DotReticulum.Core;

namespace DotLXMF.Tests;

[Trait("Category", "Unit")]
public class LxmMessageApiTests
{
    // Executed primary LXMessage.py contracts, SHA e8ba02483217af43be284a72f1c059c730b7d047.
    private static JsonDocument Vectors() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxmf-contract-vectors.json")));

    [Fact]
    public void DeferredAssignment_PacksReferenceBytesAndRejectsReassignment()
    {
        using var vectors = Vectors();
        var root = vectors.RootElement;
        using var source = Identity.FromPrivateKey(Convert.FromHexString(root.GetProperty("source_private_key").GetString()!));
        using var destination = Identity.FromPrivateKey(Convert.FromHexString(root.GetProperty("destination_private_key").GetString()!));
        using var endpoint = DestinationEndpoint.Create(DestinationType.Single, DestinationDirection.Out, "lxmf", ["delivery"], destination);
        var message = LxMessage.CreateUnassigned(content: "Assigned", title: "A", timestamp: 1700000103.0);
        Assert.Throws<InvalidOperationException>(() => message.Pack());
        message.SetDestination(endpoint);
        message.SetSource(source);
        Assert.Throws<InvalidOperationException>(() => message.SetDestination(endpoint));
        Assert.Throws<InvalidOperationException>(() => message.SetSource(source));
        Assert.All(root.GetProperty("message_api").GetProperty("assignment_errors").EnumerateArray(),
            value => Assert.Equal("ValueError", value.GetString()));
        Assert.Equal(Convert.FromHexString(root.GetProperty("message_api").GetProperty("assigned_packed").GetString()!), message.Pack());
        Assert.Throws<InvalidOperationException>(() => message.SetContent("changed"));
    }

    [Fact]
    public void TextConversion_MatchesReferenceStrictTitleNullableContentAndSurrogateRejection()
    {
        using var vectors = Vectors();
        var api = vectors.RootElement.GetProperty("message_api");
        using var source = Identity.Generate();
        var message = LxMessage.CreateBinary(new byte[16], source, [0xff, 0x00, 0x80], [0xfe]);
        Assert.Equal("UnicodeDecodeError", api.GetProperty("invalid_title_error").GetString());
        Assert.Throws<DecoderFallbackException>(() => message.Title);
        Assert.Equal(JsonValueKind.Null, api.GetProperty("invalid_content").ValueKind);
        Assert.Null(message.Content);
        Assert.Equal("UnicodeEncodeError", api.GetProperty("surrogate_error").GetString());
        Assert.Throws<EncoderFallbackException>(() => LxMessage.Create(new byte[16], source, "\ud800"));
        message.SetTitle("Valid");
        message.SetContent("Grüße");
        message.SetFields(new Dictionary<object, object?> { [1L] = "field" });
        Assert.Equal("Valid", message.Title);
        Assert.Equal("Grüße", message.Content);
        Assert.Equal("field", message.Fields[1L]);
    }

    [Fact]
    public void PlainPacking_MatchesReferenceAndGroupOpportunisticIsAnUpstreamDefect()
    {
        using var vectors = Vectors();
        var root = vectors.RootElement;
        var row = root.GetProperty("destination_types");
        using var source = Identity.FromPrivateKey(Convert.FromHexString(root.GetProperty("source_private_key").GetString()!));
        using var plain = DestinationEndpoint.Create(DestinationType.Plain, DestinationDirection.Out, "lxmf", ["delivery"]);
        var message = LxMessage.Create(plain, source, "Plain", "P", method: LxmMethod.Opportunistic, timestamp: 1700000104.0);
        Assert.Equal(Convert.FromHexString(row.GetProperty("plain_packed").GetString()!), message.Pack());
        Assert.Equal(row.GetProperty("plain_encrypted").GetBoolean(), message.TransportEncrypted);
        Assert.Equal(row.GetProperty("plain_encryption").GetString(), message.TransportEncryption);
        var oversized = LxMessage.Create(plain, source, new string('x', 500), method: LxmMethod.Opportunistic);
        Assert.Throws<ArgumentException>(() => oversized.Pack());
        using var group = DestinationEndpoint.Create(DestinationType.Group, DestinationDirection.Out, "lxmf", ["delivery"], source);
        Assert.Equal("UnboundLocalError", row.GetProperty("group_opportunistic_error").GetString());
        Assert.Throws<NotSupportedException>(() => LxMessage.Create(group, source, "Group", method: LxmMethod.Opportunistic).Pack());
    }

    [Fact]
    public void GroupPaper_DecryptsExecutedReferenceAndRejectsWrongKey()
    {
        using var vectors = Vectors();
        var root = vectors.RootElement;
        var vector = root.GetProperty("group_paper");
        using var source = Identity.FromPrivateKey(Convert.FromHexString(root.GetProperty("source_private_key").GetString()!));
        using var destination = Identity.FromPrivateKey(Convert.FromHexString(root.GetProperty("destination_private_key").GetString()!));
        using var endpoint = DestinationEndpoint.Create(DestinationType.Group, DestinationDirection.Out, "lxmf", ["delivery"], destination);
        endpoint.LoadGroupKey(Convert.FromHexString(vector.GetProperty("key").GetString()!));
        var message = LxMessage.FromPaperUri(vector.GetProperty("uri").GetString()!, endpoint, _ => source);
        Assert.True(message.SignatureValidated);
        Assert.Equal(Convert.FromHexString(vector.GetProperty("packed").GetString()!), message.Packed);
        Assert.Equal(vector.GetProperty("encryption").GetString(), message.TransportEncryption);
        endpoint.CreateGroupKey();
        Assert.Throws<FormatException>(() => LxMessage.FromPaperUri(vector.GetProperty("uri").GetString()!, endpoint, _ => source));
    }

    [Fact]
    public void ExplicitEpochTimestamp_IsNotReplacedDuringHashingOrPacking()
    {
        using var vectors = Vectors();
        var root = vectors.RootElement;
        using var source = Identity.FromPrivateKey(Convert.FromHexString(root.GetProperty("source_private_key").GetString()!));
        using var destination = Identity.FromPrivateKey(Convert.FromHexString(root.GetProperty("destination_private_key").GetString()!));
        var message = LxMessage.Create(LxMessage.DeliveryDestinationHash(destination), source, "Epoch", "Zero", timestamp: 0);
        var hash = message.ComputeHash();
        Assert.Equal(Convert.FromHexString(root.GetProperty("zero_timestamp_packed").GetString()!), message.Pack());
        Assert.Equal(hash, message.Hash);
        Assert.Equal(0, message.Timestamp);
    }
}
