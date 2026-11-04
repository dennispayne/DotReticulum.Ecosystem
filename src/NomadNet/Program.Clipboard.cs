namespace NomadNet;

internal static partial class Program
{
    private sealed partial class TerminalSession
    {
        private async Task<bool> TryHandleClipboardAsync(string command, CancellationToken cancellationToken)
        {
            if (command.Equals("clipboard", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("clipboard ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = Split(command, 3);
                if (parts.Length > 1)
                {
                    if (parts.Length != 2 || parts[1] is not ("on" or "off"))
                        throw new ArgumentException("Usage: clipboard [on|off]");
                    await propagation.UpdateSettingsAsync(current => current with { ClipboardCopy = parts[1] == "on" },
                        cancellationToken).ConfigureAwait(false);
                }
                Console.WriteLine($"OSC52 clipboard copying: {(propagation.Settings.ClipboardCopy ? "enabled" : "disabled")}. Requires a trusted terminal with OSC52 support; no system clipboard access is performed.");
                return true;
            }
            if (!command.Equals("copy", StringComparison.OrdinalIgnoreCase) &&
                !command.StartsWith("copy ", StringComparison.OrdinalIgnoreCase)) return false;
            var arguments = Split(command, 4);
            string? text;
            if (arguments.Length == 2 && arguments[1] == "url")
                text = NomadNetClipboard.BrowserUrl(_currentNode, _currentPath,
                    _currentRequestData switch
                    {
                        null => null,
                        IReadOnlyDictionary<object, object?> fields => fields,
                        _ => throw new InvalidDataException("Current browser form has an unsupported shape."),
                    });
            else if (arguments.Length == 3 && arguments[1] == "message")
                text = (await store.MessagesAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
                    .SingleOrDefault(message => message.Id == arguments[2])?.Content ??
                    throw new ArgumentException("Stored message ID not found.");
            else throw new ArgumentException("Usage: copy message <id> | copy url");
            cancellationToken.ThrowIfCancellationRequested();
            var copied = NomadNetClipboard.TryCopy(text, Console.Out, message => logging.Write(1, message),
                propagation.Settings.ClipboardCopy);
            Console.WriteLine(copied ? "Clipboard sequence emitted." : "Nothing copied; text is absent, policy is disabled or copying failed.");
            return true;
        }
    }
}
