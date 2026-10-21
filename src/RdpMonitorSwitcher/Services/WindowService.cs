using System.Windows;
using RdpMonitorSwitcher.Core.ViewModels;
using RdpMonitorSwitcher.Views;

namespace RdpMonitorSwitcher.Services;

/// <summary>Creates the separate monitor windows and remembers where the user put them.</summary>
internal sealed class WindowService : IWindowService
{
    private readonly Dictionary<MonitorViewModel, MonitorWindow> _windows = [];
    private readonly Func<AppServices> _services;
    private bool _closingProgrammatically;

    public WindowService(Func<AppServices> services) => _services = services;

    public void ShowMonitorWindow(MonitorViewModel view)
    {
        var services = _services();
        var window = new MonitorWindow(services, view);
        if (services.Main.Settings.MonitorWindows.TryGetValue(view.SelectedIndex, out var placement))
            WindowPlacementHelper.Restore(window, placement);
        else
            Cascade(window, _windows.Count);

        window.Closing += (_, _) =>
            services.Main.Settings.MonitorWindows[view.SelectedIndex] = WindowPlacementHelper.Capture(window);
        window.Closed += (_, _) =>
        {
            _windows.Remove(view);
            if (!_closingProgrammatically)
                services.Main.OnMonitorWindowClosed(view);
        };
        _windows[view] = window;
        window.Show();
    }

    public void CloseMonitorWindow(MonitorViewModel view)
    {
        if (!_windows.TryGetValue(view, out var window))
            return;
        _closingProgrammatically = true;
        try
        {
            window.Close();
        }
        finally
        {
            _closingProgrammatically = false;
        }
    }

    public void ActivateMonitorWindow(MonitorViewModel view)
    {
        if (!_windows.TryGetValue(view, out var window))
            return;
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private static void Cascade(Window window, int index)
    {
        var owner = Application.Current.MainWindow;
        if (owner is null)
            return;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = owner.Left + 40 + index * 32;
        window.Top = owner.Top + 40 + index * 32;
    }
}
