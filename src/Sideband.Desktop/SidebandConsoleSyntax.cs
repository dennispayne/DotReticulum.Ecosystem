using System.Text;

namespace Sideband.Desktop;

public sealed record SidebandConsoleCommand(string Name, int MinimumArguments = 0, int? MaximumArguments = null,
    string Source = "native", string Status = "implemented");

public sealed record SidebandConsoleInvocation(SidebandConsoleCommand Command, string[] Arguments);

/// <summary>Command names and bounds observed from primary Sideband d069122.</summary>
public static class SidebandConsoleSyntax
{
    public const int MaximumCharacters = 8192;
    public const int MaximumTokens = 256;

    public static IReadOnlyList<SidebandConsoleCommand> Commands { get; } = Array.AsReadOnly<SidebandConsoleCommand>(
    [
        new("help", 0, 10, "cmd_system.py"), new("raw", 1, null, "cmd_system.py", "unsupported"),
        new("log", 0, 1, "cmd_system.py", "implemented"), new("log stream", 0, 0, "cmd_system.py", "partial"),
        new("clear", 0, null, "cmd_system.py"), new("quit", 0, null, "cmd_system.py"),
        new("status", 0, 0, "cmd_system.py"),
        new("conv", 0, 0, "cmd_conv.py"), new("conv send", 2, null, "cmd_conv.py"),
        new("conv command", 2, null, "cmd_conv.py"), new("conv announce", 0, 0, "cmd_conv.py"),
        new("conv sync", 0, 1, "cmd_conv.py"), new("conv sync cancel", 0, 0, "cmd_conv.py"),
        new("conv sync status", 0, 0, "cmd_conv.py"), new("conv msgs", 1, 2, "cmd_conv.py"),
        new("conv create", 1, 2, "cmd_conv.py"), new("conv delete", 1, 1, "cmd_conv.py"),
        new("conv clear", 1, 1, "cmd_conv.py"), new("conv trust", 1, 1, "cmd_conv.py"),
        new("conv untrust", 1, 1, "cmd_conv.py"), new("conv name", 2, null, "cmd_conv.py"),
        new("conv paper", 2, null, "cmd_conv.py"),
        new("net propagation", 0, 1, "cmd_net.py"), new("net propagation set", 1, 1, "cmd_net.py"),
        new("net announces", 0, 0, "cmd_net.py"), new("net identity", 0, 0, "cmd_net.py"),
        new("net link", 1, 1, "cmd_net.py"),
        new("config", 0, 0, "cmd_config.py"), new("config list", 0, 1, "cmd_config.py", "partial"),
        new("config get", 2, 2, "cmd_config.py", "partial"), new("config set", 2, null, "cmd_config.py", "partial"),
        new("config save", 0, 0, "cmd_config.py"), new("config reload", 0, 0, "cmd_config.py"),
        new("telemetry", 0, 1, "cmd_telemetry.py"), new("telemetry request", 1, 1, "cmd_telemetry.py"),
        new("telemetry send", 1, 1, "cmd_telemetry.py"), new("telemetry track", 3, 3, "cmd_telemetry.py"),
        new("telemetry untrack", 1, 1, "cmd_telemetry.py"), new("telemetry list", 0, 2, "cmd_telemetry.py"),
        new("telemetry on", 0, 0, "cmd_telemetry.py"), new("telemetry off", 0, 0, "cmd_telemetry.py"),
        new("telemetry sensors", 0, 0, "cmd_telemetry.py", "partial"),
        new("voice status", 0, 0, "cmd_voice.py", "native-extension"),
        new("voice announce", 0, 0, "cmd_voice.py", "native-extension"),
        new("voice dial", 1, 2, "cmd_voice.py", "native-extension"),
        new("voice hangup", 0, 0, "cmd_voice.py", "native-extension"),
        new("voice answer", 0, 0, "cmd_voice.py", "native-extension"),
        new("voice log", 0, 0, "cmd_voice.py", "native-extension"),
        new("voice mute mic", 0, 0, "cmd_voice.py", "native-extension"),
        new("voice mute spk", 0, 0, "cmd_voice.py", "native-extension"),
        new("voice unmute mic", 0, 0, "cmd_voice.py", "native-extension"),
        new("voice unmute spk", 0, 0, "cmd_voice.py", "native-extension"),
        new("voice profile", 1, 1, "cmd_voice.py", "native-extension"),
        new("voice mode", 1, 1, "cmd_voice.py", "native-extension"),
        new("plugins", 0, 0, "cmd_plugins.py", "unsupported"),
        new("plugins reload", 0, 0, "cmd_plugins.py", "unsupported"),
        new("exit", 0, 0), new("cancel", 0, 0), new("voice enable", 0, 0), new("voice disable", 0, 0),
        new("voice reject", 0, 0), new("voice log clear", 0, 0), new("voice ptt on", 0, 0), new("voice ptt off", 0, 0),
        new("conv retry-text", 2, 2), new("conv cancel-message", 2, 2)
    ]);

    public static string[] Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Length > MaximumCharacters) throw new ArgumentException("Console commands are limited to 8,192 characters.");
        var result = new List<string>();
        var word = new StringBuilder();
        char quote = '\0';
        var started = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (char.IsControl(character) && character != '\t')
                throw new ArgumentException("Console commands cannot contain control characters.");
            if (character == '\\' && quote != '\'')
            {
                if (++index == line.Length) throw new ArgumentException("A trailing escape is not valid.");
                var escaped = line[index];
                if (char.IsControl(escaped)) throw new ArgumentException("An escape cannot contain a control character.");
                if (quote == '"' && escaped is not ('"' or '\\')) word.Append('\\');
                word.Append(escaped);
                started = true;
            }
            else if (quote != '\0')
            {
                if (character == quote) quote = '\0';
                else word.Append(character);
            }
            else if (character is '\'' or '"') { quote = character; started = true; }
            else if (char.IsWhiteSpace(character)) AddWord();
            else { word.Append(character); started = true; }
        }
        if (quote != '\0') throw new ArgumentException("Unbalanced quotes are not valid.");
        AddWord();
        return result.ToArray();

        void AddWord()
        {
            if (!started) return;
            if (result.Count == MaximumTokens) throw new ArgumentException("Console commands are limited to 256 tokens.");
            result.Add(word.ToString());
            word.Clear();
            started = false;
        }
    }

    public static SidebandConsoleInvocation? Resolve(string[] tokens)
    {
        for (var length = Math.Min(tokens.Length, 4); length > 0; length--)
        {
            var name = string.Join(' ', tokens.Take(length));
            var command = Commands.FirstOrDefault(c => c.Name == name);
            if (command is null) continue;
            var arguments = tokens[length..];
            if (arguments.Length < command.MinimumArguments) throw new ArgumentException("Too few arguments. " + Usage(command));
            if (command.MaximumArguments is { } maximum && arguments.Length > maximum)
                throw new ArgumentException("Too many arguments. " + Usage(command));
            return new(command, arguments);
        }
        if (tokens.Length == 0) return null;
        throw new ArgumentException("Unknown command. Type help.");
    }

    public static string Usage(SidebandConsoleCommand command) =>
        $"{command.Name} (arguments: {command.MinimumArguments}..{command.MaximumArguments?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "bounded remainder"}) [{command.Status}; {command.Source}]";

    public static string Safe(string? value) => value is null ? "none" :
        string.Concat(value.Select(c => char.IsControl(c) || c is '\u202a' or '\u202b' or '\u202c' or '\u202d' or '\u202e' or '\u2066' or '\u2067' or '\u2068' or '\u2069' ? ' ' : c));
}
