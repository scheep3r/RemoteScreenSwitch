using RdpMonitorSwitcher.Core.Models;

namespace RdpMonitorSwitcher.Core.Rdp;

/// <summary>Everything needed to open one RDP connection.</summary>
public sealed record RdpConnectParameters(
    string Host,
    int Port,
    string Username,
    string Domain,
    string? Password,
    MonitorLayout Layout,
    int DesktopScaleFactor,
    int DeviceScaleFactor,
    uint KeyboardLayout,
    bool RedirectClipboard,
    bool UseH264,
    bool PlayAudio)
{
    /// <summary>Never prints the password.</summary>
    public override string ToString() =>
        $"{Username}@{Host}:{Port} monitors=[{Layout}] scale={DesktopScaleFactor}/{DeviceScaleFactor} " +
        $"kbd=0x{KeyboardLayout:X8} clipboard={RedirectClipboard} h264={UseH264} audio={PlayAudio}";
}

public sealed record SessionStateChange(ConnectionState State, DisconnectReason Reason, uint ErrorCode, string Message);

public sealed record CertificateInfo(
    string Host,
    int Port,
    string CommonName,
    string Subject,
    string Issuer,
    string Fingerprint,
    string? PreviousFingerprint)
{
    public bool HasChanged => !string.IsNullOrEmpty(PreviousFingerprint);
}

public enum CertificateDecision
{
    Reject = 0,
    AcceptPermanently = 1,
    AcceptOnce = 2,
}

public enum RemoteCursorKind
{
    Custom = 0,
    Hidden = 1,
    Default = 2,
}

/// <summary>
/// One RDP connection. Implementations raise all events on the synchronization context
/// they were created with (the UI thread in the app), except <see cref="CertificateRequested"/>,
/// whose handler must return the decision synchronously.
/// </summary>
public interface IRdpSession : IDisposable
{
    event EventHandler<SessionStateChange>? StateChanged;

    /// <summary>The remote frame buffer changed size (width, height).</summary>
    event EventHandler<PixelSize>? DesktopResized;

    /// <summary>The Display Control channel is ready; runtime layout changes are possible.</summary>
    event EventHandler? DisplayControlReady;

    /// <summary>New frame data is available.</summary>
    event EventHandler? FrameAvailable;

    /// <summary>Remote text was copied.</summary>
    event EventHandler<string>? ClipboardTextReceived;

    /// <summary>The remote pointer shape changed. The handle (if any) is owned by the receiver.</summary>
    event EventHandler<RemoteCursorChange>? CursorChanged;

    /// <summary>Something was copied on the remote PC (text and/or files).</summary>
    event EventHandler<RemoteClipboardInfo>? RemoteClipboardChanged;

    /// <summary>Answer to <see cref="RequestRemoteFileList"/>.</summary>
    event EventHandler<RemoteFileList>? RemoteFileListReceived;

    /// <summary>Answer to <see cref="RequestFileRange"/>.</summary>
    event EventHandler<FileChunk>? FileChunkReceived;

    /// <summary>The remote PC answered our clipboard offer (true = accepted).</summary>
    event EventHandler<bool>? LocalClipboardAccepted;

    /// <summary>Called on a background thread while connecting. Blocks the connection until answered.</summary>
    Func<CertificateInfo, CertificateDecision>? CertificateRequested { get; set; }

    bool IsDisplayControlReady { get; }

    void Connect(RdpConnectParameters parameters);

    void Disconnect();

    /// <summary>Returns false if the server does not support runtime layout changes (yet).</summary>
    bool TrySetMonitorLayout(MonitorLayout layout);

    void SendMouse(ushort flags, int x, int y);

    void SendExtendedMouse(ushort flags, int x, int y);

    /// <param name="scancode">Low byte = scancode, 0x100 = extended (E0) key.</param>
    void SendKey(bool down, uint scancode);

    void SendFocusSync(uint toggleStates);

    void SetClipboardText(string text);

    /// <summary>
    /// Offers local files on the RDP clipboard (replaces offered text); the remote PC reads them
    /// through <see cref="Services.LocalFileOffer.Read"/> while pasting. Null withdraws the offer.
    /// </summary>
    bool SetClipboardFiles(Services.LocalFileOffer? offer);

    /// <summary>Asks for the files on the remote clipboard. False if there are none / clipboard is off.</summary>
    bool RequestRemoteFileList();

    /// <summary>Asks for a byte range of one file from the last file list.</summary>
    bool RequestFileRange(uint streamId, int listIndex, long offset, int length);

    /// <summary>
    /// Gives exclusive access to the frame buffer for the duration of <paramref name="reader"/>.
    /// Returns false if no frame exists yet.
    /// </summary>
    bool ReadFrame(FrameReader reader);
}

public sealed record RemoteCursorChange(RemoteCursorKind Kind, nint Handle);

public sealed record RemoteClipboardInfo(bool HasText, bool HasFiles, uint Sequence);

/// <summary>One entry of the remote file list; <paramref name="Name"/> is a relative path with '\'.</summary>
public sealed record RemoteFileEntry(string Name, long? Size, bool IsDirectory);

public sealed record RemoteFileList(bool Success, IReadOnlyList<RemoteFileEntry> Files);

public sealed record FileChunk(uint StreamId, bool Success, byte[] Data);

/// <summary>View on the locked frame buffer (BGRX, top-down).</summary>
public readonly record struct FrameData(
    nint Data,
    int Width,
    int Height,
    int Stride,
    PixelRect Dirty,
    uint Generation);

public delegate void FrameReader(in FrameData frame);

public interface IRdpSessionFactory
{
    IRdpSession Create();
}

/// <summary>RDP mouse flags (PTR_FLAGS_* / PTR_XFLAGS_*).</summary>
public static class MouseFlags
{
    public const ushort HorizontalWheel = 0x0400;
    public const ushort Wheel = 0x0200;
    public const ushort WheelNegative = 0x0100;
    public const ushort Move = 0x0800;
    public const ushort Down = 0x8000;
    public const ushort Button1 = 0x1000; // left
    public const ushort Button2 = 0x2000; // right
    public const ushort Button3 = 0x4000; // middle
    public const ushort ExtendedDown = 0x8000;
    public const ushort ExtendedButton1 = 0x0001; // XButton1
    public const ushort ExtendedButton2 = 0x0002; // XButton2

    /// <summary>
    /// Splits a wheel delta into RDP wheel messages (9-bit two's complement, max magnitude 255).
    /// </summary>
    public static IEnumerable<ushort> WheelSteps(int delta, bool horizontal)
    {
        var flag = horizontal ? HorizontalWheel : Wheel;
        while (delta != 0)
        {
            var step = Math.Clamp(delta, -255, 255);
            delta -= step;
            yield return (ushort)(flag | (step & 0x1FF));
        }
    }
}

/// <summary>Keyboard toggle states for focus synchronisation (KBD_SYNC_*).</summary>
[Flags]
public enum KeyboardToggles : uint
{
    None = 0,
    ScrollLock = 0x1,
    NumLock = 0x2,
    CapsLock = 0x4,
    KanaLock = 0x8,
}
