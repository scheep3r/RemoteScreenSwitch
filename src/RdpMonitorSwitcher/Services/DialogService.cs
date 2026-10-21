using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using RdpMonitorSwitcher.Core.Configuration;
using RdpMonitorSwitcher.Core.Models;
using RdpMonitorSwitcher.Core.Rdp;
using RdpMonitorSwitcher.Core.ViewModels;
using RdpMonitorSwitcher.Views;

namespace RdpMonitorSwitcher.Services;

internal sealed class DialogService(Dispatcher dispatcher, ICredentialStore credentials, ILogger<DialogService> logger) : IDialogService
{
    public ConnectRequest? ShowConnectDialog(ConnectionProfile profile, string? storedPassword, IReadOnlyList<ConnectionProfile> recent)
    {
        var dialog = new ConnectDialog(profile, storedPassword, recent, credentials) { Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    /// <summary>Called on a FreeRDP thread; shows the dialog on the UI thread and waits for the answer.</summary>
    public CertificateDecision ConfirmCertificate(CertificateInfo certificate)
    {
        try
        {
            return dispatcher.Invoke(() =>
            {
                var dialog = new CertificateDialog(certificate) { Owner = Application.Current.MainWindow };
                return dialog.ShowDialog() == true ? dialog.Decision : CertificateDecision.Reject;
            });
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Certificate prompt could not be shown; rejecting");
            return CertificateDecision.Reject;
        }
    }

    public void ShowError(string title, string message) =>
        MessageBox.Show(Application.Current.MainWindow!, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}
