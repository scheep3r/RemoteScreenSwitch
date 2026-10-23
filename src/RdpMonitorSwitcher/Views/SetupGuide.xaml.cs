using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace RdpMonitorSwitcher.Views;

/// <summary>Fold-out guide on the disconnected overlay: how the remote PC can be reached.</summary>
public partial class SetupGuide : UserControl
{
    public SetupGuide()
    {
        InitializeComponent();
    }

    private void OnOpenLink(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
