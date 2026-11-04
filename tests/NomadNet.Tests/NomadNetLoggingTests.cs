using System.Text.Json;

namespace NomadNet.Tests;

// Executed unchanged primary constructors/ConfigObj/applyConfig/CLI and RNS.log.
// Primary f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2; RNS a29bfb51c468945c96a1ee22cae0114e993e2391.
[Trait("Category", "Integration")]
public sealed class NomadNetLoggingReferenceTests
{
    [Fact]
    public async Task ConfigRoutingSeverityLabelsAndCliPrecedenceMatchExecutedPrimary()
    {
        using var fixture = JsonDocument.Parse(typeof(NomadNetLoggingReferenceTests).Assembly
            .GetManifestResourceStream("NomadNet.Tests.NomadNetLoggingVectors.json")!);
        Assert.Equal("f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2", fixture.RootElement.GetProperty("commit").GetString());
        Assert.Equal("c36ce4c537fe5af534910d1f5200d2a2ea5dd50f74bf9e4157222eff15614eb2",
            fixture.RootElement.GetProperty("sourceBlobSha256").GetProperty("nomadnet/NomadNetworkApp.py").GetString());
        Assert.Equal("ed6cd970da6e19a0fdc34aae74897f876917879eb02cf6aacc86eb9ae13ebbd9",
            fixture.RootElement.GetProperty("rnsLoggerSha256").GetString());
        var root = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomad-logging-reference-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var vector in fixture.RootElement.GetProperty("vectors").EnumerateArray())
            {
                var name = vector.GetProperty("name").GetString()!;
                var imported = NomadNetConfigImport.Parse(vector.GetProperty("input").GetString()!);
                var expected = vector.GetProperty("observed");
                Assert.False(expected.GetProperty("failure").GetProperty("visibleWriteError").GetBoolean());
                Assert.False(expected.GetProperty("failure").GetProperty("emergencyConsoleFallback").GetBoolean());
                Assert.False(expected.GetProperty("failure").GetProperty("originalMessageRetried").GetBoolean());
                Assert.False(expected.GetProperty("failure").GetProperty("completed").GetBoolean());
                Assert.True(expected.GetProperty("failure").GetProperty("fallbackFlagSet").GetBoolean());
                Assert.Equal(NomadNetLogging.MaximumFileBytes, expected.GetProperty("rotation").GetProperty("maximumBytes").GetInt32());
                Assert.True(expected.GetProperty("rotation").GetProperty("rotatedAfterExceeding").GetBoolean());
                Assert.True(expected.GetProperty("rotation").GetProperty("recreated").GetBoolean());
                var options = NomadNetLoggingOptions.Resolve(imported.Settings, root, 2, true, false,
                    vector.GetProperty("forceConsole").GetBoolean());
                Assert.Equal(expected.GetProperty("level").GetInt32(), options.Level);
                Assert.Equal(expected.GetProperty("destination").GetString() == "file", options.Destination == NomadNetLogDestination.File);
                if (options.File is not null)
                {
                    var custom = expected.GetProperty("customFile").GetString();
                    Assert.Equal(custom is null ? Path.Combine(root, "logfile") : Path.GetFullPath(custom), options.File);
                }
                // Route the actual native append to a test-owned path without changing process CWD.
                var path = Path.Combine(root, name);
                using var output = new StringWriter();
                await using (var logging = NomadNetLogging.Start(options with { File = path }, output, timestamps: false))
                    for (var level = 0; level <= 8; level++) logging.Write(level, "probe-" + level);
                Assert.Equal(expected.GetProperty("stdout").GetString(), output.ToString().ReplaceLineEndings("\n"));
                Assert.Equal(expected.GetProperty("file").GetString(),
                    File.Exists(path) ? (await File.ReadAllTextAsync(path)).ReplaceLineEndings("\n") : "");
                foreach (var command in expected.GetProperty("cli").EnumerateArray())
                {
                    var args = command.GetProperty("args").EnumerateArray().Select(value => value.GetString()).ToArray();
                    var daemon = args.Any(value => value is "-d" or "--daemon");
                    var console = args.Any(value => value is "-c" or "--console");
                    var text = args.Any(value => value is "-t" or "--textui");
                    Assert.Equal(command.GetProperty("forceConsole").GetBoolean(), daemon && console);
                    Assert.Equal(command.GetProperty("daemon").GetBoolean(), daemon && !text);
                    var selected = NomadNetLoggingOptions.Resolve(imported.Settings, root, 2, daemon, text, console);
                    Assert.Equal(imported.Settings.LogDestination == NomadNetLogDestination.File && !(daemon && console),
                        selected.Destination == NomadNetLogDestination.File);
                }
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ImportPersistenceRotationFailedWritesAndRecoveryAreBounded()
    {
        var root = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomad-logging-lifecycle-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var config = Path.Combine(root, "config");
            const string text = "[logging]\nloglevel=6\ndestination=file\nlogfile=relative.log\n";
            await File.WriteAllTextAsync(config, text);
            var profile = Path.Combine(root, "profile");
            Assert.Empty((await NomadNetConfigImport.ImportAsync(config, profile)).UnsupportedKeys);
            var settings = await NomadNetSettings.OpenAsync(profile);
            Assert.Equal(6, settings.LogLevel);
            Assert.Equal("relative.log", settings.LogFile);
            await (settings with { DisplayName = "Restart" }).SaveAsync(profile);
            Assert.Equal(settings.LogFile, (await NomadNetSettings.OpenAsync(profile)).LogFile);
            Assert.Equal(text, await File.ReadAllTextAsync(config));
            var path = Path.Combine(root, "log");
            await using (var logger = NomadNetLogging.Start(new(7, NomadNetLogDestination.File, path), timestamps: false))
            {
                logger.Write(3, "before restart");
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write)) stream.SetLength(NomadNetLogging.MaximumFileBytes);
                logger.Write(3, "rotation");
                Assert.True(File.Exists(path + ".1"));
                Assert.False(File.Exists(path));
                logger.Write(3, "after rotation");
            }

            await using (var restarted = NomadNetLogging.Start(new(7, NomadNetLogDestination.File, path), timestamps: false))
            {
                restarted.Write(3, "after restart");
                Assert.Contains("after rotation", await File.ReadAllTextAsync(path));
                Assert.Contains("after restart", await File.ReadAllTextAsync(path));
                Assert.Equal(2, Directory.GetFiles(root, "log*").Length);
                File.Delete(path);
                Directory.CreateDirectory(path);
                restarted.Write(3, "failed");
                Assert.NotNull(restarted.Fault);
                Assert.True(restarted.FailureToken.IsCancellationRequested);
                Assert.Throws<IOException>(restarted.ThrowIfFaulted);
                Directory.Delete(path);
            }
            using var errors = new StringWriter();
            await using var recovered = NomadNetLogging.Start(new(7, NomadNetLogDestination.File, path), errors: errors);
            recovered.Write(3, "healthy");
            Assert.Null(recovered.Fault);
            Assert.Contains("healthy", await File.ReadAllTextAsync(path));
            Assert.Empty(errors.ToString());
            Assert.Throws<DirectoryNotFoundException>(() =>
                NomadNetLogging.Start(new(4, NomadNetLogDestination.File, Path.Combine(root, "absent", "log"))));
            var invalid = Record.Exception(() => NomadNetLogging.Start(new(4, NomadNetLogDestination.File, root)));
            Assert.True(invalid is IOException or UnauthorizedAccessException);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FullProfileImportRetainsExplicitLoggingWithoutChangingIdentityOrSourceAndRejectsBadSettingsAtomically()
    {
        var root = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomad-logging-profile-" + Guid.NewGuid().ToString("N")));
        var source = Path.Combine(root, "primary");
        var target = Path.Combine(root, "native");
        Directory.CreateDirectory(Path.Combine(source, "storage"));
        using var identity = DotReticulum.Core.Identity.Generate();
        var key = identity.ExportPrivateKey();
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(source, "storage", "identity"), key);
            const string text = "[logging]\nloglevel=5\ndestination=file\nlogfile=profile.log\n";
            await File.WriteAllTextAsync(Path.Combine(source, "config"), text);
            await File.WriteAllBytesAsync(Path.Combine(source, "storage", "peersettings"),
                DotReticulum.Core.MsgPack.Pack(new Dictionary<object, object?> { ["display_name"] = "Logging import" }));
            var report = await NomadNetProfileImport.ImportAsync(source, target, Path.Combine(root, "selected-rns"));
            Assert.DoesNotContain(report.Warnings, warning => warning.Contains("logging.", StringComparison.Ordinal));
            var imported = await NomadNetSettings.OpenAsync(target);
            Assert.Equal(5, imported.LogLevel);
            Assert.Equal(NomadNetLogDestination.File, imported.LogDestination);
            Assert.Equal("profile.log", imported.LogFile);
            Assert.Equal(text, await File.ReadAllTextAsync(Path.Combine(source, "config")));
            var restored = await File.ReadAllBytesAsync(Path.Combine(target, "identity"));
            try { Assert.Equal(key, restored); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(restored); }
            var bad = Path.Combine(root, "bad");
            await File.WriteAllTextAsync(Path.Combine(source, "config"), "[logging]\nloglevel=NaN\n");
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                NomadNetProfileImport.ImportAsync(source, bad, Path.Combine(root, "selected-rns")));
            Assert.False(Directory.Exists(bad));
            await Assert.ThrowsAsync<InvalidDataException>(() => (imported with { LogLevel = 8 }).SaveAsync(target));
            await Assert.ThrowsAsync<InvalidDataException>(() => (imported with { LogFile = "" }).SaveAsync(target));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                (imported with { LogDestination = (NomadNetLogDestination)99 }).SaveAsync(target));
            Assert.Equal(5, (await NomadNetSettings.OpenAsync(target)).LogLevel);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
            Directory.Delete(root, true);
        }
    }
}

[Trait("Category", "Unit")]
public sealed class NomadNetLoggingTests
{
    [Theory]
    [InlineData("-999999999999999999999999999999", 0)]
    [InlineData("999999999999999999999999999999", 7)]
    [InlineData("+0_7", 7)]
    [InlineData(" 3 ", 3)]
    [InlineData("٣", 3)]
    [InlineData("𝟟", 7)]
    public void LevelsClampLikePythonInt(string text, int expected) =>
        Assert.Equal(expected, NomadNetLogging.ParseLevel(text));

    [Theory]
    [InlineData("1.0")]
    [InlineData("no")]
    [InlineData("1__0")]
    [InlineData("_4")]
    [InlineData("4_")]
    [InlineData("²")]
    [InlineData("+")]
    public void InvalidLevelFailsWithoutSuccessfulImport(string text) =>
        Assert.Throws<InvalidDataException>(() => NomadNetConfigImport.Parse("[logging]\nloglevel=" + text));

    [Fact]
    public async Task NoSubscriberCancellationDisposalRaceAndIndependentInstancesDoNotLeak()
    {
        using var first = new StringWriter();
        using var second = new StringWriter();
        await using var muted = NomadNetLogging.Start(new(0, NomadNetLogDestination.Stdout, null), first, timestamps: false);
        await using var verbose = NomadNetLogging.Start(new(7, NomadNetLogDestination.Stdout, null), second,
            clock: () => new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
        muted.Write(1, "not emitted");
        verbose.Write(1, "independent");
        Assert.Empty(first.ToString());
        Assert.Contains("[2026-11-04 12:00:00] [Error]    independent", second.ToString());
        verbose.Write(3, new string('x', 100_000) + "\u001b[0m\nforged");
        Assert.True(second.ToString().Length < NomadNetLogging.MaximumMessageCharacters + 256);
        Assert.DoesNotContain('\u001b', second.ToString());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => muted.Write(0, "cancelled", cancelled.Token));
        Assert.Throws<OperationCanceledException>(() =>
            NomadNetLogging.Start(new(4, NomadNetLogDestination.Stdout, null), cancellationToken: cancelled.Token));
        await Task.WhenAll(Task.Run(async () => await verbose.DisposeAsync()),
            Task.Run(() => { for (var i = 0; i < 32; i++) verbose.Write(3, "racing"); }));
        var before = second.ToString();
        verbose.Write(0, "late callback");
        await verbose.DisposeAsync();
        Assert.Equal(before, second.ToString());
        muted.Write(0, "still healthy");
        Assert.Contains("still healthy", first.ToString());
    }

    [Fact]
    public async Task FailingOptionalOutputIsVisibleAndDoesNotInstallGlobalFallback()
    {
        using var errors = new StringWriter();
        using var healthy = new StringWriter();
        await using var failed = NomadNetLogging.Start(new(4, NomadNetLogDestination.Stdout, null), new FailingWriter(), errors);
        await using var other = NomadNetLogging.Start(new(4, NomadNetLogDestination.Stdout, null), healthy);
        var observers = 0;
        using var success = failed.FailureToken.Register(() => observers++);
        using var failure = failed.FailureToken.Register(() => throw new InvalidOperationException("optional observer"));
        failed.Write(1, "unwritten");
        Assert.Contains("stopping without fallback", errors.ToString());
        Assert.Contains("logging cancellation observer failed", errors.ToString());
        Assert.Equal(1, observers);
        Assert.True(failed.FailureToken.IsCancellationRequested);
        Assert.Throws<IOException>(failed.ThrowIfFaulted);
        other.Write(3, "healthy");
        Assert.Contains("healthy", healthy.ToString());
    }

    private sealed class FailingWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("observer output failed");
    }
}
