namespace NomadNet;

internal static partial class Program
{
    private sealed partial class TerminalSession
    {
        private bool TryHandleGuide(string command, CancellationToken token)
        {
            var parts = Split(command, 3);
            if (parts[0] != "guide") return false;
            token.ThrowIfCancellationRequested();
            string text;
            if (parts.Length == 1) text = _guide.Open("start", token);
            else if (parts.Length == 2 && parts[1] == "topics")
                text = string.Join('\n', NomadNetGuide.Topics.Keys.Select((topic, index) => $"{index + 1}: {topic}"));
            else if (parts.Length == 2 && parts[1] == "links")
                text = string.Join('\n', _guide.Page.Links.Select((link, index) => $"{index + 1}: {link.Label} ({link.Target})"));
            else if (parts.Length == 2 && parts[1] == "back") text = _guide.Back(token);
            else if (parts.Length == 2 && parts[1] == "forward") text = _guide.Forward(token);
            else if (parts.Length == 2 && parts[1] == "next") text = _guide.Open("#", token);
            else if (parts.Length == 3 && parts[1] == "find")
            {
                var matches = _guide.Find(parts[2], token);
                text = matches.Count == 0 ? "No local guide matches." :
                    string.Join('\n', matches.Select(match => $"{match.Target} line {match.Line}: {match.Text}")) +
                    $"\n{matches.Count} local guide matches (maximum {NomadNetGuide.MaximumSearchResults}).";
            }
            else if (parts.Length == 2 && parts[1] == "view") text = _guide.View(cancellationToken: token);
            else if (parts.Length == 3 && int.TryParse(parts[2], out var number))
                text = parts[1] switch
                {
                    "open" => _guide.Follow(number, token),
                    "select" => _guide.Select(number, token),
                    "view" => _guide.View(number, cancellationToken: token),
                    "scroll" => _guide.View(scroll: number, cancellationToken: token),
                    _ => throw GuideUsage(),
                };
            else if (parts.Length == 2 && parts[1] is not ("open" or "select" or "find" or "scroll"))
                text = _guide.Open(parts[1], token);
            else throw GuideUsage();
            Console.WriteLine(Heading(text));
            return true;
        }

        private static ArgumentException GuideUsage() => new(
            "Usage: guide [topics|links|open <number>|select <number>|back|forward|next|view [1-100]|scroll <-10000..10000>|find <words>|<topic>#<anchor>]");
    }
}
