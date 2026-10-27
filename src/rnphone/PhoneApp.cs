using System.Security.Cryptography;
using DotLXST;
using DotReticulum.Applications;
using DotReticulum.Core;

namespace Rnphone;

public static class PhoneApp
{
    public const string Usage = """
        rnphone - experimental .NET Reticulum telephone

        Usage: rnphone [--config PATH] [--rnsconfig PATH] [-s] [-v ...]
          -l, --list-devices   List Linux PulseAudio sinks and sources
          -s, --service        Run without an interactive command loop
          --systemd            Print a service-unit example
          --version            Print the targeted upstream release
          -h, --help           Show this help

        Interactive: identity, desthash, announce, phonebook, redial, profile, quit.
        Extensions: reload, mute-receive, mute-transmit, receive-gain DB, transmit-gain DB.
        Enter a caller identity hash/name/alias to dial; Enter answers a ringing
        call or ends an active call. Requires separately installed libopus,
        libcodec2 and Linux pactl/pacat/parec for audio.
        Hardware controls and non-Linux device I/O are not implemented.
        """;

    public static async Task<int> RunAsync(string[] args, TextReader input, TextWriter output, TextWriter error, CancellationToken stop)
    {
        string? config = null, rnsConfig = null;
        var service = false;
        var list = false;
        var verbosity = 0;
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                string Next() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing option value.");
                switch (args[i])
                {
                    case "-h" or "--help": await output.WriteLineAsync(Usage); return 0;
                    case "--version": await output.WriteLineAsync("rnphone 0.4.5"); return 0;
                    case "--config": config = Next(); break;
                    case "--rnsconfig": rnsConfig = Next(); break;
                    case "-s" or "--service": service = true; break;
                    case "-l" or "--list-devices": list = true; break;
                    case "--verbose": verbosity++; break;
                    case "--systemd":
                        await output.WriteLineAsync("[Unit]\nDescription=Experimental .NET Reticulum telephone\nAfter=sound.target\n\n[Service]\nExecStart=/usr/local/bin/rnphone --service\nRestart=on-failure\n\n[Install]\nWantedBy=default.target");
                        return 0;
                    default:
                        if (args[i].StartsWith('-') && args[i].Length > 1 && args[i][1..].All(c => c == 'v')) verbosity += args[i].Length - 1;
                        else throw new ArgumentException($"Unknown option {args[i]}.");
                        break;
                }
            }
        }
        catch (ArgumentException ex) { await error.WriteLineAsync("rnphone: " + ex.Message); return 2; }
        try
        {
            if (list)
            {
                foreach (var kind in new[] { "sinks", "sources" })
                {
                    using var process = PulseAudio.Start("pactl", ["list", "short", kind], input: false, output: true);
                    var diagnostic = PulseAudio.DrainErrorsAsync(process);
                    var devices = process.StandardOutput.ReadToEndAsync(stop);
                    await process.WaitForExitAsync(stop);
                    if (process.ExitCode != 0) throw new IOException(await diagnostic);
                    await output.WriteLineAsync(await devices);
                }
                return 0;
            }
            var directory = ConfigurationDirectory(config);
            Directory.CreateDirectory(directory);
            Directory.CreateDirectory(Path.Combine(directory, "storage"));
            var path = Path.Combine(directory, "config");
            if (!File.Exists(path)) await File.WriteAllTextAsync(path, PhoneConfiguration.Default, stop);
            var settings = PhoneConfiguration.Load(path);
            using var identity = LoadIdentity(Path.Combine(directory, "identity"));
            settings.ExcludeLocalIdentity(identity.HexHash);
            await using var node = ReticulumNode.Open(rnsConfig,
                (level, message) => { if (level <= 3 + verbosity) error.WriteLine($"Reticulum[{level}]: {message}"); });
            await using var sink = new PulseAudioSink(settings.Speaker, () => output.WriteLine("Audio receive ready"));
            await using var toneSink = new PulseAudioSink(settings.Speaker);
            await using var ringerSink = new PulseAudioSink(settings.Ringer);
            await using var telephone = new TelephoneEndpoint(node.Router, identity,
                new TelephoneOptions { RingTimeout = TimeSpan.FromSeconds(30), WaitTimeout = TimeSpan.FromSeconds(60),
                    AuthorizeCaller = peer => Volatile.Read(ref settings).Authorize(peer.HexHash), Sink = sink });
            telephone.Error = (context, exception) => error.WriteLine($"{context} {exception?.Message}");
            var audioGate = new object();
            CancellationTokenSource? captureStop = null;
            Identity? dialledIdentity = null;
            Task capture = Task.CompletedTask;
            Task ringing = Task.CompletedTask;
            CancellationTokenSource? ringingStop = null;
            Task tones = Task.CompletedTask;
            CancellationTokenSource? toneStop = null;
            var busyTone = false;
            telephone.StateChanged += state =>
            {
                output.WriteLine("State: " + state);
                output.Flush();
                lock (audioGate)
                {
                    if ((state == CallState.Ringing && !telephone.IsIncomingCall) || state is CallState.Busy or CallState.Rejected)
                    {
                        busyTone = state is CallState.Busy or CallState.Rejected;
                        var tone = busyTone ? TelephoneTone.Busy : TelephoneTone.Ringback;
                        toneStop?.Cancel();
                        var previousTone = tones;
                        var previousStop = toneStop;
                        toneStop = CancellationTokenSource.CreateLinkedTokenSource(stop);
                        var toneToken = toneStop.Token;
                        tones = Task.Run(async () =>
                        {
                            try
                            {
                                await previousTone;
                                previousStop?.Dispose();
                                toneToken.ThrowIfCancellationRequested();
                                await foreach (var frame in new TelephoneToneSource(tone).CaptureAsync(toneToken))
                                    await toneSink.WriteAsync(DotLXST.Audio.AudioMixer.ApplyGain(frame,
                                        telephone.ReceiveGainDb, telephone.ReceiveMuted), toneToken);
                            }
                            catch (OperationCanceledException) when (toneToken.IsCancellationRequested) { }
                            catch (Exception ex) { error.WriteLine("Telephone tone playback failed: " + ex.Message); }
                            finally { await toneSink.StopAsync(); }
                        });
                    }
                    else if (state != CallState.Available || !busyTone) toneStop?.Cancel();
                    if (state == CallState.Ringing && telephone.IsIncomingCall &&
                        Volatile.Read(ref settings).Ringtone is { } ringtone && File.Exists(ringtone))
                    {
                        ringingStop?.Cancel();
                        var previousRinging = ringing;
                        var previousStop = ringingStop;
                        ringingStop = CancellationTokenSource.CreateLinkedTokenSource(stop);
                        var ringToken = ringingStop.Token;
                        ringing = Task.Run(async () =>
                        {
                            try
                            {
                                await previousRinging;
                                previousStop?.Dispose();
                                ringToken.ThrowIfCancellationRequested();
                                await using var player = new FilePlayer(ringerSink, ringtone, loop: true);
                                player.Play(ringToken);
                                await player.Completion;
                            }
                            catch (OperationCanceledException) when (ringToken.IsCancellationRequested) { }
                            catch (Exception ex) { error.WriteLine("Ringtone playback failed: " + ex.Message); }
                            finally { await ringerSink.StopAsync(); }
                        });
                    }
                    else if (state != CallState.Ringing) ringingStop?.Cancel();
                    if (state == CallState.Established)
                    {
                        var previous = capture;
                        var previousStop = captureStop;
                        previousStop?.Cancel();
                        captureStop = CancellationTokenSource.CreateLinkedTokenSource(stop);
                        var token = captureStop.Token;
                        capture = Task.Run(async () =>
                        {
                            try
                            {
                                await previous;
                                previousStop?.Dispose();
                                token.ThrowIfCancellationRequested();
                                await PulseAudio.CaptureAsync(telephone, () => Volatile.Read(ref settings).Microphone, token, () => output.WriteLine("Audio transmit ready"));
                            }
                            catch (Exception ex) when (token.IsCancellationRequested &&
                                ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException or IOException) { }
                            catch (Exception ex) { error.WriteLine("Audio capture failed: " + ex.Message); telephone.Hangup(); }
                        }, CancellationToken.None);
                    }
                    else if (state != CallState.Established) captureStop?.Cancel();
                }
            };
            await node.StartAsync(stop);
            telephone.Start();
            await output.WriteLineAsync($"Identity hash: <{identity.HexHash}>");
            await output.WriteLineAsync($"Destination hash: <{Convert.ToHexStringLower(telephone.DestinationHash)}>");
            await output.WriteLineAsync("Reticulum Telephone ready");
            await output.FlushAsync();
            string? last = null;
            var phonebookOpen = false;
            try
            {
                if (service) await node.Completion;
                else while (!stop.IsCancellationRequested)
                {
                    var line = await input.ReadLineAsync(stop);
                    if (line is null) break;
                    line = line.Trim();
                    if (phonebookOpen && line is ("back" or "b" or "quit" or "q"))
                    {
                        phonebookOpen = false;
                        await output.WriteLineAsync("Phonebook closed");
                        await output.FlushAsync();
                        continue;
                    }
                    if (line is "quit" or "q" or "exit") break;
                    try
                    {
                        if (line == "reload")
                        {
                            var replacement = PhoneConfiguration.Load(path);
                            replacement.ExcludeLocalIdentity(identity.HexHash);
                            sink.SetDevice(replacement.Speaker);
                            toneSink.SetDevice(replacement.Speaker);
                            ringerSink.SetDevice(replacement.Ringer);
                            Volatile.Write(ref settings, replacement);
                            last = null;
                            phonebookOpen = false;
                            await output.WriteLineAsync("Configuration reloaded");
                        }
                        else if (telephone.State == CallState.Ringing && telephone.IsIncomingCall)
                        {
                            if (line.Length == 0) await telephone.AnswerAsync(stop);
                            else telephone.Hangup();
                        }
                        else if (line == "profile" && telephone.State == CallState.Established)
                        {
                            await telephone.SwitchProfileAsync(
                                TelephoneProfiles.Next(telephone.ActiveProfile ?? TelephoneProfile.MediumQuality), stop);
                            await output.WriteLineAsync("Profile: " + telephone.ActiveProfile);
                        }
                        else if (line is "mute-receive" or "mute-transmit")
                        {
                            if (line == "mute-receive") telephone.ReceiveMuted = !telephone.ReceiveMuted;
                            else telephone.TransmitMuted = !telephone.TransmitMuted;
                            await output.WriteLineAsync($"Receive muted: {telephone.ReceiveMuted}; transmit muted: {telephone.TransmitMuted}");
                        }
                        else if (line.StartsWith("receive-gain ", StringComparison.Ordinal) || line.StartsWith("transmit-gain ", StringComparison.Ordinal))
                        {
                            var gain = float.Parse(line[(line.IndexOf(' ') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                            if (line[0] == 'r') telephone.ReceiveGainDb = gain;
                            else telephone.TransmitGainDb = gain;
                        }
                        else if (telephone.State is CallState.Established or CallState.Calling or CallState.Connecting) telephone.Hangup();
                        else if (line is "identity" or "i") await output.WriteLineAsync($"Identity hash: <{identity.HexHash}>");
                        else if (line is "desthash" or "d") await output.WriteLineAsync($"Destination hash: <{Convert.ToHexStringLower(telephone.DestinationHash)}>");
                        else if (line is "announce" or "anounce" or "a")
                        {
                            await telephone.AnnounceAsync(stop);
                            await output.WriteLineAsync("Announce sent");
                        }
                        else if (line is "phonebook" or "p")
                        {
                            var number = 0;
                            foreach (var entry in settings.Phonebook)
                            {
                                number++;
                                await output.WriteLineAsync($"{entry.Value.Alias ?? number.ToString(System.Globalization.CultureInfo.InvariantCulture)} {entry.Key}: <{entry.Value.Hash}>");
                            }
                            if (settings.Phonebook.Count == 0) await output.WriteLineAsync("No entries in phonebook");
                            else phonebookOpen = true;
                        }
                        else if (line is "help" or "h" or "?") await output.WriteLineAsync(Usage);
                        else if (line.Length != 0)
                        {
                            if (line is "redial" or "r")
                            {
                                if (last is null) continue;
                                line = last;
                            }
                            if (phonebookOpen)
                            {
                                var number = 0;
                                var selection = settings.Phonebook.Values.FirstOrDefault(entry =>
                                {
                                    number++;
                                    return (entry.Alias ?? number.ToString(System.Globalization.CultureInfo.InvariantCulture)) == line;
                                });
                                if (selection.Hash is not null) line = selection.Hash;
                            }
                            if (settings.Phonebook.TryGetValue(line, out var entry)) line = entry.Hash;
                            else line = settings.Phonebook.Values.FirstOrDefault(entry => entry.Alias == line).Hash ?? line;
                            last = PhoneConfiguration.ValidateHash(line);
                            var destination = Destination.ComputeHash(Convert.FromHexString(last), "lxst", ["telephony"]);
                            telephone.SetBusy();
                            try
                            {
                                if (!await node.WaitForPathAsync(destination, TimeSpan.FromSeconds(10), stop)) throw new TimeoutException("No path to caller.");
                            }
                            finally { telephone.SetBusy(false); }
                            dialledIdentity?.Dispose();
                            dialledIdentity = node.Router.Engine.Known.Recall(destination) ?? throw new InvalidOperationException("No announced remote identity.");
                            await telephone.CallAsync(dialledIdentity, cancellationToken: stop);
                        }
                    }
                    catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or TimeoutException or NotSupportedException or IOException or UnauthorizedAccessException)
                    { await error.WriteLineAsync("rnphone: " + ex.Message); }
                    await output.FlushAsync();
                }
            }
            finally
            {
                lock (audioGate) { captureStop?.Cancel(); ringingStop?.Cancel(); toneStop?.Cancel(); }
                await telephone.DisposeAsync();
                await capture;
                await ringing;
                await tones;
                captureStop?.Dispose();
                ringingStop?.Dispose();
                toneStop?.Dispose();
                dialledIdentity?.Dispose();
            }
            return 0;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        { await error.WriteLineAsync("rnphone: " + ex.Message); return 1; }
    }

    private static string ConfigurationDirectory(string? explicitPath)
    {
        if (explicitPath is not null) return Path.GetFullPath(explicitPath);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = OperatingSystem.IsWindows() ?
            new[] { Path.Combine(home, ".config", "rnphone") } :
            new[] { "/etc/rnphone", Path.Combine(home, ".config", "rnphone") };
        return candidates.FirstOrDefault(path => File.Exists(Path.Combine(path, "config"))) ?? Path.Combine(home, ".rnphone");
    }

    private static Identity LoadIdentity(string path)
    {
        if (File.Exists(path))
        {
            var data = File.ReadAllBytes(path);
            try { return Identity.FromPrivateKey(data); }
            finally { CryptographicOperations.ZeroMemory(data); }
        }
        var identity = Identity.Generate();
        var secret = identity.ExportPrivateKey();
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var file = new FileStream(path, options);
            file.Write(secret);
            file.Flush(flushToDisk: true);
            if (!OperatingSystem.IsWindows() && File.GetUnixFileMode(path) != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
            {
                file.Dispose();
                File.Delete(path);
                throw new NotSupportedException("The identity filesystem does not enforce owner-only Unix permissions. Use a native filesystem rather than an unprotected shared mount.");
            }
            return identity;
        }
        catch { identity.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
}
