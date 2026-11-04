using System.Text.Json;

namespace Sideband.Tests;

/// <summary>Actual primary d069122 lxmf_delivery/log_announce; generator records source SHA-256 and observer seams.</summary>
[Trait("Category", "Unit")]
public sealed class SidebandIncomingAdmissionReferenceTests
{
    [Fact]
    public void ActualPrimaryConversationCreationDistinguishesEmptyOrdinaryAndMetadataOnlyMedia()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-admission.json")));
        var ingestions = fixture.RootElement.GetProperty("ingestions").EnumerateArray().ToArray();
        Assert.Equal(10, ingestions.Length);
        foreach (var item in ingestions)
        {
            var trace = item.GetProperty("trace").EnumerateArray().Select(e => e.GetString()).ToArray();
            Assert.Equal("save_message", trace[0]);
            Assert.Equal(item.GetProperty("kind").GetString() is "text" or "title" or "empty" or "attachment" or
                "commands_with_text" or "telemetry_attachment_text", trace.Contains("create_conversation"));
        }
    }

    [Fact]
    public void All112PrimaryOrdinaryAndCommandAdmissionObservationsMatchSharedPolicy()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-admission.json")));
        var observations = fixture.RootElement.GetProperty("observations").EnumerateArray().ToArray();
        Assert.Equal(112, observations.Length);
        foreach (var item in observations)
        {
            var commands = item.GetProperty("kind").GetString()!.StartsWith("commands", StringComparison.Ordinal);
            var trace = item.GetProperty("trace").EnumerateArray().Select(e => e.GetString()).ToArray();
            var actual = SidebandTelemetryPolicy.DeliveryDisposition(
                new() { AllowRequestsFromAnyone = item.GetProperty("authorized").GetBoolean() },
                new(item.GetProperty("known").GetBoolean()),
                item.GetProperty("signature").GetBoolean(), commands, item.GetProperty("ignore_unknown").GetBoolean());
            Assert.Equal(trace.Contains("commands") ? SidebandTelemetryDeliveryDisposition.HandleCommands :
                trace.Contains("ingest") ? SidebandTelemetryDeliveryDisposition.IngestMessage :
                SidebandTelemetryDeliveryDisposition.Drop, actual);
            if (item.GetProperty("ignore_unknown").GetBoolean())
                Assert.Equal("conversation", trace[0]);
        }
        Assert.Equal(["save_announce", "announce_flag"],
            fixture.RootElement.GetProperty("announce_trace").EnumerateArray().Select(e => e.GetString()));
    }
}
