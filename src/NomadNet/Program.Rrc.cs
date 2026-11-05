namespace NomadNet;

internal static partial class Program
{
    private sealed partial class TerminalSession
    {
        private bool _rrcUsersVisible = true;
        private bool _rrcChannelsVisible = true;

        private void PrintRrcMessage(RrcChatMessage message, bool historical)
        {
            var context = _rrc?.Presentation;
            Console.WriteLine($"RRC{(historical ? " history" : "")} [{NomadNetTerminalText.Safe(message.Room ?? "hub")}] " +
                (message.ImportedHistory ? "(imported; unauthenticated) " : "") +
                NomadNetRrcPresentation.RenderStream(message, propagation.Settings, Convert.ToHexStringLower(identity.Hash),
                    context?.Nick ?? "", context?.Nicks, !Console.IsOutputRedirected));
        }

        private async Task<bool> TryHandleRrcViewAsync(string command, CancellationToken token)
        {
            var parts = Split(command, 4);
            if (parts.Length == 0) return false;
            if (parts[0] == "pane")
            {
                if (parts.Length != 3 || parts[1] is not ("users" or "channels") || parts[2] is not ("on" or "off"))
                    throw new ArgumentException("Use rrc pane users|channels on|off.");
                if (parts[1] == "users") _rrcUsersVisible = parts[2] == "on";
                else _rrcChannelsVisible = parts[2] == "on";
                Console.WriteLine("RRC pane selection changed; use rrc view.");
                return true;
            }
            if (parts[0] != "view") return false;
            if (parts.Length > 3 || parts.Length == 3 && parts[2] != "color")
                throw new ArgumentException("Use rrc view [room] [color]. Explicit color emits safe native ANSI styling even to a pipe.");
            var client = _rrc ?? throw new InvalidOperationException("Open an RRC hub first.");
            var settings = propagation.Settings;
            var room = parts.Length >= 2 ? RrcEnvelope.NormalizeRoom(parts[1]) : null;
            var color = parts.Length == 3 || !Console.IsOutputRedirected;
            var channels = NomadNetRrcPresentation.CollapsedPane("channels", _rrcChannelsVisible, settings.RrcShowGutters);
            if (channels.Length != 0) Console.WriteLine("RRC " + channels);
            if (_rrcChannelsVisible)
                foreach (var name in await client.ViewRoomsAsync(token).ConfigureAwait(false))
                    Console.WriteLine("RRC channel #" + NomadNetTerminalText.Safe(name));
            var context = client.Presentation;
            foreach (var message in await client.HistoryAsync(room, token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                Console.WriteLine(NomadNetRrcPresentation.Render(message, settings, Convert.ToHexStringLower(identity.Hash),
                    context.Nick, context.Nicks, color));
            }
            var users = NomadNetRrcPresentation.CollapsedPane("users", _rrcUsersVisible, settings.RrcShowGutters);
            if (users.Length != 0) Console.WriteLine("RRC " + users);
            if (_rrcUsersVisible && room is not null)
                foreach (var member in await client.MembersAsync(room, token).ConfigureAwait(false))
                {
                    var name = NomadNetTerminalText.Safe(member.Nick ?? member.Hash);
                    if (color && settings.RrcNickColors)
                    {
                        var rgb = Convert.FromHexString(NomadNetRrcPresentation.NickColor(member.Hash, settings));
                        name = $"\u001b[38;2;{rgb[0]};{rgb[1]};{rgb[2]}m{name}\u001b[0m";
                    }
                    Console.WriteLine("RRC user " + name);
                }
            Console.WriteLine("RRC view complete.");
            return true;
        }
    }
}
