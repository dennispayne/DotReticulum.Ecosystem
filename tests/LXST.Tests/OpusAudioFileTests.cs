using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace LXST.Tests;

/// <summary>Actual primary LXST 0.5.4 source/sink fixture, SHA eba5abc77fef7fef3bcb6c9c091c80b7f7492c9f.</summary>
[Trait("Category", "Integration")]
public sealed class OpusAudioFileTests
{
    [NativeCodecFact]
    public async Task PlayerNotificationMinimumMatchesActualClockAndDisposalCancelsItsWait()
    {
        using var completion = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "lxst-file-player-completion.json")));
        var minimum = TimeSpan.FromSeconds(completion.RootElement.GetProperty("completion")[0]
            .GetProperty("sleeps")[0].GetDouble());
        using var fixture = LoadFixture();
        var root = CreateDirectory();
        try
        {
            var path = Path.Combine(root, "notification.opus");
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(fixture.RootElement.GetProperty("file_base64").GetString()!));
            foreach (var cancel in new[] { false, true })
            {
                var clock = new CompletionClock();
                var output = new ControlledSink();
                await using var player = new FilePlayer(output, path, timeProvider: clock);
                var callbacks = 0;
                player.Finished += _ => callbacks++;
                player.Play();
                await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(player.IsPlaying);
                Assert.Equal(fixture.RootElement.GetProperty("frame_lengths").GetArrayLength(), output.Count);
                Assert.Equal(minimum, clock.RequestedDelay);
                Assert.Equal(0, callbacks);
                if (cancel) await player.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                else
                {
                    clock.Advance(minimum - TimeSpan.FromMilliseconds(1));
                    Assert.False(player.Completion.IsCompleted);
                    Assert.Equal(0, callbacks);
                    clock.Advance(TimeSpan.FromMilliseconds(1));
                    await player.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                }
                Assert.Equal(1, callbacks);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class CompletionClock : TimeProvider
    {
        private readonly object _gate = new();
        private long _ticks;
        private readonly List<Timer> _timers = [];
        internal TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TimeSpan RequestedDelay;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                var timer = new Timer(this, callback, state);
                _timers.Add(timer);
                timer.Change(dueTime, period);
                RequestedDelay = dueTime;
                TimerCreated.TrySetResult();
                return timer;
            }
        }
        internal void Advance(TimeSpan amount)
        {
            Timer[] ready;
            lock (_gate)
            {
                _ticks += amount.Ticks;
                ready = _timers.Where(timer => timer.Due <= _ticks).ToArray();
                foreach (var timer in ready) timer.Due = long.MaxValue;
            }
            foreach (var timer in ready) timer.Callback(timer.State);
        }
        private sealed class Timer(CompletionClock clock, TimerCallback callback, object? state) : ITimer
        {
            internal long Due = long.MaxValue;
            internal TimerCallback Callback { get; } = callback;
            internal object? State { get; } = state;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._gate) Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._ticks + dueTime.Ticks;
                return true;
            }
            public void Dispose() { lock (clock._gate) Due = long.MaxValue; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    [NativeCodecFact]
    public async Task PlayerCompletionMatchesActualQueuedPlaybackBeforeCallbacksAndRelease()
    {
        using var completion = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "lxst-file-player-completion.json")));
        Assert.Equal("eba5abc77fef7fef3bcb6c9c091c80b7f7492c9f",
            completion.RootElement.GetProperty("revision").GetString());
        Assert.False(completion.RootElement.GetProperty("source").GetProperty("input_timed").GetBoolean());
        Assert.True(completion.RootElement.GetProperty("source").GetProperty("old_source_running_after_replacement").GetBoolean());
        var queuedState = completion.RootElement.GetProperty("states").EnumerateArray().Single(row =>
            row.GetProperty("source_present").GetBoolean() && !row.GetProperty("source_running").GetBoolean() &&
            row.GetProperty("queued").GetInt32() == 1);
        using var fixture = LoadFixture();
        var root = CreateDirectory();
        try
        {
            var path = Path.Combine(root, "queued.opus");
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(fixture.RootElement.GetProperty("file_base64").GetString()!));
            foreach (var row in completion.RootElement.GetProperty("completion").EnumerateArray())
            {
                var output = new ControlledSink
                {
                    Hold = new(TaskCreationOptions.RunContinuationsAsynchronously), RespectCancellation = true,
                    HoldFromCount = fixture.RootElement.GetProperty("frame_lengths").GetArrayLength() - 1
                };
                await using var buffered = new LXST.Audio.BufferedAudioSink(output, 48000, 2);
                var probe = new QueuedSinkProbe(buffered);
                await using var player = new FilePlayer(probe, path, releaseOnFinish: row.GetProperty("release_on_finish").GetBoolean());
                var callbacks = 0;
                player.Finished += current =>
                {
                    Assert.False(current.IsPlaying);
                    Assert.Equal(row.GetProperty("events")[0].GetProperty("queued").GetInt32(), buffered.QueuedFrames);
                    callbacks++;
                };
                player.Play();
                await probe.DrainEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await output.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(queuedState.GetProperty("queued").GetInt32(), buffered.QueuedFrames);
                Assert.Equal(queuedState.GetProperty("running").GetBoolean(), player.IsPlaying);
                Assert.False(player.Completion.IsCompleted);
                Assert.Equal(0, callbacks);
                output.Hold.TrySetResult();
                await player.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, callbacks);
                Assert.Equal(0, buffered.DroppedFrames);
                if (row.GetProperty("released").GetBoolean()) Assert.Throws<ObjectDisposedException>(() => player.Play());
                await buffered.WriteBufferedAsync(new(new float[960], 48000, 2));
                await buffered.DrainAsync();
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class QueuedSinkProbe(IQueuedAudioSink sink) : IQueuedAudioSink
    {
        internal TaskCompletionSource DrainEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken = default) =>
            sink.WriteAsync(frame, cancellationToken);
        public ValueTask WriteBufferedAsync(AudioFrame frame, CancellationToken cancellationToken = default) =>
            sink.WriteBufferedAsync(frame, cancellationToken);
        public ValueTask DrainAsync(CancellationToken cancellationToken = default)
        {
            DrainEntered.TrySetResult();
            return sink.DrainAsync(cancellationToken);
        }
    }

    private sealed class ControlledSink : IAudioSink
    {
        public int Count;
        public TaskCompletionSource? Hold;
        public bool RespectCancellation;
        public int HoldFromCount = 1;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask WriteAsync(AudioFrame frame, CancellationToken cancellationToken = default)
        {
            var count = Interlocked.Increment(ref Count);
            Entered.TrySetResult();
            if (Hold is not null && count >= HoldFromCount)
            {
                Blocked.TrySetResult();
                await (RespectCancellation ? Hold.Task.WaitAsync(cancellationToken) : Hold.Task);
            }
        }
    }

    [NativeCodecFact]
    public async Task MultichannelFilesPreservePrimaryChannelsFrameBoundariesAndChannelOrder()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "lxst-multichannel-files.json")));
        Assert.Equal("eba5abc77fef7fef3bcb6c9c091c80b7f7492c9f", fixture.RootElement.GetProperty("revision").GetString());
        var root = CreateDirectory();
        try
        {
            foreach (var row in fixture.RootElement.GetProperty("cases").EnumerateArray())
            {
                var path = Path.Combine(root, "multichannel.opus");
                await File.WriteAllBytesAsync(path, Convert.FromBase64String(row.GetProperty("file_base64").GetString()!));
                await using var source = new OpusFileSource(path);
                Assert.Equal(row.GetProperty("channels").GetInt32(), source.Channels);
                Assert.Equal(row.GetProperty("samples_per_channel").GetInt64(), source.SamplesPerChannel);
                var lengths = new List<int>();
                var prefix = new List<float>();
                await foreach (var frame in source.CaptureAsync())
                {
                    lengths.Add(frame.SamplesPerChannel);
                    Assert.Equal(source.Channels, frame.Channels);
                    Assert.All(frame.Samples.ToArray(), sample => Assert.True(float.IsFinite(sample)));
                    if (prefix.Count == 0) prefix.AddRange(frame.Samples.ToArray().Take(source.Channels * 64));
                }
                Assert.Equal(row.GetProperty("frame_lengths").EnumerateArray().Select(value => value.GetInt32()), lengths);
                var expected = row.GetProperty("decoded").EnumerateArray().Select(value => value.GetSingle()).ToArray();
                var dot = expected.Zip(prefix, (left, right) => (double)left * right).Sum();
                var norm = Math.Sqrt(expected.Sum(value => (double)value * value) * prefix.Sum(value => (double)value * value));
                Assert.True(dot / norm > .99999, "Decoded channel ordering/content differs from actual primary playback.");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [NativeCodecFact]
    public async Task LoopGainDoesNotAccumulateAndChangingChannelChainsFailExplicitly()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "lxst-multichannel-files.json")));
        var root = CreateDirectory();
        try
        {
            var cases = fixture.RootElement.GetProperty("cases").EnumerateArray().ToArray();
            var first = Convert.FromBase64String(cases[0].GetProperty("file_base64").GetString()!);
            var path = Path.Combine(root, "loop.opus");
            await File.WriteAllBytesAsync(path, first);
            foreach (var row in fixture.RootElement.GetProperty("loop_gain").EnumerateArray())
            {
                var passes = row.GetProperty("pass_prefixes").EnumerateArray().ToArray();
                Assert.NotEqual(passes[0][0].GetSingle(), passes[1][0].GetSingle());
                await using var source = new OpusFileSource(path, loop: true, gainDb: row.GetProperty("gain_db").GetDouble());
                await using var frames = source.CaptureAsync().GetAsyncEnumerator();
                double? initialPower = null;
                for (var index = 0; index < 9; index++)
                {
                    Assert.True(await frames.MoveNextAsync());
                    if (index % row.GetProperty("frames_per_pass").GetInt32() != 0) continue;
                    var power = frames.Current.Samples.ToArray().Sum(sample => (double)sample * sample);
                    initialPower ??= power;
                    Assert.InRange(power / initialPower.Value, .99, 1.01);
                }
            }
            await File.WriteAllBytesAsync(path, first.Concat(Convert.FromBase64String(
                cases[1].GetProperty("file_base64").GetString()!)).ToArray());
            await using var chained = new OpusFileSource(path);
            Assert.Equal(3, fixture.RootElement.GetProperty("changing_channel_chain").GetProperty("observation")
                .GetProperty("channels").GetInt32());
            await Assert.ThrowsAsync<NotSupportedException>(async () =>
            {
                await foreach (var _ in chained.CaptureAsync()) { }
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [NativeCodecFact]
    public async Task RecordingCanExplicitlyOmitTheDefaultLxstTailWithoutTruncatingInput()
    {
        var root = CreateDirectory();
        try
        {
            foreach (var appendFinalSilence in new[] { true, false })
            {
                var path = Path.Combine(root, $"tail-{appendFinalSilence}.opus");
                await using (var sink = new OpusFileSink(path, OpusProfile.AudioLow, appendFinalSilence: appendFinalSilence))
                {
                    for (var index = 0; index < 5; index++)
                    {
                        var samples = Enumerable.Range(index * 720, 720)
                            .Select(sample => (float)(.15 * Math.Sin(2 * Math.PI * 440 * sample / 12000))).ToArray();
                        await sink.WriteAsync(new(samples, 12000, 1));
                    }
                }
                await using var source = new OpusFileSource(path);
                Assert.Equal(appendFinalSilence ? 43200 : 14400, source.SamplesPerChannel);
                Assert.Equal(1, source.Channels);
                await using var frames = source.CaptureAsync().GetAsyncEnumerator();
                Assert.True(await frames.MoveNextAsync());
                Assert.Contains(frames.Current.Samples.ToArray(), sample => Math.Abs(sample) > .05f);
            }
            var partialPath = Path.Combine(root, "partial.opus");
            await using (var sink = new OpusFileSink(partialPath, OpusProfile.AudioLow, appendFinalSilence: false))
            {
                await sink.WriteAsync(new(new float[720], 12000, 1));
                await sink.WriteAsync(new(new float[1], 12000, 1));
            }
            await using var partial = new OpusFileSource(partialPath);
            Assert.Equal(2884, partial.SamplesPerChannel);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [NativeCodecFact]
    public async Task ActualReferenceFileGainAndPlayerReleaseLeaveTheExternalSinkOwnedByTheCaller()
    {
        using var fixture = LoadFixture();
        var root = CreateDirectory();
        try
        {
            var path = Path.Combine(root, "gain.opus");
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(fixture.RootElement.GetProperty("file_base64").GetString()!));
            foreach (var vector in fixture.RootElement.GetProperty("gain_samples").EnumerateArray())
            {
                var gain = vector.GetProperty("gain_db").GetDouble();
                await using var source = new OpusFileSource(path, gainDb: gain);
                await using var frames = source.CaptureAsync().GetAsyncEnumerator();
                Assert.True(await frames.MoveNextAsync());
                Assert.Equal(vector.GetProperty("samples").EnumerateArray().Select(value => value.GetSingle()),
                    frames.Current.Samples.Span[..64].ToArray());
            }
            var sink = new ControlledSink();
            await using var player = new FilePlayer(sink, path, gainDb: -6, releaseOnFinish: true);
            player.Play();
            await player.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(player.IsPlaying);
            Assert.True(player.ReleaseOnFinish);
            Assert.Equal(-6, player.GainDb);
            Assert.Throws<ObjectDisposedException>(() => player.Play());
            await sink.WriteAsync(new(new float[960], 48000, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => player.GainDb = double.NaN);
            Assert.Throws<ArgumentOutOfRangeException>(() => new OpusFileSource(path, gainDb: double.NaN));
        }
        finally { Directory.Delete(root, recursive: true); }
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
            Assert.Equal(NativePcmReference.Expected(fixture.RootElement.GetProperty("decoded"), "file"), samples);
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
