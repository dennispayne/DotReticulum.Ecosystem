using System.Security.Cryptography;
using System.Text.Json;

namespace NomadNet;

public sealed partial class NomadNetEditor
{
    private NomadNetConfigurationForm? _configurationForm;
    public NomadNetConfigurationForm? ConfigurationForm => _configurationForm;

    public Task OpenConfigurationSectionAsync(string section, CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            _ = NomadNetConfigurationFields.ForSection(section);
            if (section != "reticulum")
            {
                var settings = await ReadNativeSettingsAsync(cancellationToken).ConfigureAwait(false);
                if (!settings.UsePrimaryConfiguration)
                    throw new InvalidOperationException("Native JSON remains active. Explicitly enable the owned primary ConfigObj source first: config primary enable confirm.");
            }
            await OpenAsync(section == "reticulum" ? "rns" : "primary", section == "reticulum" ? "selected" : "config",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!Buffer.Editable) throw new InvalidDataException("A configuration section requires valid UTF-8.");
            _configurationForm = new(section, Buffer.Text);
        }, cancellationToken);

    public void SetConfigurationField(string key, NomadNetConfigValue value)
    {
        var form = RequireConfigurationForm();
        Buffer.Set(form.Set(key, value));
    }

    public void ResetConfigurationSection(bool confirmed)
    {
        var form = RequireConfigurationForm();
        Buffer.Set(form.Reset(confirmed));
    }

    public Task SaveConfigurationSectionAsync(bool confirmed, CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            if (!confirmed) throw new ArgumentException("Section save requires explicit confirmation and a full restart.");
            var form = RequireConfigurationForm();
            Buffer.Set(form.Render());
            await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
            _configurationForm = new(form.Section, Buffer.Text);
        }, cancellationToken);

    private NomadNetConfigurationForm RequireConfigurationForm()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var form = _configurationForm ?? throw new InvalidOperationException("Open a configuration section first.");
        if (!IsConfiguration || Buffer.Text != form.LastText)
            throw new InvalidOperationException("Raw buffer changed outside the form; save/discard and reopen the section before further form edits.");
        return form;
    }

    public Task SetPrimaryConfigurationAsync(bool enabled, bool confirmed, CancellationToken cancellationToken = default) =>
        MutateAsync(async () =>
        {
            if (!confirmed) throw new ArgumentException("Configuration-source changes require explicit confirmation and restart.");
            if (Buffer.Dirty || IsConfiguration) throw new InvalidOperationException("Save/discard and close the configuration editor before changing its source.");
            var path = Resolve("config", "settings.json");
            var native = await SnapshotAsync(path, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException("Initialize native settings first.");
            byte[]? primary = null;
            byte[]? next = null;
            var source = Resolve("primary", "config");
            var created = false;
            var committed = false;
            try
            {
                ValidateConfiguration(native, "config");
                var settings = JsonSerializer.Deserialize(native.AsSpan(native.AsSpan().StartsWith("\uFEFF"u8) ? 3 : 0),
                    NomadNetJsonContext.Default.NomadNetSettings)!;
                primary = await SnapshotAsync(source, cancellationToken).ConfigureAwait(false);
                var existingSource = primary is not null;
                if (settings.UsePrimaryConfiguration && !existingSource)
                    throw new FileNotFoundException("Active primary ConfigObj source is missing; restore it or explicitly repair native JSON before changing sources.");
                if (primary is not null && primary.Length > NomadNetConfigDocument.MaximumBytes)
                    throw new InvalidDataException("Primary configuration exceeds64 KiB.");
                if (enabled && primary is null) primary = Utf8.GetBytes(NomadNetPrimaryConfiguration.Export(settings, _profile));
                if ((enabled || settings.UsePrimaryConfiguration) && primary is not null)
                {
                    settings = NomadNetConfigImport.Overlay(Utf8.GetString(primary), settings).Settings;
                    settings.Validate();
                }
                settings = settings with { UsePrimaryConfiguration = enabled };
                next = JsonSerializer.SerializeToUtf8Bytes(settings, NomadNetJsonContext.Default.NomadNetSettings);
                ValidateConfiguration(next, "config");
                if (enabled)
                {
                    if (!existingSource)
                    {
                        await WriteAsync(source, primary!, false, cancellationToken, null, verifySnapshot: true).ConfigureAwait(false);
                        created = true;
                    }
                    else await CheckAsync(source, primary, cancellationToken).ConfigureAwait(false);
                }
                else if (primary is not null) await CheckAsync(source, primary, cancellationToken).ConfigureAwait(false);
                ObjectDisposedException.ThrowIf(_disposed, this);
                await WriteAsync(path, next, true, cancellationToken, native, verifySnapshot: true).ConfigureAwait(false);
                committed = true;
            }
            catch (Exception failure)
            {
                if (created && !committed)
                {
                    try
                    {
                        await CheckAsync(source, primary, CancellationToken.None).ConfigureAwait(false);
                        Guard(source);
                        File.Delete(source);
                    }
                    catch (Exception rollback) { throw new AggregateException("Source enable failed; inactive primary file retained after rollback failure.", failure, rollback); }
                }
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(native);
                if (primary is not null) CryptographicOperations.ZeroMemory(primary);
                if (next is not null) CryptographicOperations.ZeroMemory(next);
            }
        }, cancellationToken);

    private async Task<NomadNetSettings> ReadNativeSettingsAsync(CancellationToken token)
    {
        var bytes = await SnapshotAsync(Resolve("config", "settings.json"), token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("Native settings are missing.");
        try
        {
            ValidateConfiguration(bytes, "config");
            return JsonSerializer.Deserialize(bytes.AsSpan(bytes.AsSpan().StartsWith("\uFEFF"u8) ? 3 : 0),
                NomadNetJsonContext.Default.NomadNetSettings)!;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
