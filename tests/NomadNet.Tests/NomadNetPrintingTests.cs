using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace NomadNet.Tests;

// GenerateNomadNetPrintingVectors.py executes primary f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2.
// print_message AST has disclosed synthetic owner/I/O, not live physical printing.
[Trait("Category", "Unit")]
public sealed class NomadNetPrintingPolicyTests
{
    private const string Hash = "abcdef0123456789abcdef0123456789";

    [Fact]
    public void FiltersConfigurationAndFormattingMatchExecutedPrimary()
    {
        using var fixture = JsonDocument.Parse(typeof(NomadNetPrintingPolicyTests).Assembly
            .GetManifestResourceStream("NomadNet.Tests.NomadNetPrintingVectors.json")!);
        var root = fixture.RootElement;
        Assert.Equal("f6b56044b42f6ca08dc99b3ecb9d965a8eb5edb2", root.GetProperty("commit").GetString());
        Assert.Equal("c36ce4c537fe5af534910d1f5200d2a2ea5dd50f74bf9e4157222eff15614eb2",
            root.GetProperty("sourceBlobSha256").GetProperty("nomadnet/NomadNetworkApp.py").GetString());
        Assert.True(root.GetProperty("customTemplateLoadedUnchanged").GetBoolean());
        foreach (var vector in root.GetProperty("vectors").EnumerateArray())
        {
            var imported = NomadNetConfigImport.Parse(vector.GetProperty("input").GetString()!);
            Assert.Empty(imported.UnsupportedKeys);
            var settings = imported.Settings;
            Assert.Equal(vector.GetProperty("enabled").GetBoolean(), settings.PrintMessages);
            Assert.Equal(vector.GetProperty("all").GetBoolean(), settings.PrintAllMessages);
            Assert.Equal(vector.GetProperty("trusted").GetBoolean(), settings.PrintTrustedMessages);
            Assert.Equal(vector.GetProperty("command").GetString(), settings.PrintCommand);
            foreach (var filter in vector.GetProperty("filters").EnumerateArray())
                Assert.Equal(filter.GetProperty("print").GetBoolean(),
                    NomadNetPrintingPolicy.ShouldPrint(settings, Hash, filter.GetProperty("trust").GetInt32()));
        }
        foreach (var sample in root.GetProperty("formatting").EnumerateArray())
        {
            static DateTimeOffset Local(JsonElement date)
            {
                var parts = date.EnumerateArray().Select(part => part.GetInt32()).ToArray();
                return new DateTimeOffset(new DateTime(parts[0], parts[1], parts[2], parts[3], parts[4], parts[5], DateTimeKind.Local));
            }
            var message = new NomadNetMessage(new string('a', 64), Hash, "inbound", Local(sample.GetProperty("sentLocal")),
                sample.GetProperty("title").GetString()!, sample.GetProperty("content").GetString()!, "received");
            var contact = new NomadNetContact(Hash, "Public Sender") { TrustLevel = sample.GetProperty("trust").GetInt32() };
            Assert.Equal(sample.GetProperty("output").GetString(), NomadNetPrintingPolicy.Render(
                root.GetProperty("template").GetString()!, message, NomadNetPrintingPolicy.Origin(Hash, contact),
                Local(sample.GetProperty("receivedLocal"))));
        }
        var args = root.GetProperty("subprocess").GetProperty("arguments")[0]
            .EnumerateArray().Select(value => value.GetString()!).ToArray();
        Assert.Equal(args[..^1], NomadNetPrintingPolicy.CommandArguments("printer --flag \"two words\""));
    }

    [Theory]
    [InlineData("{unknown}")]
    [InlineData("{mbody!r}")]
    [InlineData("{origin[0]}")]
    [InlineData("{origin.__class__}")]
    [InlineData("{stime:>40}")]
    [InlineData("{mbody")]
    [InlineData("body}")]
    [InlineData("\0")]
    public void UnsupportedTemplateSyntaxFailsExplicitly(string template) =>
        Assert.Throws<InvalidDataException>(() => NomadNetPrintingPolicy.ValidateTemplate(template));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("printer \"unclosed")]
    [InlineData("printer trailing\\")]
    [InlineData("printer\ninjected")]
    [InlineData("'' argument")]
    public void InvalidCommandsFailBeforeAnyProcess(string command) =>
        Assert.Throws<InvalidDataException>(() => NomadNetPrintingPolicy.CommandArguments(command));

    [Fact]
    public void ArgumentsAreLiteralAndWindowsQuotedPathsStayIntact()
    {
        Assert.Equal(new[] { @"C:\Program Files\printer.exe", "two words", "", ";", "$(whoami)", "a b" },
            NomadNetPrintingPolicy.CommandArguments("\"C:\\Program Files\\printer.exe\" 'two words' \"\" ; $(whoami) a\\ b"));
        Assert.Equal(new[] { "printer", "a\\q", "a\"b" },
            NomadNetPrintingPolicy.CommandArguments("printer \"a\\q\" \"a\\\"b\""));
        Assert.Throws<InvalidDataException>(() => NomadNetPrintingPolicy.CommandArguments("p " + string.Join(' ', Enumerable.Repeat("a", 64))));
    }

    [Fact]
    public void DefaultsDenyAutomaticAndOnlySavedTrustOrExactSourcesAllowIt()
    {
        var settings = new NomadNetSettings();
        Assert.False(NomadNetPrintingPolicy.ShouldPrint(settings, Hash, 255));
        settings.PrintMessages = true;
        settings.PrintTrustedMessages = true;
        foreach (var trust in new[] { 0, 1, 2 }) Assert.False(NomadNetPrintingPolicy.ShouldPrint(settings, Hash, trust));
        Assert.True(NomadNetPrintingPolicy.ShouldPrint(settings, Hash, 255));
        settings.PrintTrustedMessages = false;
        settings.AllowedMessagePrintDestinations = [Hash.ToUpperInvariant()];
        Assert.False(NomadNetPrintingPolicy.ShouldPrint(settings, Hash, 255));
        settings.AllowedMessagePrintDestinations.Add(Hash);
        Assert.True(NomadNetPrintingPolicy.ShouldPrint(settings, Hash, 0));
        Assert.Equal("<" + Hash + ">", NomadNetPrintingPolicy.Origin(Hash, null));
    }

    [Fact]
    public void BoundsIncludeUtf8AndTemplatesNeverInterpretInsertedText()
    {
        NomadNetPrintingPolicy.ValidateTemplate(new string('a', NomadNetPrintingPolicy.MaximumTemplateBytes));
        Assert.Throws<InvalidDataException>(() =>
            NomadNetPrintingPolicy.ValidateTemplate(new string('a', NomadNetPrintingPolicy.MaximumTemplateBytes + 1)));
        Assert.Throws<InvalidDataException>(() =>
            NomadNetPrintingPolicy.ValidateTemplate(new string('€', NomadNetPrintingPolicy.MaximumTemplateBytes / 3 + 1)));
        var message = new NomadNetMessage("a", Hash, "inbound", DateTimeOffset.UnixEpoch, "{origin}", "{mbody}", "received");
        Assert.Equal("{origin}|{mbody}|{}", NomadNetPrintingPolicy.Render("{mtitle}|{mbody}|{{}}", message, "public", DateTimeOffset.UnixEpoch));
        Assert.Throws<InvalidDataException>(() => NomadNetPrintingPolicy.Render("{mbody}", message with
        { Content = new string('€', NomadNetPrintingPolicy.MaximumJobBytes / 3 + 1) }, "public", DateTimeOffset.UnixEpoch));
        Assert.Throws<InvalidDataException>(() => NomadNetConfigImport.Parse(
            "[printing]\nprint_messages=yes\nprint_from=" + string.Join(',', Enumerable.Repeat(Hash, 129))));
    }
}

[Trait("Category", "Integration")]
public sealed class NomadNetPrinterTests
{
    private const string Hash = "abcdef0123456789abcdef0123456789";
    private static NomadNetMessage Message => new(new string('a', 64), Hash, "inbound", DateTimeOffset.UnixEpoch, "Public title", "Public body", "received");
    private static string NewRoot()
    {
        var root = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomad-printing", "managed " + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        return root;
    }
    private static string PowerShell => Environment.GetEnvironmentVariable("DOTRETICULUM_PWSH") ?? "pwsh";
    private static string Stub => Path.GetFullPath(Path.Combine(SourceDirectory(), "..", "Pester", "NomadNetPrinterStub.ps1"));
    private static string SourceDirectory([System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        Path.GetDirectoryName(source)!;
    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    private static NomadNetSettings Settings(string root, string mode = "capture", string extra = "public literal ; $(ignored)") => new()
    {
        PrintCommand = $"{Quote(PowerShell)} -NoProfile -File {Quote(Stub)} -Mode {mode} -Output {Quote(Path.Combine(root, "captures"))} -Marker {Quote(Path.Combine(root, "marker"))} -Extra {Quote(extra)}",
    };

    [Fact]
    public async Task OptInImportPersistsCapturedTemplateWithoutForeignWritesAndLegacyDefaultsDeny()
    {
        var root = NewRoot();
        try
        {
            var template = Path.Combine(root, "public custom.txt");
            await File.WriteAllTextAsync(template, "{origin}|{mtitle}|{mbody}");
            var config = Path.Combine(root, "config");
            await File.WriteAllTextAsync(config,
                $"[printing]\nprint_messages=yes\nprint_from=trusted,{Hash}\nprint_command='printer --flag'\nmessage_template=\"{template}\"\n");
            var target = Path.Combine(root, "profile");
            var imported = await NomadNetConfigImport.ImportAsync(config, target);
            Assert.Empty(imported.UnsupportedKeys);
            var settings = await NomadNetSettings.OpenAsync(target);
            Assert.True(settings.PrintMessages);
            Assert.True(settings.PrintTrustedMessages);
            Assert.Equal(new[] { "trusted", Hash }, settings.AllowedMessagePrintDestinations);
            Assert.Equal("{origin}|{mtitle}|{mbody}", settings.PrintingMessageTemplate);
            File.Delete(template);
            Assert.Equal(settings.PrintingMessageTemplate, (await NomadNetSettings.OpenAsync(target)).PrintingMessageTemplate);
            var missing = NomadNetConfigImport.Parse($"[printing]\nprint_messages=yes\nmessage_template=\"{template}\"\n");
            Assert.Single(missing.Warnings);
            Assert.False(File.Exists(template));
            Assert.Equal(NomadNetPrintingPolicy.DefaultTemplate, missing.Settings.PrintingMessageTemplate);
            Assert.Equal("lp", NomadNetConfigImport.Parse("[printing]\nprint_command='ignored'\n").Settings.PrintCommand);
            Assert.Empty(NomadNetConfigImport.Parse("[printing]\nprint_messages=no\nmessage_template=~other/ignored\n").UnsupportedKeys);
            Assert.Throws<InvalidDataException>(() => NomadNetConfigImport.Parse("[printing]\nprint_messages=yes\nmessage_template=~other/no\n"));
            await File.WriteAllTextAsync(template, "{mbody!r}");
            Assert.Throws<InvalidDataException>(() => NomadNetConfigImport.Parse(
                $"[printing]\nprint_messages=yes\nmessage_template=\"{template}\"\n"));
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), "{\"schemaVersion\":1,\"displayName\":\"Legacy\"}");
            Assert.False((await NomadNetSettings.OpenAsync(root)).PrintMessages);
            Assert.Equal(new[] { "printing.unknown" }, NomadNetConfigImport.Parse("[printing]\nunknown=yes\n").UnsupportedKeys);
        }
        finally { Directory.Delete(root, true); }
    }

    [SupportedPrintFact]
    public async Task ManualAndAutomaticJobsUseOneFilenameArgumentCleanUpAndIgnoreObserverFailures()
    {
        var root = NewRoot();
        const string extra = "public literal ; $(ignored) \"quoted\" C:\\public\\tail\\";
        try
        {
            var logs = new List<string>();
            await using var printer = new NomadNetPrinter(root);
            printer.Log += _ => throw new InvalidOperationException("Public observer failure");
            printer.Log += logs.Add;
            printer.Start();
            var settings = Settings(root, extra: extra);
            Assert.False(printer.TryPrintIncoming(settings, Message, null, DateTimeOffset.UnixEpoch));
            var result = await printer.PrintMessageAsync(settings, Message, null, DateTimeOffset.UnixEpoch);
            Assert.True(result.Success, result.Detail);
            settings.PrintMessages = settings.PrintAllMessages = true;
            Assert.True(printer.TryPrintIncoming(settings, Message, null, DateTimeOffset.UnixEpoch));
            await printer.DisposeAsync();
            // Disposal may cancel the queued job, but the explicit completed manual capture remains.
            var receipt = Directory.GetFiles(Path.Combine(root, "captures"), "*.json").First();
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(receipt));
            var job = json.RootElement.GetProperty("original").GetString()!;
            Assert.Contains("managed ", job);
            Assert.False(File.Exists(job));
            Assert.Equal(extra, json.RootElement.GetProperty("extra").GetString());
            Assert.Contains("Public body", await File.ReadAllTextAsync(json.RootElement.GetProperty("copy").GetString()!));
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "storage", "print-jobs")));
            Assert.NotEmpty(logs);
            Assert.All(logs, line => Assert.DoesNotContain("Public body", line));
        }
        finally { Directory.Delete(root, true); }
    }

    [SupportedPrintTheory]
    [InlineData("--help")]
    [InlineData("--version")]
    public async Task PrinterArgumentsNeverSelectGlobalApplicationHelp(string literal)
    {
        var root = NewRoot();
        try
        {
            await using var printer = new NomadNetPrinter(root);
            printer.Start();
            var result = await printer.PrintMessageAsync(Settings(root, extra: literal), Message, null, DateTimeOffset.UnixEpoch);
            Assert.True(result.Success, result.Detail);
            using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(
                Directory.GetFiles(Path.Combine(root, "captures"), "*.json").Single()));
            Assert.Equal(literal, receipt.RootElement.GetProperty("extra").GetString());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MalformedSupervisorModeReportsBoundedContextInsteadOfGlobalHelp()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "nomadnet.exe" : "nomadnet");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true },
        };
        process.StartInfo.ArgumentList.Add("--internal-print-supervisor");
        process.StartInfo.ArgumentList.Add("--help");
        Assert.True(process.Start());
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(125, process.ExitCode);
        Assert.Empty(await output);
        var diagnostic = await error;
        Assert.Contains("Linux printer supervisor: private mode requires", diagnostic, StringComparison.Ordinal);
        Assert.InRange(diagnostic.Length, 1, 256);
    }

    [SupportedPrintTheory]
    [InlineData("fail")]
    [InlineData("flood")]
    [InlineData("hang")]
    [InlineData("childhang")]
    [InlineData("missing")]
    public async Task SubprocessFailureIsBoundedAndNextHealthyJobSucceeds(string mode)
    {
        var root = NewRoot();
        try
        {
            await using var printer = new NomadNetPrinter(root, TimeSpan.FromSeconds(4));
            printer.Start();
            var settings = mode == "missing" ? new NomadNetSettings { PrintCommand = "no-such-nomadnet-public-printer-9348" } : Settings(root, mode);
            var watch = Stopwatch.StartNew();
            var failed = await printer.PrintMessageAsync(settings, Message, null, DateTimeOffset.UnixEpoch);
            Assert.False(failed.Success);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), failed.Detail);
            if (mode == "fail") Assert.Contains("code 7", failed.Detail);
            var healthy = await printer.PrintMessageAsync(Settings(root), Message, null, DateTimeOffset.UnixEpoch);
            Assert.True(healthy.Success, healthy.Detail);
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "storage", "print-jobs")));
            if (mode == "childhang")
            {
                var childFile = Path.Combine(root, "marker.child");
                Assert.True(File.Exists(childFile));
                var childPid = int.Parse(await File.ReadAllTextAsync(childFile));
                AssertProcessExited(childPid);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [SupportedPrintFact]
    public async Task QueueIsBoundedSnapshotsAreImmutableAndDisposalCancelsQueuedWork()
    {
        var root = NewRoot();
        try
        {
            var printer = new NomadNetPrinter(root);
            printer.Start();
            var settings = Settings(root, "hang");
            var first = printer.PrintMessageAsync(settings, Message, null, DateTimeOffset.UnixEpoch);
            await WaitForFileAsync(Path.Combine(root, "marker"));
            var queued = Enumerable.Range(0, NomadNetPrinter.QueueCapacity).Select(_ =>
                printer.PrintMessageAsync(settings, Message, null, DateTimeOffset.UnixEpoch)).ToArray();
            var overflow = await printer.PrintMessageAsync(settings, Message, null, DateTimeOffset.UnixEpoch);
            Assert.False(overflow.Success);
            Assert.Contains("queue is full", overflow.Detail);
            var disposals = new[] { printer.DisposeAsync().AsTask(), printer.DisposeAsync().AsTask() };
            await Task.WhenAll(disposals).WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            foreach (var pending in queued) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "storage", "print-jobs")));
            Assert.False((await printer.PrintMessageAsync(settings, Message, null, DateTimeOffset.UnixEpoch)).Success);
            AssertProcessExited(int.Parse(await File.ReadAllTextAsync(Path.Combine(root, "marker"))));
        }
        finally { Directory.Delete(root, true); }
    }

    [SupportedPrintFact]
    public async Task CancellationKillsActiveChildAndSubsequentJobRunsWithoutSubscribers()
    {
        var root = NewRoot();
        try
        {
            await using var printer = new NomadNetPrinter(root);
            printer.Start();
            using var cancellation = new CancellationTokenSource();
            var job = printer.PrintMessageAsync(Settings(root, "childhang"), Message, null, DateTimeOffset.UnixEpoch, cancellation.Token);
            await WaitForFileAsync(Path.Combine(root, "marker.child"));
            var childPid = int.Parse(await File.ReadAllTextAsync(Path.Combine(root, "marker.child")));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.WaitAsync(TimeSpan.FromSeconds(10)));
            AssertProcessExited(childPid);
            Assert.True((await printer.PrintMessageAsync(Settings(root), Message, null, DateTimeOffset.UnixEpoch)).Success);
        }
        finally { Directory.Delete(root, true); }
    }

    [WindowsPrintJobTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RootExitCannotReleaseOwnedDescendantsOrTheirLockedFiles(bool cancel)
    {
        var root = NewRoot();
        var marker = Path.Combine(root, "marker.child");
        try
        {
            await using var printer = new NomadNetPrinter(root, TimeSpan.FromSeconds(4));
            var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<string>();
            printer.Log = diagnostics.Enqueue;
            printer.Start();
            using var cancellation = new CancellationTokenSource();
            var job = printer.PrintMessageAsync(Settings(root, "orphan"), Message, null, DateTimeOffset.UnixEpoch, cancellation.Token);
            await WaitForFileAsync(marker);
            if (cancel)
            {
                cancellation.Cancel();
                var failure = await Record.ExceptionAsync(() => job.WaitAsync(TimeSpan.FromSeconds(10)));
                AssertProcessExited(int.Parse(await File.ReadAllTextAsync(marker)));
                Assert.True(failure is OperationCanceledException, string.Join(" ", diagnostics));
            }
            else
            {
                var result = await job.WaitAsync(TimeSpan.FromSeconds(10));
                AssertProcessExited(int.Parse(await File.ReadAllTextAsync(marker)));
                Assert.False(result.Success, result.Detail);
                Assert.True(result.Detail.Contains("timed out", StringComparison.Ordinal), result.Detail + " " + string.Join(" ", diagnostics));
            }
            AssertProcessExited(int.Parse(await File.ReadAllTextAsync(marker)));
            using (File.Open(Path.Combine(root, "captures", "held-public-capture.json"),
                FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "storage", "print-jobs")));
            var healthy = await printer.PrintMessageAsync(Settings(root), Message, null, DateTimeOffset.UnixEpoch);
            Assert.True(healthy.Success, healthy.Detail);
        }
        finally
        {
            // The synthetic descendant also has an explicit cooperative release for a failing regression run.
            if (File.Exists(marker))
            {
                await File.WriteAllTextAsync(marker + ".release", "release public synthetic owner");
                AssertProcessExited(int.Parse(await File.ReadAllTextAsync(marker)));
            }
            Directory.Delete(root, true);
        }
    }

    [LinuxPrintJobTheory]
    [InlineData("orphan", "timeout")]
    [InlineData("orphanstream", "timeout")]
    [InlineData("orphan-session", "timeout")]
    [InlineData("doublefork", "timeout")]
    [InlineData("orphan", "cancel")]
    [InlineData("orphanstream", "cancel")]
    [InlineData("orphan-session", "cancel")]
    [InlineData("doublefork", "cancel")]
    [InlineData("orphan", "dispose")]
    [InlineData("doublefork", "dispose")]
    public async Task LinuxExitedCommandCannotAbandonItsDescendant(string mode, string completion)
    {
        var root = NewRoot();
        var marker = Path.Combine(root, "marker.child");
        try
        {
            await using var printer = new NomadNetPrinter(root,
                TimeSpan.FromSeconds(completion == "timeout" ? 4 : 15));
            printer.Start();
            using var cancellation = new CancellationTokenSource();
            var job = printer.PrintMessageAsync(Settings(root, mode), Message, null, DateTimeOffset.UnixEpoch, cancellation.Token);
            await WaitForFileAsync(marker);
            AssertProcessExited(int.Parse(await File.ReadAllTextAsync(Path.Combine(root, "marker"))));
            if (completion == "timeout")
            {
                var result = await job.WaitAsync(TimeSpan.FromSeconds(12));
                Assert.False(result.Success, "Exited root reported success while its descendant still owns the public job.");
                Assert.Contains("timed out", result.Detail, StringComparison.Ordinal);
            }
            else if (completion == "cancel")
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            else
            {
                var queued = printer.PrintMessageAsync(Settings(root), Message, null, DateTimeOffset.UnixEpoch);
                await Task.WhenAll(printer.DisposeAsync().AsTask(), printer.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(10));
                await printer.DisposeAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            }
            AssertProcessExited(int.Parse(await File.ReadAllTextAsync(marker)));
            if (File.Exists(Path.Combine(root, "marker.middle")))
                AssertProcessExited(int.Parse(await File.ReadAllTextAsync(Path.Combine(root, "marker.middle"))));
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "storage", "print-jobs")));
            await printer.DisposeAsync();
            await using var recovery = new NomadNetPrinter(root);
            recovery.Start();
            var healthy = await recovery.PrintMessageAsync(Settings(root), Message, null, DateTimeOffset.UnixEpoch);
            Assert.True(healthy.Success, healthy.Detail);
        }
        finally
        {
            if (File.Exists(marker))
            {
                await File.WriteAllTextAsync(marker + ".release", "release public synthetic owner");
                await File.WriteAllTextAsync(Path.Combine(root, "marker.middle.child.release"), "release public synthetic owner");
                var elapsed = Stopwatch.StartNew();
                while (Directory.Exists($"/proc/{int.Parse(await File.ReadAllTextAsync(marker))}") &&
                    elapsed.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
            }
            Directory.Delete(root, true);
        }
    }

    [SupportedPrintFact]
    public async Task QueuedCommandAndTemplateSnapshotsAreImmutableAndQueuedCancellationIsPrompt()
    {
        var root = NewRoot();
        try
        {
            await using var printer = new NomadNetPrinter(root);
            printer.Start();
            using var blocker = new CancellationTokenSource();
            var first = printer.PrintMessageAsync(Settings(root, "hang"), Message, null, DateTimeOffset.UnixEpoch, blocker.Token);
            await WaitForFileAsync(Path.Combine(root, "marker"));
            var settings = Settings(root) with { PrintingMessageTemplate = "PUBLIC SNAPSHOT {mbody}" };
            var queued = printer.PrintMessageAsync(settings, Message, null, DateTimeOffset.UnixEpoch);
            settings.PrintCommand = "no-such-printer-after-enqueue";
            settings.PrintingMessageTemplate = "{unsupported}";
            using var cancellation = new CancellationTokenSource();
            var cancelled = printer.PrintMessageAsync(Settings(root), Message, null, DateTimeOffset.UnixEpoch, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(TimeSpan.FromSeconds(1)));
            blocker.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.True((await queued).Success);
            await printer.DisposeAsync();
            var copies = Directory.GetFiles(Path.Combine(root, "captures"), "*.txt");
            Assert.Single(copies);
            Assert.Equal("PUBLIC SNAPSHOT Public body", await File.ReadAllTextAsync(copies[0]));
        }
        finally { Directory.Delete(root, true); }
    }

    [SupportedPrintFact]
    public async Task StaleOwnedJobsRecoverAndTemplatesRejectLinksAndDevices()
    {
        var root = NewRoot();
        try
        {
            var jobs = Path.Combine(root, "storage", "print-jobs");
            Directory.CreateDirectory(jobs);
            var stale = Path.Combine(jobs, Guid.NewGuid().ToString("N") + ".txt");
            await File.WriteAllTextAsync(stale, "Public abandoned job");
            await using var printer = new NomadNetPrinter(root);
            printer.Start();
            Assert.True((await printer.PrintMessageAsync(Settings(root), Message, null, DateTimeOffset.UnixEpoch)).Success);
            Assert.False(File.Exists(stale));
            var template = Path.Combine(root, "template");
            await File.WriteAllTextAsync(template, "{mbody}");
            Assert.Equal("{mbody}", await NomadNetPrintingPolicy.ReadTemplateAsync(template));
            await Assert.ThrowsAnyAsync<Exception>(() => NomadNetPrintingPolicy.ReadTemplateAsync(root));
            await File.WriteAllBytesAsync(template, [0xff]);
            await Assert.ThrowsAsync<DecoderFallbackException>(() => NomadNetPrintingPolicy.ReadTemplateAsync(template));
            await File.WriteAllTextAsync(template, new string('a', NomadNetPrintingPolicy.MaximumTemplateBytes + 1));
            await Assert.ThrowsAsync<InvalidDataException>(() => NomadNetPrintingPolicy.ReadTemplateAsync(template));
            if (OperatingSystem.IsLinux())
            {
                var link = Path.Combine(root, "link");
                File.CreateSymbolicLink(link, template);
                await Assert.ThrowsAsync<IOException>(() => NomadNetPrintingPolicy.ReadTemplateAsync(link));
                var profile = Path.Combine(root, "symlinked");
                Directory.CreateDirectory(profile);
                Directory.CreateSymbolicLink(Path.Combine(profile, "storage"), Path.Combine(root, "captures"));
                await using var linked = new NomadNetPrinter(profile);
                linked.Start();
                Assert.False((await linked.PrintMessageAsync(Settings(root), Message, null, DateTimeOffset.UnixEpoch)).Success);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task WaitForFileAsync(string path)
    {
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(path) && deadline.Elapsed < TimeSpan.FromSeconds(12)) await Task.Delay(25);
        Assert.True(File.Exists(path), "Expected process boundary marker: " + path);
    }

    private static void AssertProcessExited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var deadline = Stopwatch.StartNew();
            while (!process.HasExited && deadline.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(20);
                process.Refresh();
            }
            Assert.True(process.HasExited, OperatingSystem.IsLinux() && File.Exists($"/proc/{pid}/status")
                ? File.ReadAllText($"/proc/{pid}/status") : "Printer process remains active: " + pid);
        }
        catch (ArgumentException) { }
    }

    [UnsupportedPrintFact]
    public async Task UnsupportedPlatformRefusesPrintBeforeStartingAProcessOrCreatingAWorkspace()
    {
        var root = NewRoot();
        try
        {
            await using var printer = new NomadNetPrinter(root);
            printer.Start();
            var result = await printer.PrintMessageAsync(Settings(root), Message, null, DateTimeOffset.UnixEpoch);
            Assert.False(result.Success);
            Assert.Equal("Printing requires Windows or Linux process ownership.", result.Detail);
            Assert.False(File.Exists(Path.Combine(root, "marker")));
            Assert.False(Directory.Exists(Path.Combine(root, "storage", "print-jobs")));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class SupportedPrintFactAttribute : FactAttribute
    {
        public SupportedPrintFactAttribute()
        {
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
                Skip = "Printing process ownership is implemented on Windows and Linux only.";
        }
    }

    private sealed class SupportedPrintTheoryAttribute : TheoryAttribute
    {
        public SupportedPrintTheoryAttribute()
        {
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
                Skip = "Printing process ownership is implemented on Windows and Linux only.";
        }
    }

    private sealed class UnsupportedPrintFactAttribute : FactAttribute
    {
        public UnsupportedPrintFactAttribute()
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
                Skip = "Verifies the explicit unsupported-platform printing refusal.";
        }
    }

    private sealed class WindowsPrintJobTheoryAttribute : TheoryAttribute
    {
        public WindowsPrintJobTheoryAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "Windows suspended-start job ownership and locked-file semantics; Linux lifecycle cases run separately.";
        }

    }

    private sealed class LinuxPrintJobTheoryAttribute : TheoryAttribute
    {
        public LinuxPrintJobTheoryAttribute()
        {
            if (!OperatingSystem.IsLinux()) Skip = "Linux private supervisor and child-subreaper ownership.";
        }
    }
}
