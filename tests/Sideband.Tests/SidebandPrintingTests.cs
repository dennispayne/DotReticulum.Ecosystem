using System.Text.Json;

namespace Sideband.Tests;

/// <summary>Actual primary d069122 gen_print_qr/default/settings callback observations; see GeneratePrintingVectors.py.</summary>
[Trait("Category", "Unit")]
public sealed class SidebandPrintingTests
{
    [Fact]
    [Trait("TestId", "T1000")]
    public void ActualPrimaryDefaultOptionsAndArgvAreRetainedWithoutItsUnsafePathSplitting()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "sideband-printing.json")));
        var root = fixture.RootElement;
        Assert.Equal("d069122af14880e43607aed0b71add4c7806fbe0", root.GetProperty("revision").GetString());
        Assert.Equal("9b2580f76310356d2e1a30b038857fbad4ee346aad9e2a25ab237515fff39928",
            root.GetProperty("source_sha256").GetProperty("sbapp/ui/messages.py").GetString());
        Assert.All(root.GetProperty("defaults").EnumerateArray(), value => Assert.Equal("lp", value.GetString()));
        Assert.Equal("lp", new SidebandPrintingConfiguration().Command);
        Assert.False(new SidebandPrintingConfiguration().Enabled);
        foreach (var item in root.GetProperty("cases").EnumerateArray().Where(item =>
                     item.GetProperty("name").GetString() is "default" or "quoted-options" or "literal-metacharacters"))
        {
            var actual = item.GetProperty("calls")[0].EnumerateArray().Select(value => value.GetString()!).ToArray();
            Assert.Equal(actual[..^1], SidebandPrintingConfiguration.CommandArguments(item.GetProperty("command").GetString()!));
            Assert.Equal("as_qr", item.GetProperty("events")[0].GetString());
            var events = item.GetProperty("events");
            Assert.Equal("dismiss", events[events.GetArrayLength() - 1].GetString());
        }
        var settings = root.GetProperty("settings").EnumerateArray().ToArray();
        Assert.Equal("lp", settings[0].GetProperty("saved").GetString());
        Assert.Equal("printer --flag", settings[1].GetProperty("saved").GetString());
        Assert.Equal("", settings[2].GetProperty("saved").GetString());
        Assert.Equal(0, settings[3].GetProperty("save_count").GetInt32());
        var nonzero = root.GetProperty("cases").EnumerateArray().Single(item =>
            item.GetProperty("name").GetString() == "nonzero-primary-silent");
        Assert.Empty(nonzero.GetProperty("errors").EnumerateArray());
        var android = root.GetProperty("cases").EnumerateArray().Single(item =>
            item.GetProperty("name").GetString() == "android-dismiss-only");
        Assert.Empty(android.GetProperty("calls").EnumerateArray());
    }

    [Theory]
    [Trait("TestId", "T1001")]
    [InlineData(true, false, "Paper", "lxm://value", false)]
    [InlineData(false, true, "Paper", "lxm://value", false)]
    [InlineData(false, false, "Direct", "lxm://value", false)]
    [InlineData(false, false, "Paper", null, false)]
    [InlineData(false, false, "Paper", "lxm://value", true)]
    public void PrintReadinessRequiresOriginalSavedOutgoingPaper(bool incoming, bool imported, string method, string? uri, bool expected)
    {
        var message = new SidebandChatMessage(Guid.Empty, "00112233445566778899aabbccddeeff",
            incoming, "", "Public synthetic paper", DateTimeOffset.UnixEpoch, "Paper",
            DeliveryMethod: method, PaperUri: uri, ImportedPaperEnvelope: imported);
        Assert.Equal(expected, SidebandPrintingConfiguration.CanPrint(message));
    }
}
