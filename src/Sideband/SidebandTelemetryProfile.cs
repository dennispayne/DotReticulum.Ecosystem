using System.Text;
using DotReticulum.Core;
using V = Sideband.SidebandTelemetryValue;

namespace Sideband;

internal static class SidebandTelemetryProfile
{
    internal static SidebandContact Conversation(SidebandContact contact, object? packed)
    {
        if (packed is null) return contact;
        if (packed is not byte[] data) throw new InvalidDataException("Conversation data must be MessagePack binary.");
        var value = V.Decode(data);
        if (value.IsNil) return contact;
        if (value.Kind != SidebandTelemetryValueKind.Map) throw new InvalidDataException("Conversation data must be a map.");
        var result = contact with
        {
            SendTelemetry = value.Get("telemetry") is { } send && (send.Boolean == true || send.Number == 1),
            AllowTelemetryRequests = value.Get("allow_requests") is { } requests && (requests.Boolean == true || requests.Number == 1)
        };
        if (value.Get("appearance") is { Kind: SidebandTelemetryValueKind.Array } appearance &&
            appearance.Items.Count == 3 && appearance.Items[0].Text is { } icon &&
            appearance.Items[1].GetBinary() is { Length: 3 } foreground &&
            appearance.Items[2].GetBinary() is { Length: 3 } background)
        {
            var candidate = new SidebandTelemetryAppearance(icon, Convert.ToHexStringLower(foreground), Convert.ToHexStringLower(background));
            SidebandTelemetryConfiguration.ValidateAppearance(candidate);
            result = result with { Appearance = candidate };
        }
        return result;
    }

    internal static async Task ImportReportsAsync(ProfileSqlite database, string directory, CancellationToken token)
    {
        if (database.Rows("SELECT name FROM sqlite_master WHERE type='table' AND name='telemetry'", 1).Count == 0) return;
        var rows = database.Rows("SELECT dest_context,ts,data FROM telemetry LIMIT 4097", 4096);
        if (rows.Count == 0) return;
        await using var store = await SidebandTelemetryStore.OpenAsync(Path.Combine(directory, "telemetry"), cancellationToken: token).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            if (row[0] is not byte[] { Length: 16 } source || row[2] is not byte[] packed)
                throw new InvalidDataException("Invalid imported telemetry record.");
            var report = SidebandTelemetryCodec.Decode(packed);
            var time = row[1] switch { long n => (double)n, double n when double.IsFinite(n) => n, _ => double.NaN };
            if (report.TimestampUtcSeconds != time) throw new InvalidDataException("Imported telemetry timestamp mismatch.");
            if (await store.AppendAsync(Convert.ToHexStringLower(source), packed, now, token).ConfigureAwait(false) == SidebandTelemetryAppendResult.InvalidTimestamp)
                throw new InvalidDataException("Future/invalid imported telemetry timestamp.");
        }
    }

    internal static IReadOnlyList<SidebandTelemetryPeerState> ReadPeers(ProfileSqlite database)
    {
        if (database.Rows("SELECT name FROM sqlite_master WHERE type='table' AND name='persistent'", 1).Count == 0) return [];
        var glob = "telemetry." + new string('?', 32);
        var rows = database.Rows(
            $"SELECT property, CASE WHEN typeof(value)='blob' AND length(value) BETWEEN 1 AND 9 THEN value END FROM persistent " +
            $"WHERE CAST(property AS TEXT) GLOB '{glob}.timebase' OR CAST(property AS TEXT) GLOB '{glob}.last_send_success_timebase' " +
            $"OR CAST(property AS TEXT) GLOB '{glob}.last_request_success_timebase' " +
            $"OR CAST(property AS TEXT) GLOB '{glob}.last_request_attempt' LIMIT 4001", 4000);
        var peers = new Dictionary<string, SidebandTelemetryPeerState>(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
        foreach (var row in rows)
        {
            if (row[0] is not byte[] key || row[1] is not byte[] data) throw new InvalidDataException("Invalid telemetry cursor.");
            var parts = Encoding.UTF8.GetString(key).Split('.');
            if (parts.Length != 3 || !SidebandContact.TryNormalizeDeliveryHash(parts[1], out var hash))
                throw new InvalidDataException("Invalid telemetry cursor source.");
            var scalar = V.Decode(data);
            if (scalar.IsNil) continue;
            if (scalar.Number is not { } value || !SidebandTelemetryCodec.IsValidTimebase(value, now))
                throw new InvalidDataException("Invalid/future telemetry cursor.");
            var peer = peers.GetValueOrDefault(hash) ?? new(hash);
            peers[hash] = parts[2] switch
            {
                "timebase" => peer with { LastReceivedTimebase = value },
                "last_send_success_timebase" => peer with { LastSendSuccessTimebase = value },
                "last_request_success_timebase" => peer with { LastRequestSuccessTimebase = value },
                _ => peer with { LastRequestAttemptUtcSeconds = value }
            };
        }
        return peers.Values.ToArray();
    }
}
