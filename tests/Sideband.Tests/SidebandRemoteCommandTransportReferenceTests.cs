using System.Text.Json;
using DotReticulum.Core;
using LXMF;

namespace Sideband.Tests;

/// <summary>Actual primary d069122 API/pack observations; generator records exact SHA-256 and cache/router seams.</summary>
[Trait("Category", "Unit")]
public sealed class SidebandRemoteCommandTransportReferenceTests
{
    [Fact]
    public void ActualPackedMethodsMatchAll32CommandSelectionCasesIncludingLargeRatchetCommands()
    {
        using var source = Identity.Generate();
        using var recipient = Identity.Generate();
        using var fixture = Fixture();
        var sends = fixture.RootElement.GetProperty("sends").EnumerateArray().ToArray();
        Assert.Equal(32, sends.Length);
        foreach (var item in sends)
        {
            var commands = SidebandRemoteCommands.CreateCommandField(item.GetProperty("input").GetString()!)!;
            var message = LxMessage.Create(LxMessage.DeliveryDestinationHash(recipient), source, "",
                fields: new() { [LxmField.Commands] = MsgPack.Unpack(commands.Encode()) },
                method: (LxmMethod)item.GetProperty("desired_method").GetInt32(), timestamp: 12345);
            message.Pack(destinationIdentity: recipient);
            Assert.Equal((LxmMethod)item.GetProperty("actual_method").GetInt32(), message.Method);
            Assert.Equal(item.GetProperty("pn").GetBoolean() && item.GetProperty("fallback").GetBoolean(),
                item.GetProperty("retry_propagation").GetBoolean());
            Assert.Equal(["originator"], item.GetProperty("history").EnumerateArray().Select(e => e.GetString()));
        }
    }

    [Fact]
    public void ActualPrimaryRepliesToPropagatedCommandsPreferDirectWithoutFieldsOrOutgoingHistory()
    {
        using var fixture = Fixture();
        var replies = fixture.RootElement.GetProperty("replies").EnumerateArray().ToArray();
        Assert.Equal(12, replies.Length);
        foreach (var item in replies)
        {
            Assert.Equal((int)LxmMethod.Direct, item.GetProperty("actual_method").GetInt32());
            Assert.Empty(item.GetProperty("fields").EnumerateObject());
            Assert.Empty(item.GetProperty("history").EnumerateArray());
            Assert.Equal(item.GetProperty("pn").GetBoolean() && item.GetProperty("fallback").GetBoolean(),
                item.GetProperty("retry_propagation").GetBoolean());
        }
    }

    private static JsonDocument Fixture() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-remote-transport.json")));
}
