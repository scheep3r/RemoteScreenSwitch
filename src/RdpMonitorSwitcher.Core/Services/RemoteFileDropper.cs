using RdpMonitorSwitcher.Core.Models;
using RdpMonitorSwitcher.Core.Rdp;

namespace RdpMonitorSwitcher.Core.Services;

/// <summary>
/// Emulates "drop local files at this point of the remote desktop": RDP has no drop channel,
/// so the files are offered on the RDP clipboard and pasted where they were dropped
/// (click at the drop point, then Ctrl+V). The remote Explorer then pulls the contents and
/// shows its own copy progress.
/// </summary>
public sealed class RemoteFileDropper
{
    public static readonly TimeSpan AcceptTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ClickToPasteDelay = TimeSpan.FromMilliseconds(200);

    private const uint ScanControl = 0x1D;
    private const uint ScanV = 0x2F;

    private readonly IRdpSession _session;
    private readonly TimeProvider _time;

    public RemoteFileDropper(IRdpSession session, TimeProvider? time = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _time = time ?? TimeProvider.System;
    }

    /// <param name="dropPoint">Remote desktop coordinates of the drop.</param>
    public async Task DropAsync(LocalFileOffer offer, PixelPoint dropPoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);
        await OfferAsync(offer, cancellationToken);

        // Focus the window / folder under the drop point, then paste there.
        _session.SendMouse(MouseFlags.Move, dropPoint.X, dropPoint.Y);
        _session.SendMouse((ushort)(MouseFlags.Button1 | MouseFlags.Down), dropPoint.X, dropPoint.Y);
        _session.SendMouse(MouseFlags.Button1, dropPoint.X, dropPoint.Y);
        await Task.Delay(ClickToPasteDelay, _time, cancellationToken);

        _session.SendKey(true, ScanControl);
        _session.SendKey(true, ScanV);
        _session.SendKey(false, ScanV);
        _session.SendKey(false, ScanControl);
    }

    /// <summary>Puts the files on the RDP clipboard and waits until the remote PC has taken the list.</summary>
    public async Task OfferAsync(LocalFileOffer offer, CancellationToken cancellationToken)
    {
        var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<bool> handler = (_, ok) => accepted.TrySetResult(ok);
        _session.LocalClipboardAccepted += handler;
        try
        {
            if (!_session.SetClipboardFiles(offer))
                throw new FileTransferException("Die Dateien konnten dem Remote-PC nicht angeboten werden. Ist „Zwischenablage teilen“ aktiviert?");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var finished = await Task.WhenAny(accepted.Task, Task.Delay(AcceptTimeout, _time, timeoutCts.Token));
            timeoutCts.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            if (finished != accepted.Task)
                throw new FileTransferException("Der Remote-PC hat die Dateiliste nicht bestätigt.");
            if (!await accepted.Task)
                throw new FileTransferException("Der Remote-PC hat die Dateiliste abgelehnt.");
        }
        finally
        {
            _session.LocalClipboardAccepted -= handler;
        }
    }
}
