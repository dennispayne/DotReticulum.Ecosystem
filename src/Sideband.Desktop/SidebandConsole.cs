using System.Globalization;
using System.Text;
using Sideband;

namespace Sideband.Desktop;

public sealed class SidebandConsole : IAsyncDisposable
{
    private readonly SidebandClient _client;
    private readonly SidebandStateStore _store;
    private readonly Action<string> _output;
    private readonly SidebandConsoleOperations _operations;
    private readonly SidebandConsoleConfiguration _configuration;
    private int _streaming;

    public SidebandConsole(SidebandClient client, SidebandStateStore store, Action<string> output,
        Action<string> error, CancellationToken lifetime)
    {
        _client = client;
        _store = store;
        _output = output;
        _operations = new(output, error, lifetime);
        _configuration = new(client);
        _client.RuntimeLogReported += ReportRuntimeLog;
    }

    public bool IsStreaming => Volatile.Read(ref _streaming) != 0;

    public bool EndStream(string[] tokens)
    {
        if (tokens.Length != 0 || Interlocked.Exchange(ref _streaming, 0) == 0) return false;
        _output("Log stream ended.");
        return true;
    }

    /// <returns>False only for a voice command handled by the existing native voice dispatcher.</returns>
    public async Task<bool> ExecuteAsync(SidebandConsoleInvocation invocation, CancellationToken token)
    {
        var name = invocation.Command.Name;
        var args = invocation.Arguments;
        if (name.StartsWith("voice ", StringComparison.Ordinal)) return false;
        if (name is "raw" or "plugins" or "plugins reload")
            throw new NotSupportedException("Python evaluation and the Python plugin ABI are not supported. No text is evaluated.");
        if (name == "help")
        {
            if (args.Length == 0)
            {
                _output("Native console. Available commands:\n" + string.Join('\n', SidebandConsoleSyntax.Commands.Select(SidebandConsoleSyntax.Usage)) +
                    "\nFunctional voice commands are native extensions; pinned upstream handlers are placeholders.\n" +
                    "Quotes and escapes group arguments; no shell evaluation. Long work is bounded and owned; cancel stops it; quit persists and exits.");
            }
            else
            {
                var prefix = string.Join(' ', args);
                var commands = SidebandConsoleSyntax.Commands.Where(c => c.Name == prefix || c.Name.StartsWith(prefix + " ", StringComparison.Ordinal)).ToArray();
                if (commands.Length == 0) throw new ArgumentException("Unknown command: " + prefix);
                _output(string.Join('\n', commands.Select(SidebandConsoleSyntax.Usage)));
            }
            return true;
        }
        if (name == "cancel")
        {
            _client.CancelSynchronization();
            await _operations.CancelAsync().ConfigureAwait(false);
            _output("OK operations cancelled");
            return true;
        }
        if (name == "clear") { _output("\u001b[2J\u001b[H"); return true; }
        if (name == "log")
        {
            if (args.Length != 0)
            {
                if (!int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var selected))
                    throw new ArgumentException("Invalid log level: " + Safe(args[0]) + ". Must be an integer 0-7.");
                if (selected is < 0 or > 7)
                    throw new ArgumentException($"Invalid log level: {selected}. Must be 0-7.");
                _client.RuntimeLogLevel = selected;
            }
            var level = _client.RuntimeLogLevel;
            var label = new[] { "squelch", "critical", "error", "warning", "notice", "info", "debug", "extreme" }[level];
            _output($"{(args.Length == 0 ? "Current log level:" : "Log level set to")} {level} ({label})");
            return true;
        }
        if (name == "log stream")
        {
            Volatile.Write(ref _streaming, 1);
            _output("Streaming native runtime reports and voice events. Press Enter to stop; commands remain available.");
            return true;
        }
        var state = await _client.LoadAsync(token).ConfigureAwait(false);
        if (name == "status")
        {
            _output($"Sideband Status\nLXMF router: {(_client.IsConnected ? "active" : "inactive")}\nDisplay name: {Safe(state.DisplayName)}\n" +
                $"Identity hash: {_client.ConnectionInfo?.IdentityHash ?? "none"}\nLXMF destination: {_client.ConnectionInfo?.DeliveryHash ?? state.OwnDeliveryHash ?? "none"}\n" +
                $"Propagation node: {state.PropagationNode ?? state.LastPropagationNode ?? "none"}\nMode: daemon interactive\nReticulum: {(_client.IsConnected ? "running" : "not started")}\n" +
                $"Transport: {_client.TransportEnabled}\nTelemetry: {state.Telemetry.Enabled}\nVoice: {state.VoiceEnabled}\nVoice running: {_client.VoiceSnapshot is not null}\n" +
                $"Conversations: {state.Contacts.Count(c => c.ConversationKnown)}\nAnnounce Stream: {state.Announces?.Count ?? 0}\nPlugins: unsupported\nOperations: {_operations.Count}");
        }
        else if (name == "conv")
        {
            var conversations = state.Contacts.Where(c => c.ConversationKnown).ToArray();
            _output(conversations.Length == 0 ? "No conversations" : string.Join('\n', conversations.Select(c =>
                $"{c.DeliveryHash} name={Safe(c.DisplayName)} trust={c.Trusted} unread={c.Unread} object={c.IsObject}")));
        }
        else if (name == "conv create")
        {
            var hash = Hash(args[0]);
            if (hash == state.OwnDeliveryHash) throw new ArgumentException("Cannot create a conversation with the local LXMF destination.");
            if (state.Contacts.Any(c => c.DeliveryHash == hash && c.ConversationKnown))
                throw new InvalidOperationException("Conversation already exists.");
            await _client.CreateConversationAsync(hash, args.Length > 1 ? args[1] : string.Empty, token).ConfigureAwait(false);
            _output("Conversation created with " + hash);
        }
        else if (name is "conv name" or "conv trust" or "conv untrust" or "conv clear" or "conv delete")
        {
            var hash = ResolveDestination(state, args[0]);
            if (!state.Contacts.Any(c => c.ConversationKnown && c.DeliveryHash == hash))
                throw new InvalidOperationException("No conversation exists for this destination.");
            if (name == "conv name") await _client.SetConversationNameAsync(hash, string.Join(' ', args[1..]), token).ConfigureAwait(false);
            else if (name is "conv trust" or "conv untrust")
                await _client.SetConversationTrustAsync(hash, name == "conv trust", token).ConfigureAwait(false);
            else await _client.ClearConversationAsync(hash, name == "conv delete", token).ConfigureAwait(false);
            _output("OK " + name + " " + hash);
        }
        else if (name == "conv msgs")
        {
            var hash = ResolveDestination(state, args[0]);
            var count = args.Length > 1 ? Count(args[1], 1000) : 12;
            var messages = await _client.GetMessagesAsync(hash, token).ConfigureAwait(false);
            _output(messages.Count == 0 ? "No messages in conversation with " + hash :
                string.Join('\n', messages.TakeLast(count).Select(m =>
                    $"{m.Id} [{(m.Incoming ? "in" : "out")}] {m.Timestamp.ToString("O", CultureInfo.InvariantCulture)} {m.DeliveryState} {m.DeliveryMethod}\n{Safe(m.Content)}")));
        }
        else if (name is "conv send" or "conv command" or "conv paper")
        {
            var hash = ResolveDestination(state, args[0]);
            var content = string.Join(' ', args[1..]);
            if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException("Message content cannot be empty.");
            if (name == "conv command")
            {
                var result = await _client.SendCommandAsync(hash, content, state.PropagationByDefault, token).ConfigureAwait(false);
                if (!result.Queued) throw new InvalidOperationException("Command was not queued: " + result.Status);
                _output("Command queued for " + hash + "; not a delivery/execution assertion.");
            }
            else _operations.Start(name, async stop =>
            {
                if (name == "conv paper")
                {
                    var message = await _client.CreatePaperMessageAsync(hash, content, cancellationToken: stop).ConfigureAwait(false);
                    return "Paper message created " + message.Id + "\n" + await _client.GetPaperMessageUriAsync(message.Id, stop).ConfigureAwait(false);
                }
                await _client.SendTextAsync(hash, content, stop).ConfigureAwait(false);
                return "Message submitted to LXMF for " + hash + "; inspect conv msgs for delivery state.";
            });
        }
        else if (name == "conv announce")
        {
            _operations.Start(name, async stop => { await _client.AnnounceAsync(stop).ConfigureAwait(false); return "LXMF announce sent."; }, unique: true);
        }
        else if (name == "conv sync")
        {
            var limit = args.Length > 0 ? Nonnegative(args[0]) : (int?)null;
            _operations.Start(name, async stop =>
            {
                var count = limit is { } maximum ? await _client.SynchronizeAsync(maximum, stop).ConfigureAwait(false) :
                    await _client.SynchronizeConfiguredAsync(stop).ConfigureAwait(false);
                return $"LXMF sync completed: {count} messages.";
            }, unique: true);
        }
        else if (name == "conv sync status") _output($"Sync status: {_client.SyncState}\nProgress: {(int)(_client.SyncProgress * 100)}%");
        else if (name == "conv sync cancel")
        {
            _client.CancelSynchronization();
            await _operations.CancelAsync("conv sync").ConfigureAwait(false);
            _output("LXMF sync cancelled");
        }
        else if (name == "net identity")
            _output($"Identity hash: {_client.ConnectionInfo?.IdentityHash ?? "none"}\nLXMF destination: {state.OwnDeliveryHash ?? "none"}\nDisplay name: {Safe(state.DisplayName)}");
        else if (name == "net propagation")
            _output(args.Length == 0 ? $"Configured: {state.PropagationNode ?? "none"}\nActive: {state.PropagationNode ?? state.LastPropagationNode ?? "none"}" :
                "Use 'net propagation set <hash>' to change the propagation node.");
        else if (name == "net propagation set")
        {
            var hash = args[0].Trim().ToLowerInvariant() is "none" or "null" or "" ? null : Hash(args[0]);
            await _client.ConfigurePropagationAsync(hash, state.PropagationByDefault, token).ConfigureAwait(false);
            _output(hash is null ? "Configured propagation node cleared; automatic/last-node selection remains active." : "Propagation node set to " + hash);
        }
        else if (name == "net announces")
        {
            var announces = await _client.GetAnnouncesAsync(token).ConfigureAwait(false);
            _output(announces.Count == 0 ? "No announces in database." :
                string.Join('\n', announces.Reverse().Take(32).Select(a => $"{a.DestinationHash} {Safe(a.Name)} {a.DestinationType}")));
        }
        else if (name == "net link")
        {
            var hash = Hash(args[0]);
            var links = _client.GetOutboundLinks().Where(l => l.DestinationHash == hash).ToArray();
            _output(links.Length == 0 ? "No active outbound link to this destination." :
                string.Join('\n', links.Select(l => $"{l.DestinationHash} state={l.State} mtu={l.Mtu} rtt={l.RoundTripSeconds.ToString("R", CultureInfo.InvariantCulture)}")));
        }
        else if (name.StartsWith("config", StringComparison.Ordinal)) await ConfigAsync(name, args, state, token).ConfigureAwait(false);
        else if (name.StartsWith("telemetry", StringComparison.Ordinal)) await TelemetryAsync(name, args, state, token).ConfigureAwait(false);
        else throw new ArgumentException("Unknown command or invalid arguments. Type help.");
        return true;
    }

    private async Task ConfigAsync(string name, string[] args, SidebandState state, CancellationToken token)
    {
        if (name == "config")
            _output("Activated native configuration categories:\n" + string.Join('\n', _configuration.Settings.Select(s => s.Category).Append("map").Distinct().Order()) +
                "\nOnly active native aliases are listed. Unsupported primary keys fail; archives are not activated.");
        else if (name == "config list")
        {
            var settings = _configuration.Settings.Where(s => args.Length == 0 || s.Category == args[0]).ToArray();
            if (settings.Length == 0 && args is not ["map"]) throw new NotSupportedException("No activated aliases in this category.");
            if (settings.Length > 0) _output(string.Join('\n', settings.Select(s => $"{s.Category} {s.Alias} = {Safe(s.Read(state))} ({s.Type})")));
            if (args.Length == 0 || args is ["map"])
                _output(await SidebandConsoleMapConfiguration.ExecuteAsync(_store.DataDirectory, null, null, token).ConfigureAwait(false));
        }
        else if (name is "config get" or "config set")
        {
            if (name == "config set" && args.Length < 3) throw new ArgumentException("Usage: config set <category> <alias> <value>");
            if (args[0] == "map")
            {
                _output(await SidebandConsoleMapConfiguration.ExecuteAsync(_store.DataDirectory, args[1],
                    name == "config set" ? string.Join(' ', args[2..]) : null, token).ConfigureAwait(false));
                return;
            }
            var setting = _configuration.Find(args[0], args[1]);
            if (name == "config set")
            {
                await setting.Write(state, string.Join(' ', args[2..]), token).ConfigureAwait(false);
                state = await _client.LoadAsync(token).ConfigureAwait(false);
            }
            _output($"{setting.Category} {setting.Alias} = {Safe(setting.Read(state))} ({setting.Type}); persisted");
        }
        else if (name == "config save") _output("Configuration saved: every successful mutation is already atomically persisted.");
        else if (name == "config reload")
        {
            await _operations.CancelAsync().ConfigureAwait(false);
            await _client.ReloadConfigurationAsync(token).ConfigureAwait(false);
            _output("Configuration reloaded. Reticulum interface configuration requires process restart.");
        }
    }

    private async Task TelemetryAsync(string name, string[] args, SidebandState state, CancellationToken token)
    {
        if (name is "telemetry on" or "telemetry off")
        {
            await _client.ConfigureTelemetryAsync(state.Telemetry with { Enabled = name == "telemetry on" }, token).ConfigureAwait(false);
            _output(name == "telemetry on" ? "Telemetry collection enabled." : "Telemetry collection disabled.");
        }
        else if (name is "telemetry track" or "telemetry untrack")
        {
            var hash = ResolveDestination(state, args[0], announces: true);
            var tracking = state.Telemetry.Tracking.Where(t => t.DeliveryHash != hash).ToList();
            if (name == "telemetry track")
                tracking.Add(new(hash, Count(args[1], 31_536_000), DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Count(args[2], 31_536_000)));
            await _client.ConfigureTelemetryAsync(state.Telemetry with { Tracking = tracking }, token).ConfigureAwait(false);
            _output("OK " + name + " " + hash);
        }
        else if (name is "telemetry request" or "telemetry send")
        {
            var hash = ResolveDestination(state, args[0], announces: true);
            var result = name == "telemetry request" ? await _client.RequestTelemetryAsync(hash, token).ConfigureAwait(false) :
                await _client.SendLatestTelemetryAsync(hash, token).ConfigureAwait(false);
            if (!result.Queued) throw new InvalidOperationException("Telemetry was not queued: " + result.Status);
            _output("Telemetry operation queued for " + hash + "; not a delivery assertion.");
        }
        else if (name == "telemetry sensors")
        {
            _output($"information: {state.Telemetry.InformationEnabled}\nfixed_location: {state.Telemetry.FixedLocationEnabled}\nrns_transport: {state.Telemetry.RnsTransportEnabled}\n" +
                "Other automatic sensors: unsupported (manual supplied data is not automatic collection).");
        }
        else if (name == "telemetry" && args.Length == 0)
        {
            var report = await _client.RefreshLocalTelemetryAsync(token).ConfigureAwait(false);
            _output(report is null ? "Telemetry collection is disabled. Use 'telemetry on' to enable." : FormatReport(report));
        }
        else
        {
            string? hash = null;
            var count = name == "telemetry" ? 1 : 20;
            if (args.Length > 0)
            {
                if (name == "telemetry list" && int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    if (args.Length > 1) throw new ArgumentException("A count alone cannot be followed by another argument.");
                    count = Count(args[0], 256);
                }
                else hash = ResolveDestination(state, args[0], announces: true);
            }
            if (args.Length > 1) count = Count(args[1], 256);
            var reports = await _client.GetTelemetryAsync(new(hash, Limit: count), token).ConfigureAwait(false);
            _output(reports.Count == 0 ? "No telemetry entries found." :
                string.Join('\n', reports.Select(r => r.SourceHash + "\n" + FormatReport(r.Report))));
        }
    }

    private static string FormatReport(SidebandTelemetryReport report)
    {
        var result = new StringBuilder();
        var remaining = 1024;
        var truncated = false;
        foreach (var sensor in report.Sensors)
        {
            if (truncated) break;
            Append(Safe(sensor.Name) + " (" + sensor.Status + "): ");
            Value(sensor.Data, 0);
            Append("\n");
        }
        if (truncated) result.Append("\n[display truncated]");
        return result.ToString();

        void Append(string text)
        {
            var available = 65_536 - result.Length;
            if (text.Length > available) { result.Append(text.AsSpan(0, available)); truncated = true; }
            else result.Append(text);
        }

        void Value(SidebandTelemetryValue value, int depth)
        {
            if (truncated) return;
            if (--remaining < 0 || depth > 4) { Append("[nested display truncated]"); return; }
            switch (value.Kind)
            {
                case SidebandTelemetryValueKind.Nil: Append("null"); break;
                case SidebandTelemetryValueKind.Boolean: Append(value.Boolean == true ? "true" : "false"); break;
                case SidebandTelemetryValueKind.Text: Append(Safe(value.Text is { Length: > 1024 } text ? text[..1024] + "[truncated]" : value.Text)); break;
                case SidebandTelemetryValueKind.Integer: Append(value.Integer!.Value.ToString(CultureInfo.InvariantCulture)); break;
                case SidebandTelemetryValueKind.UnsignedInteger: Append(value.UnsignedInteger!.Value.ToString(CultureInfo.InvariantCulture)); break;
                case SidebandTelemetryValueKind.Number: Append(value.Number!.Value.ToString("R", CultureInfo.InvariantCulture)); break;
                case SidebandTelemetryValueKind.Binary: Append("[binary data]"); break;
                case SidebandTelemetryValueKind.Array:
                    Append("[");
                    foreach (var item in value.Items.Take(16)) { Value(item, depth + 1); Append(", "); }
                    Append(value.Items.Count > 16 ? "[display truncated]]" : "]");
                    break;
                case SidebandTelemetryValueKind.Map:
                    Append("{");
                    foreach (var item in value.Members.Take(16)) { Value(item.Key, depth + 1); Append(": "); Value(item.Value, depth + 1); Append(", "); }
                    Append(value.Members.Count > 16 ? "[display truncated]}" : "}");
                    break;
            }
        }
    }

    public static string ResolveDestination(SidebandState state, string value, bool announces = false)
    {
        value = value.Trim();
        if (SidebandContact.TryNormalizeDeliveryHash(value, out var hash)) return hash;
        var matches = state.Contacts.Where(c => c.ConversationKnown && string.Equals(Safe(c.DisplayName), Safe(value), StringComparison.OrdinalIgnoreCase))
            .Select(c => c.DeliveryHash);
        if (announces) matches = matches.Concat((state.Announces ?? []).Where(a => string.Equals(a.Name, value, StringComparison.OrdinalIgnoreCase)).Select(a => a.DestinationHash));
        var hashes = matches.Distinct().ToArray();
        return hashes.Length switch
        {
            1 => hashes[0], 0 => throw new ArgumentException("Unknown destination: " + Safe(value)),
            _ => throw new ArgumentException("Ambiguous destination name; use the hexadecimal hash.")
        };
    }

    private static string Hash(string value) => SidebandContact.TryNormalizeDeliveryHash(value.Trim(), out var hash) ? hash :
        throw new ArgumentException("A destination hash must contain exactly 32 hexadecimal characters.");
    private static int Count(string value, int maximum) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) &&
        count >= 1 && count <= maximum ? count : throw new ArgumentException($"Count must be between 1 and {maximum}.");
    private static int Nonnegative(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count >= 0 ? count :
        throw new ArgumentException("Sync limit must be a nonnegative 32-bit integer.");
    private static string Safe(string? value) => SidebandConsoleSyntax.Safe(value);
    private void ReportRuntimeLog(SidebandRuntimeLogEntry entry)
    {
        if (IsStreaming) _output($"LOG [{entry.Level}] {Safe(entry.Message)}");
    }

    public async ValueTask DisposeAsync()
    {
        Volatile.Write(ref _streaming, 0);
        _client.RuntimeLogReported -= ReportRuntimeLog;
        await _operations.DisposeAsync().ConfigureAwait(false);
    }
}
