namespace Sideband;

public sealed record SidebandRuntimeLogEntry(int Level, string Message);

public sealed partial class SidebandClient
{
    private int _runtimeLogLevel = 2;

    public event Action<SidebandRuntimeLogEntry>? RuntimeLogReported;

    internal DotReticulum.Applications.ReticulumNode? RuntimeNode => _node;

    public int RuntimeLogLevel
    {
        get => Volatile.Read(ref _runtimeLogLevel);
        set
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 7);
            Volatile.Write(ref _runtimeLogLevel, value);
        }
    }

    internal void ReportRuntimeLog(int level, string message)
    {
        if (level > RuntimeLogLevel) return;
        if (level <= 1) Report(message);
        if (RuntimeLogReported is not { } callbacks)
        {
            if (level > 1) System.Diagnostics.Trace.WriteLine($"Sideband runtime [{level}]: {message}");
            return;
        }
        var entry = new SidebandRuntimeLogEntry(level, message);
        foreach (Action<SidebandRuntimeLogEntry> callback in callbacks.GetInvocationList())
        {
            try { callback(entry); }
            catch (Exception ex) { Report("A Sideband runtime log observer failed: " + ex.Message); }
        }
    }
}
