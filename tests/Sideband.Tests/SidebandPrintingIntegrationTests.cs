using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.AccessControl;
using System.Security.Principal;
using LXMF;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandPrintingIntegrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    [Trait("TestId", "T1014")]
    public async Task LegacyStateAndPassiveImportedCommandsCannotActivateOrReplaceALocalExecutable()
    {
        var root = NewRoot();
        try
        {
            var initial = new SidebandStateStore(root);
            var message = await SeedAsync(initial);
            var legacy = JsonNode.Parse(await File.ReadAllTextAsync(initial.StatePath, Token))!.AsObject();
            Assert.True(legacy.Remove("Printing"));
            await File.WriteAllTextAsync(initial.StatePath, legacy.ToJsonString(), Token);
            var store = new SidebandStateStore(root);
            Assert.Equal(new SidebandPrintingConfiguration(), (await store.LoadAsync(cancellationToken: Token)).Printing);
            var imported = Path.Combine(root, "untrusted-settings.json");
            await File.WriteAllTextAsync(imported, """{"print_command":"untrusted-command --unsafe"}""", Token);
            await store.ImportSettingsAsync(imported, Token);
            Assert.Equal(new SidebandPrintingConfiguration(), (await store.LoadAsync(cancellationToken: Token)).Printing);
            await using var printer = new SidebandPrintingService(store);
            printer.Start();
            await Assert.ThrowsAsync<InvalidOperationException>(() => printer.PrintPaperAsync(message.Id, "lp", true, Token));
            await store.ConfigurePrintingAsync(true, "reviewed-local-command", true, Token);
            await store.ImportSettingsAsync(imported, Token);
            Assert.Equal(new SidebandPrintingConfiguration(true, "reviewed-local-command"),
                (await new SidebandStateStore(root).LoadAsync(cancellationToken: Token)).Printing);
            Assert.False(Directory.Exists(Path.Combine(root, "storage", "print-jobs")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("TestId", "T1002")]
    public async Task ActivationConsentAndOriginalMessageOwnershipAreRequiredAcrossRestart()
    {
        var root = NewRoot();
        try
        {
            var store = new SidebandStateStore(root);
            var message = await SeedAsync(store);
            await using var client = new SidebandClient(store);
            await using var service = client.CreatePrintingService();
            service.Start();
            Assert.False((await client.LoadAsync(Token)).Printing.Enabled);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrintPaperAsync(message.Id, "lp", false, Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrintPaperAsync(message.Id, "lp", true, Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.ConfigurePrintingAsync(true, "lp", false, Token));
            await client.ConfigurePrintingAsync(true, "synthetic-printer", true, Token);
            Assert.True((await new SidebandStateStore(root).LoadAsync(cancellationToken: Token)).Printing.Enabled);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrintPaperAsync(message.Id, "lp", true, Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrintPaperAsync(Guid.NewGuid(), "synthetic-printer", true, Token));
            foreach (var changed in new[] {
                         message with { Id = Guid.NewGuid(), Incoming = true, PaperUri = null, DeliveryMethod = "Direct", DeliveryState = "Received" },
                         message with { Id = Guid.NewGuid(), ImportedPaperEnvelope = true },
                         message with { Id = Guid.NewGuid(), DeliveryMethod = "Direct", PaperUri = null } })
            {
                await store.AddMessageAsync(changed, cancellationToken: Token);
                await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrintPaperAsync(changed.Id, "synthetic-printer", true, Token));
            }
            await client.ConfigurePrintingAsync(false, "lp", false, Token);
            Assert.False((await new SidebandStateStore(root).LoadAsync(cancellationToken: Token)).Printing.Enabled);
            Assert.False(client.IsConnected);
            Assert.False(Directory.Exists(Path.Combine(root, "storage", "print-jobs")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("TestId", "T1003")]
    public async Task RealPrivateSoftwareSinkReceivesOneExactPngPathAndDoesNotMutateHistory()
    {
        var root = NewRoot();
        try
        {
            var store = new SidebandStateStore(root);
            var message = await SeedAsync(store);
            var command = Command(root, "capture");
            await store.ConfigurePrintingAsync(true, command, true, Token);
            var before = await File.ReadAllBytesAsync(store.StatePath, Token);
            await using var printer = new SidebandPrintingService(store);
            printer.Start();
            var result = await printer.PrintPaperAsync(message.Id, command, true, TestContext.Current.CancellationToken);
            Assert.True(result.Success, result.Detail);
            using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(
                Directory.GetFiles(Path.Combine(root, "captures"), "*.json").Single(), Token));
            var original = receipt.RootElement.GetProperty("original").GetString()!;
            var copy = receipt.RootElement.GetProperty("copy").GetString()!;
            Assert.Contains("public paper ", original, StringComparison.Ordinal);
            Assert.False(File.Exists(original));
            Assert.Equal("public literal ; $(ignored)", receipt.RootElement.GetProperty("extra").GetString());
            Assert.Equal(LxPaperQr.Encode(message.PaperUri!, cancellationToken: Token).ToPng(cancellationToken: Token),
                await File.ReadAllBytesAsync(copy, Token));
            Assert.Equal(before, await File.ReadAllBytesAsync(store.StatePath, Token));
            var jobs = Path.Combine(root, "storage", "print-jobs");
            Assert.Empty(Directory.GetFiles(jobs));
            if (OperatingSystem.IsWindows())
            {
                using var identity = WindowsIdentity.GetCurrent();
                var acl = new DirectoryInfo(jobs).GetAccessControl();
                Assert.True(acl.AreAccessRulesProtected);
                var rule = Assert.Single(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
                Assert.Equal(identity.User, rule.IdentityReference);
                Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("fail", "code 7")]
    [InlineData("fail125", "code 125")]
    [InlineData("missing", "could not start")]
    [Trait("TestId", "T1004")]
    public async Task SidebandReportsProcessFailuresAndNextHealthyJobRecovers(string mode, string expected)
    {
        var root = NewRoot();
        try
        {
            var store = new SidebandStateStore(root);
            var message = await SeedAsync(store);
            var command = mode == "missing" ? Quote(Path.Combine(root, "no-such-printer.exe")) : Command(root, mode);
            await store.ConfigurePrintingAsync(true, command, true, Token);
            await using var printer = new SidebandPrintingService(store);
            printer.Start();
            var result = await printer.PrintPaperAsync(message.Id, command, true, TestContext.Current.CancellationToken);
            Assert.False(result.Success);
            Assert.Contains(expected, result.Detail, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "storage", "print-jobs")));
            var healthy = Command(root, "capture");
            await store.ConfigurePrintingAsync(true, healthy, true, Token);
            Assert.True((await printer.PrintPaperAsync(message.Id, healthy, true, TestContext.Current.CancellationToken)).Success);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("TestId", "T1005")]
    public async Task QueuedOwnershipAndConcurrentDisposalCancelAndJoinAllAppOperations()
    {
        var root = NewRoot();
        try
        {
            var store = new SidebandStateStore(root);
            var message = await SeedAsync(store);
            var command = Command(root, "hang");
            await store.ConfigurePrintingAsync(true, command, true, Token);
            var printer = new SidebandPrintingService(store);
            printer.Start();
            var jobs = Enumerable.Range(0, SidebandPrintingService.QueueCapacity)
                .Select(_ => printer.PrintPaperAsync(message.Id, command, true, Token)).ToArray();
            await Assert.ThrowsAsync<InvalidOperationException>(() => printer.PrintPaperAsync(message.Id, command, true, Token));
            await Task.WhenAll(printer.DisposeAsync().AsTask(), printer.DisposeAsync().AsTask())
                .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            foreach (var job in jobs) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => printer.PrintPaperAsync(message.Id, command, true, Token));
            Assert.Throws<ObjectDisposedException>(printer.Start);
            if (Directory.Exists(Path.Combine(root, "storage", "print-jobs")))
                Assert.Empty(Directory.GetFiles(Path.Combine(root, "storage", "print-jobs")));
        }
        finally { Directory.Delete(root, true); }
    }

    internal static async Task<SidebandChatMessage> SeedAsync(SidebandStateStore store)
    {
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-paper.json"), Token));
        var paper = fixture.RootElement.GetProperty("cases")[0];
        var message = new SidebandChatMessage(Guid.NewGuid(), fixture.RootElement.GetProperty("destination").GetString()!,
            false, "", paper.GetProperty("content").GetString()!, DateTimeOffset.UnixEpoch, "Paper",
            DeliveryMethod: "Paper", PaperUri: paper.GetProperty("uri").GetString());
        await store.AddContactAsync(SidebandContact.Create("Public synthetic paper peer", message.PeerDeliveryHash), Token);
        await store.AddMessageAsync(message, cancellationToken: Token);
        return message;
    }

    [Fact]
    [Trait("TestId", "T1013")]
    public async Task AnotherApplicationOwnerCannotRecoverOrDeleteAnActiveProfilesPrivateJob()
    {
        var root = NewRoot();
        try
        {
            var store = new SidebandStateStore(root);
            var message = await SeedAsync(store);
            var command = Command(root, "hang");
            await store.ConfigurePrintingAsync(true, command, true, Token);
            await using var first = new SidebandPrintingService(store);
            await using var second = new SidebandPrintingService(new(root));
            first.Start();
            second.Start();
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
            var active = first.PrintPaperAsync(message.Id, command, true, cancel.Token);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(Path.Combine(root, "marker")))
                await Task.Delay(20, deadline.Token);
            var original = Assert.Single(Directory.GetFiles(Path.Combine(root, "storage", "print-jobs")));
            var denied = await second.PrintPaperAsync(message.Id, command, true, Token);
            Assert.False(denied.Success);
            Assert.Contains("Another printer owns", denied.Detail, StringComparison.Ordinal);
            Assert.True(File.Exists(original));
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
            await first.DisposeAsync();
            var healthy = Command(root, "capture");
            await new SidebandStateStore(root).ConfigurePrintingAsync(true, healthy, true, Token);
            // A new service observes the persisted current policy, independently of another store's cache.
            await using var recovery = new SidebandPrintingService(new(root));
            recovery.Start();
            Assert.True((await recovery.PrintPaperAsync(message.Id, healthy, true, Token)).Success);
        }
        finally { Directory.Delete(root, true); }
    }

    internal static string Command(string root, string mode) =>
        $"{Quote(Environment.GetEnvironmentVariable("DOTRETICULUM_PWSH") ?? "pwsh")} -NoProfile -File " +
        $"{Quote(Path.GetFullPath(Path.Combine(SourceDirectory(), "..", "Pester", "NomadNetPrinterStub.ps1")))} " +
        $"-Mode {mode} -Output {Quote(Path.Combine(root, "captures"))} -Marker {Quote(Path.Combine(root, "marker"))} " +
        "-Extra 'public literal ; $(ignored)'";
    private static string SourceDirectory([System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        Path.GetDirectoryName(source)!;
    internal static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    internal static string NewRoot()
    {
        var root = Path.GetFullPath(Path.Combine("artifacts", "tests", "sideband-printing", "public paper " + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        return root;
    }
}
