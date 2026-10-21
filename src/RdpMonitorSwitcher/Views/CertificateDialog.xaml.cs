using System.Windows;
using RdpMonitorSwitcher.Core.Rdp;

namespace RdpMonitorSwitcher.Views;

public partial class CertificateDialog : Window
{
    public CertificateDialog(CertificateInfo info)
    {
        InitializeComponent();
        if (info.HasChanged)
        {
            Headline.Text = "Das Zertifikat des Remote-PCs hat sich geändert!";
            Explanation.Text = "Das kann nach einer Neuinstallation oder einem Zertifikatswechsel normal sein. " +
                               "Wenn du dir nicht sicher bist, brich ab – es könnte sich jemand als dein PC ausgeben.";
        }
        else
        {
            Headline.Text = "Die Identität des Remote-PCs kann nicht automatisch bestätigt werden.";
            Explanation.Text = "Windows-PCs verwenden für Remotedesktop standardmäßig ein selbst ausgestelltes Zertifikat. " +
                               "Das ist bei der ersten Verbindung normal (auch über Tailscale). " +
                               "Wähle „Immer vertrauen“, um diese Meldung künftig nicht mehr zu sehen.";
        }
        HostText.Text = $"{info.Host}:{info.Port}";
        SubjectText.Text = string.IsNullOrEmpty(info.Subject) ? info.CommonName : info.Subject;
        IssuerText.Text = info.Issuer;
        FingerprintText.Text = info.Fingerprint;
        OldFingerprintText.Text = info.PreviousFingerprint ?? "";
        OldLabel.Visibility = OldFingerprintText.Visibility = info.HasChanged ? Visibility.Visible : Visibility.Collapsed;
    }

    public CertificateDecision Decision { get; private set; } = CertificateDecision.Reject;

    private void OnAlways(object sender, RoutedEventArgs e)
    {
        Decision = CertificateDecision.AcceptPermanently;
        DialogResult = true;
    }

    private void OnOnce(object sender, RoutedEventArgs e)
    {
        Decision = CertificateDecision.AcceptOnce;
        DialogResult = true;
    }
}
