using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace RdpMonitorSwitcher.Agent;

/// <summary>
/// The helper on the remote PC ("RdpMonitorSwitcher.exe --agent"): whenever a window comes to the
/// front it sends the centre of that window's monitor to the app over a dynamic virtual channel of
/// the RDP connection. Event driven (WinEvent hook) – no polling of windows. A heartbeat every few
/// seconds (re)opens the channel after a reconnect; with mstsc or no RDP client, opening just fails.
/// </summary>
internal sealed class FocusAgent : IDisposable
{
    /// <summary>Must match RB_FOCUS_CHANNEL_NAME in rdpbridge.h.</summary>
    public const string ChannelName = "RdpMonitorSwitcher.Focus";

    private const uint MessageHeartbeat = 0;
    private const uint MessageForeground = 1;
    private const int MessageSize = 12;

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Shell windows that take the focus briefly (taskbar, Start, Alt+Tab, desktop).</summary>
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "MultitaskingViewFrame",
        "XamlExplorerHostIslandWindow", "ForegroundStaging", "Windows.UI.Core.CoreWindow",
        "TaskListThumbnailWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
        "Shell_InputSwitchTopLevelWindow", "#32768", "tooltips_class32",
    };

    private readonly ILogger _log;
    private readonly Native.WinEventProc _onWinEvent; // referenced so the GC keeps the callback alive
    private readonly DispatcherTimer _heartbeat;
    private nint _hook;
    private SafeFileHandle? _channel;

    public FocusAgent(ILogger log)
    {
        _log = log;
        _onWinEvent = OnWinEvent;
        _heartbeat = new DispatcherTimer { Interval = HeartbeatInterval };
        _heartbeat.Tick += (_, _) => Send(MessageHeartbeat, 0, 0);
    }

    /// <summary>Starts on the calling thread, which must run a message loop (the WPF dispatcher).</summary>
    public void Start()
    {
        _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, 0, _onWinEvent,
            0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        if (_hook == 0)
            throw new InvalidOperationException($"SetWinEventHook failed ({Marshal.GetLastWin32Error()}).");
        _heartbeat.Start();
        Send(MessageHeartbeat, 0, 0);
        _log.LogInformation("Helper started");
    }

    private void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != Native.OBJID_WINDOW || !TryGetMonitorCentre(hwnd, out var x, out var y))
            return;
        Send(MessageForeground, x, y);
    }

    /// <summary>Centre of the monitor the window is on, relative to the virtual screen (= frame buffer).</summary>
    private static bool TryGetMonitorCentre(nint hwnd, out int x, out int y)
    {
        x = y = 0;
        if (hwnd == 0 || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd))
            return false;
        var className = new StringBuilder(64);
        if (Native.GetClassName(hwnd, className, className.Capacity) > 0 && IgnoredClasses.Contains(className.ToString()))
            return false;
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        var monitor = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONULL);
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (monitor == 0 || !Native.GetMonitorInfo(monitor, ref info))
            return false;
        x = (info.rcMonitor.Left + info.rcMonitor.Right) / 2 - Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        y = (info.rcMonitor.Top + info.rcMonitor.Bottom) / 2 - Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        return true;
    }

    private void Send(uint type, int x, int y)
    {
        // A failed write means the client went away (disconnect, reconnect): reopen once and retry.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (_channel is null && !TryOpen())
                return;
            if (Write(type, x, y))
                return;
            _log.LogInformation("App connection lost");
            CloseChannel();
        }
    }

    private bool TryOpen()
    {
        var wts = Native.WTSVirtualChannelOpenEx(Native.WTS_CURRENT_SESSION, ChannelName, Native.WTS_CHANNEL_OPTION_DYNAMIC);
        if (wts == 0)
            return false; // no RDP client with our channel (mstsc, local console, disconnected)
        try
        {
            if (!Native.WTSVirtualChannelQuery(wts, Native.WTSVirtualFileHandle, out var buffer, out var length))
                return false;
            try
            {
                if (length < nint.Size)
                    return false;
                var process = Native.GetCurrentProcess();
                if (!Native.DuplicateHandle(process, Marshal.ReadIntPtr(buffer), process, out var file, 0, false, Native.DUPLICATE_SAME_ACCESS))
                    return false;
                _channel = new SafeFileHandle(file, ownsHandle: true);
                _log.LogInformation("Connected to the app");
                return true;
            }
            finally
            {
                Native.WTSFreeMemory(buffer);
            }
        }
        finally
        {
            Native.WTSVirtualChannelClose(wts);
        }
    }

    /// <summary>Overlapped write (the channel handle is opened for overlapped I/O).</summary>
    private unsafe bool Write(uint type, int x, int y)
    {
        var message = stackalloc byte[MessageSize];
        var span = new Span<byte>(message, MessageSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span, type);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], x);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], y);

        using var done = new ManualResetEvent(false);
        var overlapped = new NativeOverlapped { EventHandle = done.SafeWaitHandle.DangerousGetHandle() };
        if (Native.WriteFile(_channel!, message, MessageSize, null, &overlapped))
            return true;
        if (Marshal.GetLastPInvokeError() != Native.ERROR_IO_PENDING)
            return false;
        if (!done.WaitOne(WriteTimeout))
        {
            Native.CancelIoEx(_channel!, &overlapped);
            Native.GetOverlappedResult(_channel!, &overlapped, out _, true);
            return false;
        }
        return Native.GetOverlappedResult(_channel!, &overlapped, out var written, false) && written == MessageSize;
    }

    private void CloseChannel()
    {
        _channel?.Dispose();
        _channel = null;
    }

    public void Dispose()
    {
        _heartbeat.Stop();
        if (_hook != 0)
            Native.UnhookWinEvent(_hook);
        _hook = 0;
        CloseChannel();
    }

    private static unsafe class Native
    {
        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
        public const int OBJID_WINDOW = 0;
        public const uint MONITOR_DEFAULTTONULL = 0;
        public const int SM_XVIRTUALSCREEN = 76;
        public const int SM_YVIRTUALSCREEN = 77;
        public const int DWMWA_CLOAKED = 14;
        public const uint WTS_CURRENT_SESSION = uint.MaxValue;
        public const uint WTS_CHANNEL_OPTION_DYNAMIC = 0x00000001;
        public const int WTSVirtualFileHandle = 1;
        public const uint DUPLICATE_SAME_ACCESS = 0x00000002;
        public const int ERROR_IO_PENDING = 997;

        public delegate void WinEventProc(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc, WinEventProc proc,
            uint idProcess, uint idThread, uint flags);

        [DllImport("user32.dll")]
        public static extern bool UnhookWinEvent(nint hook);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(nint hwnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(nint hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(nint hwnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        public static extern nint MonitorFromWindow(nint hwnd, uint flags);

        [DllImport("user32.dll")]
        public static extern bool GetMonitorInfo(nint monitor, ref MONITORINFO info);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

        [DllImport("wtsapi32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        public static extern nint WTSVirtualChannelOpenEx(uint sessionId, string virtualName, uint flags);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSVirtualChannelQuery(nint channel, int virtualClass, out nint buffer, out int bytesReturned);

        [DllImport("wtsapi32.dll")]
        public static extern void WTSFreeMemory(nint memory);

        [DllImport("wtsapi32.dll")]
        public static extern bool WTSVirtualChannelClose(nint channel);

        [DllImport("kernel32.dll")]
        public static extern nint GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DuplicateHandle(nint sourceProcess, nint sourceHandle, nint targetProcess, out nint targetHandle,
            uint desiredAccess, bool inheritHandle, uint options);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(SafeFileHandle file, byte* buffer, int bytesToWrite, int* bytesWritten, NativeOverlapped* overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetOverlappedResult(SafeFileHandle file, NativeOverlapped* overlapped, out int bytesTransferred, bool wait);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CancelIoEx(SafeFileHandle file, NativeOverlapped* overlapped);
    }
}
