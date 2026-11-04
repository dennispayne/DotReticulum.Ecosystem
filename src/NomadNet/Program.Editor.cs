using System.Text.Json;

namespace NomadNet;

internal static partial class Program
{
    private sealed partial class TerminalSession
    {
        private NomadNetEditor? _editor;
        private readonly NomadNetGuide _guide = new();
        private NomadNetEditor Editor => _editor ??= new(dataDirectory, propagation.Settings);

        private async Task<bool> TryHandleEditorAsync(string command, CancellationToken cancellationToken)
        {
            var parts = Split(command, 3);
            if (parts[0] == "guide")
            {
                if (parts.Length == 1) Console.WriteLine(_guide.Open("start"));
                else if (parts[1] == "topics")
                    foreach (var topic in NomadNetGuide.Topics.Keys) Console.WriteLine(topic);
                else if (parts[1] == "links")
                    foreach (var (link, index) in _guide.Page.Links.Select((link, index) => (link, index)))
                        Console.WriteLine($"{index + 1}: {link.Label} ({link.Target})");
                else if (parts[1] == "open" && parts.Length == 3 && int.TryParse(parts[2], out var number))
                    Console.WriteLine(_guide.Follow(number));
                else if (parts[1] == "back") Console.WriteLine(_guide.Back());
                else if (parts.Length == 2) Console.WriteLine(_guide.Open(parts[1]));
                else throw new ArgumentException("Usage: guide [topics|links|open <number>|back|<topic>#<anchor>]");
                return true;
            }
            if (parts[0] == "config")
            {
                if (parts.Length == 2 && parts[1] == "edit")
                {
                    await Editor.OpenAsync("config", "settings.json", cancellationToken: cancellationToken).ConfigureAwait(false);
                    PrintEditorStatus();
                }
                else if (parts.Length == 2 && parts[1] == "show")
                {
                    var bytes = await NomadNetLocalFiles.ReadAsync(Path.Combine(dataDirectory, "settings.json"),
                        NomadNetSettings.MaximumSettingsBytes, cancellationToken).ConfigureAwait(false);
                    Console.WriteLine(NomadNetTerminalText.Safe(new System.Text.UTF8Encoding(false, true).GetString(bytes)));
                    Console.WriteLine("Saved native configuration; restart required after edits.");
                }
                else if (parts.Length == 2 && parts[1] == "validate")
                {
                    if (Editor.Scope != "config") throw new InvalidOperationException("Use config edit first.");
                    Editor.ValidateConfiguration();
                    Console.WriteLine("Configuration buffer valid; not saved.");
                }
                else throw new ArgumentException("Usage: config edit|show|validate");
                return true;
            }
            if (parts[0] != "edit") return false;
            var action = parts.Length > 1 ? parts[1] : "status";
            var value = parts.Length > 2 ? parts[2] : "";
            switch (action)
            {
                case "open":
                case "new":
                    var location = Split(value, 2);
                    if (location.Length != 2) throw new ArgumentException("Usage: edit open|new <pages|files> <relative-path>");
                    if (location[0] == "config") throw new ArgumentException("Use config edit for configuration.");
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
                    Console.WriteLine(Editor.Preview());
                    break;
                case "save":
                    if (Editor.Scope == "config")
                        await propagation.SaveConfigurationForRestartAsync(Editor.SaveAsync, cancellationToken).ConfigureAwait(false);
                    else await Editor.SaveAsync(cancellationToken).ConfigureAwait(false);
                    Console.WriteLine(Editor.Scope == "config" ? "Configuration saved. Restart NomadNet for changes to take effect." : "Document saved.");
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
                        cancellationToken.ThrowIfCancellationRequested();
                        Editor.CreateDirectory(folder[0], folder.ElementAtOrDefault(1));
                        Console.WriteLine("Hosted folder created.");
                    }
                    else foreach (var entry in Editor.List(folder[0], folder.ElementAtOrDefault(1))) Console.WriteLine(NomadNetTerminalText.Safe(entry));
                    break;
                case "close":
                    if (value == "save" && Editor.Scope == "config")
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
