namespace RdpMonitorSwitcher.Core.Models;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Disconnecting,
}

/// <summary>Why a session ended. Values match RB_REASON_* in rdpbridge.h.</summary>
public enum DisconnectReason
{
    None = 0,
    User = 1,
    Network = 2,
    Authentication = 3,
    Certificate = 4,
    ServerEnded = 5,
    Replaced = 6,
    Other = 7,
}

public static class DisconnectReasonText
{
    public static string Describe(DisconnectReason reason, string? detail) => reason switch
    {
        DisconnectReason.None or DisconnectReason.User => "Getrennt.",
        DisconnectReason.Network => "Die Verbindung zum Remote-PC ist unterbrochen oder er ist nicht erreichbar.",
        DisconnectReason.Authentication => "Anmeldung fehlgeschlagen. Bitte Benutzername und Passwort prüfen.",
        DisconnectReason.Certificate => "Das Zertifikat des Remote-PCs wurde abgelehnt.",
        DisconnectReason.ServerEnded => "Die Sitzung wurde vom Remote-PC beendet (Abmeldung, Zeitüberschreitung oder Administrator).",
        DisconnectReason.Replaced => "Ein anderer Client hat die Sitzung übernommen.",
        _ => "Die Verbindung wurde mit einem Fehler beendet.",
    } + (string.IsNullOrWhiteSpace(detail) ? "" : $" ({detail})");
}
