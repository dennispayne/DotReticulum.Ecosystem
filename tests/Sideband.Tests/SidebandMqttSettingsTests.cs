using System.Collections.Concurrent;
using System.Text.Json;
using Sideband;
using Sideband.Desktop;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandMqttSettingsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ActivePrimaryKeysAcceptGuiPortTextPersistAndExportWhileUnusedTlsRemainsArchiveOnly()
    {
        var root = Root();
        Directory.CreateDirectory(root);
        try
        {
            var input = Path.Combine(root, "import.json");
            await File.WriteAllTextAsync(input, """
                {"telemetry_to_mqtt":true,"telemetry_mqtt_host":"example.invalid","telemetry_mqtt_port":"1884",
                 "telemetry_mqtt_user":"synthetic-user","telemetry_mqtt_pass":"synthetic-password",
                 "telemetry_mqtt_validate_ssl":true}
                """, Token);
            var store = new SidebandStateStore(Path.Combine(root, "profile"));
            Assert.Equal(["telemetry_mqtt_validate_ssl"], await store.ImportSettingsAsync(input, Token));
            var mqtt = (await store.LoadAsync(cancellationToken: Token)).Telemetry.Mqtt;
            Assert.True(mqtt.Enabled);
            Assert.Equal(1884, mqtt.EffectivePort);
            Assert.Equal("example.invalid", mqtt.Host);
            Assert.Equal("synthetic-user", mqtt.Username);
            Assert.Equal("synthetic-password", mqtt.Password);
            Assert.False((await store.LoadAsync(cancellationToken: Token)).Telemetry.Enabled);
            Assert.Equal(mqtt, (await new SidebandStateStore(store.DataDirectory).LoadAsync(cancellationToken: Token)).Telemetry.Mqtt);
            var output = Path.Combine(root, "export.json");
            await store.ExportSettingsAsync(output, Token);
            using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(output, Token));
            Assert.Equal(1884, json.RootElement.GetProperty("telemetry_mqtt_port").GetInt32());
            Assert.True(json.RootElement.GetProperty("telemetry_mqtt_validate_ssl").GetBoolean());
            await File.WriteAllTextAsync(input, """{"telemetry_mqtt_port":""}""", Token);
            Assert.Empty(await store.ImportSettingsAsync(input, Token));
            Assert.Equal(1883, (await store.LoadAsync(cancellationToken: Token)).Telemetry.Mqtt.EffectivePort);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InvalidPrimaryImportsRollbackExactlyAndServiceContinuesWithValidNullableDefaults()
    {
        var root = Root();
        Directory.CreateDirectory(root);
        try
        {
            var store = new SidebandStateStore(Path.Combine(root, "profile"));
            await store.ConfigureTelemetryAsync(new() { Mqtt = new() { Host = "retained.invalid", Port = 1884 } }, Token);
            var original = await File.ReadAllBytesAsync(store.StatePath, Token);
            var input = Path.Combine(root, "import.json");
            foreach (var port in new[] { "0", "-1", "65536", "\"invalid\"", "\"1.5\"", "true" })
            {
                await File.WriteAllTextAsync(input,
                    "{\"telemetry_mqtt_host\":\"discarded.invalid\",\"telemetry_mqtt_port\":" + port + "}", Token);
                await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportSettingsAsync(input, Token));
                Assert.Equal(original, await File.ReadAllBytesAsync(store.StatePath, Token));
            }
            await File.WriteAllTextAsync(input, """{"telemetry_mqtt_host":null,"telemetry_mqtt_port":null}""", Token);
            Assert.Empty(await store.ImportSettingsAsync(input, Token));
            var state = await new SidebandStateStore(store.DataDirectory).LoadAsync(cancellationToken: Token);
            Assert.Null(state.Telemetry.Mqtt.Host);
            Assert.Null(state.Telemetry.Mqtt.Port);
            Assert.False(state.Telemetry.Mqtt.Enabled);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ActualConsoleAliasesActivateIndependentlyAndNeverEchoCredentials()
    {
        var root = Root();
        try
        {
            var store = new SidebandStateStore(root);
            await using var client = new SidebandClient(store);
            var output = new ConcurrentQueue<string>();
            await using var console = new SidebandConsole(client, store, output.Enqueue, output.Enqueue, Token);
            await Execute("config set telemetry mqtt_host example.invalid");
            await Execute("config set telemetry mqtt_port 1884");
            await Execute("config set telemetry mqtt_user synthetic-private-user");
            await Execute("config set telemetry mqtt_pass synthetic-private-password");
            await Execute("config set telemetry to_mqtt true");
            await Execute("config list telemetry");
            await Execute("config get telemetry mqtt_pass");
            var settings = (await client.LoadAsync(Token)).Telemetry;
            Assert.False(settings.Enabled);
            Assert.True(settings.Mqtt.Enabled);
            Assert.Equal("synthetic-private-user", settings.Mqtt.Username);
            Assert.Equal("synthetic-private-password", settings.Mqtt.Password);
            Assert.All(output, line => Assert.DoesNotContain("synthetic-private", line));
            Assert.Contains(output, line => line.Contains("mqtt_pass = [redacted]", StringComparison.Ordinal));
            await Assert.ThrowsAsync<NotSupportedException>(() => Execute("config set telemetry mqtt_validate_ssl true"));
            await Assert.ThrowsAsync<InvalidDataException>(() => Execute("config set telemetry mqtt_port 0"));
            Assert.Equal(1884, (await client.LoadAsync(Token)).Telemetry.Mqtt.Port);
            await Execute("config set telemetry to_mqtt false");
            await Execute("config set telemetry mqtt_pass None");
            Assert.Null((await client.LoadAsync(Token)).Telemetry.Mqtt.Password);

            async Task Execute(string line) =>
                Assert.True(await console.ExecuteAsync(SidebandConsoleSyntax.Resolve(SidebandConsoleSyntax.Parse(line))!, Token));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static string Root() => Path.Combine(AppContext.BaseDirectory, "mqtt-settings-" + Guid.NewGuid().ToString("N"));
}
