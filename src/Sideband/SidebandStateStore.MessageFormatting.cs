using System.Text.Json;

namespace Sideband;

public sealed partial class SidebandStateStore
{
    public Task ConfigureMessageFormattingAsync(SidebandMessageFormatting configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return MutateAsync(state =>
        {
            var settings = state.ImportedSettings?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            configuration.Export(settings);
            return state with { ImportedSettings = settings };
        }, cancellationToken);
    }
}
