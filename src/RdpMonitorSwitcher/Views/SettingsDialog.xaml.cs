using System.Windows;
using RdpMonitorSwitcher.Core.Services;

namespace RdpMonitorSwitcher.Views;

/// <summary>App-wide settings (currently: the file drag-out zone).</summary>
public partial class SettingsDialog : Window
{
    private static readonly ScreenEdge[] Edges = [ScreenEdge.Right, ScreenEdge.Left, ScreenEdge.Top, ScreenEdge.Bottom];

    public SettingsDialog(DragOutSettings settings)
    {
        InitializeComponent();
        var s = settings.Normalized();
        EnabledBox.IsChecked = s.Enabled;
        EdgeBox.SelectedIndex = Array.IndexOf(Edges, s.Edge);
        SizeSlider.Value = s.Size;
        DelaySlider.Value = s.ArmDelayMs;
        UpdateEnabled();
    }

    public DragOutSettings? Result { get; private set; }

    private void OnEnabledChanged(object sender, RoutedEventArgs e) => UpdateEnabled();

    private void UpdateEnabled()
    {
        if (ZoneOptions is not null)
            ZoneOptions.IsEnabled = EnabledBox.IsChecked == true;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Result = new DragOutSettings
        {
            Enabled = EnabledBox.IsChecked == true,
            Edge = Edges[Math.Max(0, EdgeBox.SelectedIndex)],
            Size = SizeSlider.Value,
            ArmDelayMs = (int)DelaySlider.Value,
        }.Normalized();
        DialogResult = true;
    }
}
