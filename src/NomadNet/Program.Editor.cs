using System.Text.Json;

namespace NomadNet;

internal static partial class Program
{
    private sealed partial class TerminalSession
    {
        private NomadNetEditor? _editor;
        private readonly NomadNetGuide _guide = new();
        private NomadNetEditor Editor => _editor ??= new(dataDirectory, propagation.Settings, selectedRnsFile)
        { PresentationLog = message => logging.Write(1, message) };

        private async Task<bool> TryHandleEditorAsync(string command, CancellationToken cancellationToken)
        {
            var parts = Split(command, 3);
            if (TryHandleGuide(command, cancellationToken)) return true;
            if (parts[0] == "config")
            {
                if (await TryHandleConfigurationFormAsync(parts, cancellationToken).ConfigureAwait(false)) return true;
                if (parts.Length >= 2 && parts[1] is "edit" or "external" or "show")
                {
                    var target = parts.Length == 3 ? parts[2] : "native";
                    var (scope, relative) = target switch
                    {
                        "native" => ("config", "settings.json"),
                        "rns" => ("rns", "selected"),
                        "primary" => ("primary", "config"),
                        _ => throw new ArgumentException("Select native, primary or rns configuration explicitly."),
                    };
                    if (parts[1] == "external" && !NomadNetConsoleInput.Interactive)
                        throw new InvalidOperationException("External editing requires an interactive terminal; redirected input/output is unsupported.");
                    if (parts[1] == "show")
                    {
                        var bytes = await NomadNetLocalFiles.ReadAsync(Editor.Resolve(scope, relative),
                            scope == "rns" ? NomadNetEditor.MaximumBytes : NomadNetSettings.MaximumSettingsBytes,
                            cancellationToken).ConfigureAwait(false);
                        try { Console.WriteLine(NomadNetTerminalText.Safe(new System.Text.UTF8Encoding(false, true).GetString(bytes))); }
                        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
                        Console.WriteLine($"Saved {target} configuration; restart required after edits.");
                    }
                    else
                    {
                        await Editor.OpenAsync(scope, relative, cancellationToken: cancellationToken).ConfigureAwait(false);
                        if (parts[1] == "external")
                        {
                            Console.WriteLine($"Editing {target} configuration working copy. Ctrl+] cancels; saved changes require restart.");
                            var changed = await NomadNetExternalEditor.EditAsync(Editor, dataDirectory,
                                (path, token) => NomadNetExternalEditor.RunTerminalAsync(propagation.Settings.Editor, path, token),
                                token => propagation.SaveConfigurationForRestartAsync(Editor.SaveAsync, token),
                                cancellationToken).ConfigureAwait(false);
                            Console.WriteLine(changed ? "Configuration saved. Restart NomadNet for changes to take effect." : "Configuration unchanged.");
                        }
                        PrintEditorStatus();
                    }
                }
                else if (parts.Length == 2 && parts[1] == "validate")
                {
                    Editor.ValidateConfiguration();
                    Console.WriteLine("Configuration buffer valid; not saved.");
                }
                else throw new ArgumentException("Use config sections, section <name>, set <field> <JSON>, reset confirm, save confirm, cancel; edit|show|external [native|primary|rns]; or validate.");
                return true;
            }
            if (parts[0] != "edit") return false;
            await Editor.ActivatePresentationAsync(cancellationToken).ConfigureAwait(false);
            var action = parts.Length > 1 ? parts[1] : "status";
            var value = parts.Length > 2 ? parts[2] : "";
            if (action == "folder")
            {
                if (value.StartsWith("permissions ", StringComparison.Ordinal))
                {
                    var permissions = Split(value["permissions ".Length..], 4);
                    if (permissions.Length >= 3 && permissions[0] == "inspect")
                    {
                        var source = Split(value["permissions ".Length..], 3);
                        var summary = await Editor.InspectFolderPermissionsAsync(source[1], source[2], cancellationToken).ConfigureAwait(false);
                        Console.WriteLine($"Folder permissions: {summary.Token}; total={summary.Total}; restricted={summary.Restricted}; " +
                            $"common={JsonSerializer.Serialize(summary.Common, NomadNetEditorJsonContext.Default.String)}");
                        Console.WriteLine("Applying replaces every targeted access list. Explicit confirm is required even for restriction expansion or making public.");
                    }
                    else if (permissions.Length == 3 && permissions[0] == "public" && permissions[2] == "confirm")
                    {
                        await Editor.ApplyFolderPermissionsAsync(permissions[1], null, true, cancellationToken).ConfigureAwait(false);
                        Console.WriteLine("Folder permissions committed: public.");
                    }
                    else if (permissions.Length == 4 && permissions[0] == "apply" && permissions[2] == "confirm")
                    {
                        using var json = JsonDocument.Parse(permissions[3]);
                        if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() > 1024 ||
                            json.RootElement.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                            throw new ArgumentException("Supply a JSON array of at most 1024 identity hashes.");
                        var identities = json.RootElement.EnumerateArray().Select(item => item.GetString()!).ToArray();
                        if (identities.Length == 0) throw new ArgumentException("Use permissions public TOKEN confirm to make public.");
                        await Editor.ApplyFolderPermissionsAsync(permissions[1], identities, true, cancellationToken).ConfigureAwait(false);
                        Console.WriteLine("Folder permissions committed: restricted.");
                    }
                    else throw new ArgumentException("Use edit folder permissions inspect <pages|files> <relative>, apply TOKEN confirm <JSON-identities>, or public TOKEN confirm. Cancel with edit folder cancel.");
                    return true;
                }
                var folderParts = Split(value, 4);
                if (folderParts.Length >= 3 && folderParts[0] == "inspect")
                {
                    var source = Split(value, 3);
                    var summary = await Editor.InspectFolderAsync(source[1], source[2], cancellationToken).ConfigureAwait(false);
                    Console.WriteLine($"Folder plan: {summary.Token}; scope={summary.Scope}; path={NomadNetTerminalText.Safe(summary.Path)}; " +
                        $"directories={summary.Directories}; files={summary.Files}; bytes={summary.Bytes}");
                }
                else if (folderParts.Length == 4 && folderParts[0] is "move" or "rename")
                {
                    await Editor.MoveFolderAsync(folderParts[1], folderParts[2], folderParts[3], cancellationToken).ConfigureAwait(false);
                    Console.WriteLine("Folder moved; inspected files and permissions retained.");
                }
                else if (folderParts.Length == 3 && folderParts[0] == "remove" && folderParts[2] == "confirm")
                {
                    await Editor.RemoveFolderAsync(folderParts[1], confirmed: true, cancellationToken).ConfigureAwait(false);
                    Console.WriteLine("Folder removed; selected descendant closed.");
                }
                else if (folderParts.Length == 1 && folderParts[0] == "cancel")
                {
                    Editor.CancelFolderPlan();
                    Console.WriteLine("Folder plan cancelled; filesystem unchanged.");
                }
                else throw new ArgumentException("Use edit folder inspect <pages|files> <relative>, move|rename <token> <pages|files> <relative>, remove <token> confirm, or cancel.");
                return true;
            }
            switch (action)
            {
                case "help":
                    Console.WriteLine("Editor presentation: view [width40–240] [rows1–60]; tree; expand|collapse <pages|files> [folder]; " +
                        "filter on|off; pane files|editor|preview; focus next|previous; preview-status; " +
                        "scroll <editor|preview|focused> <up|down|page-up|page-down|home|end|signed-row-delta>. " +
                        "Scrolling uses the last view dimensions (default120x20), retains independent session positions; selection/restart resets them. " +
                        "Source edits render a bounded local preview after400ms; view prints the current panes. No network/image retrieval or executable preview.");
                    break;
                case "view":
                    var dimensions = Split(value, 2);
                    var width = dimensions.Length == 0 ? 120 : int.Parse(dimensions[0], System.Globalization.CultureInfo.InvariantCulture);
                    var rows = dimensions.Length < 2 ? 20 : int.Parse(dimensions[1], System.Globalization.CultureInfo.InvariantCulture);
                    Console.Write(Editor.View(width, rows, propagation.Settings, !Console.IsOutputRedirected, cancellationToken));
                    break;
                case "tree":
                    foreach (var entry in Editor.Tree(cancellationToken))
                        Console.WriteLine($"{new string(' ', entry.Depth * 2)}{(entry.Directory ? entry.Expanded ? "[-]" : "[+]" : " . ")} " +
                            NomadNetEditorSyntax.SafeCells(entry.Scope + "/" + entry.Path));
                    break;
                case "scroll":
                    var scrolling = Split(value, 3);
                    if (scrolling.Length != 2) throw new ArgumentException("Use edit scroll <editor|preview|focused> <movement>.");
                    Editor.Scroll(scrolling[0], scrolling[1]);
                    Console.Write(Editor.View(settings: propagation.Settings, ansi: !Console.IsOutputRedirected,
                        token: cancellationToken, width: Editor.ViewWidth, rows: Editor.ViewRows));
                    break;
                case "expand":
                case "collapse":
                    var expansion = Split(value, 2);
                    if (expansion.Length == 0) throw new ArgumentException("Choose pages or files and an optional relative folder.");
                    await Editor.ExpandAsync(expansion[0], expansion.ElementAtOrDefault(1), action == "expand", cancellationToken).ConfigureAwait(false);
                    Console.WriteLine("Editor tree expansion saved.");
                    break;
                case "filter":
                    if (value is not ("on" or "off")) throw new ArgumentException("Use edit filter on|off (.mu is case-sensitive).");
                    await Editor.FilterAsync(value == "on", cancellationToken).ConfigureAwait(false);
                    Console.WriteLine("Editor .mu filter: " + value);
                    break;
                case "pane":
                    await Editor.PaneAsync(value, cancellationToken).ConfigureAwait(false);
                    Console.WriteLine("Editor visible panes: " + string.Join(", ", Editor.VisiblePanes));
                    break;
                case "focus":
                    if (value is not ("" or "next" or "previous")) throw new ArgumentException("Use edit focus next|previous.");
                    Editor.FocusPane(value == "previous");
                    Console.WriteLine("Editor focused pane: " + Editor.FocusedPane);
                    break;
                case "preview-status":
                    var preview = Editor.AutomaticPreview;
                    Console.WriteLine($"Automatic preview: revision={preview?.Revision ?? 0}; requested={Editor.PreviewRevision}; " +
                        $"state={(preview is not null && preview.Revision == Editor.PreviewRevision ? "ready" : "pending")}");
                    break;
                case "open":
                case "new":
                    var location = Split(value, 2);
                    if (location.Length != 2) throw new ArgumentException("Usage: edit open|new <pages|files> <relative-path>");
                    if (location[0] is "config" or "rns" or "primary") throw new ArgumentException("Use config edit for configuration.");
                    await Editor.OpenAsync(location[0], location[1], action == "new", cancellationToken).ConfigureAwait(false);
                    PrintEditorStatus();
                    break;
                case "resume":
                    await Editor.ResumeAsync(cancellationToken).ConfigureAwait(false);
                    PrintEditorStatus();
                    break;
                case "text":
                    RequireEditor();
                    Editor.Buffer.Set(JsonString(value));
                    PrintEditorStatus();
                    break;
                case "insert":
                    RequireEditor();
                    var insertion = Split(value, 2);
                    if (insertion.Length != 2 || !int.TryParse(insertion[0], out var offset))
                        throw new ArgumentException("Usage: edit insert <UTF-16-offset> <JSON-string>");
                    Editor.Buffer.Insert(JsonString(insertion[1]), offset);
                    PrintEditorStatus();
                    break;
                case "find":
                    RequireEditor();
                    Console.WriteLine("Found UTF-16 offset: " + Editor.Buffer.Find(JsonString(value)));
                    break;
                case "replace":
                case "replace-all":
                    RequireEditor();
                    using (var json = JsonDocument.Parse(value))
                    {
                        if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() != 2 ||
                            json.RootElement.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                            throw new ArgumentException("Use a JSON array containing term and replacement strings.");
                        var term = json.RootElement[0].GetString()!;
                        var replacement = json.RootElement[1].GetString()!;
                        Console.WriteLine(action == "replace-all" ? $"Replaced: {Editor.Buffer.ReplaceAll(term, replacement)}" :
                            Editor.Buffer.ReplaceOne(term, replacement) ? "Replaced selected match." : "Next match selected, or no match.");
                    }
                    break;
                case "snippets":
                    foreach (var name in NomadNetGuide.Snippets.Keys) Console.WriteLine(name);
                    break;
                case "snippet":
                    RequireEditor();
                    if (!NomadNetGuide.Snippets.TryGetValue(value, out var snippet)) throw new ArgumentException("Unknown original snippet.");
                    Editor.Buffer.Insert(snippet);
                    PrintEditorStatus();
                    break;
                case "show":
                    RequireEditor();
                    Console.WriteLine(NomadNetTerminalText.Safe(Editor.Buffer.Text));
                    break;
                case "preview":
                    Console.WriteLine(Editor.Preview(propagation.Settings, !Console.IsOutputRedirected));
                    break;
                case "save":
                    if (Editor.IsConfiguration)
                        await propagation.SaveConfigurationForRestartAsync(Editor.SaveAsync, cancellationToken).ConfigureAwait(false);
                    else await Editor.SaveAsync(cancellationToken).ConfigureAwait(false);
                    Console.WriteLine(Editor.IsConfiguration ? "Configuration saved. Restart NomadNet for changes to take effect." : "Document saved.");
                    break;
                case "save-as":
                case "rename":
                    var destination = Split(value, 2);
                    if (destination.Length != 2) throw new ArgumentException("Use <pages|files> <relative-path>.");
                    if (action == "rename") await Editor.RenameAsync(destination[0], destination[1], cancellationToken).ConfigureAwait(false);
                    else await Editor.SaveAsAsync(destination[0], destination[1], cancellationToken).ConfigureAwait(false);
                    Console.WriteLine("Document " + (action == "rename" ? "renamed." : "saved as new path."));
                    break;
                case "delete":
                    if (value != "confirm") throw new ArgumentException("Destructive operation requires edit delete confirm.");
                    await Editor.DeleteAsync(cancellationToken).ConfigureAwait(false);
                    Console.WriteLine("Document deleted.");
                    break;
                case "permissions":
                    if (value == "public") await Editor.PermissionsAsync(null, cancellationToken).ConfigureAwait(false);
                    else
                    {
                        using var json = JsonDocument.Parse(value);
                        if (json.RootElement.ValueKind != JsonValueKind.Array ||
                            json.RootElement.GetArrayLength() > 1024 ||
                            json.RootElement.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                            throw new ArgumentException("Use edit permissions public or a JSON array of identity hashes.");
                        await Editor.PermissionsAsync(json.RootElement.EnumerateArray().Select(item => item.GetString()!).ToArray(),
                            cancellationToken).ConfigureAwait(false);
                    }
                    Console.WriteLine("Document permissions saved.");
                    break;
                case "mkdir":
                case "list":
                    var folder = Split(value, 2);
                    if (folder.Length == 0) throw new ArgumentException("Choose pages or files.");
                    if (action == "mkdir")
                    {
                        await Editor.CreateFolderAsync(folder[0], folder.ElementAtOrDefault(1), cancellationToken).ConfigureAwait(false);
                        Console.WriteLine("Hosted folder created.");
                    }
                    else foreach (var entry in Editor.List(folder[0], folder.ElementAtOrDefault(1))) Console.WriteLine(NomadNetTerminalText.Safe(entry));
                    break;
                case "close":
                    if (value == "save" && Editor.IsConfiguration)
                    {
                        await propagation.SaveConfigurationForRestartAsync(Editor.SaveAsync, cancellationToken).ConfigureAwait(false);
                        await Editor.CloseAsync("discard", cancellationToken).ConfigureAwait(false);
                        Console.WriteLine("Configuration saved; editor closed. Restart NomadNet for changes to take effect.");
                    }
                    else Console.WriteLine(await Editor.CloseAsync(value, cancellationToken).ConfigureAwait(false) ? "Editor closed." : "Close cancelled; buffer retained.");
                    break;
                case "status":
                    PrintEditorStatus();
                    break;
                default: throw new ArgumentException("Unknown editor action. Use guide authoring.");
            }
            return true;
        }

        private void RequireEditor()
        {
            if (!Editor.IsOpen) throw new InvalidOperationException("Open a document first.");
        }

        private void PrintEditorStatus() => Console.WriteLine(
            $"Editor: {Editor.Scope ?? "closed"}/{NomadNetTerminalText.Safe(Editor.RelativePath ?? "")}; " +
            $"editable={Editor.Buffer.Editable}; dirty={Editor.Buffer.Dirty}; cursor={Editor.Buffer.Cursor}; chars={Editor.Buffer.Text.Length}");

        private static string JsonString(string value)
        {
            using var json = JsonDocument.Parse(value);
            return json.RootElement.ValueKind == JsonValueKind.String ? json.RootElement.GetString()! :
                throw new ArgumentException("Text must be a JSON string.");
        }
    }
}
