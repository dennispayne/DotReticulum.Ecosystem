namespace Rnphone;

public static class TerminalCommands
{
    public static bool IsQuit(string command) => command is "quit" or "q" or "exit";

    public static string? IdleAction(string command) => command switch
    {
        "quit" or "q" or "exit" => "quit",
        "identity" or "i" => "identity",
        "desthash" or "d" => "desthash",
        "anounce" or "a" => "announce",
        "phonebook" or "p" => "phonebook",
        "redial" or "r" => "redial",
        _ => null,
    };

    public static string? CallAction(LXST.CallState state, bool incoming, string command) => state switch
    {
        LXST.CallState.Ringing when incoming => command.Length == 0 ? "answer" : "hangup",
        LXST.CallState.Established or LXST.CallState.Calling or LXST.CallState.Connecting => "hangup",
        _ => null,
    };
}
