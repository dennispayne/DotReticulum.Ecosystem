namespace Rnphone;

public static class TerminalCommands
{
    public static string CommandWord(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var space = command.IndexOf(' ');
        return space < 0 ? command : command[..space];
    }

    public static bool IsQuit(string command) => IdleAction(command) == "quit";

    public static string? IdleAction(string command, IReadOnlyDictionary<string, string>? phonebook = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Length == 32) return "dial";
        var word = CommandWord(command);
        if (phonebook is not null)
            return word is "back" or "b" or "quit" or "q" ? "back" : phonebook.ContainsKey(word) ? "dial" : null;
        return word switch
        {
            "quit" or "q" or "exit" => "quit",
            "identity" or "i" => "identity",
            "desthash" or "d" => "desthash",
            "anounce" or "a" => "announce",
            "phonebook" or "p" => "phonebook",
            "redial" or "r" => "redial",
            "help" or "h" or "?" => "help",
            _ => null,
        };
    }

    public static string? CallAction(LXST.CallState state, bool incoming, string command) => state switch
    {
        LXST.CallState.Ringing when incoming => command.Length == 0 ? "answer" : "hangup",
        LXST.CallState.Ringing or LXST.CallState.Established or LXST.CallState.Calling or LXST.CallState.Connecting
            or LXST.CallState.Busy or LXST.CallState.Rejected => "hangup",
        _ => null,
    };
}
