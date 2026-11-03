using System.Text.Json;
using Sideband;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandSettingsTests
{
    // GenerateSettingsVectors.py executes main.py's export statements at Sideband
    // d069122af14880e43607aed0b71add4c7806fbe0 (source SHA-256 recorded by the generator).
    [Fact]
    public async Task ExecutedUpstreamExportImportsTransactionallyAndPreservesUnsupportedSettings()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "sideband-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SidebandStateStore(root);
            var token = TestContext.Current.CancellationToken;
            var unsupported = await store.ImportSettingsAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-settings.json"), token);
            Assert.Contains("plugin_settings", unsupported);
            Assert.Contains("lxmf_try_propagation_on_fail", unsupported);
            var loaded = await new SidebandStateStore(root).LoadAsync(cancellationToken: token);
            Assert.Equal("Reference Peer", loaded.DisplayName);
            Assert.Equal("Light", loaded.Theme);
            Assert.Equal("00112233445566778899aabbccddeeff", loaded.PropagationNode);
            Assert.True(loaded.PropagationByDefault);

            var output = Path.Combine(root, "export.json");
            await store.ExportSettingsAsync(output, token);
            await Assert.ThrowsAsync<IOException>(() => store.ExportSettingsAsync(output, token));
            using var exported = JsonDocument.Parse(await File.ReadAllTextAsync(output, token));
            Assert.Equal(42, exported.RootElement.GetProperty("plugin_settings").GetProperty("example")[1].GetInt32());
            Assert.True(exported.RootElement.GetProperty("lxmf_try_propagation_on_fail").GetBoolean());
            Assert.Equal("ffeeddccbbaa99887766554433221100", exported.RootElement.GetProperty("nn_home_node").GetString());
            Assert.False(exported.RootElement.GetProperty("dark_ui").GetBoolean());

            var invalid = Path.Combine(root, "invalid.json");
            await File.WriteAllTextAsync(invalid, """{"display_name":"Should not apply","telemetry_collector":"bad"}""", token);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportSettingsAsync(invalid, token));
            Assert.Equal("Reference Peer", (await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).DisplayName);
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task PropagationSettingsSurviveReloadAndExportCannotOverwriteState()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "sideband-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var token = TestContext.Current.CancellationToken;
            var store = new SidebandStateStore(root);
            await Assert.ThrowsAsync<ArgumentException>(() => store.SetPropagationAsync(null, true, token));
            await store.SetPropagationAsync(new string('A', 32), true, token);
            var reloaded = await new SidebandStateStore(root).LoadAsync(cancellationToken: token);
            Assert.Equal(new string('a', 32), reloaded.PropagationNode);
            Assert.True(reloaded.PropagationByDefault);
            await Assert.ThrowsAsync<ArgumentException>(() => store.SetPropagationAsync("bad", true, token));
            await store.SetPropagationAsync(null, true, token);
            reloaded = await new SidebandStateStore(root).LoadAsync(cancellationToken: token);
            Assert.Null(reloaded.PropagationNode);
            Assert.Equal(new string('a', 32), reloaded.LastPropagationNode);
            Assert.True(reloaded.PropagationByDefault);
            await Assert.ThrowsAsync<ArgumentException>(() => store.ExportSettingsAsync(store.StatePath, token));
            await store.SetPropagationAsync(null, false, token);
            Assert.Null((await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).PropagationNode);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task LegacyManualSelectionCanBecomeAutomaticWithoutLosingItsKnownNode()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "sideband-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var token = TestContext.Current.CancellationToken;
            var store = new SidebandStateStore(root);
            await File.WriteAllTextAsync(store.StatePath, """
                {"SchemaVersion":1,"ReticulumConfigDirectory":"isolated","Contacts":[],"Messages":[],
                 "PropagationNode":"00112233445566778899aabbccddeeff","PropagationByDefault":true}
                """, token);
            await store.SetPropagationAsync(null, true, token);
            var state = await new SidebandStateStore(root).LoadAsync(cancellationToken: token);
            Assert.Null(state.PropagationNode);
            Assert.Equal("00112233445566778899aabbccddeeff", state.LastPropagationNode);
            Assert.True(state.PropagationByDefault);
            var output = Path.Combine(root, "export.json");
            await store.ExportSettingsAsync(output, token);
            using var exported = JsonDocument.Parse(await File.ReadAllTextAsync(output, token));
            Assert.Equal(JsonValueKind.Null, exported.RootElement.GetProperty("lxmf_propagation_node").ValueKind);
            Assert.Equal(state.LastPropagationNode, exported.RootElement.GetProperty("last_lxmf_propagation_node").GetString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ScheduleSettingsAndRetryStatePersistAndMalformedIntervalsAreTransactional()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "sideband-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var token = TestContext.Current.CancellationToken;
            var store = new SidebandStateStore(root);
            var initial = await store.LoadAsync(cancellationToken: token);
            Assert.False(initial.PeriodicSynchronization);
            Assert.Equal(43_200, initial.SynchronizationIntervalSeconds);
            await store.SetPeriodicSynchronizationAsync(true, 60, token);
            await store.SetSynchronizationStateAsync(1_000_000, true, token);
            var state = await new SidebandStateStore(root).LoadAsync(cancellationToken: token);
            Assert.True(state.PeriodicSynchronization);
            Assert.Equal(60, state.SynchronizationIntervalSeconds);
            Assert.Equal(1_000_000d, state.LastSynchronization);
            Assert.True(state.SynchronizationRetrying);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SetSynchronizationStateAsync(double.NaN, false, token));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SetPeriodicSynchronizationAsync(true, 0, token));
            var input = Path.Combine(root, "import.json");
            await File.WriteAllTextAsync(input, """{"lxmf_periodic_sync":false,"lxmf_sync_interval":1.5}""", token);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportSettingsAsync(input, token));
            Assert.True((await store.LoadAsync(cancellationToken: token)).PeriodicSynchronization);
            await File.WriteAllTextAsync(input, """{"lxmf_periodic_sync":false,"lxmf_sync_interval":86400}""", token);
            Assert.Empty(await store.ImportSettingsAsync(input, token));
            var output = Path.Combine(root, "export.json");
            await store.ExportSettingsAsync(output, token);
            using var exported = JsonDocument.Parse(await File.ReadAllTextAsync(output, token));
            Assert.False(exported.RootElement.GetProperty("lxmf_periodic_sync").GetBoolean());
            Assert.Equal(86_400, exported.RootElement.GetProperty("lxmf_sync_interval").GetInt32());
            Assert.Equal(1_000_000d, (await store.LoadAsync(cancellationToken: token)).LastSynchronization);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task InvalidTypesOversizeAndCancellationLeaveExistingSettingsUnchanged()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "sideband-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var token = TestContext.Current.CancellationToken;
            var store = new SidebandStateStore(root);
            await store.SetPreferencesAsync("Retained", "Dark", token);
            var invalid = Path.Combine(root, "invalid.json");
            await File.WriteAllTextAsync(invalid, """{"display_name":"New","dark_ui":42}""", token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.ImportSettingsAsync(invalid, token));
            await File.WriteAllTextAsync(invalid, "null", token);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportSettingsAsync(invalid, token));
            await File.WriteAllTextAsync(invalid, new string(' ', 8 * 1024 * 1024 + 1), token);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportSettingsAsync(invalid, token));
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ExportSettingsAsync(Path.Combine(root, "cancelled.json"), cancelled.Token));
            Assert.False(File.Exists(Path.Combine(root, "cancelled.json")));
            Assert.Equal("Retained", (await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).DisplayName);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
