using System.Threading.Channels;

namespace NomadNet;

/// <summary>One browser's serial image decoder, latest-page queue and joined cancellation lifetime.</summary>
public sealed class NomadNetInlineMedia(
    Func<NomadNetPageMedia, bool, CancellationToken, Task<string>> preview) : IAsyncDisposable
{
    public const int MaximumImages = 32;
    private readonly object _gate = new();
    private readonly Channel<NomadNetRenderedPage> _queue = Channel.CreateBounded<NomadNetRenderedPage>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Dictionary<NomadNetPageMedia, string?> _previews = [];
    private readonly List<Task> _pending = [];
    private NomadNetRenderedPage? _page;
    private Task? _worker;
    private Task? _disposal;
    private bool _disposed;

    public event Action<NomadNetRenderedPage>? Changed;
    public Action<string>? Log { get; set; }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _worker ??= Task.Run(RunAsync);
        }
    }

    public void Show(NomadNetRenderedPage page, bool automatic)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker is null) throw new InvalidOperationException("Start inline media before showing a page.");
            _page = page;
            var retained = page.Media.Distinct().Take(MaximumImages).ToHashSet();
            foreach (var key in _previews.Keys.Where(key => !retained.Contains(key)).ToArray()) _previews.Remove(key);
            if (automatic) _queue.Writer.TryWrite(page);
        }
    }

    public string Render(NomadNetRenderedPage page)
    {
        Dictionary<NomadNetPageMedia, string> images;
        lock (_gate) images = _previews.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value!);
        return page.DisplayWithImages(images);
    }

    public Task LoadAsync(bool reload = false, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker is null || _page is null) throw new InvalidOperationException("Browse a page first.");
            _pending.RemoveAll(task => task.IsCompleted);
            if (_pending.Count >= 16) throw new InvalidOperationException("Too many pending inline image requests.");
            var page = _page;
            var task = Task.Run(() => ProcessAsync(page, reload, cancellationToken), CancellationToken.None);
            _pending.Add(task);
            return task;
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var page in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
                await ProcessAsync(page, false, _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task ProcessAsync(NomadNetRenderedPage page, bool reload, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        await _serial.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (page.Media.Distinct().Skip(MaximumImages).Any())
                Report($"Inline images are limited to {MaximumImages} distinct descriptors per page; remaining images stay downloadable.");
            foreach (var image in page.Media.Distinct().Take(MaximumImages))
            {
                linked.Token.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (!ReferenceEquals(_page, page)) return;
                    if (!reload && _previews.ContainsKey(image)) continue;
                }
                string? result = null;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(90));
                    result = await preview(image, reload, timeout.Token).ConfigureAwait(false);
                    if (result.Length > 65536 || result.Any(character => char.IsControl(character) && character != '\n'))
                        throw new InvalidDataException("Inline preview exceeds the text/control-character safety bound.");
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    result = null;
                    Report("Could not load inline image " + NomadNetTerminalText.Safe(image.Target) + ": " +
                        NomadNetTerminalText.Safe(exception.Message));
                }
                NomadNetRenderedPage? current;
                lock (_gate)
                {
                    if (_disposed) return;
                    current = _page;
                    if (current?.Media.Distinct().Take(MaximumImages).Contains(image) != true) continue;
                    _previews[image] = result;
                }
                foreach (var observer in Changed?.GetInvocationList() ?? [])
                {
                    try { ((Action<NomadNetRenderedPage>)observer)(current); }
                    catch (Exception exception) { Report("Inline image observer failed: " + exception.Message); }
                }
            }
        }
        finally { _serial.Release(); }
    }

    private void Report(string message)
    {
        var report = NomadNetTerminalText.Safe(message[..Math.Min(message.Length, 4096)]);
        try
        {
            if (Log is { } log)
            {
                log(report);
                return;
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("NomadNet inline image log observer failed: {0}; original report: {1}",
                NomadNetTerminalText.Safe(exception.Message[..Math.Min(exception.Message.Length, 4096)]), report);
            return;
        }
        System.Diagnostics.Trace.TraceError("NomadNet inline image: {0}", report);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            _disposal ??= Task.Run(DisposeCoreAsync);
            return new(_disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Task[] pending;
        lock (_gate) pending = _pending.Append(_worker).OfType<Task>().ToArray();
        _queue.Writer.TryComplete();
        _stop.Cancel();
        try { await Task.WhenAll(pending).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally { _serial.Dispose(); _stop.Dispose(); }
    }
}
