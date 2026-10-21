using System.Runtime.InteropServices;

namespace RdpMonitorSwitcher.Infrastructure;

internal static class NativeMethods
{
    public const int WH_KEYBOARD_LL = 13;
    public const int HC_ACTION = 0;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;
    public const int WM_MOUSEHWHEEL = 0x020E;
    public const int WM_CLIPBOARDUPDATE = 0x031D;
    public const uint LLKHF_EXTENDED = 0x01;
    public const uint LLKHF_INJECTED = 0x10;
    public const uint LLKHF_UP = 0x80;
    public const int VK_PACKET = 0xE7;
    public const int VK_CAPITAL = 0x14;
    public const int VK_NUMLOCK = 0x90;
    public const int VK_SCROLL = 0x91;
    public const int VK_KANA = 0x15;
    public const int VK_LBUTTON = 0x01;
    public const int VK_ESCAPE = 0x1B;
    public const uint MAPVK_VK_TO_VSC_EX = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    public delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    public static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    public static extern nint GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    public static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AddClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyCursor(nint hCursor);

    public static void DestroyCursorHandle(nint cursor)
    {
        if (cursor != 0)
            DestroyCursor(cursor);
    }

    /// <summary>Owns an HCURSOR received from RdpBridge.</summary>
    public sealed class SafeCursorHandle : SafeHandle
    {
        public SafeCursorHandle(nint handle) : base(0, ownsHandle: true) => SetHandle(handle);

        public override bool IsInvalid => handle == 0;

        protected override bool ReleaseHandle() => DestroyCursor(handle);
    }
}
