using System.Security.Cryptography;

namespace NomadNet;

public sealed record NomadNetFolderPermissionSummary(string Token, string Scope, string Path,
    int Total, int Restricted, string? Common);

public sealed partial class NomadNetEditor
{
    private sealed class PermissionChange(string path, FolderEntry? previous, string staged, string backup)
    {
        public string Path { get; } = path;
        public FolderEntry? Previous { get; } = previous;
        public string Staged { get; } = staged;
        public string Backup { get; } = backup;
        public bool BackedUp { get; set; }
        public bool Installed { get; set; }
    }

    public async Task<NomadNetFolderPermissionSummary> InspectFolderPermissionsAsync(string scope, string relative,
        CancellationToken cancellationToken = default)
    {
        NomadNetFolderPermissionSummary? result = null;
        await MutateAsync(async () =>
        {
            var path = ResolveFolder(scope, relative);
            ProtectRoots(path);
            RefuseRootCompanion(path);
            var entries = await ReadFolderAsync(path, cancellationToken).ConfigureAwait(false);
            var bodies = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var entry in PermissionTargets(entries))
            {
                var bytes = await SnapshotAsync(System.IO.Path.Combine(path, entry.Path) + ".allowed", cancellationToken).ConfigureAwait(false);
                try
                {
                    var body = bytes is null ? "" : string.Join('\n',
                        Utf8.GetString(bytes).Split(['\r', '\n']).Select(line => line.Trim()).Where(line => line.Length != 0));
                    bodies[body] = bodies.GetValueOrDefault(body) + 1;
                }
                finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
            }
            var token = Guid.NewGuid().ToString("N");
            result = new(token, scope, relative, bodies.Values.Sum(), bodies.Where(pair => pair.Key.Length != 0).Sum(pair => pair.Value),
                bodies.Count == 1 && bodies.Keys.Single().Length != 0 ? bodies.Keys.Single() : null);
            var summary = new NomadNetFolderSummary(token, scope, relative, entries.Count(entry => entry.Directory),
                entries.Count(entry => !entry.Directory), entries.Sum(entry => entry.Length));
            var plan = new FolderPlan(summary, path, entries, Permissions: true);
            await VerifyFolderAsync(plan, path, cancellationToken).ConfigureAwait(false);
            ObjectDisposedException.ThrowIf(_disposed, this);
            _folderPlan = plan;
        }, cancellationToken).ConfigureAwait(false);
        return result!;
    }

    public Task ApplyFolderPermissionsAsync(string token, IReadOnlyList<string>? identities, bool confirmed,
        CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            if (!confirmed) throw new ArgumentException("All recursive permission changes require explicit confirmation, including expanding access and making public.");
            var plan = TakeFolderPlan(token);
            if (!plan.Permissions) throw new InvalidOperationException("Inspect folder permissions before applying access changes.");
            if (identities is { Count: > 1024 }) throw new InvalidDataException("Too many allowed identities.");
            var normalized = identities?.Select(NomadNetAddress.NormalizeHash).Distinct(StringComparer.Ordinal).ToArray() ?? [];
            var bytes = normalized.Length == 0 ? null : Utf8.GetBytes(string.Join('\n', normalized) + "\n");
            try { await ApplyFolderPermissionsCoreAsync(plan, bytes, cancellationToken).ConfigureAwait(false); }
            finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
        }, cancellationToken);

    private static IEnumerable<FolderEntry> PermissionTargets(IReadOnlyList<FolderEntry> entries) =>
        entries.Where(entry => !entry.Directory && !entry.Path.EndsWith(".allowed", StringComparison.OrdinalIgnoreCase) &&
            !entry.Path.Split(System.IO.Path.DirectorySeparatorChar).Contains("__pycache__", StringComparer.Ordinal));

    private async Task ApplyFolderPermissionsCoreAsync(FolderPlan plan, byte[]? body, CancellationToken token)
    {
        ProtectRoots(plan.Absolute);
        await VerifyFolderAsync(plan, plan.Absolute, token).ConfigureAwait(false);
        _ = await FolderSelectionAsync(plan, null, null, token).ConfigureAwait(false);
        var stage = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(plan.Absolute)!, ".editor-permissions-" + Guid.NewGuid().ToString("N"));
        var changes = PermissionTargets(plan.Entries).Select((entry, index) => new PermissionChange(
            System.IO.Path.Combine(plan.Absolute, entry.Path) + ".allowed",
            plan.Entries.SingleOrDefault(item => item.Path == entry.Path + ".allowed"),
            System.IO.Path.Combine(stage, index + ".new"), System.IO.Path.Combine(stage, index + ".old"))).ToArray();
        var projectedEntries = plan.Entries.Count + changes.Count(change => change.Previous is null && body is not null)
            - changes.Count(change => change.Previous is not null && body is null);
        var projectedBytes = plan.Summary.Bytes - changes.Sum(change => change.Previous?.Length ?? 0) + (long)(body?.Length ?? 0) * changes.Length;
        if (projectedEntries > MaximumFolderEntries || projectedBytes > MaximumFolderBytes)
            throw new InvalidDataException("Resulting permission tree exceeds 1024 entries or 16 MiB.");
        if (changes.Any(change => System.IO.Path.GetRelativePath(plan.Absolute, change.Path).Length > 512))
            throw new InvalidDataException("Resulting permission path exceeds 512 characters.");
        if (changes.Any(change => change.Previous is { } previous &&
            ((FileAttributes)previous.Attributes & FileAttributes.ReadOnly) != 0))
            throw new IOException("Read-only permission policies require explicit manual reconciliation.");
        Guard(stage);
        Directory.CreateDirectory(stage);
        var committed = false;
        var retain = false;
        try
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(stage, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (body is not null)
                foreach (var change in changes)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    await WriteAsync(change.Staged, body, false, token).ConfigureAwait(false);
                    if (!OperatingSystem.IsWindows() && change.Previous?.Mode is { } mode)
                        File.SetUnixFileMode(change.Staged, (UnixFileMode)mode);
                }
            await VerifyFolderAsync(plan, plan.Absolute, token).ConfigureAwait(false);
            foreach (var change in changes)
            {
                token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
                await VerifyPolicyAsync(change, token).ConfigureAwait(false);
                Guard(change.Backup);
                Guard(change.Staged);
                if (change.Previous is not null && body is not null)
                {
                    // Replacement must never transiently remove an existing restriction.
                    try { File.Replace(change.Staged, change.Path, change.Backup); }
                    finally
                    {
                        change.BackedUp = File.Exists(change.Backup);
                        change.Installed = !File.Exists(change.Staged);
                    }
                    File.SetAttributes(change.Path, (FileAttributes)change.Previous.Attributes);
                }
                else if (body is not null)
                {
                    Guard(change.Path);
                    File.Move(change.Staged, change.Path, false);
                    change.Installed = true;
                }
                else if (change.Previous is not null)
                {
                    File.Move(change.Path, change.Backup, false);
                    change.BackedUp = true;
                }
            }
            await VerifyPermissionResultAsync(plan, changes, body, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            committed = true;
            if (IsOpen && !IsConfiguration && Within(Current, plan.Absolute) &&
                changes.Any(change => string.Equals(change.Path, Current + ".allowed", StringComparison.Ordinal)))
            {
                if (_allowedSnapshot is not null) CryptographicOperations.ZeroMemory(_allowedSnapshot);
                _allowedSnapshot = body?.ToArray();
            }
        }
        catch (Exception failure)
        {
            try
            {
                foreach (var change in changes.Reverse())
                {
                    if (change.BackedUp)
                    {
                        Guard(change.Backup);
                        var backup = await ReadFileEntryAsync(change.Backup, change.Previous!.Path, CancellationToken.None).ConfigureAwait(false);
                        if (!SamePermissionBackup(backup, change.Previous)) throw new IOException("Permission rollback backup changed.");
                    }
                    if (change.Installed)
                    {
                        await CheckAsync(change.Path, body, CancellationToken.None).ConfigureAwait(false);
                        Guard(change.Path);
                        if (change.BackedUp) File.Replace(change.Backup, change.Path, null);
                        else File.Delete(change.Path);
                        if (change.BackedUp) File.SetAttributes(change.Path, (FileAttributes)change.Previous!.Attributes);
                    }
                    else if (change.BackedUp)
                    {
                        Guard(change.Path);
                        if (File.Exists(change.Path))
                        {
                            await VerifyPolicyAsync(change, CancellationToken.None).ConfigureAwait(false);
                            File.Delete(change.Backup);
                        }
                        else
                        {
                            File.Move(change.Backup, change.Path, false);
                            File.SetAttributes(change.Path, (FileAttributes)change.Previous!.Attributes);
                        }
                    }
                }
            }
            catch (Exception rollback)
            {
                retain = true;
                throw new AggregateException($"Permission application failed; rollback incomplete. Policies may be partially changed; reconcile retained transaction at {stage}.", failure, rollback);
            }
            throw;
        }
        finally
        {
            if (!retain)
            {
                try
                {
                    foreach (var change in changes)
                    {
                        Guard(change.Staged);
                        Guard(change.Backup);
                        if (File.Exists(change.Staged)) File.Delete(change.Staged);
                        if (File.Exists(change.Backup))
                        {
                            var actual = await ReadFileEntryAsync(change.Backup, change.Previous!.Path, CancellationToken.None).ConfigureAwait(false);
                            if (!SamePermissionBackup(actual, change.Previous))
                                throw new IOException("Permission cleanup backup changed; retained for reconciliation.");
                            File.Delete(change.Backup);
                        }
                    }
                    Guard(stage);
                    Directory.Delete(stage, false);
                }
                catch (Exception cleanup)
                {
                    throw new IOException($"Permissions {(committed ? "committed" : "not committed")}; transaction cleanup incomplete at {stage}.", cleanup);
                }
            }
        }
    }

    private static bool SamePermissionBackup(FolderEntry actual, FolderEntry previous)
    {
        // Windows rename/replacement can set Archive and clear Normal on the backup.
        const int renameFlags = (int)(FileAttributes.Archive | FileAttributes.Normal);
        return OperatingSystem.IsWindows()
            ? actual with { Attributes = actual.Attributes & ~renameFlags } == previous with { Attributes = previous.Attributes & ~renameFlags }
            : actual == previous;
    }

    private static async Task VerifyPolicyAsync(PermissionChange change, CancellationToken token)
    {
        Guard(change.Path);
        if (change.Previous is null)
        {
            if (System.IO.Path.Exists(change.Path)) throw new IOException("Permission policy appeared after inspection.");
        }
        else if (await ReadFileEntryAsync(change.Path, change.Previous.Path, token).ConfigureAwait(false) != change.Previous)
            throw new IOException("Permission policy changed after inspection.");
    }

    private async Task VerifyPermissionResultAsync(FolderPlan plan, PermissionChange[] changes, byte[]? body, CancellationToken token)
    {
        var actual = await ReadFolderAsync(plan.Absolute, token).ConfigureAwait(false);
        var paths = changes.Select(change => System.IO.Path.GetRelativePath(plan.Absolute, change.Path)).ToHashSet(StringComparer.Ordinal);
        var expected = plan.Entries.Where(entry => !paths.Contains(entry.Path)).ToArray();
        var unchanged = actual.Where(entry => !paths.Contains(entry.Path)).ToArray();
        // Sidecar replacement changes parent directory timestamps, not content metadata.
        if (!expected.Select(entry => entry.Directory ? entry with { Modified = 0 } : entry)
            .SequenceEqual(unchanged.Select(entry => entry.Directory ? entry with { Modified = 0 } : entry)))
            throw new IOException("Folder content or unrelated policy changed during permission application.");
        foreach (var change in changes)
            await CheckAsync(change.Path, body, token).ConfigureAwait(false);
    }
}
