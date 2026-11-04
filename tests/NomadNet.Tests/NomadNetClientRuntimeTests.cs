using System.Text.Json;

namespace NomadNet.Tests;

// Actual primary constructor, applyConfig, notify_message_recieved and CLI main at
// f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2; generator records exact Git blob SHA-256.
[Trait("Category", "Unit")]
public sealed class NomadNetClientRuntimeTests
{
    [Fact]
    public void ImportedUiNotificationAndCliPrecedenceMatchExecutedPrimary()
    {
        using var fixture = JsonDocument.Parse(typeof(NomadNetClientRuntimeTests).Assembly
            .GetManifestResourceStream("NomadNet.Tests.NomadNetClientRuntimeVectors.json")!);
        Assert.Equal("f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2", fixture.RootElement.GetProperty("commit").GetString());
        Assert.Equal("c36ce4c537fe5af534910d1f5200d2a2ea5dd50f74bf9e4157222eff15614eb2",
            fixture.RootElement.GetProperty("sourceBlobSha256").GetProperty("nomadnet/NomadNetworkApp.py").GetString());
        foreach (var vector in fixture.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var imported = NomadNetConfigImport.Parse(vector.GetProperty("input").GetString()!);
            Assert.Empty(imported.UnsupportedKeys);
            var expected = vector.GetProperty("observed");
            Assert.Equal(expected.GetProperty("configured_ui").GetInt32(), (int)imported.Settings.UserInterface);
            Assert.Equal(expected.GetProperty("notify_on_new_message").GetBoolean(), imported.Settings.NotifyOnNewMessage);
            var output = "";
            NomadNetClientRuntime.NotifyReceived(imported.Settings,
                NomadNetClientRuntime.IsHeadless(imported.Settings, false, false), "test",
                () => output += '\a', _ => Assert.Fail("Unexpected observer error."));
            Assert.Equal(expected.GetProperty("notification").GetString(), output);
            Assert.Equal(0, expected.GetProperty("constructor_daemon_ui").GetInt32());
            Assert.True(NomadNetClientRuntime.IsHeadless(imported.Settings, true, false));
            foreach (var command in expected.GetProperty("cli").EnumerateArray())
            {
                var args = command.GetProperty("args").EnumerateArray().Select(value => value.GetString()).ToArray();
                var daemon = args.Any(value => value is "-d" or "--daemon");
                var text = args.Any(value => value is "-t" or "--textui");
                Assert.Equal(imported.Settings.UserInterface == NomadNetUserInterface.None ||
                    command.GetProperty("daemon").GetBoolean(), NomadNetClientRuntime.IsHeadless(imported.Settings, daemon, text));
            }
        }
    }

    [Theory]
    [InlineData("[client]\nuser_interface=menu", "placeholder")]
    [InlineData("[client]\nuser_interface=graphical", "none or text")]
    [InlineData("[client]\nuser_interface=web", "none or text")]
    [InlineData("[client]\nuser_interface=garbage", "none or text")]
    [InlineData("[client]\nuser_interface=text,none", "scalar")]
    [InlineData("[client]\nnotify_on_new_message=maybe", "boolean")]
    public void InvalidOrPlaceholderSelectionsFailExplicitly(string text, string error) =>
        Assert.Contains(error, Assert.Throws<InvalidDataException>(() => NomadNetConfigImport.Parse(text)).Message);

    [Fact]
    public void NoObserverAndFailingObserverCannotDisableSubsequentNotifications()
    {
        var settings = new NomadNetSettings();
        var errors = new List<string>();
        NomadNetClientRuntime.NotifyReceived(settings, false, "without-observer", null, errors.Add);
        NomadNetClientRuntime.NotifyReceived(settings, false, "failed-message", () => throw new IOException("observer failed"), errors.Add);
        Assert.Contains("failed-message", Assert.Single(errors));
        Assert.Contains("observer failed", errors[0]);
        var count = 0;
        NomadNetClientRuntime.NotifyReceived(settings, false, "healthy-message", () => count++, errors.Add);
        NomadNetClientRuntime.NotifyReceived(settings, true, "daemon", () => count++, errors.Add);
        NomadNetClientRuntime.NotifyReceived(settings with { NotifyOnNewMessage = false }, false, "muted", () => count++, errors.Add);
        Assert.Equal(1, count);
    }
}

[Trait("Category", "Integration")]
public sealed class NomadNetClientRuntimePersistenceTests
{
    [Fact]
    public async Task OfflineProfileMigrationActivatesClientRuntimeSettingsAndRetainsIdentity()
    {
        var root = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomad-client-profile-" + Guid.NewGuid().ToString("N")));
        var source = Path.Combine(root, "primary");
        var target = Path.Combine(root, "native");
        Directory.CreateDirectory(Path.Combine(source, "storage"));
        using var identity = DotReticulum.Core.Identity.Generate();
        var privateKey = identity.ExportPrivateKey();
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(source, "storage", "identity"), privateKey);
            const string text = "[client]\nuser_interface=none\nnotify_on_new_message=no\n";
            await File.WriteAllTextAsync(Path.Combine(source, "config"), text);
            await File.WriteAllBytesAsync(Path.Combine(source, "storage", "peersettings"),
                DotReticulum.Core.MsgPack.Pack(new Dictionary<object, object?> { ["display_name"] = "Client runtime import" }));
            var report = await NomadNetProfileImport.ImportAsync(source, target, Path.Combine(root, "selected-rns"));
            Assert.DoesNotContain(report.Warnings, warning => warning.Contains("user_interface", StringComparison.Ordinal) ||
                warning.Contains("notify_on_new_message", StringComparison.Ordinal));
            var settings = await NomadNetSettings.OpenAsync(target);
            Assert.Equal(NomadNetUserInterface.None, settings.UserInterface);
            Assert.False(settings.NotifyOnNewMessage);
            Assert.Equal(text, await File.ReadAllTextAsync(Path.Combine(source, "config")));
            var restored = await File.ReadAllBytesAsync(Path.Combine(target, "identity"));
            try { Assert.Equal(privateKey, restored); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(restored); }
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(privateKey);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task SelectionSurvivesImportRestartAndUnrelatedUpdatesWithoutChangingSource()
    {
        var root = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomad-client-runtime-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "config");
            const string text = "[client]\nuser_interface=none\nnotify_on_new_message=no\n";
            await File.WriteAllTextAsync(source, text);
            var target = Path.Combine(root, "profile");
            var imported = await NomadNetConfigImport.ImportAsync(source, target);
            Assert.Empty(imported.UnsupportedKeys);
            var settings = await NomadNetSettings.OpenAsync(target);
            Assert.Equal(NomadNetUserInterface.None, settings.UserInterface);
            Assert.False(settings.NotifyOnNewMessage);
            await (settings with { DisplayName = "Changed name" }).SaveAsync(target);
            var reopened = await NomadNetSettings.OpenAsync(target);
            Assert.Equal(NomadNetUserInterface.None, reopened.UserInterface);
            Assert.False(reopened.NotifyOnNewMessage);
            Assert.Equal(text, await File.ReadAllTextAsync(source));
            await Assert.ThrowsAsync<IOException>(() => NomadNetConfigImport.ImportAsync(source, target));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                (reopened with { UserInterface = (NomadNetUserInterface)1 }).SaveAsync(target));
            Assert.Equal(NomadNetUserInterface.None, (await NomadNetSettings.OpenAsync(target)).UserInterface);
            var legacy = Path.Combine(root, "legacy");
            Directory.CreateDirectory(legacy);
            await new NomadNetSettings().SaveAsync(legacy);
            var legacyJson = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(legacy, "settings.json")))!.AsObject();
            legacyJson.Remove("userInterface");
            legacyJson.Remove("notifyOnNewMessage");
            await File.WriteAllTextAsync(Path.Combine(legacy, "settings.json"), legacyJson.ToJsonString());
            var defaults = await NomadNetSettings.OpenAsync(legacy);
            Assert.Equal(NomadNetUserInterface.Text, defaults.UserInterface);
            Assert.True(defaults.NotifyOnNewMessage);
            await File.WriteAllTextAsync(source, "[client]\nuser_interface=web");
            var invalid = Path.Combine(root, "invalid");
            await Assert.ThrowsAsync<InvalidDataException>(() => NomadNetConfigImport.ImportAsync(source, invalid));
            Assert.False(Directory.Exists(invalid));
            Assert.Empty(Directory.GetDirectories(root, "*.import-*"));
        }
        finally { Directory.Delete(root, true); }
    }
}
