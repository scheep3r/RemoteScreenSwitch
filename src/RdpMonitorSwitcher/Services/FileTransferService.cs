using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using RdpMonitorSwitcher.Core.Services;
using RdpMonitorSwitcher.Core.ViewModels;
using RdpMonitorSwitcher.Infrastructure;
using RdpMonitorSwitcher.Views;

namespace RdpMonitorSwitcher.Services;

/// <summary>
/// "Drag files out of the remote PC": fetches the selected remote files through the RDP
/// clipboard and then either continues the drag locally (if the left button is still held)
/// or puts the files on the local clipboard.
/// </summary>
internal sealed class FileTransferService
{
    private static readonly TimeSpan RemoteSettleDelay = TimeSpan.FromMilliseconds(150);

    private readonly MainViewModel _main;
    private readonly FrameRenderer _renderer;
    private readonly InputRouter _input;
    private readonly ClipboardSync _clipboard;
    private readonly ILogger _log;
    private CancellationTokenSource? _cts;
    private bool _draggingOut;

    public FileTransferService(MainViewModel main, FrameRenderer renderer, InputRouter input, ClipboardSync clipboard,
        ILogger<FileTransferService> logger)
    {
        _main = main;
        _renderer = renderer;
        _input = input;
        _clipboard = clipboard;
        _log = logger;
        _main.PullFilesRequested += (_, view) => OnPullRequested(view);
        _renderer.ViewportRegistered += (_, viewport) =>
        {
            viewport.DragOutTriggered += OnDragOutTriggered;
            viewport.FilesDropped += (_, drop) => _ = DropAsync(drop.Paths, drop.Point);
        };
        _input.EscapePressed = TryCancel;
    }

    /// <summary>Local files may be dropped on a remote view (not our own drag-out coming back).</summary>
    public bool CanAcceptDrop => !IsBusy && !_draggingOut && _main.CanPullFiles;

    public static string TransferRoot { get; } = Path.Combine(Path.GetTempPath(), "RdpMonitorSwitcher", "transfer");

    public bool IsBusy => _cts is not null;

    private void OnPullRequested(MonitorViewModel view)
    {
        var viewport = _renderer.FindViewport(view);
        if (viewport is null)
            return;
        var dragging = viewport.IsLeftButtonHeld && IsLeftButtonPhysicallyDown();
        _ = RunAsync(viewport, dragging);
    }

    private void OnDragOutTriggered(object? sender, EventArgs e)
    {
        if (sender is RemoteViewport viewport && !IsBusy)
            _ = RunAsync(viewport, dragging: true);
    }

    private bool TryCancel()
    {
        if (_cts is null)
            return false;
        _cts.Cancel();
        return true;
    }

    private async Task RunAsync(RemoteViewport viewport, bool dragging)
    {
        if (IsBusy || _main.Controller.Session is not { } session)
            return;
        using var cts = new CancellationTokenSource();
        _cts = cts;
        var popup = new TransferPopup(viewport, dragging);
        try
        {
            if (dragging)
                viewport.CancelRemoteDrag();
            _input.ReleaseAll(); // e.g. Ctrl/Alt from Ctrl+Alt+D must not turn Ctrl+C into Ctrl+Alt+C
            popup.Show("Kopiere Auswahl auf dem Remote-PC …");
            await Task.Delay(RemoteSettleDelay, cts.Token);

            var directory = Path.Combine(TransferRoot, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}");
            var progress = new Progress<TransferProgress>(popup.Update);
            var result = await new RemoteFilePuller(session).PullAsync(directory, progress, cts.Token);
            _log.LogInformation("Fetched {Count} file(s), {Bytes} bytes from the remote PC", result.FileCount, result.TotalBytes);
            popup.Close();

            var data = CreateFileDropData(result.TopLevelPaths);
            if (dragging && IsLeftButtonPhysicallyDown())
            {
                viewport.ReleaseMouseCapture();
                _clipboard.IgnoreFileDrop(result.TopLevelPaths);
                DragDropEffects effect;
                _draggingOut = true;
                try
                {
                    effect = DragDrop.DoDragDrop(viewport, data, DragDropEffects.Copy);
                }
                finally
                {
                    _draggingOut = false;
                }
                if (effect == DragDropEffects.None)
                {
                    SetClipboard(data);
                    _main.Notice = $"{Describe(result)} nicht abgelegt – sie liegen jetzt in der lokalen Zwischenablage (Strg+V).";
                }
                else
                {
                    _main.Notice = $"{Describe(result)} auf den lokalen PC kopiert.";
                }
            }
            else
            {
                _clipboard.IgnoreFileDrop(result.TopLevelPaths);
                SetClipboard(data);
                _main.Notice = $"{Describe(result)} vom Remote-PC in der lokalen Zwischenablage – mit Strg+V im Explorer einfügen.";
            }
        }
        catch (OperationCanceledException)
        {
            _main.Notice = "Dateiübertragung abgebrochen.";
        }
        catch (FileTransferException ex)
        {
            _main.Notice = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "File transfer failed");
            _main.Notice = $"Dateiübertragung fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            popup.Close();
            viewport.ResumeInput();
            _cts = null;
        }
    }

    /// <summary>Local files dropped on a remote view: offer them and paste them at the drop point.</summary>
    private async Task DropAsync(IReadOnlyList<string> paths, Core.Models.PixelPoint point)
    {
        if (IsBusy || _main.Controller.Session is not { } session)
            return;
        using var cts = new CancellationTokenSource();
        _cts = cts;
        LocalFileOffer? offer = null;
        try
        {
            offer = await Task.Run(() => LocalFileOffer.Create(paths), cts.Token);
            _input.ReleaseAll();
            await new RemoteFileDropper(session).DropAsync(offer, point, cts.Token);
            _clipboard.AdoptOffer(offer); // stays readable while the remote Explorer copies
            var count = offer.FileCount == 1 ? "1 Datei" : $"{offer.FileCount} Dateien";
            _main.Notice = $"{count} wird auf dem Remote-PC eingefügt – den Fortschritt zeigt der Explorer dort.";
            _log.LogInformation("Dropped {Count} local file(s), {Bytes} bytes onto the remote PC", offer.FileCount, offer.TotalBytes);
            offer = null;
        }
        catch (OperationCanceledException)
        {
            _main.Notice = "Übertragung abgebrochen.";
        }
        catch (FileTransferException ex)
        {
            _main.Notice = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "Drop failed");
            _main.Notice = $"Dateien konnten nicht übertragen werden: {ex.Message}";
        }
        finally
        {
            offer?.Dispose();
            _cts = null;
        }
    }

    private static string Describe(PullResult result) =>
        result.FileCount == 1 ? "1 Datei" : $"{result.FileCount} Dateien";

    private static DataObject CreateFileDropData(IReadOnlyList<string> paths)
    {
        var data = new DataObject();
        var list = new StringCollection();
        list.AddRange([.. paths]);
        data.SetFileDropList(list);
        // DROPEFFECT_COPY: Explorer copies instead of moving the temporary files.
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)));
        return data;
    }

    private void SetClipboard(DataObject data)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return;
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 4)
            {
                Thread.Sleep(20);
            }
            catch (System.Runtime.InteropServices.COMException ex)
            {
                _log.LogWarning(ex, "Could not put files on the clipboard");
            }
        }
    }

    private static bool IsLeftButtonPhysicallyDown() =>
        (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LBUTTON) & 0x8000) != 0;

    /// <summary>Deletes transfer folders from earlier runs (Explorer has long finished copying them).</summary>
    public static void CleanupOldTransfers(TimeSpan maxAge)
    {
        try
        {
            if (!Directory.Exists(TransferRoot))
                return;
            foreach (var dir in Directory.GetDirectories(TransferRoot))
            {
                if (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir) > maxAge)
                    Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a folder may still be in use.
        }
    }

    /// <summary>Small progress window next to the mouse.</summary>
    private sealed class TransferPopup
    {
        private readonly Popup _popup;
        private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
        private readonly TextBlock _detail = new() { Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)), Margin = new Thickness(0, 4, 0, 0), MaxWidth = 340, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly ProgressBar _bar = new() { Height = 6, Margin = new Thickness(0, 6, 0, 0), IsIndeterminate = true, Minimum = 0, Maximum = 1 };

        public TransferPopup(UIElement target, bool dragging)
        {
            var hint = new TextBlock
            {
                Text = dragging ? "Maustaste gedrückt halten · Esc = Abbrechen" : "Esc = Abbrechen",
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
                FontSize = 11,
                Margin = new Thickness(0, 6, 0, 0),
            };
            _popup = new Popup
            {
                PlacementTarget = target,
                Placement = PlacementMode.MousePoint,
                HorizontalOffset = 18,
                VerticalOffset = 18,
                AllowsTransparency = true,
                StaysOpen = true,
                IsHitTestVisible = false,
                Child = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x26, 0x26, 0x26)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x0A, 0x6C, 0xD8)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12, 8, 12, 8),
                    MinWidth = 240,
                    Child = new StackPanel { Children = { _title, _detail, _bar, hint } },
                },
            };
        }

        public void Show(string title)
        {
            _title.Text = title;
            _popup.IsOpen = true;
        }

        public void Update(TransferProgress p)
        {
            _title.Text = $"Hole Datei {p.FileIndex} von {p.FileCount} vom Remote-PC …";
            _detail.Text = p.BytesTotal is { } total
                ? $"{p.CurrentFile} · {FormatBytes(p.BytesDone)} / {FormatBytes(total)}"
                : $"{p.CurrentFile} · {FormatBytes(p.BytesDone)}";
            if (p.Fraction is { } fraction)
            {
                _bar.IsIndeterminate = false;
                _bar.Value = fraction;
            }
        }

        public void Close() => _popup.IsOpen = false;

        private static string FormatBytes(long bytes) => bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
            < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
        };
    }
}
