namespace Sideband;

public static class SidebandMessageActions
{
    public static bool CanRetryText(SidebandChatMessage message) =>
        !message.Incoming && message.DeliveryState == "Failed" &&
        message.DeliveryMethod != "Paper" && message.PaperUri is null &&
        !string.IsNullOrWhiteSpace(message.Content) && message.Content.Length <= 65_536;
}
