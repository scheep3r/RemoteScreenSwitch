using RdpMonitorSwitcher.Core.Models;
using RdpMonitorSwitcher.Core.Services;

namespace RdpMonitorSwitcher.Core.Configuration;

public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>Persisted, non-sensitive application settings.</summary>
public sealed class AppSettings
{
    public List<ConnectionProfile> Profiles { get; set; } = [];

    public string? LastProfileKey { get; set; }

    public bool ToolbarVisible { get; set; } = true;

    public WindowPlacement? MainWindow { get; set; }

    /// <summary>Last positions of the separate monitor windows, by monitor index.</summary>
    public Dictionary<int, WindowPlacement> MonitorWindows { get; set; } = [];

    /// <summary>Automatic "drag files out of the remote PC" zone.</summary>
    public DragOutSettings DragOut { get; set; } = new();

    /// <summary>Show the monitor of the window that comes to the front (needs the helper on the remote PC).</summary>
    public bool FollowRemoteForeground { get; set; } = true;

    /// <summary>Whether remote monitor 2, 3, … continue to the right or the left of monitor 1.</summary>
    public MonitorArrangement MonitorArrangement { get; set; } = MonitorArrangement.Right;

    public ConnectionProfile? FindProfile(string? key) =>
        key is null ? null : Profiles.FirstOrDefault(p => p.Key == key);

    /// <summary>Inserts or replaces a profile (matched by host, port and user) and makes it the last used.</summary>
    public void Remember(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var copy = profile.Clone();
        Profiles.RemoveAll(p => p.Key == copy.Key);
        Profiles.Insert(0, copy);
        LastProfileKey = copy.Key;
    }
}
