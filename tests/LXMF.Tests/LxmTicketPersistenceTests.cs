using System.Security.Cryptography;
using System.Text.Json;
using DotReticulum.Core;

namespace LXMF.Tests;

[Trait("Category", "Integration")]
public class LxmTicketPersistenceTests
{
    private sealed class Clock(double now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds((long)(now * 1000));
    }

    [Fact]
    public void RuntimeReload_ReplacesMemoryButPreservesOwnedSecretsOnCorruptionOrMissingFile()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxmf-contract-vectors.json")));
        using var policy = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "lxmf-node-policy-vectors.json")));
        var source = policy.RootElement.GetProperty("ticket_reload");
        var vector = fixture.RootElement.GetProperty("ticket_policy");
        var directory = Path.Combine(AppContext.BaseDirectory, "ticket-reload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "available_tickets");
        var destination = Convert.FromHexString(vector.GetProperty("destination").GetString()!);
        var packed = Convert.FromHexString(vector.GetProperty("store_packed").GetString()!);
        var expected = Convert.FromHexString(vector.GetProperty("issued_ticket").GetString()!);
        var copied = Array.Empty<byte>();
        try
        {
            File.WriteAllBytes(path, packed);
            using var store = new LxmTicketStore(path, new Clock(vector.GetProperty("now").GetDouble()));
            copied = store.GetOutbound(destination)!.Token;
            var other = Enumerable.Repeat((byte)'n', 16).ToArray();
            var secret = Enumerable.Repeat((byte)'s', 16).ToArray();
            try { store.Remember(other, new LxmTicket(vector.GetProperty("now").GetDouble() + 100, secret)); }
            finally { CryptographicOperations.ZeroMemory(secret); }
            File.WriteAllBytes(path, packed);
            Assert.Equal(source.GetProperty("replaces_memory").GetBoolean(), store.Reload());
            Assert.Null(store.GetOutbound(other));
            Assert.Equal(expected, copied);
            File.WriteAllBytes(path, [0xc1]);
            Assert.ThrowsAny<Exception>(() => store.Reload());
            var current = store.GetOutbound(destination);
            try
            {
                Assert.Equal(source.GetProperty("corrupt_preserved").GetBoolean(), current is not null);
                Assert.Equal(expected, current!.Token);
            }
            finally { if (current is not null) CryptographicOperations.ZeroMemory(current.Token); }
            File.Delete(path);
            Assert.False(store.Reload());
            current = store.GetOutbound(destination);
            try { Assert.Equal(source.GetProperty("missing_preserved").GetBoolean(), current is not null); }
            finally { if (current is not null) CryptographicOperations.ZeroMemory(current.Token); }
            store.Save();
            Assert.True(File.Exists(path));
            Assert.Throws<IOException>(() => new LxmTicketStore(path));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(packed);
            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(copied);
            Directory.Delete(directory, recursive: true);
        }
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
