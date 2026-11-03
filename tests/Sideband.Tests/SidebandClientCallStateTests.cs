using System.Text.Json;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandClientCallStateTests
{
    [Fact]
    public async Task VoiceSettingsTrustHistoryAndConversationPreferencesSurviveRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "sideband-call-state-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        try
        {
            var store = new SidebandStateStore(root);
            var identity = new string('a', 32);
            var delivery = new string('b', 32);
            var configuration = new SidebandVoiceConfiguration
            {
                TrustedOnly = true, DirectPushToTalk = true, HalfDuplexLoudspeaker = false,
                Microphone = "capture", Speaker = "private", Loudspeaker = "loud", Ringer = "ring",
            };
            await store.ConfigureVoiceAsync(true, configuration, token);
            await store.AddContactAsync(SidebandContact.Create("Peer", delivery, trusted: true), token);
            await store.SetConversationPushToTalkAsync(delivery, true, token);
            await store.AddContactAsync(SidebandContact.Create("Renamed", delivery), token);
            await store.SetVoicePeerTrustAsync(identity.ToUpperInvariant(), true, token);
            await store.AddCallEventAsync(new(DateTimeOffset.UtcNow, "incoming-missed", identity), token);
            await store.AddCallEventAsync(new(DateTimeOffset.UtcNow.AddDays(-15), "incoming-missed", identity), token);
            var state = await new SidebandStateStore(root).LoadAsync(cancellationToken: token);
            Assert.True(state.VoiceEnabled);
            Assert.Equal(configuration, state.Voice);
            Assert.True(Assert.Single(state.Contacts).DirectPushToTalk);
            Assert.True(state.Contacts[0].Trusted);
            Assert.Equal(identity, Assert.Single(state.VoicePeers!).IdentityHash);
            Assert.True(state.VoicePeers![0].VoiceTrusted);
            Assert.Equal("incoming-missed", Assert.Single(state.Calls!).Event);

            var export = Path.Combine(root, "voice-settings.json");
            await store.ExportSettingsAsync(export, token);
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(export, token));
            Assert.True(json.RootElement.GetProperty("voice_enabled").GetBoolean());
            Assert.True(json.RootElement.GetProperty("voice_trusted_only").GetBoolean());
            Assert.True(json.RootElement.GetProperty("voice_hdx_direct_ptt").GetBoolean());
            Assert.False(json.RootElement.GetProperty("voice_hdx_loudspeaker").GetBoolean());
            Assert.Equal("capture", json.RootElement.GetProperty("voice_input").GetString());
            var imported = new SidebandStateStore(Path.Combine(root, "import"));
            Assert.Empty(await imported.ImportSettingsAsync(export, token));
            Assert.Equal(configuration, (await imported.LoadAsync(cancellationToken: token)).Voice);
            Assert.Null((await imported.LoadAsync(cancellationToken: token)).VoicePeers);
            Assert.Null((await imported.LoadAsync(cancellationToken: token)).Calls);
            await store.ClearCallHistoryAsync(token);
            Assert.Empty((await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).Calls!);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InvalidVoiceSettingsAndHistoryDoNotReplacePersistedState()
    {
        var root = Path.Combine(Path.GetTempPath(), "sideband-call-bounds-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        try
        {
            var store = new SidebandStateStore(root);
            await store.LoadAsync(cancellationToken: token);
            var before = await File.ReadAllBytesAsync(store.StatePath, token);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.ConfigureVoiceAsync(true, new() { Speaker = new string('x', 513) }, token));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.ConfigureVoiceAsync(true, new() { Microphone = "invalid\n" }, token));
            await Assert.ThrowsAsync<ArgumentException>(() => store.SetVoicePeerTrustAsync("invalid", true, token));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                store.AddCallEventAsync(new(DateTimeOffset.UtcNow, "event", "invalid"), token));
            Assert.Equal(before, await File.ReadAllBytesAsync(store.StatePath, token));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExistingStateLeavesVoiceDisabledWithoutOpeningDevices()
    {
        var root = Path.Combine(Path.GetTempPath(), "sideband-call-legacy-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        try
        {
            Directory.CreateDirectory(root);
            var store = new SidebandStateStore(root);
            await File.WriteAllTextAsync(store.StatePath,
                """{"SchemaVersion":1,"ReticulumConfigDirectory":"isolated","Contacts":[],"Messages":[]}""", token);
            var state = await store.LoadAsync(cancellationToken: token);
            Assert.False(state.VoiceEnabled);
            Assert.Null(state.Voice);
            Assert.Null(state.VoicePeers);
            Assert.Null(state.Calls);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
