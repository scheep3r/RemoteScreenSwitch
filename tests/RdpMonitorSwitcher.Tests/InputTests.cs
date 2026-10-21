using RdpMonitorSwitcher.Core.Models;
using RdpMonitorSwitcher.Core.Rdp;
using RdpMonitorSwitcher.Core.Services;

namespace RdpMonitorSwitcher.Tests;

public class ShortcutMapTests
{
    [Theory]
    [InlineData(VirtualKeys.Left, AppCommandKind.PreviousMonitor)]
    [InlineData(VirtualKeys.Right, AppCommandKind.NextMonitor)]
    [InlineData(VirtualKeys.Enter, AppCommandKind.ToggleFullscreen)]
    [InlineData(VirtualKeys.Cancel, AppCommandKind.ToggleFullscreen)]
    [InlineData(VirtualKeys.T, AppCommandKind.ToggleToolbar)]
    [InlineData(VirtualKeys.M, AppCommandKind.OpenAllMonitorWindows)]
    [InlineData(VirtualKeys.End, AppCommandKind.SendCtrlAltDel)]
    [InlineData(VirtualKeys.D, AppCommandKind.PullRemoteFiles)]
    public void CtrlAlt_Combinations(int key, AppCommandKind expected)
    {
        Assert.Equal(expected, ShortcutMap.Resolve(key, ctrl: true, alt: true, shift: false).Kind);
    }

    [Theory]
    [InlineData(VirtualKeys.D1, 0)]
    [InlineData(VirtualKeys.D9, 8)]
    [InlineData(VirtualKeys.NumPad1 + 2, 2)]
    public void NumberKeys_SelectMonitor(int key, int index)
    {
        var command = ShortcutMap.Resolve(key, ctrl: true, alt: true, shift: false);

        Assert.Equal(AppCommandKind.SelectMonitor, command.Kind);
        Assert.Equal(index, command.MonitorIndex);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)] // Ctrl+Alt+Shift+… stays with the remote session
    public void OtherModifierCombinations_GoToRemote(bool ctrl, bool alt, bool shift)
    {
        Assert.Equal(AppCommand.None, ShortcutMap.Resolve(VirtualKeys.Left, ctrl, alt, shift));
    }

    [Fact]
    public void Ctrl_Alt_Zero_IsNotAMonitor()
    {
        Assert.Equal(AppCommand.None, ShortcutMap.Resolve(VirtualKeys.D0, true, true, false));
    }
}

public class KeyTranslatorTests
{
    [Fact]
    public void ExtendedKeys_GetExtendedFlag()
    {
        var t = new KeyTranslator();

        var events = t.Translate(0x4D, extended: true, down: true); // Right arrow

        Assert.Equal([new RdpKeyEvent(true, 0x14D)], events);
    }

    [Fact]
    public void NumLock_IsNotExtended()
    {
        var events = new KeyTranslator().Translate(0x45, extended: true, down: true);

        Assert.Equal([new RdpKeyEvent(true, 0x45)], events);
    }

    [Fact]
    public void Pause_IsSentAsCtrlNumLock_OnKeyDownOnly()
    {
        var t = new KeyTranslator();

        var down = t.Translate(0x45, extended: false, down: true);
        var up = t.Translate(0x45, extended: false, down: false);

        Assert.Equal(4, down.Count);
        Assert.Equal(new RdpKeyEvent(true, 0x1D), down[0]);
        Assert.Equal(new RdpKeyEvent(true, 0x45), down[1]);
        Assert.Empty(up);
        Assert.Empty(t.PressedKeys); // nothing left pressed
    }

    [Fact]
    public void RightShift_IsNotExtended()
    {
        Assert.Equal([new RdpKeyEvent(false, 0x36)], new KeyTranslator().Translate(0x36, extended: true, down: false));
    }

    [Fact]
    public void ReleaseAll_ReleasesEveryHeldKeyOnce()
    {
        var t = new KeyTranslator();
        t.Translate(0x1D, false, true); // Ctrl
        t.Translate(0x38, false, true); // Alt
        t.Translate(0x38, false, false);
        t.Translate(0x5B, true, true); // Win

        var released = t.ReleaseAll();

        Assert.Equal(2, released.Count);
        Assert.Contains(new RdpKeyEvent(false, 0x1D), released);
        Assert.Contains(new RdpKeyEvent(false, 0x15B), released);
        Assert.Empty(t.ReleaseAll());
    }

    [Fact]
    public void ScancodeZero_IsIgnored()
    {
        Assert.Empty(new KeyTranslator().Translate(0, false, true));
    }
}

public class MouseFlagsTests
{
    [Fact]
    public void WheelUp_IsPositive()
    {
        Assert.Equal([(ushort)(MouseFlags.Wheel | 120)], MouseFlags.WheelSteps(120, horizontal: false));
    }

    [Fact]
    public void WheelDown_UsesNineBitTwosComplement()
    {
        // -120 -> 0x188 (negative flag 0x100 set), as in FreeRDP's Windows client.
        Assert.Equal([(ushort)(MouseFlags.Wheel | 0x188)], MouseFlags.WheelSteps(-120, horizontal: false));
    }

    [Fact]
    public void LargeDeltas_AreSplit()
    {
        var steps = MouseFlags.WheelSteps(600, horizontal: true).ToList();

        Assert.Equal(3, steps.Count);
        Assert.All(steps, s => Assert.Equal(MouseFlags.HorizontalWheel, s & MouseFlags.HorizontalWheel));
        Assert.Equal(600, steps.Sum(s => s & 0xFF));
    }
}

public class LocalEnvironmentTests
{
    [Theory]
    [InlineData(1.0, RemoteScaling.MatchLocal, 100, 100)]
    [InlineData(1.25, RemoteScaling.MatchLocal, 125, 100)]
    [InlineData(1.5, RemoteScaling.MatchLocal, 150, 140)]
    [InlineData(2.0, RemoteScaling.MatchLocal, 200, 180)]
    [InlineData(1.5, RemoteScaling.Percent100, 100, 100)]
    [InlineData(0.8, RemoteScaling.MatchLocal, 100, 100)]
    public void ScaleFactors(double dpi, RemoteScaling setting, int desktop, int device)
    {
        Assert.Equal((desktop, device), LocalEnvironment.ScaleFactorsFor(dpi, setting));
    }
}
