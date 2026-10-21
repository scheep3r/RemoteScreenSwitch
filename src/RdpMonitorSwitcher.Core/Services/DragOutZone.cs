namespace RdpMonitorSwitcher.Core.Services;

public enum ScreenEdge
{
    Left,
    Right,
    Top,
    Bottom,
}

/// <summary>Settings of the automatic drag-out zone.</summary>
public sealed class DragOutSettings
{
    public bool Enabled { get; set; } = true;

    public ScreenEdge Edge { get; set; } = ScreenEdge.Right;

    /// <summary>Width of the zone in device-independent pixels.</summary>
    public double Size { get; set; } = 24;

    /// <summary>The mouse button must be held at least this long before the zone becomes active.</summary>
    public int ArmDelayMs { get; set; } = 300;

    /// <summary>... and the mouse must have moved at least this far (DIP) since the button went down.</summary>
    public double ArmDistance { get; set; } = 12;

    public DragOutSettings Normalized() => new()
    {
        Enabled = Enabled,
        Edge = Enum.IsDefined(Edge) ? Edge : ScreenEdge.Right,
        Size = Math.Clamp(Size, 4, 200),
        ArmDelayMs = Math.Clamp(ArmDelayMs, 0, 5000),
        ArmDistance = Math.Clamp(ArmDistance, 0, 500),
    };
}

/// <summary>
/// Decides when a drag inside a remote view should automatically turn into "fetch files and
/// continue dragging locally": the left button is held long enough, the mouse has moved (so it is
/// a drag, not a click) and it reaches the configured edge zone. Fires once per button press.
/// </summary>
public sealed class DragOutTrigger
{
    private bool _pressed;
    private bool _fired;
    private long _downTicks;
    private double _downX;
    private double _downY;
    private bool _moved;

    public DragOutTrigger(DragOutSettings settings)
    {
        Settings = settings.Normalized();
    }

    public DragOutSettings Settings { get; set; }

    /// <summary>True while a drag is in progress and the zone may fire (used to show the zone).</summary>
    public bool IsArmed(long nowMs) =>
        Settings.Enabled && _pressed && !_fired && _moved && nowMs - _downTicks >= Settings.ArmDelayMs;

    public void ButtonDown(long nowMs, double x, double y)
    {
        _pressed = true;
        _fired = false;
        _moved = false;
        _downTicks = nowMs;
        _downX = x;
        _downY = y;
    }

    public void ButtonUp() => _pressed = false;

    /// <returns>True exactly once when the drag reaches the zone.</returns>
    public bool Move(long nowMs, double x, double y, double viewWidth, double viewHeight)
    {
        if (!_pressed || _fired || !Settings.Enabled)
            return false;
        if (!_moved && Math.Abs(x - _downX) + Math.Abs(y - _downY) >= Settings.ArmDistance)
            _moved = true;
        if (!IsArmed(nowMs) || !IsInZone(x, y, viewWidth, viewHeight))
            return false;
        _fired = true;
        return true;
    }

    public bool IsInZone(double x, double y, double viewWidth, double viewHeight)
    {
        var size = Settings.Size;
        return Settings.Edge switch
        {
            ScreenEdge.Left => x <= size,
            ScreenEdge.Right => x >= viewWidth - size,
            ScreenEdge.Top => y <= size,
            _ => y >= viewHeight - size,
        };
    }
}
