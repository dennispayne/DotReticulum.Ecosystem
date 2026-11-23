using LXMF;

namespace Sideband;

public sealed partial class SidebandClient
{
    public const int MaximumTrackedOutgoingMessages = 257;
    public const int MaximumPendingMessageCancellations = 8;
    private readonly object _outgoingGate = new();
    private readonly Dictionary<Guid, ActiveOutgoingMessage> _activeOutgoingMessages = [];
    private readonly SemaphoreSlim _messageCancellationAdmission = new(MaximumPendingMessageCancellations, MaximumPendingMessageCancellations);

    public event Action<Guid>? MessageCancellationAvailabilityChanged;

    public bool CanRequestMessageCancellation(string deliveryHash, Guid id)
    {
        ActiveOutgoingMessage? active;
        lock (_outgoingGate)
            _activeOutgoingMessages.TryGetValue(id, out active);
        return active is not null && active.Peer == deliveryHash && ReferenceEquals(active.Router, _router) &&
            active.Router.CanCancelPending(active.Message);
    }

    public async Task RequestMessageCancellationAsync(string deliveryHash, Guid id, CancellationToken cancellationToken = default)
    {
        if (!SidebandContact.TryNormalizeDeliveryHash(deliveryHash, out var normalized))
            throw new ArgumentException("An LXMF delivery address must be exactly 32 hexadecimal characters.", nameof(deliveryHash));
        if (id == Guid.Empty) throw new ArgumentException("A saved message ID is required.", nameof(id));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_messageCancellationAdmission.Wait(0))
            throw new InvalidOperationException("The pending message cancellation limit has been reached.");
        try
        {
            using var owned = RuntimeOperationCancellation(cancellationToken);
            var state = await LoadAsync(owned.Token).ConfigureAwait(false);
            var selected = state.Messages.FirstOrDefault(message => message.Id == id && message.PeerDeliveryHash == normalized);
            if (selected is null || selected.Incoming || selected.DeliveryMethod == "Paper" || selected.PaperUri is not null)
                throw new InvalidOperationException("Select an active outgoing non-paper message in this runtime.");
            ActiveOutgoingMessage? active;
            lock (_outgoingGate) _activeOutgoingMessages.TryGetValue(id, out active);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            owned.Token.ThrowIfCancellationRequested();
            if (active is null || active.Peer != normalized || !ReferenceEquals(active.Router, _router) ||
                OutgoingMessageIsTerminal(active.Message) || !active.Router.TryCancelPending(active.Message))
                throw new InvalidOperationException("The message is no longer pending in this runtime; cancellation was not requested.");
        }
        finally { _messageCancellationAdmission.Release(); }
    }

    private void TrackOutgoingMessage(Guid id, string peer, LxmRouter router, LxMessage message)
    {
        lock (_outgoingGate)
        {
            if (_activeOutgoingMessages.Count >= MaximumTrackedOutgoingMessages)
                throw new InvalidOperationException("The active outgoing message tracking limit has been reached.");
            _activeOutgoingMessages.Add(id, new(peer, router, message));
        }
    }

    private void UntrackOutgoingMessage(Guid id, LxMessage message)
    {
        lock (_outgoingGate)
            if (_activeOutgoingMessages.TryGetValue(id, out var active) && ReferenceEquals(active.Message, message))
                _activeOutgoingMessages.Remove(id);
    }

    private void ClearOutgoingMessages()
    {
        lock (_outgoingGate) _activeOutgoingMessages.Clear();
    }

    private void PublishMessageCancellationAvailability(Guid id)
    {
        if (MessageCancellationAvailabilityChanged is not { } callbacks) return;
        foreach (Action<Guid> callback in callbacks.GetInvocationList())
        {
            try { callback(id); }
            catch (Exception ex) { Report("A Sideband message cancellation observer failed: " + ex.Message); }
        }
    }

    private static bool OutgoingMessageIsTerminal(LxMessage message) =>
        message.State is LxmState.Paper or LxmState.Delivered or LxmState.Failed or LxmState.Rejected or LxmState.Cancelled ||
        message.State == LxmState.Sent && message.Method == LxmMethod.Propagated;

    private sealed record ActiveOutgoingMessage(string Peer, LxmRouter Router, LxMessage Message);
}
