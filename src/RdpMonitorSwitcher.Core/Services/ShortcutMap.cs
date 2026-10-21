namespace RdpMonitorSwitcher.Core.Services;

public enum AppCommandKind
{
    None,
    PreviousMonitor,
    NextMonitor,
    SelectMonitor,
    ToggleFullscreen,
    ToggleToolbar,
    SendCtrlAltDel,
    OpenAllMonitorWindows,
    PullRemoteFiles,
}

public readonly record struct AppCommand(AppCommandKind Kind, int MonitorIndex = -1)
{
    public static readonly AppCommand None = new(AppCommandKind.None);
}

/// <summary>Windows virtual-key codes used by the shortcut map.</summary>
public static class VirtualKeys
{
    public const int Back = 0x08;
    public const int Enter = 0x0D;
    public const int Shift = 0x10;
    public const int Control = 0x11;
    public const int Menu = 0x12; // Alt
    public const int Pause = 0x13;
    public const int Cancel = 0x03; // Ctrl+Break
    public const int End = 0x23;
    public const int Left = 0x25;
    public const int Right = 0x27;
    public const int D0 = 0x30;
    public const int D1 = 0x31;
    public const int D9 = 0x39;
    public const int D = 0x44;
    public const int M = 0x4D;
    public const int T = 0x54;
    public const int LWin = 0x5B;
    public const int RWin = 0x5C;
    public const int NumPad1 = 0x61;
    public const int NumPad9 = 0x69;
    public const int LShift = 0xA0;
    public const int RShift = 0xA1;
    public const int LControl = 0xA2;
    public const int RControl = 0xA3;
    public const int LMenu = 0xA4;
    public const int RMenu = 0xA5;
}

/// <summary>
/// Local shortcuts while the remote view has the keyboard. All use Ctrl+Alt so that ordinary
/// key combinations reach the remote session unchanged.
///   Ctrl+Alt+Left / Right   previous / next monitor
///   Ctrl+Alt+1..9           monitor 1..9
///   Ctrl+Alt+Enter / Break  fullscreen
///   Ctrl+Alt+T              show / hide toolbar
///   Ctrl+Alt+M              one window per monitor
///   Ctrl+Alt+End            send Ctrl+Alt+Del to the remote PC
///   Ctrl+Alt+D              fetch the selected / dragged remote files to the local PC
/// </summary>
public static class ShortcutMap
{
    public static AppCommand Resolve(int virtualKey, bool ctrl, bool alt, bool shift)
    {
        if (!ctrl || !alt || shift)
            return AppCommand.None;

        return virtualKey switch
        {
            VirtualKeys.Left => new AppCommand(AppCommandKind.PreviousMonitor),
            VirtualKeys.Right => new AppCommand(AppCommandKind.NextMonitor),
            >= VirtualKeys.D1 and <= VirtualKeys.D9 => new AppCommand(AppCommandKind.SelectMonitor, virtualKey - VirtualKeys.D1),
            >= VirtualKeys.NumPad1 and <= VirtualKeys.NumPad9 => new AppCommand(AppCommandKind.SelectMonitor, virtualKey - VirtualKeys.NumPad1),
            VirtualKeys.Enter or VirtualKeys.Cancel or VirtualKeys.Pause => new AppCommand(AppCommandKind.ToggleFullscreen),
            VirtualKeys.T => new AppCommand(AppCommandKind.ToggleToolbar),
            VirtualKeys.M => new AppCommand(AppCommandKind.OpenAllMonitorWindows),
            VirtualKeys.End => new AppCommand(AppCommandKind.SendCtrlAltDel),
            VirtualKeys.D => new AppCommand(AppCommandKind.PullRemoteFiles),
            _ => AppCommand.None,
        };
    }

    public static IReadOnlyList<(string Keys, string Description)> Descriptions { get; } =
    [
        ("Strg+Alt+← / →", "Vorheriger / nächster Monitor"),
        ("Strg+Alt+1 … 9", "Monitor direkt wählen"),
        ("Strg+Alt+Enter", "Vollbild ein/aus"),
        ("Strg+Alt+T", "Toolbar ein/aus"),
        ("Strg+Alt+M", "Jeden Monitor in eigenem Fenster öffnen"),
        ("Strg+Alt+Ende", "Strg+Alt+Entf an den Remote-PC senden"),
        ("Strg+Alt+D", "Markierte Remote-Dateien holen (beim Ziehen: lokal weiterziehen, sonst Zwischenablage)"),
    ];
}
