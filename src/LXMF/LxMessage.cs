using DotReticulum.Core;
using DotReticulum.Transport;
using System.Security.Cryptography;

namespace LXMF;

public enum LxmState : byte
{
    Generating = 0x00,
    Outbound = 0x01,
    Sending = 0x02,
    Sent = 0x04,
    Paper = 0x05,
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
    public const long RnrReferences = 0x0E;
    public const long Renderer = 0x0F;
    public const long ReplyTo = 0x30;
    public const long ReplyQuote = 0x31;
    public const long Reaction = 0x40;
    public const long Comment = 0x41;
    public const long Continuation = 0x42;
    public const long CustomType = 0xFB;
    public const long CustomData = 0xFC;
    public const long CustomMeta = 0xFD;
    public const long NonSpecific = 0xFE;
    public const long Debug = 0xFF;
}

/// <summary>
/// An LXMF message (upstream <c>LXMessage</c>). Wire form: <c>destination(16) || source(16) || signature(64) || msgpack([timestamp, title, content, fields(, stamp)])</c>.
/// The hash covers destination, source and the payload without the stamp; the signature covers the hashed part followed by the hash.
/// </summary>
public sealed partial class LxMessage
{
    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(false, true);
    public const int DestinationLength = 16;
    public const int SignatureLength = 64;
    public const int TimestampSize = 8;
    public const int StructOverhead = 8;
    public const int Overhead = (2 * DestinationLength) + SignatureLength + TimestampSize + StructOverhead;
    public const int TicketLength = 16;
    public const int TicketStampValue = 0x100;
    public const int PaperMdu = ((2953 - 6) * 6) / 8;

    /// <summary>Upstream <c>Packet.ENCRYPTED_MDU</c> for the default 500 byte MTU.</summary>
    public const int EncryptedPacketMdu = 383;

    public const int EncryptedPacketMaxContent = EncryptedPacketMdu - Overhead + DestinationLength;
    public const int PlainPacketMaxContent = 368;

    /// <summary>Largest packed content that still fits a single link packet at the default MTU.</summary>
    public static readonly int LinkPacketMaxContent = Link.ComputeMdu(Packet.Mtu) - Overhead;

    private readonly object _gate = new();
    private readonly TaskCompletionSource<LxmState> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private LxmState _state = LxmState.Generating;
    private double _nextAttempt;
    private bool _timestampSet;
    private DestinationEndpoint? _destination;

    private LxMessage(byte[] destinationHash, byte[] sourceHash)
    {
        DestinationHash = destinationHash;
        SourceHash = sourceHash;
    }

    public byte[] DestinationHash { get; private set; }

    public byte[] SourceHash { get; private set; }

    public DestinationEndpoint? Destination => _destination;

    public Identity? SourceIdentity => _source;

    public DestinationType DestinationType => _destination?.Destination.Type ?? DestinationType.Single;

    /// <summary>Seconds since the Unix epoch, as upstream's <c>time.time()</c>.</summary>
    public double Timestamp { get; private set; }

    public byte[] TitleBytes { get; private set; } = [];

    public byte[] ContentBytes { get; private set; } = [];

    public string Title => StrictUtf8.GetString(TitleBytes);

    public string? Content
    {
        get
        {
            try { return StrictUtf8.GetString(ContentBytes); }
            catch (System.Text.DecoderFallbackException)
            {
                if (Log is { } callbacks)
                    foreach (Action<string> callback in callbacks.GetInvocationList())
                        try { callback("Could not decode LXMF content as UTF-8."); }
                        catch (Exception ex) { System.Diagnostics.Trace.TraceError("LXMF log observer failed: {0}", ex.Message); }
                return null;
            }
        }
    }

    private object _fieldsValue = new Dictionary<object, object?>();

    /// <summary>Conventional field dictionary. For maps containing nil, tuple or nonfinite keys, use <see cref="FieldsValue"/>.</summary>
    public Dictionary<object, object?> Fields
    {
        get => _fieldsValue as Dictionary<object, object?>
            ?? throw new InvalidOperationException("The field map contains extended keys; use FieldsValue.");
        private set => _fieldsValue = value;
    }

    /// <summary>The complete Dictionary or MsgPackMap, including extended keys and nested maps.</summary>
    public object FieldsValue => _fieldsValue;

    public void SetFieldMap(MsgPackMap fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        EnsureUnpacked();
        _fieldsValue = fields;
    }

    public bool TryGetField(long code, out object? value)
    {
        foreach (var (key, entry) in FieldEntries())
            if (IsFieldKey(key, code)) { value = entry; return true; }
        value = null;
        return false;
    }

    public void SetField(long code, object? value)
    {
        EnsureUnpacked();
        if (_fieldsValue is Dictionary<object, object?> dictionary)
        {
            var existing = dictionary.Keys.FirstOrDefault(key => IsFieldKey(key, code));
            dictionary[existing ?? code] = value;
        }
        else
        {
            var found = false;
            var entries = FieldEntries().Select(entry =>
            {
                if (!IsFieldKey(entry.Key, code)) return entry;
                found = true;
                return new KeyValuePair<object?, object?>(entry.Key, value);
            }).ToList();
            if (!found) entries.Add(new(code, value));
            _fieldsValue = new MsgPackMap(entries.ToArray());
        }
    }

    private IEnumerable<KeyValuePair<object?, object?>> FieldEntries() => _fieldsValue is MsgPackMap map ? map :
        ((Dictionary<object, object?>)_fieldsValue).Select(entry => new KeyValuePair<object?, object?>(entry.Key, entry.Value));

    private static bool IsFieldKey(object? key, long code) => key switch
    {
        sbyte value => value == code,
        byte value => value == code,
        short value => value == code,
        ushort value => value == code,
        int value => value == code,
        uint value => value == code,
        long value => value == code,
        ulong value => code >= 0 && value == (ulong)code,
        float value => IsFloatingFieldKey(value, code),
        double value => IsFloatingFieldKey(value, code),
        bool value => (value ? 1 : 0) == code,
        _ => false,
    };

    private static bool IsFloatingFieldKey(double value, long code) =>
        value >= long.MinValue && value < 9223372036854775808d &&
        value == Math.Truncate(value) && (long)value == code;

    public byte[]? Stamp { get; private set; }
    public int? StampCost { get; internal set; }
    public int? MaxDeliveryAttempts { get; internal set; }
    public bool AwaitingPath { get; internal set; }
    public bool SentOnPath { get; internal set; }
    public bool SentOnSlowPath { get; internal set; }
    public TimeSpan? ReceiptTimeout { get; internal set; }
    public double? NextDeliveryAttempt => NextAttempt == 0 ? null : NextAttempt;
    public int? StampValue { get; internal set; }
    public bool StampChecked { get; internal set; }
    public bool StampValid { get; internal set; }
    public int? PropagationTargetCost { get; internal set; }

    public byte[]? Hash { get; private set; }

    public byte[]? Signature { get; private set; }

    public bool SignatureValidated { get; private set; }

    public LxmUnverifiedReason UnverifiedReason { get; private set; }

    public byte[]? Packed { get; private set; }

    public byte[]? PaperPacked { get; private set; }

    public byte[]? PropagationData { get; private set; }

    public byte[]? PropagationPacked { get; private set; }

    public byte[]? TransientId { get; private set; }

    internal byte[]? PropagationNodeHash { get; set; }

    public bool? TransportEncrypted { get; internal set; }

    public string? TransportEncryption { get; internal set; }
    public bool SourceBlackholed { get; private set; }
    private byte[]? _ratchetId;
    public byte[]? RatchetId { get => _ratchetId?.ToArray(); internal set => _ratchetId = value?.ToArray(); }
    public double? Rssi { get; internal set; }
    public double? Snr { get; internal set; }
    public double? Quality { get; internal set; }

    internal void ApplyTransportMetadata(DestinationType type, PacketPhysicalLayer physicalLayer = default,
        byte[]? ratchetId = null)
    {
        TransportEncrypted = type is DestinationType.Single or DestinationType.Group or DestinationType.Link;
        TransportEncryption = type switch
        {
            DestinationType.Single or DestinationType.Link => "Curve25519",
            DestinationType.Group => "AES-256",
            _ => null,
        };
        Rssi = physicalLayer.Rssi;
        Snr = physicalLayer.Snr;
        Quality = physicalLayer.Quality;
        if (_ratchetId is null && ratchetId is not null) RatchetId = ratchetId;
    }

    public bool Incoming { get; private init; }

    /// <summary>Ask the router to include a reply ticket in the signed fields.</summary>
    public bool IncludeTicket { get; set; }

    public LxmMethod DesiredMethod { get; set; } = LxmMethod.Direct;

    /// <summary>The delivery method chosen by <see cref="Pack"/> (opportunistic content that does not fit falls back to direct) or, for received messages, the method it arrived by.</summary>
    public LxmMethod Method { get; internal set; }

    /// <summary>True when the packed message is sent as a Resource rather than a single packet (direct delivery only).</summary>
    public bool IsResource { get; private set; }

    public int DeliveryAttempts { get; internal set; }

    internal Resource? ActiveResource { get; set; }

    public double Progress => State == LxmState.Delivered ? 1 : ActiveResource?.Progress ?? 0;

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
                if (_state is LxmState.Delivered or LxmState.Paper or LxmState.Failed or LxmState.Rejected or LxmState.Cancelled) return;
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
                        if (Log is { } logs)
                            foreach (Action<string> log in logs.GetInvocationList())
                            {
                                try { log(error); }
                                catch { }
                            }
                        else System.Diagnostics.Trace.TraceError(error);
                    }
                }
            }
            if (value is LxmState.Delivered or LxmState.Paper or LxmState.Failed or LxmState.Rejected or LxmState.Cancelled ||
                value == LxmState.Sent && Method == LxmMethod.Propagated) _done.TrySetResult(value);
        }
    }

    /// <summary>Raised (on a router thread) whenever <see cref="State"/> changes.</summary>
    public event Action<LxMessage>? StateChanged;

    public Action<string>? Log { get; set; }

    /// <summary>Completes once the message reaches a final state (delivered, failed, rejected or cancelled).</summary>
    public Task<LxmState> Completion => _done.Task;

    /// <summary>Creates an outbound message to <paramref name="destinationHash"/> (an <c>lxmf.delivery</c> hash) from <paramref name="source"/>.</summary>
    public static LxMessage Create(byte[] destinationHash, Identity source, string? content, string? title = "", Dictionary<object, object?>? fields = null,
        LxmMethod method = LxmMethod.Direct, double? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(destinationHash);
        ArgumentNullException.ThrowIfNull(source);
        if (destinationHash.Length != DestinationLength) throw new ArgumentException("Destination hash must be 16 bytes.", nameof(destinationHash));
        var message = new LxMessage(destinationHash, DeliveryDestinationHash(source))
        {
            TitleBytes = StrictUtf8.GetBytes(title ?? ""),
            ContentBytes = StrictUtf8.GetBytes(content ?? ""),
            Fields = fields ?? [],
            DesiredMethod = method,
            Timestamp = timestamp ?? 0,
            _timestampSet = timestamp.HasValue,
        };
        message._source = source;
        return message;
    }

    public static LxMessage Create(DestinationEndpoint destination, Identity source, string? content, string? title = "",
        Dictionary<object, object?>? fields = null, LxmMethod method = LxmMethod.Direct, double? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var message = Create(destination.Destination.Hash, source, content, title, fields, method, timestamp);
        message.SetDestination(destination);
        return message;
    }

    public static LxMessage CreateUnassigned(byte[]? destinationHash = null, byte[]? sourceHash = null,
        string? content = "", string? title = "", Dictionary<object, object?>? fields = null,
        LxmMethod method = LxmMethod.Direct, double? timestamp = null)
    {
        if (destinationHash is { Length: not DestinationLength } || sourceHash is { Length: not DestinationLength })
            throw new ArgumentException("Message destination and source hashes must be 16 bytes.");
        return new LxMessage(destinationHash?.ToArray() ?? [], sourceHash?.ToArray() ?? [])
        {
            ContentBytes = StrictUtf8.GetBytes(content ?? ""),
            TitleBytes = StrictUtf8.GetBytes(title ?? ""),
            Fields = fields ?? [],
            DesiredMethod = method,
            Timestamp = timestamp ?? 0,
            _timestampSet = timestamp.HasValue,
        };
    }

    public void SetDestination(DestinationEndpoint destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (_destination is not null) throw new InvalidOperationException("Cannot reassign message destination.");
        if (Packed is not null) throw new InvalidOperationException("Cannot assign the destination of a packed message.");
        _destination = destination;
        DestinationHash = destination.Destination.Hash.ToArray();
    }

    public void SetSource(Identity source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_source is not null) throw new InvalidOperationException("Cannot reassign message source.");
        if (Packed is not null) throw new InvalidOperationException("Cannot assign the source of a packed message.");
        _source = source;
        SourceHash = DeliveryDestinationHash(source);
    }

    public void SetTitle(string title) => SetTitle(StrictUtf8.GetBytes(title));
    public void SetContent(string content) => SetContent(StrictUtf8.GetBytes(content));
    public void SetTitle(ReadOnlySpan<byte> title) { EnsureUnpacked(); TitleBytes = title.ToArray(); }
    public void SetContent(ReadOnlySpan<byte> content) { EnsureUnpacked(); ContentBytes = content.ToArray(); }
    public void SetFields(Dictionary<object, object?>? fields) { EnsureUnpacked(); Fields = fields ?? []; }
    private void EnsureUnpacked()
    {
        if (Packed is not null) throw new InvalidOperationException("Cannot mutate a packed message.");
    }

    /// <summary>Creates a message without UTF-8 conversion of the title or content.</summary>
    public static LxMessage CreateBinary(byte[] destinationHash, Identity source, ReadOnlySpan<byte> content, ReadOnlySpan<byte> title = default,
        Dictionary<object, object?>? fields = null, LxmMethod method = LxmMethod.Direct, double? timestamp = null)
    {
        var message = Create(destinationHash, source, "", fields: fields, method: method, timestamp: timestamp);
        message.ContentBytes = content.ToArray();
        message.TitleBytes = title.ToArray();
        return message;
    }

    private Identity? _source;

    public static byte[] DeliveryDestinationHash(Identity identity) =>
        DotReticulum.Core.Destination.ComputeHash(identity.Hash, LxmRouter.AppName, [LxmRouter.DeliveryAspect]);

    /// <summary>
    /// Signs and packs the message (upstream <c>pack</c>). <paramref name="stamp"/>, when given, is appended to the payload after hashing and does not
    /// change the hash; <see cref="LxStamper"/> can generate one.
    /// </summary>
    public byte[] Pack(byte[]? stamp = null, Identity? destinationIdentity = null, byte[]? ratchet = null)
    {
        if (Packed is not null) throw new InvalidOperationException("The message was already packed.");
        destinationIdentity ??= _destination?.Identity;
        if (_destination is null && DesiredMethod is LxmMethod.Paper or LxmMethod.Propagated &&
            (destinationIdentity is null || !DeliveryDestinationHash(destinationIdentity).AsSpan().SequenceEqual(DestinationHash)))
            throw new ArgumentException("Paper and propagated messages require the matching destination identity.", nameof(destinationIdentity));
        var source = _source ?? throw new InvalidOperationException("Only locally created messages can be packed.");
        if (DestinationHash.Length != DestinationLength) throw new InvalidOperationException("Message destination is not assigned.");
        if (!_timestampSet) { Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0; _timestampSet = true; }
        if (DestinationType == DestinationType.Group && DesiredMethod == LxmMethod.Opportunistic)
            throw new NotSupportedException("The pinned upstream cannot pack opportunistic GROUP messages.");

        var payload = new object?[] { Timestamp, TitleBytes, ContentBytes, FieldsValue };
        var packedPayload = MsgPack.Pack(payload);
        var hashedPart = Concat(DestinationHash, SourceHash, packedPayload);
        Hash = Identity.FullHash(hashedPart);
        Signature = source.Sign(Concat(hashedPart, Hash));
        SignatureValidated = true;

        if (stamp is not null)
        {
            Stamp = stamp;
            packedPayload = MsgPack.Pack(new object?[] { Timestamp, TitleBytes, ContentBytes, FieldsValue, stamp });
        }

        Packed = Concat(DestinationHash, SourceHash, Signature, packedPayload);
        var contentSize = packedPayload.Length - TimestampSize - StructOverhead;
        Method = DesiredMethod;
        if (DesiredMethod == LxmMethod.Opportunistic)
        {
            if (DestinationType == DestinationType.Plain && contentSize > PlainPacketMaxContent)
                throw new ArgumentException("Plain opportunistic message exceeds the single-packet limit.");
            if (DestinationType == DestinationType.Single && contentSize > EncryptedPacketMaxContent) Method = LxmMethod.Direct;
        }
        if (Method == LxmMethod.Direct) IsResource = contentSize > LinkPacketMaxContent;
        if (Method == LxmMethod.Paper)
        {
            var paper = Concat(DestinationHash, EncryptForDestination(Packed.AsSpan(DestinationLength), destinationIdentity, ratchet));
            if (paper.Length > PaperMdu)
            {
                Packed = null;
                throw new ArgumentException("Message exceeds the paper message maximum size.");
            }

            PaperPacked = paper;
        }
        else if (Method == LxmMethod.Propagated)
        {
            PropagationData = Concat(DestinationHash, EncryptForDestination(Packed.AsSpan(DestinationLength), destinationIdentity, ratchet));
            TransientId = Identity.FullHash(PropagationData);
            PropagationPacked = MsgPack.Pack(new object?[] { DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0, new object?[] { PropagationData } });
            IsResource = PropagationPacked.Length > LinkPacketMaxContent;
        }
        if (Method is LxmMethod.Paper or LxmMethod.Propagated && ratchet is { Length: > 0 })
            RatchetId = DestinationRatchets.GetRatchetId(ratchet);
        DetermineTransportEncryption();
        return Packed;
    }

    public void DetermineTransportEncryption()
    {
        TransportEncrypted = Method == LxmMethod.Direct || DestinationType != DestinationType.Plain;
        TransportEncryption = Method == LxmMethod.Direct || DestinationType == DestinationType.Single ? "Curve25519" :
            DestinationType == DestinationType.Group ? "AES-256" : "Unencrypted";
    }

    private byte[] EncryptForDestination(ReadOnlySpan<byte> data, Identity? identity, byte[]? ratchet) =>
        _destination is { } endpoint ? endpoint.Encrypt(data, ratchet ?? []) : identity!.Encrypt(data, ratchet);

    public void SetPropagationStamp(ReadOnlySpan<byte> stamp, double? timestamp = null)
    {
        if (PropagationData is null) throw new InvalidOperationException("Message is not packed for propagation.");
        if (stamp.Length != LxStamper.StampSize) throw new ArgumentException("Propagation stamp must be 32 bytes.", nameof(stamp));
        PropagationPacked = MsgPack.Pack(new object?[]
        {
            timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            new object?[] { Concat(PropagationData, stamp.ToArray()) },
        });
        IsResource = PropagationPacked.Length > LinkPacketMaxContent;
    }

    /// <summary>Encodes an already packed paper message as upstream's unpadded URL-safe <c>lxm://</c> URI.</summary>
    public string AsUri(bool finalise = true)
    {
        if (Method != LxmMethod.Paper || PaperPacked is null) throw new InvalidOperationException("Only packed paper messages have a URI.");
        var uri = "lxm://" + Convert.ToBase64String(PaperPacked).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (finalise) State = LxmState.Paper;
        return uri;
    }

    /// <summary>Decrypts a paper URI for this identity. Ratcheted messages require the caller's ratchet-aware decryptor.</summary>
    public static LxMessage FromPaperUri(string uri, Identity destination, Func<byte[], Identity?> recallSource,
        Func<byte[], byte[]?>? decrypt = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var paper = DecodePaperUri(uri);
        if (!paper.AsSpan(0, DestinationLength).SequenceEqual(DeliveryDestinationHash(destination)))
            throw new FormatException("Paper message is not addressed to this destination.");
        var encrypted = paper[DestinationLength..];
        var plain = decrypt is null ? destination.Decrypt(encrypted) : decrypt(encrypted);
        if (plain is null) throw new FormatException("Paper message could not be decrypted.");
        var message = Unpack(Concat(paper[..DestinationLength], plain), recallSource, LxmMethod.Paper);
        message.PaperPacked = paper;
        return message;
    }

    public static LxMessage FromPaperUri(string uri, DestinationEndpoint destination, Func<byte[], Identity?> recallSource)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var paper = DecodePaperUri(uri);
        if (!paper.AsSpan(0, DestinationLength).SequenceEqual(destination.Destination.Hash))
            throw new FormatException("Paper message is not addressed to this destination.");
        var plain = destination.Decrypt(paper.AsSpan(DestinationLength))
            ?? throw new FormatException("Paper message could not be decrypted.");
        var message = Unpack(Concat(paper[..DestinationLength], plain), recallSource, LxmMethod.Paper);
        message._destination = destination;
        message.PaperPacked = paper;
        message.DetermineTransportEncryption();
        return message;
    }

    private static byte[] DecodePaperUri(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.StartsWith("lxm://", StringComparison.OrdinalIgnoreCase)) throw new FormatException("Invalid LXMF URI scheme.");
        var encoded = uri[6..].Replace('-', '+').Replace('_', '/');
        if (encoded.Length > ((PaperMdu + 2) / 3) * 4) throw new FormatException("Paper message exceeds the maximum size.");
        byte[] paper;
        try { paper = Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '=')); }
        catch (FormatException) { throw new FormatException("Invalid LXMF URI encoding."); }
        if (paper.Length <= DestinationLength || paper.Length > PaperMdu) throw new FormatException("Invalid paper message size.");
        return paper;
    }

    /// <summary>Upstream ticket stamp: truncated SHA-256(ticket || message-id).</summary>
    public byte[] CreateTicketStamp(ReadOnlySpan<byte> ticket)
    {
        if (ticket.Length != TicketLength) throw new ArgumentException("Ticket must be 16 bytes.", nameof(ticket));
        StampValue = TicketStampValue;
        return TicketStamp(ticket, ComputeHash());
    }

    private static byte[] TicketStamp(ReadOnlySpan<byte> ticket, ReadOnlySpan<byte> hash)
    {
        Span<byte> input = stackalloc byte[TicketLength + 32];
        ticket.CopyTo(input);
        hash.CopyTo(input[TicketLength..]);
        try { return Identity.FullHash(input)[..TicketLength]; }
        finally { CryptographicOperations.ZeroMemory(input); }
    }

    /// <summary>Validates an inbound ticket before falling back to proof of work, as upstream <c>validate_stamp</c>.</summary>
    public bool ValidateStamp(int targetCost, IEnumerable<byte[]>? tickets = null)
    {
        if (Stamp is null || Hash is null) return false;
        if (tickets is not null)
        {
            foreach (var ticket in tickets)
            {
                if (ticket.Length == TicketLength && CryptographicOperations.FixedTimeEquals(Stamp, TicketStamp(ticket, Hash)))
                {
                    StampValue = TicketStampValue;
                    return true;
                }
            }
        }

        var workblock = LxStamper.Workblock(Hash);
        if (!LxStamper.IsValid(Stamp, targetCost, workblock)) return false;
        StampValue = LxStamper.Value(workblock, Stamp);
        return true;
    }

    /// <summary>Upstream message file container, distinct from the signed wire message.</summary>
    public byte[] PackedContainer()
    {
        if (Packed is null) Pack();
        return MsgPack.Pack(new Dictionary<object, object?>
        {
            ["state"] = (long)State,
            ["lxmf_bytes"] = Packed,
            ["transport_encrypted"] = TransportEncrypted,
            ["transport_encryption"] = TransportEncryption,
            ["method"] = (long)Method,
        });
    }

    public static LxMessage UnpackContainer(ReadOnlySpan<byte> container, Func<byte[], Identity?> recallSource)
    {
        if (MsgPack.UnpackApplicationValue(container) is not Dictionary<object, object?> map ||
            !map.TryGetValue("lxmf_bytes", out var value) || value is not byte[] wire)
            throw new FormatException("Invalid LXMF message container.");
        var method = map.TryGetValue("method", out var methodValue) ? (LxmMethod)checked((byte)MsgPack.ToInt64(methodValue)) : LxmMethod.Unknown;
        var message = Unpack(wire, recallSource, method);
        if (map.TryGetValue("transport_encrypted", out var encrypted))
        {
            if (encrypted is not null and not bool) throw new FormatException("Invalid transport encryption flag.");
            message.TransportEncrypted = (bool?)encrypted;
        }

        if (map.TryGetValue("transport_encryption", out var encryption))
        {
            if (encryption is not null and not string) throw new FormatException("Invalid transport encryption description.");
            message.TransportEncryption = (string?)encryption;
        }

        if (map.TryGetValue("state", out var state)) message.State = (LxmState)checked((byte)MsgPack.ToInt64(state));
        return message;
    }

    /// <summary>Hash the message gets (and its stamp must be generated against); available after <see cref="Pack"/> or computable early via <see cref="ComputeHash"/>.</summary>
    public byte[] ComputeHash()
    {
        if (Hash is not null) return Hash;
        if (!_timestampSet) { Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0; _timestampSet = true; }
        var packedPayload = MsgPack.Pack(new object?[] { Timestamp, TitleBytes, ContentBytes, FieldsValue });
        return Identity.FullHash(Concat(DestinationHash, SourceHash, packedPayload));
    }

    /// <summary>Parses <paramref name="lxmfBytes"/> (upstream <c>unpack_from_bytes</c>). <paramref name="recallSource"/> maps a source destination hash to its announced identity.</summary>
    public static LxMessage Unpack(ReadOnlySpan<byte> lxmfBytes, Func<byte[], Identity?> recallSource, LxmMethod method = LxmMethod.Unknown,
        Func<Identity, bool>? sourceIsBlackholed = null)
    {
        ArgumentNullException.ThrowIfNull(recallSource);
        if (lxmfBytes.Length < Overhead - TimestampSize - StructOverhead) throw new FormatException("LXMF data is too short.");
        var destinationHash = lxmfBytes[..DestinationLength].ToArray();
        var sourceHash = lxmfBytes[DestinationLength..(2 * DestinationLength)].ToArray();
        var signature = lxmfBytes[(2 * DestinationLength)..((2 * DestinationLength) + SignatureLength)].ToArray();
        var packedPayload = lxmfBytes[((2 * DestinationLength) + SignatureLength)..].ToArray();
        if (MsgPack.UnpackApplicationValue(packedPayload, allowExtendedMapKeys: true) is not object?[] payload || payload.Length < 4) throw new FormatException("LXMF payload is not a list of at least 4 elements.");

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
            _fieldsValue = payload[3] switch
            {
                null => new Dictionary<object, object?>(),
                Dictionary<object, object?> or MsgPackMap => payload[3]!,
                _ => throw new FormatException("LXMF fields must be a map."),
            },
            Stamp = stamp,
            Hash = hash,
            Signature = signature,
            Packed = lxmfBytes.ToArray(),
        };

        var sourceIdentity = recallSource(sourceHash);
        if (sourceIdentity is not null && sourceIsBlackholed is not null)
        {
            try { message.SourceBlackholed = sourceIsBlackholed(sourceIdentity); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError("Could not determine LXMF source blackhole status: {0}", ex.Message); }
        }
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
        if (Stamp is not null && MsgPack.UnpackApplicationValue(packedPayload, allowExtendedMapKeys: true) is object?[] payload && payload.Length > 4) packedPayload = MsgPack.Pack(payload[..4]);
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
