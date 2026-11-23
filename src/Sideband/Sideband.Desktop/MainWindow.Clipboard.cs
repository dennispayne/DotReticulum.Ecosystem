using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Sideband.Desktop;

public sealed partial class MainWindow
{
    private readonly Func<string, Task> _clipboardWriter;
    private Task _clipboardWriteTask = Task.CompletedTask;
    private string? _smokeClipboardText;

    private void CopyMessageClicked(object? sender, RoutedEventArgs e) => TrackOperation(async () =>
    {
        try { await CopySelectedMessageTextAsync(); }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested) { }
        catch (TimeoutException) when (!_clipboardWriteTask.IsCompleted)
        { ShowError("Could not copy message text: clipboard did not complete within five seconds; one write remains pending."); }
        catch (Exception ex) { ShowError("Could not copy message text: " + ex.Message); }
    });

    private async Task CopySelectedMessageTextAsync()
    {
        var message = SelectedMessage();
        await WriteClipboardTextAsync(message.Content);
        SetStatus("Selected raw message text copied; title and attachments were not copied.");
    }

    private void CopyMessageTelemetryClicked(object? sender, RoutedEventArgs e) => TrackOperation(async () =>
    {
        try { await CopySelectedMessageTelemetryAsync(); }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested) { }
        catch (TimeoutException) when (!_clipboardWriteTask.IsCompleted)
        { ShowError("Could not copy selected telemetry: clipboard did not complete within five seconds; one write remains pending."); }
        catch (Exception ex) { ShowError("Could not copy selected telemetry: " + ex.Message); }
    });

    private async Task CopySelectedMessageTelemetryAsync()
    {
        var snapshot = SelectedMessage().TelemetryHex ??
            throw new InvalidOperationException("The selected message has no captured telemetry; a newer source report will not be substituted.");
        await WriteClipboardTextAsync(SidebandMessageTelemetry.CopyText(snapshot));
        SetStatus("Selected message's captured telemetry copied; no latest source report or sensor was queried.");
    }

    private async Task WriteClipboardTextAsync(string text)
    {
        _windowCancellation.Token.ThrowIfCancellationRequested();
        if (!_clipboardWriteTask.IsCompleted)
            throw new InvalidOperationException("A previous clipboard write is still pending.");
        var operation = _clipboardWriter(text) ?? throw new InvalidOperationException("The clipboard writer returned no operation.");
        _clipboardWriteTask = operation;
        UpdateRetryAvailability();
        _ = operation.ContinueWith(completed =>
        {
            if (completed.IsFaulted)
                System.Diagnostics.Trace.TraceError("Sideband desktop clipboard write failed: {0}", completed.Exception);
            if (!_closing) Dispatcher.UIThread.Post(() => { if (!_closing) UpdateRetryAvailability(); });
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        await operation.WaitAsync(TimeSpan.FromSeconds(5), _windowCancellation.Token);
    }

    private Task WriteDesktopClipboardAsync(string text)
    {
        var clipboard = GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("The desktop clipboard is unavailable.");
        return clipboard.SetTextAsync(text);
    }

    private Task WriteSmokeClipboardAsync(string text)
    {
        _smokeClipboardText = text;
        return Task.CompletedTask;
    }
}
