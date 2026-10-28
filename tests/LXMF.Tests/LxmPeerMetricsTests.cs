using System.Text.Json;

namespace LXMF.Tests;

[Trait("Category", "Unit")]
public class LxmPeerMetricsTests
{
    [Fact]
    public void CompletedTransfer_MatchesActualUpstreamWireRateAndDataCounters()
    {
        using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "lxmf-peer-metrics-vectors.json")));
        foreach (var row in vectors.RootElement.GetProperty("cases").EnumerateArray())
        {
            var peer = new LxmPeer(Enumerable.Repeat((byte)'p', 16).ToArray());
            var ids = new[] { Enumerable.Repeat((byte)'a', 32).ToArray(), Enumerable.Repeat((byte)'b', 32).ToArray() };
            foreach (var id in ids) peer.AddUnhandled(id);
            peer.RecordCompletedTransfer(ids, 3, row.GetProperty("data_size").GetInt64(),
                row.GetProperty("transfer_size").GetInt64(), row.GetProperty("elapsed").GetDouble(),
                vectors.RootElement.GetProperty("fixed_now").GetDouble());
            Assert.Equal(row.GetProperty("offered").GetInt64(), peer.Offered);
            Assert.Equal(row.GetProperty("outgoing").GetInt64(), peer.Outgoing);
            Assert.Equal(row.GetProperty("tx_bytes").GetInt64(), peer.TxBytes);
            Assert.Equal(row.GetProperty("sync_transfer_rate").GetDouble(), peer.SyncTransferRate);
            Assert.Equal(row.GetProperty("last_heard").GetDouble(), peer.LastHeard);
            Assert.Equal(row.GetProperty("alive").GetBoolean(), peer.Alive);
            Assert.Equal(row.GetProperty("unhandled_count").GetInt32(), peer.UnhandledCount);
            Assert.Equal(2, peer.HandledCount);
        }
    }
}
