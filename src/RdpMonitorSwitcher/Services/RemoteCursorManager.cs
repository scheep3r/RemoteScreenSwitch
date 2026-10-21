using System.Windows.Input;
using System.Windows.Interop;
using RdpMonitorSwitcher.Core.Rdp;
using RdpMonitorSwitcher.Core.Services;
using RdpMonitorSwitcher.Infrastructure;

namespace RdpMonitorSwitcher.Services;

/// <summary>Turns remote pointer shapes into a WPF <see cref="Cursor"/> shown over all remote views.</summary>
internal sealed class RemoteCursorManager : IDisposable
{
    private Cursor? _owned;

    public RemoteCursorManager(RemoteDesktopController controller)
    {
        controller.CursorChanged += (_, change) => Apply(change);
        controller.StateChanged += (_, _) =>
        {
            // A hidden or custom remote pointer must not outlive the session.
            if (controller.State != Core.Models.ConnectionState.Connected && Current != Cursors.Arrow)
                Apply(new RemoteCursorChange(RemoteCursorKind.Default, 0));
        };
    }

    public event EventHandler? Changed;

    public Cursor Current { get; private set; } = Cursors.Arrow;

    private void Apply(RemoteCursorChange change)
    {
        Cursor next;
        Cursor? owned = null;
        switch (change.Kind)
        {
            case RemoteCursorKind.Custom when change.Handle != 0:
                owned = CursorInteropHelper.Create(new NativeMethods.SafeCursorHandle(change.Handle));
                next = owned;
                break;
            case RemoteCursorKind.Hidden:
                next = Cursors.None;
                break;
            default:
                next = Cursors.Arrow;
                break;
        }

        var previous = _owned;
        _owned = owned;
        Current = next;
        Changed?.Invoke(this, EventArgs.Empty);
        previous?.Dispose();
    }

    public void Dispose()
    {
        _owned?.Dispose();
        _owned = null;
    }
}
