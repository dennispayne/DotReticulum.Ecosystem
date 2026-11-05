using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Sideband;

namespace Sideband.Desktop;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, SidebandChatMessage> _messageRows = new(ReferenceEqualityComparer.Instance);
    private (bool TrustedOnly, bool Trusted)? _messageFormattingKey;
    private bool _refreshingMessageFormatting;

    private void InitializeMessageFormatting() =>
        MessageList.ItemTemplate = new FuncDataTemplate<string>((text, _) =>
        {
            if (text is null || !_messageRows.TryGetValue(text, out var message)) return new TextBlock { Text = text };
            var block = new TextBlock { Name = "FormattedMessageBody", TextWrapping = TextWrapping.Wrap, MaxWidth = 900 };
            block.Inlines!.Add(new Run(MessageHeader(message) + "\n"));
            try
            {
                var state = _state ?? throw new InvalidOperationException("Message state was not loaded.");
                var policy = SidebandMessageFormatting.FromState(state);
                var trusted = state.Contacts.Any(contact => contact.DeliveryHash == message.PeerDeliveryHash && contact.Trusted);
                var runs = SidebandStyledText.Parse(SidebandMessageMarkup.Build(message.Content, message.Renderer,
                    policy.TrustedMarkupOnly, trusted));
                block.Tag = runs;
                foreach (var run in runs)
                {
                    var style = run.Style;
                    var inline = new Run(run.Text)
                    {
                        FontWeight = style.Bold ? FontWeight.Bold : FontWeight.Normal,
                        FontStyle = style.Italic ? FontStyle.Italic : FontStyle.Normal,
                        FontSize = style.Size,
                        BaselineAlignment = style.Script < 0 ? BaselineAlignment.Subscript :
                            style.Script > 0 ? BaselineAlignment.Superscript : BaselineAlignment.Baseline
                    };
                    if (style.Monospace) inline.FontFamily = new FontFamily("Cascadia Mono,Consolas,DejaVu Sans Mono,monospace");
                    if (style.Color is { } color)
                    {
                        if (color.StartsWith('#') && color.Length == 9 && uint.TryParse(color.AsSpan(1),
                            System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var rgba))
                            inline.Foreground = new SolidColorBrush(Color.FromArgb((byte)rgba, (byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8)));
                        else if (Color.TryParse(color, out var parsed))
                            inline.Foreground = new SolidColorBrush(parsed);
                        else throw new InvalidDataException("Unsupported named message color.");
                    }
                    if (style.Underline || style.Strikethrough)
                    {
                        inline.TextDecorations = new TextDecorationCollection();
                        if (style.Underline) inline.TextDecorations.AddRange(TextDecorations.Underline);
                        if (style.Strikethrough) inline.TextDecorations.AddRange(TextDecorations.Strikethrough);
                    }
                    block.Inlines.Add(inline);
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or ArgumentException or
                System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                block.Inlines.Clear();
                block.Inlines.Add(new Run(MessageHeader(message) + "\n[Formatting failed; raw text follows: " + ex.Message + "]\n" + message.Content));
                block.Tag = ex;
            }
            block.Inlines.Add(new Run(MessageExtras(message)));
            return block;
        });

    private static string MessageHeader(SidebandChatMessage message) =>
        $"{message.Timestamp.LocalDateTime:g}  {(message.Incoming ? "Received" : message.DeliveryState)} ({message.DeliveryMethod})";

    private static string MessageExtras(SidebandChatMessage message) =>
        (message.Attachments is { Count: > 0 } files ? "\nFiles: " +
            string.Join(", ", files.Select(file => $"{file.Name} ({file.Length} bytes)")) : "") +
        (message.Image is { } image ? $"\nImage: {image.Format} ({image.Data.Length} bytes)" : "") +
        (message.Audio is { } audio ? $"\nAudio: mode {audio.Mode} ({audio.Data.Length} bytes)" : "");

    private void LoadMessageFormatting()
    {
        var policy = SidebandMessageFormatting.FromState(
            _state ?? throw new InvalidOperationException("Load state before editing message formatting."));
        ComposeMarkdownBox.IsChecked = policy.ComposeMarkdown;
        TrustedMarkupOnlyBox.IsChecked = policy.TrustedMarkupOnly;
    }

    private bool VerifyMessageFormattingSmoke()
    {
        try
        {
            var bodies = MessageList.GetVisualDescendants().OfType<TextBlock>()
                .Where(block => block.Name == "FormattedMessageBody").ToArray();
            if (bodies.Length == 0) throw new InvalidOperationException("No formatted message row was realized.");
            foreach (var body in bodies)
            {
                if (body.Tag is not IReadOnlyList<SidebandTextRun> runs)
                    throw new InvalidOperationException("A formatted message row failed: " + (body.Tag as Exception)?.Message);
                var displayed = string.Concat(runs.Select(run => run.Text));
                var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(displayed)));
                Console.WriteLine($"SMOKE_UI_MESSAGE_FORMATTING=bold:{runs.Count(run => run.Style.Bold)};italic:{runs.Count(run => run.Style.Italic)};text-sha256:{hash}");
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("sideband: Message formatting smoke failed: " + ex.Message);
            _ = CloseAfterCaptureAsync();
            return false;
        }
    }

    private void RefreshMessageFormatting()
    {
        if (_state is null || _selectedContact is null || _messageRows.Count == 0) return;
        var key = MessageFormattingKey();
        if (_messageFormattingKey == key) return;
        _messageFormattingKey = key;
        var selection = MessageList.SelectedIndex;
        var rows = MessageList.ItemsSource?.Cast<string>().ToArray() ?? [];
        _refreshingMessageFormatting = true;
        try
        {
            InitializeMessageFormatting();
            MessageList.ItemsSource = rows;
            MessageList.SelectedIndex = selection;
        }
        finally { _refreshingMessageFormatting = false; }
    }

    private (bool TrustedOnly, bool Trusted) MessageFormattingKey()
    {
        var state = _state ?? throw new InvalidOperationException("Message state was not loaded.");
        var policy = SidebandMessageFormatting.FromState(state);
        return (policy.TrustedMarkupOnly, policy.TrustedMarkupOnly &&
            state.Contacts.Any(contact => contact.DeliveryHash == _selectedContact && contact.Trusted));
    }

    private void SaveMessageFormattingClicked(object? sender, RoutedEventArgs e) => TrackOperation(async () =>
    {
        SaveMessageFormattingButton.IsEnabled = false;
        try
        {
            await _client.ConfigureMessageFormattingAsync(new(ComposeMarkdownBox.IsChecked == true,
                TrustedMarkupOnlyBox.IsChecked == true), _windowCancellation.Token);
            _state = await _client.LoadAsync(_windowCancellation.Token);
            LoadMessageFormatting();
            RefreshMessageFormatting();
            SetStatus("Message formatting saved and applied. Raw text, drafts and contact trust are unchanged.");
        }
        catch (Exception ex) { ShowError("Could not save message formatting: " + ex.Message); }
        finally { if (!_closing) SaveMessageFormattingButton.IsEnabled = true; }
    });
}
