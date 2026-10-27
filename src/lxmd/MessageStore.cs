using DotLXMF;

namespace Lxmd;

internal sealed class MessageStore
{
    private readonly string _directory;
    private readonly object _gate = new();

    public MessageStore(string directory)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    public string Save(LxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Hash is not { Length: > 0 } hash) throw new InvalidDataException("Received LXMF message has no hash.");
        var target = Path.Combine(_directory, Convert.ToHexStringLower(hash));
        var temporary = target + ".tmp." + Guid.NewGuid().ToString("N");
        var bytes = message.PackedContainer();
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            lock (_gate) File.Move(temporary, target, overwrite: true);
            return target;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
        }
    }
}
