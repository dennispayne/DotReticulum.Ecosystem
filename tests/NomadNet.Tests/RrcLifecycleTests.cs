using DotReticulum.Applications;
using DotReticulum.Core;
using NomadNet;

namespace NomadNet.Tests;

[Trait("Category", "Integration")]
public sealed class RrcLifecycleTests
{
    [Fact]
    public async Task DisposalCancelsAnInProgressPathLookupAndCanBeRepeated()
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomadnet-rrc-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "config"),
                "[reticulum]\n share_instance = No\n[interfaces]\n");
            await using var node = ReticulumNode.Open(directory);
            using var identity = Identity.Generate();
            await node.StartAsync();
            var client = await RrcClient.OpenAsync(node, identity, directory, new string('a', 32));
            var connect = client.ConnectAsync();
            await Task.Delay(30);
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
            await client.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ConnectAsync());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
