using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using RdpMonitorSwitcher.Core.Models;
using RdpMonitorSwitcher.Core.Services;
using RdpMonitorSwitcher.Infrastructure;

namespace RdpMonitorSwitcher.Services;

/// <summary>
/// Keeps the local and remote clipboard in sync (when enabled in the profile):
/// text in both directions, and local files (Ctrl+C in Explorer) offered to the remote PC so
/// Ctrl+V works there. Owns the current <see cref="LocalFileOffer"/>.
/// </summary>
internal sealed class ClipboardSync : IDisposable
{
    private readonly RemoteDesktopController _controller;
    private readonly ILogger _log;
    private HwndSource? _source;
    private string? _lastFromRemote;
    private IReadOnlyList<string>? _ignoredFileDrop;
    private LocalFileOffer? _offer;
    private int _changeCounter;

    public ClipboardSync(RemoteDesktopController controller, ILogger<ClipboardSync> logger)
    {
        _controller = controller;
        _log = logger;
        _controller.ClipboardTextReceived += (_, text) => SetLocal(text);
        _controller.StateChanged += (_, _) =>
        {
            if (_controller.State == ConnectionState.Connected)
                PushLocal();
            else if (_controller.State == ConnectionState.Disconnected)
                ReplaceOffer(null);
        };
    }

    private bool Enabled => _controller.Profile?.RedirectClipboard == true &&
                            _controller.State == ConnectionState.Connected;

    /// <summary>Starts listening for local clipboard changes on the given window.</summary>
    public void Attach(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
        if (!NativeMethods.AddClipboardFormatListener(hwnd))
            _log.LogWarning("AddClipboardFormatListener failed: {Error}", Marshal.GetLastWin32Error());
    }

    /// <summary>
    /// Files that the app itself put on the local clipboard (fetched from the remote PC);
    /// they must not be offered back.
    /// </summary>
    public void IgnoreFileDrop(IReadOnlyList<string> paths) => _ignoredFileDrop = paths;

    /// <summary>Takes ownership of an offer that was put on the RDP clipboard elsewhere (drop).</summary>
    public void AdoptOffer(LocalFileOffer offer)
    {
        _changeCounter++; // a pending Ctrl+C offer must not replace it
        ReplaceOffer(offer);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_CLIPBOARDUPDATE)
            PushLocal();
        return 0;
    }

    private void PushLocal()
    {
        if (!Enabled || _controller.Session is null)
            return;

        var files = TryGet(() => Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList().Cast<string>().ToList() : null);
        if (files is { Count: > 0 })
        {
            if (_ignoredFileDrop is not null && files.SequenceEqual(_ignoredFileDrop, StringComparer.OrdinalIgnoreCase))
                return;
            _ = OfferLocalFilesAsync(files);
            return;
        }

        var text = TryGet(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
        if (text is null || text == _lastFromRemote)
            return;
        _changeCounter++;
        ReplaceOffer(null);
        _controller.Session.SetClipboardText(text);
    }

    /// <summary>Enumerating folders can take a moment; do it off the UI thread.</summary>
    private async Task OfferLocalFilesAsync(IReadOnlyList<string> files)
    {
        var change = ++_changeCounter;
        LocalFileOffer offer;
        try
        {
            offer = await Task.Run(() => LocalFileOffer.Create(files));
        }
        catch (Exception ex) when (ex is FileTransferException or IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Local files cannot be offered to the remote PC");
            return;
        }

        if (change != _changeCounter || !Enabled || _controller.Session is not { } session)
        {
            offer.Dispose(); // the clipboard changed again meanwhile
            return;
        }
        if (session.SetClipboardFiles(offer))
        {
            ReplaceOffer(offer);
            _log.LogInformation("Offered {Count} local file(s), {Bytes} bytes to the remote clipboard", offer.FileCount, offer.TotalBytes);
        }
        else
        {
            offer.Dispose();
        }
    }

    private void ReplaceOffer(LocalFileOffer? offer)
    {
        var previous = _offer;
        _offer = offer;
        if (previous is not null && previous != offer)
            previous.Dispose();
    }

    private void SetLocal(string text)
    {
        if (!Enabled)
            return;
        _lastFromRemote = text;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return;
            }
            catch (COMException) when (attempt < 4)
            {
                Thread.Sleep(20); // another process holds the clipboard
            }
            catch (COMException ex)
            {
                _log.LogWarning(ex, "Could not set the local clipboard");
            }
        }
    }

    private T? TryGet<T>(Func<T?> read) where T : class
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                return read();
            }
            catch (COMException) when (attempt < 4)
            {
                Thread.Sleep(20);
            }
            catch (COMException ex)
            {
                _log.LogWarning(ex, "Could not read the local clipboard");
            }
        }
        return null;
    }

    public void Dispose()
    {
        ReplaceOffer(null);
        if (_source is not null)
        {
            NativeMethods.RemoveClipboardFormatListener(_source.Handle);
            _source.RemoveHook(WndProc);
            _source = null;
        }
    }
}
