using RdpMonitorSwitcher.Core.Models;

namespace RdpMonitorSwitcher.Tests;

public class MonitorLayoutTests
{
    [Fact]
    public void CreateRow_PlacesMonitorsLeftToRightWithPrimaryAtOrigin()
    {
        var layout = MonitorLayout.CreateRow([new(1920, 1080), new(2560, 1440), new(1280, 1024)]);

        Assert.Equal(3, layout.Count);
        Assert.Equal(new PixelRect(0, 0, 1920, 1080), layout.Monitors[0].Bounds);
        Assert.Equal(new PixelRect(1920, 0, 2560, 1440), layout.Monitors[1].Bounds);
        Assert.Equal(new PixelRect(4480, 0, 1280, 1024), layout.Monitors[2].Bounds);
        Assert.True(layout.Monitors[0].IsPrimary);
        Assert.False(layout.Monitors[1].IsPrimary);
        Assert.Equal(new PixelSize(5760, 1440), layout.DesktopSize);
    }

    [Fact]
    public void CreateRow_MonitorsTouchWithoutGapsOrOverlap()
    {
        var layout = MonitorLayout.CreateUniform(4, new PixelSize(1600, 900));

        for (var i = 1; i < layout.Count; i++)
            Assert.Equal(layout.Monitors[i - 1].Bounds.Right, layout.Monitors[i].Bounds.X);
    }

    [Theory]
    [InlineData(1921, 1080, 1920, 1080)] // odd widths are not allowed by MS-RDPEDISP
    [InlineData(100, 50, 200, 200)]      // below the minimum
    [InlineData(9000, 9000, 8192, 8192)] // above the maximum
    [InlineData(1366, 768, 1366, 768)]
    public void Normalize_ClampsAndMakesWidthEven(int w, int h, int expectedW, int expectedH)
    {
        Assert.Equal(new PixelSize(expectedW, expectedH), MonitorLayout.Normalize(new PixelSize(w, h)));
    }

    [Fact]
    public void CreateRow_RejectsEmptyAndTooManyMonitors()
    {
        Assert.Throws<ArgumentException>(() => MonitorLayout.CreateRow([]));
        Assert.Throws<ArgumentException>(() => MonitorLayout.CreateUniform(17, new PixelSize(800, 600)));
    }

    [Fact]
    public void Equality_ComparesBounds()
    {
        var a = MonitorLayout.CreateUniform(2, new PixelSize(1920, 1080));
        var b = MonitorLayout.CreateRow([new(1920, 1080), new(1920, 1080)]);
        var c = MonitorLayout.CreateUniform(3, new PixelSize(1920, 1080));

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void RemoteMonitor_NamesAreUserFriendly()
    {
        var layout = MonitorLayout.CreateUniform(2, new PixelSize(1920, 1080));

        Assert.Equal("Monitor 1 (Haupt)", layout.Monitors[0].Name);
        Assert.Equal("Monitor 2", layout.Monitors[1].Name);
        Assert.Equal("1920×1080", layout.Monitors[1].Resolution);
    }

    [Fact]
    public void PixelRect_Intersect()
    {
        var a = new PixelRect(0, 0, 100, 100);
        Assert.Equal(new PixelRect(50, 50, 50, 50), a.Intersect(new PixelRect(50, 50, 100, 100)));
        Assert.True(a.Intersect(new PixelRect(100, 0, 10, 10)).IsEmpty);
    }
}
