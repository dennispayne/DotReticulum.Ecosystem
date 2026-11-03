using System.Text;
using System.Text.Json;
using NomadNet;

namespace NomadNet.Tests;

[Trait("Category", "Unit")]
public sealed class MicronPartialTests
{
    [Fact]
    public void DescriptorsCompositionAndSchedulingMatchExecutedPrimary()
    {
        using var fixture = JsonDocument.Parse(typeof(MicronPartialTests).Assembly
            .GetManifestResourceStream("NomadNet.Tests.NomadNetVectors.json")!);
        foreach (var descriptor in fixture.RootElement.GetProperty("partials").EnumerateArray())
        {
            var page = MicronText.Render(Encoding.UTF8.GetBytes("`{" + descriptor.GetProperty("source").GetString()));
            if (!descriptor.GetProperty("valid").GetBoolean()) { Assert.Empty(page.Partials); continue; }
            var partial = Assert.Single(page.Partials);
            Assert.Equal(descriptor.GetProperty("id").GetString(), partial.Id);
            var refresh = descriptor.GetProperty("refresh");
            Assert.Equal(refresh.ValueKind == JsonValueKind.Null ? null : refresh.GetDouble(), partial.RefreshSeconds);
            Assert.Equal(string.Join('|', descriptor.GetProperty("fields").EnumerateArray().Select(value => value.GetString())),
                partial.Fields);
        }
        var root = MicronText.Render("`<root`outer>\n`{/page/child.mu`2`*|pid=child}"u8);
        var child = MicronText.Render("`<child`inner>\nChild text"u8);
        var composed = NomadNetMicronComposition.Compose(root, new Dictionary<NomadNetPagePartial, NomadNetRenderedPage>
        { [root.Partials[0]] = child });
        var submission = composed.Submission(root.Partials[0].Fields)!;
        var expected = fixture.RootElement.GetProperty("composedSubmission");
        Assert.Equal(expected.EnumerateObject().Count(), submission.Count);
        foreach (var field in expected.EnumerateObject()) Assert.Equal(field.Value.GetString(), submission[field.Name]);
        var now = DateTimeOffset.FromUnixTimeSeconds(1000);
        var decisions = new[]
        {
            ("initial", (DateTimeOffset?)null, (double?)null),
            ("due", now.AddSeconds(-10), 2d), ("fresh", now.AddSeconds(100), 2d),
            ("static", now.AddSeconds(-10), null),
        };
        Assert.Equal(fixture.RootElement.GetProperty("partialScheduled").EnumerateArray().Select(value => value.GetString()),
            decisions.Where(item => NomadNetMicronBrowser.IsDue(now, item.Item2, item.Item3)).Select(item => item.Item1).Order());
        Assert.False(NomadNetMicronBrowser.IsDue(now, now.AddSeconds(-2), 2));
        Assert.False(NomadNetMicronBrowser.IsDue(now, now, double.MaxValue));
    }

    [Fact]
    public void CompositionRenumbersOnlyActualControlsAndRetainsRootEditsAndFolds()
    {
        var root = MicronText.Render("`<root`old>\n`[root`:/page/root.mu]\n`->Folded\n`{/page/child.mu}\n>Visible\nliteral [1] [field 1 x: unchanged]\n`[last`:/page/last.mu]"u8);
        root.SetField(1, "edited");
        var child = MicronText.Render("`<child`inside>\n`[child`:/page/next.mu`*]\n`(photo`/photo.webp)\n>`:child-anchor Child"u8);
        var children = new Dictionary<NomadNetPagePartial, NomadNetRenderedPage> { [root.Partials[0]] = child };
        var composed = NomadNetMicronComposition.Compose(root, children);
        Assert.DoesNotContain("inside", composed.DisplayText);
        Assert.Contains("literal [1] [field 1 x: unchanged]", composed.DisplayText);
        Assert.Contains("[3] last", composed.DisplayText);
        composed.ToggleFold(1);
        Assert.Contains("[field 2 child: inside]", composed.DisplayText);
        Assert.Contains("[2] child", composed.DisplayText);
        Assert.Contains("Child", composed.AtAnchor("child-anchor"));
        composed.SetField(2, "edited-child");
        var again = NomadNetMicronComposition.Compose(root, children);
        Assert.Contains("edited-child", again.DisplayText);
        Assert.Contains("[field 1 root: edited]", again.DisplayText);
        Assert.False(again.Folds[0].Collapsed);
        Assert.Equal("edited-child", again.Submission("*")!["field_child"]);
        Assert.Single(again.Media);
    }

    [Fact]
    public async Task InitialManualAndTimedRequestsUseCurrentCombinedFieldsAndIsolateObservers()
    {
        var root = MicronText.Render("`<root`outer>\n`{/page/child.mu`2`*|pid=child}"u8);
        var observed = new List<Dictionary<object, object?>?>();
        await using var browser = new NomadNetMicronBrowser(root, (_, data, _) =>
        {
            observed.Add(data);
            return Task.FromResult("`<child`inner>\nChild text"u8.ToArray());
        });
        browser.Changed += _ => throw new InvalidOperationException("test observer");
        var notifications = 0;
        browser.Changed += _ => notifications++;
        var errors = new List<string>();
        browser.Log = errors.Add;
        await browser.RefreshDueAsync();
        Assert.Equal(1, notifications);
        Assert.Equal("outer", observed[0]!["field_root"]);
        Assert.False(observed[0]!.ContainsKey("field_child"));
        browser.SetField(1, "changed");
        browser.SetField(2, "child-edit");
        await browser.RefreshDueAsync();
        Assert.Single(observed);
        await browser.RefreshAsync(1);
        Assert.Equal("changed", observed[1]!["field_root"]);
        Assert.Equal("child-edit", observed[1]!["field_child"]);
        Assert.Equal("inner", browser.Current.Fields[1].Value);
        Assert.Contains(errors, error => error.Contains("observer failed", StringComparison.Ordinal));
        Assert.Equal(2, notifications);
    }

    [Fact]
    public async Task AFailedOrOversizedPartialDoesNotDisableOtherPartsOrRecovery()
    {
        var root = MicronText.Render("`{/page/fail.mu}\n`{/page/okay.mu}"u8);
        var failed = true;
        await using var browser = new NomadNetMicronBrowser(root, (partial, _, _) =>
        {
            if (partial.Target.Contains("fail", StringComparison.Ordinal) && failed)
                throw new IOException("peer stopped");
            return Task.FromResult("RECOVERED"u8.ToArray());
        });
        await browser.RefreshDueAsync();
        Assert.Contains("peer stopped", browser.Current.DisplayText);
        Assert.Contains("RECOVERED", browser.Current.DisplayText);
        failed = false;
        await browser.RefreshAsync(1);
        Assert.DoesNotContain("peer stopped", browser.Current.DisplayText);
        var full = MicronText.Render(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("`<x`y>", 128)) + "\n`{/page/extra.mu}"));
        await using var bounded = new NomadNetMicronBrowser(full, (_, _, _) => Task.FromResult("`<extra`value>"u8.ToArray()));
        await bounded.RefreshDueAsync();
        Assert.Contains("aggregate text/control bounds", bounded.Current.DisplayText);
        Assert.Equal(128, bounded.Current.Fields.Count);
    }

    [Fact]
    public async Task DisposalCancelsAndJoinsPendingWorkAndNoObserverIsRequired()
    {
        var root = MicronText.Render("`{/page/wait.mu}"u8);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var browser = new NomadNetMicronBrowser(root, async (_, _, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return [];
        });
        browser.Start();
        browser.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await browser.DisposeAsync();
        await browser.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(browser.Start);
        Assert.Throws<ObjectDisposedException>(() => browser.SetField(1, "value"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => browser.RefreshAsync(1));
        await using var noObserver = new NomadNetMicronBrowser(root, (_, _, _) => Task.FromResult("complete"u8.ToArray()));
        await noObserver.RefreshDueAsync();
        Assert.Equal("complete", noObserver.Current.DisplayText);
    }
}
