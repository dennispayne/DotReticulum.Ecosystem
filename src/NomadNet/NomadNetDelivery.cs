using DotReticulum.Core;
using LXMF;

namespace NomadNet;

public sealed class NomadNetDelivery(LxmRouter router, Identity identity, NomadNetStore store,
    Func<bool> allowFallback) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<(LxMessage Message, Task Work)> _pending = [];
    private bool _disposed;
    public Action<string>? Log { get; set; }

    public static LxMessage CreateFallback(LxMessage original, Identity identity)
    {
        var fallback = LxMessage.CreateBinary(original.DestinationHash, identity, original.ContentBytes,
            original.TitleBytes, new Dictionary<object, object?>(original.Fields),
            LxmMethod.Propagated, original.Timestamp);
        if (!fallback.ComputeHash().AsSpan().SequenceEqual(original.ComputeHash()))
            throw new InvalidOperationException("Fallback must retain the original LXMF message hash.");
        return fallback;
    }

    public async Task QueueAsync(LxMessage message, NomadNetMessage stored,
        CancellationToken cancellationToken = default)
    {
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pending.RemoveAll(item => item.Work.IsCompleted);
            if (_pending.Count >= 256) throw new InvalidOperationException("Application outbound queue is full.");
            _pending.Add((message, DeliverAsync(message, stored, queued, cancellationToken)));
        }
        await queued.Task.ConfigureAwait(false);
    }

    private async Task DeliverAsync(LxMessage message, NomadNetMessage stored, TaskCompletionSource queued,
        CancellationToken cancellationToken)
    {
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var active = message;
        try
        {
            await store.SaveMessageAsync(stored, preparation.Token).ConfigureAwait(false);
            await router.SendAsync(message, preparation.Token).ConfigureAwait(false);
            await store.UpdateMessageStatusAsync(stored.Id, message.State.ToString().ToLowerInvariant(), _lifetime.Token)
                .ConfigureAwait(false);
            queued.TrySetResult();
            var state = await message.Completion.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            if (NomadNetPropagation.ShouldFallback(state, message.Method, allowFallback(),
                    router.OutboundPropagationNode is not null))
            {
                await store.UpdateMessageStatusAsync(stored.Id, "retrying-propagated", _lifetime.Token).ConfigureAwait(false);
                Report($"Direct delivery failed; retrying {stored.Id} through propagation.");
                var fallback = CreateFallback(message, identity);
                active = fallback;
                await router.SendAsync(fallback, _lifetime.Token).ConfigureAwait(false);
                state = await fallback.Completion.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            }
            await store.UpdateMessageStatusAsync(stored.Id, state.ToString().ToLowerInvariant(), _lifetime.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            if (active.Hash is { } hash) router.CancelOutbound(hash);
            queued.TrySetCanceled(ex.CancellationToken);
            var status = active.State == LxmState.Delivered || active.State == LxmState.Sent && active.Method == LxmMethod.Propagated
                ? active.State.ToString().ToLowerInvariant() : "cancelled";
            await SaveFailureAsync(stored.Id, status).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            queued.TrySetException(ex);
            Report($"Message {stored.Id}: {ex.Message}");
            await SaveFailureAsync(stored.Id, "failed").ConfigureAwait(false);
        }
    }

    private async Task SaveFailureAsync(string id, string status)
    {
        try { await store.UpdateMessageStatusAsync(id, status).ConfigureAwait(false); }
        catch (Exception ex) { Report($"Could not persist {id} status: {ex.Message}"); }
    }

    private void Report(string message)
    {
        if (Log is not { } callbacks) return;
        foreach (Action<string> observer in callbacks.GetInvocationList())
            try { observer(message); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError($"NomadNet delivery observer: {ex}"); }
    }

    public async ValueTask DisposeAsync()
    {
        (LxMessage Message, Task Work)[] pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            pending = _pending.ToArray();
        }
        foreach (var item in pending)
            if (item.Message.Hash is { } hash) router.CancelOutbound(hash);
        await Task.WhenAll(pending.Select(item => item.Work)).ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
