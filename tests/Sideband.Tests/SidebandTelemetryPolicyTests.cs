using System.Text.Json;
using Sideband;
using V = Sideband.SidebandTelemetryValue;

namespace Sideband.Tests;

// GenerateSidebandTelemetryPolicyVectors.py executes primary d069122af14880e43607aed0b71add4c7806fbe0.
// core.py SHA-256 8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25.
[Trait("Category", "Unit")]
public sealed class SidebandTelemetryPolicyTests
{
    private const double Now = 1_700_000_000;
    private static readonly JsonElement Root = Load("sideband-telemetry-policy.json");
    private static JsonElement Load(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));
        return doc.RootElement.Clone();
    }

    public static IEnumerable<object[]> Observations()
    {
        foreach (var group in new[] { "permissions", "fields", "sends", "requests", "deliveries", "receipts" })
            for (var index = 0; index < Root.GetProperty(group).GetArrayLength(); index++)
                yield return [group, index];
    }

    [Theory]
    [MemberData(nameof(Observations))]
    public void ActualPrimaryDecisionsMatchTypedHelper(string group, int index)
    {
        var item = Root.GetProperty(group)[index];
        var input = item.GetProperty("input");
        var settings = Settings(input);
        var conversation = Conversation(input);
        switch (group)
        {
            case "permissions":
                Assert.Equal(Bool(item, "send"), SidebandTelemetryPolicy.ShouldSendTelemetry(settings, conversation));
                Assert.Equal(Bool(item, "request"), SidebandTelemetryPolicy.AllowRequest(settings, conversation));
                Assert.Equal(Bool(item, "trusted"), conversation.IsTrusted);
                break;
            case "fields":
                var fields = SidebandTelemetryPolicy.EvaluateFields(settings, conversation, new(
                    Bool(input, "packed", true), Number(input, "own_time", Now), Number(input, "last_success"),
                    Bool(input, "force"), Bool(input, "authorized"), Bool(input, "response"),
                    Bool(input, "excluded"), Bool(input, "signal")));
                Assert.Equal(Bool(item, "suppressed"), fields.SuppressFields);
                Assert.Equal(Bool(item, "telemetry"), fields.IncludeTelemetry);
                Assert.Equal(Bool(item, "appearance"), fields.IncludeAppearance);
                break;
            case "sends":
                var decision = SidebandTelemetryPolicy.EvaluateSend(settings, conversation, new(Now)
                {
                    HasAddress = Bool(input, "address", true), IsSelf = Bool(input, "self"),
                    TargetIsCollector = Bool(input, "collector"), InProgress = Bool(input, "busy"),
                    IdentityKnown = Bool(input, "identity", true), HasPackedTelemetry = Bool(input, "packed", true),
                    HasReadings = Bool(input, "readings", true), OwnTimestamp = Number(input, "own_time", Now),
                    LastSuccessTimestamp = Number(input, "last_success"), AuthorizedRequest = Bool(input, "authorized"),
                    CollectorResponse = Bool(input, "response"), OwnSourceExcluded = Bool(input, "excluded"),
                    StreamTimebases = input.TryGetProperty("stream", out var stream) ? stream.EnumerateArray().Select(e => e.GetDouble()).ToArray() : null,
                    LinkAvailable = Bool(input, "link"), RatchetAvailable = Bool(input, "ratchet"), PropagationNodeAvailable = Bool(input, "node")
                });
                AssertOutput(item.GetProperty("output"), decision);
                break;
            case "requests":
                var request = SidebandTelemetryPolicy.EvaluateRequest(settings, new(Now)
                {
                    HasAddress = Bool(input, "address", true), IsSelf = Bool(input, "self"), InProgress = Bool(input, "busy"),
                    IdentityKnown = Bool(input, "identity", true), LinkAvailable = Bool(input, "link"),
                    RatchetAvailable = Bool(input, "ratchet"), PropagationNodeAvailable = Bool(input, "node"),
                    IsLiveTrack = Bool(input, "live"), IsCollectorRequest = Bool(input, "collector_request"),
                    LastReceivedTimebase = input.TryGetProperty("last_received", out var received) ? received.GetDouble() : null
                });
                var output = item.GetProperty("output");
                AssertOutput(output, request);
                if (request.Status == SidebandTelemetryAdmission.Sent)
                {
                    var commands = V.Decode(Convert.FromHexString(output.GetProperty("commands_hex").GetString()!));
                    Assert.Equal(request.Timebase, commands.Items[0].Get(1)!.Items[0].Number);
                    Assert.Equal(request.CollectorRequest, commands.Items[0].Get(1)!.Items[1].Boolean);
                }
                break;
            case "deliveries":
                var actual = SidebandTelemetryPolicy.DeliveryDisposition(settings, conversation,
                    Bool(input, "signature"), Bool(input, "commands"), Bool(input, "ignore_unknown"));
                Assert.Equal(item.GetProperty("output").GetString(), actual switch
                {
                    SidebandTelemetryDeliveryDisposition.IngestMessage => "ingest",
                    SidebandTelemetryDeliveryDisposition.HandleCommands => "commands", _ => "dropped"
                });
                break;
            case "receipts":
                var accepted = SidebandTelemetryPolicy.AcceptReceivedTelemetry(settings, conversation, Bool(input, "originator"));
                Assert.Equal(Bool(item, "accepted"), accepted);
                if (accepted)
                {
                    var save = Assert.Single(item.GetProperty("saves").EnumerateArray());
                    var kwargs = save.GetProperty("kwargs");
                    if (Bool(input, "stream"))
                    {
                        Assert.Equal(string.Concat(Enumerable.Repeat("01", 16)), kwargs.GetProperty("via").GetString());
                        Assert.Equal(Now + 10000, item.GetProperty("cursor").GetDouble());
                        Assert.NotEqual(kwargs.GetProperty("via").GetString(), save.GetProperty("source").GetString());
                    }
                    else
                    {
                        Assert.True(kwargs.TryGetProperty("source_dest", out _));
                        var physical = input.GetProperty("physical");
                        double? Stat(int index) => physical[index].ValueKind == JsonValueKind.Null ? null : physical[index].GetDouble();
                        Assert.Equal(SidebandTelemetryPolicy.UsePhysicalLinkObservation(Stat(0), Stat(1), Stat(2)),
                            kwargs.GetProperty("physical_link").EnumerateObject().Any());
                    }
                }
                break;
        }
    }

    [Fact]
    public void DefaultsBoundariesAndDeliberateSafetyLimitsAreExplicit()
    {
        var settings = new SidebandTelemetryPolicySettings();
        var defaults = Root.GetProperty("defaults");
        foreach (var (key, value) in new (string, bool)[]
        {
            ("telemetry_enabled", settings.Enabled), ("telemetry_send_to_trusted", settings.SendToTrusted),
            ("telemetry_send_to_collector", settings.SendToCollector), ("telemetry_request_from_collector", settings.RequestFromCollector),
            ("telemetry_collector_enabled", settings.CollectorEnabled), ("telemetry_send_all_to_collector", settings.SendAllToCollector),
            ("telemetry_allow_requests_from_anyone", settings.AllowRequestsFromAnyone),
            ("telemetry_allow_requests_from_trusted", settings.AllowRequestsFromTrusted),
            ("telemetry_receive_trusted_only", settings.ReceiveTrustedOnly), ("telemetry_display_trusted_only", settings.DisplayTrustedOnly),
            ("telemetry_send_appearance", settings.SendAppearance), ("telemetry_use_propagation_only", settings.UsePropagationOnly),
            ("telemetry_try_propagation_on_fail", settings.TryPropagationOnFail),
            ("telemetry_requests_only_send_latest", settings.RequestsOnlySendLatest)
        })
            Assert.Equal(defaults.GetProperty(key).GetBoolean(), value);
        Assert.Equal(SidebandTelemetryPolicySettings.DefaultSendIntervalSeconds, defaults.GetProperty("telemetry_send_interval").GetInt32());
        Assert.Equal(SidebandTelemetryPolicySettings.DefaultRequestIntervalSeconds, defaults.GetProperty("telemetry_request_interval").GetInt32());
        Assert.Equal(SidebandTelemetryPolicySettings.DefaultRequestHistorySeconds, Root.GetProperty("request_history_seconds").GetInt32());
        Assert.Throws<ArgumentOutOfRangeException>(() => SidebandTelemetryPolicy.EvaluateSend(settings, new(), new(Now)
        { StreamTimebases = new double[257] }));
        Assert.Throws<ArgumentOutOfRangeException>(() => SidebandTelemetryPolicy.EvaluateRequest(settings, new(double.NaN)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SidebandTelemetryPolicy.EvaluateFields(settings, new(), new(true, double.PositiveInfinity)));
        Assert.False(SidebandTelemetryPolicy.UsePhysicalLinkObservation(0, 0, 0));
        Assert.False(SidebandTelemetryPolicy.UsePhysicalLinkObservation(null, null, null));
        Assert.True(SidebandTelemetryPolicy.UsePhysicalLinkObservation(-90, 0, null));
        Assert.True(SidebandTelemetryPolicy.UsePhysicalLinkObservation(null, 0.1, null));
        Assert.True(SidebandTelemetryPolicy.UsePhysicalLinkObservation(null, null, 50));
        Assert.False(SidebandTelemetryPolicy.UsePhysicalLinkObservation(double.NaN, null, null));
        var maximum = SidebandTelemetryPolicy.EvaluateSend(settings, new(), new(Now)
        { StreamTimebases = Enumerable.Repeat(Now, SidebandTelemetryPolicy.MaximumStreamEntries).ToArray() });
        Assert.Equal(SidebandTelemetryAdmission.Sent, maximum.Status);
        Assert.True(maximum.IncludeStream);
        Assert.Equal(Now, maximum.Timebase);
    }

    [Fact]
    public void ActualCollectorStreamObservationsMatchSourceInclusionDecisions()
    {
        var fixture = Load("sideband-telemetry-store-geo.json");
        foreach (var item in fixture.GetProperty("streams").EnumerateArray())
        {
            var latest = item.GetProperty("latest_only").GetBoolean();
            var result = V.Decode(Convert.FromHexString(item.GetProperty("result_hex").GetString()!));
            var entries = result.Get("stream")!.Items;
            foreach (var source in new[] { 1, 2, 3, 4 })
            {
                var include = SidebandTelemetryPolicy.IncludeCollectorSource(source == 4, source == 3, latest, false);
                Assert.Equal(include, entries.Any(e => e.Items[0].GetBinary()![0] == source));
            }
            Assert.Equal(!latest, SidebandTelemetryPolicy.IncludeCollectorSource(false, false, latest, true));
        }
    }

    private static void AssertOutput(JsonElement expected, SidebandTelemetryOutboundDecision actual)
    {
        Assert.Equal(expected.GetProperty("status").GetString(), actual.PrimaryStatus);
        Assert.Equal(Bool(expected, "path_requested"), actual.RequestPath);
        if (actual.Status != SidebandTelemetryAdmission.Sent) return;
        Assert.Equal(expected.GetProperty("method").GetInt32(), (int)actual.Method);
        Assert.Equal(Bool(expected, "ticket"), actual.IncludeTicket);
        Assert.Equal(Bool(expected, "retry"), actual.TryPropagationOnFail);
        Assert.Equal(Number(expected, "timebase"), actual.Timebase);
        if (expected.TryGetProperty("own", out _))
        {
            Assert.Equal(Bool(expected, "own"), actual.IncludeOwnTelemetry);
            Assert.Equal(Bool(expected, "stream"), actual.IncludeStream);
            Assert.Equal(Bool(expected, "appearance"), actual.IncludeAppearance);
        }
    }
    private static SidebandTelemetryPolicySettings Settings(JsonElement value) => new()
    {
        Enabled = Bool(value, "enabled"), SendToTrusted = Bool(value, "send_trusted"),
        AllowRequestsFromAnyone = Bool(value, "allow_anyone"), AllowRequestsFromTrusted = Bool(value, "allow_trusted"),
        SendAppearance = Bool(value, "appearance", true), ReceiveTrustedOnly = Bool(value, "receive_trusted"),
        UsePropagationOnly = Bool(value, "propagation_only"), TryPropagationOnFail = Bool(value, "retry", true)
    };
    private static SidebandTelemetryConversationPolicy Conversation(JsonElement value) => new(
        Bool(value, "exists", true), (int)Number(value, "trust"), Bool(value, "conversation_send"), Bool(value, "conversation_requests"));
    private static bool Bool(JsonElement value, string name, bool fallback = false) =>
        value.TryGetProperty(name, out var item) ? item.GetBoolean() : fallback;
    private static double Number(JsonElement value, string name, double fallback = 0) =>
        value.TryGetProperty(name, out var item) ? item.GetDouble() : fallback;
}
