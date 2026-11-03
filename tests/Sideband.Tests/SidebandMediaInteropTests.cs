using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LXMF.Tests.Interop;

namespace Sideband.Tests;

public sealed partial class SidebandLiveInteropTests
{
    public static bool ApplicationMediaIsDisabled => ApplicationInteropIsDisabled ||
        Environment.GetEnvironmentVariable("DOTLXST_NATIVE") != "1";

    [Theory(SkipWhen = nameof(ApplicationMediaIsDisabled), Skip = "Actual Sideband media requires application live mode, pinned sources and DOTLXST_NATIVE=1 with installed codecs.")]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Live")]
    public async Task ActualPrimarySidebandMediaRoundTripsDecodeExportRestartAndRetainTextAfterMalformedImage(bool propagated)
    {
        var token = TestContext.Current.CancellationToken;
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "sideband-media.json"), token));
        var imageVector = fixture.RootElement.GetProperty("images")[0];
        var opusVector = fixture.RootElement.GetProperty("opus");
        var codec2Vector = fixture.RootElement.GetProperty("codec2").EnumerateArray().Single(row => row.GetProperty("mode").GetInt32() == 8);
        var root = Path.Combine(AppContext.BaseDirectory, "sideband-media-live-" + Guid.NewGuid().ToString("N"));
        var rns = Path.Combine(root, "rns");
        var data = Path.Combine(root, "data");
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        Directory.CreateDirectory(rns);
        await File.WriteAllTextAsync(Path.Combine(rns, "config"),
            $"[reticulum]\n  enable_transport = No\n  share_instance = No\n\n[logging]\n  loglevel = 4\n\n[interfaces]\n  [[PythonMedia]]\n    type = TCPClientInterface\n    enabled = yes\n    target_host = 127.0.0.1\n    target_port = {port}\n", token);
        try
        {
            await using var propagation = propagated ? await PyLinkNode.StartAsync("SidebandMediaPropagation",
                tcpServerPort: port, harness: "sideband_live_node.py", extraArguments: ["--transport"]) : null;
            await using var python = await PyLinkNode.StartAsync("ActualSidebandMedia", tcpServerPort: propagated ? 0 : port,
                rnsdPort: propagated ? port : 0, harness: "sideband_application_node.py",
                extraArguments: ["--source", Environment.GetEnvironmentVariable("DOTRETICULUM_SIDEBAND_UPSTREAM")!]);
            var store = new SidebandStateStore(data);
            await using var client = new SidebandClient(store);
            var errors = new ConcurrentQueue<string>();
            client.ErrorReported += errors.Enqueue;
            await client.ConnectAsync(rns, token);
            var own = (await client.LoadAsync(token)).OwnDeliveryHash!;
            await python.WaitForAnnounce(Convert.FromHexString(own), TimeSpan.FromSeconds(30));
            await python.Command("announce", "announced", TimeSpan.FromSeconds(30));
            if (propagation is not null)
            {
                var ready = await propagation.Command("enable-pn", "pn-ready", TimeSpan.FromSeconds(30));
                var pn = ready.GetProperty("dest").GetString()!;
                await client.ConfigurePropagationAsync(pn, true, token);
                await python.Command("use-pn " + pn, "pn-selected", TimeSpan.FromSeconds(10));
            }

            var imagePath = Path.Combine(root, "synthetic.png");
            await File.WriteAllBytesAsync(imagePath, Convert.FromBase64String(imageVector.GetProperty("input").GetString()!), token);
            await client.SendImageAsync(python.HashHex, "", imagePath, SidebandImageQuality.LowBandwidth, token);
            var inspected = await InspectOutboundAsync(" ");
            Assert.Equal(imageVector.GetProperty("width").GetInt32(), inspected.GetProperty("image").GetProperty("width").GetInt32());
            Assert.Equal(imageVector.GetProperty("height").GetInt32(), inspected.GetProperty("image").GetProperty("height").GetInt32());
            var outgoingImage = Assert.Single(await client.GetMessagesAsync(python.HashHex, token));
            Assert.Equal(outgoingImage.Image!.Data.ContentHash, inspected.GetProperty("image").GetProperty("sha256").GetString());

            var encoded = await SidebandMediaCodec.EncodeOggOpusAsync(new(
                Enumerable.Range(0, 2400).Select(index => (float)(.2 * Math.Sin(index * .1))).ToArray(), 12000, 1), token);
            await client.SendAudioAsync(python.HashHex, "native Opus", encoded, token);
            Array.Clear(encoded.EncodedBytes);
            inspected = await InspectOutboundAsync("native Opus");
            Assert.Equal(1600, inspected.GetProperty("audio").GetProperty("decoded_samples").GetInt32());

            var pcm = Convert.FromBase64String(fixture.RootElement.GetProperty("input_pcm").GetString()!);
            var samples = new float[pcm.Length / 2];
            for (var index = 0; index < samples.Length; index++)
                samples[index] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(index * 2)) / 32767f;
            var codec2 = SidebandMediaCodec.EncodeCodec2(new(samples, 8000, 1), 8, token);
            await client.SendAudioAsync(python.HashHex, "native Codec2", codec2, token);
            inspected = await InspectOutboundAsync("native Codec2");
            Assert.Equal(codec2Vector.GetProperty("decoded_samples").GetInt32(), inspected.GetProperty("audio").GetProperty("decoded_samples").GetInt32());

            await SendIncomingAsync(new
            {
                content = "actual primary image and audio",
                image = new { format = "webp", data = imageVector.GetProperty("encoded").GetString() },
                audio = new { mode = SidebandMediaCodec.OpusOggMode, data = opusVector.GetProperty("encoded").GetString() },
            });
            var incoming = await WaitIncomingAsync("actual primary image and audio");
            Assert.NotNull(incoming.Image);
            Assert.NotNull(incoming.Audio);
            Assert.Equal(imageVector.GetProperty("sha256").GetString(), incoming.Image.Data.ContentHash);
            Assert.Equal(opusVector.GetProperty("sha256").GetString(), incoming.Audio.Data.ContentHash);
            var decoded = await client.LoadAudioAsync(python.HashHex, incoming.Id, token);
            Assert.Equal(opusVector.GetProperty("decoded_samples").GetInt32(), decoded.Samples.Length);
            Assert.NotEmpty(await client.LoadImageAsync(python.HashHex, incoming.Id, token));
            var imageExport = Path.Combine(root, "export.webp");
            var audioExport = Path.Combine(root, "export.ogg");
            await client.ExportMediaAsync(python.HashHex, incoming.Id, false, imageExport, token);
            await client.ExportMediaAsync(python.HashHex, incoming.Id, true, audioExport, token);
            Assert.Equal(incoming.Image.Data.ContentHash, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(imageExport, token))));
            Assert.Equal(incoming.Audio.Data.ContentHash, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(audioExport, token))));
            await client.DisconnectAsync();
            await client.ConnectAsync(rns, token);
            Assert.Equal(own, (await client.LoadAsync(token)).OwnDeliveryHash);
            Assert.Equal(incoming, (await client.GetMessagesAsync(python.HashHex, token)).Single(message => message.Id == incoming.Id));
            Assert.Equal(decoded.Samples.Length, (await client.LoadAudioAsync(python.HashHex, incoming.Id, token)).Samples.Length);

            await SendIncomingAsync(new
            {
                content = "text survives invalid media",
                image = new { format = "webp", data = Convert.ToBase64String("invalid image"u8) },
                audio = new { mode = 255, data = Convert.ToBase64String("unknown audio"u8) },
            });
            var invalid = await WaitIncomingAsync("text survives invalid media");
            Assert.Null(invalid.Image);
            Assert.NotNull(invalid.Audio);
            Assert.Contains(errors, error => error.StartsWith("The received image was not stored; text is retained:", StringComparison.Ordinal));
            await Assert.ThrowsAsync<NotSupportedException>(() => client.LoadAudioAsync(python.HashHex, invalid.Id, token));
            await client.ExportMediaAsync(python.HashHex, invalid.Id, true, Path.Combine(root, "unknown-audio.bin"), token);
            await SendIncomingAsync(new { content = "continued service after invalid media" });
            Assert.Null((await WaitIncomingAsync("continued service after invalid media")).Image);

            async Task<JsonElement> InspectOutboundAsync(string content)
            {
                if (propagation is not null)
                {
                    await WaitForPropagationCountAsync(propagation, 1, token);
                    await python.Command("retrieve", "retrieved", TimeSpan.FromSeconds(90));
                }
                var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
                var deadline = DateTime.UtcNow.AddSeconds(90);
                while (DateTime.UtcNow < deadline)
                {
                    var snapshot = await python.Command("snapshot " + own, "snapshot", TimeSpan.FromSeconds(10));
                    var found = snapshot.GetProperty("messages").EnumerateArray().FirstOrDefault(message =>
                        message.GetProperty("incoming").GetBoolean() && message.GetProperty("sha256").GetString() == expected);
                    if (found.ValueKind != JsonValueKind.Undefined)
                        return await python.Command($"inspect-media {own} {found.GetProperty("hash").GetString()}",
                            "media-inspected", TimeSpan.FromSeconds(30));
                    await Task.Delay(50, token);
                }
                throw new TimeoutException("The actual Sideband application did not persist the outgoing media.");
            }

            async Task SendIncomingAsync<T>(T payload)
            {
                var json = JsonSerializer.SerializeToUtf8Bytes(payload);
                await python.Command((propagated ? "send-media-propagated " : "send-media ") + own + " " + Convert.ToBase64String(json),
                    "sent_queued", TimeSpan.FromSeconds(30));
                if (propagation is not null)
                {
                    await WaitForPropagationCountAsync(propagation, 1, token);
                    Assert.Equal(1, await client.SynchronizeAsync(cancellationToken: token));
                }
            }

            async Task<SidebandChatMessage> WaitIncomingAsync(string content)
            {
                var deadline = DateTime.UtcNow.AddSeconds(90);
                while (DateTime.UtcNow < deadline)
                {
                    var found = (await client.GetMessagesAsync(python.HashHex, token)).SingleOrDefault(message => message.Incoming && message.Content == content);
                    if (found is not null) return found;
                    await Task.Delay(50, token);
                }
                throw new TimeoutException("The native client did not persist the reference media message.");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
