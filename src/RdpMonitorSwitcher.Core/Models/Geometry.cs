namespace RdpMonitorSwitcher.Core.Models;

/// <summary>A size in device pixels.</summary>
public readonly record struct PixelSize(int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public override string ToString() => $"{Width}×{Height}";
}

/// <summary>A point in device pixels.</summary>
public readonly record struct PixelPoint(int X, int Y);

/// <summary>A rectangle in device pixels; Right/Bottom are exclusive.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public PixelSize Size => new(Width, Height);

    public PixelRect Intersect(PixelRect other)
    {
        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right <= left || bottom <= top ? default : new PixelRect(left, top, right - left, bottom - top);
    }

    public bool Contains(PixelPoint p) => p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;
}

/// <summary>A rectangle in floating point units (device-independent pixels of a view).</summary>
public readonly record struct ViewRect(double X, double Y, double Width, double Height);
