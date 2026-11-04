namespace NomadNet;

internal sealed class NomadNetProfileLease : IDisposable
{
    private readonly FileStream _stream;

    private NomadNetProfileLease(FileStream stream) => _stream = stream;

    public static NomadNetProfileLease Acquire(string directory)
    {
        Directory.CreateDirectory(directory);
        try
        {
            return new(new FileStream(Path.Combine(directory, ".nomadnet.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException exception)
        {
            throw new IOException("Native profile is already in use or cannot be locked; stop its process before offline import.", exception);
        }
    }

    // Keep the inode after releasing ownership; unlinking could split concurrent owners onto different files.
    public void Dispose() => _stream.Dispose();
}
