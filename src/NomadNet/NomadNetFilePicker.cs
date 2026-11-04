using System.Diagnostics;
using System.Security.Cryptography;

namespace NomadNet;

public sealed record NomadNetFileEntry(string Name, string Path, bool IsDirectory, bool IsParent = false);

/// <summary>Session-local file selection; selected bytes are read only when sending.</summary>
public sealed class NomadNetFilePicker
{
    public const int MaximumDirectoryEntries = 4096;
    private readonly List<string> _selected = [];
    private readonly bool _multiple;
    private readonly int _maximumBytes;
    private IReadOnlyList<NomadNetFileEntry> _entries = [];
    private bool _finished;

    public NomadNetFilePicker(string? startPath = null, IEnumerable<string>? selected = null, bool multiple = true,
        int maximumBytes = NomadNetAttachments.MaximumBytes)
    {
        if (maximumBytes is < 0 or > NomadNetAttachments.MaximumBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _multiple = multiple;
        _maximumBytes = maximumBytes;
        if (selected is not null)
        {
            var inspected = 0;
            foreach (var path in selected)
            {
                if (++inspected > NomadNetAttachments.MaximumAttachments) throw new InvalidDataException("Too many selected attachments.");
                var full = Path.GetFullPath(path);
                if (!_selected.Contains(full, PathComparer)) _selected.Add(full);
            }
        }
        CurrentDirectory = Path.GetFullPath(NomadNetConfigImport.ExpandHome(startPath ?? "~"));
        Refresh();
    }

    public string CurrentDirectory { get; private set; }
    public string? Notice { get; private set; }
    public IReadOnlyList<NomadNetFileEntry> Entries => _entries;
    public IReadOnlyList<string> Selected => Array.AsReadOnly(_selected.ToArray());
    public event Action<IReadOnlyList<string>>? Changed;
    public event Action<IReadOnlyList<string>>? Completed;
    public event Action? Cancelled;
    public Action<string>? Log { get; set; }
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public void Refresh()
    {
        EnsureActive();
        Notice = null;
        var rows = new List<NomadNetFileEntry>();
        if (Directory.GetParent(CurrentDirectory) is { } parent) rows.Add(new("..", parent.FullName, false, true));
        try
        {
            var files = new List<NomadNetFileEntry>();
            var directories = new List<NomadNetFileEntry>();
            var inspected = 0;
            foreach (var path in Directory.EnumerateFileSystemEntries(CurrentDirectory))
            {
                if (++inspected > MaximumDirectoryEntries) throw new InvalidDataException("Directory exceeds the 4096-entry limit.");
                var name = Path.GetFileName(path);
                if (name.StartsWith('.') || name.Any(char.IsControl) ||
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                if (Directory.Exists(path)) directories.Add(new(name, path, true));
                else if (NomadNetLocalFiles.IsRegularFile(path)) files.Add(new(name, path, false));
            }
            var comparer = Comparer<string>.Create(NomadNetDirectoryPolicy.CompareNames);
            rows.AddRange(directories.OrderBy(entry => entry.Name, comparer));
            rows.AddRange(files.OrderBy(entry => entry.Name, comparer));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Notice = exception is InvalidDataException ? exception.Message : "Cannot read this directory.";
        }
        _entries = rows.AsReadOnly();
    }

    public void Open(int index)
    {
        EnsureActive();
        if ((uint)index >= _entries.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var entry = _entries[index];
        if (entry.IsDirectory || entry.IsParent)
        {
            if ((File.GetAttributes(entry.Path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Choose a directory, not a symbolic link.");
            CurrentDirectory = entry.Path;
            Refresh();
            return;
        }
        var existing = _selected.FindIndex(path => PathComparer.Equals(path, entry.Path));
        if (existing < 0)
        {
            if (!NomadNetLocalFiles.IsRegularFile(entry.Path)) throw new IOException("The selected entry is no longer a regular file.");
            long total = new FileInfo(entry.Path).Length;
            if (_multiple)
                foreach (var path in _selected) total = checked(total + new FileInfo(path).Length);
            if (new FileInfo(entry.Path).Length > _maximumBytes || total > NomadNetAttachments.MaximumBytes ||
                _multiple && _selected.Count >= NomadNetAttachments.MaximumAttachments)
            {
                Notice = "Selection exceeds the attachment size or count limit.";
                return;
            }
        }
        if (_multiple)
        {
            if (existing >= 0) _selected.RemoveAt(existing);
            else _selected.Add(entry.Path);
        }
        else
        {
            _selected.Clear();
            _selected.Add(entry.Path);
        }
        Notify(Changed, Selected, "file selection changed");
        if (_finished) return;
        if (!_multiple) Done();
        else Refresh();
    }

    public IReadOnlyList<string> Done()
    {
        EnsureActive();
        _finished = true;
        var snapshot = Selected;
        Notify(Completed, snapshot, "file selection completed");
        return snapshot;
    }

    public void Cancel()
    {
        EnsureActive();
        _selected.Clear();
        _finished = true;
        if (Cancelled is { } callbacks)
            foreach (Action callback in callbacks.GetInvocationList())
                try { callback(); }
                catch (Exception exception) { Report("file selection cancelled", exception); }
    }

    public static async Task<Dictionary<object, object?>> ReadAttachmentsAsync(IEnumerable<string> paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var fields = new List<object?>();
        var total = 0;
        try
        {
            foreach (var path in paths)
            {
                if (fields.Count >= NomadNetAttachments.MaximumAttachments) throw new InvalidDataException("Too many selected attachments.");
                var bytes = await NomadNetLocalFiles.ReadAsync(path, NomadNetAttachments.MaximumBytes - total, cancellationToken).ConfigureAwait(false);
                total = checked(total + bytes.Length);
                fields.Add(new object?[] { NomadNetAttachments.SafeName(Path.GetFileName(path)), bytes });
            }
            if (fields.Count == 0) throw new InvalidOperationException("Select at least one attachment first.");
            return new() { [LXMF.LxmField.FileAttachments] = fields.ToArray() };
        }
        catch
        {
            foreach (var item in fields)
                if (item is object?[] file && file[1] is byte[] bytes) CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    private void EnsureActive()
    {
        if (_finished) throw new InvalidOperationException("This file selection has already completed.");
    }

    private void Notify(Action<IReadOnlyList<string>>? callbacks, IReadOnlyList<string> snapshot, string context)
    {
        if (callbacks is null) return;
        foreach (Action<IReadOnlyList<string>> callback in callbacks.GetInvocationList())
            try { callback(snapshot); }
            catch (Exception exception) { Report(context, exception); }
    }

    private void Report(string context, Exception exception)
    {
        var message = $"NomadNet {context} observer failed: {exception.Message}";
        Trace.TraceError(message);
        if (Log is not { } callbacks) return;
        foreach (Action<string> callback in callbacks.GetInvocationList())
            try { callback(message); }
            catch (Exception observer) { Trace.TraceError("NomadNet file picker log observer: {0}", observer.Message); }
    }
}
