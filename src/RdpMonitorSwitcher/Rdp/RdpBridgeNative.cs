using System.Runtime.InteropServices;

namespace RdpMonitorSwitcher.Rdp;

/// <summary>P/Invoke declarations for native/RdpBridge (see include/rdpbridge.h).</summary>
internal static class RdpBridgeNative
{
    private const string Dll = "RdpBridge.dll";

    public const int ApiVersion = 3;

    public const int Ok = 0;
    public const int NotSupported = -3;

    public const int StateDisconnected = 0;
    public const int StateConnecting = 1;
    public const int StateConnected = 2;
    public const int StateDisconnecting = 3;

    public const int EventDesktopResized = 1;
    public const int EventDisplayControlReady = 2;
    public const int EventClipboardReady = 3;
    public const int EventRemoteClipboard = 4;
    public const int EventLocalClipboardAccepted = 5;
    public const int ClipText = 0x1;
    public const int ClipFiles = 0x2;
    public const uint FileAttributeDirectory = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    public struct Monitor
    {
        public int Left;
        public int Top;
        public int Width;
        public int Height;
        public int IsPrimary;
        public uint PhysicalWidthMm;
        public uint PhysicalHeightMm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ConnectParams
    {
        public uint StructSize;
        public nint Host;
        public uint Port;
        public nint Username;
        public nint Domain;
        public nint Password;
        public nint ClientHostname;
        public uint MonitorCount;
        public nint Monitors;
        public uint DesktopScaleFactor;
        public uint DeviceScaleFactor;
        public uint KeyboardLayout;
        public int EnableClipboard;
        public int EnableH264;
        public int EnableAudio;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Frame
    {
        public nint Data;
        public int Width;
        public int Height;
        public int Stride;
        public int DirtyX;
        public int DirtyY;
        public int DirtyWidth;
        public int DirtyHeight;
        public uint Generation;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Callbacks
    {
        public uint StructSize;
        public nint User;
        public nint OnState;
        public nint OnEvent;
        public nint OnFrame;
        public nint OnCertificate;
        public nint OnCursor;
        public nint OnClipboardText;
        public nint OnFileList;
        public nint OnFileData;
        public nint OnFileRead;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FileInfo
    {
        public nint Name;
        public ulong Size;
        public uint Attributes;
        public int HasSize;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void StateCallback(nint user, int state, int reason, uint errorCode, nint message);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void EventCallback(nint user, int evt, int a, int b);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FrameCallback(nint user);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate uint CertificateCallback(nint user, nint host, uint port, nint commonName, nint subject,
        nint issuer, nint fingerprint, nint oldFingerprint, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void CursorCallback(nint user, int kind, nint cursor);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void ClipboardCallback(nint user, nint text, uint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FileListCallback(nint user, int ok, nint files, uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FileDataCallback(nint user, uint streamId, int ok, nint data, uint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int FileReadCallback(nint user, uint listIndex, ulong offset, uint length, nint buffer, out uint read);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rb_clipboard_set_files")]
    public static extern int ClipboardSetFiles(nint session, FileInfo[]? files, uint count);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rb_clipboard_request_files")]
    public static extern int ClipboardRequestFiles(nint session);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rb_clipboard_request_file_range")]
    public static extern int ClipboardRequestFileRange(nint session, uint streamId, uint listIndex, ulong offset, uint length);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rb_api_version")]
    public static extern int GetApiVersion();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rb_configure_logging")]
    public static extern int ConfigureLogging([MarshalAs(UnmanagedType.LPUTF8Str)] string? directory,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? level);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rb_create")]
    public static extern nint Create(in Callbacks callbacks);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rb_connect")]
    public static extern int Connect(nint session, in ConnectParams parameters);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rb_disconnect")]
    public static extern void Disconnect(nint session);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rb_destroy")]
    public static extern void Destroy(nint session);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rb_frame_acquire")]
    public static extern int FrameAcquire(nint session, out Frame frame);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rb_frame_release")]
    public static extern void FrameRelease(nint session);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rb_send_mouse")]
    public static extern int SendMouse(nint session, ushort flags, int x, int y);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rb_send_extended_mouse")]
    public static extern int SendExtendedMouse(nint session, ushort flags, int x, int y);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rb_send_key")]
    public static extern int SendKey(nint session, int down, uint scancode);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rb_send_focus_sync")]
    public static extern int SendFocusSync(nint session, uint toggleStates);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rb_set_monitor_layout")]
    public static extern int SetMonitorLayout(nint session, Monitor[] monitors, uint count);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "rb_clipboard_set_text", CharSet = CharSet.Unicode)]
    public static extern int ClipboardSetText(nint session, string text, uint length);
}
