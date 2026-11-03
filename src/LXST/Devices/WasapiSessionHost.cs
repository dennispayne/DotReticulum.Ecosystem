using System.Threading.Channels;

namespace LXST.Devices;

/// <summary>Keeps native initialization, buffers and COM release on one owned MTA thread.</summary>
internal sealed class WasapiSessionHost : IWindowsWaveSession
{
    private readonly Channel<Action<IWindowsWaveSession>> _commands = Channel.CreateBounded<Action<IWindowsWaveSession>>(1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly object _gate = new();
    private bool _disposed;
    private bool _resourcesDisposed;

    internal WasapiSessionHost(Func<IWindowsWaveSession> factory)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _worker = Task.Factory.StartNew(() =>
        {
            try
            {
                using var session = factory();
                started.TrySetResult();
                while (_commands.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                    while (_commands.Reader.TryRead(out var command)) command(session);
            }
            catch (Exception exception)
            {
                started.TrySetException(exception);
                _commands.Writer.TryComplete(exception);
                throw;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try { started.Task.GetAwaiter().GetResult(); }
        catch
        {
            try { _worker.GetAwaiter().GetResult(); }
            finally { _stop.Dispose(); }
            throw;
        }
    }

    private T Invoke<T>(Func<IWindowsWaveSession, CancellationToken, T> operation, CancellationToken token)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        var completed = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Writer.WriteAsync(session =>
        {
            try { stop.Token.ThrowIfCancellationRequested(); completed.TrySetResult(operation(session, stop.Token)); }
            catch (Exception exception) { completed.TrySetException(exception); }
        }, stop.Token).AsTask().GetAwaiter().GetResult();
        return completed.Task.GetAwaiter().GetResult();
    }

    public float[] Read(CancellationToken token) => Invoke((session, stop) => session.Read(stop), token);
    public void Write(ReadOnlySpan<float> samples, CancellationToken token)
    {
        var owned = samples.ToArray();
        Invoke((session, stop) => { session.Write(owned, stop); return true; }, token);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _disposed = true;
                _stop.Cancel();
                _commands.Writer.TryComplete();
            }
        }
        try { _worker.GetAwaiter().GetResult(); }
        finally
        {
            lock (_gate)
            {
                if (!_resourcesDisposed)
                {
                    _resourcesDisposed = true;
                    _stop.Dispose();
                }
            }
        }
    }
}
