namespace RdpMonitorSwitcher.Core.Models;

/// <summary>One virtual monitor of the remote session.</summary>
/// <param name="Index">Zero-based position in the layout.</param>
/// <param name="Bounds">Position inside the remote desktop (frame buffer) in pixels.</param>
public sealed record RemoteMonitor(int Index, PixelRect Bounds, bool IsPrimary)
{
    public int Number => Index + 1;
    public string Name => IsPrimary ? $"Monitor {Number} (Haupt)" : $"Monitor {Number}";
    public string Resolution => Bounds.Size.ToString();
}

/// <summary>Where the other remote monitors sit relative to monitor 1 (the primary).</summary>
public enum MonitorArrangement
{
    /// <summary>Monitor 2, 3, … continue to the right of monitor 1.</summary>
    Right,

    /// <summary>Monitor 2, 3, … continue to the left of monitor 1.</summary>
    Left,
}

/// <summary>
/// The monitor layout declared to the server. Monitors are placed in one row without gaps,
/// top-aligned, with monitor 1 as primary; the others continue to its right or left. Bounds are
/// frame buffer positions (always ≥ 0); the bridge shifts them so the primary is at (0,0) as RDP
/// requires, with monitors on the left at negative positions.
/// </summary>
public sealed class MonitorLayout : IEquatable<MonitorLayout>
{
    public const int MaxMonitors = 16;
    public const int MinMonitorWidth = 200;
    public const int MaxMonitorWidth = 8192;
    public const int MinMonitorHeight = 200;
    public const int MaxMonitorHeight = 8192;

    private MonitorLayout(IReadOnlyList<RemoteMonitor> monitors)
    {
        Monitors = monitors;
        DesktopSize = new PixelSize(monitors.Max(m => m.Bounds.Right), monitors.Max(m => m.Bounds.Bottom));
    }

    public IReadOnlyList<RemoteMonitor> Monitors { get; }

    /// <summary>Bounding box of all monitors = size of the remote frame buffer.</summary>
    public PixelSize DesktopSize { get; }

    public int Count => Monitors.Count;

    /// <summary>Creates a row layout from the given monitor sizes after normalising them.</summary>
    public static MonitorLayout CreateRow(IEnumerable<PixelSize> sizes, MonitorArrangement arrangement = MonitorArrangement.Right)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        var list = sizes.Select(Normalize).ToList();
        if (list.Count == 0)
            throw new ArgumentException("At least one monitor is required.", nameof(sizes));
        if (list.Count > MaxMonitors)
            throw new ArgumentException($"At most {MaxMonitors} monitors are supported.", nameof(sizes));

        var bounds = new PixelRect[list.Count];
        var x = 0;
        // Fill the row from the left: monitor 1 first (Right) or last (Left).
        foreach (var i in arrangement == MonitorArrangement.Left ? Enumerable.Range(0, list.Count).Reverse() : Enumerable.Range(0, list.Count))
        {
            bounds[i] = new PixelRect(x, 0, list[i].Width, list[i].Height);
            x += list[i].Width;
        }
        return new MonitorLayout(bounds.Select((b, i) => new RemoteMonitor(i, b, i == 0)).ToList());
    }

    public static MonitorLayout CreateUniform(int count, PixelSize size, MonitorArrangement arrangement = MonitorArrangement.Right) =>
        CreateRow(Enumerable.Repeat(size, count), arrangement);

    /// <summary>The monitors from left to right.</summary>
    public IEnumerable<RemoteMonitor> SpatialOrder => Monitors.OrderBy(m => m.Bounds.X);

    /// <summary>
    /// Clamps a size to the limits of MS-RDPEDISP and rounds the width down to an even number
    /// (the protocol forbids odd widths).
    /// </summary>
    public static PixelSize Normalize(PixelSize size)
    {
        var width = Math.Clamp(size.Width, MinMonitorWidth, MaxMonitorWidth);
        width -= width % 2;
        var height = Math.Clamp(size.Height, MinMonitorHeight, MaxMonitorHeight);
        return new PixelSize(width, height);
    }

    /// <summary>The monitor containing the point, or the nearest one.</summary>
    public RemoteMonitor FindMonitorAt(PixelPoint point) =>
        Monitors.MinBy(m => DistanceSquared(m.Bounds, point))!;

    private static long DistanceSquared(PixelRect r, PixelPoint p)
    {
        long dx = p.X < r.X ? r.X - p.X : p.X >= r.Right ? p.X - r.Right + 1 : 0;
        long dy = p.Y < r.Y ? r.Y - p.Y : p.Y >= r.Bottom ? p.Y - r.Bottom + 1 : 0;
        return dx * dx + dy * dy;
    }

    public IReadOnlyList<PixelSize> Sizes => Monitors.Select(m => m.Bounds.Size).ToList();

    public bool Equals(MonitorLayout? other) =>
        other is not null && Monitors.Select(m => m.Bounds).SequenceEqual(other.Monitors.Select(m => m.Bounds));

    public override bool Equals(object? obj) => Equals(obj as MonitorLayout);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var m in Monitors)
            hash.Add(m.Bounds);
        return hash.ToHashCode();
    }

    public override string ToString() => string.Join(" | ", Monitors.Select(m => m.Resolution));
}
