using DotReticulum.Core;

namespace Sideband.Tests;

public sealed partial class SidebandProfileImportTests
{
    public static bool VoiceFilePlatformIsUnavailable => !OperatingSystem.IsWindows() && !OperatingSystem.IsLinux();
    public static bool VoicePipePlatformIsUnavailable => !OperatingSystem.IsLinux();

    [Fact(SkipWhen = nameof(VoiceFilePlatformIsUnavailable), Skip = "Voice-log file migration requires Windows or Linux metadata acceptance.")]
    public async Task PrimaryVoiceLogEventsImportWithoutGrantingTrustOrEnablingDevicesAndSurviveRestart()
    {
        using var profile = new Profile();
        var path = Path.Combine(profile.Source, "lxst_call_log");
        var token = TestContext.Current.CancellationToken;
        using var fixture = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "sideband-voice-profile.json")));
        var vector = fixture.RootElement.GetProperty("observations").EnumerateArray()
            .Single(row => row.GetProperty("name").GetString() == "all-primary-event-types");
        var entries = (object?[])MsgPack.UnpackApplicationValue(Convert.FromHexString(vector.GetProperty("packed_hex").GetString()!))!;
        foreach (var entry in entries) ((Dictionary<object, object?>)entry!)["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
        await File.WriteAllBytesAsync(path, MsgPack.Pack(entries), token);
        var before = profile.Fingerprint();
        var result = await SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, token);
        var state = await new SidebandStateStore(profile.Target).LoadAsync(cancellationToken: token);
        Assert.Equal(5, state.Calls!.Count);
        Assert.Equal(vector.GetProperty("loaded").EnumerateArray().Select(row => row.GetProperty("event").GetString()),
            state.Calls.Select(call => call.Event));
        Assert.All(state.Calls, call => Assert.Equal("000102030405060708090a0b0c0d0e0f", call.IdentityHash));
        Assert.All(state.Calls, call => Assert.True(call.Imported));
        Assert.Empty(state.Contacts);
        Assert.Empty(state.VoicePeers ?? []);
        Assert.False(state.VoiceEnabled);
        Assert.Equal(before, profile.Fingerprint());
        Assert.Equal(await File.ReadAllBytesAsync(path, token),
            await File.ReadAllBytesAsync(Path.Combine(profile.Target, "imported-profile", "lxst_call_log"), token));
        Assert.Contains(result.Notices, notice => notice.Contains("historical assertions", StringComparison.Ordinal));
        await using var reopened = new SidebandClient(new(profile.Target));
        Assert.Equal(state.Calls, (await reopened.LoadAsync(token)).Calls);
    }

    [Theory(SkipWhen = nameof(VoiceFilePlatformIsUnavailable), Skip = "Voice-log file migration requires Windows or Linux metadata acceptance.")]
    [InlineData("truncated")]
    [InlineData("malformed")]
    [InlineData("oversized")]
    [InlineData("future")]
    public async Task InvalidPrimaryVoiceLogRollsBackWholeImportAndHealthyRetrySucceeds(string mutation)
    {
        using var profile = new Profile();
        var path = Path.Combine(profile.Source, "lxst_call_log");
        var token = TestContext.Current.CancellationToken;
        var bytes = mutation switch
        {
            "truncated" => new byte[] { 0x91 },
            "malformed" => MsgPack.Pack(new object?[] { "not an event" }),
            "oversized" => new byte[SidebandVoiceProfile.MaximumEncodedBytes + 1],
            _ => MsgPack.Pack(new object?[] { new Dictionary<object, object?>
            {
                ["time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 241d,
                ["event"] = "incoming-missed", ["identity"] = new byte[16],
            } }),
        };
        await File.WriteAllBytesAsync(path, bytes, token);
        var before = profile.Fingerprint();
        await Assert.ThrowsAsync<InvalidDataException>(() => SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, token));
        Assert.False(Directory.Exists(profile.Target));
        Assert.Empty(Directory.GetDirectories(profile.Root, ".native.import-*"));
        Assert.Equal(before, profile.Fingerprint());
        await File.WriteAllBytesAsync(path, MsgPack.Pack(Array.Empty<object?>()), token);
        await SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, token);
        Assert.Empty((await new SidebandStateStore(profile.Target).LoadAsync(cancellationToken: token)).Calls!);
    }

    [Fact(SkipWhen = nameof(VoiceFilePlatformIsUnavailable), Skip = "Voice-log file migration requires Windows or Linux metadata acceptance.")]
    public void PrimaryVoiceLogMetadataAcceptsOnlyAnUnlinkedRegularFile()
    {
        using var profile = new Profile();
        var path = Path.Combine(profile.Source, "lxst_call_log");
        File.WriteAllBytes(path, [0x90]);
        SidebandVoiceProfileFile.Validate(path);
        Assert.Throws<InvalidDataException>(() => SidebandVoiceProfileFile.Validate(profile.Source));
        if (OperatingSystem.IsLinux())
        {
            var link = Path.Combine(profile.Root, "voice-link");
            File.CreateSymbolicLink(link, path);
            Assert.Throws<InvalidDataException>(() => SidebandVoiceProfileFile.Validate(link));
            File.Delete(link);
            File.CreateSymbolicLink(link, Path.Combine(profile.Root, "missing"));
            Assert.Throws<InvalidDataException>(() => SidebandVoiceProfileFile.Validate(link));
            File.Delete(link);
        }
    }

    [Fact(SkipWhen = nameof(VoicePipePlatformIsUnavailable), Skip = "FIFO/device metadata acceptance requires Linux.")]
    public async Task PrimaryVoiceLogPipesAndDevicesFailBeforeOpeningOrBlocking()
    {
        using var profile = new Profile();
        var path = Path.Combine(profile.Source, "lxst_call_log");
        var token = TestContext.Current.CancellationToken;
        using var process = new System.Diagnostics.Process();
        process.StartInfo.FileName = "mkfifo";
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.ArgumentList.Add(path);
        Assert.True(process.Start());
        await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token);
        Assert.Equal(0, process.ExitCode);
        Assert.Throws<InvalidDataException>(() => SidebandVoiceProfileFile.Validate(path));
        Assert.Throws<InvalidDataException>(() => SidebandVoiceProfileFile.Validate("/dev/zero"));
        await Assert.ThrowsAsync<InvalidDataException>(() => SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, token))
            .WaitAsync(TimeSpan.FromSeconds(5), token);
        Assert.False(Directory.Exists(profile.Target));
        Assert.Empty(Directory.GetDirectories(profile.Root, ".native.import-*"));
    }
}
