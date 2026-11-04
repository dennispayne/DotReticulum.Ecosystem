using System.Text;
using System.Text.Json;
using Sideband;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandFixedLocationTests
{
    [Fact]
    public async Task ActualPrimaryFixedLocationsMatchWithExplicitMalformedAndNonfiniteSafetyDifferences()
    {
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-objects.json"), TestContext.Current.CancellationToken));
        Assert.Equal("d069122af14880e43607aed0b71add4c7806fbe0", fixture.RootElement.GetProperty("revision").GetString());
        var cases = fixture.RootElement.GetProperty("fixed_locations").EnumerateArray().ToArray();
        Assert.Equal(11, cases.Length);
        foreach (var observation in cases)
        {
            var name = observation.GetProperty("name").GetString();
            var text = observation.GetProperty("text").GetString()!;
            if (name is "wrong-columns" or "bad-hash" or "bad-number" or "outside-range" or "nonfinite")
            {
                Assert.Throws<InvalidDataException>(() => SidebandFixedLocations.Parse(text));
                if (name == "nonfinite")
                    Assert.Equal("nan", observation.GetProperty("locations")[0].GetProperty("latitude").GetString());
                continue;
            }
            var actual = SidebandFixedLocations.Parse(text);
            var expected = observation.GetProperty("locations").EnumerateArray().ToArray();
            Assert.Equal(expected.Length, actual.Count);
            for (var index = 0; index < expected.Length; index++)
            {
                Assert.Equal(expected[index].GetProperty("address").GetString(), actual[index].DeliveryHash);
                Assert.Equal(expected[index].GetProperty("latitude").GetDouble(), actual[index].Latitude);
                Assert.Equal(expected[index].GetProperty("longitude").GetDouble(), actual[index].Longitude);
            }
        }
    }

    [Fact]
    public async Task ManualImportIsAtomicBoundedDetachedAndCancellableWithoutEnablingHardware()
    {
        var root = Path.Combine(Path.GetTempPath(), "sideband-fixed-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        const string peer = "00112233445566778899aabbccddeeff";
        try
        {
            var store = new SidebandStateStore(root);
            await store.LoadAsync(cancellationToken: token);
            var path = Path.Combine(root, "synthetic-fixed");
            await File.WriteAllTextAsync(path, $"{peer},0,0\n", token);
            await using (var client = new SidebandClient(store))
            {
                await client.ImportFixedLocationsAsync(path, token);
                var location = Assert.Single((await client.LoadAsync(token)).MapFixedLocations!);
                Assert.Equal(new(peer, 0, 0), location);
                await File.WriteAllTextAsync(path, $"{peer},1,2\ninvalid,3,4\n", token);
                await Assert.ThrowsAsync<InvalidDataException>(() => client.ImportFixedLocationsAsync(path, token));
                Assert.Equal(location, Assert.Single((await client.LoadAsync(token)).MapFixedLocations!));
                using var stop = new CancellationTokenSource();
                await stop.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    client.ImportFixedLocationsAsync(Path.Combine(root, "not-opened"), stop.Token));
                await File.WriteAllBytesAsync(path, [0xff], token);
                await Assert.ThrowsAsync<DecoderFallbackException>(() => client.ImportFixedLocationsAsync(path, token));
                await using (var oversized = File.OpenWrite(path)) oversized.SetLength(SidebandFixedLocations.MaximumFileBytes + 1);
                await Assert.ThrowsAsync<InvalidDataException>(() => client.ImportFixedLocationsAsync(path, token));
            }
            var reloaded = await new SidebandStateStore(root).LoadAsync(cancellationToken: token);
            Assert.Equal(new(peer, 0, 0), Assert.Single(reloaded.MapFixedLocations!));
            Assert.Empty(reloaded.Contacts);
            Assert.Empty(reloaded.Messages);
            Assert.False(reloaded.Telemetry.Enabled);
            var mutable = new[] { new SidebandFixedLocation(peer, 1, 2) };
            await store.ConfigureFixedLocationsAsync(mutable, token);
            mutable[0] = new(peer, 3, 4);
            Assert.Equal(1, Assert.Single((await store.LoadAsync(cancellationToken: token)).MapFixedLocations!).Latitude);
            await using var final = new SidebandClient(new(root));
            await final.ClearFixedLocationsAsync(token);
            Assert.Empty((await final.LoadAsync(token)).MapFixedLocations!);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void CountAndTextLimitsRejectWithoutPartialResults()
    {
        var text = string.Join('\n', Enumerable.Range(0, 256).Select(index => $"{index:x32},1,2"));
        Assert.Equal(256, SidebandFixedLocations.Parse(text).Count);
        Assert.Throws<InvalidDataException>(() => SidebandFixedLocations.Parse(text + "\n" + $"{256:x32},1,2"));
        Assert.Throws<InvalidDataException>(() => SidebandFixedLocations.Parse(new string('#', SidebandFixedLocations.MaximumFileBytes + 1)));
        Assert.Throws<EncoderFallbackException>(() => SidebandFixedLocations.Parse("\ud800"));
        Assert.Empty(SidebandFixedLocations.Parse("#comment\n\n"));
    }
}
