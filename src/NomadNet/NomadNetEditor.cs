using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NomadNet;

/// <summary>Original bounded text buffer. Positions are UTF-16 offsets, not terminal columns.</summary>
public sealed class NomadNetEditorBuffer
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public string Text { get; private set; } = "";
    public int Cursor { get; private set; }
    public bool Dirty { get; private set; }
    public bool Editable { get; private set; } = true;

    public void Load(string text, bool editable = true)
    {
        Validate(text);
        Text = text;
        Cursor = 0;
        Dirty = false;
        Editable = editable;
    }

    public void Set(string text)
    {
        if (!Editable) throw new InvalidOperationException("Binary files are read-only.");
        Validate(text);
        Text = text;
        Cursor = Math.Min(Cursor, text.Length);
        Dirty = true;
    }

    public void Insert(string text, int? position = null)
    {
        var offset = position ?? Cursor;
        if (offset < 0 || offset > Text.Length) throw new ArgumentOutOfRangeException(nameof(position));
        if ((long)Text.Length + text.Length > NomadNetEditor.MaximumBytes)
            throw new InvalidDataException("Editor text exceeds its one-megabyte limit.");
        Set(Text.Insert(offset, text));
        Cursor = offset + text.Length;
    }

    public int Find(string term)
    {
        if (term.Length == 0) return -1;
        var index = Text.IndexOf(term, Cursor, StringComparison.Ordinal);
        if (index < 0) index = Text.IndexOf(term, StringComparison.Ordinal);
        if (index >= 0) Cursor = index + term.Length;
        return index;
    }

    public bool ReplaceOne(string term, string replacement)
    {
        if (term.Length == 0) return false;
        var start = Cursor - term.Length;
        if (start < 0 || !Text.AsSpan(start, term.Length).SequenceEqual(term))
        {
            Find(term);
            return false;
        }
        if ((long)Text.Length - term.Length + replacement.Length > NomadNetEditor.MaximumBytes)
            throw new InvalidDataException("Editor text exceeds its one-megabyte limit.");
        Set(Text[..start] + replacement + Text[Cursor..]);
        Cursor = start + replacement.Length;
        return true;
    }

    public int ReplaceAll(string term, string replacement)
    {
        if (term.Length == 0) return 0;
        var count = 0;
        for (var index = 0; (index = Text.IndexOf(term, index, StringComparison.Ordinal)) >= 0; index += term.Length) count++;
        if (count != 0)
        {
            // Check before String.Replace allocates its potentially expanded result.
            if ((long)Text.Length + (long)count * (replacement.Length - term.Length) > NomadNetEditor.MaximumBytes)
                throw new InvalidDataException("Editor text exceeds its one-megabyte limit.");
            Set(Text.Replace(term, replacement, StringComparison.Ordinal));
        }
        return count;
    }

    internal void Saved() => Dirty = false;

    private static void Validate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > NomadNetEditor.MaximumBytes || Utf8.GetByteCount(text) > NomadNetEditor.MaximumBytes)
            throw new InvalidDataException("Editor text exceeds its one-megabyte UTF-8 limit.");
    }
}

public sealed record NomadNetEditorState(int SchemaVersion = 1, string? Scope = null, string? Path = null);

/// <summary>
/// Single-console-session editor. The profile lease excludes other native processes; content snapshots
/// reject ordinary external changes. Filesystem administrators racing path checks are not sandboxed.
/// </summary>
public sealed partial class NomadNetEditor : IDisposable
{
    public const int MaximumBytes = 1_000_000;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly SemaphoreSlim MutationGate = new(1, 1);
    private readonly string _profile;
    private readonly string? _rnsFile;
    private readonly NomadNetSettings _configurationBasis;
    private readonly Dictionary<string, string> _roots;
    private byte[]? _snapshot;
    private byte[]? _allowedSnapshot;
    private bool _disposed;
    public NomadNetEditorBuffer Buffer { get; } = new();
    public string? Scope { get; private set; }
    public string? RelativePath { get; private set; }
    public bool IsOpen => Scope is not null;
    public bool Executable { get; private set; }
    private string Current => Resolve(Scope ?? throw new InvalidOperationException("Open a document first."), RelativePath!);

    public NomadNetEditor(string profile, NomadNetSettings settings, string? selectedRnsFile = null)
    {
        _profile = System.IO.Path.GetFullPath(profile);
        _configurationBasis = settings.Copy();
        _rnsFile = selectedRnsFile is null ? null : System.IO.Path.GetFullPath(selectedRnsFile);
        if (_rnsFile is not null) Guard(_rnsFile);
        _roots = new(StringComparer.Ordinal)
        {
            ["pages"] = System.IO.Path.GetFullPath(settings.PagesPath ?? System.IO.Path.Combine(_profile, "storage", "pages")),
            ["files"] = System.IO.Path.GetFullPath(settings.FilesPath ?? System.IO.Path.Combine(_profile, "storage", "files")),
            ["config"] = _profile,
        };
        foreach (var root in _roots.Values) Guard(root);
    }

    public string Resolve(string scope, string relative)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (scope == "rns")
        {
            if (relative != "selected" || _rnsFile is null)
                throw new ArgumentException("Only the explicitly selected running RNS configuration is editable.");
            Guard(_rnsFile);
            return _rnsFile;
        }
        if (scope == "primary")
        {
            if (relative != "config") throw new ArgumentException("Only the owned profile's primary ConfigObj file is editable.");
            var primary = System.IO.Path.Combine(_profile, "config");
            Guard(primary);
            return primary;
        }
        if (!_roots.TryGetValue(scope, out var root)) throw new ArgumentException("Scope must be pages, files or config.");
        if (scope == "config" && relative != "settings.json") throw new ArgumentException("Only settings.json is editable as configuration.");
        ArgumentException.ThrowIfNullOrWhiteSpace(relative);
        if (relative.Length > 512 || System.IO.Path.IsPathRooted(relative) || relative.Any(char.IsControl))
            throw new ArgumentException("Use a bounded relative document path.");
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(part => part.Length == 0 || part.StartsWith('.') || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0 ||
                part.EndsWith(".allowed", StringComparison.OrdinalIgnoreCase) || IsDevice(part)))
            throw new ArgumentException("Hidden, traversal, device and .allowed paths are not editable documents.");
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, System.IO.Path.Combine(parts)));
        Guard(full);
        return full;
    }

    public async Task OpenAsync(string scope, string relative, bool create = false, CancellationToken cancellationToken = default)
    {
        if (Buffer.Dirty) throw new InvalidOperationException("Unsaved changes: use edit close save, discard or cancel first.");
        var path = Resolve(scope, relative);
        var bytes = await SnapshotAsync(path, cancellationToken).ConfigureAwait(false);
        byte[]? allowed = null;
        var retained = false;
        try
        {
            if (create && bytes is not null) throw new IOException("New document already exists.");
            if (!create && bytes is null) throw new FileNotFoundException("Document not found.");
            allowed = IsConfigurationScope(scope) ? null : await SnapshotAsync(path + ".allowed", cancellationToken).ConfigureAwait(false);
            var editable = true;
            string text;
            try { text = bytes is null ? "" : Utf8.GetString(bytes); }
            catch (DecoderFallbackException) { text = ""; editable = false; }
            await WriteStateAsync(new(Scope: scope, Path: relative), cancellationToken).ConfigureAwait(false);
            Buffer.Load(text, editable);
            _configurationForm = null;
            ClearSnapshots();
            _snapshot = bytes;
            _allowedSnapshot = allowed;
            retained = true;
            Scope = scope;
            RelativePath = relative;
            Executable = bytes is not null && !OperatingSystem.IsWindows() &&
                (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        finally
        {
            if (!retained)
            {
                if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
                if (allowed is not null) CryptographicOperations.ZeroMemory(allowed);
            }
        }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        var bytes = await SnapshotAsync(System.IO.Path.Combine(_profile, "editor_state.json"), cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("No saved editor selection.");
        if (bytes.Length > 4096) throw new InvalidDataException("Editor selection exceeds its limit.");
        var state = JsonSerializer.Deserialize(bytes, NomadNetEditorJsonContext.Default.NomadNetEditorState)
            ?? throw new InvalidDataException("Invalid editor selection.");
        if (state.SchemaVersion != 1 || state.Scope is null || state.Path is null) throw new InvalidDataException("No valid saved editor selection.");
        await OpenAsync(state.Scope, state.Path, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        MutateAsync(() => SaveCoreAsync(cancellationToken), cancellationToken);

    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        var path = Current;
        if (!Buffer.Editable) throw new InvalidOperationException("Binary files are read-only.");
        var bytes = Utf8.GetBytes(Buffer.Text);
        var retained = false;
        try
        {
            if (IsConfiguration) ValidateConfiguration(bytes, Scope!);
            await CheckAsync(path, _snapshot, cancellationToken).ConfigureAwait(false);
            await WriteAsync(path, bytes, _snapshot is not null, cancellationToken, _snapshot, verifySnapshot: true).ConfigureAwait(false);
            if (_snapshot is not null) CryptographicOperations.ZeroMemory(_snapshot);
            _snapshot = bytes;
            retained = true;
            Buffer.Saved();
        }
        finally { if (!retained) CryptographicOperations.ZeroMemory(bytes); }
    }

    public Task SaveAsAsync(string scope, string relative, CancellationToken cancellationToken = default) =>
        MutateAsync(() => SaveAsCoreAsync(scope, relative, cancellationToken), cancellationToken);

    private async Task SaveAsCoreAsync(string scope, string relative, CancellationToken cancellationToken)
    {
        if (!IsOpen || !Buffer.Editable) throw new InvalidOperationException("Open an editable document first.");
        if (IsConfiguration || IsConfigurationScope(scope)) throw new InvalidOperationException("Configuration cannot use save-as.");
        await CheckAsync(Current, _snapshot, cancellationToken).ConfigureAwait(false);
        await CheckAsync(Current + ".allowed", _allowedSnapshot, cancellationToken).ConfigureAwait(false);
        var path = Resolve(scope, relative);
        if (await SnapshotAsync(path, cancellationToken).ConfigureAwait(false) is not null ||
            await SnapshotAsync(path + ".allowed", cancellationToken).ConfigureAwait(false) is not null)
            throw new IOException("Save-as never overwrites a document or its permissions.");
        // Copying a restricted document must not silently publish an unrestricted copy.
        if (_allowedSnapshot is not null) throw new InvalidOperationException("Restricted documents must be renamed, not copied with save-as.");
        var bytes = Utf8.GetBytes(Buffer.Text);
        await WriteAsync(path, bytes, false, cancellationToken).ConfigureAwait(false);
        Scope = scope;
        RelativePath = relative;
        ClearSnapshots();
        _snapshot = bytes;
        _allowedSnapshot = null;
        Executable = false;
        Buffer.Saved();
        await WriteStateAsync(new(Scope: scope, Path: relative), cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> CloseAsync(string decision, CancellationToken cancellationToken = default)
    {
        if (decision == "cancel") return false;
        if (decision is not ("save" or "discard")) throw new ArgumentException("Choose save, discard or cancel.");
        if (decision == "save") await SaveAsync(cancellationToken).ConfigureAwait(false);
        Buffer.Load("");
        _configurationForm = null;
        Scope = null;
        RelativePath = null;
        ClearSnapshots();
        Executable = false;
        return true;
    }

    public string Preview()
    {
        if (!IsOpen) throw new InvalidOperationException("Open a document first.");
        if (!Buffer.Editable) return "Binary file: read-only; no preview.";
        if (IsConfiguration) return NomadNetTerminalText.Safe(Buffer.Text);
        if (Executable) return "Executable source only; never executed by preview.\n" + NomadNetTerminalText.Safe(Buffer.Text);
        if (Buffer.Text.Length > 100_000) return "Preview disabled above 100000 characters; editing remains available.";
        return MicronText.Render(Utf8.GetBytes(Buffer.Text)).Text;
    }

    public Task PermissionsAsync(IReadOnlyList<string>? identities, CancellationToken cancellationToken = default) =>
        MutateAsync(() => PermissionsCoreAsync(identities, cancellationToken), cancellationToken);

    private async Task PermissionsCoreAsync(IReadOnlyList<string>? identities, CancellationToken cancellationToken)
    {
        var path = Current;
        if (IsConfiguration) throw new InvalidOperationException("Configuration has no hosted permissions.");
        if (_snapshot is null) throw new InvalidOperationException("Save the document before setting permissions.");
        if (identities is { Count: > 1024 }) throw new InvalidDataException("Too many allowed identities.");
        var normalized = identities?.Select(NomadNetAddress.NormalizeHash).Distinct(StringComparer.Ordinal).ToArray() ?? [];
        await CheckAsync(path, _snapshot, cancellationToken).ConfigureAwait(false);
        await CheckAsync(path + ".allowed", _allowedSnapshot, cancellationToken).ConfigureAwait(false);
        var bytes = normalized.Length == 0 ? null : Utf8.GetBytes(string.Join('\n', normalized) + "\n");
        if (bytes is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Guard(path + ".allowed");
            File.Delete(path + ".allowed");
        }
        else await WriteAsync(path + ".allowed", bytes, _allowedSnapshot is not null, cancellationToken,
            _allowedSnapshot, verifySnapshot: true).ConfigureAwait(false);
        if (_allowedSnapshot is not null) CryptographicOperations.ZeroMemory(_allowedSnapshot);
        _allowedSnapshot = bytes;
    }

    public Task RenameAsync(string scope, string relative, CancellationToken cancellationToken = default) =>
        MutateAsync(() => RenameCoreAsync(scope, relative, cancellationToken), cancellationToken);

    private async Task RenameCoreAsync(string scope, string relative, CancellationToken cancellationToken)
    {
        RequireCleanHosted();
        var old = Current;
        var target = Resolve(scope, relative);
        if (IsConfigurationScope(scope)) throw new ArgumentException("Cannot rename hosted content into configuration.");
        await CheckAsync(old, _snapshot, cancellationToken).ConfigureAwait(false);
        await CheckAsync(old + ".allowed", _allowedSnapshot, cancellationToken).ConfigureAwait(false);
        if (System.IO.Path.Exists(target) || System.IO.Path.Exists(target + ".allowed")) throw new IOException("Rename never overwrites.");
        Guard(target);
        if (!Directory.Exists(System.IO.Path.GetDirectoryName(target))) throw new DirectoryNotFoundException("Create the destination folder first.");
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(old, target, false);
        try
        {
            if (_allowedSnapshot is not null) File.Move(old + ".allowed", target + ".allowed", false);
        }
        catch (Exception failure)
        {
            try { File.Move(target, old, false); }
            catch (Exception rollback) { throw new AggregateException("Rename and rollback failed; inspect both document paths.", failure, rollback); }
            throw;
        }
        Scope = scope;
        RelativePath = relative;
        await WriteStateAsync(new(Scope: scope, Path: relative), CancellationToken.None).ConfigureAwait(false);
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default) =>
        MutateAsync(() => DeleteCoreAsync(cancellationToken), cancellationToken);

    private async Task DeleteCoreAsync(CancellationToken cancellationToken)
    {
        RequireCleanHosted();
        var path = Current;
        await CheckAsync(path, _snapshot, cancellationToken).ConfigureAwait(false);
        await CheckAsync(path + ".allowed", _allowedSnapshot, cancellationToken).ConfigureAwait(false);
        var staged = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, ".editor-delete-" + Guid.NewGuid().ToString("N"));
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(path, staged, false);
        try
        {
            if (_allowedSnapshot is not null) File.Move(path + ".allowed", staged + ".allowed", false);
        }
        catch (Exception failure)
        {
            try { File.Move(staged, path, false); }
            catch (Exception rollback) { throw new AggregateException("Delete staging and rollback failed; inspect editor tombstones.", failure, rollback); }
            throw;
        }
        await CloseAsync("discard", CancellationToken.None).ConfigureAwait(false);
        // After the two moves, deletion is committed. A cleanup error is surfaced and the hidden
        // tombstone is retained rather than falsely reporting a rollback after destroying data.
        File.Delete(staged);
        File.Delete(staged + ".allowed");
    }

    public void CreateDirectory(string scope, string? relative = null)
        => CreateFolderAsync(scope, relative).GetAwaiter().GetResult();

    public IReadOnlyList<string> List(string scope, string? relative = null)
    {
        if (scope is not ("pages" or "files")) throw new ArgumentException("List pages or files.");
        var root = relative is null ? _roots[scope] : Resolve(scope, relative);
        Guard(root);
        if (!Directory.Exists(root)) return [];
        var entries = new List<string>();
        var scanned = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            if (++scanned > 4096) throw new InvalidDataException("Editor listing exceeds its 4096-entry scan bound; choose a smaller folder.");
            var name = System.IO.Path.GetFileName(path);
            if (name.StartsWith('.') || name.EndsWith(".allowed", StringComparison.OrdinalIgnoreCase)) continue;
            if (entries.Count == 2048) throw new InvalidDataException("Editor listing exceeds 2048 entries; choose a smaller folder.");
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            entries.Add(name + (Directory.Exists(path) ? "/" : ""));
        }
        return entries.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void RequireCleanHosted()
    {
        if (!IsOpen || IsConfiguration || _snapshot is null) throw new InvalidOperationException("Open a saved hosted document.");
        if (Buffer.Dirty) throw new InvalidOperationException("Save or discard changes before rename/delete.");
        Guard(Current);
    }

    private async Task WriteStateAsync(NomadNetEditorState state, CancellationToken cancellationToken)
    {
        var path = System.IO.Path.Combine(_profile, "editor_state.json");
        Guard(path);
        await WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(state, NomadNetEditorJsonContext.Default.NomadNetEditorState),
            File.Exists(path), cancellationToken).ConfigureAwait(false);
    }

    public void ValidateConfiguration()
    {
        if (!IsConfiguration) throw new InvalidOperationException("Open native or selected RNS configuration first.");
        var bytes = Utf8.GetBytes(Buffer.Text);
        try { ValidateConfiguration(bytes, Scope!); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public bool IsConfiguration => Scope is not null && IsConfigurationScope(Scope);
    private static bool IsConfigurationScope(string scope) => scope is "config" or "rns" or "primary";

    private void ValidateConfiguration(byte[] bytes, string scope)
    {
        if (scope == "primary")
        {
            if (bytes.Length > NomadNetConfigDocument.MaximumBytes) throw new InvalidDataException("Primary ConfigObj exceeds64 KiB.");
            NomadNetConfigImport.Overlay(Utf8.GetString(bytes), _configurationBasis).Settings.Validate();
            return;
        }
        if (scope == "rns")
        {
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("RNS configuration exceeds one megabyte.");
            var parsed = NomadNetConfigDocument.Parse(Utf8.GetString(bytes).TrimStart('\ufeff'), MaximumBytes);
            if (parsed.Contains("reticulum", "panic_on_interface_error"))
                throw new NotSupportedException(NomadNetConfigurationForm.PanicSafetyRefusal);
            if (parsed.RequiresExtendedStackGrammar)
                throw new NotSupportedException("The selected public RNS parser does not support ConfigObj interpolation/triple-quoted values; no ineffective source will be saved.");
            _ = DotReticulum.Applications.Rnsd.RnsdConfigParser.Parse(Utf8.GetString(bytes).TrimStart('\ufeff'));
            return;
        }
        if (bytes.Length > NomadNetSettings.MaximumSettingsBytes) throw new InvalidDataException("Configuration exceeds 64 KiB.");
        var offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
        using var document = JsonDocument.Parse(bytes.AsMemory(offset));
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
            if (!names.Add(property.Name) || !NomadNetJsonContext.Default.NomadNetSettings.Properties.Any(item => item.Name == property.Name))
                throw new InvalidDataException("Unknown or duplicate configuration property: " + property.Name);
        if (!names.Contains("schemaVersion") || !names.Contains("displayName"))
            throw new InvalidDataException("Configuration requires schemaVersion and displayName.");
        var settings = JsonSerializer.Deserialize(bytes.AsSpan(offset), NomadNetJsonContext.Default.NomadNetSettings)
            ?? throw new InvalidDataException("Configuration is empty.");
        settings.Validate();
    }

    private static async Task<byte[]?> SnapshotAsync(string path, CancellationToken cancellationToken)
    {
        Guard(path);
        if (!File.Exists(path)) return null;
        return await NomadNetLocalFiles.ReadAsync(path, MaximumBytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CheckAsync(string path, byte[]? expected, CancellationToken cancellationToken)
    {
        var actual = await SnapshotAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            if ((actual is null) != (expected is null) ||
                actual is not null && !CryptographicOperations.FixedTimeEquals(SHA256.HashData(actual), SHA256.HashData(expected!)))
                throw new IOException("Document or permissions changed externally; reopen before writing.");
        }
        finally { if (actual is not null) CryptographicOperations.ZeroMemory(actual); }
    }

    private static async Task WriteAsync(string path, byte[] bytes, bool overwrite, CancellationToken cancellationToken,
        byte[]? expected = null, bool verifySnapshot = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Guard(path);
        var parent = System.IO.Path.GetDirectoryName(path)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("Create the document folder first.");
        var mode = !OperatingSystem.IsWindows() && File.Exists(path) ? File.GetUnixFileMode(path) : (UnixFileMode?)null;
        var staged = System.IO.Path.Combine(parent, ".editor-write-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                Share = FileShare.None, Options = FileOptions.Asynchronous | FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(staged, options))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows() && mode is not null) File.SetUnixFileMode(staged, mode.Value);
            cancellationToken.ThrowIfCancellationRequested();
            Guard(path);
            if (verifySnapshot) await CheckAsync(path, expected, cancellationToken).ConfigureAwait(false);
            File.Move(staged, path, overwrite);
        }

        finally { if (File.Exists(staged)) File.Delete(staged); }
    }

    private static async Task MutateAsync(Func<Task> mutation, CancellationToken cancellationToken)
    {
        await MutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await mutation().ConfigureAwait(false); }
        finally { MutationGate.Release(); }
    }

    private static bool IsDevice(string name)
    {
        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
            stem[3] is >= '0' and <= '9';
    }

    internal static void Guard(string path)
    {
        for (var entry = new FileInfo(System.IO.Path.GetFullPath(path)); entry is not null; entry = entry.Directory is { } parent ? new FileInfo(parent.FullName) : null)
        {
            try
            {
                if ((File.GetAttributes(entry.FullName) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Editor paths cannot traverse symbolic links or reparse points.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }

    }

    private void ClearSnapshots()
    {
        if (_snapshot is not null) CryptographicOperations.ZeroMemory(_snapshot);
        if (_allowedSnapshot is not null) CryptographicOperations.ZeroMemory(_allowedSnapshot);
        _snapshot = null;
        _allowedSnapshot = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _configurationForm = null;
        CancelFolderPlan();
        ClearSnapshots();
        Buffer.Load("");
        Scope = null;
        RelativePath = null;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NomadNetEditorState))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
internal partial class NomadNetEditorJsonContext : JsonSerializerContext;
