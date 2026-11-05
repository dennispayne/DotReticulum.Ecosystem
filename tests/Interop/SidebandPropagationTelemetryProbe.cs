using System.Text.Json;
using DotReticulum.Applications;
using DotReticulum.Core;
using Sideband;

internal static class SidebandPropagationTelemetryProbe
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["--help"])
        {
            Console.WriteLine("Propagation telemetry acceptance probe: --self-test <fixture> | --rnsconfig <directory> --identity <explicit-owned-file>");
            return 0;
        }
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        if (args is ["--self-test", var fixture])
        {
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(fixture, stop.Token));
            var status = SidebandTelemetryValue.Decode(Convert.FromHexString(document.RootElement.GetProperty("status_hex").GetString()!));
            using var identity = Identity.Generate();
            var clock = new Clock();
            var source = new Source(status);
            await using var provider = new SidebandPropagationTelemetry(identity, source, clock);
            var initial = await provider.CollectAsync(stop.Token);
            if (initial.Reading.Status != SidebandTelemetryStatus.Available || source.Calls != 1) return 1;
            source.Value = SidebandTelemetryValue.From(0xf1L);
            clock.Now += 300;
            var failure = await provider.CollectAsync(stop.Token);
            if (failure.Failure != SidebandPropagationTelemetryFailure.AccessDenied || !ReferenceEquals(initial.Reading, failure.Reading)) return 2;
            source.Value = status;
            clock.Now += 300;
            var recovered = await provider.CollectAsync(stop.Token);
            if (recovered.Failure != SidebandPropagationTelemetryFailure.None || source.Calls != 3) return 3;
            Console.WriteLine(Convert.ToHexStringLower(SidebandTelemetryCodec.Encode(SidebandTelemetryCodec.FromSensors(recovered.Reading))));
            await provider.DisposeAsync();
            if (!identity.HasPrivateKey || provider.Snapshot.Reading.Status != SidebandTelemetryStatus.Unavailable) return 4;
            Console.WriteLine("PASS native provider status/failure/recovery/wire/disposal");
            return 0;
        }
        if (args is ["--rnsconfig", var config, "--identity", var path])
        {
            using var identity = IdentityFile.Load(path);
            await using var runtime = ReticulumNode.Open(config);
            await runtime.StartAsync(stop.Token);
            await using var provider = new SidebandPropagationTelemetry(identity, new SidebandPropagationTelemetrySource(runtime.Router));
            provider.ErrorReported += Console.Error.WriteLine;
            var report = await provider.CollectAsync(stop.Token);
            if (report.Failure != SidebandPropagationTelemetryFailure.None) return 5;
            Console.WriteLine(Convert.ToHexStringLower(SidebandTelemetryCodec.Encode(SidebandTelemetryCodec.FromSensors(report.Reading))));
            Console.WriteLine("PASS native provider actual lxmd query");
            return 0;
        }
        Console.Error.WriteLine("Explicit probe mode and inputs are required.");
        return 64;
    }

    private sealed class Clock : TimeProvider
    {
        internal double Now = 1000;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(Now);
    }
    private sealed class Source(SidebandTelemetryValue value) : ISidebandPropagationTelemetrySource
    {
        internal SidebandTelemetryValue Value = value;
        internal int Calls;
        public Task<SidebandTelemetryValue> QueryAsync(Identity identity, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(Value);
        }
    }
}
