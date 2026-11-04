using System.Globalization;
using System.Text;
using LXST;
using Sideband;

namespace Sideband.Desktop;

public static class SidebandDaemon
{
    public static async Task<int> RunAsync(string dataDirectory, string? reticulumConfig, bool interactive,
        bool syntheticAudio, bool verbose, TextReader input, TextWriter output, TextWriter error, CancellationToken token)
    {
        var standardInput = ReferenceEquals(input, Console.In);
        var standardOutput = ReferenceEquals(output, Console.Out);
        var standardError = ReferenceEquals(error, Console.Error);
        if (standardInput)
        {
            Console.InputEncoding = new UTF8Encoding(false, true);
            input = Console.In;
        }
        if (standardOutput || standardError)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            if (standardOutput) output = Console.Out;
            if (standardError) error = Console.Error;
        }
        var store = new SidebandStateStore(dataDirectory);
        await using var client = new SidebandClient(store, syntheticAudio ? new HeadlessAudioDeviceBackend() : null);
        var outputGate = new object();
        await using var console = new SidebandConsole(client, store, text => Write(output, text), text => Write(error, "ERROR " + text), token);
        CancellationTokenSource? dialStop = null;
        Task dial = Task.CompletedTask;
        client.ErrorReported += message => Write(error, "ERROR " + SidebandConsoleSyntax.Safe(message));
        client.VoiceStateChanged += snapshot =>
        {
            if (verbose || console.IsStreaming) Write(output, $"VOICE state={snapshot.State} peer={snapshot.RemoteIdentityHash ?? "none"}");
        };
        try
        {
            await client.ConnectAsync(reticulumConfig, token).ConfigureAwait(false);
            Write(output, $"READY identity={client.ConnectionInfo!.IdentityHash} delivery={client.ConnectionInfo.DeliveryHash} voice={client.VoiceDestinationHash ?? "disabled"} synthetic={syntheticAudio}");
            if (!interactive)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                return 0;
            }
            while (!token.IsCancellationRequested)
            {
                string? line;
                // Console.In can block synchronously before returning its ValueTask. Isolate
                // that single borrowed read; process shutdown must still release the client.
                try { line = await Task.Run(() => ReadLineAsync(input, token), CancellationToken.None).WaitAsync(token).ConfigureAwait(false); }
                catch (InvalidDataException exception) { Write(error, "ERROR " + exception.Message); continue; }
                if (line is null) break;
                try
                {
                    using var commandStop = CancellationTokenSource.CreateLinkedTokenSource(token);
                    commandStop.CancelAfter(TimeSpan.FromSeconds(5));
                    var args = SidebandConsoleSyntax.Parse(line);
                    if (console.EndStream(args)) continue;
                    var invocation = SidebandConsoleSyntax.Resolve(args);
                    if (invocation is null) continue;
                    if (invocation.Command.Name is "quit" or "exit") break;
                    if (await console.ExecuteAsync(invocation, commandStop.Token).ConfigureAwait(false)) continue;
                    if (args is ["voice", "status"])
                    {
                        var voice = client.VoiceSnapshot;
                        Write(output, voice is null ? "STATUS voice=disabled" :
                            $"STATUS voice={voice.State} peer={voice.RemoteIdentityHash ?? "none"} profile={(int?)voice.Profile} mode={(int?)voice.Mode} rx={voice.ReceivedFrames} tx={voice.TransmittedFrames} mic_muted={voice.TransmitMuted} spk_muted={voice.ReceiveMuted}");
                    }
                    else if (args is ["voice", "enable"] or ["voice", "disable"])
                    {
                        if (args[1] == "disable" && dialStop is not null)
                        {
                            await dialStop.CancelAsync().ConfigureAwait(false);
                            await dial.ConfigureAwait(false);
                        }
                        var state = await client.LoadAsync(token).ConfigureAwait(false);
                        await client.ConfigureVoiceAsync(args[1] == "enable", state.Voice ?? new(), token).ConfigureAwait(false);
                        Write(output, "OK voice " + args[1]);
                    }
                    else if (args is ["voice", "announce"])
                    {
                        await client.AnnounceVoiceAsync(token).ConfigureAwait(false);
                        Write(output, "OK voice announce");
                    }
                    else if (args is ["voice", "dial", var identity])
                    {
                        if (!dial.IsCompleted) throw new InvalidOperationException("A call is already being placed.");
                        dialStop?.Dispose();
                        dialStop = CancellationTokenSource.CreateLinkedTokenSource(token);
                        dialStop.CancelAfter(TimeSpan.FromMinutes(2));
                        dial = DialAsync(identity, TelephoneProfile.MediumQuality, dialStop.Token);
                        Write(output, "OK voice dial queued");
                    }
                    else if (args is ["voice", "dial", var selectedIdentity, var selectedProfile])
                    {
                        var profile = ParseEnum<TelephoneProfile>(selectedProfile);
                        if (!dial.IsCompleted) throw new InvalidOperationException("A call is already being placed.");
                        dialStop?.Dispose();
                        dialStop = CancellationTokenSource.CreateLinkedTokenSource(token);
                        dialStop.CancelAfter(TimeSpan.FromMinutes(2));
                        dial = DialAsync(selectedIdentity, profile, dialStop.Token);
                        Write(output, "OK voice dial queued");
                    }
                    else if (args is ["voice", "hangup"])
                    {
                        // Hangup cancels a pending path lookup as well as an attached telephone call.
                        if (dialStop is not null) await dialStop.CancelAsync().ConfigureAwait(false);
                        await dial.ConfigureAwait(false);
                        await client.HangupCallAsync(token).ConfigureAwait(false);
                        Write(output, "OK voice hangup");
                    }
                    else if (args is ["voice", "answer"])
                    {
                        await client.AnswerCallAsync(token).ConfigureAwait(false);
                        Write(output, "OK voice answer");
                    }
                    else if (args is ["voice", "reject"])
                    {
                        await client.RejectCallAsync(token).ConfigureAwait(false);
                        Write(output, "OK voice reject");
                    }
                    else if (args is ["voice", "profile", var profile])
                    {
                        await client.SetVoiceProfileAsync(ParseEnum<TelephoneProfile>(profile), token).ConfigureAwait(false);
                        Write(output, "OK voice profile");
                    }
                    else if (args is ["voice", "mode", var mode])
                    {
                        await client.SetVoiceModeAsync(ParseEnum<TelephoneMode>(mode), token).ConfigureAwait(false);
                        Write(output, "OK voice mode");
                    }
                    else if (args is ["voice", "ptt", "on"] or ["voice", "ptt", "off"])
                    {
                        client.SetPushToTalk(args[2] == "on");
                        Write(output, "OK voice ptt");
                    }
                    else if (args is ["voice", "mute", "mic"] or ["voice", "unmute", "mic"])
                    {
                        client.MuteVoiceMicrophone(args[1] == "mute");
                        Write(output, "OK voice microphone");
                    }
                    else if (args is ["voice", "mute", "spk"] or ["voice", "unmute", "spk"])
                    {
                        client.MuteVoiceSpeaker(args[1] == "mute");
                        Write(output, "OK voice speaker");
                    }
                    else if (args is ["voice", "log", "clear"])
                    {
                        await client.ClearCallHistoryAsync(token).ConfigureAwait(false);
                        Write(output, "OK voice log cleared");
                    }
                    else if (args is ["voice", "log"])
                        foreach (var call in (await client.LoadAsync(token).ConfigureAwait(false)).Calls ?? [])
                            Write(output, $"CALL {call.Time.ToString("O", CultureInfo.InvariantCulture)} {call.Event} {call.IdentityHash}");
                    else throw new ArgumentException("Unknown command or invalid arguments. Type help.");
                }
                catch (Exception exception) when (IsCommandFailure(exception))
                {
                    Write(error, "ERROR " + SidebandConsoleSyntax.Safe(exception.Message));
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    Write(error, "ERROR foreground command exceeded its five-second budget or was cancelled; inspect status before retrying.");
                }
            }
            return 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return 0; }
        finally
        {
            if (dialStop is not null) await dialStop.CancelAsync().ConfigureAwait(false);
            await dial.ConfigureAwait(false);
            dialStop?.Dispose();
        }

        async Task DialAsync(string identity, TelephoneProfile profile, CancellationToken dialToken)
        {
            try { await client.CallIdentityAsync(identity, profile, cancellationToken: dialToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (dialToken.IsCancellationRequested) { Write(output, "CANCELLED voice dial"); }
            catch (Exception exception) when (IsCommandFailure(exception)) { Write(error, "ERROR " + SidebandConsoleSyntax.Safe(exception.Message)); }
        }

        void Write(TextWriter writer, string text)
        {
            lock (outputGate) { writer.WriteLine(text); writer.Flush(); }
        }
    }

    private static bool IsCommandFailure(Exception exception) => exception is ArgumentException or InvalidOperationException or
        IOException or InvalidDataException or TimeoutException or DllNotFoundException or EntryPointNotFoundException or NotSupportedException or
        System.Text.Json.JsonException or FormatException or OverflowException;

    private static T ParseEnum<T>(string value) where T : struct, Enum =>
        Enum.TryParse<T>(value, true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed : throw new ArgumentException("Unsupported voice profile or mode.");

    private static async Task<string?> ReadLineAsync(TextReader input, CancellationToken token)
    {
        var line = new StringBuilder();
        var character = new char[1];
        var oversized = false;
        int read;
        while ((read = await input.ReadAsync(character.AsMemory(), token).AsTask().WaitAsync(token).ConfigureAwait(false)) != 0)
        {
            if (character[0] == '\n') break;
            if (character[0] == '\r') continue;
            if (line.Length < 8192) line.Append(character[0]);
            else oversized = true;
        }
        if (oversized) throw new InvalidDataException("Console commands are limited to 8,192 characters.");
        return line.Length == 0 && read == 0 ? null : line.ToString();
    }
}
