using System.Text;

namespace SharedPrinting;

internal static class PrintCommand
{
    public const int MaximumJobBytes = 2 * 1024 * 1024;

    public static IReadOnlyList<string> Arguments(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > 4096 || command.Any(char.IsControl))
            throw new InvalidDataException("Print command must be 1–4096 printable characters.");
        var result = new List<string>();
        var token = new StringBuilder();
        char quote = '\0';
        var started = false;
        for (var index = 0; index < command.Length; index++)
        {
            var character = command[index];
            if (quote == '\0' && character == ' ')
            {
                if (started) { result.Add(token.ToString()); token.Clear(); started = false; }
                continue;
            }
            started = true;
            if (character is '\'' or '"' && (quote == '\0' || quote == character))
            {
                quote = quote == '\0' ? character : '\0';
                continue;
            }
            if (character == '\\' && quote != '\'')
            {
                if (++index == command.Length) throw new InvalidDataException("Trailing print command escape.");
                var next = command[index];
                if (quote == '"' && next is not ('"' or '\\')) token.Append('\\');
                token.Append(next);
            }
            else token.Append(character);
        }
        if (quote != '\0') throw new InvalidDataException("Unterminated print command quote.");
        if (started) result.Add(token.ToString());
        if (result.Count is < 1 or > 64 || result[0].Length == 0)
            throw new InvalidDataException("Print command must have an executable and at most 64 arguments.");
        return result;
    }
}
