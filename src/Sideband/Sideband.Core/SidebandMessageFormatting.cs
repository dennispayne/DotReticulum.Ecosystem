using System.Text.Json;
using LXMF;

namespace Sideband;

public sealed record SidebandFormattedText(string Content, long? Renderer);

public sealed record SidebandMessageFormatting(bool ComposeMarkdown = false, bool TrustedMarkupOnly = false)
{
    public const long MarkdownRenderer = 2;
    public const long BbCodeRenderer = 3;

    public static SidebandMessageFormatting FromState(SidebandState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Import(state.ImportedSettings);
    }

    public SidebandFormattedText PrepareOutgoing(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0) throw new ArgumentException("Message content cannot be empty.", nameof(content));
        if (content.Length > 65_536) throw new ArgumentException("Message text exceeds the 65536-character limit.", nameof(content));
        if (content.StartsWith("#!md\n", StringComparison.Ordinal)) return new(content[5..], MarkdownRenderer);
        if (ComposeMarkdown) return new(content, MarkdownRenderer);
        var close = content.IndexOf(']');
        return new(content, content[0] == '[' && close >= 0 && !content.AsSpan(0, close).Contains('\n')
            ? BbCodeRenderer : null);
    }

    internal static long? ReadRenderer(LxMessage message) =>
        !message.TryGetField(LxmField.Renderer, out var value) ? null : value switch
        {
            long renderer => renderer,
            double renderer when renderer is MarkdownRenderer or BbCodeRenderer => (long)renderer,
            _ => null
        };

    internal static SidebandMessageFormatting Import(IReadOnlyDictionary<string, JsonElement>? settings,
        SidebandMessageFormatting? fallback = null)
    {
        var prior = fallback ?? new();
        return new(Boolean("compose_in_markdown", prior.ComposeMarkdown), Boolean("trusted_markup_only", prior.TrustedMarkupOnly));

        bool Boolean(string key, bool previous) => settings?.TryGetValue(key, out var value) != true ? previous : value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"{key} must be boolean.")
        };
    }

    internal void Export(IDictionary<string, JsonElement> settings)
    {
        settings["compose_in_markdown"] = JsonSerializer.SerializeToElement(ComposeMarkdown, SidebandStateJsonContext.Default.Boolean);
        settings["trusted_markup_only"] = JsonSerializer.SerializeToElement(TrustedMarkupOnly, SidebandStateJsonContext.Default.Boolean);
    }
}
