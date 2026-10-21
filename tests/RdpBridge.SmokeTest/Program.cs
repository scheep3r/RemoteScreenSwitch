// Smoke test for the native RdpBridge C API against a real RDP server.
//
//   dotnet run --project tests/RdpBridge.SmokeTest -- [host] [port]
//
// Connects WITHOUT credentials, so no logon attempt is made on the server. Expected:
// Connecting -> certificate callback (self-signed RDP certificate) -> Disconnected with
// reason AUTH (3). This verifies DLL loading, the worker thread, TLS, callbacks and the
// error classification.
using System.Runtime.InteropServices;

var host = args.Length > 0 ? args[0] : "127.0.0.1";
var port = args.Length > 1 ? uint.Parse(args[1]) : 3389u;

Console.WriteLine($"rb_api_version = {Native.rb_api_version()}");
var logDir = Path.Combine(Path.GetTempPath(), "rdpbridge-smoke");
Directory.CreateDirectory(logDir);
Console.WriteLine($"rb_configure_logging = {Native.rb_configure_logging(logDir, "INFO")} (log: {logDir})");

var done = new ManualResetEventSlim();
var certificateSeen = false;
int finalReason = -1;

Native.StateCb onState = (_, state, reason, code, msg) =>
{
    Console.WriteLine($"state={state} reason={reason} code=0x{code:X8} msg={Marshal.PtrToStringUTF8(msg)}");
    if (state == 0)
    {
        finalReason = reason;
        done.Set();
    }
};
Native.EventCb onEvent = (_, e, a, b) => Console.WriteLine($"event={e} a={a} b={b}");
Native.FrameCb onFrame = _ => { };
Native.CertCb onCert = (_, h, p, cn, subject, issuer, fp, oldFp, flags) =>
{
    certificateSeen = true;
    Console.WriteLine($"certificate: host={Marshal.PtrToStringUTF8(h)}:{p} cn={Marshal.PtrToStringUTF8(cn)} flags=0x{flags:X}");
    return 2; // accept for this session only
};
Native.CursorCb onCursor = (_, _, _) => { };
Native.ClipCb onClip = (_, _, _) => { };

var callbacks = new Native.Callbacks
{
    StructSize = (uint)Marshal.SizeOf<Native.Callbacks>(),
    OnState = Marshal.GetFunctionPointerForDelegate(onState),
    OnEvent = Marshal.GetFunctionPointerForDelegate(onEvent),
    OnFrame = Marshal.GetFunctionPointerForDelegate(onFrame),
    OnCertificate = Marshal.GetFunctionPointerForDelegate(onCert),
    OnCursor = Marshal.GetFunctionPointerForDelegate(onCursor),
    OnClipboard = Marshal.GetFunctionPointerForDelegate(onClip),
};

var session = Native.rb_create(callbacks);
Console.WriteLine($"rb_create = 0x{session:X}");
if (session == 0)
    return 1;

var monitors = new[]
{
    new Native.Monitor { Width = 1920, Height = 1080, IsPrimary = 1 },
    new Native.Monitor { Left = 1920, Width = 1920, Height = 1080 },
};
var pinned = GCHandle.Alloc(monitors, GCHandleType.Pinned);
var hostPtr = Marshal.StringToCoTaskMemUTF8(host);
var p = new Native.ConnectParams
{
    StructSize = (uint)Marshal.SizeOf<Native.ConnectParams>(),
    Host = hostPtr,
    Port = port,
    MonitorCount = 2,
    Monitors = pinned.AddrOfPinnedObject(),
    DesktopScaleFactor = 100,
    DeviceScaleFactor = 100,
};
Console.WriteLine($"rb_connect = {Native.rb_connect(session, p)}");
pinned.Free();
Marshal.FreeCoTaskMem(hostPtr);

var finished = done.Wait(TimeSpan.FromSeconds(30));
Native.rb_destroy(session);
GC.KeepAlive(onState); GC.KeepAlive(onEvent); GC.KeepAlive(onFrame);
GC.KeepAlive(onCert); GC.KeepAlive(onCursor); GC.KeepAlive(onClip);

Console.WriteLine($"finished={finished} certificateCallback={certificateSeen} reason={finalReason}");
var ok = finished && finalReason == 3;
Console.WriteLine(ok ? "SMOKE TEST PASSED" : "SMOKE TEST FAILED");
return ok ? 0 : 2;

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Monitor { public int Left, Top, Width, Height, IsPrimary; public uint PhysW, PhysH; }

    [StructLayout(LayoutKind.Sequential)]
    public struct ConnectParams
    {
        public uint StructSize; public nint Host; public uint Port; public nint Username, Domain, Password, ClientHostname;
        public uint MonitorCount; public nint Monitors; public uint DesktopScaleFactor, DeviceScaleFactor, KeyboardLayout;
        public int EnableClipboard, EnableH264, EnableAudio;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Callbacks
    {
        public uint StructSize; public nint User, OnState, OnEvent, OnFrame, OnCertificate, OnCursor, OnClipboard, OnFileList, OnFileData, OnFileRead;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void StateCb(nint u, int s, int r, uint c, nint m);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void EventCb(nint u, int e, int a, int b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void FrameCb(nint u);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate uint CertCb(nint u, nint h, uint p, nint cn, nint s, nint i, nint fp, nint ofp, uint f);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void CursorCb(nint u, int k, nint c);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void ClipCb(nint u, nint t, uint l);

    [DllImport("RdpBridge.dll")] public static extern int rb_api_version();
    [DllImport("RdpBridge.dll")] public static extern int rb_configure_logging([MarshalAs(UnmanagedType.LPUTF8Str)] string dir, [MarshalAs(UnmanagedType.LPUTF8Str)] string level);
    [DllImport("RdpBridge.dll")] public static extern nint rb_create(in Callbacks cb);
    [DllImport("RdpBridge.dll")] public static extern int rb_connect(nint s, in ConnectParams p);
    [DllImport("RdpBridge.dll")] public static extern void rb_destroy(nint s);
}
