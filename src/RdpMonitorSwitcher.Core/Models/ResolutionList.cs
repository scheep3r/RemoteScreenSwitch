using System.Globalization;

namespace RdpMonitorSwitcher.Core.Models;

/// <summary>Parses and formats lists like "1920x1080, 2560x1440".</summary>
public static class ResolutionList
{
    private static readonly char[] Separators = [',', ';', ' ', '\t', '\r', '\n'];

    public static bool TryParse(string? text, out List<PixelSize> sizes, out string? error)
    {
        sizes = [];
        error = null;
        var parts = (text ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            error = "Bitte mindestens eine Auflösung angeben, z. B. 1920x1080.";
            return false;
        }
        foreach (var part in parts)
        {
            var xy = part.Split(['x', 'X', '×', '*']);
            if (xy.Length != 2 ||
                !int.TryParse(xy[0], NumberStyles.None, CultureInfo.InvariantCulture, out var w) ||
                !int.TryParse(xy[1], NumberStyles.None, CultureInfo.InvariantCulture, out var h))
            {
                error = $"„{part}“ ist keine gültige Auflösung (Format: 1920x1080).";
                return false;
            }
            if (w is < MonitorLayout.MinMonitorWidth or > MonitorLayout.MaxMonitorWidth ||
                h is < MonitorLayout.MinMonitorHeight or > MonitorLayout.MaxMonitorHeight)
            {
                error = $"{w}x{h}: Breite und Höhe müssen zwischen 200 und 8192 liegen.";
                return false;
            }
            sizes.Add(new PixelSize(w, h));
        }
        if (sizes.Count > MonitorLayout.MaxMonitors)
        {
            error = $"Höchstens {MonitorLayout.MaxMonitors} Monitore.";
            return false;
        }
        return true;
    }

    public static string Format(IEnumerable<PixelSize> sizes) =>
        string.Join(", ", sizes.Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.Width}x{s.Height}")));
}
