using DotReticulum.Core;
using LXMF;

namespace Sideband;

public static class SidebandDeliveryRetry
{
    public static bool ShouldRetry(LxmState state, bool eligible, bool stampGenerationFailed) =>
        state == LxmState.Failed && eligible && !stampGenerationFailed;

    public static LxMessage CreatePropagatedRetry(LxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var source = message.SourceIdentity ?? throw new InvalidOperationException("The outbound message source is unavailable.");
        var retry = LxMessage.CreateBinary(message.DestinationHash.ToArray(), source,
            message.ContentBytes, message.TitleBytes, method: LxmMethod.Propagated, timestamp: message.Timestamp);
        if (message.FieldsValue is MsgPackMap map) retry.SetFieldMap(map);
        else retry.SetFields(new Dictionary<object, object?>(message.Fields));
        return retry;
    }
}
