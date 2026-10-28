namespace LXST.Audio;

/// <summary>Forwards encoded audio frames to an already-started stream session; it does not own the session.</summary>
public sealed class StreamSessionAudioSink(StreamSession session) : IEncodedAudioSink
{
    private readonly StreamSession _session = session ?? throw new ArgumentNullException(nameof(session));

    public ValueTask WriteAsync(EncodedFrame frame, CancellationToken cancellationToken = default) =>
        _session.SendFrameAsync(frame, cancellationToken);
}
