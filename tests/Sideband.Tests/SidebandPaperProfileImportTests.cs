using DotReticulum.Core;
using LXMF;
using Sideband.Desktop;

namespace Sideband.Tests;

public sealed partial class SidebandProfileImportTests
{
    [Fact]
    public async Task OutgoingPaperProfileRestoresExactOfflineExportsAndProvenanceAfterRestart()
    {
        using var profile = new Profile();
        var uri = AddPaperMessage(profile);
        var before = profile.Fingerprint();
        var result = await SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, TestContext.Current.CancellationToken);
        Assert.Equal(before, profile.Fingerprint());
        Assert.Contains(result.Notices, notice => notice.Contains("not authenticated", StringComparison.Ordinal));
        for (var restart = 0; restart < 2; restart++)
        {
            var message = Assert.Single((await new SidebandStateStore(profile.Target)
                .LoadAsync(cancellationToken: TestContext.Current.CancellationToken)).Messages);
            Assert.True(message.ImportedPaperEnvelope);
            Assert.Equal(uri, message.PaperUri);
            Assert.False(message.Incoming);
            Assert.Equal("Paper", message.DeliveryMethod);
            using var output = new StringWriter();
            var path = Path.Combine(profile.Root, $"paper-{restart}.txt");
            Assert.Equal(0, await PaperExportCommand.RunAsync(profile.Target, message.Id, path, false, output,
                TestContext.Current.CancellationToken));
            Assert.Equal(uri, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            Assert.Contains("not authenticated", output.ToString());
            await Assert.ThrowsAsync<IOException>(() => PaperExportCommand.RunAsync(profile.Target, message.Id, path, false,
                output, TestContext.Current.CancellationToken));
            Assert.Equal(uri, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData("destination")]
    [InlineData("oversized")]
    [InlineData("trailing")]
    [InlineData("missing")]
    public async Task InvalidPaperEnvelopeRollsBackWholeImportAndHealthyRetrySucceeds(string scenario)
    {
        using var profile = new Profile();
        AddPaperMessage(profile, scenario);
        var before = profile.Fingerprint();
        await Assert.ThrowsAsync<InvalidDataException>(() => SidebandProfileImporter.ImportAsync(profile.Source, profile.Target,
            TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(profile.Target));
        Assert.Equal(before, profile.Fingerprint());
        WriteSql(Path.Combine(profile.Source, "sideband.db"), "DELETE FROM lxm; DELETE FROM conv");
        var uri = AddPaperMessage(profile);
        await SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, TestContext.Current.CancellationToken);
        Assert.Equal(uri, Assert.Single((await new SidebandStateStore(profile.Target)
            .LoadAsync(cancellationToken: TestContext.Current.CancellationToken)).Messages).PaperUri);
    }

    [Fact]
    public async Task ImportedPaperEnvelopeMarkerWithoutExportIsRejectedWithoutReplacingState()
    {
        using var profile = new Profile();
        var store = new SidebandStateStore(profile.Target);
        var before = await store.LoadAsync(cancellationToken: TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => store.AddMessageAsync(new(Guid.NewGuid(), new string('1', 32),
            false, "", "No envelope", DateTimeOffset.UtcNow, "Paper", DeliveryMethod: "Paper", ImportedPaperEnvelope: true),
            TestContext.Current.CancellationToken));
        var invalid = Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.Contains("imported paper-envelope marker", invalid.Message);
        Assert.Empty((await store.LoadAsync(cancellationToken: TestContext.Current.CancellationToken)).Messages);
        Assert.Equal(before.OwnDeliveryHash, (await store.LoadAsync(cancellationToken: TestContext.Current.CancellationToken)).OwnDeliveryHash);
    }

    [Fact]
    public async Task IncomingPaperHistoryPreservesPlaintextButDoesNotActivateAnOutgoingExport()
    {
        using var profile = new Profile();
        AddPaperMessage(profile, incoming: true);
        var result = await SidebandProfileImporter.ImportAsync(profile.Source, profile.Target, TestContext.Current.CancellationToken);
        var message = Assert.Single((await new SidebandStateStore(profile.Target)
            .LoadAsync(cancellationToken: TestContext.Current.CancellationToken)).Messages);
        Assert.True(message.Incoming);
        Assert.Null(message.PaperUri);
        Assert.False(message.ImportedPaperEnvelope);
        Assert.Equal("Historical paper", message.Content);
        Assert.Contains(result.Notices, notice => notice.Contains("incoming paper envelope remains archive-only", StringComparison.Ordinal));
    }

    private static string AddPaperMessage(Profile profile, string? invalid = null, bool incoming = false)
    {
        using var own = IdentityFile.Load(Path.Combine(profile.Source, "primary_identity"));
        using var peer = Identity.Generate();
        var destination = LxMessage.DeliveryDestinationHash(peer);
        var message = LxMessage.Create(incoming ? LxMessage.DeliveryDestinationHash(own) : destination,
            incoming ? peer : own, "Historical paper", method: LxmMethod.Paper, timestamp: 1760000000.25);
        var wire = message.Pack(destinationIdentity: incoming ? own : peer);
        var uri = message.AsUri();
        byte[]? envelope = message.PaperPacked!.ToArray();
        if (invalid == "destination") envelope[0] ^= 1;
        if (invalid == "oversized") envelope = new byte[LxMessage.PaperMdu + 1];
        if (invalid == "missing") envelope = null;
        var stored = MsgPack.Pack(new object?[] { wire, envelope });
        if (invalid == "trailing") stored = [.. stored, 0];
        WriteSql(Path.Combine(profile.Source, "sideband.db"),
            $"INSERT INTO conv VALUES(X'{Convert.ToHexString(destination)}',0,0,0,1,0,X'5061706572',X'C0');" +
            $"INSERT INTO lxm VALUES(X'{Convert.ToHexString(message.Hash!)}',X'{Convert.ToHexString(message.DestinationHash)}'," +
            $"X'{Convert.ToHexString(message.SourceHash)}',X'',0,0,5,5,1,0,X'{Convert.ToHexString(stored)}',X'80')");
        return uri;
    }
}
