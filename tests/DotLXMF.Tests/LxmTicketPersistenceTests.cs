using System.Security.Cryptography;
using System.Text.Json;
using DotReticulum.Core;

namespace DotLXMF.Tests;

[Trait("Category", "Integration")]
public class LxmTicketPersistenceTests
{
    private sealed class Clock(double now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds((long)(now * 1000));
    }

    [Fact]
    public void PythonTicketStore_ImportsAndSurvivesAtomicReplacementAndReopen()
    {
        var root = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxmf-contract-vectors.json"))).RootElement;
        var vector = root.GetProperty("ticket_policy");
        var directory = Path.Combine(AppContext.BaseDirectory, "ticket-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "available_tickets");
        var destination = Convert.FromHexString(vector.GetProperty("destination").GetString()!);
        var ticket = Convert.FromHexString(vector.GetProperty("issued_ticket").GetString()!);
        var clock = new Clock(vector.GetProperty("now").GetDouble());
        try
        {
            File.WriteAllBytes(path, Convert.FromHexString(vector.GetProperty("store_packed").GetString()!));
            using (var store = new LxmTicketStore(path, clock))
            {
                Assert.Equal(ticket, store.GetOutbound(destination)!.Token);
                Assert.Equal(ticket, Assert.Single(store.GetInbound(destination)));
                Assert.Throws<IOException>(() => new LxmTicketStore(path, clock));
                store.MarkDelivered(destination);
            }

            using (var reopened = new LxmTicketStore(path, clock))
            {
                Assert.Null(reopened.Generate(destination));
                Assert.Equal(ticket, reopened.GetOutbound(destination)!.Token);
            }

            var packed = File.ReadAllBytes(path);
            try
            {
                var map = Assert.IsType<Dictionary<object, object?>>(MsgPack.Unpack(packed));
                Assert.True(map.ContainsKey("inbound"));
                Assert.True(map.ContainsKey("outbound"));
                Assert.True(map.ContainsKey("last_deliveries"));
            }
            finally { CryptographicOperations.ZeroMemory(packed); }
            File.WriteAllBytes(path, [0xc1]);
            Assert.ThrowsAny<Exception>(() => new LxmTicketStore(path, clock));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
