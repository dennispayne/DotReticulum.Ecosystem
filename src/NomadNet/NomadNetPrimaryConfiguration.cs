using System.Globalization;

namespace NomadNet;

public static class NomadNetPrimaryConfiguration
{
    internal static NomadNetSettings Project(NomadNetSettings source, NomadNetSettings basis) => basis with
    {
        EnableClient = source.EnableClient, UserInterface = source.UserInterface,
        DownloadsPath = source.DownloadsPath, AttachmentSavePath = source.AttachmentSavePath,
        NotifyOnNewMessage = source.NotifyOnNewMessage, ClientAnnounceAtStart = source.ClientAnnounceAtStart,
        ClientAnnounceIntervalMinutes = source.ClientAnnounceIntervalMinutes, TryPropagationOnSendFail = source.TryPropagationOnSendFail,
        PeriodicLxmfSync = source.PeriodicLxmfSync, LxmfSyncIntervalMinutes = source.LxmfSyncIntervalMinutes, LxmfSyncLimit = source.LxmfSyncLimit,
        InboundStampCost = source.InboundStampCost, AcceptInvalidStamps = source.AcceptInvalidStamps,
        DeliveryTransferLimitBytes = source.DeliveryTransferLimitBytes, CompactAnnounceStream = source.CompactAnnounceStream,
        ComposeInMarkdown = source.ComposeInMarkdown, Editor = source.Editor, ImageLoading = source.ImageLoading,
        ClipboardCopy = source.ClipboardCopy, RrcRenderMarkdown = source.RrcRenderMarkdown, RrcRenderMicron = source.RrcRenderMicron,
        LogLevel = source.LogLevel, LogDestination = source.LogDestination, LogFile = source.LogFile,
        EnableNode = source.EnableNode, NodeName = source.NodeName, NodeAnnounceAtStart = source.NodeAnnounceAtStart,
        NodeAnnounceIntervalMinutes = source.NodeAnnounceIntervalMinutes, EnablePropagation = source.EnablePropagation,
        PropagationStampCost = source.PropagationStampCost, PropagationTransferLimitBytes = source.PropagationTransferLimitBytes,
        PropagationSyncLimitBytes = source.PropagationSyncLimitBytes, PagesPath = source.PagesPath, FilesPath = source.FilesPath,
        NodePageRefreshIntervalMinutes = source.NodePageRefreshIntervalMinutes, NodeFileRefreshIntervalMinutes = source.NodeFileRefreshIntervalMinutes,
        PrioritisedDestinations = source.PrioritisedDestinations, PropagationStaticPeers = source.PropagationStaticPeers,
        PropagationMaxPeers = source.PropagationMaxPeers, PropagationStorageLimitBytes = source.PropagationStorageLimitBytes,
        PrintMessages = source.PrintMessages, PrintCommand = source.PrintCommand, PrintAllMessages = source.PrintAllMessages,
        PrintTrustedMessages = source.PrintTrustedMessages, AllowedMessagePrintDestinations = source.AllowedMessagePrintDestinations,
        PrintingMessageTemplate = source.PrintingMessageTemplate,
    };

    public static string Export(NomadNetSettings settings, string? profile = null)
    {
        if (settings.NodePageRefreshIntervalMinutes != Math.Truncate(settings.NodePageRefreshIntervalMinutes) ||
            settings.NodeFileRefreshIntervalMinutes != Math.Truncate(settings.NodeFileRefreshIntervalMinutes))
            throw new InvalidOperationException("Primary page/file refresh intervals require whole minutes. Set native nodePageRefreshIntervalMinutes and nodeFileRefreshIntervalMinutes explicitly (0 disables) before enabling primary configuration; no rounding is performed.");
        if (settings.PrintingMessageTemplate != NomadNetPrintingPolicy.DefaultTemplate)
            throw new InvalidOperationException("An embedded custom print template cannot be converted to a primary template filename implicitly. Keep native JSON or explicitly reconcile the template first.");
        var sections = new Dictionary<string, Dictionary<string, NomadNetConfigValue?>>(StringComparer.Ordinal);
        Add("logging", "loglevel", settings.LogLevel?.ToString(CultureInfo.InvariantCulture));
        Add("logging", "destination", settings.LogDestination is null ? null : settings.LogDestination == NomadNetLogDestination.File ? "file" : "stdout");
        Add("logging", "logfile", settings.LogFile);
        Add("client", "enable_client", Bool(settings.EnableClient));
        Add("client", "user_interface", settings.UserInterface.ToString().ToLowerInvariant());
        Add("client", "downloads_path", settings.DownloadsPath ?? (profile is null ? null : Path.Combine(profile, "downloads")));
        Add("client", "attachment_save_path", settings.AttachmentSavePath);
        Add("client", "notify_on_new_message", Bool(settings.NotifyOnNewMessage));
        Add("client", "announce_at_start", Bool(settings.ClientAnnounceAtStart));
        Add("client", "announce_interval", Number(settings.ClientAnnounceIntervalMinutes));
        Add("client", "try_propagation_on_send_fail", Bool(settings.TryPropagationOnSendFail));
        Add("client", "periodic_lxmf_sync", Bool(settings.PeriodicLxmfSync));
        Add("client", "lxmf_sync_interval", Number(settings.LxmfSyncIntervalMinutes));
        Add("client", "lxmf_sync_limit", Number(settings.LxmfSyncLimit));
        Add("client", "required_stamp_cost", settings.InboundStampCost?.ToString(CultureInfo.InvariantCulture) ?? "None");
        Add("client", "accept_invalid_stamps", Bool(settings.AcceptInvalidStamps));
        Add("client", "max_accepted_size", Number(settings.DeliveryTransferLimitBytes / 1000d));
        Add("client", "compact_announce_stream", Bool(settings.CompactAnnounceStream));
        Add("client", "compose_in_markdown", Bool(settings.ComposeInMarkdown));
        Add("textui", "editor", settings.Editor);
        Add("textui", "image_loading", settings.ImageLoading.ToString().ToLowerInvariant());
        Add("textui", "clipboard_copy", Bool(settings.ClipboardCopy));
        Add("rrc", "render_markdown", Bool(settings.RrcRenderMarkdown));
        Add("rrc", "render_micron", Bool(settings.RrcRenderMicron));
        Add("node", "enable_node", Bool(settings.EnableNode));
        Add("node", "node_name", settings.NodeName ?? "None");
        Add("node", "announce_at_start", Bool(settings.NodeAnnounceAtStart));
        Add("node", "announce_interval", Number(settings.NodeAnnounceIntervalMinutes));
        Add("node", "disable_propagation", Bool(!settings.EnablePropagation));
        Add("node", "propagation_cost", Number(settings.PropagationStampCost));
        Add("node", "max_transfer_size", Number(settings.PropagationTransferLimitBytes / 1000d));
        Add("node", "max_sync_size", Number(settings.PropagationSyncLimitBytes / 1000d));
        Add("node", "pages_path", settings.PagesPath);
        Add("node", "files_path", settings.FilesPath);
        Add("node", "page_refresh_interval", Number(settings.NodePageRefreshIntervalMinutes));
        Add("node", "file_refresh_interval", Number(settings.NodeFileRefreshIntervalMinutes));
        List("node", "prioritise_destinations", settings.PrioritisedDestinations);
        List("node", "static_peers", settings.PropagationStaticPeers);
        Add("node", "max_peers", settings.PropagationMaxPeers?.ToString(CultureInfo.InvariantCulture));
        Add("node", "message_storage_limit", Number(settings.PropagationStorageLimitBytes / 1_000_000d));
        Add("printing", "print_messages", Bool(settings.PrintMessages));
        Add("printing", "print_command", settings.PrintCommand);
        if (settings.PrintAllMessages) Add("printing", "print_from", "everywhere");
        else
        {
            var sources = settings.AllowedMessagePrintDestinations.ToList();
            if (settings.PrintTrustedMessages) sources.Insert(0, "trusted");
            List("printing", "print_from", sources);
        }
        var text = "# Original native export; primary ConfigObj values, not the upstream configuration assets.\n";
        foreach (var (section, changes) in sections) text = NomadNetConfigDocument.Parse(text).EditSection(section, changes);
        return text;

        void Add(string section, string key, string? value)
        {
            if (value is null) return;
            if (!sections.TryGetValue(section, out var fields)) sections.Add(section, fields = new(StringComparer.Ordinal));
            fields[key] = new(value);
        }
        void List(string section, string key, IEnumerable<string> values)
        {
            if (!sections.TryGetValue(section, out var fields)) sections.Add(section, fields = new(StringComparer.Ordinal));
            var list = values.ToArray();
            if (list.Length != 0) fields[key] = new(list);
        }
        static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        static string Bool(bool value) => value ? "yes" : "no";
    }
}
