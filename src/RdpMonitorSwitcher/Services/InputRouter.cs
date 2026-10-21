using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using RdpMonitorSwitcher.Core.Models;
using RdpMonitorSwitcher.Core.Rdp;
using RdpMonitorSwitcher.Core.Services;
using RdpMonitorSwitcher.Core.ViewModels;
using RdpMonitorSwitcher.Infrastructure;
using RdpMonitorSwitcher.Views;

namespace RdpMonitorSwitcher.Services;

/// <summary>
/// Sends keyboard input to the session while a <see cref="RemoteViewport"/> has the focus and
/// its window is in the foreground. App shortcuts (Ctrl+Alt+…) are handled locally.
/// Releases all held keys whenever the focus leaves the remote view or the monitor changes,
/// so no key stays "stuck" on the remote PC.
/// </summary>
internal sealed class InputRouter : IInputResetter, IDisposable
{
    private readonly MainViewModel _main;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger _log;
    private readonly KeyTranslator _translator = new();
    private readonly HashSet<int> _swallowedKeys = [];
    private KeyboardHook? _hook;
    private RemoteViewport? _active;
    private bool _ctrl, _alt, _shift;

    public InputRouter(MainViewModel main, Dispatcher dispatcher, ILogger<InputRouter> logger)
    {
        _main = main;
        _dispatcher = dispatcher;
        _log = logger;
        try
        {
            _hook = new KeyboardHook(OnKey);
        }
        catch (Exception ex)
        {
            // Without the hook, Win/Alt+Tab stay local; everything else still works via the hook-less path.
            _log.LogError(ex, "Keyboard hook could not be installed");
        }
    }

    private IRdpSession? Session => _main.IsConnected ? _main.Controller.Session : null;

    /// <summary>Called for Esc in a remote view; returns true if it was consumed (e.g. cancels a file transfer).</summary>
    public Func<bool>? EscapePressed { get; set; }

    public void Activate(RemoteViewport viewport)
    {
        if (_active == viewport)
            return;
        ReleaseAll();
        _active = viewport;
        SyncToggleStates();
    }

    public void Deactivate(RemoteViewport viewport)
    {
        if (_active != viewport)
            return;
        ReleaseAll();
        _active = null;
    }

    /// <summary>Key-up for every key the remote side believes is pressed, plus mouse buttons.</summary>
    public void ReleaseAll()
    {
        var session = Session;
        foreach (var key in _translator.ReleaseAll())
            session?.SendKey(key.Down, key.Scancode);
        _active?.ReleaseMouseButtons();
    }

    private void SyncToggleStates()
    {
        var toggles = KeyboardToggles.None;
        if ((NativeMethods.GetKeyState(NativeMethods.VK_CAPITAL) & 1) != 0)
            toggles |= KeyboardToggles.CapsLock;
        if ((NativeMethods.GetKeyState(NativeMethods.VK_NUMLOCK) & 1) != 0)
            toggles |= KeyboardToggles.NumLock;
        if ((NativeMethods.GetKeyState(NativeMethods.VK_SCROLL) & 1) != 0)
            toggles |= KeyboardToggles.ScrollLock;
        if ((NativeMethods.GetKeyState(NativeMethods.VK_KANA) & 1) != 0)
            toggles |= KeyboardToggles.KanaLock;
        Session?.SendFocusSync((uint)toggles);
    }

    private bool IsActiveWindowInForeground()
    {
        if (_active?.OwnerWindow is not { } window)
            return false;
        var hwnd = new WindowInteropHelper(window).Handle;
        return hwnd != 0 && NativeMethods.GetForegroundWindow() == hwnd && _active.IsKeyboardFocusWithin;
    }

    /// <returns>True to swallow the key locally.</returns>
    private bool OnKey(LowLevelKey key)
    {
        TrackModifier(key);

        if (!key.Down && _swallowedKeys.Remove(key.VirtualKey))
            return true;

        if (_active is null || Session is not { } session || !IsActiveWindowInForeground())
            return false;
        if (key.VirtualKey == NativeMethods.VK_PACKET)
            return false; // injected unicode characters (e.g. on-screen keyboard) stay local

        if (key.Down && key.VirtualKey == NativeMethods.VK_ESCAPE && EscapePressed?.Invoke() == true)
        {
            _swallowedKeys.Add(key.VirtualKey);
            return true;
        }

        if (key.Down)
        {
            var command = ShortcutMap.Resolve(key.VirtualKey, _ctrl, _alt, _shift);
            if (command.Kind != AppCommandKind.None)
            {
                _swallowedKeys.Add(key.VirtualKey);
                var view = _active.View;
                if (view is not null)
                    _dispatcher.BeginInvoke(() => _main.Execute(command, view));
                return true;
            }
        }

        var scanCode = key.ScanCode;
        var extended = key.Extended;
        if (scanCode == 0)
        {
            // Some injected keys carry no scancode; derive it from the virtual key.
            var mapped = NativeMethods.MapVirtualKey((uint)key.VirtualKey, NativeMethods.MAPVK_VK_TO_VSC_EX);
            scanCode = mapped & 0xFF;
            extended = (mapped & 0xFF00) == 0xE000;
        }

        foreach (var e in _translator.Translate(scanCode, extended, key.Down))
            session.SendKey(e.Down, e.Scancode);

        // Lock keys also toggle locally so the keyboard LEDs stay in sync.
        return key.VirtualKey is not (NativeMethods.VK_CAPITAL or NativeMethods.VK_NUMLOCK or NativeMethods.VK_SCROLL or NativeMethods.VK_KANA);
    }

    private void TrackModifier(LowLevelKey key)
    {
        switch (key.VirtualKey)
        {
            case VirtualKeys.Control or VirtualKeys.LControl or VirtualKeys.RControl:
                _ctrl = key.Down;
                break;
            case VirtualKeys.Menu or VirtualKeys.LMenu or VirtualKeys.RMenu:
                _alt = key.Down;
                break;
            case VirtualKeys.Shift or VirtualKeys.LShift or VirtualKeys.RShift:
                _shift = key.Down;
                break;
        }
    }

    public void Dispose()
    {
        _hook?.Dispose();
        _hook = null;
    }
}
