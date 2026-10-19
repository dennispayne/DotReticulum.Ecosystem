using DotReticulum.Core;
using DotReticulum.Transport;

namespace DotReticulum.Lxmf;

public enum LxmState : byte
{
    Generating = 0x00,
    Outbound = 0x01,
    Sending = 0x02,
    Sent = 0x04,
    Delivered = 0x08,
    Rejected = 0xFD,
    Cancelled = 0xFE,
    Failed = 0xFF,
}

public enum LxmMethod : byte
{
    Unknown = 0x00,
    Opportunistic = 0x01,
    Direct = 0x02,
    Propagated = 0x03,
    Paper = 0x05,
}

public enum LxmUnverifiedReason : byte
{
    None = 0x00,
    SourceUnknown = 0x01,
    SignatureInvalid = 0x02,
}

/// <summary>Well-known LXMF field keys (<c>LXMF.py</c>).</summary>
public static class LxmField
{
    public const long EmbeddedLxms = 0x01;
    public const long Telemetry = 0x02;
    public const long TelemetryStream = 0x03;
    public const long IconAppearance = 0x04;
    public const long FileAttachments = 0x05;
    public const long Image = 0x06;
    public const long Audio = 0x07;
    public const long Thread = 0x08;
    public const long Commands = 0x09;
    public const long Results = 0x0A;
    public const long Group = 0x0B;
    public const long Ticket = 0x0C;
    public const long Event = 0x0D;
    public const long CustomType = 0xFB;
    public const long CustomData = 0xFC;
    public const long CustomMeta = 0xFD;
}

/// <summary>
/// An LXMF message (upstream <c>LXMessage</c>). Wire form: <c>destination(16) || source(16) || signature(64) || msgpack([timestamp, title, content, fields(, stamp)])</c>.
/// The hash covers destination, source and the payload without the stamp; the signature covers the hashed part followed by the hash.
/// </summary>
public sealed class LxMessage
{
    public const int DestinationLength = 16;
    public const int SignatureLength = 64;
    public const int TimestampSize = 8;
    public const int StructOverhead = 8;
    public const int Overhead = (2 * DestinationLength) + SignatureLength + TimestampSize + StructOverhead;

    /// <summary>Upstream <c>Packet.ENCRYPTED_MDU</c> for the default 500 byte MTU.</summary>
    public const int EncryptedPacketMdu = 383;

    public const int EncryptedPacketMaxContent = EncryptedPacketMdu - Overhead + DestinationLength;

    /// <summary>Largest packed content that still fits a single link packet at the default MTU.</summary>
    public static readonly int LinkPacketMaxContent = Link.ComputeMdu(Packet.Mtu) - Overhead;

    private readonly object _gate = new();
    private readonly TaskCompletionSource<LxmState> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private LxmState _state = LxmState.Generating;
    private double _nextAttempt;

    private LxMessage(byte[] destinationHash, byte[] sourceHash)
    {
        DestinationHash = destinationHash;
        SourceHash = sourceHash;
    }

    public byte[] DestinationHash { get; }

    public byte[] SourceHash { get; }

    /// <summary>Seconds since the Unix epoch, as upstream's <c>time.time()</c>.</summary>
    public double Timestamp { get; private set; }

    public byte[] TitleBytes { get; private set; } = [];

    public byte[] ContentBytes { get; private set; } = [];

    public string Title => System.Text.Encoding.UTF8.GetString(TitleBytes);

    public string Content => System.Text.Encoding.UTF8.GetString(ContentBytes);

    /// <summary>Integer-keyed fields (see <see cref="LxmField"/>). Values are the msgpack object model of <see cref="MsgPack"/>.</summary>
    public Dictionary<object, object?> Fields { get; private set; } = [];

    public byte[]? Stamp { get; private set; }

    public byte[]? Hash { get; private set; }

    public byte[]? Signature { get; private set; }

    public bool SignatureValidated { get; private set; }

    public LxmUnverifiedReason UnverifiedReason { get; private set; }

    public byte[]? Packed { get; private set; }

    public bool Incoming { get; private init; }

    public LxmMethod DesiredMethod { get; set; } = LxmMethod.Direct;

    /// <summary>The delivery method chosen by <see cref="Pack"/> (opportunistic content that does not fit falls back to direct) or, for received messages, the method it arrived by.</summary>
    public LxmMethod Method { get; internal set; }

    /// <summary>True when the packed message is sent as a Resource rather than a single packet (direct delivery only).</summary>
    public bool IsResource { get; private set; }

    public int DeliveryAttempts { get; internal set; }

    internal int PathlessTries { get; set; }

    internal double NextAttempt
    {
        get { lock (_gate) return _nextAttempt; }
        set { lock (_gate) _nextAttempt = value; }
    }

    public LxmState State
    {
        get { lock (_gate) return _state; }
        internal set
        {
            lock (_gate)
            {
                if (_state == value) return;
                if (_state is LxmState.Delivered or LxmState.Failed or LxmState.Rejected or LxmState.Cancelled) return;
                _state = value;
            }

            if (StateChanged is { } callbacks)
            {
                foreach (Action<LxMessage> callback in callbacks.GetInvocationList())
                {
                    try { callback(this); }
                    catch (Exception ex)
                    {
                        var error = $"LXMF state callback failed for destination {Convert.ToHexStringLower(DestinationHash)}: {ex}";
                        if (Log is { } log) log(error);
                        else System.Diagnostics.Trace.TraceError(error);
                    }
                }
            }
            if (value is LxmState.Delivered or LxmState.Failed or LxmState.Rejected or LxmState.Cancelled) _done.TrySetResult(value);
        }
    }

    /// <summary>Raised (on a router thread) whenever <see cref="State"/> changes.</summary>
    public event Action<LxMessage>? StateChanged;

    public Action<string>? Log { get; set; }

    /// <summary>Completes once the message reaches a final state (delivered, failed, rejected or cancelled).</summary>
    public Task<LxmState> Completion => _done.Task;

    /// <summary>Creates an outbound message to <paramref name="destinationHash"/> (an <c>lxmf.delivery</c> hash) from <paramref name="source"/>.</summary>
    public static LxMessage Create(byte[] destinationHash, Identity source, string content, string title = "", Dictionary<object, object?>? fields = null,
        LxmMethod method = LxmMethod.Direct, double? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(destinationHash);
        ArgumentNullException.ThrowIfNull(source);
        if (destinationHash.Length != DestinationLength) throw new ArgumentException("Destination hash must be 16 bytes.", nameof(destinationHash));
        var message = new LxMessage(destinationHash, DeliveryDestinationHash(source))
        {
            TitleBytes = System.Text.Encoding.UTF8.GetBytes(title ?? ""),
            ContentBytes = System.Text.Encoding.UTF8.GetBytes(content ?? ""),
            Fields = fields ?? [],
            DesiredMethod = method,
            Timestamp = timestamp ?? 0,
        };
        message._source = source;
        return message;
    }

    private Identity? _source;

    public static byte[] DeliveryDestinationHash(Identity identity) =>
        Destination.ComputeHash(identity.Hash, LxmRouter.AppName, [LxmRouter.DeliveryAspect]);

    /// <summary>
    /// Signs and packs the message (upstream <c>pack</c>). <paramref name="stamp"/>, when given, is appended to the payload after hashing and does not
    /// change the hash; <see cref="LxStamper"/> can generate one.
    /// </summary>
    public byte[] Pack(byte[]? stamp = null)
    {
        if (Packed is not null) throw new InvalidOperationException("The message was already packed.");
        var source = _source ?? throw new InvalidOperationException("Only locally created messages can be packed.");
        if (Timestamp == 0) Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

        var payload = new object?[] { Timestamp, TitleBytes, ContentBytes, Fields };
        var packedPayload = MsgPack.Pack(payload);
        var hashedPart = Concat(DestinationHash, SourceHash, packedPayload);
        Hash = Identity.FullHash(hashedPart);
        Signature = source.Sign(Concat(hashedPart, Hash));
        SignatureValidated = true;

        if (stamp is not null)
        {
            Stamp = stamp;
            packedPayload = MsgPack.Pack(new object?[] { Timestamp, TitleBytes, ContentBytes, Fields, stamp });
        }

        Packed = Concat(DestinationHash, SourceHash, Signature, packedPayload);
        var contentSize = packedPayload.Length - TimestampSize - StructOverhead;
        Method = DesiredMethod;
        if (DesiredMethod == LxmMethod.Opportunistic && contentSize > EncryptedPacketMaxContent) Method = LxmMethod.Direct;
        if (Method == LxmMethod.Direct) IsResource = contentSize > LinkPacketMaxContent;
        if (Method is LxmMethod.Propagated or LxmMethod.Paper) throw new NotSupportedException("Propagated and paper delivery are not supported by Pack.");
        return Packed;
    }

    /// <summary>Hash the message gets (and its stamp must be generated against); available after <see cref="Pack"/> or computable early via <see cref="ComputeHash"/>.</summary>
    public byte[] ComputeHash()
    {
        if (Hash is not null) return Hash;
        if (Timestamp == 0) Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        var packedPayload = MsgPack.Pack(new object?[] { Timestamp, TitleBytes, ContentBytes, Fields });
        return Identity.FullHash(Concat(DestinationHash, SourceHash, packedPayload));
    }

    /// <summary>Parses <paramref name="lxmfBytes"/> (upstream <c>unpack_from_bytes</c>). <paramref name="recallSource"/> maps a source destination hash to its announced identity.</summary>
    public static LxMessage Unpack(ReadOnlySpan<byte> lxmfBytes, Func<byte[], Identity?> recallSource, LxmMethod method = LxmMethod.Unknown)
    {
        ArgumentNullException.ThrowIfNull(recallSource);
        if (lxmfBytes.Length < Overhead - TimestampSize - StructOverhead) throw new FormatException("LXMF data is too short.");
        var destinationHash = lxmfBytes[..DestinationLength].ToArray();
        var sourceHash = lxmfBytes[DestinationLength..(2 * DestinationLength)].ToArray();
        var signature = lxmfBytes[(2 * DestinationLength)..((2 * DestinationLength) + SignatureLength)].ToArray();
        var packedPayload = lxmfBytes[((2 * DestinationLength) + SignatureLength)..].ToArray();
        if (MsgPack.Unpack(packedPayload) is not object?[] payload || payload.Length < 4) throw new FormatException("LXMF payload is not a list of at least 4 elements.");

        byte[]? stamp = null;
        if (payload.Length > 4)
        {
            stamp = payload[4] as byte[];
            packedPayload = MsgPack.Pack(payload[..4]);
        }

        var hashedPart = Concat(destinationHash, sourceHash, packedPayload);
        var hash = Identity.FullHash(hashedPart);
        var message = new LxMessage(destinationHash, sourceHash)
        {
            Incoming = true,
            Method = method,
            DesiredMethod = method,
            Timestamp = MsgPack.ToDouble(payload[0]),
            TitleBytes = AsBytes(payload[1]),
            ContentBytes = AsBytes(payload[2]),
            Fields = payload[3] as Dictionary<object, object?> ?? [],
            Stamp = stamp,
            Hash = hash,
            Signature = signature,
            Packed = lxmfBytes.ToArray(),
        };

        var sourceIdentity = recallSource(sourceHash);
        if (sourceIdentity is null)
        {
            message.UnverifiedReason = LxmUnverifiedReason.SourceUnknown;
        }
        else if (sourceIdentity.Verify(signature, Concat(hashedPart, hash)))
        {
            message.SignatureValidated = true;
        }
        else
        {
            message.UnverifiedReason = LxmUnverifiedReason.SignatureInvalid;
        }

        return message;
    }

    /// <summary>Re-evaluates the signature once the source identity becomes known (upstream leaves this to the application).</summary>
    public bool Validate(Identity sourceIdentity)
    {
        if (Hash is null || Signature is null || Packed is null) return false;
        var packedPayload = Packed[((2 * DestinationLength) + SignatureLength)..];
        if (Stamp is not null && MsgPack.Unpack(packedPayload) is object?[] payload && payload.Length > 4) packedPayload = MsgPack.Pack(payload[..4]);
        var hashedPart = Concat(DestinationHash, SourceHash, packedPayload);
        SignatureValidated = sourceIdentity.Verify(Signature, Concat(hashedPart, Hash));
        UnverifiedReason = SignatureValidated ? LxmUnverifiedReason.None : LxmUnverifiedReason.SignatureInvalid;
        return SignatureValidated;
    }

    public override string ToString() => $"<LXMessage {(Hash is null ? "unpacked" : Convert.ToHexStringLower(Hash))}>";

    private static byte[] AsBytes(object? value) => value switch
    {
        byte[] b => b,
        string s => System.Text.Encoding.UTF8.GetBytes(s),
        null => [],
        _ => throw new FormatException("LXMF title/content must be binary."),
    };

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }
}
