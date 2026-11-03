using System.Security.Cryptography;
using Sideband;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandAttachmentTests
{
    [Fact]
    public async Task RestartExportChecksIntegrityRefusesOverwriteAndRejectsUnsafeMetadata()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "sideband-attachments-" + Guid.NewGuid().ToString("N"));
        try
        {
            var token = TestContext.Current.CancellationToken;
            var data = new byte[] { 0, 1, 2, 255 };
            var hash = Convert.ToHexStringLower(SHA256.HashData(data));
            Directory.CreateDirectory(Path.Combine(root, "attachments"));
            await File.WriteAllBytesAsync(Path.Combine(root, "attachments", hash), data, token);
            var store = new SidebandStateStore(root);
            var message = new SidebandChatMessage(Guid.NewGuid(), new string('a', 32), true, "", "file", DateTimeOffset.UtcNow,
                "Received", [new SidebandAttachment("example.bin", hash, data.Length)]);
            await store.AddMessageAsync(message, token);
            await using var reloaded = new SidebandClient(new SidebandStateStore(root));
            var output = Path.Combine(root, "output.bin");
            await reloaded.ExportAttachmentAsync(message.PeerDeliveryHash, message.Id, 0, output, token);
            Assert.Equal(data, await File.ReadAllBytesAsync(output, token));
            await Assert.ThrowsAsync<IOException>(() => reloaded.ExportAttachmentAsync(message.PeerDeliveryHash, message.Id, 0, output, token));
            await File.WriteAllBytesAsync(Path.Combine(root, "attachments", hash), [9, 8, 7, 6], token);
            var corruptOutput = Path.Combine(root, "corrupt.bin");
            await Assert.ThrowsAsync<InvalidDataException>(() => reloaded.ExportAttachmentAsync(message.PeerDeliveryHash, message.Id, 0, corruptOutput, token));
            Assert.False(File.Exists(corruptOutput));
            await Assert.ThrowsAsync<ArgumentException>(() => store.AddMessageAsync(message with
            {
                Attachments = [new SidebandAttachment("../escape", hash, data.Length)],
            }, token));
            await Assert.ThrowsAsync<ArgumentException>(() => store.AddMessageAsync(message with
            {
                Attachments = [new SidebandAttachment("example.bin", "../escape", data.Length)],
            }, token));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reloaded.ExportAttachmentAsync(message.PeerDeliveryHash, message.Id, 1, output, token));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task OversizeFileIsRejectedBeforeNetworkUseAndCancelledExportCreatesNoFile()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "sideband-attachments-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var token = TestContext.Current.CancellationToken;
            var source = Path.Combine(root, "oversize.bin");
            await using (var stream = File.Create(source)) stream.SetLength(2 * 1024 * 1024 + 1);
            await using var client = new SidebandClient(new SidebandStateStore(root));
            await Assert.ThrowsAsync<InvalidDataException>(() => client.SendFileAsync(new string('a', 32), "file", source, token));
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            var output = Path.Combine(root, "cancelled.bin");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ExportAttachmentAsync(new string('a', 32), Guid.NewGuid(), 0, output, cancelled.Token));
            Assert.False(File.Exists(output));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
