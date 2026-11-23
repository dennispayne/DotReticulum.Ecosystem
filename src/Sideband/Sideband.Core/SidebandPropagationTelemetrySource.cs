using DotReticulum.Core;
using DotReticulum.Transport;
using LXMF;

namespace Sideband;

/// <summary>Caller-owned transport seam. Implementations must honor cancellation and return bounded immutable values.</summary>
public interface ISidebandPropagationTelemetrySource
{
    Task<SidebandTelemetryValue> QueryAsync(Identity identity, CancellationToken cancellationToken);
}

/// <summary>Queries the explicit identity's propagation-control destination; never discovers a key file or opens another runtime.</summary>
public sealed class SidebandPropagationTelemetrySource : ISidebandPropagationTelemetrySource
{
    private readonly LxmPropagationStatusClient _client;

    public SidebandPropagationTelemetrySource(TransportRouter router) => _client = new(router);

    public async Task<SidebandTelemetryValue> QueryAsync(Identity identity, CancellationToken cancellationToken)
    {
        var response = await _client.QueryAsync(identity, maxResponseBytes: SidebandTelemetryCodec.MaxEncodedBytes,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return SidebandTelemetryValue.Decode(MsgPack.Pack(response, SidebandTelemetryCodec.Limits));
    }
}
