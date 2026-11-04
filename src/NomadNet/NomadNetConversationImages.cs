namespace NomadNet;

/// <summary>Latest-view, serial local conversation previews with joined cancellation.</summary>
public sealed class NomadNetConversationImages(
    Func<NomadNetMessage, CancellationToken, Task<string>> preview) : IAsyncDisposable
{
    public const int MaximumImages = 32;
    private readonly SemaphoreSlim _control = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<string, string?> _previews = new(StringComparer.Ordinal);
    private CancellationTokenSource? _workStop;
    private Task _work = Task.CompletedTask;
    private bool _disposed;
    public event Action<NomadNetMessage, string>? Changed;
    public Action<string>? Log { get; set; }
    public Task Completion { get { lock (_gate) return _work; } }
    public IReadOnlyDictionary<string, string> Previews
    {
        get { lock (_gate) return _previews.Where(item => item.Value is not null).ToDictionary(item => item.Key, item => item.Value!); }
    }

    public static async Task<string> PreviewAsync(string directory, NomadNetMessage message,
        NomadNetImageCache cache, NomadNetImages images, CancellationToken cancellationToken = default)
    {
        var source = await NomadNetAttachments.ReadAsync(directory, message.Id,
            message.ImageAttachmentIndex ?? throw new InvalidDataException("Message has no image attachment."),
            cancellationToken).ConfigureAwait(false);
        var encoded = await cache.ConvertAsync(source, cancellationToken).ConfigureAwait(false);
        return await images.PreviewAsync(encoded, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task ShowAsync(IReadOnlyList<NomadNetMessage> messages, NomadNetImageLoading mode,
        bool explicitLoad = false, bool reload = false, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (messages.Count > NomadNetStore.MaximumMessages) throw new InvalidDataException("Conversation view exceeds the message bound.");
        if (mode == NomadNetImageLoading.Never && explicitLoad)
            throw new InvalidOperationException("Conversation images are disabled; choose manual, auto or always first.");
        await _control.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var all = messages.Where(message => message.ImageAttachmentIndex is not null)
                .OrderByDescending(message => message.Timestamp).DistinctBy(message => message.Id)
                .Take(MaximumImages + 1).ToArray();
            var selected = all.Take(MaximumImages).ToArray();
            lock (_gate)
            {
                var ids = selected.Select(message => message.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var id in _previews.Keys.Where(id => reload || mode == NomadNetImageLoading.Never || !ids.Contains(id)).ToArray())
                    _previews.Remove(id);
            }
            if (all.Length > MaximumImages) Report($"Conversation previews are limited to {MaximumImages} images per view.");
            // These bytes are already local, unlike browser media that may require a network request.
            if (!explicitLoad && !NomadNetImagePolicy.ShouldLoad(mode, local: true, metrics: null)) return;
            _workStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var stop = _workStop.Token;
            lock (_gate) _work = Task.Run(() => ProcessAsync(selected, stop), CancellationToken.None);
        }
        finally { _control.Release(); }
    }

    private async Task ProcessAsync(IEnumerable<NomadNetMessage> messages, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var message in messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate) if (_previews.ContainsKey(message.Id)) continue;
                string? result = null;
                try
                {
                    result = await preview(message, cancellationToken).ConfigureAwait(false);
                    if (result.Length > 65536 || result.Any(character => char.IsControl(character) && character != '\n'))
                        throw new InvalidDataException("Conversation preview exceeds the text/control-character safety bound.");
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    result = null;
                    Report($"Could not load conversation image {message.Id}: {exception.Message}");
                }
                lock (_gate) _previews[message.Id] = result;
                if (result is null) continue;
                foreach (var observer in Changed?.GetInvocationList() ?? [])
                {
                    try { ((Action<NomadNetMessage, string>)observer)(message, result); }
                    catch (Exception exception) { Report($"Conversation image observer failed for {message.Id}: {exception.Message}"); }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void Report(string message)
    {
        var safe = NomadNetTerminalText.Safe(message[..Math.Min(message.Length, 4096)]);
        try
        {
            if (Log is { } log) { log(safe); return; }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("NomadNet conversation image log observer failed: {0}; original report: {1}",
                NomadNetTerminalText.Safe(exception.Message[..Math.Min(exception.Message.Length, 4096)]), safe);
            return;
        }
        System.Diagnostics.Trace.TraceError("NomadNet conversation image: {0}", safe);
    }

    private async Task StopAsync()
    {
        _workStop?.Cancel();
        await Completion.ConfigureAwait(false);
        _workStop?.Dispose();
        _workStop = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _control.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await StopAsync().ConfigureAwait(false);
            lock (_gate) _previews.Clear();
        }
        finally { _control.Release(); }
    }
}
