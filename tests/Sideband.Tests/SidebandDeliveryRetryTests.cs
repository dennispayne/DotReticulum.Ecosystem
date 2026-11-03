using System.Text.Json;
using DotReticulum.Core;
using LXMF;
using Sideband;

namespace Sideband.Tests;

[Trait("Category", "Unit")]
public sealed class SidebandDeliveryRetryTests
{
    // GenerateDeliveryRetryVectors.py executes primary Sideband message_notification
    // at d069122af14880e43607aed0b71add4c7806fbe0; the fixture records the digest.
    [Fact]
    public void RetryAdmissionMatchesActualReferenceNotification()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "sideband-delivery-retry.json")));
        foreach (var item in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var retry = SidebandDeliveryRetry.ShouldRetry((LxmState)item.GetProperty("state").GetByte(),
                item.GetProperty("eligible").GetBoolean(), item.GetProperty("stamp_failed").GetBoolean());
            Assert.True(item.GetProperty("retry").GetBoolean() == retry, item.GetProperty("name").GetString());
            Assert.Equal(retry ? 1 : 0, item.GetProperty("retry_count_after_second_notification").GetInt32());
        }
    }

    [Fact]
    public void FreshPropagationAttemptRetainsSignedPayloadIdentityWithoutReusingTerminalCompletion()
    {
        using var source = Identity.Generate();
        var message = LxMessage.CreateBinary(new byte[16], source, [0x00, 0xFF], [0xFE],
            new Dictionary<object, object?> { [LxmField.FileAttachments] = new object?[] { new object?[] { "a.bin", new byte[] { 1, 2 } } } },
            timestamp: 12345);
        message.Pack();
        var retry = SidebandDeliveryRetry.CreatePropagatedRetry(message);
        Assert.Equal(message.Hash, retry.ComputeHash());
        Assert.Equal(message.Timestamp, retry.Timestamp);
        Assert.Equal(message.ContentBytes, retry.ContentBytes);
        Assert.Equal(message.TitleBytes, retry.TitleBytes);
        Assert.Equal(LxmMethod.Propagated, retry.DesiredMethod);
        Assert.Equal(0, retry.DeliveryAttempts);
        Assert.Null(retry.NextDeliveryAttempt);
        Assert.Null(retry.Packed);
        Assert.NotSame(message.Completion, retry.Completion);
        Assert.Equal(LxmMethod.Direct, message.Method);
        Assert.NotNull(message.Packed);
    }
}
