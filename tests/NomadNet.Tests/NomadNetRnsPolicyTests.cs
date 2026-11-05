using System.Text.Json;
using DotReticulum.Applications;
using DotReticulum.Applications.Rnsd;

namespace NomadNet.Tests;

// GenerateNomadNetRnsPolicyVectors.py executes primary ConfigSectionView from
// f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2; canonical source hashes are in the fixture.
[Trait("Category", "Unit")]
public sealed class NomadNetRnsPolicyTests
{
    [Fact, Trait("TestId", "T510")]
    public void ExecutedSelectedRnsFormsDefaultsSaveReloadCancelAndResetMatchOperationalPolicies()
    {
        using var fixture = Fixture();
        var root = fixture.RootElement;
        var form = new NomadNetConfigurationForm("reticulum", root.GetProperty("initial").GetString()!);
        foreach (var value in root.GetProperty("defaults").EnumerateObject())
            Assert.Equal(value.Value.GetString(), form.Get(value.Name).Scalar);
        Assert.True(root.GetProperty("cancelUnchanged").GetBoolean());
        Assert.True(root.GetProperty("primaryConfigUnchanged").GetBoolean());
        foreach (var key in new[] { "respond_to_probes", "use_implicit_proof" })
        {
            Assert.Equal("runtime", Assert.Single(form.Fields, field => field.Key == key).Disposition);
            form.Set(key, new(root.GetProperty("staged").GetProperty(key).GetString()!));
        }
        var parsed = RnsdConfigParser.Parse(form.LastText);
        Assert.Equal(root.GetProperty("saved").GetProperty("respond_to_probes").GetBoolean(), parsed.RespondToProbes);
        Assert.Equal(root.GetProperty("saved").GetProperty("use_implicit_proof").GetBoolean(), parsed.UseImplicitProof);
        var reloaded = new NomadNetConfigurationForm("reticulum", form.LastText);
        foreach (var value in root.GetProperty("reloaded").EnumerateObject())
            Assert.Equal(value.Value.GetString(), reloaded.Get(value.Name).Scalar);
        Assert.Throws<ArgumentException>(() => reloaded.Reset(false));
        foreach (var value in root.GetProperty("resetCancelled").EnumerateObject())
            Assert.Equal(value.Value.GetString(), reloaded.Get(value.Name).Scalar);
        var reset = RnsdConfigParser.Parse(reloaded.Reset(true));
        Assert.False(reset.RespondToProbes);
        Assert.True(reset.UseImplicitProof);
        Assert.True(root.GetProperty("resetUnsaved").GetBoolean());
    }

    [Fact, Trait("TestId", "T511")]
    public void MalformedFlagsAndIntentionalPanicSafetyRefusalLeaveStagedValuesHealthy()
    {
        var form = new NomadNetConfigurationForm("reticulum", "[reticulum]\nshare_instance=no\n[interfaces]\n");
        var before = form.LastText;
        foreach (var key in new[] { "respond_to_probes", "use_implicit_proof" })
            foreach (var invalid in new[] { "maybe", "1", "False", "yes\n" })
                Assert.Throws<InvalidDataException>(() => form.Set(key, new(invalid)));
        Assert.Equal(before, form.LastText);
        using var fixture = Fixture();
        Assert.True(fixture.RootElement.GetProperty("primaryAcceptsPanicTrue").GetBoolean());
        var refused = Assert.Throws<NotSupportedException>(() => form.Set("panic_on_interface_error", new("yes")));
        Assert.Contains("fixed safety ownership", refused.Message);
        Assert.Equal("safety-refused", Assert.Single(form.Fields, field => field.Key == "panic_on_interface_error").Disposition);
        Assert.Equal(before, form.LastText);
        Assert.Contains("use_implicit_proof = no", form.Set("use_implicit_proof", new("no")));
    }

    internal static JsonDocument Fixture() => JsonDocument.Parse(typeof(NomadNetRnsPolicyTests).Assembly
        .GetManifestResourceStream("NomadNet.Tests.NomadNetRnsPolicyVectors.json")!);
}

[Trait("Category", "Integration")]
public sealed class NomadNetRnsPolicyStorageTests
{
    [Fact, Trait("TestId", "T512")]
    public async Task SelectedFormsActivateActualRuntimeOwnerOnlyAfterRestartAndKeepNativeIdentityCommentsAndInterfaces()
    {
        using var work = new Workspace();
        await work.Settings.SaveAsync(work.Profile);
        var native = await File.ReadAllBytesAsync(work.SettingsPath);
        using var editor = work.Editor();
        await editor.OpenConfigurationSectionAsync("reticulum");
        editor.SetConfigurationField("enable_transport", new("yes"));
        editor.SetConfigurationField("respond_to_probes", new("yes"));
        editor.SetConfigurationField("use_implicit_proof", new("no"));
        await Assert.ThrowsAsync<ArgumentException>(() => editor.SaveConfigurationSectionAsync(false));
        Assert.Equal(work.Initial, await File.ReadAllTextAsync(work.Selected));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => editor.SaveConfigurationSectionAsync(true, cancelled.Token));
        await editor.SaveConfigurationSectionAsync(true);
        var saved = await File.ReadAllTextAsync(work.Selected);
        Assert.EndsWith(work.Interfaces, saved);
        Assert.Contains("share_instance=no # unchanged", saved);
        string identity;
        await using (var running = ReticulumNode.Open(work.Rns))
        {
            await running.StartAsync();
            Assert.True(running.Config.RespondToProbes);
            Assert.False(running.Config.UseImplicitProof);
            Assert.IsType<byte[]>(running.GetStatistics()["probe_responder"]);
            identity = Convert.ToHexStringLower(running.Router.TransportId);
            editor.SetConfigurationField("respond_to_probes", new("no"));
            editor.SetConfigurationField("use_implicit_proof", new("yes"));
            await editor.SaveConfigurationSectionAsync(true);
            Assert.True(running.Config.RespondToProbes);
            Assert.False(running.Config.UseImplicitProof);
            Assert.IsType<byte[]>(running.GetStatistics()["probe_responder"]);
        }
        await using (var restarted = ReticulumNode.Open(work.Rns))
        {
            await restarted.StartAsync();
            Assert.False(restarted.Config.RespondToProbes);
            Assert.True(restarted.Config.UseImplicitProof);
            Assert.Null(restarted.GetStatistics()["probe_responder"]);
            Assert.Equal(identity, Convert.ToHexStringLower(restarted.Router.TransportId));
        }
        Assert.Equal(native, await File.ReadAllBytesAsync(work.SettingsPath));
        Assert.False(File.Exists(Path.Combine(work.Profile, "config")));
        Assert.EndsWith(work.Interfaces, await File.ReadAllTextAsync(work.Selected));
    }

    [Fact, Trait("TestId", "T513")]
    public async Task InvalidRawFlagsFixedPanicOwnershipAndExternalConflictRefuseWithoutOverwriteAndRecover()
    {
        using var work = new Workspace();
        await work.Settings.SaveAsync(work.Profile);
        using (var editor = work.Editor())
        {
            await editor.OpenConfigurationSectionAsync("reticulum");
            foreach (var key in new[] { "respond_to_probes", "use_implicit_proof" })
            {
                editor.Buffer.Set(work.Initial.Replace("[reticulum]", "[reticulum]\n" + key + "=maybe", StringComparison.Ordinal));
                await Assert.ThrowsAsync<FormatException>(() => editor.SaveAsync());
                Assert.Equal(work.Initial, await File.ReadAllTextAsync(work.Selected));
            }
            foreach (var value in new[] { "yes", "no" })
            {
                editor.Buffer.Set(work.Initial.Replace("[reticulum]", "[reticulum]\npanic_on_interface_error=" + value, StringComparison.Ordinal));
                var refusal = await Assert.ThrowsAsync<NotSupportedException>(() => editor.SaveAsync());
                Assert.Contains("child-peer failure is isolated", refusal.Message);
                Assert.Equal(work.Initial, await File.ReadAllTextAsync(work.Selected));
            }
        }
        using (var editor = work.Editor())
        {
            await editor.OpenConfigurationSectionAsync("reticulum");
            editor.SetConfigurationField("respond_to_probes", new("yes"));
            await File.AppendAllTextAsync(work.Selected, "# external change\n");
            await Assert.ThrowsAsync<IOException>(() => editor.SaveConfigurationSectionAsync(true));
            Assert.Equal(work.Initial + "# external change\n", await File.ReadAllTextAsync(work.Selected));
        }
        using (var recovered = work.Editor())
        {
            await recovered.OpenConfigurationSectionAsync("reticulum");
            recovered.SetConfigurationField("use_implicit_proof", new("no"));
            await recovered.SaveConfigurationSectionAsync(true);
        }
        Assert.False(RnsdConfigParser.Parse(await File.ReadAllTextAsync(work.Selected)).UseImplicitProof);
        Assert.Empty(Directory.GetFiles(work.Root, ".editor-*", SearchOption.AllDirectories));
    }

    private sealed class Workspace : IDisposable
    {
        public string Root { get; } = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomad-rns-policy", "managed-" + Guid.NewGuid().ToString("N")));
        public NomadNetSettings Settings { get; } = new() { NodePageRefreshIntervalMinutes = 0, NodeFileRefreshIntervalMinutes = 0 };
        public string Profile => Path.Combine(Root, "profile");
        public string Rns => Path.Combine(Root, "selected-rns");
        public string Selected => Path.Combine(Rns, "config");
        public string SettingsPath => Path.Combine(Profile, "settings.json");
        public string Initial { get; }
        public string Interfaces { get; }
        public Workspace()
        {
            using var fixture = NomadNetRnsPolicyTests.Fixture();
            Initial = fixture.RootElement.GetProperty("initial").GetString()!;
            Interfaces = Initial[Initial.IndexOf("[interfaces]", StringComparison.Ordinal)..];
            Directory.CreateDirectory(Rns);
            File.WriteAllText(Selected, Initial);
        }
        public NomadNetEditor Editor() => new(Profile, Settings, Selected);
        public void Dispose() => Directory.Delete(Root, true);
    }
}
