using Avalonia.Interactivity;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LXMF;
using Sideband;

namespace Sideband.Desktop;

public sealed partial class MainWindow
{
    private bool _paperUiBusy;
    private Bitmap? _paperQrBitmap;

    private void CreatePaperClicked(object? sender, RoutedEventArgs e) => RunPaperOperation(async () =>
    {
        var peer = _selectedContact ?? throw new InvalidOperationException("Select a contact before creating paper.");
        if (_recordedAudio is not null || !string.IsNullOrWhiteSpace(AttachmentPathBox.Text))
            throw new InvalidOperationException("Paper creation uses text only. Remove the attachment or recording first.");
        var content = MessageBox.Text ?? string.Empty;
        var message = await _client.CreatePaperMessageAsync(peer, content, _windowCancellation.Token);
        var uri = await _client.GetPaperMessageUriAsync(message.Id, _windowCancellation.Token);
        await RefreshMessagesAsync();
        if (_selectedContact == peer)
        {
            DisplayPaper(uri);
            if (MessageBox.Text == content) MessageBox.Text = string.Empty;
        }
        SetStatus("Paper text saved locally. Export or copy it explicitly; it has not been delivered.");
    });

    private void ShowPaperClicked(object? sender, RoutedEventArgs e) => RunPaperOperation(async () =>
    {
        var version = _mediaSelectionVersion;
        var uri = await _client.GetPaperMessageUriAsync(SelectedMessage().Id, _windowCancellation.Token);
        if (version != _mediaSelectionVersion) throw new InvalidOperationException("Selection changed. Select paper again.");
        DisplayPaper(uri);
        SetStatus("Saved paper URI and QR shown. This is not delivery confirmation.");
    });

    private void ExportPaperClicked(object? sender, RoutedEventArgs e) => RunPaperOperation(async () =>
    {
        var path = AttachmentExportPathBox.Text;
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await PaperExportCommand.ExportAsync(_client, SelectedMessage().Id, path, false, _windowCancellation.Token);
        SetStatus("Paper URI exported without overwriting or opening a file.");
    });

    private void ExportPaperQrClicked(object? sender, RoutedEventArgs e) => RunPaperOperation(async () =>
    {
        var path = AttachmentExportPathBox.Text;
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await PaperExportCommand.ExportAsync(_client, SelectedMessage().Id, path, true, _windowCancellation.Token);
        SetStatus("Paper QR PNG exported without overwriting or opening a file.");
    });

    private void ImportPaperClicked(object? sender, RoutedEventArgs e) => RunPaperOperation(async () =>
    {
        var result = await _client.IngestPaperUriAsync(PaperImportBox.Text ?? string.Empty, _windowCancellation.Token);
        PaperImportStatusText.Text = result switch
        {
            SidebandPaperIngestResult.LocalDelivery => "Accepted locally. Normal inbound history processing continues; no network delivery is claimed.",
            SidebandPaperIngestResult.Duplicate => "Duplicate paper message; no new history entry.",
            SidebandPaperIngestResult.NonLocal => "Envelope addressed elsewhere. Content is not authenticated and was not forwarded.",
            _ => "Rejected paper URI; no message accepted.",
        };
        SetStatus(PaperImportStatusText.Text);
    });

    private void DisplayPaper(string uri)
    {
        var png = LxPaperQr.Encode(uri, cancellationToken: _windowCancellation.Token)
            .ToPng(cancellationToken: _windowCancellation.Token);
        using var stream = new MemoryStream(png, writable: false);
        var bitmap = new Bitmap(stream);
        PaperQrPreview.Source = null;
        _paperQrBitmap?.Dispose();
        _paperQrBitmap = bitmap;
        PaperUriBox.Text = uri;
        PaperQrPreview.ResetRenderObservation();
        PaperQrPreview.Source = bitmap;
        PaperQrPreview.IsVisible = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_closing && PaperQrPreview.Source == bitmap) PaperQrPreview.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    private void ClearPaperPreview()
    {
        PaperQrPreview.Source = null;
        _paperQrBitmap?.Dispose();
        _paperQrBitmap = null;
        PaperQrPreview.IsVisible = false;
        PaperQrPreview.ResetRenderObservation();
        PaperUriBox.Text = string.Empty;
    }

    private void RunPaperOperation(Func<Task> operation) => TrackOperation(async () =>
    {
        if (_paperUiBusy) { ShowError("Another paper operation is pending."); return; }
        _paperUiBusy = true;
        SetPaperButtonsEnabled(false);
        PaperImportStatusText.Text = "Paper operation pending.";
        SetStatus(PaperImportStatusText.Text);
        try { await operation(); }
        catch (OperationCanceledException) { SetStatus("Paper operation cancelled."); }
        catch (Exception ex)
        {
            PaperImportStatusText.Text = "Paper operation failed: " + ex.Message;
            ShowError(PaperImportStatusText.Text);
        }
        finally
        {
            _paperUiBusy = false;
            SetPaperButtonsEnabled(true);
        }
    });

    private void SetPaperButtonsEnabled(bool enabled)
    {
        CreatePaperButton.IsEnabled = ShowPaperButton.IsEnabled = ExportPaperButton.IsEnabled =
            ExportPaperQrButton.IsEnabled = ImportPaperButton.IsEnabled = enabled;
    }
}

public sealed class PaperQrView : Control
{
    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<PaperQrView, IImage?>(nameof(Source));

    static PaperQrView()
    {
        AffectsMeasure<PaperQrView>(SourceProperty);
        AffectsRender<PaperQrView>(SourceProperty);
    }

    public IImage? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public int RenderedImageCount { get; private set; }

    internal void ResetRenderObservation() => RenderedImageCount = 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Source is not { } image || image.Size.Width <= 0 || image.Size.Height <= 0) return default;
        var scale = Math.Min(1, Math.Min(availableSize.Width / image.Size.Width, availableSize.Height / image.Size.Height));
        return new Size(image.Size.Width * scale, image.Size.Height * scale);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Source is not { } image || Bounds.Width <= 0 || Bounds.Height <= 0 ||
            image.Size.Width <= 0 || image.Size.Height <= 0) return;
        var scale = Math.Min(Bounds.Width / image.Size.Width, Bounds.Height / image.Size.Height);
        var width = image.Size.Width * scale;
        var height = image.Size.Height * scale;
        context.DrawImage(image, new Rect(image.Size),
            new Rect((Bounds.Width - width) / 2, (Bounds.Height - height) / 2, width, height));
        RenderedImageCount = 1;
    }
}
