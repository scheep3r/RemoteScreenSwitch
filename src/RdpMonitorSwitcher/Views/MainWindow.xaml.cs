using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Navigation;
using RdpMonitorSwitcher.Core.Services;
using RdpMonitorSwitcher.Services;

namespace RdpMonitorSwitcher.Views;

public partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly WindowChrome _chrome;

    internal MainWindow(AppServices services)
    {
        InitializeComponent();
        _services = services;
        DataContext = services.Main;
        Viewport.Attach(services, services.Main.MainView);
        _chrome = new WindowChrome(this, services.Main.MainView, Toolbar, HotZone);
        WindowPlacementHelper.Restore(this, services.Main.Settings.MainWindow);
        SourceInitialized += (_, _) => services.Clipboard.Attach(this);
        Loaded += (_, _) => Viewport.Focus();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel)
            return;
        _services.Main.Settings.MainWindow = WindowPlacementHelper.Capture(this);
        _services.Main.CloseAllMonitorWindowsCommand.Execute(null);
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(_services.Main.Settings.DragOut) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } settings)
            _services.Main.UpdateDragOutSettings(settings);
    }

    private void OnCheckUpdates(object sender, RoutedEventArgs e) => _ = _services.Updates?.CheckAsync(interactive: true);

    private void OnOpenLink(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void OnHelp(object sender, RoutedEventArgs e)
    {
        var text = new StringBuilder("Tastenkürzel (wenn der Remote-Bildschirm den Fokus hat):\n\n");
        foreach (var (keys, description) in ShortcutMap.Descriptions)
            text.AppendLine($"{keys}\t{description}");
        text.AppendLine();
        text.AppendLine("Alle anderen Tasten – auch Windows-Taste und Alt+Tab – werden an den Remote-PC gesendet.");
        text.AppendLine("Zum lokalen Wechseln außerhalb des Fensters klicken oder Strg+Alt+Enter (Vollbild verlassen) nutzen.");
        text.AppendLine();
        text.AppendLine($"Version {UpdateChecker.FormatVersion(UpdateService.CurrentVersion)}");
        text.AppendLine("Diese App wurde mit KI entwickelt (Claude Code von Anthropic) und nutzt FreeRDP (Apache 2.0).");
        MessageBox.Show(this, text.ToString(), "Tastenkürzel", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
