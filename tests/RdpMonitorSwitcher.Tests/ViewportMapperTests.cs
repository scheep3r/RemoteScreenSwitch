using RdpMonitorSwitcher.Core.Models;
using RdpMonitorSwitcher.Core.Services;

namespace RdpMonitorSwitcher.Tests;

public class ViewportMapperTests
{
    private static readonly MonitorLayout Layout = MonitorLayout.CreateRow([new(1920, 1080), new(2560, 1440)]);

    [Fact]
    public void Fit_LetterboxesWiderView()
    {
        var rect = ViewportMapper.ComputeImageRect(new PixelSize(1920, 1080), 2000, 1080, ScalingMode.Fit, 1.0);

        Assert.Equal(1920, rect.Width, 3);
        Assert.Equal(1080, rect.Height, 3);
        Assert.Equal(40, rect.X, 3);
        Assert.Equal(0, rect.Y, 3);
    }

    [Fact]
    public void Fit_ScalesDownKeepingAspectRatio()
    {
        var rect = ViewportMapper.ComputeImageRect(new PixelSize(2560, 1440), 1280, 1000, ScalingMode.Fit, 1.0);

        Assert.Equal(1280, rect.Width, 3);
        Assert.Equal(720, rect.Height, 3);
        Assert.Equal(140, rect.Y, 3);
    }

    [Fact]
    public void Original_UsesDevicePixels()
    {
        var rect = ViewportMapper.ComputeImageRect(new PixelSize(1920, 1080), 800, 600, ScalingMode.Original, 1.5);

        Assert.Equal(1280, rect.Width, 3); // 1920 px at 150 % = 1280 DIP
        Assert.Equal(720, rect.Height, 3);
        Assert.Equal(0, rect.X, 3);
    }

    [Fact]
    public void ToDesktop_AddsMonitorOffset()
    {
        var second = Layout.Monitors[1];

        var p = ViewportMapper.ToDesktop(second, 640, 360, 1280, 720);

        Assert.Equal(new PixelPoint(1920 + 1280, 720), p);
    }

    [Fact]
    public void ToDesktop_ClampsToMonitor()
    {
        var first = Layout.Monitors[0];

        Assert.Equal(new PixelPoint(0, 0), ViewportMapper.ToDesktop(first, -50, -10, 960, 540));
        Assert.Equal(new PixelPoint(1919, 1079), ViewportMapper.ToDesktop(first, 5000, 5000, 960, 540));
    }

    [Fact]
    public void ToDesktop_NeverLeaksIntoNeighbourMonitor()
    {
        var first = Layout.Monitors[0];

        var p = ViewportMapper.ToDesktop(first, 960, 100, 960, 540); // right edge of the image

        Assert.True(first.Bounds.Contains(p));
    }

    [Fact]
    public void FitSize_ConvertsToDevicePixelsAndNormalizes()
    {
        Assert.Equal(new PixelSize(1920, 1050), ViewportMapper.FitSize(1280, 700, 1.5));
        Assert.Equal(new PixelSize(1000, 600), ViewportMapper.FitSize(1001, 600, 1.0)); // even width
    }
}
