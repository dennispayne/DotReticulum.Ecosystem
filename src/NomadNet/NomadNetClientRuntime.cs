namespace NomadNet;

public enum NomadNetUserInterface
{
    None = 0,
    Text = 2,
}

public static class NomadNetClientRuntime
{
    public static NomadNetUserInterface ParseInterface(string value) => value.ToLowerInvariant() switch
    {
        "none" => NomadNetUserInterface.None,
        "text" => NomadNetUserInterface.Text,
        "menu" => throw new InvalidDataException("client.user_interface=menu selects an unimplemented upstream placeholder."),
        _ => throw new InvalidDataException("client.user_interface must be none or text; graphical/web interfaces are upstream placeholders."),
    };

    // The primary's --textui cancels --daemon; it does not override the configured UI.
    public static bool IsHeadless(NomadNetSettings settings, bool daemon, bool textUi) =>
        settings.UserInterface == NomadNetUserInterface.None || daemon && !textUi;

    public static void NotifyReceived(NomadNetSettings settings, bool headless, string messageId,
        Action? notification, Action<string> log)
    {
        if (headless || !settings.NotifyOnNewMessage || notification is null) return;
        try { notification(); }
        catch (Exception exception) { log($"Could not notify for incoming message {messageId}: {exception.Message}"); }
    }
}
