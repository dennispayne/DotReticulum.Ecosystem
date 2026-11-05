using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sideband;

namespace Sideband.Tests;

[Trait("Category", "Unit")]
public sealed class SidebandGeoidInputTests
{
    [Fact]
    public void T680_MissingPathIsNeverAnImplicitDefault()
    {
        Assert.Throws<ArgumentNullException>(() => SidebandGeoid.Open(null!));
        Assert.Throws<ArgumentException>(() => SidebandGeoid.Open(""));
        Assert.Throws<ArgumentException>(() => SidebandGeoid.Open(" \t"));
    }
}

[Trait("Category", "Integration")]
public sealed class SidebandGeoidTests : IDisposable
{
    private readonly string directory = Path.GetFullPath(Path.Combine(
        "artifacts", "tests", "sideband-geoid", "cases", Guid.NewGuid().ToString("N")));

    public static bool ExternalDataDisabled =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_GEOID"));

    [Fact]
    public void T681_SnapshotHasNoRetainedFileHandleAndPreservesNegativeAltitude()
    {
        string path = WriteGrid();
        using var geoid = SidebandGeoid.Open(path);
        File.Delete(path);
        double offset = geoid.Offset(12.5, 23.75);
        Assert.True(double.IsFinite(offset));
        Assert.Equal(-123.5 - offset, geoid.AltitudeAboveMeanSeaLevel(-123.5, 12.5, 23.75));
        Assert.Equal(offset, geoid.Offset(12.5, 23.75 + 360));
        Assert.Equal(offset, geoid.Offset(12.5, 23.75 - 720));
        geoid.Dispose();
        geoid.Dispose();
        Assert.Throws<ObjectDisposedException>(() => geoid.Offset(0, 0));
        Assert.Throws<ObjectDisposedException>(() => geoid.AltitudeAboveMeanSeaLevel(0, 0, 0));
    }

    [Theory]
    [InlineData("P2\n# Offset 0\n# Scale 1\n4 3\n65535\n", 24)]
    [InlineData("P5\n# Scale 1\n4 3\n65535\n", 24)]
    [InlineData("P5\n# Offset 0\n4 3\n65535\n", 24)]
    [InlineData("P5\n# Offset NaN\n# Scale 1\n4 3\n65535\n", 24)]
    [InlineData("P5\n# Offset 0\n# Scale Infinity\n4 3\n65535\n", 24)]
    [InlineData("P5\n# Offset 0\n# Scale 0\n4 3\n65535\n", 24)]
    [InlineData("P5\n# Offset 0\n# Scale -1\n4 3\n65535\n", 24)]
    [InlineData("P5\n# Offset 0\n# Scale 1e308\n4 3\n65535\n", 24)]
    [InlineData("P5\n# Offset 0\n# Offset 1\n# Scale 1\n4 3\n65535\n", 24)]
    [InlineData("P5\n# Offset 0\n# Scale 1\n# Scale 2\n4 3\n65535\n", 24)]
    [InlineData("P5\n# Offset 0\n# Scale 1\n3 3\n65535\n", 18)]
    [InlineData("P5\n# Offset 0\n# Scale 1\n4 4\n65535\n", 32)]
    [InlineData("P5\n# Offset 0\n# Scale 1\n2 3\n65535\n", 12)]
    [InlineData("P5\n# Offset 0\n# Scale 1\n2147483647 2147483647\n65535\n", 0)]
    [InlineData("P5\n# Offset 0\n# Scale 1\n16384 8193\n65535\n", 0)]
    [InlineData("P5\n# Offset 0\n# Scale 1\n-4 3\n65535\n", 24)]
    [InlineData("P5\n# Offset 0\n# Scale 1\n4 3\n255\n", 24)]
    [InlineData("P5\n# Offset 0\n# Scale 1\n4 3\n65535\n", 23)]
    [InlineData("P5\n# Offset 0\n# Scale 1\n4 3\n65535\n", 25)]
    [InlineData("P5\n# Offset 0\n", 0)]
    public void T682_HostileHeadersAndInconsistentLengthsFailBeforeLibraryAllocation(string header, int bytes)
    {
        string path = WriteBytes([.. Encoding.ASCII.GetBytes(header), .. new byte[bytes]]);
        Assert.Throws<InvalidDataException>(() => SidebandGeoid.Open(path));
        File.Delete(path);
    }

    [Fact]
    public void T683_HeaderAndFileSizeAreBounded()
    {
        string path = WriteBytes(Encoding.ASCII.GetBytes("P5\n#" + new string('x', 1026) + "\n"));
        Assert.Throws<InvalidDataException>(() => SidebandGeoid.Open(path));
        File.WriteAllText(path, "P5\n" + string.Concat(Enumerable.Repeat("# comment\n", 2000)));
        Assert.Throws<InvalidDataException>(() => SidebandGeoid.Open(path));
        using (var stream = File.OpenWrite(path))
            stream.SetLength(32 * 1024 * 1024 + 16 * 1024 + 1);
        Assert.Throws<InvalidDataException>(() => SidebandGeoid.Open(path));
        Assert.Throws<FileNotFoundException>(() => SidebandGeoid.Open(Path.Combine(directory, "absent.pgm")));
    }

    [Fact]
    public void T684_MetadataIsNormalizedWithoutTrustingLibraryHeaderSearch()
    {
        string comments = "# ignored 65535\n" + string.Concat(Enumerable.Repeat("# ignored\n", 120));
        string path = WriteGrid("P5\r\n" + comments + "# Offset -100\r\n# Scale 0.003\r\n8 5\r\n65535\r\n");
        using var geoid = SidebandGeoid.Open(path);
        Assert.True(double.IsFinite(geoid.Offset(0, 0)));
    }

    [Fact]
    public void T685_InvalidCoordinatesAndAltitudesAreExplicit()
    {
        using var geoid = SidebandGeoid.Open(WriteGrid());
        foreach (double value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -90.00001, 90.00001 })
            Assert.Throws<ArgumentOutOfRangeException>(() => geoid.Offset(value, 0));
        foreach (double value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => geoid.Offset(0, value));
            Assert.Throws<ArgumentOutOfRangeException>(() => geoid.AltitudeAboveMeanSeaLevel(value, 0, 0));
        }
        Assert.True(double.IsFinite(geoid.Offset(90, double.MaxValue)));
        Assert.True(double.IsFinite(geoid.Offset(-90, -double.MaxValue)));
        using var extreme = SidebandGeoid.Open(WriteGrid("P5\n# Offset -1e307\n# Scale 1\n8 5\n65535\n"));
        Assert.Throws<ArgumentOutOfRangeException>(() => extreme.AltitudeAboveMeanSeaLevel(double.MaxValue, 0, 0));
    }

    [Fact]
    public async Task T686_ConcurrentLookupsAndDisposalAreSerialized()
    {
        using var geoid = SidebandGeoid.Open(WriteGrid());
        double[] expected = Enumerable.Range(0, 181).Select(i => geoid.Offset(i - 90, i)).ToArray();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 181; i++)
                Assert.Equal(expected[i], geoid.Offset(i - 90, i));
        }, TestContext.Current.CancellationToken)));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 181; i++)
            {
                try { Assert.True(double.IsFinite(geoid.Offset(i - 90, i))); }
                catch (ObjectDisposedException) { }
            }
        }, TestContext.Current.CancellationToken)).Append(Task.Run(geoid.Dispose, TestContext.Current.CancellationToken)));
        Assert.Throws<ObjectDisposedException>(() => geoid.Offset(0, 0));
    }

    // Generated by actual Sideband 2.2.0 d069122af14880e43607aed0b71add4c7806fbe0
    // geo.GeoidHeight and altitude_to_aamsl; see GenerateGeoidVectors.py and fixture hashes.
    // 1e-8 metres is an absolute floating-point evaluation tolerance, not grid/model accuracy.
    [Fact(SkipWhen = nameof(ExternalDataDisabled),
        Skip = "Set DOTRETICULUM_SIDEBAND_GEOID to the external pinned EGM2008-5 grid file; no grid is bundled.")]
    public void T687_ActualPrimaryCubicAndAltitudeObservationsMatchWithinTenNanometres()
    {
        string path = Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_GEOID")!;
        using var document = ReadVectors();
        using (var file = File.OpenRead(path))
            Assert.Equal(document.RootElement.GetProperty("grid_sha256").GetString(),
                Convert.ToHexStringLower(SHA256.HashData(file)));
        using var geoid = SidebandGeoid.Open(path);
        double tolerance = document.RootElement.GetProperty("tolerance_metres").GetDouble();
        int compared = 0, cubicDiffersFromBilinear = 0;
        foreach (var row in document.RootElement.GetProperty("observations").EnumerateArray())
        {
            if (row.GetProperty("latitude").ValueKind != JsonValueKind.Number ||
                row.GetProperty("longitude").ValueKind != JsonValueKind.Number ||
                !row.GetProperty("cubic").TryGetProperty("value", out var expected))
                continue;
            double latitude = row.GetProperty("latitude").GetDouble(), longitude = row.GetProperty("longitude").GetDouble();
            if (Math.Abs(latitude) > 90 || longitude < -180 || longitude > 360)
                continue; // Outside the primary's coherent single-wrap range; independently covered below.
            Assert.InRange(Math.Abs(geoid.Offset(latitude, longitude) - expected.GetDouble()), 0, tolerance);
            Assert.InRange(Math.Abs(geoid.AltitudeAboveMeanSeaLevel(row.GetProperty("ellipsoid_altitude").GetDouble(), latitude, longitude)
                - row.GetProperty("aamsl").GetProperty("value").GetDouble()), 0, tolerance);
            if (Math.Abs(row.GetProperty("bilinear").GetProperty("value").GetDouble() - expected.GetDouble()) > tolerance)
                cubicDiffersFromBilinear++;
            compared++;
        }
        Assert.Equal(78, compared);
        Assert.True(cubicDiffersFromBilinear >= 60);
    }

    [Fact]
    public void T688_ReferenceBoundaryFailuresAreRecordedNotRelabelledAsParity()
    {
        using var document = ReadVectors();
        var rows = document.RootElement.GetProperty("observations").EnumerateArray().ToArray();
        foreach (double latitude in new[] { -90d, 90d, -89.99, 89.99 })
        {
            var row = rows.Single(row => row.GetProperty("latitude").ValueKind == JsonValueKind.Number &&
                row.GetProperty("latitude").GetDouble() == latitude);
            Assert.Equal("TypeError", row.GetProperty("cubic").GetProperty("error").GetString());
            Assert.Equal("TypeError", row.GetProperty("aamsl").GetProperty("error").GetString());
        }
        Assert.Equal("d069122af14880e43607aed0b71add4c7806fbe0",
            document.RootElement.GetProperty("sideband_revision").GetString());
        var cacheProbe = document.RootElement.GetProperty("cached_failure_probe").EnumerateArray().ToArray();
        Assert.Equal("TypeError", cacheProbe[1].GetProperty("error").GetString());
        Assert.Equal(cacheProbe[0].GetProperty("value").GetDouble(), cacheProbe[2].GetProperty("value").GetDouble());
    }

    [Fact]
    public void T689_PolesDatelineAndArbitraryWrapRemainFiniteAfterFailures()
    {
        using var geoid = SidebandGeoid.Open(WriteGrid());
        foreach (double latitude in new[] { -90d, -89.99, 0, 89.99, 90 })
        {
            foreach (double longitude in new[] { -1080d, -720, -360, 0, 360, 720, 1080 })
                Assert.Equal(geoid.Offset(latitude, 0), geoid.Offset(latitude, longitude));
            Assert.Equal(geoid.Offset(latitude, -180), geoid.Offset(latitude, 180));
        }
        double before = geoid.Offset(0, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => geoid.Offset(91, 1));
        Assert.Equal(before, geoid.Offset(0, 1));
    }

    private static JsonDocument ReadVectors() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-geoid.json")));

    private string WriteGrid(string? header = null)
    {
        byte[] raster = new byte[8 * 5 * 2];
        for (int i = 0; i < 40; i++)
            BinaryPrimitives.WriteUInt16BigEndian(raster.AsSpan(i * 2), (ushort)(30000 + i * i));
        return WriteBytes([.. Encoding.ASCII.GetBytes(header ?? "P5\n# Offset -100\n# Scale 0.003\n8 5\n65535\n"), .. raster]);
    }

    private string WriteBytes(byte[] bytes)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "owned.pgm");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
