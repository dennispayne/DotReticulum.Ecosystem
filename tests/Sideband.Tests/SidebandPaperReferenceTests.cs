using System.Text.Json;
using DotReticulum.Core;
using LXMF;

namespace Sideband.Tests;

/// <summary>Primary d069122 paper_message/lxm_ingest_uri, generated with real pinned LXMF 1.2.1 packing.</summary>
[Trait("Category", "Unit")]
public sealed class SidebandPaperReferenceTests
{
    [Fact]
    public void ActualPrimaryPaperMessagesPreserveSignedContentFieldsAndTicketFlagIsNotATicket()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-paper.json")));
        var root = fixture.RootElement;
        Assert.Equal("d069122af14880e43607aed0b71add4c7806fbe0", root.GetProperty("revision").GetString());
        Assert.Equal("8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25", root.GetProperty("source_sha256").GetString());
        using var sender = Identity.FromPublicKey(Convert.FromHexString(root.GetProperty("sender_public").GetString()!));
        foreach (var item in root.GetProperty("cases").EnumerateArray())
        {
            var accepted = item.GetProperty("accepted").GetBoolean();
            Assert.Equal(item.GetProperty("name").GetString() is "untrusted" or "trusted" or "whitespace", accepted);
            if (!accepted) continue;
            var message = LxMessage.Unpack(Convert.FromHexString(item.GetProperty("packed").GetString()!), _ => sender, LxmMethod.Paper);
            Assert.True(message.SignatureValidated);
            Assert.Equal(item.GetProperty("content").GetString(), message.Content);
            Assert.Equal("", message.Title);
            Assert.Equal(LxmMethod.Paper, message.Method);
            Assert.Equal(item.GetProperty("trusted").GetBoolean(), item.GetProperty("include_ticket").GetBoolean());
            Assert.False(item.GetProperty("field_ticket").GetBoolean());
            Assert.False(message.TryGetField(LxmField.Ticket, out _));
            Assert.True(message.TryGetField(LxmField.IconAppearance, out var field));
            var appearance = Assert.IsType<object?[]>(field);
            Assert.Equal("paperclip", appearance[0]);
            Assert.Equal(Convert.FromHexString("112233"), Assert.IsType<byte[]>(appearance[1]));
            Assert.StartsWith("lxm://", item.GetProperty("uri").GetString());
        }
        var statuses = root.GetProperty("statuses").EnumerateArray().ToArray();
        Assert.Equal(4, statuses.Length);
        Assert.Contains("no decodable", statuses[0].GetProperty("response").GetString());
        Assert.Contains("added to your conversation list", statuses[1].GetProperty("response").GetString());
        Assert.Contains("already been processed", statuses[2].GetProperty("response").GetString());
        Assert.Contains("not addressed", statuses[3].GetProperty("response").GetString());
    }
}
