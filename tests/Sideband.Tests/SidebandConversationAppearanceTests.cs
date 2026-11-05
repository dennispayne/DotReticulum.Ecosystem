using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Sideband;
using Sideband.Desktop;

namespace Sideband.Tests;

[Trait("Category", "Integration")]
public sealed class SidebandConversationAppearanceTests
{
    [Fact]
    [Trait("TestId", "T480")]
    public async Task NinetySixExecutedPrimaryConversationBadgesAndColorsMatchWithoutGrantingTrust()
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "sideband-conversation-appearance.json"), TestContext.Current.CancellationToken));
        var root = document.RootElement;
        Assert.Equal("d069122af14880e43607aed0b71add4c7806fbe0", root.GetProperty("revision").GetString());
        Assert.Equal("741cdc76c264e899c8b9f2e57ebc8d3c6036f665bd95e645e5df21022c88c32d", root.GetProperty("source_sha256").GetString());
        Assert.Equal("8e6e650c950135f32c31769648a9859422ecf076d3c2b8e540f45889b4bbec25", root.GetProperty("core_sha256").GetString());
        Assert.True(root.GetProperty("source_defaults").GetProperty("display_style_in_contact_list").GetBoolean());
        Assert.True(root.GetProperty("source_defaults").GetProperty("display_style_from_all").GetBoolean());
        Assert.Equal(new SidebandConversationAppearance(true, true), new());
        Assert.Equal(96, root.GetProperty("cases").GetArrayLength());
        foreach (var item in root.GetProperty("cases").EnumerateArray())
        {
            var contact = SidebandContact.Create("Peer", "00112233445566778899aabbccddeeff",
                item.GetProperty("trusted").GetBoolean(), item.GetProperty("unread").GetBoolean()) with
            {
                Appearance = item.GetProperty("appearance").GetString() switch
                {
                    "absent" => null,
                    "default" => new(),
                    "custom" => new("rocket", "336699", "ffcc66"),
                    _ => throw new InvalidDataException("Unexpected reference appearance")
                }
            };
            var badge = new SidebandConversationAppearance(item.GetProperty("enabled").GetBoolean(),
                item.GetProperty("from_all").GetBoolean()).Select(contact, item.GetProperty("requests_allowed").GetBoolean());
            Assert.Equal(item.GetProperty("icon").GetString(), badge.Icon);
            var colors = item.GetProperty("colors");
            if (colors.GetProperty("ti_color").ValueKind == JsonValueKind.Null)
            {
                Assert.Null(badge.Foreground);
                Assert.Null(badge.Background);
            }
            else
            {
                Assert.Equal("Custom", colors.GetProperty("ti_color").GetString());
                Assert.Equal(Rgb(colors.GetProperty("fg")), badge.Foreground);
                Assert.Equal(Rgb(colors.GetProperty("bg")), badge.Background);
            }
        }
        var policy = new SidebandConversationAppearance();
        var basic = SidebandContact.Create("Peer", "00112233445566778899aabbccddeeff");
        Assert.Null(policy.Select(basic with { Appearance = new("account", "000000", "FFFFFF") }, false).Foreground);
        Assert.Throws<InvalidDataException>(() => policy.Select(basic with { Appearance = new("rocket", "oops", "ffffff") }, false));
        Assert.Throws<InvalidDataException>(() => policy.Select(basic with { Appearance = new("bad\nicon") }, false));

        static string Rgb(JsonElement rgba)
        {
            Assert.Equal(4, rgba.GetArrayLength());
            Assert.Equal(1, rgba[3].GetDouble());
            return Convert.ToHexStringLower(rgba.EnumerateArray().Take(3)
                .Select(channel => checked((byte)Math.Round(channel.GetDouble() * 255))).ToArray());
        }
    }

    [Fact]
    [Trait("TestId", "T481")]
    public async Task StrictSettingsImportExportRestartCancellationAndOfflineClientUpdatesPreserveUnrelatedState()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "conversation-style-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        try
        {
            var store = new SidebandStateStore(root);
            var contact = SidebandContact.Create("Styled", "00112233445566778899aabbccddeeff") with
            { Appearance = new("rocket", "336699", "ffcc66"), NameIsUserDefined = true };
            await store.AddContactAsync(contact, token);
            var source = Path.Combine(root, "import.json");
            await File.WriteAllTextAsync(source, """{"display_style_in_contact_list":false,"display_style_from_all":false,"unrelated":"retain"}""", token);
            Assert.Equal(["unrelated"], await store.ImportSettingsAsync(source, token));
            Assert.Equal(new(false, false), SidebandConversationAppearance.FromState(await store.LoadAsync(cancellationToken: token)));
            var exported = Path.Combine(root, "export.json");
            await store.ExportSettingsAsync(exported, token);
            using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(exported, token)))
            {
                Assert.False(json.RootElement.GetProperty("display_style_in_contact_list").GetBoolean());
                Assert.False(json.RootElement.GetProperty("display_style_from_all").GetBoolean());
                Assert.Equal("retain", json.RootElement.GetProperty("unrelated").GetString());
            }
            await using var client = new SidebandClient(store);
            await client.ConfigureConversationAppearanceAsync(new(true, false), token);
            var state = await new SidebandStateStore(root).LoadAsync(cancellationToken: token);
            Assert.Equal(new(true, false), SidebandConversationAppearance.FromState(state));
            Assert.Equal(contact, Assert.Single(state.Contacts));
            Assert.Equal("retain", state.ImportedSettings!["unrelated"].GetString());
            Assert.False(client.IsConnected);
            var before = await File.ReadAllBytesAsync(store.StatePath, token);
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ConfigureConversationAppearanceAsync(new(), cancelled.Token));
            foreach (var invalid in new[] { """{"display_style_from_all":1}""", """{"display_style_in_contact_list":null}""",
                """{"display_style_in_contact_list":"true"}""", """{"display_style_from_all":[]}""" })
            {
                await File.WriteAllTextAsync(source, invalid, token);
                await Assert.ThrowsAsync<InvalidDataException>(() => store.ImportSettingsAsync(source, token));
                Assert.Equal(before, await File.ReadAllBytesAsync(store.StatePath, token));
            }
            await File.WriteAllTextAsync(source, """{"display_name":"Keep policy"}""", token);
            await store.ImportSettingsAsync(source, token);
            Assert.Equal(new(true, false), SidebandConversationAppearance.FromState(await store.LoadAsync(cancellationToken: token)));
            var console = new SidebandConsoleConfiguration(client);
            await console.Find("general", "display_style_in_contact_list").Write(await client.LoadAsync(token), "false", token);
            await console.Find("telemetry", "display_style_from_all").Write(await client.LoadAsync(token), "true", token);
            Assert.Equal(new(false, true), SidebandConversationAppearance.FromState(await new SidebandStateStore(root).LoadAsync(cancellationToken: token)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

public sealed partial class SidebandHeadlessUiTests
{
    [AvaloniaFact]
    [Trait("TestId", "T482")]
    public async Task ReportedConversationBadgesApplyPreferencesImmediatelyAndSurviveRestartWithoutTrustOrDraftChanges()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "conversation-style-ui-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        MainWindow? window = null;
        try
        {
            var store = new SidebandStateStore(root);
            var contact = SidebandContact.Create("Styled peer", "00112233445566778899aabbccddeeff") with
            { Appearance = new("rocket", "336699", "ffcc66"), NameIsUserDefined = true };
            await store.AddContactAsync(contact, token);
            window = new MainWindow(root);
            window.Show();
            await WaitForAsync(() => FindBadge(window)?.Icon == "rocket");
            var badge = FindBadge(window)!;
            Assert.Equal("336699", badge.Foreground);
            Assert.Equal("ffcc66", badge.Background);
            var draft = window.FindControl<TextBox>("MessageBox")!;
            draft.Text = "Keep draft";
            var selection = window.FindControl<ListBox>("ContactList")!.SelectedIndex;
            window.FindControl<CheckBox>("TrustedConversationAppearanceOnlyBox")!.IsChecked = true;
            window.FindControl<Button>("SaveConversationAppearanceButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => FindBadge(window)?.Icon == "account-question");
            Assert.Null(FindBadge(window)!.Foreground);
            Assert.Equal("Keep draft", draft.Text);
            Assert.Equal(selection, window.FindControl<ListBox>("ContactList")!.SelectedIndex);
            Assert.Equal(contact, Assert.Single((await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).Contacts));
            await window.CloseAndWaitAsync();
            window = new MainWindow(root);
            window.Show();
            await WaitForAsync(() => FindBadge(window)?.Icon == "account-question");
            Assert.True(window.FindControl<CheckBox>("TrustedConversationAppearanceOnlyBox")!.IsChecked);
            window.FindControl<CheckBox>("TrustedConversationAppearanceOnlyBox")!.IsChecked = false;
            window.FindControl<CheckBox>("ShowConversationAppearanceBox")!.IsChecked = false;
            window.FindControl<Button>("SaveConversationAppearanceButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForAsync(() => window.FindControl<TextBlock>("StatusText")?.Text?.StartsWith("Conversation appearance saved", StringComparison.Ordinal) == true);
            Assert.Equal(new(false, true), SidebandConversationAppearance.FromState(await new SidebandStateStore(root).LoadAsync(cancellationToken: token)));
            Assert.Equal("account-question", FindBadge(window)!.Icon);
            Assert.False(Assert.Single((await new SidebandStateStore(root).LoadAsync(cancellationToken: token)).Contacts).Trusted);
            await window.CloseAndWaitAsync();
        }
        finally
        {
            if (window?.IsVisible == true) await window.CloseAndWaitAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static SidebandConversationBadge? FindBadge(MainWindow window) =>
        window.FindControl<ListBox>("ContactList")!.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(border => border.Name == "ConversationAppearanceBadge")?.Tag as SidebandConversationBadge;
}
