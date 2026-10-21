namespace RdpMonitorSwitcher.Core.Configuration;

/// <summary>Secure password storage (Windows Credential Manager in the app).</summary>
public interface ICredentialStore
{
    string? TryGetPassword(string key);

    void SavePassword(string key, string userName, string password);

    void DeletePassword(string key);
}
