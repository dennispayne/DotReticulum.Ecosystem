using System.Text.Json;
using DotReticulum.Core;

namespace LXMF.Tests;

// Executed LXMF 1.2.1 / RNS a29bfb51c468945c96a1ee22cae0114e993e2391;
// exact LXMF source hashes and disclosed no-network shims are in the fixture and generator.
[Trait("Category", "Unit")]
public class LxmfContractTests
{
    [Fact]
    public void StorageWeights_MatchExecutedUpstreamAgeAndPriorityCases()
    {
        var fixture = Root.GetProperty("store_eviction");
        var prioritised = fixture.GetProperty("prioritised").EnumerateArray().Select(value => value.GetString()).ToArray();
        foreach (var entry in fixture.GetProperty("entries").EnumerateArray())
            Assert.Equal(entry.GetProperty("weight").GetDouble(), LxmPropagationNode.CalculateStorageWeight(
                entry.GetProperty("size").GetInt64(), entry.GetProperty("received").GetDouble(), 1700000302.0,
                prioritised.Contains(entry.GetProperty("destination").GetString())));
    }

    [Fact]
    public async Task StampGeneration_CancelsQueuedAndActiveWorkWithoutPoisoningTheWorkerPool()
    {
        var id = Identity.FullHash("cancellation"u8);
        using var activeCancellation = new CancellationTokenSource();
        using var queuedCancellation = new CancellationTokenSource();
        var active = LxStamper.GenerateAsync(id, 256, activeCancellation.Token, LxStamper.WorkblockExpandRoundsPeering);
        var queued = LxStamper.GenerateAsync(id, 4, queuedCancellation.Token, LxStamper.WorkblockExpandRoundsPeering);
        queuedCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        activeCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        var stamp = await LxStamper.GenerateAsync(id, 4, expandRounds: LxStamper.WorkblockExpandRoundsPeering);
        Assert.True(LxStamper.IsValid(stamp, 4, LxStamper.Workblock(id, LxStamper.WorkblockExpandRoundsPeering)));
    }

    private sealed class Clock(double now) : TimeProvider
    {
        public double Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds((long)(Now * 1000));
    }

    private static readonly JsonElement Root = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxmf-contract-vectors.json"))).RootElement;

    private static byte[] Hex(JsonElement item) => Convert.FromHexString(item.GetString()!);
    private static Identity Source => Identity.FromPrivateKey(Hex(Root.GetProperty("source_private_key")));
    private static Identity Destination => Identity.FromPrivateKey(Hex(Root.GetProperty("destination_private_key")));

    [Fact]
    public void TicketStamp_IsByteIdenticalAndValidatesBeforeProofOfWork()
    {
        var vector = Root.GetProperty("ticket");
        var message = LxMessage.Create(LxMessage.DeliveryDestinationHash(Destination), Source, "Ticket", "T", timestamp: 1700000100.25);
        var stamp = message.CreateTicketStamp(Hex(vector.GetProperty("ticket")));
        Assert.Equal(Hex(vector.GetProperty("stamp")), stamp);
        Assert.Equal(Hex(vector.GetProperty("packed")), message.Pack(stamp));
        Assert.True(message.ValidateStamp(254, [Hex(vector.GetProperty("ticket"))]));
        Assert.False(message.ValidateStamp(254, [new byte[16]]));
        Assert.Throws<ArgumentException>(() => message.CreateTicketStamp(new byte[15]));
    }

    [Fact]
    public async Task PaperUri_DecryptsPythonMessageAndPreservesCanonicalUri()
    {
        var vector = Root.GetProperty("paper");
        var message = LxMessage.FromPaperUri(vector.GetProperty("uri").GetString()!, Destination, _ => Source);
        Assert.Equal(Hex(vector.GetProperty("packed")), message.Packed);
        Assert.True(message.SignatureValidated);
        Assert.Equal("Paper Grüße", message.Content);
        Assert.Equal(vector.GetProperty("uri").GetString(), message.AsUri(finalise: false));
        Assert.Equal(LxmState.Generating, message.State);
        message.AsUri();
        Assert.Equal(LxmState.Paper, await message.Completion);
    }

    [Fact]
    public void PaperPacking_EncryptsSignedMessageAndRejectsOversizeOrWrongDestination()
    {
        var destination = Destination;
        var message = LxMessage.Create(LxMessage.DeliveryDestinationHash(destination), Source, "Paper Grüße", "P",
            method: LxmMethod.Paper, timestamp: 1700000101.5);
        Assert.Throws<ArgumentException>(() => message.Pack());
        Assert.Throws<ArgumentException>(() => message.Pack(destinationIdentity: Source));
        Assert.Equal(Hex(Root.GetProperty("paper").GetProperty("packed")), message.Pack(destinationIdentity: destination));
        var decoded = LxMessage.FromPaperUri(message.AsUri(), destination, _ => Source);
        Assert.True(decoded.SignatureValidated);
        Assert.Equal(message.Packed, decoded.Packed);
        var big = LxMessage.Create(LxMessage.DeliveryDestinationHash(destination), Source, new string('x', 3000), method: LxmMethod.Paper);
        Assert.Throws<ArgumentException>(() => big.Pack(destinationIdentity: destination));
        Assert.Null(big.Packed);
        Assert.Throws<FormatException>(() => LxMessage.FromPaperUri("lxm://not-valid", destination, _ => Source));
        Assert.Throws<FormatException>(() => LxMessage.FromPaperUri(message.AsUri(), Source, _ => Source));
    }

    [Fact]
    public void BinaryMessage_PreservesNonUtf8Bytes()
    {
        var message = LxMessage.CreateBinary(LxMessage.DeliveryDestinationHash(Destination), Source, [0xff, 0x00, 0x80], [0xfe]);
        var unpacked = LxMessage.Unpack(message.Pack(), _ => Source);
        Assert.Equal(new byte[] { 0xff, 0x00, 0x80 }, unpacked.ContentBytes);
        Assert.Equal(new byte[] { 0xfe }, unpacked.TitleBytes);
        Assert.True(unpacked.SignatureValidated);
    }

    [Fact]
    public void StampedBinaryMapKeys_PreserveSignatureAndFieldBytes()
    {
        var vector = Root.GetProperty("binary_fields");
        var message = LxMessage.Unpack(Hex(vector.GetProperty("packed")), _ => Source);
        Assert.True(message.SignatureValidated);
        Assert.Equal(Hex(vector.GetProperty("hash")), message.Hash);
        var nested = Assert.IsType<Dictionary<object, object?>>(message.Fields[LxmField.CustomData]);
        Assert.Equal(new byte[] { 0xff, 0x01 }, Assert.IsType<byte[]>(Assert.Single(nested.Keys)));
        Assert.True(message.Validate(Source));
    }

    [Fact]
    public void MessageContainer_MatchesPythonAndRestoresMetadata()
    {
        var vector = Root.GetProperty("paper");
        var packed = Hex(vector.GetProperty("container"));
        var restored = LxMessage.UnpackContainer(packed, _ => Source);
        Assert.Equal(Hex(vector.GetProperty("packed")), restored.Packed);
        Assert.True(restored.SignatureValidated);
        Assert.True(restored.TransportEncrypted);
        Assert.Equal("Curve25519", restored.TransportEncryption);
        Assert.Equal(LxmMethod.Paper, restored.Method);
        Assert.Equal(packed, restored.PackedContainer());
        Assert.Throws<FormatException>(() => LxMessage.UnpackContainer([0x80], _ => Source));
    }

    [Fact]
    public void PropagationEnvelopeAndWorkblocks_MatchExecutedPython()
    {
        var vector = Root.GetProperty("propagation");
        var envelope = Assert.IsType<object?[]>(MsgPack.UnpackApplicationValue(Hex(vector.GetProperty("envelope"))));
        var encrypted = Assert.IsType<byte[]>(Assert.Single(Assert.IsType<object?[]>(envelope[1])));
        var id = Identity.FullHash(encrypted);
        Assert.Equal(Hex(vector.GetProperty("transient_id")), id);
        var plain = Destination.Decrypt(encrypted.AsSpan(16));
        Assert.Equal(Hex(vector.GetProperty("packed")), encrypted[..16].Concat(plain!).ToArray());
        var block = LxStamper.Workblock(id, LxStamper.WorkblockExpandRoundsPn);
        Assert.Equal(vector.GetProperty("workblock_sha256").GetString(), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(block)));
        Assert.True(LxStamper.IsValid(Hex(vector.GetProperty("stamp")), 4, block));
        Assert.Equal(vector.GetProperty("stamp_value").GetInt32(), LxStamper.Value(block, Hex(vector.GetProperty("stamp"))));
        var peering = LxStamper.Workblock(Hex(vector.GetProperty("peering_id")), LxStamper.WorkblockExpandRoundsPeering);
        Assert.Equal(vector.GetProperty("peering_workblock_sha256").GetString(), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(peering)));
        Assert.True(LxStamper.IsValid(Hex(vector.GetProperty("peering_key")), 4, peering));
        Assert.True(LxmfPropagationAnnounce.TryParse(Hex(vector.GetProperty("announce")), out var announce));
        Assert.Equal("Vector PN", announce!.Name);
        Assert.Equal(16, announce.StampCost);
        Assert.Equal(10240, announce.SyncLimitKilobytes);
        Assert.False(LxmfPropagationAnnounce.TryParse([0x90], out _));
        var peer = LxmPeer.FromBytes(Hex(vector.GetProperty("peer")), _ => true);
        Assert.True(peer.Alive);
        Assert.Equal(1, peer.HandledCount);
        Assert.Equal(0, peer.UnhandledCount);
        Assert.Equal(18, peer.PeeringCost);
        Assert.Equal(1, LxmPeer.FromBytes(peer.ToBytes(), _ => true).HandledCount);
        Assert.Equal(0, LxmPeer.FromBytes(peer.ToBytes(), _ => false).HandledCount);
    }

    [Fact]
    public void AnnounceHelpers_MatchExecutedUpstream()
    {
        foreach (var vector in Root.GetProperty("announces").EnumerateArray())
        {
            var packed = Hex(vector.GetProperty("packed"));
            Assert.Equal(vector.GetProperty("name").GetString(), LxmfAppData.DisplayName(packed));
            Assert.Equal(vector.GetProperty("stamp_cost").ValueKind == JsonValueKind.Null ? null : (int?)vector.GetProperty("stamp_cost").GetInt32(),
                LxmfAppData.StampCost(packed));
            Assert.Equal(vector.GetProperty("compression").GetBoolean(), LxmfAppData.CompressionSupported(packed));
        }
    }

    [Fact]
    public void TicketLifecycle_ReusesRenewsSuppressesAndExpiresAccordingToUpstream()
    {
            var vector = Root.GetProperty("ticket_policy");
            var clock = new Clock(vector.GetProperty("now").GetDouble());
            var destination = Hex(vector.GetProperty("destination"));
            using var store = new LxmTicketStore(timeProvider: clock);
            var issued = store.Generate(destination)!;
            Assert.Equal(vector.GetProperty("issued_expiry").GetDouble(), issued.Expires);
            Assert.Equal(issued.Token, store.Generate(destination)!.Token);
            store.Remember(destination, issued);
            Assert.Equal(issued.Token, store.GetOutbound(destination)!.Token);
            Assert.Equal(issued.Token, Assert.Single(store.GetInbound(destination)));
            store.MarkDelivered(destination);
            Assert.Null(store.Generate(destination));
            clock.Now += LxmTicketStore.DeliveryInterval.TotalSeconds;
            Assert.Equal(issued.Token, store.Generate(destination)!.Token);
            clock.Now += TimeSpan.FromDays(7).TotalSeconds;
            var renewed = store.Generate(destination)!;
            Assert.NotEqual(issued.Token, renewed.Token);
            clock.Now = issued.Expires;
            Assert.Null(store.GetOutbound(destination));
            Assert.DoesNotContain(store.GetInbound(destination), token => token.AsSpan().SequenceEqual(issued.Token));
            clock.Now = renewed.Expires + LxmTicketStore.Grace.TotalSeconds + 1;
            store.Clean();
            Assert.Empty(store.GetInbound(destination));
    }

    [Fact]
    public void TicketStore_BoundsDestinationsAndRejectsUseAfterDisposal()
    {
            using var store = new LxmTicketStore(capacity: 1);
            store.Generate(new byte[16]);
            Assert.Throws<InvalidOperationException>(() => store.Generate(Enumerable.Repeat((byte)1, 16).ToArray()));
            store.Dispose();
            Assert.Throws<ObjectDisposedException>(() => store.GetInbound(new byte[16]));
    }
}
