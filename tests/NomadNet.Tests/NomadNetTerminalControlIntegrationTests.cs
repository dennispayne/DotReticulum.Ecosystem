using System.Diagnostics;
using System.Text.Json;

namespace NomadNet.Tests;

[Trait("Category", "Integration")]
public sealed class NomadNetTerminalControlIntegrationTests
{
    [Fact, Trait("TestId", "T551")]
    public async Task T551NativeSettingsPersistRestartWithoutPrimarySourceAndCancellationRetainsFile()
    {
        var root = Root();
        try
        {
            var settings = new NomadNetSettings { IntroTime = .125, IntroText = "Synthetic title", Theme = "light",
                ColorMode = "88", Glyphs = "plain", MouseEnabled = false, HideGuide = true, SanitizeNames = false,
                AnimationInterval = 2, EnableUtilities = true };
            await settings.SaveAsync(root);
            Assert.Equal(JsonSerializer.Serialize(settings), JsonSerializer.Serialize(await NomadNetSettings.OpenAsync(root)));
            var original = await File.ReadAllBytesAsync(Path.Combine(root, "settings.json"));
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => (settings with { IntroText = "not saved" }).SaveAsync(root, cancel.Token));
            Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(root, "settings.json")));
            Assert.Empty(Directory.GetFiles(root, "*.new"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact, Trait("TestId", "T552")]
    public async Task T552PrimarySourceRestartOverridesNativeAndDisableReconcilesAllControls()
    {
        var root = Root();
        try
        {
            var settings = new NomadNetSettings { UsePrimaryConfiguration = true, NodePageRefreshIntervalMinutes = 0, NodeFileRefreshIntervalMinutes = 0 };
            await settings.SaveAsync(root);
            var primary = settings with { IntroTime = 0, IntroText = "primary", Theme = "light", ColorMode = "monochrome",
                Glyphs = "plain", MouseEnabled = false, HideGuide = true, SanitizeNames = false, AnimationInterval = 2, EnableUtilities = true };
            var text = NomadNetPrimaryConfiguration.Export(primary);
            await File.WriteAllTextAsync(Path.Combine(root, "config"), text);
            var opened = await NomadNetSettings.OpenAsync(root);
            Assert.Equal(primary.IntroText, opened.IntroText);
            Assert.Equal(primary.ColorMode, opened.ColorMode);
            Assert.Equal(primary.AnimationInterval, opened.AnimationInterval);
            Assert.True(opened.EnableUtilities);
            var editor = new NomadNetEditor(root, opened);
            using (editor)
                await editor.SetPrimaryConfigurationAsync(false, true);
            var native = await NomadNetSettings.OpenAsync(root);
            Assert.False(native.UsePrimaryConfiguration);
            Assert.Equal(JsonSerializer.Serialize(opened with { UsePrimaryConfiguration = false }), JsonSerializer.Serialize(native));
            Assert.Equal(text, await File.ReadAllTextAsync(Path.Combine(root, "config")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact, Trait("TestId", "T553")]
    public async Task T553FormsSaveRequireRestartValidateRuntimeBoundsCancelAndReset()
    {
        var root = Root();
        try
        {
            var settings = new NomadNetSettings { NodePageRefreshIntervalMinutes = 0, NodeFileRefreshIntervalMinutes = 0 };
            await settings.SaveAsync(root);
            using var editor = new NomadNetEditor(root, settings);
            await editor.SetPrimaryConfigurationAsync(true, true);
            var path = Path.Combine(root, "config");
            var initial = await File.ReadAllBytesAsync(path);
            await editor.OpenConfigurationSectionAsync("textui");
            editor.SetConfigurationField("intro_time", new("61"));
            await Assert.ThrowsAsync<InvalidDataException>(() => editor.SaveConfigurationSectionAsync(true));
            Assert.Equal(initial, await File.ReadAllBytesAsync(path));
            await editor.CloseAsync("discard");
            await editor.OpenConfigurationSectionAsync("textui");
            editor.SetConfigurationField("intro_text", new("staged only"));
            await editor.CloseAsync("discard");
            Assert.Equal(initial, await File.ReadAllBytesAsync(path));
            await editor.OpenConfigurationSectionAsync("textui");
            editor.SetConfigurationField("intro_text", new("saved title"));
            await editor.SaveConfigurationSectionAsync(true);
            Assert.Equal("Nomad Network", settings.IntroText);
            Assert.Equal("saved title", (await NomadNetSettings.OpenAsync(root)).IntroText);
            await editor.OpenConfigurationSectionAsync("textui");
            editor.ResetConfigurationSection(true);
            await editor.SaveConfigurationSectionAsync(true);
            Assert.Equal("Nomad Network", (await NomadNetSettings.OpenAsync(root)).IntroText);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact, Trait("TestId", "T554")]
    public async Task T554MalformedSavedNativeAndPrimaryFailBeforeActivation()
    {
        var root = Root();
        try
        {
            var path = Path.Combine(root, "settings.json");
            await File.WriteAllTextAsync(path, "{\"animationInterval\":0}");
            await Assert.ThrowsAsync<InvalidDataException>(() => NomadNetSettings.OpenAsync(root));
            var settings = new NomadNetSettings { UsePrimaryConfiguration = true };
            await settings.SaveAsync(root);
            await File.WriteAllTextAsync(Path.Combine(root, "config"), "[textui]\nintro_time=nan\n");
            await Assert.ThrowsAsync<InvalidDataException>(() => NomadNetSettings.OpenAsync(root));
            File.Delete(Path.Combine(root, "config"));
            await Assert.ThrowsAsync<FileNotFoundException>(() => NomadNetSettings.OpenAsync(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact, Trait("TestId", "T555")]
    public async Task T555RealIntroDurationAndWatchIntervalApplyAndCancellationJoinsWithoutFurtherWrites()
    {
        var output = new StringWriter();
        var clock = Stopwatch.StartNew();
        await NomadNetTerminalControls.IntroAsync(new() { IntroTime = .1, IntroText = "custom intro" }, output, false, default);
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(85));
        Assert.Contains("custom intro", output.ToString());
        var stamps = new List<TimeSpan>();
        clock.Restart();
        await NomadNetTerminalControls.WatchAsync(new() { AnimationInterval = 1 }, 2,
            () => { stamps.Add(clock.Elapsed); return "state"; }, output, false, default);
        Assert.Equal(2, stamps.Count);
        Assert.True(stamps[1] - stamps[0] >= TimeSpan.FromMilliseconds(900));
        using var cancel = new CancellationTokenSource();
        var count = 0;
        var running = NomadNetTerminalControls.WatchAsync(new() { AnimationInterval = 3600 }, 60,
            () => { count++; return "cancelled next"; }, output, false, cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(1, count);
        var text = output.ToString();
        await Task.Delay(50);
        Assert.Equal(text, output.ToString());
    }

    private static string Root()
    {
        var path = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomad-terminal-controls", "managed-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(path);
        return path;
    }
}
