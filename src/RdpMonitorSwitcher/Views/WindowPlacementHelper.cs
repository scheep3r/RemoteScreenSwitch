using System.Windows;
using RdpMonitorSwitcher.Core.Configuration;

namespace RdpMonitorSwitcher.Views;

internal static class WindowPlacementHelper
{
    public static WindowPlacement Capture(Window window)
    {
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;
        return new WindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = window.WindowState == WindowState.Maximized,
        };
    }

    /// <summary>Restores a saved placement if it is still (mostly) on a visible screen.</summary>
    public static void Restore(Window window, WindowPlacement? placement)
    {
        if (placement is null || placement.Width < 200 || placement.Height < 150)
            return;
        var virtualScreen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var rect = new Rect(placement.Left, placement.Top, placement.Width, placement.Height);
        if (!virtualScreen.IntersectsWith(rect))
            return;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = placement.Left;
        window.Top = placement.Top;
        window.Width = placement.Width;
        window.Height = placement.Height;
        if (placement.Maximized)
            window.WindowState = WindowState.Maximized;
    }
}
