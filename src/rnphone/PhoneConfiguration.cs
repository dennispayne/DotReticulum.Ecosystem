namespace Rnphone;

internal sealed class PhoneConfiguration
{
    public const string Default = "[telephone]\nallowed_callers = all\n\n[phonebook]\n\n[hardware]\n";
    public string? Speaker { get; private set; }
    public string? Microphone { get; private set; }
    public string? Ringer { get; private set; }
    public string? Ringtone { get; private set; }
    public string Allowed { get; private set; } = "all";
    public HashSet<string> Blocked { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, (string Hash, string? Alias)> Phonebook { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static PhoneConfiguration Load(string path)
    {
        var result = new PhoneConfiguration();
        var section = "";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var original in File.ReadLines(path))
        {
            var line = original.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim().ToLowerInvariant();
                if (section is not ("telephone" or "phonebook" or "hardware")) throw new FormatException($"Unknown configuration section [{section}].");
                continue;
            }
            var separator = line.IndexOf('=');
            if (separator <= 0) throw new FormatException("Invalid rnphone configuration entry.");
            var key = line[..separator].Trim();
            var values = ParseValues(line[(separator + 1)..]);
            var value = string.Join(',', values);
            if (!seen.Add(section + "\0" + key)) throw new FormatException($"Duplicate rnphone setting: {section}.{key}.");
            switch (section)
            {
                case "telephone":
                    switch (key.ToLowerInvariant())
                    {
                        case "speaker": result.Speaker = value; break;
                        case "microphone": result.Microphone = value; break;
                        case "allowed_callers": result.Allowed = value.ToLowerInvariant(); break;
                        case "blocked_callers":
                            foreach (var hash in values.Where(hash => hash.Length != 0))
                                result.Blocked.Add(ValidateHash(hash));
                            break;
                        case "ringer": result.Ringer = value; break;
                        case "ringtone": result.Ringtone = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, value)); break;
                        default: throw new NotSupportedException($"Unsupported rnphone setting: telephone.{key}.");
                    }
                    break;
                case "phonebook":
                    var parts = values;
                    var alias = parts.Length == 2 ? new string(parts[1].Where(char.IsAsciiDigit).ToArray()) : null;
                    result.Phonebook.Add(key, (ValidateHash(parts[0]), string.IsNullOrEmpty(alias) ? null : alias));
                    break;
                case "hardware": throw new NotSupportedException($"Hardware setting {key} is not implemented by this build.");
                default: throw new FormatException("rnphone settings must be inside a section.");
            }
        }
        if (result.Allowed is not ("all" or "none" or "phonebook"))
            foreach (var hash in result.Allowed.Split(',', StringSplitOptions.TrimEntries)) _ = ValidateHash(hash);
        return result;
    }

    public void ExcludeLocalIdentity(string hash)
    {
        foreach (var name in Phonebook.Where(entry => entry.Value.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase)).Select(entry => entry.Key).ToArray())
            Phonebook.Remove(name);
        Blocked.Remove(hash);
    }

    private static string[] ParseValues(string text)
    {
        var result = new List<string>();
        var value = new System.Text.StringBuilder();
        var quote = '\0';
        foreach (var character in text)
        {
            if (quote != '\0')
            {
                if (character == quote) quote = '\0';
                else value.Append(character);
            }
            else if (character is '\'' or '"') quote = character;
            else if (character is '#' or ';') break;
            else if (character == ',') { result.Add(value.ToString().Trim()); value.Clear(); }
            else value.Append(character);
        }
        if (quote != '\0') throw new FormatException("Unterminated quoted rnphone value.");
        result.Add(value.ToString().Trim());
        return result.ToArray();
    }

    public bool Authorize(string hash)
    {
        if (Blocked.Contains(hash)) return false;
        return Allowed switch
        {
            "all" => true,
            "none" => false,
            "phonebook" => Phonebook.Values.Any(entry => entry.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase)),
            _ => Allowed.Split(',', StringSplitOptions.TrimEntries).Contains(hash, StringComparer.OrdinalIgnoreCase),
        };
    }

    public static string ValidateHash(string hash)
    {
        if (hash.Length != 32 || !hash.All(char.IsAsciiHexDigit)) throw new FormatException("Telephone identity hashes must contain 32 hexadecimal characters.");
        return hash.ToLowerInvariant();
    }
}
