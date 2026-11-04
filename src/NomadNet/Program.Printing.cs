using LXMF;

namespace NomadNet;

internal static partial class Program
{
    private sealed partial class TerminalSession
    {
        private async Task<bool> TryHandlePrintingAsync(string command, CancellationToken cancellationToken)
        {
            if (command.Equals("printing", StringComparison.OrdinalIgnoreCase) ||
                command.StartsWith("printing ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = Split(command, 3);
                if (parts.Length > 1)
                {
                    var current = propagation.Settings;
                    var updated = parts[1] switch
                    {
                        "on" when parts.Length == 2 => current with { PrintMessages = true },
                        "off" when parts.Length == 2 => current with { PrintMessages = false },
                        "command" when parts.Length == 3 => current with { PrintCommand = parts[2] },
                        "template" when parts.Length == 3 => current with
                        {
                            PrintingMessageTemplate = await NomadNetPrintingPolicy.ReadTemplateAsync(parts[2], cancellationToken).ConfigureAwait(false),
                        },
                        "default-template" when parts.Length == 2 => current with { PrintingMessageTemplate = NomadNetPrintingPolicy.DefaultTemplate },
                        "from" when parts.Length == 3 => PrintFrom(current, parts[2]),
                        _ => throw new ArgumentException("Usage: printing [on|off|from <everywhere|trusted|exact-hash-or-list>|command <argv>|template <file>|default-template]"),
                    };
                    await propagation.UpdateSettingsAsync(_ => updated, cancellationToken).ConfigureAwait(false);
                    Console.WriteLine("Printing configuration saved; applies to subsequent jobs.");
                }
                var saved = propagation.Settings;
                Console.WriteLine($"Incoming printing: {saved.PrintMessages}; all={saved.PrintAllMessages}; trusted={saved.PrintTrustedMessages}; exact sources={saved.AllowedMessagePrintDestinations.Count}. Manual printing is explicit and independent.");
                return true;
            }
            if (command.StartsWith("print ", StringComparison.OrdinalIgnoreCase))
            {
                var id = command["print ".Length..].Trim();
                var message = (await store.MessagesAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
                    .SingleOrDefault(message => message.Id == id) ?? throw new ArgumentException("Stored message ID not found.");
                // Outbound history's peer is the recipient, not the message origin.
                var source = message.Direction == "outbound" ? router.DeliveryHashHex : message.PeerHash;
                var contact = await store.FindContactAsync(source, cancellationToken).ConfigureAwait(false);
                var result = await printer.PrintMessageAsync(propagation.Settings, message with { PeerHash = source }, contact,
                    DateTimeOffset.Now, cancellationToken).ConfigureAwait(false);
                Console.WriteLine(result.Success ? "Message print completed." : "Message print failed: " + result.Detail);
                return true;
            }
            if (command.StartsWith("paper-print ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = Split(command, 3);
                if (parts.Length != 3) throw new ArgumentException("Usage: paper-print <hash> <message>");
                var destination = Convert.FromHexString(NomadNetAddress.NormalizeHash(parts[1]));
                var remote = await RecallAsync(destination, cancellationToken).ConfigureAwait(false);
                var message = LxMessage.Create(destination, identity, parts[2], method: LxmMethod.Paper);
                message.Pack(destinationIdentity: remote);
                var id = Convert.ToHexStringLower(message.Hash ?? throw new InvalidDataException("Paper message has no hash."));
                var result = await printer.PrintPaperAsync(propagation.Settings, id, message.AsUri(), cancellationToken).ConfigureAwait(false);
                if (result.Success)
                {
                    await store.SaveMessageAsync(new NomadNetMessage(id, Convert.ToHexStringLower(destination), "outbound",
                        DateTimeOffset.FromUnixTimeMilliseconds((long)(message.Timestamp * 1000)), "", parts[2], "paper"),
                        cancellationToken).ConfigureAwait(false);
                    Console.WriteLine("Paper print completed; conversation saved.");
                }
                else Console.WriteLine("Paper print failed; conversation not saved: " + result.Detail);
                return true;
            }
            return false;
        }

        private static NomadNetSettings PrintFrom(NomadNetSettings settings, string filter)
        {
            var updated = settings.Copy();
            NomadNetConfigImport.ApplyPrintFrom(updated, filter);
            return updated;
        }
    }
}
