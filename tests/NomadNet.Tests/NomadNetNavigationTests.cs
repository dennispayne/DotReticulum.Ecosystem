using System.Text.Json;

namespace NomadNet.Tests;

// Actual primary Browser.write_history/back/forward at f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2.
// GenerateNomadNetNavigationVectors.py records source SHA-256 and the retrieval-observer boundary.
[Trait("Category", "Unit")]
public sealed class NomadNetNavigationTests
{
    private const string Hash = "11111111111111111111111111111111";

    [Fact]
    public void NavigationMatchesExecutedPrimaryIncludingFormReplayAndBranchTruncation()
    {
        using var fixture = JsonDocument.Parse(typeof(NomadNetNavigationTests).Assembly
            .GetManifestResourceStream("NomadNet.Tests.NomadNetNavigationVectors.json")!);
        Assert.Equal("f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2", fixture.RootElement.GetProperty("commit").GetString());
        var history = new NomadNetNavigationHistory();
        foreach (var step in fixture.RootElement.GetProperty("steps").EnumerateArray())
        {
            NomadNetNavigationEntry? requested = null;
            switch (step.GetProperty("action").GetString())
            {
                case "visit":
                    var fields = step.GetProperty("data");
                    history.Visit(Hash, step.GetProperty("path").GetString()!, fields.ValueKind == JsonValueKind.Null ? null :
                        fields.EnumerateObject().ToDictionary(item => (object)item.Name, item => (object?)item.Value.GetString()));
                    break;
                case "back": requested = history.Back(); break;
                case "forward": requested = history.Forward(); break;
            }
            Assert.Equal(step.GetProperty("position").GetInt32(), history.Position);
            Assert.Equal(step.GetProperty("paths").EnumerateArray().Select(item => item.GetString()), history.Snapshot().Select(item => item.Path));
            var request = step.GetProperty("request");
            if (request.ValueKind == JsonValueKind.Null) Assert.Null(requested);
            else
            {
                Assert.NotNull(requested);
                Assert.Equal(request.GetProperty("url").GetString(), requested.DestinationHash + ":" + requested.Path);
                var data = request.GetProperty("data");
                if (data.ValueKind == JsonValueKind.Null) Assert.Null(requested.RequestData);
                else Assert.Equal(data.GetProperty("field_user").GetString(), requested.RequestData!["field_user"]);
            }
        }
    }

    [Fact]
    public void FormsAndReturnedSnapshotsCannotMutateRetainedHistory()
    {
        var history = new NomadNetNavigationHistory();
        var data = new Dictionary<object, object?> { ["field_user"] = "original" };
        history.Visit(Hash, "/page/one.mu", data);
        data["field_user"] = "changed";
        history.Snapshot()[0].RequestData!["field_user"] = "changed again";
        history.Visit(Hash, "/page/two.mu");
        var back = Assert.IsType<NomadNetNavigationEntry>(history.Back());
        Assert.Equal("original", back.RequestData!["field_user"]);
        back.RequestData["field_user"] = "returned mutation";
        history.Forward();
        Assert.Equal("original", history.Back()!.RequestData!["field_user"]);
    }

    [Fact]
    public void EmptyHistoryAndBoundedOldestEntryEvictionKeepCursorConsistent()
    {
        var history = new NomadNetNavigationHistory();
        Assert.Null(history.Back());
        Assert.Null(history.Forward());
        Assert.Equal(0, history.Position);
        for (var index = 0; index < 100; index++) history.Visit(Hash, $"/page/{index}.mu");
        Assert.Equal(NomadNetNavigationHistory.MaximumEntries, history.Count);
        Assert.Equal(history.Count, history.Position);
        Assert.Equal("/page/36.mu", history.Snapshot()[0].Path);
        while (history.Back() is not null) { }
        Assert.Equal(1, history.Position);
        history.Visit(Hash, "/page/branch.mu");
        Assert.Equal(2, history.Count);
        Assert.Null(history.Forward());
    }

    [Fact]
    public void InvalidOrOversizedVisitsDoNotDiscardForwardEntries()
    {
        var history = new NomadNetNavigationHistory();
        history.Visit(Hash, "/page/one.mu");
        history.Visit(Hash, "/page/two.mu");
        history.Back();
        Assert.Throws<ArgumentException>(() => history.Visit(Hash, "/page/x.mu", new Dictionary<object, object?> { [1] = "not a string" }));
        Assert.Throws<ArgumentException>(() => history.Visit(Hash, "/page/x.mu", new Dictionary<object, object?> { ["nested"] = new object() }));
        Assert.Throws<InvalidDataException>(() => history.Visit(Hash, "/page/x.mu", new Dictionary<object, object?> { ["name"] = new string('x', 65536) }));
        Assert.Throws<ArgumentException>(() => history.Visit("invalid", "/page/x.mu"));
        Assert.Equal(1, history.Position);
        Assert.Equal("/page/two.mu", history.Forward()!.Path);
    }
}
