using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DotReticulum.Core;

namespace LXMF.Tests;

public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DOTRETICULUM_LIVE") != "1")
        {
            Skip = "Live Python interop is opt-in: set DOTRETICULUM_LIVE=1 (plus DOTRETICULUM_PYTHON / DOTRETICULUM_UPSTREAM).";
        }
    }
}

public sealed class LiveTheoryAttribute : TheoryAttribute
{
    public LiveTheoryAttribute()
    {
        Skip = new LiveFactAttribute().Skip;
    }
}

// LIVE: messages packed by DotReticulum are opened by upstream LXMF (tests/Interop/verify_dotnet_lxmf.py): unpack, signature, hash, fields, stamp, app_data.
//   dotnet test tests/LXMF.Tests -c Release --filter "FullyQualifiedName~LxmfDotNetToPython"
[Trait("Category", "Integration")]
public class LxmfDotNetToPythonTests
{
    [LiveFact]
    [Trait("Category", "Live")]
    public async Task UpstreamLxmf_AcceptsDotNetMessagesStampsAndAppData()
    {
        var source = Identity.Generate();
        var destination = Identity.Generate();
        var destinationHash = LxMessage.DeliveryDestinationHash(destination);
        var messages = new List<(string Label, LxMessage Message, int? Cost)>();

        LxMessage Make(string label, string content, string title, Dictionary<object, object?>? fields, LxmMethod method, double timestamp, int? cost = null)
        {
            var m = LxMessage.Create(destinationHash, source, content, title, fields, method, timestamp);
            m.Pack(cost is { } c ? LxStamper.Generate(m.ComputeHash(), c) : null);
            messages.Add((label, m, cost));
            return m;
        }

        Make("short", "Hello from .NET", "Greeting", null, LxmMethod.Opportunistic, 1700000100.5);
        Make("fields", "Fields \u2603", "F", new() { [1L] = new object?[] { new byte[] { 1, 2, 3 } }, [5L] = new object?[] { new object?[] { "a.txt", new byte[] { 9, 8 } } }, [0x0CL] = new object?[] { 1900000000.0, new byte[16] } }, LxmMethod.Direct, 1700000101.0);
        Make("big", new string('z', 5000), "", null, LxmMethod.Direct, 1700000102.0);
        Make("stamped", "Stamped from .NET", "S", null, LxmMethod.Direct, 1700000103.0, cost: 6);
        var ticket = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        var ticketed = LxMessage.Create(destinationHash, source, "Ticket from .NET", "T", timestamp: 1700000104.0);
        ticketed.Pack(ticketed.CreateTicketStamp(ticket));
        messages.Add(("ticket", ticketed, null));
        var paper = LxMessage.Create(destinationHash, source, "Paper from .NET", "P", method: LxmMethod.Paper, timestamp: 1700000105.0);
        paper.Pack(destinationIdentity: destination);
        using var group = DestinationEndpoint.Create(DestinationType.Group, DestinationDirection.Out, "lxmf", ["delivery"], destination);
        group.LoadGroupKey(Enumerable.Range(0, 64).Select(value => (byte)value).ToArray());
        var groupPaper = LxMessage.Create(group, source, "Group paper from .NET", "G", method: LxmMethod.Paper, timestamp: 1700000106.0);
        groupPaper.Pack();
        using var plain = DestinationEndpoint.Create(DestinationType.Plain, DestinationDirection.Out, "lxmf", ["delivery"]);
        var plainMessage = LxMessage.Create(plain, source, "Plain from .NET", "P", method: LxmMethod.Opportunistic, timestamp: 1700000107.0);
        plainMessage.Pack();
        messages.Add(("plain", plainMessage, null));
        var extended = LxMessage.Create(destinationHash, source, "Extended fields from .NET", "E", timestamp: 1700000108.0);
        extended.SetFieldMap(new MsgPackMap(new(null, "nil"), new(new object?[] { 1L, new byte[] { 2 }, null }, "tuple"),
            new(double.PositiveInfinity, "infinite"), new(double.CopySign(double.NaN, 1), "nan")));
        extended.Pack();
        messages.Add(("extended-fields", extended, null));

        var path = Path.Combine(AppContext.BaseDirectory, "dotnet-lxmf-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using (var stream = File.Create(path))
            using (var json = new Utf8JsonWriter(stream))
            {
                json.WriteStartObject();
                json.WriteStartArray("identities");
                foreach (var id in new[] { source, destination })
                {
                    json.WriteStartObject();
                    json.WriteString("public_key", Convert.ToHexStringLower(id.ExportPublicKey()));
                    json.WriteString("delivery_hash", Convert.ToHexStringLower(LxMessage.DeliveryDestinationHash(id)));
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteStartArray("messages");
                foreach (var (label, m, cost) in messages)
                {
                    json.WriteStartObject();
                    json.WriteString("label", label);
                    json.WriteString("packed", Convert.ToHexStringLower(m.Packed!));
                    json.WriteString("hash", Convert.ToHexStringLower(m.Hash!));
                    json.WriteString("title", m.Title);
                    json.WriteString("content", m.Content);
                    json.WriteNumber("timestamp", m.Timestamp);
                    json.WriteString("fields_packed", Convert.ToHexStringLower(MsgPack.Pack(m.FieldsValue)));
                    if (m.Stamp is not null)
                    {
                        json.WriteString("stamp", Convert.ToHexStringLower(m.Stamp));
                        if (cost is { } stampCost) json.WriteNumber("stamp_cost", stampCost);
                        else json.WriteString("ticket", Convert.ToHexStringLower(ticket));
                    }

                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteStartObject("paper");
                var testPrivateKey = destination.ExportPrivateKey();
                try { json.WriteString("destination_private_key", Convert.ToHexStringLower(testPrivateKey)); }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(testPrivateKey); }
                json.WriteString("uri", paper.AsUri(finalise: false));
                json.WriteString("packed", Convert.ToHexStringLower(paper.Packed!));
                json.WriteString("container", Convert.ToHexStringLower(paper.PackedContainer()));
                json.WriteEndObject();
                json.WriteStartObject("group_paper");
                var groupKey = group.ExportGroupKey();
                try { json.WriteString("key", Convert.ToHexStringLower(groupKey)); }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(groupKey); }
                json.WriteString("uri", groupPaper.AsUri(finalise: false));
                json.WriteString("packed", Convert.ToHexStringLower(groupPaper.Packed!));
                json.WriteEndObject();
                json.WriteStartArray("app_data");
                foreach (var (name, cost) in new (string?, int?)[] { ("Alice", null), ("Bob \u00e9", 12), (null, null) })
                {
                    json.WriteStartObject();
                    json.WriteString("data", Convert.ToHexStringLower(LxmfAppData.Build(name, cost)));
                    if (name is null) json.WriteNull("display_name"); else json.WriteString("display_name", name);
                    if (cost is null) json.WriteNull("stamp_cost"); else json.WriteNumber("stamp_cost", cost.Value);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTRETICULUM_PYTHON") ?? "python")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add(FindScript());
            psi.ArgumentList.Add(path);
            psi.Environment["PYTHONPATH"] = Environment.GetEnvironmentVariable("DOTRETICULUM_UPSTREAM")
                ?? throw new InvalidOperationException("DOTRETICULUM_UPSTREAM must point at the pinned upstream checkout.");
            if (Environment.GetEnvironmentVariable("DOTRETICULUM_LXMF_UPSTREAM") is { Length: > 0 } lxmfRoot)
                psi.Environment["PYTHONPATH"] += Path.PathSeparator + lxmfRoot;
            using var process = Process.Start(psi)!;
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, "Upstream LXMF rejected .NET output:\n" + stderr);
            Assert.Contains("all LXMF output accepted by upstream", stderr);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string FindScript()
    {
        var deployed = Path.Combine(AppContext.BaseDirectory, "Interop", "verify_dotnet_lxmf.py");
        if (File.Exists(deployed)) return deployed;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "Interop", "verify_dotnet_lxmf.py");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException("tests/Interop/verify_dotnet_lxmf.py not found.");
    }
}
