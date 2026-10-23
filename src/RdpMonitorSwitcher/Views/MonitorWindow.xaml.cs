using System.Windows;
using RdpMonitorSwitcher.Core.ViewModels;
using RdpMonitorSwitcher.Services;

namespace RdpMonitorSwitcher.Views;

/// <summary>A separate, freely movable window showing one remote monitor of the shared session.</summary>
public partial class MonitorWindow : Window
{
    private readonly WindowChrome _chrome;

    internal MonitorWindow(AppServices services, MonitorViewModel view)
    {
        InitializeComponent();
        View = view;
        DataContext = view;
        Viewport.Attach(services, view);
        _chrome = new WindowChrome(this, view, Toolbar, HotZone);
        BannerAutoHide.Attach(ErrorBanner, view, nameof(view.ErrorMessage), () => view.ErrorMessage, () => view.DismissErrorCommand.Execute(null));
        Loaded += (_, _) => Viewport.Focus();
        Closed += (_, _) => Viewport.Detach();
    }

    public MonitorViewModel View { get; }
}
