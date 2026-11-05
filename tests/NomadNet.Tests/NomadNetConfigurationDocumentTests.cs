using System.Text.Json;

namespace NomadNet.Tests;

// GenerateNomadNetConfigurationFormVectors.py executes NomadNet primary f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2
// and pinned RNS a29bfb51c468945c96a1ee22cae0114e993e2391 ConfigObj.
[Trait("Category", "Unit")]
public sealed class NomadNetConfigurationDocumentTests
{
    [Fact, Trait("TestId", "T450")]
    public void ExecutedConfigObjGrammarValuesAndErrorsMatchIncludingInterpolation()
    {
        using var fixture = Fixture();
        foreach (var item in fixture.RootElement.GetProperty("grammar").EnumerateArray())
        {
            var text = item.GetProperty("text").GetString()!;
            if (item.TryGetProperty("error", out _))
            {
                Assert.Throws<InvalidDataException>(() => NomadNetConfigDocument.Parse(text));
                continue;
            }
            var actual = NomadNetConfigDocument.Parse(text);
            Assert.Equal(text, actual.Text);
            var expected = new Dictionary<string, JsonElement>();
            Flatten(item.GetProperty("value"), "");
            Assert.Equal(expected.Keys.Order(), actual.Flatten().Keys.Order());
            foreach (var (key, value) in actual.Flatten())
            {
                if (expected[key].ValueKind == JsonValueKind.Array)
                    Assert.Equal(expected[key].EnumerateArray().Select(entry => entry.GetString()), value.List);
                else Assert.Equal(expected[key].GetString(), value.Scalar);
            }
            void Flatten(JsonElement value, string path)
            {
                foreach (var property in value.EnumerateObject())
                    if (property.Value.ValueKind == JsonValueKind.Object) Flatten(property.Value, path + property.Name + ".");
                    else expected.Add(path + property.Name, property.Value);
            }
        }
    }

    [Fact, Trait("TestId", "T451")]
    public void LosslessSectionEditsPreserveUntouchedBytesCommentsNestedValuesAndListScalarDistinction()
    {
        var text = "\uFEFF# owned\r\n[one] # section\r\n a = old # inline\r\n[[nested]]\r\nb = '''multi\r\n# literal'''\r\n[two]\r\nunknown = preserved\r\n";
        var original = NomadNetConfigDocument.Parse(text);
        var changed = original.EditSection("one", new Dictionary<string, NomadNetConfigValue?>
        {
            ["a"] = new("new,# value"),
            ["list"] = new(new[] { "only" }),
        });
        Assert.Contains(" a = \"new,# value\" # inline\r\nlist = only,\r\n[[nested]]", changed);
        Assert.EndsWith("[[nested]]\r\nb = '''multi\r\n# literal'''\r\n[two]\r\nunknown = preserved\r\n", changed);
        Assert.StartsWith("\uFEFF# owned\r\n[one] # section\r\n", changed);
        var parsed = NomadNetConfigDocument.Parse(changed);
        Assert.Equal("new,# value", parsed.Get("one", "a")!.Scalar);
        Assert.Equal(["only"], parsed.Get("one", "list")!.List);
        Assert.Equal("multi\n# literal", parsed.Flatten()["one.nested.b"].Scalar);
        var added = parsed.EditSection("new", new Dictionary<string, NomadNetConfigValue?> { ["empty"] = new(Array.Empty<string>()) });
        Assert.Equal(Array.Empty<string>(), NomadNetConfigDocument.Parse(added).Get("new", "empty")!.List);
        var deleted = NomadNetConfigDocument.Parse(added).EditSection("one", new Dictionary<string, NomadNetConfigValue?> { ["a"] = null });
        Assert.Null(NomadNetConfigDocument.Parse(deleted).Get("one", "a"));
    }

    [Fact, Trait("TestId", "T450")]
    public void ExecutedConfigObjRootAssignmentIsPreservedReportedAndCannotEnableASectionSetting()
    {
        using var fixture = Fixture();
        var observed = fixture.RootElement.GetProperty("grammar").EnumerateArray().Single(item => item.GetProperty("name").GetString() == "root-setting");
        Assert.Equal("yes", observed.GetProperty("value").GetProperty("enable_node").GetString());
        var parsed = NomadNetConfigImport.Parse(observed.GetProperty("text").GetString()!);
        Assert.False(parsed.Settings.EnableNode);
        Assert.Equal(["enable_node"], parsed.UnsupportedKeys);
        Assert.Equal("yes", NomadNetConfigDocument.Parse(observed.GetProperty("text").GetString()!).Flatten()["enable_node"].Scalar);
    }

    internal static JsonDocument Fixture() => JsonDocument.Parse(typeof(NomadNetConfigurationDocumentTests).Assembly
        .GetManifestResourceStream("NomadNet.Tests.NomadNetConfigurationFormVectors.json")!);
}
