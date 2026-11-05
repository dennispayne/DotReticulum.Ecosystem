using System.Collections.Concurrent;
using System.Text.Json;
using DotReticulum.Core;
using Sideband.Desktop;
using V = Sideband.SidebandTelemetryValue;
using S = Sideband.SidebandTelemetrySensorId;

namespace Sideband.Tests;

[Trait("Category", "Unit")]
public sealed class SidebandPropagationTelemetryConfigurationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("identity\nfile")]
    [InlineData("identity\0file")]
    [Trait("TestId", "T908")]
    public void EnabledSensorRequiresAnExplicitBoundedIdentityPath(string? path)
    {
        Assert.False(new SidebandTelemetryConfiguration().PropagationEnabled);
        Assert.Null(new SidebandTelemetryConfiguration().PropagationIdentityPath);
        Assert.Throws<InvalidDataException>(() => new SidebandTelemetryConfiguration
        { PropagationEnabled = true, PropagationIdentityPath = path }.Validate());
        Assert.Throws<InvalidDataException>(() => new SidebandTelemetryConfiguration
        { PropagationIdentityPath = new string('a', 4097) }.Validate());
    }

    [Fact]
    [Trait("TestId", "T909")]
    public void AutomaticAndManualPropagationSensorsCannotCollideEvenWhileGlobalTelemetryIsOff()
    {
        var config = new SidebandTelemetryConfiguration
        {
            PropagationIdentityPath = "explicit-identity",
            ManualSensors = [new((byte)S.LxmfPropagation, "c0")]
        };
        var validated = config.Validate();
        Assert.False(validated.PropagationEnabled);
        Assert.Equal(Path.GetFullPath("explicit-identity"), validated.PropagationIdentityPath);
        Assert.NotNull(validated.PropagationIdentityPath);
        Assert.True(Path.IsPathFullyQualified(validated.PropagationIdentityPath));
        Assert.Equal(validated.PropagationIdentityPath, validated.Validate().PropagationIdentityPath);
        Assert.Throws<InvalidDataException>(() => (config with { PropagationEnabled = true }).Validate());
    }
}

[Trait("Category", "Integration")]
public sealed class SidebandPropagationTelemetryApplicationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("TestId", "T911")]
    public async Task NativeConsoleExtensionsPersistExportImportAndNeverImplyPrimaryPluginActivation()
    {
        var root = Root();
        try
        {
            var path = Path.Combine(root, "explicit-identity");
            await using (var client = new SidebandClient(new(root)))
            {
                var console = new SidebandConsoleConfiguration(client);
                await console.Find("sensors", "propagation_identity").Write(await client.LoadAsync(Token), path, Token);
                await console.Find("sensors", "propagation").Write(await client.LoadAsync(Token), "true", Token);
                var config = (await client.LoadAsync(Token)).Telemetry;
                Assert.True(config.PropagationEnabled);
                Assert.False(config.Enabled);
                Assert.Equal(path, config.PropagationIdentityPath);
                Assert.Equal(SidebandPropagationTelemetryClientState.Disabled, client.PropagationTelemetryStatus.State);
                Assert.Null(await client.RefreshLocalTelemetryAsync(Token));
                await new SidebandStateStore(root).ExportSettingsAsync(Path.Combine(root, "export.json"), Token);
            }
            var imported = new SidebandStateStore(Path.Combine(root, "imported"));
            Assert.Empty(await imported.ImportSettingsAsync(Path.Combine(root, "export.json"), Token));
            await using var reopened = new SidebandClient(imported);
            var saved = (await reopened.LoadAsync(Token)).Telemetry;
            Assert.True(saved.PropagationEnabled);
            Assert.False(saved.Enabled);
            Assert.Equal(path, saved.PropagationIdentityPath);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "export.json"), Token));
            Assert.Contains("dotreticulum_propagation_identity", json.RootElement.GetRawText());
            Assert.Contains("dotreticulum_propagation_telemetry", json.RootElement.GetRawText());
            var primary = SidebandTelemetrySettings.Import(new Dictionary<string, JsonElement>
            {
                ["telemetry_enabled"] = JsonDocument.Parse("true").RootElement.Clone(),
                ["telemetry_s_rns_transport"] = JsonDocument.Parse("true").RootElement.Clone()
            }, new());
            Assert.False(primary.PropagationEnabled);
            Assert.Null(primary.PropagationIdentityPath);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("TestId", "T912")]
    public async Task ConnectedProviderReconcilesEnableIdentityReplacementDisableAndReconnectWithoutOldData()
    {
        var root = Root();
        try
        {
            using var first = Identity.Generate();
            using var second = Identity.Generate();
            var firstPath = Path.Combine(root, "first");
            var secondPath = Path.Combine(root, "second");
            IdentityFile.SaveNew(first, firstPath);
            IdentityFile.SaveNew(second, secondPath);
            var observed = new ConcurrentQueue<Identity>();
            var source = new SidebandPropagationTelemetryTests.Source((identity, token) =>
            {
                token.ThrowIfCancellationRequested();
                observed.Enqueue(identity);
                return Task.FromResult(Status(identity));
            });
            await using var client = new SidebandClient(new(root), null, new SidebandBatterySource(), _ => source);
            var config = new SidebandTelemetryConfiguration
            { Enabled = true, PropagationEnabled = true, PropagationIdentityPath = firstPath };
            await client.ConfigureTelemetryAsync(config, Token);
            Assert.Equal(SidebandPropagationTelemetryClientState.NotConnected, client.PropagationTelemetryStatus.State);
            Assert.Equal(SidebandTelemetryStatus.Unavailable, (await client.RefreshLocalTelemetryAsync(Token))!.Get(S.LxmfPropagation)!.Status);
            Assert.Equal(0, source.Calls);
            var network = await Network(root);
            await client.ConnectAsync(network, Token);
            await Available(client);
            var initial = (await client.RefreshLocalTelemetryAsync(Token))!.Get(S.LxmfPropagation)!;
            Assert.Equal(first.Hash, initial.Data.Get("identity_hash")!.GetBinary());
            Assert.Equal(1, source.Calls);
            Assert.Equal(SidebandPropagationTelemetryClientState.Active, client.PropagationTelemetryStatus.State);
            Assert.Null(client.PropagationTelemetryStatus.Diagnostic);
            await client.RefreshLocalTelemetryAsync(Token);
            Assert.Equal(1, source.Calls);
            config = config with { PropagationIdentityPath = secondPath };
            await client.ConfigureTelemetryAsync(config, Token);
            await Available(client);
            Assert.Equal(second.Hash, (await client.RefreshLocalTelemetryAsync(Token))!.Get(S.LxmfPropagation)!.Data.Get("identity_hash")!.GetBinary());
            Assert.Equal(2, source.Calls);
            Assert.Throws<ObjectDisposedException>(() => observed.First().Sign([1]));
            await client.ConfigureTelemetryAsync(config with { PropagationEnabled = false }, Token);
            Assert.Null((await client.RefreshLocalTelemetryAsync(Token))!.Get(S.LxmfPropagation));
            Assert.Equal(SidebandPropagationTelemetryClientState.Disabled, client.PropagationTelemetryStatus.State);
            Assert.Null(client.PropagationTelemetryStatus.Snapshot);
            await client.ConfigureTelemetryAsync(config, Token);
            await Available(client);
            Assert.Equal(3, source.Calls);
            await client.DisconnectAsync();
            Assert.Equal(SidebandPropagationTelemetryClientState.NotConnected, client.PropagationTelemetryStatus.State);
            Assert.Null(client.PropagationTelemetryStatus.Snapshot);
            Assert.Equal(SidebandTelemetryStatus.Unavailable, (await client.RefreshLocalTelemetryAsync(Token))!.Get(S.LxmfPropagation)!.Status);
            await client.ConnectAsync(network, Token);
            await Available(client);
            Assert.Equal(second.Hash, (await client.RefreshLocalTelemetryAsync(Token))!.Get(S.LxmfPropagation)!.Data.Get("identity_hash")!.GetBinary());
            Assert.Equal(4, source.Calls);
            await client.ConfigureTelemetryAsync(config with { Enabled = false }, Token);
            Assert.Null(await client.RefreshLocalTelemetryAsync(Token));
            Assert.Equal(SidebandPropagationTelemetryClientState.Disabled, client.PropagationTelemetryStatus.State);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("TestId", "T913")]
    public async Task UnavailableOrMalformedExplicitIdentityIsSanitizedAndCanRecoverWithoutFallback()
    {
        var root = Root();
        try
        {
            var path = Path.Combine(root, "private-selected-path");
            var diagnostics = new ConcurrentQueue<string>();
            var source = new SidebandPropagationTelemetryTests.Source((identity, _) => Task.FromResult(Status(identity)));
            await using var client = new SidebandClient(new(root), null, new SidebandBatterySource(), _ => source);
            client.ErrorReported += diagnostics.Enqueue;
            var config = new SidebandTelemetryConfiguration { Enabled = true, PropagationEnabled = true, PropagationIdentityPath = path };
            await client.ConfigureTelemetryAsync(config, Token);
            await client.ConnectAsync(await Network(root), Token);
            var unavailable = (await client.RefreshLocalTelemetryAsync(Token))!.Get(S.LxmfPropagation)!;
            Assert.Equal(SidebandTelemetryStatus.Unavailable, unavailable.Status);
            Assert.NotNull(unavailable.Error);
            Assert.Equal(SidebandPropagationTelemetryClientState.IdentityUnavailable, client.PropagationTelemetryStatus.State);
            Assert.Equal(0, source.Calls);
            Assert.DoesNotContain(diagnostics, text => text.Contains(path, StringComparison.Ordinal));
            await File.WriteAllBytesAsync(path, new byte[65], Token);
            await client.RefreshLocalTelemetryAsync(Token);
            Assert.Equal(0, source.Calls);
            File.Delete(path);
            using var identity = Identity.Generate();
            IdentityFile.SaveNew(identity, path);
            await client.RefreshLocalTelemetryAsync(Token);
            await Available(client);
            var recovered = (await client.RefreshLocalTelemetryAsync(Token))!.Get(S.LxmfPropagation)!;
            Assert.Equal(identity.Hash, recovered.Data.Get("identity_hash")!.GetBinary());
            Assert.Equal(1, source.Calls);
            Assert.Null(client.PropagationTelemetryStatus.Diagnostic);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("TestId", "T914")]
    public async Task RuntimeDisposalCancelsAndJoinsTheQueryBeforeDisposingItsLoadedIdentity()
    {
        var root = Root();
        try
        {
            using var original = Identity.Generate();
            var path = Path.Combine(root, "identity");
            IdentityFile.SaveNew(original, path);
            var entered = new TaskCompletionSource<Identity>(TaskCreationOptions.RunContinuationsAsynchronously);
            var leftWithLiveIdentity = false;
            var source = new SidebandPropagationTelemetryTests.Source(async (identity, token) =>
            {
                entered.TrySetResult(identity);
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { leftWithLiveIdentity = identity.Sign([1]).Length == 64; }
                throw new InvalidOperationException("The read must be cancelled.");
            });
            await using var client = new SidebandClient(new(root), null, new SidebandBatterySource(), _ => source);
            await client.ConfigureTelemetryAsync(new() { Enabled = true, PropagationEnabled = true, PropagationIdentityPath = path }, Token);
            await client.ConnectAsync(await Network(root), Token);
            var loaded = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            var pendingReport = await client.RefreshLocalTelemetryAsync(Token).WaitAsync(TimeSpan.FromSeconds(1), Token);
            Assert.Equal(SidebandTelemetryStatus.Unavailable, pendingReport!.Get(S.LxmfPropagation)!.Status);
            await Task.WhenAll(client.DisposeAsync().AsTask(), client.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.True(leftWithLiveIdentity);
            Assert.Throws<ObjectDisposedException>(() => loaded.Sign([1]));
            Assert.Null(client.PropagationTelemetryStatus.Snapshot);
            Assert.Equal(64, original.Sign([1]).Length);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("TestId", "T915")]
    public async Task FailedSourceCannotLeakExceptionDetailsOrPreventConnectedApplicationService()
    {
        var root = Root();
        try
        {
            using var identity = Identity.Generate();
            var path = Path.Combine(root, "identity");
            IdentityFile.SaveNew(identity, path);
            var source = new SidebandPropagationTelemetryTests.Source((_, _) =>
                throw new IOException("PRIVATE UNLOGGABLE DETAIL"));
            var errors = new ConcurrentQueue<string>();
            await using var client = new SidebandClient(new(root), null, new SidebandBatterySource(), _ => source);
            client.ErrorReported += errors.Enqueue;
            await client.ConfigureTelemetryAsync(new() { Enabled = true, PropagationEnabled = true, PropagationIdentityPath = path }, Token);
            await client.ConnectAsync(await Network(root), Token);
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                while (client.PropagationTelemetrySnapshot?.Failure != SidebandPropagationTelemetryFailure.SourceFailed)
                    await Task.Delay(1, deadline.Token);
            }
            var reading = (await client.RefreshLocalTelemetryAsync(Token))!.Get(S.LxmfPropagation)!;
            Assert.Equal(SidebandTelemetryStatus.Unavailable, reading.Status);
            Assert.Contains("SourceFailed", reading.Error);
            Assert.Equal(SidebandPropagationTelemetryFailure.SourceFailed, client.PropagationTelemetryStatus.Snapshot!.Failure);
            Assert.True(client.IsConnected);
            Assert.DoesNotContain(errors, text => text.Contains("PRIVATE", StringComparison.Ordinal));
            Assert.DoesNotContain("PRIVATE", client.PropagationTelemetryStatus.Diagnostic);
            Assert.Equal(1, source.Calls);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Root()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "propagation-application-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task Available(SidebandClient client)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (client.PropagationTelemetrySnapshot?.Reading.Status != SidebandTelemetryStatus.Available)
            await Task.Delay(1, deadline.Token);
    }

    private static async Task<string> Network(string root)
    {
        var path = Path.Combine(root, "network");
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "config"),
            "[reticulum]\n share_instance = no\n enable_transport = no\n[logging]\n loglevel = 0\n[interfaces]\n", Token);
        return path;
    }

    private static V Status(Identity identity)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "sideband-propagation-telemetry.json")));
        var value = V.Decode(Convert.FromHexString(document.RootElement.GetProperty("status_hex").GetString()!));
        return V.Map(value.Members.Select(pair => pair.Key.Text == "identity_hash"
            ? KeyValuePair.Create(pair.Key, V.From(identity.Hash)) : pair).ToArray());
    }
}
