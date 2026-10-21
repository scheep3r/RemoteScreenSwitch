using RdpMonitorSwitcher.Core.Models;

namespace RdpMonitorSwitcher.Core.Services;

/// <summary>Tracks which remote monitor a view shows; wraps around at both ends.</summary>
public sealed class MonitorNavigator
{
    private IReadOnlyList<RemoteMonitor> _monitors = [];

    public event EventHandler? SelectionChanged;

    public IReadOnlyList<RemoteMonitor> Monitors => _monitors;

    public int SelectedIndex { get; private set; }

    public RemoteMonitor? Selected => _monitors.Count == 0 ? null : _monitors[SelectedIndex];

    /// <summary>Replaces the monitor list and keeps the selection if it still exists.</summary>
    public void SetMonitors(IReadOnlyList<RemoteMonitor> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        var previous = Selected;
        _monitors = monitors;
        SelectedIndex = monitors.Count == 0 ? 0 : Math.Clamp(SelectedIndex, 0, monitors.Count - 1);
        if (previous != Selected)
            SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <returns>False with an explanation if the monitor does not exist.</returns>
    public bool TrySelect(int index, out string? error)
    {
        if (_monitors.Count == 0)
        {
            error = "Es ist keine Verbindung aktiv.";
            return false;
        }
        if (index < 0 || index >= _monitors.Count)
        {
            error = _monitors.Count == 1
                ? $"Monitor {index + 1} ist nicht verfügbar – die Sitzung hat nur einen Monitor."
                : $"Monitor {index + 1} ist nicht verfügbar – die Sitzung hat {_monitors.Count} Monitore.";
            return false;
        }
        error = null;
        if (index != SelectedIndex)
        {
            SelectedIndex = index;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        return true;
    }

    public void Next() => Step(+1);

    public void Previous() => Step(-1);

    private void Step(int delta)
    {
        if (_monitors.Count < 2)
            return;
        SelectedIndex = ((SelectedIndex + delta) % _monitors.Count + _monitors.Count) % _monitors.Count;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}
