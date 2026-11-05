using System.Text.Json;
using Sideband;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandMessageMarkupTests
{
    [Fact]
    [Trait("TestId", "T570")]
    public async Task OutgoingSelectionAndTrustedMarkupMatchExecutedPrimaryMethods()
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "sideband-message-markup.json"), TestContext.Current.CancellationToken));
        var root = document.RootElement;
        Assert.Equal("d069122af14880e43607aed0b71add4c7806fbe0", root.GetProperty("revision").GetString());
        var hashes = root.GetProperty("source_sha256");
        Assert.Equal("8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25", hashes.GetProperty("sbapp/sideband/core.py").GetString());
        Assert.Equal("5b962d9ed2ccbc066bb19865d78c77da9ecf4008900f6f0e7e9dd1eb1c4767aa", hashes.GetProperty("sbapp/main.py").GetString());
        Assert.Equal("9b2580f76310356d2e1a30b038857fbad4ee346aad9e2a25ab237515fff39928", hashes.GetProperty("sbapp/ui/messages.py").GetString());
        Assert.Equal("212ff9b6a5e9dd6f880ce086ec7e00c95d0ba1b288a2bc631eff728a6edd6c95", hashes.GetProperty("sbapp/md/md.py").GetString());
        Assert.Equal(15, root.GetProperty("renderers").GetProperty("field").GetInt32());
        Assert.Equal(SidebandMessageFormatting.MarkdownRenderer, root.GetProperty("renderers").GetProperty("markdown").GetInt64());
        Assert.Equal(SidebandMessageFormatting.BbCodeRenderer, root.GetProperty("renderers").GetProperty("bbcode").GetInt64());
        Assert.Equal(30, root.GetProperty("sending").GetArrayLength());
        foreach (var item in root.GetProperty("sending").EnumerateArray())
        {
            var policy = new SidebandMessageFormatting(item.GetProperty("markdown").GetBoolean());
            var content = item.GetProperty("input").GetString()!;
            if (!item.GetProperty("accepted").GetBoolean())
            {
                Assert.Throws<ArgumentException>(() => policy.PrepareOutgoing(content));
                continue;
            }
            var actual = policy.PrepareOutgoing(content);
            Assert.Equal(item.GetProperty("content").GetString(), actual.Content);
            Assert.Equal(item.GetProperty("renderer").ValueKind == JsonValueKind.Null ? null :
                (long?)item.GetProperty("renderer").GetInt64(), actual.Renderer);
        }
        Assert.Equal(432, root.GetProperty("rendering").GetArrayLength());
        foreach (var item in root.GetProperty("rendering").EnumerateArray())
        {
            var renderer = item.GetProperty("renderer");
            var actual = SidebandMessageMarkup.Build(item.GetProperty("content").GetString()!,
                renderer.ValueKind == JsonValueKind.Null ? null : renderer.GetInt64(),
                item.GetProperty("trusted_only").GetBoolean(), item.GetProperty("trusted").GetBoolean());
            Assert.True(item.GetProperty("markup").GetString() == actual,
                $"Primary mismatch for {item}: actual {JsonSerializer.Serialize(actual)}");
        }
    }

    [Fact]
    [Trait("TestId", "T571")]
    public async Task StrictFormattingSettingsAndRendererHistoryPersistWithoutGrantingTrustOrDroppingUnknownState()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "message-markup-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        try
        {
            var store = new SidebandStateStore(directory);
            await using var client = new SidebandClient(store);
            var peer = SidebandContact.Create("Markup peer", "00112233445566778899aabbccddeeff");
            await store.AddContactAsync(peer, token);
            var original = new SidebandChatMessage(Guid.NewGuid(), peer.DeliveryHash, true, "", "**raw**",
                DateTimeOffset.UtcNow, "Received", Renderer: 2);
            await store.AddMessageAsync(original, true, token);
            var source = Path.Combine(directory, "import.json");
            await File.WriteAllTextAsync(source, """{"compose_in_markdown":true,"trusted_markup_only":true,"unrelated":"retain"}""", token);
            Assert.Equal(["unrelated"], await store.ImportSettingsAsync(source, token));
            Assert.Equal(new(true, true), SidebandMessageFormatting.FromState(await client.LoadAsync(token)));
            var exported = Path.Combine(directory, "export.json");
            await store.ExportSettingsAsync(exported, token);
            using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(exported, token)))
            {
                Assert.True(json.RootElement.GetProperty("compose_in_markdown").GetBoolean());
                Assert.True(json.RootElement.GetProperty("trusted_markup_only").GetBoolean());
            }
            var before = await File.ReadAllBytesAsync(store.StatePath, token);
            foreach (var invalid in new[] { """{"compose_in_markdown":null}""", """{"trusted_markup_only":"true"}""",
                """{"compose_in_markdown":1}""", """{"trusted_markup_only":[]}""" })
            {
                await File.WriteAllTextAsync(source, invalid, token);
                await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportSettingsAsync(source, token));
                Assert.Equal(before, await File.ReadAllBytesAsync(store.StatePath, token));
            }
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ConfigureMessageFormattingAsync(new(), cancelled.Token));
            Assert.Equal(before, await File.ReadAllBytesAsync(store.StatePath, token));
            await client.ConfigureMessageFormattingAsync(new(false, true), token);
            var restarted = await new SidebandStateStore(directory).LoadAsync(cancellationToken: token);
            Assert.Equal(new(false, true), SidebandMessageFormatting.FromState(restarted));
            Assert.Equal(original, Assert.Single(restarted.Messages));
            Assert.False(Assert.Single(restarted.Contacts).Trusted);
            Assert.Equal("retain", restarted.ImportedSettings!["unrelated"].GetString());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
