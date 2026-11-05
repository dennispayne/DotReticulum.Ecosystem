using System.Text.Json;

namespace NomadNet;

internal static partial class Program
{
    private sealed partial class TerminalSession
    {
        private async Task<bool> TryHandleConfigurationFormAsync(string[] parts, CancellationToken token)
        {
            if (parts.Length < 2) return false;
            switch (parts[1])
            {
                case "sections":
                    foreach (var section in NomadNetConfigurationFields.Sections)
                        Console.WriteLine($"{section}: {(section == "reticulum" ? "selected RNS file" : "opted-in profile ConfigObj source")}; full restart required");
                    Console.WriteLine("Native JSON remains the default. config primary enable confirm opts into the owned profile/config source; no unknown fields are silently applied.");
                    return true;
                case "primary":
                    var source = parts.Length == 3 ? Split(parts[2], 2) : [];
                    if (source.Length != 2 || source[0] is not ("enable" or "disable") || source[1] != "confirm")
                        throw new ArgumentException("Use config primary enable|disable confirm. This changes the saved configuration source, not running services.");
                    await propagation.SaveConfigurationForRestartAsync(
                        cancellation => Editor.SetPrimaryConfigurationAsync(source[0] == "enable", true, cancellation), token).ConfigureAwait(false);
                    Console.WriteLine("Configuration source saved. Restart NomadNet for changes to take effect; existing primary text was not overwritten or removed.");
                    return true;
                case "section":
                    if (parts.Length != 3) throw new ArgumentException("Use config section <section-name>.");
                    await Editor.OpenConfigurationSectionAsync(parts[2], token).ConfigureAwait(false);
                    PrintConfigurationForm();
                    return true;
                case "fields":
                    PrintConfigurationForm();
                    return true;
                case "set":
                    var assignment = parts.Length == 3 ? Split(parts[2], 2) : [];
                    if (assignment.Length != 2) throw new ArgumentException("Use config set <field> <JSON-string|number|bool|list>.");
                    using (var json = JsonDocument.Parse(assignment[1]))
                    {
                        var value = json.RootElement;
                        var parsed = value.ValueKind switch
                        {
                            JsonValueKind.String => new NomadNetConfigValue(value.GetString()!.Trim()),
                            JsonValueKind.True => new NomadNetConfigValue("yes"),
                            JsonValueKind.False => new NomadNetConfigValue("no"),
                            JsonValueKind.Number => new NomadNetConfigValue(value.GetRawText()),
                            JsonValueKind.Array when value.GetArrayLength() <= 128 &&
                                value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String) =>
                                new NomadNetConfigValue(value.EnumerateArray().Select(item => item.GetString()!)),
                            _ => throw new ArgumentException("Form values require a JSON scalar or at most128 strings."),
                        };
                        Editor.SetConfigurationField(assignment[0], parsed);
                    }
                    Console.WriteLine("Configuration field staged; not saved.");
                    return true;
                case "reset":
                    if (parts.Length != 3 || parts[2] != "confirm")
                        throw new ArgumentException("Use config reset confirm. Defaults are staged only; Save is still required.");
                    Editor.ResetConfigurationSection(true);
                    Console.WriteLine("Section defaults staged; not saved. Use config save confirm or config cancel.");
                    return true;
                case "save":
                    if (parts.Length != 3 || parts[2] != "confirm") throw new ArgumentException("Use config save confirm; changes require a full restart.");
                    await propagation.SaveConfigurationForRestartAsync(
                        cancellation => Editor.SaveConfigurationSectionAsync(true, cancellation), token).ConfigureAwait(false);
                    Console.WriteLine("Section saved. Restart NomadNet for changes to take effect.");
                    return true;
                case "cancel":
                    if (parts.Length != 2) throw new ArgumentException("Use config cancel.");
                    if (!Editor.IsConfiguration) throw new InvalidOperationException("No configuration editor is open.");
                    await Editor.CloseAsync("discard", token).ConfigureAwait(false);
                    Console.WriteLine("Configuration cancelled; saved files unchanged.");
                    return true;
                default: return false;
            }
        }

        private void PrintConfigurationForm()
        {
            var form = Editor.ConfigurationForm ?? throw new InvalidOperationException("Open a configuration section first.");
            Console.WriteLine($"Configuration section: {form.Section}; saved changes require restart.");
            foreach (var field in form.Fields)
            {
                var value = form.Get(field.Key);
                var json = value.List is { } list
                    ? JsonSerializer.Serialize(list.ToArray(), NomadNetEditorJsonContext.Default.StringArray)
                    : JsonSerializer.Serialize(value.Scalar, NomadNetEditorJsonContext.Default.String);
                Console.WriteLine($"{field.Key} = {json}; {field.Type}; {field.Disposition}");
            }
            Console.WriteLine("stored-only controls remain preserved but their primary UI/history behavior is not implemented. safety-refused panic_on_interface_error uses fixed top-level/child failure ownership; it is not a missing hardware feature or a no-op toggle.");
        }
    }
}
