using System.Security.Cryptography;
using System.Text.Json;

namespace NomadNet;

public sealed record NomadNetFolderSummary(string Token, string Scope, string Path, int Directories, int Files, long Bytes);

public sealed partial class NomadNetEditor
{
    public const int MaximumFolderEntries = 1024;
    public const int MaximumFolderDepth = 16;
    public const long MaximumFolderBytes = 16 * 1024 * 1024;
    private FolderPlan? _folderPlan;
    private sealed record FolderEntry(string Path, bool Directory, long Length, string Hash, long Modified, long Created, int Attributes, int? Mode);
    private sealed record FolderPlan(NomadNetFolderSummary Summary, string Absolute, IReadOnlyList<FolderEntry> Entries);
    private sealed record FolderSelection(bool CurrentAffected, NomadNetEditorState? State, string? NextPath);
    public NomadNetFolderSummary? PendingFolder => Volatile.Read(ref _folderPlan)?.Summary;

    public void CancelFolderPlan() => Interlocked.Exchange(ref _folderPlan, null);

    public Task CreateFolderAsync(string scope, string? relative = null, CancellationToken cancellationToken = default) =>
        MutateAsync(() =>
        {
            RequireFolderScope(scope);
            ObjectDisposedException.ThrowIf(_disposed, this);
            var path = relative is null ? _roots[scope] : ResolveFolder(scope, relative);
            Guard(path);
            var missing = new Stack<string>();
            for (var next = path; !Directory.Exists(next); next = System.IO.Path.GetDirectoryName(next)
                ?? throw new DirectoryNotFoundException("Folder has no existing ancestor."))
            {
                Guard(next);
                if (File.Exists(next)) throw new IOException("Folder creation cannot overwrite a file.");
                if (missing.Count == 64) throw new InvalidDataException("Folder ancestor creation exceeds 64 levels.");
                missing.Push(next);
            }
            var created = new List<string>();
            try
            {
                while (missing.TryPop(out var next))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Guard(next);
                    Directory.CreateDirectory(next);
                    created.Add(next);
                }
                cancellationToken.ThrowIfCancellationRequested();
                Guard(path);
            }
            catch (Exception failure)
            {
                try
                {
                    foreach (var next in created.AsEnumerable().Reverse()) { Guard(next); Directory.Delete(next, false); }
                }
                catch (Exception rollback) { throw new AggregateException("Folder creation and empty-directory rollback failed.", failure, rollback); }
                throw;
            }
            return Task.CompletedTask;
        }, cancellationToken);

    public async Task<NomadNetFolderSummary> InspectFolderAsync(string scope, string relative,
        CancellationToken cancellationToken = default)
    {
        NomadNetFolderSummary? summary = null;
        await MutateAsync(async () =>
        {
            var path = ResolveFolder(scope, relative);
            ProtectRoots(path);
            RefuseRootCompanion(path);
            var entries = await ReadFolderAsync(path, cancellationToken).ConfigureAwait(false);
            summary = new(Guid.NewGuid().ToString("N"), scope, System.IO.Path.GetRelativePath(_roots[scope], path),
                entries.Count(entry => entry.Directory), entries.Count(entry => !entry.Directory), entries.Sum(entry => entry.Length));
            _folderPlan = new(summary, path, entries);
        }, cancellationToken).ConfigureAwait(false);
        return summary!;
    }

    public Task MoveFolderAsync(string token, string scope, string relative, CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            var plan = TakeFolderPlan(token);
            var target = ResolveFolder(scope, relative);
            ProtectRoots(plan.Absolute);
            ProtectRoots(target);
            if (Within(target, plan.Absolute)) throw new IOException("A folder cannot move into itself or its descendants.");
            Guard(target);
            RefuseRootCompanion(target);
            if (System.IO.Path.Exists(target)) throw new IOException("Folder moves never overwrite a file or folder.");
            if (!Directory.Exists(System.IO.Path.GetDirectoryName(target)))
                throw new DirectoryNotFoundException("Create the destination parent folder first.");
            await VerifyFolderAsync(plan, plan.Absolute, cancellationToken).ConfigureAwait(false);
            var selection = await FolderSelectionAsync(plan, scope, target, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Guard(target);
            Directory.Move(plan.Absolute, target);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await VerifyFolderAsync(plan, target, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception failure)
            {
                try { Guard(plan.Absolute); Guard(target); Directory.Move(target, plan.Absolute); }
                catch (Exception rollback) { throw new AggregateException("Folder move verification and nonoverwriting rollback failed.", failure, rollback); }
                throw;
            }
            await ApplyFolderSelectionAsync(selection, scope, removed: false).ConfigureAwait(false);
        }, cancellationToken);

    public Task RemoveFolderAsync(string token, bool confirmed, CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            if (!confirmed) throw new ArgumentException("Recursive folder removal requires explicit confirmation.");
            var plan = TakeFolderPlan(token);
            ProtectRoots(plan.Absolute);
            await VerifyFolderAsync(plan, plan.Absolute, cancellationToken).ConfigureAwait(false);
            var selection = await FolderSelectionAsync(plan, null, null, cancellationToken).ConfigureAwait(false);
            var staged = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(plan.Absolute)!,
                ".editor-folder-delete-" + Guid.NewGuid().ToString("N"));
            Guard(staged);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(plan.Absolute, staged);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await VerifyFolderAsync(plan, staged, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception failure)
            {
                try { Guard(staged); Guard(plan.Absolute); Directory.Move(staged, plan.Absolute); }
                catch (Exception rollback) { throw new AggregateException("Folder removal verification and nonoverwriting rollback failed.", failure, rollback); }
                throw;
            }
            // Detachment is committed. Finish bounded cleanup without cancellation; never recurse
            // into newly introduced entries or claim rollback after destroying any inspected file.
            Exception? cleanup = null;
            try
            {
                foreach (var entry in plan.Entries.Where(entry => !entry.Directory))
                {
                    var path = System.IO.Path.Combine(staged, entry.Path);
                    Guard(path);
                    if (await ReadFileEntryAsync(path, entry.Path, CancellationToken.None).ConfigureAwait(false) != entry)
                        throw new IOException("Detached folder file changed before cleanup.");
                    File.Delete(path);
                }
                foreach (var entry in plan.Entries.Where(entry => entry.Directory).OrderByDescending(entry => entry.Path.Length))
                {
                    var path = System.IO.Path.Combine(staged, entry.Path);
                    Guard(path);
                    Directory.Delete(path, recursive: false);
                }
            }
            catch (Exception failure) { cleanup = failure; }
            try { await ApplyFolderSelectionAsync(selection, null, removed: true).ConfigureAwait(false); }
            catch (Exception stateFailure) when (cleanup is not null)
            {
                throw new AggregateException($"Folder removal committed; cleanup incomplete at {staged}; selection persistence also failed.", cleanup, stateFailure);
            }
            if (cleanup is not null) throw new IOException($"Folder removal committed; cleanup incomplete at {staged}.", cleanup);
        }, cancellationToken);

    private FolderPlan TakeFolderPlan(string token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var plan = _folderPlan;
        if (plan is null || !string.Equals(plan.Summary.Token, token, StringComparison.Ordinal))
            throw new InvalidOperationException("Inspect the folder first and use its current exact confirmation token.");
        _folderPlan = null;
        return plan;
    }

    private string ResolveFolder(string scope, string relative)
    {
        RequireFolderScope(scope);
        var path = Resolve(scope, relative);
        if (System.IO.Path.GetRelativePath(_roots[scope], path).Split(System.IO.Path.DirectorySeparatorChar).Length > MaximumFolderDepth)
            throw new InvalidDataException("Folder path exceeds 16 levels.");
        return path;
    }

    private static void RequireFolderScope(string scope)
    {
        if (scope is not ("pages" or "files")) throw new ArgumentException("Folder operations select pages or files only.");
    }

    private void ProtectRoots(string path)
    {
        foreach (var root in _roots.Values)
            if (Within(root, path)) throw new IOException("Folder operation cannot move/remove a configured root or its ancestor.");
    }

    private static bool Within(string path, string root)
    {
        var relative = System.IO.Path.GetRelativePath(root, path);
        return relative == "." || !System.IO.Path.IsPathRooted(relative) &&
            relative != ".." && !relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void RefuseRootCompanion(string path)
    {
        Guard(path);
        Guard(path + ".allowed");
        if (System.IO.Path.Exists(path + ".allowed"))
            throw new IOException("Folder-root .allowed companions require manual reconciliation; no implicit sidecar move/removal.");
    }

    private async Task VerifyFolderAsync(FolderPlan plan, string path, CancellationToken token)
    {
        RefuseRootCompanion(path);
        var actual = await ReadFolderAsync(path, token).ConfigureAwait(false);
        if (!plan.Entries.SequenceEqual(actual))
            throw new IOException("Folder changed after inspection; inspect again before mutation.");
    }

    private async Task<IReadOnlyList<FolderEntry>> ReadFolderAsync(string root, CancellationToken token)
    {
        Guard(root);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Select an existing hosted folder.");
        var entries = new List<FolderEntry>();
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        long bytes = 0;
        while (pending.TryPop(out var next))
        {
            token.ThrowIfCancellationRequested();
            Guard(next.Path);
            var relative = System.IO.Path.GetRelativePath(root, next.Path);
            if (relative == ".") relative = "";
            Add(DirectoryEntry(next.Path, relative));
            foreach (var path in Directory.EnumerateFileSystemEntries(next.Path))
            {
                token.ThrowIfCancellationRequested();
                Guard(path);
                var name = System.IO.Path.GetFileName(path);
                var attributes = File.GetAttributes(path);
                var directory = (attributes & FileAttributes.Directory) != 0;
                var allowed = !directory && name.EndsWith(".allowed", StringComparison.OrdinalIgnoreCase);
                var baseName = allowed ? name[..^8] : name;
                if (baseName.Length == 0 || baseName.StartsWith('.') || baseName.EndsWith('.') || baseName.EndsWith(' ') ||
                    baseName.Any(char.IsControl) || baseName.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0 || IsDevice(baseName))
                    throw new IOException("Folder contains hidden, device or unsupported names; reconcile them explicitly first.");
                var childRelative = System.IO.Path.GetRelativePath(root, path);
                if (childRelative.Length > 512 || next.Depth + 1 > MaximumFolderDepth)
                    throw new InvalidDataException("Folder traversal exceeds 512-character paths or 16 levels.");
                if (directory)
                {
                    if (pending.Count + entries.Count >= MaximumFolderEntries)
                        throw new InvalidDataException("Folder traversal exceeds 1024 entries.");
                    pending.Push((path, next.Depth + 1));
                }
                else
                {
                    var entry = await ReadFileEntryAsync(path, childRelative, token).ConfigureAwait(false);
                    bytes += entry.Length;
                    if (bytes > MaximumFolderBytes) throw new InvalidDataException("Folder contents exceed 16 MiB.");
                    Add(entry);
                }
            }
        }
        return entries.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray();

        void Add(FolderEntry entry)
        {
            if (entries.Count + pending.Count >= MaximumFolderEntries) throw new InvalidDataException("Folder traversal exceeds 1024 entries.");
            entries.Add(entry);
        }
    }

    private static FolderEntry DirectoryEntry(string path, string relative)
    {
        var info = new DirectoryInfo(path);
        return new(relative, true, 0, "", info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks, (int)info.Attributes,
            OperatingSystem.IsWindows() ? null : (int)File.GetUnixFileMode(path));
    }

    private static async Task<FolderEntry> ReadFileEntryAsync(string path, string relative, CancellationToken token)
    {
        Guard(path);
        var bytes = await NomadNetLocalFiles.ReadAsync(path, MaximumBytes, token).ConfigureAwait(false);
        try
        {
            var info = new FileInfo(path);
            return new(relative, false, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)),
                info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks, (int)info.Attributes,
                OperatingSystem.IsWindows() ? null : (int)File.GetUnixFileMode(path));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task<FolderSelection> FolderSelectionAsync(FolderPlan plan, string? nextScope, string? target, CancellationToken token)
    {
        var currentAffected = IsOpen && !IsConfiguration && Within(Current, plan.Absolute);
        string? nextPath = null;
        if (currentAffected)
        {
            if (Buffer.Dirty) throw new InvalidOperationException("Save or discard the selected document before changing its folder.");
            await CheckAsync(Current, _snapshot, token).ConfigureAwait(false);
            await CheckAsync(Current + ".allowed", _allowedSnapshot, token).ConfigureAwait(false);
            if (target is not null) nextPath = Rebase(Current);
        }
        var statePath = System.IO.Path.Combine(_profile, "editor_state.json");
        var bytes = await SnapshotAsync(statePath, token).ConfigureAwait(false);
        NomadNetEditorState? changed = null;
        if (bytes is not null)
        {
            try
            {
                if (bytes.Length > 4096) throw new InvalidDataException("Editor selection exceeds its limit.");
                var state = JsonSerializer.Deserialize(bytes, NomadNetEditorJsonContext.Default.NomadNetEditorState)
                    ?? throw new InvalidDataException("Invalid editor selection.");
                if (state.SchemaVersion != 1) throw new InvalidDataException("Invalid editor selection schema.");
                if (state.Scope is "pages" or "files" && state.Path is not null &&
                    Within(Resolve(state.Scope, state.Path), plan.Absolute))
                    changed = target is null ? new() : new(Scope: nextScope, Path: Rebase(Resolve(state.Scope, state.Path)));
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        return new(currentAffected, changed, nextPath);

        string Rebase(string path)
        {
            var relative = System.IO.Path.GetRelativePath(_roots[nextScope!],
                System.IO.Path.Combine(target!, System.IO.Path.GetRelativePath(plan.Absolute, path)));
            _ = Resolve(nextScope!, relative);
            return relative;
        }
    }

    private async Task ApplyFolderSelectionAsync(FolderSelection selection, string? scope, bool removed)
    {
        if (selection.CurrentAffected)
        {
            Scope = removed ? null : scope;
            RelativePath = selection.NextPath;
            if (removed) { ClearSnapshots(); Buffer.Load(""); Executable = false; }
        }
        if (selection.State is not null)
        {
            try { await WriteStateAsync(selection.State, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception failure) { throw new IOException("Folder change committed; editor selection persistence failed.", failure); }
        }
    }
}
