using System.Text.Json;

namespace Sideband.Tests;

// GenerateSidebandTelemetryAppearanceVectors.py executes primary get_message_fields at
// d069122af14880e43607aed0b71add4c7806fbe0; core.py SHA-256
// 8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25.
[Trait("Category", "Integration")]
public sealed class SidebandTelemetryAppearanceTests
{
    public static IEnumerable<object[]> Observations() =>
        Enumerable.Range(0, 261).Select(index => new object[] { index });

    [Theory]
    [MemberData(nameof(Observations))]
    public async Task ImportedRgbaMatchesActualPrimaryAppearanceBytes(int index)
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = JsonDocument.Parse(await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-telemetry-appearance.json"), token));
        Assert.Equal(261, fixture.RootElement.GetProperty("vectors").GetArrayLength());
        var vector = fixture.RootElement.GetProperty("vectors")[index];
        var root = Path.GetFullPath(Path.Combine("artifacts", "builds", "sideband-telemetry-contracts",
            "appearance-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var input = Path.Combine(root, "import.json");
            await File.WriteAllTextAsync(input,
                $$"""{"telemetry_fg":{{vector.GetProperty("rgba").GetRawText()}},"telemetry_bg":[1,1,1,1]}""", token);
            var store = new SidebandStateStore(Path.Combine(root, "profile"));
            Assert.Empty(await store.ImportSettingsAsync(input, token));
            var configuration = (await store.LoadAsync(cancellationToken: token)).Telemetry;
            Assert.Equal(vector.GetProperty("foreground").GetString(), configuration.Appearance.Foreground);
            Assert.Equal(vector.GetProperty("background").GetString(), configuration.Appearance.Background);
            Assert.False(configuration.Enabled);
        }
        finally { Directory.Delete(root, true); }
    }
}
