using System.Text.Json;
using V = Sideband.SidebandTelemetryValue;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandMessageTelemetryStorageTests
{
    private const string Peer = "00112233445566778899aabbccddeeff";

    [Fact]
    [Trait("TestId", "T833")]
    public async Task ImmutableSelectedSnapshotSurvivesNewerSourceReportDeletionAndRestart()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "message-telemetry-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        try
        {
            var old = Fixture("battery");
            var newer = Fixture("information");
            var store = new SidebandStateStore(root);
            await store.AddMessageAsync(new(Guid.NewGuid(), Peer, true, "", "First message",
                DateTimeOffset.UtcNow, "Received", TelemetryHex: old), cancellationToken: token);
            await using (var telemetry = await SidebandTelemetryStore.OpenAsync(Path.Combine(root, "telemetry"), cancellationToken: token))
                await telemetry.AppendAsync(Peer, Convert.FromHexString(newer), 1700000000, token);
            await using var reopened = new SidebandClient(new(root));
            Assert.NotEmpty(await reopened.GetTelemetryAsync(new(Peer), token));
            var selected = Assert.Single((await reopened.LoadAsync(token)).Messages);
            Assert.Equal(old, selected.TelemetryHex);
            Assert.NotEqual(SidebandMessageTelemetry.CopyText(newer), SidebandMessageTelemetry.CopyText(selected.TelemetryHex!));
            await reopened.ClearTelemetryAsync(Peer, token);
            Assert.Empty(await reopened.GetTelemetryAsync(new(Peer), token));
            Assert.Equal(old, Assert.Single((await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).Messages).TelemetryHex);
            var before = await File.ReadAllBytesAsync(store.StatePath, token);
            await Assert.ThrowsAsync<FormatException>(() => store.AddMessageAsync(selected with
                { Id = Guid.NewGuid(), TelemetryHex = "90" }, cancellationToken: token));
            Assert.Equal(before, await File.ReadAllBytesAsync(store.StatePath, token));
            Assert.False(reopened.IsConnected);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    internal static string Fixture(string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "sideband-message-telemetry.json")));
        return document.RootElement.GetProperty("cases").EnumerateArray().Single(item => item.GetProperty("name").GetString() == name)
            .GetProperty("wire_hex").GetString()!;
    }
}

public sealed partial class SidebandProfileImportTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("override")]
    [InlineData("malformed")]
    [InlineData("unknown")]
    [InlineData("link-ui-rounding")]
    [InlineData("link-zero")]
    [InlineData("link-replacement")]
    [Trait("TestId", "T834")]
    public async Task MessageTelemetryFieldAndPrimaryNormalizedExtrasKeepPrecedenceAndRestartWithoutSourceChanges(string shape)
    {
        using var profile = new Profile();
        var field = SidebandMessageTelemetryStorageTests.Fixture("battery");
        var normalized = SidebandMessageTelemetryStorageTests.Fixture("information");
        string? copied = null;
        byte[]? extras = shape switch
        {
            "none" => null,
            "override" => V.Fields(("packed_telemetry", V.From(Convert.FromHexString(normalized)))).Encode(),
            "malformed" => [0x90],
            "unknown" => V.Fields(("unknown", V.From("inert"))).Encode(),
            _ when shape.StartsWith("link-", StringComparison.Ordinal) => LinkExtras(),
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
        AddMessage(profile, "valid", telemetry: Convert.FromHexString(field), extra: extras);
        var before = profile.Fingerprint();
        var result = await SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, TestContext.Current.CancellationToken);
        Assert.Equal(before, profile.Fingerprint());
        await using var reopened = new SidebandClient(new(profile.Target));
        var selected = Assert.Single((await reopened.LoadAsync(TestContext.Current.CancellationToken)).Messages);
        if (copied is null) Assert.Equal(shape == "override" ? normalized : field, selected.TelemetryHex);
        else Assert.Equal(copied, SidebandMessageTelemetry.CopyText(selected.TelemetryHex!));
        Assert.Equal("Test content", selected.Content);
        Assert.True(Assert.Single((await reopened.LoadAsync(TestContext.Current.CancellationToken)).Contacts).Trusted);
        if (shape == "malformed") Assert.Contains(result.Notices, text => text.Contains("telemetry extras", StringComparison.Ordinal));
        Assert.False(reopened.IsConnected);

        byte[] LinkExtras()
        {
            using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
                "Fixtures", "sideband-message-telemetry.json")));
            var item = fixture.RootElement.GetProperty("cases").EnumerateArray().Single(item => item.GetProperty("name").GetString() == shape);
            field = item.GetProperty("wire_hex").GetString()!;
            copied = Assert.Single(item.GetProperty("copied").EnumerateArray()).GetString();
            var raw = item.GetProperty("raw_link");
            var link = raw.EnumerateObject().Any() ? raw : item.GetProperty("extra_link");
            return V.Map(link.EnumerateObject().Select(pair => KeyValuePair.Create(
                V.From(pair.Name == "quality" ? "q" : pair.Name), V.From(pair.Value.GetDouble()))).ToArray()).Encode();
        }
    }
}
