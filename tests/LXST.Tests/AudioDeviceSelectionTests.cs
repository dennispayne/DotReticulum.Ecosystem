using System.Text.Json;
using System.Text.RegularExpressions;

namespace LXST.Tests;

[Trait("Category", "Unit")]
public sealed class AudioDeviceSelectionTests
{
    [Fact]
    public void IdentifierSubstringFuzzyAndMonitorSelectionMatchActualPrimary()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxst-device-selection.json")));
        var devices = fixture.RootElement.GetProperty("devices").EnumerateArray()
            .Select(device => new AudioDeviceInfo(device.GetProperty("id").GetString()!, device.GetProperty("name").GetString()!, false)).ToArray();
        foreach (var vector in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            AudioDeviceInfo Resolve() => AudioDeviceSelection.Resolve(vector.GetProperty("selector").GetString()!,
                devices, vector.GetProperty("loopback").GetBoolean());
            if (vector.TryGetProperty("id", out var id)) Assert.Equal(id.GetString(), Resolve().Id);
            else if (vector.GetProperty("error").GetString() == "IndexError") Assert.Throws<KeyNotFoundException>(Resolve);
            else Assert.Throws<RegexParseException>(Resolve);
        }
    }
}
