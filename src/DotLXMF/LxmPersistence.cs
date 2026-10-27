namespace DotLXMF;

internal static class LxmPersistence
{
    internal static void RemoveInterruptedWrites(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory)) return;
        var prefix = Path.GetFileName(path);
        foreach (var output in Directory.EnumerateFiles(directory, prefix + ".*.out"))
        {
            var name = Path.GetFileName(output);
            if (name.Length == prefix.Length + 37 && Guid.TryParseExact(name.AsSpan(prefix.Length + 1, 32), "N", out _))
                File.Delete(output);
        }
    }
}
