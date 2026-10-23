using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using RdpMonitorSwitcher.Agent;
using RdpMonitorSwitcher.Core.Models;
using RdpMonitorSwitcher.Core.Services;

namespace RdpMonitorSwitcher.Views;

/// <summary>App-wide settings: drag-out zone, monitor arrangement and following the remote foreground window.</summary>
public partial class SettingsDialog : Window
{
    private static readonly ScreenEdge[] Edges = [ScreenEdge.Right, ScreenEdge.Left, ScreenEdge.Top, ScreenEdge.Bottom];

    private static readonly MonitorArrangement[] Arrangements = [MonitorArrangement.Right, MonitorArrangement.Left];

    public SettingsDialog(DragOutSettings settings, MonitorArrangement arrangement, bool followForeground, bool remoteHelperConnected)
    {
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height - 20; // never taller than the screen; content scrolls
        ArrangementBox.SelectedIndex = Math.Max(0, Array.IndexOf(Arrangements, arrangement));
        FollowBox.IsChecked = followForeground;
        RemoteHelperStatus.Text = remoteHelperConnected
            ? "✓ Hilfsprogramm auf dem verbundenen Remote-PC aktiv."
            : "Kein Hilfsprogramm auf dem Remote-PC gefunden (oder nicht verbunden) – siehe unten.";
        RemoteHelperStatus.Foreground = remoteHelperConnected ? Brushes.ForestGreen : Brushes.Gray;
        UpdateHelperState();
        var s = settings.Normalized();
        EnabledBox.IsChecked = s.Enabled;
        EdgeBox.SelectedIndex = Array.IndexOf(Edges, s.Edge);
        SizeSlider.Value = s.Size;
        DelaySlider.Value = s.ArmDelayMs;
        UpdateEnabled();
    }

    public DragOutSettings? Result { get; private set; }

    public bool FollowForeground { get; private set; }

    public MonitorArrangement Arrangement { get; private set; }

    private void UpdateHelperState()
    {
        var installed = AgentSetup.IsInstalled;
        HelperButton.Content = installed ? "Von diesem PC entfernen" : "Auf diesem PC einrichten";
        LocalHelperStatus.Text = installed
            ? AgentSetup.IsRunning ? "✓ Auf diesem PC eingerichtet und aktiv" : "Eingerichtet, läuft aber gerade nicht"
            : "Auf diesem PC nicht eingerichtet";
        LocalHelperStatus.Foreground = installed && AgentSetup.IsRunning ? Brushes.ForestGreen : Brushes.Gray;
    }

    private void OnToggleHelper(object sender, RoutedEventArgs e)
    {
        try
        {
            if (AgentSetup.IsInstalled)
                AgentSetup.Uninstall();
            else
                AgentSetup.Install();
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            MessageBox.Show(this, "Das Hilfsprogramm konnte nicht eingerichtet werden:\n" + ex.Message, "Einstellungen",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        UpdateHelperState();
    }

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
        FollowForeground = FollowBox.IsChecked == true;
        Arrangement = Arrangements[Math.Max(0, ArrangementBox.SelectedIndex)];
        DialogResult = true;
    }
}
