using System.Windows.Threading;
using RdpMonitorSwitcher.Core.Models;
using RdpMonitorSwitcher.Core.Rdp;
using RdpMonitorSwitcher.Core.Services;
using RdpMonitorSwitcher.Views;

namespace RdpMonitorSwitcher.Services;

/// <summary>
/// Distributes frame updates to every visible <see cref="RemoteViewport"/>. The frame buffer is
/// locked once per update and all views copy their monitor's dirty region from it.
/// </summary>
internal sealed class FrameRenderer
{
    private readonly RemoteDesktopController _controller;
    private readonly Dispatcher _dispatcher;
    private readonly List<RemoteViewport> _viewports = [];
    private readonly FrameReader _copyToViewports;
    private bool _refreshQueued;

    public FrameRenderer(RemoteDesktopController controller, Dispatcher dispatcher)
    {
        _controller = controller;
        _dispatcher = dispatcher;
        _copyToViewports = CopyToViewports;
        _controller.FrameAvailable += (_, _) => Render();
        _controller.StateChanged += (_, _) =>
        {
            if (_controller.State is ConnectionState.Disconnected or ConnectionState.Reconnecting)
            {
                foreach (var viewport in _viewports)
                    viewport.Clear();
            }
        };
        _controller.LayoutChanged += (_, _) => RequestRefresh();
    }

    /// <summary>Raised the first time a viewport is registered.</summary>
    public event EventHandler<RemoteViewport>? ViewportRegistered;

    private readonly HashSet<RemoteViewport> _known = [];

    public void Register(RemoteViewport viewport)
    {
        if (!_viewports.Contains(viewport))
            _viewports.Add(viewport);
        if (_known.Add(viewport))
            ViewportRegistered?.Invoke(this, viewport);
        RequestRefresh();
    }

    public RemoteViewport? FindViewport(Core.ViewModels.MonitorViewModel view) =>
        _viewports.FirstOrDefault(v => v.View == view);

    // _known keeps its entry so a re-loaded viewport is not announced (and subscribed to) twice.
    public void Unregister(RemoteViewport viewport) => _viewports.Remove(viewport);

    /// <summary>Schedules a render pass even without new frame data (e.g. after a monitor switch).</summary>
    public void RequestRefresh()
    {
        if (_refreshQueued)
            return;
        _refreshQueued = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _refreshQueued = false;
            Render();
        });
    }

    private void Render()
    {
        if (_viewports.Count == 0 || _controller.Session is not { } session)
            return;
        session.ReadFrame(_copyToViewports);
    }

    private void CopyToViewports(in FrameData frame)
    {
        foreach (var viewport in _viewports)
            viewport.UpdateFrom(frame);
    }
}
