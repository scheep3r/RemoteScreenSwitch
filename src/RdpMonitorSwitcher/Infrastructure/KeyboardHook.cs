using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RdpMonitorSwitcher.Infrastructure;

/// <summary>Event data of a low-level keyboard event.</summary>
internal readonly record struct LowLevelKey(int VirtualKey, uint ScanCode, bool Extended, bool Down, bool Injected);

/// <summary>
/// WH_KEYBOARD_LL hook. Needed so that keys like Win, Alt+Tab or Ctrl+Esc reach the remote
/// session instead of the local shell while a remote view has the keyboard focus.
/// The handler runs on the UI thread and must be fast (Windows drops slow hooks).
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private readonly Func<LowLevelKey, bool> _handler;
    private nint _hook;

    /// <param name="handler">Returns true to swallow the key locally.</param>
    public KeyboardHook(Func<LowLevelKey, bool> handler)
    {
        _handler = handler;
        _proc = HookProc;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx failed");
    }

    private nint HookProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode == NativeMethods.HC_ACTION)
        {
            var message = (int)wParam;
            if (message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN or NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP)
            {
                var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                var key = new LowLevelKey(
                    (int)data.vkCode,
                    data.scanCode,
                    (data.flags & NativeMethods.LLKHF_EXTENDED) != 0,
                    (data.flags & NativeMethods.LLKHF_UP) == 0,
                    (data.flags & NativeMethods.LLKHF_INJECTED) != 0);
                try
                {
                    if (_handler(key))
                        return 1;
                }
                catch (Exception)
                {
                    // Never let an exception escape into user32.
                }
            }
        }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = 0;
        }
    }
}
