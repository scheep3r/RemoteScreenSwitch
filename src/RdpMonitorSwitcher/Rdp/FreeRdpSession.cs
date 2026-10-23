using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using RdpMonitorSwitcher.Core.Models;
using RdpMonitorSwitcher.Core.Rdp;
using RdpMonitorSwitcher.Core.Services;
using RdpMonitorSwitcher.Infrastructure;

namespace RdpMonitorSwitcher.Rdp;

/// <summary>
/// <see cref="IRdpSession"/> on top of FreeRDP via RdpBridge.dll.
/// Native callbacks arrive on FreeRDP threads and are forwarded to the UI dispatcher.
/// </summary>
internal sealed class FreeRdpSession : IRdpSession
{
    private readonly Dispatcher _dispatcher;
    private readonly ILogger _log;

    // Delegates must stay alive as long as the native session can call them.
    private readonly RdpBridgeNative.StateCallback _onState;
    private readonly RdpBridgeNative.EventCallback _onEvent;
    private readonly RdpBridgeNative.FrameCallback _onFrame;
    private readonly RdpBridgeNative.CertificateCallback _onCertificate;
    private readonly RdpBridgeNative.CursorCallback _onCursor;
    private readonly RdpBridgeNative.ClipboardCallback _onClipboard;
    private readonly RdpBridgeNative.FileListCallback _onFileList;
    private readonly RdpBridgeNative.FileDataCallback _onFileData;
    private readonly RdpBridgeNative.FileReadCallback _onFileRead;
    private volatile LocalFileOffer? _offer;

    private nint _handle;
    private volatile bool _disposed;
    private volatile bool _displayControlReady;
    private volatile bool _certificateRejected;

    public FreeRdpSession(Dispatcher dispatcher, ILogger<FreeRdpSession> logger)
    {
        _dispatcher = dispatcher;
        _log = logger;
        _onState = OnState;
        _onEvent = OnEvent;
        _onFrame = OnFrame;
        _onCertificate = OnCertificate;
        _onCursor = OnCursor;
        _onClipboard = OnClipboard;
        _onFileList = OnFileList;
        _onFileData = OnFileData;
        _onFileRead = OnFileRead;

        var callbacks = new RdpBridgeNative.Callbacks
        {
            StructSize = (uint)Marshal.SizeOf<RdpBridgeNative.Callbacks>(),
            OnState = Marshal.GetFunctionPointerForDelegate(_onState),
            OnEvent = Marshal.GetFunctionPointerForDelegate(_onEvent),
            OnFrame = Marshal.GetFunctionPointerForDelegate(_onFrame),
            OnCertificate = Marshal.GetFunctionPointerForDelegate(_onCertificate),
            OnCursor = Marshal.GetFunctionPointerForDelegate(_onCursor),
            OnClipboardText = Marshal.GetFunctionPointerForDelegate(_onClipboard),
            OnFileList = Marshal.GetFunctionPointerForDelegate(_onFileList),
            OnFileData = Marshal.GetFunctionPointerForDelegate(_onFileData),
            OnFileRead = Marshal.GetFunctionPointerForDelegate(_onFileRead),
        };
        _handle = RdpBridgeNative.Create(callbacks);
        if (_handle == 0)
            throw new InvalidOperationException("RdpBridge: rb_create failed.");
    }

    public event EventHandler<SessionStateChange>? StateChanged;
    public event EventHandler<PixelSize>? DesktopResized;
    public event EventHandler? DisplayControlReady;
    public event EventHandler? FrameAvailable;
    public event EventHandler<string>? ClipboardTextReceived;
    public event EventHandler<RemoteCursorChange>? CursorChanged;
    public event EventHandler<RemoteClipboardInfo>? RemoteClipboardChanged;
    public event EventHandler<RemoteFileList>? RemoteFileListReceived;
    public event EventHandler<FileChunk>? FileChunkReceived;
    public event EventHandler<bool>? LocalClipboardAccepted;
    public event EventHandler<bool>? FocusAgentChanged;
    public event EventHandler<PixelPoint>? RemoteForeground;

    public Func<CertificateInfo, CertificateDecision>? CertificateRequested { get; set; }

    public bool IsDisplayControlReady => _displayControlReady;

    public void Connect(RdpConnectParameters p)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var monitors = ToNative(p.Layout);
        var pinned = GCHandle.Alloc(monitors, GCHandleType.Pinned);
        var host = Marshal.StringToCoTaskMemUTF8(p.Host);
        var user = Marshal.StringToCoTaskMemUTF8(p.Username);
        var domain = string.IsNullOrEmpty(p.Domain) ? 0 : Marshal.StringToCoTaskMemUTF8(p.Domain);
        var password = string.IsNullOrEmpty(p.Password) ? 0 : Marshal.StringToCoTaskMemUTF8(p.Password);
        var clientName = Marshal.StringToCoTaskMemUTF8(Environment.MachineName);
        try
        {
            var native = new RdpBridgeNative.ConnectParams
            {
                StructSize = (uint)Marshal.SizeOf<RdpBridgeNative.ConnectParams>(),
                Host = host,
                Port = (uint)p.Port,
                Username = user,
                Domain = domain,
                Password = password,
                ClientHostname = clientName,
                MonitorCount = (uint)monitors.Length,
                Monitors = pinned.AddrOfPinnedObject(),
                DesktopScaleFactor = (uint)p.DesktopScaleFactor,
                DeviceScaleFactor = (uint)p.DeviceScaleFactor,
                KeyboardLayout = p.KeyboardLayout,
                EnableClipboard = p.RedirectClipboard ? 1 : 0,
                EnableH264 = p.UseH264 ? 1 : 0,
                EnableAudio = p.PlayAudio ? 1 : 0,
            };
            var rc = RdpBridgeNative.Connect(_handle, native);
            if (rc != RdpBridgeNative.Ok)
                throw new InvalidOperationException($"RdpBridge: rb_connect failed ({rc}).");
        }
        finally
        {
            pinned.Free();
            FreeUtf8(host);
            FreeUtf8(user);
            FreeUtf8(domain);
            FreeUtf8(password, wipe: true);
            FreeUtf8(clientName);
        }
    }

    public void Disconnect()
    {
        if (!_disposed && _handle != 0)
            RdpBridgeNative.Disconnect(_handle);
    }

    public bool TrySetMonitorLayout(MonitorLayout layout)
    {
        if (_disposed || _handle == 0)
            return false;
        var monitors = ToNative(layout);
        var rc = RdpBridgeNative.SetMonitorLayout(_handle, monitors, (uint)monitors.Length);
        if (rc != RdpBridgeNative.Ok && rc != RdpBridgeNative.NotSupported)
            _log.LogWarning("rb_set_monitor_layout returned {Code}", rc);
        return rc == RdpBridgeNative.Ok;
    }

    public void SendMouse(ushort flags, int x, int y)
    {
        if (!_disposed)
            RdpBridgeNative.SendMouse(_handle, flags, x, y);
    }

    public void SendExtendedMouse(ushort flags, int x, int y)
    {
        if (!_disposed)
            RdpBridgeNative.SendExtendedMouse(_handle, flags, x, y);
    }

    public void SendKey(bool down, uint scancode)
    {
        if (!_disposed)
            RdpBridgeNative.SendKey(_handle, down ? 1 : 0, scancode);
    }

    public void SendFocusSync(uint toggleStates)
    {
        if (!_disposed)
            RdpBridgeNative.SendFocusSync(_handle, toggleStates);
    }

    public void SetClipboardText(string text)
    {
        if (!_disposed)
            RdpBridgeNative.ClipboardSetText(_handle, text, (uint)text.Length);
    }

    public bool SetClipboardFiles(LocalFileOffer? offer)
    {
        if (_disposed || _handle == 0)
            return false;
        _offer = offer;
        if (offer is null || offer.Entries.Count == 0)
            return RdpBridgeNative.ClipboardSetFiles(_handle, null, 0) == RdpBridgeNative.Ok;

        var names = new nint[offer.Entries.Count];
        try
        {
            var files = new RdpBridgeNative.FileInfo[offer.Entries.Count];
            for (var i = 0; i < files.Length; i++)
            {
                var entry = offer.Entries[i];
                names[i] = Marshal.StringToHGlobalUni(entry.RelativeName);
                files[i] = new RdpBridgeNative.FileInfo
                {
                    Name = names[i],
                    Size = (ulong)entry.Size,
                    Attributes = entry.IsDirectory ? RdpBridgeNative.FileAttributeDirectory : 0x80, // FILE_ATTRIBUTE_NORMAL
                    HasSize = 1,
                };
            }
            return RdpBridgeNative.ClipboardSetFiles(_handle, files, (uint)files.Length) == RdpBridgeNative.Ok;
        }
        finally
        {
            foreach (var name in names)
            {
                if (name != 0)
                    Marshal.FreeHGlobal(name);
            }
        }
    }

    public bool RequestRemoteFileList() =>
        !_disposed && _handle != 0 && RdpBridgeNative.ClipboardRequestFiles(_handle) == RdpBridgeNative.Ok;

    public bool RequestFileRange(uint streamId, int listIndex, long offset, int length) =>
        !_disposed && _handle != 0 && listIndex >= 0 && offset >= 0 && length > 0 &&
        RdpBridgeNative.ClipboardRequestFileRange(_handle, streamId, (uint)listIndex, (ulong)offset, (uint)length) == RdpBridgeNative.Ok;

    public bool ReadFrame(FrameReader reader)
    {
        if (_disposed || _handle == 0)
            return false;
        if (RdpBridgeNative.FrameAcquire(_handle, out var f) != RdpBridgeNative.Ok)
            return false;
        try
        {
            var dirty = new PixelRect(f.DirtyX, f.DirtyY, f.DirtyWidth, f.DirtyHeight);
            reader(new FrameData(f.Data, f.Width, f.Height, f.Stride, dirty, f.Generation));
            return true;
        }
        finally
        {
            RdpBridgeNative.FrameRelease(_handle);
        }
    }

    /// <summary>
    /// Disconnects and frees the native session on a background thread: joining the
    /// FreeRDP thread must never block the UI thread (it may be waiting for a dialog).
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var handle = _handle;
        _handle = 0;
        if (handle == 0)
            return;
        RdpBridgeNative.Disconnect(handle);
        var task = Task.Run(() =>
        {
            RdpBridgeNative.Destroy(handle);
            GC.KeepAlive(this);
        });
        lock (PendingShutdowns)
        {
            PendingShutdowns.RemoveAll(t => t.IsCompleted);
            PendingShutdowns.Add(task);
        }
    }

    private static readonly List<Task> PendingShutdowns = [];

    /// <summary>Waits (bounded) for disposed sessions to disconnect cleanly, e.g. on app exit.</summary>
    public static void WaitForPendingShutdowns(TimeSpan timeout)
    {
        Task[] tasks;
        lock (PendingShutdowns)
            tasks = [.. PendingShutdowns];
        Task.WaitAll(tasks, timeout);
    }

    private static RdpBridgeNative.Monitor[] ToNative(MonitorLayout layout) =>
        layout.Monitors.Select(m => new RdpBridgeNative.Monitor
        {
            Left = m.Bounds.X,
            Top = m.Bounds.Y,
            Width = m.Bounds.Width,
            Height = m.Bounds.Height,
            IsPrimary = m.IsPrimary ? 1 : 0,
        }).ToArray();

    private static unsafe void FreeUtf8(nint ptr, bool wipe = false)
    {
        if (ptr == 0)
            return;
        if (wipe)
        {
            var p = (byte*)ptr;
            while (*p != 0)
                *p++ = 0;
        }
        Marshal.FreeCoTaskMem(ptr);
    }

    private void Post(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
    {
        if (_disposed)
            return;
        _dispatcher.BeginInvoke(priority, () =>
        {
            if (!_disposed)
                action();
        });
    }

    // ---- native callbacks (FreeRDP threads) -----------------------------------------

    private void OnState(nint user, int state, int reason, uint errorCode, nint message)
    {
        try
        {
            var text = Marshal.PtrToStringUTF8(message) ?? "";
            var mapped = state switch
            {
                RdpBridgeNative.StateConnecting => ConnectionState.Connecting,
                RdpBridgeNative.StateConnected => ConnectionState.Connected,
                RdpBridgeNative.StateDisconnecting => ConnectionState.Disconnecting,
                _ => ConnectionState.Disconnected,
            };
            if (mapped == ConnectionState.Disconnected)
                _displayControlReady = false;
            var mappedReason = (DisconnectReason)reason;
            if (mapped == ConnectionState.Disconnected && _certificateRejected && mappedReason != DisconnectReason.User)
                mappedReason = DisconnectReason.Certificate;
            var change = new SessionStateChange(mapped, mappedReason, errorCode, text);
            Post(() => StateChanged?.Invoke(this, change));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "State callback failed");
        }
    }

    private void OnEvent(nint user, int evt, int a, int b)
    {
        try
        {
            switch (evt)
            {
                case RdpBridgeNative.EventDesktopResized:
                    Post(() => DesktopResized?.Invoke(this, new PixelSize(a, b)));
                    break;
                case RdpBridgeNative.EventDisplayControlReady:
                    _displayControlReady = true;
                    Post(() => DisplayControlReady?.Invoke(this, EventArgs.Empty));
                    break;
                case RdpBridgeNative.EventLocalClipboardAccepted:
                    var accepted = a != 0;
                    Post(() => LocalClipboardAccepted?.Invoke(this, accepted));
                    break;
                case RdpBridgeNative.EventFocusAgent:
                    var connected = a != 0;
                    Post(() => FocusAgentChanged?.Invoke(this, connected));
                    break;
                case RdpBridgeNative.EventRemoteForeground:
                    var point = new PixelPoint(a, b);
                    Post(() => RemoteForeground?.Invoke(this, point));
                    break;
                case RdpBridgeNative.EventRemoteClipboard:
                    var info = new RemoteClipboardInfo((a & RdpBridgeNative.ClipText) != 0, (a & RdpBridgeNative.ClipFiles) != 0, (uint)b);
                    Post(() => RemoteClipboardChanged?.Invoke(this, info));
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Event callback failed");
        }
    }

    private void OnFrame(nint user)
    {
        try
        {
            Post(() => FrameAvailable?.Invoke(this, EventArgs.Empty), DispatcherPriority.Render);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Frame callback failed");
        }
    }

    private uint OnCertificate(nint user, nint host, uint port, nint commonName, nint subject, nint issuer,
        nint fingerprint, nint oldFingerprint, uint flags)
    {
        try
        {
            var handler = CertificateRequested;
            if (handler is null || _disposed)
                return (uint)CertificateDecision.Reject;
            var info = new CertificateInfo(
                Marshal.PtrToStringUTF8(host) ?? "", (int)port,
                Marshal.PtrToStringUTF8(commonName) ?? "",
                Marshal.PtrToStringUTF8(subject) ?? "",
                Marshal.PtrToStringUTF8(issuer) ?? "",
                Marshal.PtrToStringUTF8(fingerprint) ?? "",
                oldFingerprint == 0 ? null : Marshal.PtrToStringUTF8(oldFingerprint));
            var decision = handler(info);
            _certificateRejected = decision == CertificateDecision.Reject;
            return (uint)decision;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Certificate callback failed");
            return (uint)CertificateDecision.Reject;
        }
    }

    private void OnCursor(nint user, int kind, nint cursor)
    {
        try
        {
            var change = new RemoteCursorChange((RemoteCursorKind)kind, cursor);
            if (_disposed)
            {
                NativeMethods.DestroyCursorHandle(cursor);
                return;
            }
            _dispatcher.BeginInvoke(() =>
            {
                if (_disposed)
                    NativeMethods.DestroyCursorHandle(change.Handle);
                else
                    CursorChanged?.Invoke(this, change);
            });
        }
        catch (Exception ex)
        {
            NativeMethods.DestroyCursorHandle(cursor);
            _log.LogError(ex, "Cursor callback failed");
        }
    }

    /// <summary>The remote PC reads an offered local file (FreeRDP thread, synchronous).</summary>
    private unsafe int OnFileRead(nint user, uint listIndex, ulong offset, uint length, nint buffer, out uint read)
    {
        read = 0;
        try
        {
            var offer = _offer;
            if (offer is null || _disposed)
                return 0;
            read = (uint)offer.Read((int)listIndex, (long)offset, new Span<byte>((void*)buffer, (int)length));
            return 1;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Remote read of offered file {Index} failed", listIndex);
            return 0;
        }
    }

    private void OnFileList(nint user, int ok, nint files, uint count)
    {
        try
        {
            var entries = new List<RemoteFileEntry>((int)count);
            var size = Marshal.SizeOf<RdpBridgeNative.FileInfo>();
            for (var i = 0; i < count; i++)
            {
                var f = Marshal.PtrToStructure<RdpBridgeNative.FileInfo>(files + i * size);
                entries.Add(new RemoteFileEntry(
                    Marshal.PtrToStringUni(f.Name) ?? "",
                    f.HasSize != 0 ? (long)f.Size : null,
                    (f.Attributes & RdpBridgeNative.FileAttributeDirectory) != 0));
            }
            var list = new RemoteFileList(ok != 0, entries);
            Post(() => RemoteFileListReceived?.Invoke(this, list));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "File list callback failed");
            Post(() => RemoteFileListReceived?.Invoke(this, new RemoteFileList(false, [])));
        }
    }

    private void OnFileData(nint user, uint streamId, int ok, nint data, uint length)
    {
        try
        {
            var bytes = new byte[ok != 0 ? length : 0];
            if (bytes.Length > 0)
                Marshal.Copy(data, bytes, 0, bytes.Length);
            var chunk = new FileChunk(streamId, ok != 0, bytes);
            Post(() => FileChunkReceived?.Invoke(this, chunk));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "File data callback failed");
            Post(() => FileChunkReceived?.Invoke(this, new FileChunk(streamId, false, [])));
        }
    }

    private void OnClipboard(nint user, nint text, uint length)
    {
        try
        {
            var value = Marshal.PtrToStringUni(text, (int)length);
            Post(() => ClipboardTextReceived?.Invoke(this, value));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Clipboard callback failed");
        }
    }
}

internal sealed class FreeRdpSessionFactory(Dispatcher dispatcher, ILoggerFactory loggerFactory) : IRdpSessionFactory
{
    public IRdpSession Create() => new FreeRdpSession(dispatcher, loggerFactory.CreateLogger<FreeRdpSession>());
}
