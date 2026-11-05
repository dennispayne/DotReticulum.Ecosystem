using System.Security.Cryptography;
using System.Text.Json;

namespace NomadNet;

public sealed record NomadNetEditorStateImportResult(NomadNetEditorState State, IReadOnlyList<string> Warnings);

/// <summary>Translates stopped primary editor persistence into the existing native editor state.</summary>
public static class NomadNetEditorStateImport
{
    public const int MaximumStateBytes = 32768;
    public const int MaximumDocuments = 2048;
    public const long MaximumDocumentBytes = 16 * 1024 * 1024;
    private static readonly string[] Panes = ["files", "editor", "preview"];

    public static NomadNetEditorStateImportResult Parse(ReadOnlyMemory<byte> bytes, string primaryProfile)
    {
        if (bytes.Length > MaximumStateBytes) throw new InvalidDataException("Primary editor state exceeds32 KiB.");
        try
        {
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            var root = json.RootElement;
            RequireObject(root);
            var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(primaryProfile));
            var warnings = new List<string>();
            foreach (var item in root.EnumerateObject())
                if (item.Name is not ("filter_mu" or "expanded" or "pane_visible" or "last_file"))
                    warnings.Add("Unsupported primary editor field archived, not activated: " + SafeName(item.Name));
            var filter = root.TryGetProperty("filter_mu", out var filterValue) && Boolean(filterValue);
            var expanded = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("expanded", out var expansion))
            {
                if (expansion.ValueKind != JsonValueKind.Array || expansion.GetArrayLength() > 256)
                    throw new InvalidDataException("Primary editor expansion must contain at most256 paths.");
                foreach (var item in expansion.EnumerateArray())
                {
                    var location = Location(item, allowNull: false);
                    var mapped = Map(location!);
                    if (mapped is not null) expanded.Add(mapped.Value.Scope +
                        (mapped.Value.Relative.Length == 0 ? "" : "/" + mapped.Value.Relative));
                }
            }
            else expanded.UnionWith(["pages", "files"]);
            var visible = Panes.ToDictionary(pane => pane, _ => true, StringComparer.Ordinal);
            if (root.TryGetProperty("pane_visible", out var paneValues))
            {
                RequireObject(paneValues);
                foreach (var item in paneValues.EnumerateObject())
                {
                    if (visible.ContainsKey(item.Name)) visible[item.Name] = Boolean(item.Value);
                    else warnings.Add("Unsupported primary editor pane archived, not activated: " + SafeName(item.Name));
                }
            }
            var selected = root.TryGetProperty("last_file", out var last) ? Location(last, allowNull: true) : null;
            var selection = selected is null ? null : Map(selected);
            if (selection is { Relative.Length: 0 })
                throw new InvalidDataException("Primary editor selection must be a document, not a root.");
            var panes = Panes.Where(pane => visible[pane]).ToArray();
            if (panes.Length == 0) panes = ["editor"]; // Actual visible_panes() fallback, not an invented stored pane.
            return new(new(Scope: selection?.Scope, Path: selection?.Relative, FilterMu: filter,
                Expanded: expanded.Order(StringComparer.Ordinal).ToArray(), VisiblePanes: panes), warnings);

            (string Scope, string Relative)? Map(string path)
            {
                if (!Path.IsPathFullyQualified(path))
                {
                    warnings.Add("Primary editor relative or foreign-platform path was not activated.");
                    return null;
                }
                if (path.Replace('\\', '/').Split('/').Any(piece => piece is "." or ".."))
                    throw new InvalidDataException("Primary editor paths cannot contain traversal.");
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                foreach (var scope in new[] { "pages", "files" })
                {
                    var owned = Path.Combine(source, "storage", scope);
                    var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    if (full.Equals(owned, comparison)) return (scope, "");
                    if (full.StartsWith(owned + Path.DirectorySeparatorChar, comparison))
                    {
                        var relative = Path.GetRelativePath(owned, full).Replace('\\', '/');
                        if (relative.Length > 512 || relative.Count(character => character == '/') >= 16)
                            throw new InvalidDataException("Primary editor path exceeds native512-character/16-level bounds.");
                        return (scope, relative);
                    }
                }
                warnings.Add("Primary editor path outside this profile's owned storage/pages or storage/files was not activated.");
                return null;
            }
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException)
        { throw new InvalidDataException("Malformed primary editor state; nothing was imported.", error); }
    }

    private static string SafeName(string name) => NomadNetTerminalText.Safe(name.Length > 128 ? name[..128] : name);

    private static bool Boolean(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new InvalidDataException("Primary editor booleans must be JSON booleans; Python truthiness is not imported.")
    };

    private static string? Location(JsonElement value, bool allowNull)
    {
        if (allowNull && value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= 2048 } text ||
            text.Any(char.IsControl))
            throw new InvalidDataException("Primary editor paths must be nonempty bounded strings without control characters.");
        return text;
    }

    private static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Primary editor state requires JSON objects.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateObject())
            if (!names.Add(item.Name)) throw new InvalidDataException("Duplicate primary editor key.");
    }

    internal static async Task ImportAsync(string source, string staging, NomadNetSettings settings,
        IReadOnlyDictionary<string, string> snapshot, ISet<string> activated, ISet<string> archived,
        ICollection<string> warnings, CancellationToken token)
    {
        const string stateFile = "editor_state.json";
        if (!snapshot.ContainsKey(stateFile))
        {
            warnings.Add("Primary editor state absent; native editor defaults retained. No authoring files were activated.");
            return;
        }
        var original = await ReadStableAsync(stateFile, MaximumStateBytes).ConfigureAwait(false);
        var result = Parse(original, source);
        foreach (var warning in result.Warnings) warnings.Add(warning);
        var state = result.State;
        await using var editor = new NomadNetEditor(staging, settings);
        editor.ValidateImportedState(state);
        var documents = 0;
        long total = 0;
        foreach (var scope in new[] { "pages", "files" })
        {
            var prefix = "storage/" + scope + "/";
            Directory.CreateDirectory(Path.Combine(staging, "storage", scope));
            foreach (var file in snapshot.Keys.Where(path => path.StartsWith(prefix, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                var relative = file[prefix.Length..];
                if (relative.EndsWith(".allowed", StringComparison.OrdinalIgnoreCase)) continue;
                string destination;
                try { destination = editor.Resolve(scope, relative); }
                catch (ArgumentException)
                {
                    warnings.Add("Non-editor authoring path not imported: " + SafeName(file));
                    continue;
                }
                if (relative.Count(character => character == '/') > 16)
                    throw new InvalidDataException("Primary editor document exceeds16 folder levels.");
                if (++documents > MaximumDocuments) throw new InvalidDataException("Primary editor exceeds2048 documents.");
                var content = await ReadStableAsync(file, NomadNetEditor.MaximumBytes).ConfigureAwait(false);
                total += content.Length;
                if (total > MaximumDocumentBytes) throw new InvalidDataException("Primary editor documents exceed16 MiB.");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await File.WriteAllBytesAsync(destination, content, token).ConfigureAwait(false);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(destination, File.GetUnixFileMode(Path.Combine(source, file.Replace('/', Path.DirectorySeparatorChar))) &
                        (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
                activated.Add(file);
                var policy = file + ".allowed";
                if (snapshot.ContainsKey(policy))
                {
                    var permissions = await ReadStableAsync(policy, NomadNetEditor.MaximumBytes).ConfigureAwait(false);
                    total += permissions.Length;
                    if (total > MaximumDocumentBytes) throw new InvalidDataException("Primary editor documents and permissions exceed16 MiB.");
                    await File.WriteAllBytesAsync(destination + ".allowed", permissions, token).ConfigureAwait(false);
                    activated.Add(policy);
                }
            }
        }
        foreach (var key in state.Expanded!)
        {
            var parts = key.Split('/', 2);
            var originalFolder = Path.Combine(source, "storage", parts[0], parts.Length == 1 ? "" : parts[1].Replace('/', Path.DirectorySeparatorChar));
            NomadNetProfileImport.EnsureNoLinks(originalFolder);
            if (Directory.Exists(originalFolder))
                Directory.CreateDirectory(Path.Combine(staging, "storage", parts[0],
                    parts.Length == 1 ? "" : parts[1].Replace('/', Path.DirectorySeparatorChar)));
            else warnings.Add("Primary editor expanded folder is absent; retained as an inert expansion key: " + key);
        }
        if (state.Scope is not null && !File.Exists(editor.Resolve(state.Scope, state.Path!)))
            warnings.Add("Primary editor selected file is absent; native activation reports the missing selection and remains usable.");
        var translated = JsonSerializer.SerializeToUtf8Bytes(state, NomadNetEditorJsonContext.Default.NomadNetEditorState);
        if (translated.Length > MaximumStateBytes) throw new InvalidDataException("Translated editor state exceeds32 KiB.");
        await File.WriteAllBytesAsync(Path.Combine(staging, stateFile), translated, token).ConfigureAwait(false);
        var archive = Path.Combine(staging, "import-archive", stateFile + ".bin");
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        await File.WriteAllBytesAsync(archive, original, token).ConfigureAwait(false);
        activated.Add(stateFile);
        archived.Add(stateFile);
        warnings.Add($"Activated primary editor filter, expanded owned folders, visible panes and last saved file with {documents} owned documents. " +
            "External roots, hidden/device/traversal paths and orphan policies are not activated. No dirty buffer, focus, cursor, scroll offset or timer is imported; upstream does not persist them. Executable pages remain disabled.");

        async Task<byte[]> ReadStableAsync(string relative, int maximum)
        {
            var bytes = await NomadNetProfileImport.ReadAsync(source, relative, maximum, token).ConfigureAwait(false);
            if (!snapshot.TryGetValue(relative, out var expected) ||
                Convert.ToHexStringLower(SHA256.HashData(bytes)) != expected)
                throw new IOException("Primary editor state or document changed during import; stop the primary and retry.");
            return bytes;
        }
    }
}
