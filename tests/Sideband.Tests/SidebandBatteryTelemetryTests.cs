using System.Text;
using System.Text.Json;
using Sideband.Desktop;
using V = Sideband.SidebandTelemetryValue;
using S = Sideband.SidebandTelemetrySensorId;

namespace Sideband.Tests;

[Trait("Category", "Unit")]
public sealed class SidebandBatteryInputTests
{
    [Theory]
    [InlineData("POWER_SUPPLY_STATUS=Charging\nPOWER_SUPPLY_CAPACITY=NaN\n")]
    [InlineData("POWER_SUPPLY_STATUS=Charging\nPOWER_SUPPLY_CAPACITY=Infinity\n")]
    [InlineData("POWER_SUPPLY_STATUS=Charging\nPOWER_SUPPLY_CAPACITY=-1\n")]
    [InlineData("POWER_SUPPLY_STATUS=Charging\nPOWER_SUPPLY_CAPACITY=101\n")]
    [InlineData("POWER_SUPPLY_STATUS=Charging\nPOWER_SUPPLY_CAPACITY=1\nPOWER_SUPPLY_CAPACITY=2\n")]
    [InlineData("POWER_SUPPLY_STATUS=Charging\nPOWER_SUPPLY_CAPACITY=1\nBROKEN\n")]
    public void InvalidInputNeverBecomesAnAvailableReading(string text) =>
        Assert.Throws<InvalidDataException>(() => SidebandBatterySource.Parse(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void InputSizeAndUnicodeAreStrictlyBounded()
    {
        Assert.Throws<InvalidDataException>(() => SidebandBatterySource.Parse(new byte[8193]));
        Assert.Throws<DecoderFallbackException>(() => SidebandBatterySource.Parse([0xff]));
    }
}

[Trait("Category", "Integration")]
public sealed class SidebandBatteryTelemetryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ActualPrimaryBatteryObservationsMatchSyntheticFilesystemOrExplicitSafetyDifference(string name, string json)
    {
        using var document = JsonDocument.Parse(json);
        var item = document.RootElement;
        var root = TemporaryDirectory();
        try
        {
            foreach (var node in item.GetProperty("nodes").EnumerateArray())
            {
                var path = Path.Combine(root, "BAT" + node.GetInt32());
                Directory.CreateDirectory(path);
                await File.WriteAllTextAsync(Path.Combine(path, "uevent"), item.GetProperty("uevent").GetString(), Token);
            }
            var actual = await SidebandBatterySource.ReadAtAsync(item.GetProperty("linux").GetBoolean() ? root : null, Token);
            if (item.GetProperty("packed").ValueKind == JsonValueKind.Null || name.EndsWith("-primary", StringComparison.Ordinal))
            {
                Assert.Equal(SidebandTelemetryStatus.Unavailable, actual.Status);
                Assert.Equal(SidebandTelemetryValueKind.Nil, actual.WireValue.Kind);
                Assert.False(string.IsNullOrEmpty(actual.Error));
            }
            else
            {
                Assert.Equal(SidebandTelemetryStatus.Available, actual.Status);
                var expected = item.GetProperty("packed");
                Assert.Equal(expected[0].GetDouble(), actual.Data.Get("charge_percent")!.Number);
                Assert.Equal(expected[1].GetBoolean(), actual.Data.Get("charging")!.Boolean);
                Assert.Equal(expected[0].GetDouble(), actual.WireValue.Items[0].Number);
                Assert.Equal(expected[1].GetBoolean(), actual.WireValue.Items[1].Boolean);
                Assert.Equal(SidebandTelemetryValueKind.Nil, actual.WireValue.Items[2].Kind);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SelectedNodeDoesNotSilentlyFallThroughAndMalformedOversizeCancellationRecover()
    {
        var root = TemporaryDirectory();
        try
        {
            foreach (var name in new[] { "BAT0", "BAT9" }) Directory.CreateDirectory(Path.Combine(root, name));
            var first = Path.Combine(root, "BAT0", "uevent");
            await File.WriteAllTextAsync(Path.Combine(root, "BAT9", "uevent"),
                "POWER_SUPPLY_STATUS=Charging\nPOWER_SUPPLY_CAPACITY=99\n", Token);
            Assert.Equal(SidebandTelemetryStatus.Unavailable, (await SidebandBatterySource.ReadAtAsync(root, Token)).Status);
            await File.WriteAllBytesAsync(first, new byte[8193], Token);
            Assert.Equal(SidebandTelemetryStatus.Unavailable, (await SidebandBatterySource.ReadAtAsync(root, Token)).Status);
            await File.WriteAllBytesAsync(first, [0xff], Token);
            Assert.Equal(SidebandTelemetryStatus.Unavailable, (await SidebandBatterySource.ReadAtAsync(root, Token)).Status);
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token);
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SidebandBatterySource.ReadAtAsync(root, cancelled.Token));
            await File.WriteAllTextAsync(first, "POWER_SUPPLY_STATUS=Full\nPOWER_SUPPLY_CAPACITY=73.25\n", Token);
            var healthy = await SidebandBatterySource.ReadAtAsync(root, Token);
            Assert.Equal(73.2, healthy.Data.Get("charge_percent")!.Number);
            Assert.False(healthy.Data.Get("charging")!.Boolean);
            using var exclusive = new FileStream(first, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.True(exclusive.CanRead);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExplicitCollectionCachesAvailableDataAndDisableReenableInvalidatesWithoutSubscribers()
    {
        var root = TemporaryDirectory();
        try
        {
            var source = new ObservingBattery();
            await using var client = new SidebandClient(new(root), voiceDevices: null, batterySource: source);
            await client.ConfigureTelemetryAsync(new() { Enabled = true, InformationEnabled = true, Information = "Retain information" }, Token);
            Assert.Equal(0, source.Reads);
            var config = (await client.LoadAsync(Token)).Telemetry with { BatteryEnabled = true };
            await client.ConfigureTelemetryAsync(config, Token);
            Assert.Equal(1, source.Reads);
            var report = await client.RefreshLocalTelemetryAsync(Token);
            Assert.Equal(1, source.Reads);
            Assert.Equal(42.2, report!.Get(S.Battery)!.Data.Get("charge_percent")!.Number);
            Assert.Equal("Retain information", report.Get(S.Information)!.Data.Get("contents")!.Text);
            await client.ConfigureTelemetryAsync(config with { BatteryEnabled = false }, Token);
            Assert.Null((await client.RefreshLocalTelemetryAsync(Token))!.Get(S.Battery));
            await client.ConfigureTelemetryAsync(config, Token);
            Assert.Equal(2, source.Reads);
            source.Available = false;
            await client.ConfigureTelemetryAsync(config with { BatteryEnabled = false }, Token);
            await client.ConfigureTelemetryAsync(config, Token);
            Assert.Equal(3, source.Reads);
            Assert.Equal(SidebandTelemetryStatus.Unavailable, (await client.RefreshLocalTelemetryAsync(Token))!.Get(S.Battery)!.Status);
            Assert.Equal(4, source.Reads);
            source.Available = true;
            Assert.Equal(SidebandTelemetryStatus.Available, (await client.RefreshLocalTelemetryAsync(Token))!.Get(S.Battery)!.Status);
            Assert.Equal(5, source.Reads);
            var before = await File.ReadAllBytesAsync(Path.Combine(root, "state.json"), Token);
            await Assert.ThrowsAsync<InvalidDataException>(() => client.ConfigureTelemetryAsync(config with
            { ManualSensors = [new((byte)S.Battery, Convert.ToHexString(V.Array(V.From(1), V.From(false), V.Nil).Encode()))] }, Token));
            Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(root, "state.json"), Token));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AvailableBatteryRefreshesAfterThePrimaryTenSecondStalenessInterval()
    {
        var root = TemporaryDirectory();
        try
        {
            var source = new ObservingBattery();
            await using var client = new SidebandClient(new(root), null, source);
            await client.ConfigureTelemetryAsync(new() { Enabled = true, BatteryEnabled = true }, Token);
            for (var index = 0; index < 3; index++) await client.RefreshLocalTelemetryAsync(Token);
            Assert.Equal(1, source.Reads);
            await Task.Delay(TimeSpan.FromSeconds(10.1), Token);
            await client.RefreshLocalTelemetryAsync(Token);
            Assert.Equal(2, source.Reads);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DisposalCancelsAnOwnedInFlightBatteryReadAndJoinsConcurrentDisposers()
    {
        var root = TemporaryDirectory();
        try
        {
            var source = new BlockingBattery();
            await using var client = new SidebandClient(new(root), null, source);
            var preparing = client.ConfigureTelemetryAsync(new() { Enabled = true, BatteryEnabled = true }, Token);
            await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            var disposal = client.DisposeAsync().AsTask();
            await Task.WhenAll(disposal, client.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5), Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing);
            Assert.True(source.Cancelled);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.RefreshLocalTelemetryAsync(Token));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task NativeConsoleAliasAndPrimarySettingPersistAcrossClientRestartWithoutImplicitOptIn()
    {
        var root = TemporaryDirectory();
        try
        {
            await using (var client = new SidebandClient(new(root)))
            {
                var console = new SidebandConsoleConfiguration(client);
                await console.Find("sensors", "battery").Write(await client.LoadAsync(Token), "true", Token);
                var state = await client.LoadAsync(Token);
                Assert.True(state.Telemetry.BatteryEnabled);
                Assert.False(state.Telemetry.Enabled);
                Assert.Null(await client.RefreshLocalTelemetryAsync(Token));
                await new SidebandStateStore(root).ExportSettingsAsync(Path.Combine(root, "export.json"), Token);
            }
            var next = Path.Combine(root, "imported");
            var store = new SidebandStateStore(next);
            Assert.Empty(await store.ImportSettingsAsync(Path.Combine(root, "export.json"), Token));
            await using var reopened = new SidebandClient(store);
            Assert.True((await reopened.LoadAsync(Token)).Telemetry.BatteryEnabled);
            Assert.False((await reopened.LoadAsync(Token)).Telemetry.Enabled);
        }
        finally { Directory.Delete(root, true); }
    }

    public static IEnumerable<object[]> Cases()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-battery.json")));
        Assert.Equal("d069122af14880e43607aed0b71add4c7806fbe0", document.RootElement.GetProperty("revision").GetString());
        Assert.Equal("80418b1aa4de638f2c65f1bd64e5ea0466378a6f0bc682781672ef89b93a7dab",
            document.RootElement.GetProperty("source_sha256").GetString());
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
            yield return [item.GetProperty("name").GetString()!, item.GetRawText()];
    }

    private static string TemporaryDirectory()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "battery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class ObservingBattery : ISidebandBatterySource
    {
        internal int Reads;
        internal bool Available = true;
        public Task<SidebandTelemetryReading> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            return Task.FromResult(Available
                ? SidebandBatterySource.Parse("POWER_SUPPLY_STATUS=Charging\nPOWER_SUPPLY_CAPACITY=42.25\n"u8)
                : SidebandBatterySource.Unavailable("Synthetic missing battery"));
        }
    }

    private sealed class BlockingBattery : ISidebandBatterySource
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Cancelled;
        public async Task<SidebandTelemetryReading> ReadAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            finally { Cancelled = cancellationToken.IsCancellationRequested; }
            throw new InvalidOperationException("An infinite read unexpectedly completed.");
        }
    }
}
