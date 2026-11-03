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
        var store = new SidebandStateStore(dataDirectory);
        await using var client = new SidebandClient(store, syntheticAudio ? new HeadlessAudioDeviceBackend() : null);
        var outputGate = new object();
        CancellationTokenSource? dialStop = null;
        Task dial = Task.CompletedTask;
        client.ErrorReported += message => Write(error, "ERROR " + message);
        client.VoiceStateChanged += snapshot =>
        {
            if (verbose) Write(output, $"VOICE state={snapshot.State} peer={snapshot.RemoteIdentityHash ?? "none"}");
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
                var args = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (args.Length == 0) continue;
                try
                {
                    if (args is ["quit"] or ["exit"]) break;
                    if (args is ["help"])
                        Write(output, "Native console: status; voice status|enable|disable|announce|dial IDENTITY|answer|reject|hangup|log|log clear|profile VALUE|mode VALUE|mute mic|mute spk|unmute mic|unmute spk|ptt on|ptt off; quit. Functional voice commands are native extensions; pinned upstream handlers are placeholders.");
                    else if (args is ["status"] or ["voice", "status"])
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
                        dial = DialAsync(identity, dialStop.Token);
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
                    Write(error, "ERROR " + exception.Message);
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

        async Task DialAsync(string identity, CancellationToken dialToken)
        {
            try { await client.CallIdentityAsync(identity, cancellationToken: dialToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (dialToken.IsCancellationRequested) { Write(output, "CANCELLED voice dial"); }
            catch (Exception exception) when (IsCommandFailure(exception)) { Write(error, "ERROR " + exception.Message); }
        }

        void Write(TextWriter writer, string text)
        {
            lock (outputGate) { writer.WriteLine(text); writer.Flush(); }
        }
    }

    private static bool IsCommandFailure(Exception exception) => exception is ArgumentException or InvalidOperationException or
        IOException or InvalidDataException or TimeoutException or DllNotFoundException or EntryPointNotFoundException or NotSupportedException;

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
