using V = Sideband.SidebandTelemetryValue;
using S = Sideband.SidebandTelemetrySensorId;

namespace Sideband;

/// <summary>Local observation enrichment, not authentication of a reported origin or a stream source.</summary>
public static class SidebandTelemetryObservations
{
    /// <summary>
    /// Sender must be the verified LXMF sender. Forwarded metadata describes claimed original reception;
    /// only via is locally established. Caller owns signature, privacy and source-timestamp admission.
    /// </summary>
    public static SidebandTelemetryReport Apply(SidebandTelemetryReport report, string receiverDeliveryHash,
        string senderDeliveryHash, bool forwarded, SidebandTelemetryLocation? ownLocation = null,
        double? rssi = null, double? snr = null, double? quality = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!SidebandContact.TryNormalizeDeliveryHash(receiverDeliveryHash, out var receiver))
            throw new ArgumentException("Receiver must be a delivery hash.", nameof(receiverDeliveryHash));
        if (!SidebandContact.TryNormalizeDeliveryHash(senderDeliveryHash, out var sender))
            throw new ArgumentException("Sender must be a delivery hash.", nameof(senderDeliveryHash));
        if (report.TimestampValue?.Number is null) throw new FormatException("A numeric source timestamp is required.");
        var readings = report.Sensors.ToDictionary(sensor => sensor.Id);
        var previous = report.Get(S.Received);
        var by = V.Nil;
        var geodesic = V.Nil;
        var euclidean = V.Nil;
        if (forwarded && previous?.Data is { IsNil: false } data)
        {
            by = data.Get("by") ?? V.Nil;
            geodesic = data.Get("distance")?.Get("geodesic") ?? V.Nil;
            euclidean = data.Get("distance")?.Get("euclidian") ?? V.Nil;
        }
        if (!forwarded)
        {
            by = V.From(Convert.FromHexString(receiver));
            if (ownLocation is not null && report.Get(S.Location)?.Location is { } location)
            {
                var first = new SidebandGeoCoordinate(ownLocation.Latitude, ownLocation.Longitude, ownLocation.Altitude);
                var second = new SidebandGeoCoordinate(location.Latitude, location.Longitude, location.Altitude);
                euclidean = V.From(SidebandGeo.EuclideanDistance(first, second));
                if (SidebandGeo.OrthodromicDistance(first, second) is { } distance) geodesic = V.From(distance);
            }
            if (rssi is { } r && !double.IsFinite(r) || snr is { } s && !double.IsFinite(s) ||
                quality is { } q && !double.IsFinite(q))
                throw new ArgumentOutOfRangeException(nameof(rssi), "Physical observations must be finite.");
            if (rssi is not null and not 0 || snr is not null and not 0 || quality is not null and not 0)
                readings[(byte)S.PhysicalLink] = SidebandTelemetryCodec.FromWireValue((byte)S.PhysicalLink,
                    V.Array(Number(rssi), Number(snr), Number(quality)));
        }
        readings[(byte)S.Received] = SidebandTelemetryCodec.FromWireValue((byte)S.Received,
            V.Array(by, V.From(Convert.FromHexString(sender)), geodesic, euclidean));
        return SidebandTelemetryCodec.FromSensors(readings.Values.ToArray());
    }

    private static V Number(double? value) => value is { } number ? V.From(number) : V.Nil;
}
