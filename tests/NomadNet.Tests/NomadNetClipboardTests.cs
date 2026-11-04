using System.Text.Json;

namespace NomadNet.Tests;

// GenerateNomadNetWorkflowVectors.py executes primary 1.4.4 f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2.
// The fixture records source SHA256, captured OSC52 output and actual Browser.current_url observations.
[Trait("Category", "Unit")]
public sealed class NomadNetClipboardTests
{
    [Fact]
    [Trait("TestId", "T364")]
    public void ClipboardAndBrowserUrlsMatchActualReference()
    {
        using var fixture = JsonDocument.Parse(typeof(NomadNetClipboardTests).Assembly
            .GetManifestResourceStream("NomadNet.Tests.NomadNetWorkflowVectors.json")!);
        var root = fixture.RootElement;
        Assert.Equal("f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2", root.GetProperty("revision").GetString());
        Assert.Equal("031cb6872ec33eb6b58be24770c1daa77026a0a7b77fbd790db10c31bc2e6bb3",
            root.GetProperty("sources").GetProperty("nomadnet/ui/textui/Helpers.py").GetString());
        Assert.Equal("cbce04302ec092ae6bb3cc6ce9f423139b1339b9e903f14f12cf8e6ebe736729",
            root.GetProperty("sources").GetProperty("nomadnet/ui/textui/Browser.py").GetString());
        foreach (var row in root.GetProperty("clipboard").EnumerateArray())
        {
            var text = row.TryGetProperty("inputUtf16", out var units)
                ? new string(units.EnumerateArray().Select(value => (char)value.GetInt32()).ToArray())
                : row.GetProperty("input").GetString();
            using var output = new StringWriter();
            var errors = new List<string>();
            Assert.Equal(row.GetProperty("accepted").GetBoolean(), NomadNetClipboard.TryCopy(text, output, errors.Add, enabled: true));
            Assert.Equal(row.GetProperty("output").GetString(), output.ToString());
            Assert.Equal(row.GetProperty("errorLevels").GetArrayLength(), errors.Count);
        }
        foreach (var row in root.GetProperty("urls").EnumerateArray())
        {
            var fields = row.GetProperty("fields").ValueKind == JsonValueKind.Null ? null :
                row.GetProperty("fields").EnumerateObject().ToDictionary(property => (object)property.Name,
                    property => (object?)property.Value.GetString());
            Assert.Equal(row.GetProperty("url").GetString(), NomadNetClipboard.BrowserUrl(
                row.GetProperty("hash").GetString(), row.GetProperty("path").GetString(), fields));
        }
    }

    [Fact]
    [Trait("TestId", "T365")]
    public void DisabledBoundsAndWriterFailuresNeverReportSuccessOrEmitRawControls()
    {
        using var output = new StringWriter();
        var errors = new List<string>();
        Assert.False(NomadNetClipboard.TryCopy("secret synthetic text", output, errors.Add));
        Assert.Empty(output.ToString());
        Assert.Single(errors);
        Assert.False(NomadNetClipboard.TryCopy(new string('x', NomadNetClipboard.MaximumBytes + 1), output, errors.Add, true));
        Assert.False(NomadNetClipboard.TryCopy(new string('\u03bb', NomadNetClipboard.MaximumBytes / 2 + 1), output, errors.Add, true));
        Assert.Empty(output.ToString());
        Assert.False(NomadNetClipboard.TryCopy("synthetic", new FailingWriter(), errors.Add, true));
        Assert.Equal(4, errors.Count);
        Assert.False(NomadNetClipboard.TryCopy("synthetic", new FailingFlushWriter(), errors.Add, true));
        Assert.True(NomadNetClipboard.TryCopy(new string('x', NomadNetClipboard.MaximumBytes), output, errors.Add, true));
        Assert.EndsWith("\a", output.ToString());
        Assert.DoesNotContain("synthetic", string.Join('\n', errors), StringComparison.OrdinalIgnoreCase);
        var hash = new string('0', 32);
        Assert.Throws<InvalidDataException>(() => NomadNetClipboard.BrowserUrl(hash, new string('x', NomadNetClipboard.MaximumBytes)));
        Assert.Throws<ArgumentException>(() => NomadNetClipboard.BrowserUrl(hash, "", new Dictionary<object, object?> { ["var_number"] = 1 }));
        Assert.Throws<InvalidDataException>(() => NomadNetClipboard.BrowserUrl(hash, "",
            Enumerable.Range(0, 1025).ToDictionary(index => (object)("var_" + index), _ => (object?)"")));
    }

    [Fact]
    [Trait("TestId", "T366")]
    public void PrimaryConfigImportsClipboardOptInWithoutChangingDefault()
    {
        Assert.False(new NomadNetSettings().ClipboardCopy);
        var result = NomadNetConfigImport.Parse("[textui]\nclipboard_copy = yes\n");
        Assert.True(result.Settings.ClipboardCopy);
        Assert.Empty(result.UnsupportedKeys);
        Assert.False(NomadNetConfigImport.Parse("[textui]\nclipboard_copy = no\n").Settings.ClipboardCopy);
    }

    private sealed class FailingWriter : StringWriter
    {
        public override void Write(string? value) => throw new IOException("Terminal unavailable.");
    }

    private sealed class FailingFlushWriter : StringWriter
    {
        public override void Flush() => throw new IOException("Terminal unavailable.");
    }
}
