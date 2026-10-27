using System.Text.Json;
using DotReticulum.Core;

namespace DotLXMF.Tests;

[Trait("Category", "Unit")]
public class LxmSchedulingTests
{
    // GenerateLxmSchedulingVectors.py executes LXMRouter.py at e8ba02483217af43be284a72f1c059c730b7d047.
    [Fact]
    public void AttemptSchedules_MatchActualRouterMediumAndAirtimePolicies()
    {
        using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxmf-scheduling-vectors.json")));
        var now = vectors.RootElement.GetProperty("fixed_now").GetDouble();
        foreach (var row in vectors.RootElement.GetProperty("cases").EnumerateArray())
        {
            var pathRequest = row.GetProperty("path_request").GetBoolean();
            var hasPath = row.GetProperty("has_path").GetBoolean();
            var rtt = pathRequest ? row.GetProperty("medium_timeout").GetDouble() : row.GetProperty("round_trip").GetDouble();
            var result = LxmDeliveryTiming.Schedule(now, rtt, pathRequest,
                row.GetProperty("delivery_attempts").GetInt32(), vectors.RootElement.GetProperty("jitter_fraction").GetDouble());
            Assert.Equal(row.GetProperty("next_attempt").GetDouble(), result.NextAttempt, 6);
            Assert.Equal(row.GetProperty("attempt_limit").GetInt32(), result.AttemptLimit);
            var timeout = row.GetProperty("receipt_timeout");
            if (timeout.ValueKind == JsonValueKind.Null) Assert.Null(result.ReceiptTimeout);
            else Assert.Equal(timeout.GetDouble(), result.ReceiptTimeout!.Value, 6);
            Assert.Equal(row.GetProperty("due_at_deadline").GetBoolean(),
                LxmDeliveryTiming.AttemptDue(result.NextAttempt, result.NextAttempt, pathRequest, hasPath));
            Assert.Equal(row.GetProperty("due_after_deadline").GetBoolean(),
                LxmDeliveryTiming.AttemptDue(result.NextAttempt + 0.001, result.NextAttempt, pathRequest, hasPath));
        }
    }

    [Fact]
    public void SlowPathOrdering_MatchesActualRouterQueueAndInflightChecks()
    {
        using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxmf-scheduling-vectors.json")));
        using var source = Identity.Generate();
        var destination = Enumerable.Repeat((byte)'d', 16).ToArray();
        foreach (var row in vectors.RootElement.GetProperty("ordering").EnumerateArray())
        {
            var message = LxMessage.Create(destination, source, "");
            message.State = LxmState.Outbound;
            var other = LxMessage.Create(row.GetProperty("same_target").GetBoolean()
                ? destination : Enumerable.Repeat((byte)'x', 16).ToArray(), source, "",
                method: (LxmMethod)row.GetProperty("other_method").GetInt32());
            other.Method = other.DesiredMethod;
            other.State = (LxmState)row.GetProperty("other_state").GetInt32();
            other.SentOnSlowPath = row.GetProperty("sent_on_slow_path").GetBoolean();
            var pending = row.GetProperty("ahead").GetBoolean() ? new[] { other, message } : new[] { message, other };
            Assert.Equal(row.GetProperty("held").GetBoolean(),
                LxmDeliveryTiming.Held(message, destination, pending, entry =>
                    entry.Method == LxmMethod.Propagated ? Enumerable.Repeat((byte)'p', 16).ToArray() : entry.DestinationHash));
        }
    }
}
