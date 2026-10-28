using Sideband;

namespace Sideband.Tests;

[Trait("Category", "Unit")]
public sealed class SidebandClientTests
{
    [Fact]
    public async Task SendRejectsInvalidInputBeforeNetworkIsConnected()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "sideband-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var client = new SidebandClient(new SidebandStateStore(path));
            var cancellationToken = TestContext.Current.CancellationToken;
            await Assert.ThrowsAsync<ArgumentException>(() => client.SendTextAsync("bad", "hello", cancellationToken));
            await Assert.ThrowsAsync<ArgumentException>(() => client.SendTextAsync(new string('a', 32), "", cancellationToken));
            await Assert.ThrowsAsync<ArgumentException>(() => client.SendTextAsync(new string('a', 32), new string('x', 65_537), cancellationToken));
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }
}
