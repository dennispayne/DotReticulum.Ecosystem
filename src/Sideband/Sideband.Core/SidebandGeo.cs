namespace Sideband;

public readonly record struct SidebandGeoCoordinate(double Latitude, double Longitude, double Altitude = 0);
public readonly record struct SidebandGeoPoint(double X, double Y, double Z);
public sealed record SidebandGeoBearing(double? AzimuthDegrees, double? ElevationDegrees);
public sealed record SidebandGeoSharedHorizon(double FirstRange, double SecondRange, double CombinedRange,
    bool WithinRange, double SurfaceDistance, double AntennaDistance);

/// <summary>Original mathematical implementation checked against primary geo.py. No map/geoid assets or location collection.</summary>
public static class SidebandGeo
{
    public const double EquatorialRadius = 6_378_137;
    public const double PolarRadius = 6_356_752.3142;
    public const double MeanEarthRadius = (1d / 3) * (2 * EquatorialRadius + PolarRadius);
    private const double Flattening = 1 - PolarRadius / EquatorialRadius;
    private const double Degrees = 180 / Math.PI;
    private const double Radians = Math.PI / 180;

    public static double RadiusAt(double latitude)
    {
        Validate(new(latitude, 0));
        var (s, c) = Math.SinCos(latitude * Radians);
        var a2 = EquatorialRadius * EquatorialRadius;
        var b2 = PolarRadius * PolarRadius;
        return Math.Sqrt((a2 * a2 * c * c + b2 * b2 * s * s) / (a2 * c * c + b2 * s * s));
    }

    public static SidebandGeoPoint ToCartesian(SidebandGeoCoordinate coordinate, bool ellipsoid = true)
    {
        Validate(coordinate);
        return Cartesian(coordinate, ellipsoid);
    }

    public static double EuclideanDistance(SidebandGeoCoordinate first, SidebandGeoCoordinate second, bool ellipsoid = true)
    {
        Validate(first); Validate(second);
        return Length(Subtract(Cartesian(second, ellipsoid), Cartesian(first, ellipsoid)));
    }

    /// <summary>Surface distance ignores altitude. Nonconvergent primary Vincenty cases return null, not a spherical substitute.</summary>
    public static double? OrthodromicDistance(SidebandGeoCoordinate first, SidebandGeoCoordinate second,
        bool ellipsoid = true, CancellationToken cancellationToken = default)
    {
        Validate(first); Validate(second);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ellipsoid)
        {
            var firstLatitude = first.Latitude * Radians;
            var secondLatitude = second.Latitude * Radians;
            return MeanEarthRadius * Acos(Math.Sin(firstLatitude) * Math.Sin(secondLatitude) +
                Math.Cos(firstLatitude) * Math.Cos(secondLatitude) * Math.Cos(second.Longitude * Radians - first.Longitude * Radians));
        }
        if (first.Latitude == second.Latitude && first.Longitude == second.Longitude) return 0;
        // The primary inverse routine perturbs a zero first latitude, including equatorial paths.
        var latitude1 = (first.Latitude == 0 ? 1e-6 : first.Latitude) * Radians;
        var latitude2 = second.Latitude * Radians;
        var reduced1 = Math.Atan((1 - Flattening) * Math.Tan(latitude1));
        var reduced2 = Math.Atan((1 - Flattening) * Math.Tan(latitude2));
        var (s1, c1) = Math.SinCos(reduced1);
        var (s2, c2) = Math.SinCos(reduced2);
        var difference = second.Longitude * Radians - first.Longitude * Radians;
        var longitude = difference;
        for (var iteration = 0; iteration < 10_000; iteration++)
        {
            if ((iteration & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
            var (sinLongitude, cosLongitude) = Math.SinCos(longitude);
            var east = c2 * sinLongitude;
            var north = c1 * s2 - s1 * c2 * cosLongitude;
            var sinSigma = Math.Sqrt(east * east + north * north);
            if (sinSigma == 0) return null;
            var cosSigma = s1 * s2 + c1 * c2 * cosLongitude;
            var sigma = Math.Atan2(sinSigma, cosSigma);
            var sinAlpha = c1 * c2 * sinLongitude / sinSigma;
            var cosAlphaSquared = 1 - sinAlpha * sinAlpha;
            if (cosAlphaSquared == 0) return null;
            var midpoint = cosSigma - 2 * s1 * s2 / cosAlphaSquared;
            var correction = Flattening / 16 * cosAlphaSquared * (4 + Flattening * (4 - 3 * cosAlphaSquared));
            var next = difference + (1 - correction) * Flattening * sinAlpha *
                (sigma + correction * sinSigma * (midpoint + correction * cosSigma * (2 * midpoint * midpoint - 1)));
            if (!double.IsFinite(next)) return null;
            if (Math.Abs(next - longitude) <= 1e-9)
            {
                var u = cosAlphaSquared * (EquatorialRadius * EquatorialRadius - PolarRadius * PolarRadius) /
                    (PolarRadius * PolarRadius);
                var a = 1 + u / 16_384 * (4096 + u * (-768 + u * (320 - 175 * u)));
                var b = u / 1024 * (256 + u * (-128 + u * (74 - 47 * u)));
                var middleSquared = midpoint * midpoint;
                var delta = b * sinSigma * (midpoint + b / 4 * cosSigma * (2 * middleSquared - 1) -
                    b / 6 * midpoint * (4 * sinSigma * sinSigma - 3) * (4 * middleSquared - 3));
                return PolarRadius * a * (sigma - delta);
            }
            longitude = next;
        }
        return null;
    }

    /// <summary>Primary rotated-globe azimuth and line-of-sight elevation, rounded to four decimal degrees.</summary>
    public static SidebandGeoBearing Bearing(SidebandGeoCoordinate observer, SidebandGeoCoordinate target, bool ellipsoid = true)
    {
        Validate(observer); Validate(target);
        var rotated = Cartesian(target with { Longitude = target.Longitude - observer.Longitude }, ellipsoid);
        var rotation = -observer.Latitude * Radians;
        if (ellipsoid) rotation = Math.Atan((1 - Flattening) * (1 - Flattening) * Math.Tan(rotation));
        var z = rotated.X * Math.Sin(rotation) + rotated.Z * Math.Cos(rotation);
        double? azimuth = null;
        if (z * z + rotated.Y * rotated.Y > 1e-6)
        {
            var angle = 90 - Math.Atan2(z, rotated.Y) * Degrees;
            if (angle < 0) angle += 360;
            if (angle > 360) angle -= 360;
            azimuth = Math.Round(angle, 4, MidpointRounding.ToEven);
        }
        var difference = Subtract(Cartesian(target, ellipsoid), Cartesian(observer, ellipsoid));
        var length = Length(difference);
        double? elevation = null;
        if (length != 0)
        {
            var latitude = observer.Latitude * Radians;
            var longitude = observer.Longitude * Radians;
            var dot = (difference.X * Math.Cos(latitude) * Math.Cos(longitude) +
                difference.Y * Math.Cos(latitude) * Math.Sin(longitude) + difference.Z * Math.Sin(latitude)) / length;
            elevation = Math.Round(90 - Acos(dot) * Degrees, 4, MidpointRounding.ToEven);
        }
        return new(azimuth, elevation);
    }

    public static double HorizonDistance(double height, bool ellipsoid = false)
    {
        HorizonArguments(height, ellipsoid);
        return Math.Sqrt((MeanEarthRadius + height) * (MeanEarthRadius + height) - MeanEarthRadius * MeanEarthRadius);
    }

    public static double HorizonAngle(double height, bool ellipsoid = false)
    {
        if (!double.IsFinite(height)) throw new ArgumentOutOfRangeException(nameof(height));
        HorizonArguments(Math.Max(0, height), ellipsoid);
        return -Acos(MeanEarthRadius / (MeanEarthRadius + Math.Max(0, height))) * Degrees;
    }

    public static double RadioHorizon(double height, bool ellipsoid = false)
    {
        var distance = HorizonDistance(height, ellipsoid);
        var r = MeanEarthRadius;
        var outer = r + height;
        return r * Acos((r * r + outer * outer - distance * distance) / (2 * r * outer));
    }

    public static SidebandGeoSharedHorizon SharedRadioHorizon(SidebandGeoCoordinate first, SidebandGeoCoordinate second)
    {
        Validate(first); Validate(second);
        var firstRange = RadioHorizon(first.Altitude);
        var secondRange = RadioHorizon(second.Altitude);
        var distance = OrthodromicDistance(first, second, ellipsoid: false)!.Value;
        return new(firstRange, secondRange, firstRange + secondRange, firstRange + secondRange > distance,
            distance, EuclideanDistance(first, second, ellipsoid: false));
    }

    private static SidebandGeoPoint Cartesian(SidebandGeoCoordinate coordinate, bool ellipsoid)
    {
        var latitude = coordinate.Latitude * Radians;
        var longitude = coordinate.Longitude * Radians;
        var radius = ellipsoid ? RadiusAt(coordinate.Latitude) : MeanEarthRadius;
        var geocentric = ellipsoid ? Math.Atan((1 - Flattening) * (1 - Flattening) * Math.Tan(latitude)) : latitude;
        return new(radius * Math.Cos(geocentric) * Math.Cos(longitude) + coordinate.Altitude * Math.Cos(latitude) * Math.Cos(longitude),
            radius * Math.Cos(geocentric) * Math.Sin(longitude) + coordinate.Altitude * Math.Cos(latitude) * Math.Sin(longitude),
            radius * Math.Sin(geocentric) + coordinate.Altitude * Math.Sin(latitude));
    }
    private static SidebandGeoPoint Subtract(SidebandGeoPoint a, SidebandGeoPoint b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static double Length(SidebandGeoPoint value) => Math.Sqrt(value.X * value.X + value.Y * value.Y + value.Z * value.Z);
    private static double Acos(double value) => Math.Acos(Math.Clamp(value, -1, 1));
    private static void Validate(SidebandGeoCoordinate coordinate)
    {
        if (!double.IsFinite(coordinate.Latitude) || coordinate.Latitude is < -90 or > 90 ||
            !double.IsFinite(coordinate.Longitude) || coordinate.Longitude is < -180 or > 180 ||
            !double.IsFinite(coordinate.Altitude) || Math.Abs(coordinate.Altitude) > 1e9)
            throw new ArgumentOutOfRangeException(nameof(coordinate), "Require finite geographic coordinates and altitude within +/- 1e9 metres.");
    }
    private static void HorizonArguments(double height, bool ellipsoid)
    {
        if (ellipsoid) throw new NotSupportedException("The primary ellipsoid horizon calculation is unimplemented.");
        if (!double.IsFinite(height) || height is < 0 or > 1e9) throw new ArgumentOutOfRangeException(nameof(height));
    }
}
