using System.Collections.Frozen;
using DotReticulum.Core;
using LXST;

namespace Sideband;

public enum SidebandVoiceState
{
    Available = 0,
    Connecting = 1,
    Ringing = 2,
    InCall = 3,
}

public sealed record SidebandVoiceConfiguration
{
    public bool TrustedOnly { get; init; }
    public bool DirectPushToTalk { get; init; }
    public bool HalfDuplexLoudspeaker { get; init; } = true;
    public string? Microphone { get; init; }
    public string? Speaker { get; init; }
    public string? Loudspeaker { get; init; }
    public string? Ringer { get; init; }
}

/// <param name="DeliveryTrusted">Null means there is no LXMF conversation; false overrides voice-only trust.</param>
public sealed record SidebandVoicePeer(string IdentityHash, bool? DeliveryTrusted = null,
    bool VoiceTrusted = false, bool DeliveryPushToTalk = false);

public sealed record SidebandVoiceDeliveryPeer(string DestinationHash, bool Trusted, bool PushToTalk = false);

/// <summary>Immutable owner policy, keyed by identity, not by an LXMF destination hash.</summary>
public sealed class SidebandVoicePolicy
{
    private readonly FrozenDictionary<string, SidebandVoicePeer> _peers;
    private readonly FrozenDictionary<string, SidebandVoiceDeliveryPeer> _deliveryPeers;

    public SidebandVoicePolicy(SidebandVoiceConfiguration? configuration = null,
        IEnumerable<SidebandVoicePeer>? peers = null, IEnumerable<SidebandVoiceDeliveryPeer>? deliveryPeers = null)
    {
        Configuration = configuration ?? new();
        var copy = new Dictionary<string, SidebandVoicePeer>(StringComparer.OrdinalIgnoreCase);
        foreach (var peer in peers ?? [])
        {
            ArgumentNullException.ThrowIfNull(peer);
            if (peer.IdentityHash is not { Length: 32 } || !peer.IdentityHash.All(char.IsAsciiHexDigit))
                throw new ArgumentException("A voice peer must have a 16-byte identity hash.", nameof(peers));
            if (!copy.TryAdd(peer.IdentityHash, peer))
                throw new ArgumentException("Duplicate voice peer identity.", nameof(peers));
        }
        _peers = copy.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        var deliveryCopy = new Dictionary<string, SidebandVoiceDeliveryPeer>(StringComparer.OrdinalIgnoreCase);
        foreach (var peer in deliveryPeers ?? [])
        {
            ArgumentNullException.ThrowIfNull(peer);
            if (peer.DestinationHash is not { Length: 32 } || !peer.DestinationHash.All(char.IsAsciiHexDigit))
                throw new ArgumentException("A delivery peer must have a 16-byte destination hash.", nameof(deliveryPeers));
            if (!deliveryCopy.TryAdd(peer.DestinationHash, peer))
                throw new ArgumentException("Duplicate delivery peer destination.", nameof(deliveryPeers));
        }
        _deliveryPeers = deliveryCopy.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    public SidebandVoiceConfiguration Configuration { get; }
    public bool IsAllowed(string identityHash)
    {
        if (!Configuration.TrustedOnly) return true;
        if (TryDeliveryPeer(identityHash, out var delivery)) return delivery!.Trusted;
        return _peers.TryGetValue(identityHash, out var peer) && (peer.DeliveryTrusted ?? peer.VoiceTrusted);
    }

    public bool AllowsDirectPushToTalk(string identityHash)
    {
        if (!Configuration.DirectPushToTalk) return false;
        if (TryDeliveryPeer(identityHash, out var delivery)) return delivery!.Trusted && delivery.PushToTalk;
        return _peers.TryGetValue(identityHash, out var peer) && peer.DeliveryTrusted == true && peer.DeliveryPushToTalk;
    }

    private bool TryDeliveryPeer(string identityHash, out SidebandVoiceDeliveryPeer? peer)
    {
        peer = null;
        if (_deliveryPeers.Count == 0 || identityHash is not { Length: 32 } || !identityHash.All(char.IsAsciiHexDigit))
            return false;
        var destination = Destination.ComputeHashFromNameAndIdentity("lxmf.delivery", Convert.FromHexString(identityHash));
        return _deliveryPeers.TryGetValue(Convert.ToHexStringLower(destination), out peer);
    }
}

public sealed record SidebandVoiceSnapshot(SidebandVoiceState State, bool Started, bool Incoming,
    string? RemoteIdentityHash, TelephoneProfile? Profile, TelephoneMode? Mode,
    bool TransmitMuted, bool ReceiveMuted, bool TransmitSquelched, bool LoudspeakerEnabled,
    DateTimeOffset? EstablishedAt, long ReceivedFrames, long TransmittedFrames);

public sealed record SidebandVoiceCallEvent(DateTimeOffset Time, string Event, string IdentityHash);

public sealed record SidebandVoiceError(string Context, Exception? Exception);
