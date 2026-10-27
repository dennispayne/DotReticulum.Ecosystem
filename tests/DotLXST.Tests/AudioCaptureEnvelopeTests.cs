using System.Text.Json;

namespace DotLXST.Tests;

[Trait("Category", "Unit")]
public sealed class AudioCaptureEnvelopeTests
{
    [Fact]
    public void CaptureSkipAndFrameDelayedGainRampMatchActualPrimaryLineSource()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxst-capture.json")));
        foreach (var vector in fixture.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var envelope = new AudioCaptureEnvelope(TimeSpan.FromSeconds(vector.GetProperty("skip").GetDouble()),
                TimeSpan.FromSeconds(vector.GetProperty("ease").GetDouble()), vector.GetProperty("gain").GetSingle());
            foreach (var frame in vector.GetProperty("frames").EnumerateArray())
            {
                var input = new AudioFrame(new float[] { .25f, .25f }, 8000, 1);
                var output = envelope.Process(input, TimeSpan.FromSeconds(frame.GetProperty("elapsed").GetDouble()));
                if (frame.GetProperty("sample").ValueKind == JsonValueKind.Null) Assert.Null(output);
                else
                {
                    Assert.NotNull(output);
                    Assert.Equal(frame.GetProperty("sample").GetSingle(), output.Samples.Span[0], precision: 6);
                    Assert.Equal(.25f, input.Samples.Span[0]);
                }
            }
            Assert.Throws<ArgumentOutOfRangeException>(() => envelope.Process(new(new float[] { 0 }, 8000, 1), TimeSpan.Zero));
        }
    }
}
