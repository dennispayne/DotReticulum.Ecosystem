using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotReticulum.Applications.Rnsh;
using NomadNet;

namespace NomadNet.Tests;

[Trait("Category", "Integration")]
public sealed class NomadNetMicronGrammarNativeTests
{
    [ImageBackendFact, Trait("TestId", "T879")]
    public async Task T879ActualAsciiDecoderAppliesImagePropertiesAndRecoversAfterMalformedBytes()
    {
        using var fixture = JsonDocument.Parse(typeof(NomadNetImageTests).Assembly
            .GetManifestResourceStream("NomadNet.Tests.NomadNetImageVectors.json")!);
        var image = Convert.FromBase64String(fixture.RootElement.GetProperty("vectors")[0].GetProperty("referenceWebp").GetString()!);
        var renderer = new NomadNetImages(Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_IMAGE_CONVERTER")!);
        var descriptor = Assert.Single(MicronText.Render("`(original`w=12`h=4`a=r`/media/original.webp)"u8).Media);
        var preview = await renderer.PreviewAsync(image, columns: 40, descriptor: descriptor);
        Assert.Equal(4, preview.Split('\n').Length);
        Assert.All(preview.Split('\n'), line =>
        {
            Assert.Equal(40, line.Length);
            Assert.StartsWith(new string(' ', 28), line);
            Assert.All(line, character => Assert.Contains(character, " .:-=+*#%@"));
        });
        var percent = descriptor with { Width = "50%", Height = "25%", Alignment = "left" };
        var percentages = await renderer.PreviewAsync(image, columns: 40, descriptor: percent, terminalRows: 32);
        Assert.Equal(6, percentages.Split('\n').Length);
        Assert.All(percentages.Split('\n'), line => Assert.Equal(20, line.Length));
        await Assert.ThrowsAsync<InvalidDataException>(() => renderer.PreviewAsync("malformed"u8.ToArray(), descriptor: descriptor));
        Assert.Equal(preview, await renderer.PreviewAsync(image, columns: 40, descriptor: descriptor));
    }

    [MicronNativeFact, Trait("TestId", "T881")]
    public async Task T881ActualConptyActivatesPagePreviewConversationRrcAndImageProperties()
    {
        var binary = Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_BINARY")!;
        var root = Path.Combine(Path.GetFullPath(Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_MICRON_EVIDENCE")!),
            "styled-" + Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(root, "profile");
        var rns = Path.Combine(root, "rns");
        Directory.CreateDirectory(rns);
        await File.WriteAllTextAsync(Path.Combine(rns, "config"),
            "[reticulum]\nshare_instance=no\nenable_transport=no\n[logging]\nloglevel=0\n[interfaces]\n");
        var settings = new NomadNetSettings { ClientAnnounceAtStart = false, PeriodicLxmfSync = false, IntroTime = 0,
            ColorMode = "24bit", Glyphs = "unicode", MouseEnabled = false, HideGuide = true,
            ImageLoading = NomadNetImageLoading.Manual,
            MediaConverter = Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_IMAGE_CONVERTER")
                ?? throw new InvalidOperationException("Freeze the actual ASCII image backend."),
            RrcRenderMarkdown = false, RrcRenderMicron = true };
        await settings.SaveAsync(profile);
        const string destination = "0123456789abcdef0123456789abcdef";
        const string source = "#!fold OPEN SHUT\n>Heading-C104\n`!BOLD-C104`! `_UNDER-C104`_ `*ITALIC-C104`*\n" +
            "`FT123456FG-C104`f `BT654321BG-C104`b\n`cCENTER-C104\n`rRIGHT-C104\n`lLEFT-C104\n" +
            "`:inside\n`->Fold-C104\nHIDDEN-C104\n<end\n`(original`w=12`h=4`a=l`/media/original.webp)";
        await new NomadNetPageCache(profile).StoreAsync(destination + ":/page/styled.mu", Encoding.UTF8.GetBytes(source));
        var store = await NomadNetStore.OpenAsync(profile);
        await store.SaveMessageAsync(new NomadNetMessage(new string('a', 64), destination, "inbound", DateTimeOffset.UtcNow,
            "synthetic", "**BOLD-MESSAGE-C104** and *ITALIC-MESSAGE-C104*", "received") { Renderer = 2 });
        Directory.CreateDirectory(Path.Combine(profile, "rrc"));
        await File.WriteAllBytesAsync(Path.Combine(profile, "rrc", destination + ".cbor"), RrcCbor.Encode(new Dictionary<long, object?>
        {
            [0] = 1L, [1] = "Native", [2] = new List<object?> { "room" }, [4] = false,
            [3] = new List<object?> { new List<object?> { "msg", "room", new string('b', 32), "Reference",
                "¦!R-BOLD¦! ¦_R-UNDER¦_ ¦*R-ITALIC¦* ¦FT123456R-COLOR¦f ¦BT654321R-BG¦b", 100000L, false } },
        }));
        using var fixture = JsonDocument.Parse(typeof(NomadNetImageTests).Assembly
            .GetManifestResourceStream("NomadNet.Tests.NomadNetImageVectors.json")!);
        var webp = Convert.FromBase64String(fixture.RootElement.GetProperty("vectors")[0].GetProperty("referenceWebp").GetString()!);
        using (var cache = new NomadNetRemoteMediaCache(profile))
            await cache.GetAsync(destination + ":/media/original.webp", _ => Task.FromResult(webp));
        await using (var terminal = new Terminal(binary, profile, rns, Path.Combine(root, "styled.terminal.txt")))
        {
            await terminal.WaitAsync("nomadnet> ");
            await terminal.CommandAsync($"browse {destination} /page/styled.mu");
            AssertStyled(terminal.LastResponse, "BOLD-C104", "UNDER-C104", "ITALIC-C104", "FG-C104", "BG-C104");
            Assert.Matches(@"\x1b\[1m[\r\n]*BOLD-C104", terminal.LastResponse);
            Assert.Contains("\u001b[4mUNDER-C104", terminal.LastResponse);
            Assert.Contains("\u001b[3mITALIC-C104", terminal.LastResponse);
            Assert.Matches(@"\x1b\[38;2;18;52;86m(?:\x1b\[[\d;]*m|[\r\n])*FG-C104", terminal.LastResponse);
            Assert.Contains("\u001b[48;2;101;67;33mBG-C104", terminal.LastResponse);
            var aligned = Regex.Replace(terminal.LastResponse, @"\x1b\[(\d+)C",
                match => new string(' ', int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)));
            aligned = Regex.Replace(aligned, @"\x1b\[[\d;?]*[ -/]*[@-~]", "");
            Assert.Contains("\n" + new string(' ', 74) + "CENTER-C104", aligned);
            Assert.Contains("\n" + new string(' ', 150) + "RIGHT-C104", aligned);
            Assert.Contains("SHUT", terminal.LastResponse);
            Assert.DoesNotContain("HIDDEN-C104", terminal.LastResponse);
            await terminal.CommandAsync("fold 1");
            Assert.Contains("OPEN", terminal.LastResponse);
            Assert.Contains("HIDDEN-C104", terminal.LastResponse);
            await terminal.CommandAsync("anchor inside");
            Assert.DoesNotContain("BOLD-C104", terminal.LastResponse);
            Assert.Contains("Fold-C104", terminal.LastResponse);
            await terminal.CommandAsync("images load");
            Assert.Contains("Inline image loading complete.", terminal.LastResponse);
            Assert.DoesNotContain("Could not load inline image", terminal.LastResponse);
            await terminal.CommandAsync("messages " + destination);
            AssertStyled(terminal.LastResponse, "BOLD-MESSAGE-C104", "ITALIC-MESSAGE-C104");
            Assert.Matches(@"\x1b\[1m[\r\n]*BOLD-MESSAGE-C104", terminal.LastResponse);
            Assert.Contains("\u001b[3mITALIC-MESSAGE-C104", terminal.LastResponse);
            await terminal.CommandAsync("rrc open " + destination);
            await terminal.CommandAsync("rrc view room");
            AssertStyled(terminal.LastResponse, "R-BOLD", "R-UNDER", "R-ITALIC", "R-COLOR");
            Assert.Contains("\u001b[1mR-BOLD", terminal.LastResponse);
            Assert.Contains("\u001b[4mR-UNDER", terminal.LastResponse);
            Assert.Contains("\u001b[3mR-ITALIC", terminal.LastResponse);
            Assert.Contains("\u001b[38;2;18;52;86mR-COLOR", terminal.LastResponse);
            Assert.Contains("\u001b[48;2;101;67;33mR-BG", terminal.LastResponse);
            await terminal.CommandAsync("edit mkdir pages");
            await terminal.CommandAsync("edit new pages styled.mu");
            await terminal.CommandAsync("edit text \"`!BOLD-PREVIEW-C104`! `_UNDER-PREVIEW-C104`_ `*ITALIC-PREVIEW-C104`*\"");
            await terminal.CommandAsync("edit preview");
            AssertStyled(terminal.LastResponse, "BOLD-PREVIEW-C104", "UNDER-PREVIEW-C104", "ITALIC-PREVIEW-C104");
            Assert.Matches(@"\x1b\[1m[\r\n]*BOLD-PREVIEW-C104", terminal.LastResponse);
            Assert.Contains("\u001b[4mUNDER-PREVIEW-C104", terminal.LastResponse);
            Assert.Contains("\u001b[3mITALIC-PREVIEW-C104", terminal.LastResponse);
            await terminal.CommandAsync("edit save");
            await terminal.CommandAsync("quit", true);
        }
        settings.ColorMode = "monochrome";
        await settings.SaveAsync(profile);
        await using (var terminal = new Terminal(binary, profile, rns, Path.Combine(root, "monochrome.terminal.txt")))
        {
            await terminal.WaitAsync("nomadnet> ");
            await terminal.CommandAsync($"browse {destination} /page/styled.mu");
            Assert.Contains("BOLD-C104", terminal.LastResponse);
            Assert.DoesNotMatch(@"\x1b\[(?:1|3|4|38;2|48;2)(?:;\d+)*m", terminal.LastResponse);
            await terminal.CommandAsync("edit open pages styled.mu");
            await terminal.CommandAsync("edit preview");
            Assert.Contains("BOLD-PREVIEW-C104", terminal.LastResponse);
            Assert.DoesNotMatch(@"\x1b\[(?:1|3|4|38;2|48;2)(?:;\d+)*m", terminal.LastResponse);
            await terminal.CommandAsync("quit", true);
        }

        static void AssertStyled(string text, params string[] labels)
        {
            Assert.Matches(@"\x1b\[[\d;]*m", text);
            var plain = Regex.Replace(text, @"\x1b\[[\d;?]*[ -/]*[@-~]", "");
            foreach (var label in labels) Assert.Contains(label, plain);
        }
    }

    [Fact, Trait("TestId", "T882")]
    public async Task T882AutomaticEditorPaneKeepsStyleClipAlignmentAndSourceIsolation()
    {
        var root = Path.GetFullPath(Path.Combine("artifacts", "tests", "nomad-micron-contracts", "editor-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new NomadNetSettings { ColorMode = "24bit" };
            await using var editor = new NomadNetEditor(root, settings);
            editor.CreateDirectory("pages");
            await editor.ActivatePresentationAsync();
            await editor.OpenAsync("pages", "styled.mu", create: true);
            await editor.PaneAsync("files");
            await editor.PaneAsync("editor");
            const string source = "`!bold`! `_under`_ `*italic`*\n`ccentered\n`rright";
            editor.Buffer.Set(source);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (editor.AutomaticPreview?.Revision != editor.PreviewRevision) await Task.Delay(20, timeout.Token);
            var view = editor.View(80, 10, settings, true);
            Assert.Contains("\u001b[1mbold\u001b[0m", view);
            Assert.Contains("\u001b[4munder\u001b[0m", view);
            Assert.Contains("\u001b[3mitalic\u001b[0m", view);
            Assert.Contains(new string(' ', 36) + "centered", view);
            Assert.Equal(source, editor.Buffer.Text);
            Assert.DoesNotContain('\u001b', editor.View(80, 10, settings, false));
            settings.ColorMode = "monochrome";
            Assert.DoesNotContain('\u001b', editor.View(80, 10, settings, true));
        }
        finally { Directory.Delete(root, true); }
    }

    [MicronNativeFact, Trait("TestId", "T870")]
    public async Task T870ActualConptyCachedGrammarActionsMalformedRecoveryAndRestart()
    {
        var binary = Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_BINARY")
            ?? throw new InvalidOperationException("Select the frozen C104 native binary.");
        var root = Path.Combine(Path.GetFullPath(Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_MICRON_EVIDENCE")
            ?? Path.Combine("artifacts", "tests", "nomad-micron-contracts")), "conpty-" + Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(root, "profile");
        var rns = Path.Combine(root, "rns");
        Directory.CreateDirectory(rns);
        await File.WriteAllTextAsync(Path.Combine(rns, "config"),
            "[reticulum]\nshare_instance=no\nenable_transport=no\n[logging]\nloglevel=0\n[interfaces]\n");
        await new NomadNetSettings { ClientAnnounceAtStart = false, PeriodicLxmfSync = false,
            IntroTime = 0, ColorMode = "monochrome", Glyphs = "plain", MouseEnabled = false, HideGuide = true }
            .SaveAsync(profile);
        const string destination = "0123456789abcdef0123456789abcdef";
        const string source = "`:pending\n#synthetic comment\n`->Folded\nHIDDEN-SYNTHETIC\n<visible-reset\n" +
            ">>Synthetic `<input`initial>\n`<?!|flag|yes|*`Flag>\n`t24\n| A | B |\n|---|---|\n| one | two |\n`t\n" +
            "one`[]two\ntrailing`\n`[Jump`#pending]";
        var cache = new NomadNetPageCache(profile);
        await cache.StoreAsync(destination + ":/page/grammar.mu", Encoding.UTF8.GetBytes(source));
        await cache.StoreAsync(destination + ":/page/bad.mu", "`t513\n| a |\n|--|\n`t"u8.ToArray());
        byte[] identity;
        await using (var terminal = new Terminal(binary, profile, rns, Path.Combine(root, "first.terminal.txt")))
        {
            await terminal.WaitAsync("nomadnet> ");
            identity = await IdentityDigestAsync(Path.Combine(rns, "storage", "nomadnet_identity"));
            await terminal.CommandAsync($"browse {destination} /page/grammar.mu");
            Assert.Contains("(cached page)", terminal.LastResponse);
            Assert.Contains("visible-reset", terminal.LastResponse);
            Assert.DoesNotContain("HIDDEN-SYNTHETIC", terminal.LastResponse);
            Assert.Contains("onetwo", terminal.LastResponse);
            Assert.DoesNotContain("trailing`", terminal.LastResponse);
            Assert.Contains("│ one │ two │", terminal.LastResponse);
            await terminal.CommandAsync("fields");
            Assert.Contains("input (field) = initial", terminal.LastResponse);
            Assert.Contains("flag (checkbox) = on", terminal.LastResponse);
            await terminal.CommandAsync("field 1 changed");
            await terminal.CommandAsync("field 2 off");
            await terminal.CommandAsync("page");
            Assert.Contains("[field 1 input: changed]", terminal.LastResponse);
            Assert.Contains("[ ] Flag", terminal.LastResponse);
            await terminal.CommandAsync("fold 1");
            Assert.Contains("HIDDEN-SYNTHETIC", terminal.LastResponse);
            await terminal.CommandAsync("fold 1");
            Assert.DoesNotContain("HIDDEN-SYNTHETIC", terminal.LastResponse);
            await terminal.CommandAsync("links");
            Assert.Contains("#pending", terminal.LastResponse);
            await terminal.CommandAsync("open 1");
            Assert.Contains("visible-reset", terminal.LastResponse);
            await terminal.CommandAsync($"browse {destination} /page/bad.mu");
            Assert.Contains("Error: Micron table width", terminal.LastResponse);
            await terminal.CommandAsync($"browse {destination} /page/grammar.mu");
            Assert.Contains("visible-reset", terminal.LastResponse);
            await terminal.CommandAsync("quit", true);
        }
        await using (var terminal = new Terminal(binary, profile, rns, Path.Combine(root, "restart.terminal.txt")))
        {
            await terminal.WaitAsync("nomadnet> ");
            await terminal.CommandAsync($"browse {destination} /page/grammar.mu");
            Assert.Contains("(cached page)", terminal.LastResponse);
            Assert.Contains("[field 1 input: initial]", terminal.LastResponse);
            Assert.Contains("[x] Flag", terminal.LastResponse);
            Assert.DoesNotContain("HIDDEN-SYNTHETIC", terminal.LastResponse);
            Assert.DoesNotContain("Error:", terminal.LastResponse);
            await terminal.CommandAsync("quit", true);
        }
        Assert.Equal(identity, await IdentityDigestAsync(Path.Combine(rns, "storage", "nomadnet_identity")));
        Assert.Equal(source, Encoding.UTF8.GetString((await cache.ReadAsync(destination + ":/page/grammar.mu"))!));
    }

    private static async Task<byte[]> IdentityDigestAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        try { return SHA256.HashData(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private sealed class MicronNativeFactAttribute : FactAttribute
    {
        public MicronNativeFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "C104 ConPTY acceptance requires Windows; current WSL Linux execution is blocked by EIO.";
            else if (Environment.GetEnvironmentVariable("DOTRETICULUM_NOMADNET_MICRON") != "1")
                Skip = "Enable C104 with its frozen native binary and owned evidence directory.";
        }
    }

    private sealed class Terminal : IAsyncDisposable
    {
        private readonly RnshProcess _process;
        private readonly Task<int> _exit;
        private readonly Task _output;
        private readonly StringBuilder _text = new();
        private readonly string _log;
        private int _responseStart;
        private string Text { get { lock (_text) return _text.ToString(); } }
        private string PromptText => Text.Replace("\u001b[1C", " ", StringComparison.Ordinal);
        public string LastResponse => Text[_responseStart..].Replace("\r", "", StringComparison.Ordinal);
        public Terminal(string binary, string profile, string rns, string log)
        {
            _log = log;
            string[] command = [binary, "--config", profile, "--rnsconfig", rns];
            _process = RnshProcess.Start(command, new RnshExecute
            {
                Command = command, Terminal = "xterm", Window = new RnshWindow { Columns = 160, Rows = 40 }
            }, null);
            _exit = _process.WaitAsync(default);
            _output = ReadAsync();
        }
        private async Task ReadAsync()
        {
            using var reader = new StreamReader(_process.Stdout, Encoding.UTF8, leaveOpen: true);
            var buffer = new char[4096];
            while (await reader.ReadAsync(buffer) is var length && length > 0)
                lock (_text)
                {
                    if (_text.Length + length > 2_000_000) throw new IOException("Micron ConPTY evidence exceeds two MiB.");
                    _text.Append(buffer, 0, length);
                }
        }
        public async Task WaitAsync(string text, int count = 1)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                while (PromptText.Split(text, StringSplitOptions.None).Length - 1 < count)
                {
                    if (_output.IsFaulted) await _output;
                    if (_exit.IsCompleted) throw new IOException("Exited before: " + text + "\n" + Text);
                    await Task.Delay(30, deadline.Token);
                }
            }
            catch (OperationCanceledException) { throw new IOException("Timed out before: " + text + "\n" + Text); }
        }
        public async Task CommandAsync(string text, bool exits = false)
        {
            var count = PromptText.Split("nomadnet> ", StringSplitOptions.None).Length - 1;
            _responseStart = Text.Length;
            await _process.Stdin.WriteAsync(Encoding.UTF8.GetBytes(text + "\r"));
            await _process.Stdin.FlushAsync();
            if (!exits) await WaitAsync("nomadnet> ", count + 1);
            else
            {
                Assert.Equal(0, await _exit.WaitAsync(TimeSpan.FromSeconds(15)));
                await _output.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        public async ValueTask DisposeAsync()
        {
            await _process.DisposeAsync();
            try { await _output.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (ObjectDisposedException) { }
            finally { await File.WriteAllTextAsync(_log, Text, new UTF8Encoding(false)); }
        }
    }
}
