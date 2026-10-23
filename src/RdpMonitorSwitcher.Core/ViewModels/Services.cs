using RdpMonitorSwitcher.Core.Models;
using RdpMonitorSwitcher.Core.Rdp;
using RdpMonitorSwitcher.Core.Services;

namespace RdpMonitorSwitcher.Core.ViewModels;

/// <param name="FritzBoxPassword">Only set when the user entered/changed it in the dialog.</param>
public sealed record ConnectRequest(ConnectionProfile Profile, string Password, string? FritzBoxPassword = null);

/// <summary>UI dialogs; implemented by the WPF app.</summary>
public interface IDialogService
{
    /// <summary>Shows the connection dialog. Returns null if cancelled.</summary>
    ConnectRequest? ShowConnectDialog(ConnectionProfile profile, string? storedPassword, IReadOnlyList<ConnectionProfile> recent);

    /// <summary>Asks the user about an untrusted certificate. May be called from any thread.</summary>
    CertificateDecision ConfirmCertificate(CertificateInfo certificate);

    void ShowError(string title, string message);
}

/// <summary>Opens and closes the separate monitor windows.</summary>
public interface IWindowService
{
    void ShowMonitorWindow(MonitorViewModel view);

    void CloseMonitorWindow(MonitorViewModel view);

    void ActivateMonitorWindow(MonitorViewModel view);

    /// <summary>Restores and raises the window above other windows without taking the keyboard focus.</summary>
    void RaiseMonitorWindow(MonitorViewModel view);
}

/// <summary>Provides local values such as DPI and keyboard layout at connect time.</summary>
public interface ILocalEnvironmentProvider
{
    LocalEnvironment GetEnvironment(RemoteScaling remoteScaling);
}

/// <summary>Releases pressed keys / buttons before the view changes, so nothing gets stuck remotely.</summary>
public interface IInputResetter
{
    void ReleaseAll();
}
