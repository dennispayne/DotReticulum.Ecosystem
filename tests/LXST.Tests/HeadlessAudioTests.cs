namespace LXST.Tests;

[Trait("Category", "Integration")]
public sealed class HeadlessAudioTests
{
    [Fact]
    public async Task SyntheticCaptureIsSilentPacedAndDisposalCancelsWithoutOpeningDevices()
    {
        var backend = new HeadlessAudioDeviceBackend();
        await using var capture = await backend.OpenCaptureAsync(null, new(8000, 1, TimeSpan.FromMilliseconds(20)));
        await using var reader = capture.CaptureAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(160, reader.Current.Samples.Length);
        Assert.All(reader.Current.Samples.ToArray(), sample => Assert.Equal(0, sample));
        var pending = reader.MoveNextAsync().AsTask();
        var disposal = capture.DisposeAsync().AsTask();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await disposal;
        await using var sink = await backend.OpenPlaybackAsync(null, new(8000, 1, TimeSpan.FromMilliseconds(20)));
        await sink.WriteAsync(new(new float[160], 8000, 1));
        await sink.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => sink.WriteAsync(new(new float[160], 8000, 1)).AsTask());
    }
}
