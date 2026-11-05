using System.Globalization;
using System.Text;
using GeographicLib;

namespace Sideband;

/// <summary>Owned cubic geoid lookup over an explicitly supplied GeographicLib PGM grid.</summary>
/// <remarks>
/// The immutable raster cache is limited to 32 MiB plus two interpolation border rows.
/// No grid data, path discovery or downloads are provided. Coordinates use degrees and heights use metres.
/// </remarks>
public sealed class SidebandGeoid : IDisposable
{
    private const int MaximumRasterBytes = 32 * 1024 * 1024;
    private const int MaximumHeaderBytes = 16 * 1024;
    private readonly object gate = new();
    private Geoid? geoid;

    private SidebandGeoid(Geoid geoid) => this.geoid = geoid;

    /// <summary>Loads a bounded, validated snapshot and closes the external file before returning.</summary>
    public static SidebandGeoid Open(string pgmFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pgmFile);
        using var file = new FileStream(pgmFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaximumRasterBytes + MaximumHeaderBytes)
            throw new InvalidDataException("Geoid file exceeds the 32 MiB raster and 16 KiB header limits.");

        int headerBytes = 0;
        string ReadLine()
        {
            var line = new StringBuilder();
            while (true)
            {
                if (++headerBytes > MaximumHeaderBytes || line.Length > 1024)
                    throw new InvalidDataException("Geoid header exceeds its bounded size.");
                int value = file.ReadByte();
                if (value < 0)
                    throw new InvalidDataException("Geoid header is truncated.");
                if (value == '\n')
                    return line.ToString().TrimEnd('\r');
                if ((value < 32 || value > 126) && value is not '\r' and not '\t')
                    throw new InvalidDataException("Geoid header must contain ASCII text.");
                line.Append((char)value);
            }
        }

        if (ReadLine() != "P5")
            throw new InvalidDataException("Geoid must be a binary PGM (P5).");
        double? offset = null, scale = null;
        string dimensions;
        while (true)
        {
            string line = ReadLine();
            if (!line.StartsWith('#'))
            {
                dimensions = line;
                break;
            }
            string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[1] is "Offset" or "Scale")
            {
                if (parts.Length != 3 ||
                    !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
                    !double.IsFinite(value))
                    throw new InvalidDataException("Geoid Offset/Scale metadata must be finite numbers.");
                if (parts[1] == "Offset")
                {
                    if (offset.HasValue)
                        throw new InvalidDataException("Duplicate geoid Offset metadata.");
                    offset = value;
                }
                else
                {
                    if (scale.HasValue)
                        throw new InvalidDataException("Duplicate geoid Scale metadata.");
                    scale = value;
                }
            }
        }
        string[] sizes = dimensions.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (sizes.Length != 2 ||
            !int.TryParse(sizes[0], NumberStyles.None, CultureInfo.InvariantCulture, out int width) ||
            !int.TryParse(sizes[1], NumberStyles.None, CultureInfo.InvariantCulture, out int height) ||
            width is < 4 or > 16384 || height is < 3 or > 8193 ||
            width % 2 != 0 || height % 2 != 1)
            throw new InvalidDataException("Geoid requires even width 4..16384 and odd height 3..8193.");
        long rasterBytes = checked((long)width * height * sizeof(ushort));
        if (rasterBytes > MaximumRasterBytes)
            throw new InvalidDataException("Geoid raster exceeds 32 MiB.");
        if (ReadLine() != "65535")
            throw new InvalidDataException("Geoid PGM must use 65535 levels.");
        if (!offset.HasValue || !scale.HasValue || scale.Value <= 0 ||
            Math.Abs(offset.Value) > double.MaxValue / 16 ||
            scale.Value > double.MaxValue / (16 * ushort.MaxValue))
            throw new InvalidDataException("Geoid requires finite Offset and positive, numerically bounded Scale.");
        if (file.Length - file.Position != rasterBytes)
            throw new InvalidDataException("Geoid raster length does not match the dimensions.");

        // Normalize only metadata, avoiding the dependency's 1024-character header
        // search and retaining an owned snapshot even if the source is later replaced.
        byte[] header = Encoding.ASCII.GetBytes(FormattableString.Invariant(
            $"P5\n# Offset {offset.Value:R}\n# Scale {scale.Value:R}\n{width} {height}\n65535\n"));
        using var snapshot = new MemoryStream(checked(header.Length + (int)rasterBytes));
        snapshot.Write(header);
        snapshot.SetLength(header.Length + rasterBytes);
        file.ReadExactly(snapshot.GetBuffer().AsSpan(header.Length, (int)rasterBytes));
        if (file.ReadByte() != -1)
            throw new InvalidDataException("Geoid file changed while loading.");
        snapshot.Position = 0;
        try
        {
            return new SidebandGeoid(new Geoid(snapshot, cubic: true, threadsafe: true, leaveOpen: true));
        }
        catch (GeographicException exception)
        {
            throw new InvalidDataException("Invalid GeographicLib geoid grid.", exception);
        }
    }

    /// <summary>Returns geoid height above the reference ellipsoid in metres.</summary>
    /// <remarks>Latitude is in [-90,90]; any finite longitude is normalized modulo 360.</remarks>
    public double Offset(double latitude, double longitude)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(geoid is null, this);
            if (!double.IsFinite(latitude) || latitude is < -90 or > 90)
                throw new ArgumentOutOfRangeException(nameof(latitude));
            if (!double.IsFinite(longitude))
                throw new ArgumentOutOfRangeException(nameof(longitude));
            double result = geoid.Evaluate(latitude, longitude % 360);
            if (!double.IsFinite(result))
                throw new InvalidDataException("Geoid interpolation produced a nonfinite height.");
            return result;
        }
    }

    /// <summary>Subtracts the geoid offset, without display-only clamping of negative heights.</summary>
    public double AltitudeAboveMeanSeaLevel(double ellipsoidAltitude, double latitude, double longitude)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(geoid is null, this);
            if (!double.IsFinite(ellipsoidAltitude))
                throw new ArgumentOutOfRangeException(nameof(ellipsoidAltitude));
            double result = ellipsoidAltitude - Offset(latitude, longitude);
            if (!double.IsFinite(result))
                throw new ArgumentOutOfRangeException(nameof(ellipsoidAltitude), "Corrected altitude overflowed.");
            return result;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            geoid?.Dispose();
            geoid = null;
        }
    }
}
