using RdpMonitorSwitcher.Core.Models;

namespace RdpMonitorSwitcher.Core.Services;

/// <summary>Maps between a local view and one remote monitor.</summary>
public static class ViewportMapper
{
    /// <summary>
    /// Where the monitor image is drawn inside a view of <paramref name="viewSize"/> (same units as the result).
    /// Fit keeps the aspect ratio and centres the image; Original draws 1:1 (in device pixels) from the top-left.
    /// </summary>
    /// <param name="pixelsPerUnit">Device pixels per view unit (the DPI scale, e.g. 1.5 at 150 %).</param>
    public static ViewRect ComputeImageRect(PixelSize monitorSize, double viewWidth, double viewHeight,
        ScalingMode mode, double pixelsPerUnit)
    {
        if (monitorSize.IsEmpty || viewWidth <= 0 || viewHeight <= 0 || pixelsPerUnit <= 0)
            return default;

        if (mode == ScalingMode.Original)
        {
            var w = monitorSize.Width / pixelsPerUnit;
            var h = monitorSize.Height / pixelsPerUnit;
            return new ViewRect(Math.Max(0, (viewWidth - w) / 2), Math.Max(0, (viewHeight - h) / 2), w, h);
        }

        var scale = Math.Min(viewWidth / monitorSize.Width, viewHeight / monitorSize.Height);
        var width = monitorSize.Width * scale;
        var height = monitorSize.Height * scale;
        return new ViewRect((viewWidth - width) / 2, (viewHeight - height) / 2, width, height);
    }

    /// <summary>
    /// Converts a point relative to the drawn image (0..imageWidth, 0..imageHeight) into
    /// remote desktop coordinates, clamped to the monitor.
    /// </summary>
    public static PixelPoint ToDesktop(RemoteMonitor monitor, double imageX, double imageY,
        double imageWidth, double imageHeight)
    {
        var bounds = monitor.Bounds;
        if (imageWidth <= 0 || imageHeight <= 0)
            return new PixelPoint(bounds.X, bounds.Y);

        var x = (int)Math.Floor(imageX / imageWidth * bounds.Width);
        var y = (int)Math.Floor(imageY / imageHeight * bounds.Height);
        x = Math.Clamp(x, 0, bounds.Width - 1);
        y = Math.Clamp(y, 0, bounds.Height - 1);
        return new PixelPoint(bounds.X + x, bounds.Y + y);
    }

    /// <summary>The desired remote monitor size for a view in FitToWindow mode.</summary>
    public static PixelSize FitSize(double viewWidth, double viewHeight, double pixelsPerUnit) =>
        MonitorLayout.Normalize(new PixelSize(
            (int)Math.Round(viewWidth * pixelsPerUnit),
            (int)Math.Round(viewHeight * pixelsPerUnit)));
}
