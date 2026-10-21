using System.Windows;
using System.Windows.Media;
using RdpMonitorSwitcher.Core.Models;
using RdpMonitorSwitcher.Core.Services;
using RdpMonitorSwitcher.Core.ViewModels;
using RdpMonitorSwitcher.Infrastructure;

namespace RdpMonitorSwitcher.Services;

internal sealed class LocalEnvironmentProvider : ILocalEnvironmentProvider
{
    public LocalEnvironment GetEnvironment(RemoteScaling remoteScaling)
    {
        var dpiScale = Application.Current?.MainWindow is { } window ? VisualTreeHelper.GetDpi(window).DpiScaleX : 1.0;
        var (desktop, device) = LocalEnvironment.ScaleFactorsFor(dpiScale, remoteScaling);

        // Low word of the HKL is the language/layout id (e.g. 0x0407 = German).
        var hkl = NativeMethods.GetKeyboardLayout(0);
        var layout = (uint)(hkl.ToInt64() & 0xFFFF);
        return new LocalEnvironment(desktop, device, layout);
    }
}
