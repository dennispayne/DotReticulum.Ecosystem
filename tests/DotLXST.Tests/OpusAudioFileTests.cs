using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace DotLXST.Tests;

/// <summary>Actual primary LXST 0.4.5 source/sink fixture, SHA 3b596f48ddd252ddc9c315ef8f3020cfd3688223.</summary>
[Trait("Category", "Integration")]
public sealed class OpusAudioFileTests
{
    private sealed class ControlledSink : IAudioSink
    {
        public int Count;
        public TaskCompletionSource? Hold;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Count);
            Entered.TrySetResult();
            if (Hold is not null) await Hold.Task;
        }
    }

    [NativeCodecFact]
    public async Task PlayerLoopsRestartsReportsObserversAndEveryDisposerWaitsForTheExternalSink()
    {
        using var fixture = LoadFixture();
        var root = CreateDirectory();
        try
        {
            var path = Path.Combine(root, "player.opus");
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(fixture.RootElement.GetProperty("file_base64").GetString()!));
            var sink = new ControlledSink();
            var player = new FilePlayer(sink, path);
            var completions = 0;
            var errors = 0;
            player.Error = (_, _) => errors++;
            player.Finished += _ => throw new InvalidOperationException("test observer");
            player.Finished += _ => { Assert.False(player.IsPlaying); completions++; };
            player.Play();
            await player.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, completions);
            Assert.Equal(1, errors);
            var firstCount = sink.Count;
            player.SetLoop(true);
            player.Play();
            await LiveStreamTests.WaitForAsync(() => sink.Count > firstCount * 2);
            player.SetLoop(false);
            await player.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            sink.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
            sink.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            player.Play();
            await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Throws<InvalidOperationException>(() => player.SetSource(path));
            var first = player.DisposeAsync().AsTask();
            var second = player.DisposeAsync().AsTask();
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            sink.Hold.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Throws<ObjectDisposedException>(() => player.Play());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [NativeCodecFact]
    public async Task RecorderFinalizesOnStopAndSupportsSourceAndOutputReplacement()
    {
        using var fixture = LoadFixture();
        var root = CreateDirectory();
        try
        {
            var input = Path.Combine(root, "input.opus");
            var output = Path.Combine(root, "output.opus");
            var nextOutput = Path.Combine(root, "next.opus");
            await File.WriteAllBytesAsync(input, Convert.FromBase64String(fixture.RootElement.GetProperty("file_base64").GetString()!));
            await using var source = new OpusFileSource(input);
            await using var recorder = new FileRecorder(source, output);
            recorder.Record();
            await recorder.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            await using (var recorded = new OpusFileSource(output))
                Assert.True(recorded.SamplesPerChannel > source.SamplesPerChannel);
            await using var looping = new OpusFileSource(input, loop: true, timed: true);
            recorder.SetSource(looping);
            recorder.SetOutputPath(nextOutput);
            recorder.Record();
            await LiveStreamTests.WaitForAsync(() => File.Exists(nextOutput));
            Assert.Throws<InvalidOperationException>(() => recorder.SetOutputPath(output));
            await Task.WhenAll(recorder.StopAsync().AsTask(), recorder.StopAsync().AsTask());
            Assert.False(recorder.IsRecording);
            await Task.WhenAll(recorder.DisposeAsync().AsTask(), recorder.DisposeAsync().AsTask());
            await using var stillOwnedByCaller = source.CaptureAsync().GetAsyncEnumerator();
            Assert.True(await stillOwnedByCaller.MoveNextAsync());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [NativeCodecFact]
    public async Task ActualReferenceRecordingDecodesWithIdenticalFramesAndSamples()
    {
        using var fixture = LoadFixture();
        var root = CreateDirectory();
        try
        {
            var path = Path.Combine(root, "reference.opus");
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(fixture.RootElement.GetProperty("file_base64").GetString()!));
            await using var source = new OpusFileSource(path);
            Assert.Equal(fixture.RootElement.GetProperty("channels").GetInt32(), source.Channels);
            var lengths = new List<int>();
            var samples = new List<float>();
            await foreach (var frame in source.CaptureAsync())
            {
                lengths.Add(frame.SamplesPerChannel);
                samples.AddRange(frame.Samples.ToArray());
            }
            Assert.Equal(fixture.RootElement.GetProperty("frame_lengths").EnumerateArray().Select(value => value.GetInt32()), lengths);
            Assert.Equal(fixture.RootElement.GetProperty("decoded").EnumerateArray().Select(value => value.GetSingle()), samples);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [NativeCodecFact]
    public async Task LoopCancellationMalformedInputAndConcurrentDisposalReleaseNativeFiles()
    {
        using var fixture = LoadFixture();
        var root = CreateDirectory();
        try
        {
            var path = Path.Combine(root, "loop.opus");
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(fixture.RootElement.GetProperty("file_base64").GetString()!));
            await using var source = new OpusFileSource(path, loop: true);
            using var stop = new CancellationTokenSource();
            await using var frames = source.CaptureAsync(stop.Token).GetAsyncEnumerator();
            var count = 0L;
            while (count <= source.SamplesPerChannel)
            {
                Assert.True(await frames.MoveNextAsync());
                count += frames.Current.SamplesPerChannel;
            }
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => frames.MoveNextAsync().AsTask());
            await Task.WhenAll(source.DisposeAsync().AsTask(), source.DisposeAsync().AsTask());
            await using var paced = new OpusFileSource(path, loop: true, timed: true);
            await using var pacedFrames = paced.CaptureAsync().GetAsyncEnumerator();
            Assert.True(await pacedFrames.MoveNextAsync());
            var waiting = pacedFrames.MoveNextAsync().AsTask();
            await Task.WhenAll(paced.DisposeAsync().AsTask(), paced.DisposeAsync().AsTask());
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            var malformed = Path.Combine(root, "invalid.opus");
            await File.WriteAllBytesAsync(malformed, [1, 2, 3]);
            Assert.Throws<IOException>(() => new OpusFileSource(malformed));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [LxstLiveFact]
    [Trait("Category", "Live")]
    public async Task ActualPythonFileSourceAcceptsNativeRecordingWithIdenticalDecodedSamples()
    {
        var root = CreateDirectory();
        try
        {
            var path = Path.Combine(root, "native.opus");
            var sink = new OpusFileSink(path);
            await using (sink)
            {
                for (var index = 0; index < 12; index++)
                {
                    var samples = Enumerable.Range(index * 960, 960)
                        .SelectMany(sample => Enumerable.Repeat((float)(.15 * Math.Sin(2 * Math.PI * 440 * sample / 48000)), 2)).ToArray();
                    await sink.WriteAsync(new(samples, 48000, 2));
                }
            }
            await Assert.ThrowsAsync<ObjectDisposedException>(() => sink.WriteAsync(new(new float[1920], 48000, 2)).AsTask());
            await using var source = new OpusFileSource(path);
            var bytes = new List<byte>();
            await foreach (var frame in source.CaptureAsync())
            {
                var encoded = new byte[frame.Samples.Length * sizeof(float)];
                for (var i = 0; i < frame.Samples.Length; i++)
                    BinaryPrimitives.WriteSingleLittleEndian(encoded.AsSpan(i * sizeof(float)), frame.Samples.Span[i]);
                bytes.AddRange(encoded);
            }
            var observed = await ObservePythonFileAsync(path);
            Assert.Equal(21120, observed.GetProperty("samples_per_channel").GetInt64());
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray())), observed.GetProperty("decoded_sha256").GetString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static JsonDocument LoadFixture() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxst-file-vectors.json")));

    private static string CreateDirectory()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "artifacts", "lxst-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<JsonElement> ObservePythonFileAsync(string path)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTRETICULUM_PYTHON") ??
            throw new InvalidOperationException("Set DOTRETICULUM_PYTHON to the pinned interpreter."))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "GenerateLxstFileVectors.py"), "--read-input", path })
            start.ArgumentList.Add(argument);
        var roots = new List<string>
        {
            Environment.GetEnvironmentVariable("DOTRETICULUM_LXST_UPSTREAM") ?? throw new InvalidOperationException("Set DOTRETICULUM_LXST_UPSTREAM."),
            Environment.GetEnvironmentVariable("DOTRETICULUM_UPSTREAM") ?? throw new InvalidOperationException("Set DOTRETICULUM_UPSTREAM."),
        };
        if (Environment.GetEnvironmentVariable("DOTRETICULUM_LXMF_UPSTREAM") is { Length: > 0 } lxmf) roots.Add(lxmf);
        start.Environment["PYTHONPATH"] = string.Join(Path.PathSeparator, roots);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await error);
            var json = (await output).Split('\n').Single(line => line.StartsWith("@@", StringComparison.Ordinal));
            using var document = JsonDocument.Parse(json[2..]);
            return document.RootElement.Clone();
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }
}
