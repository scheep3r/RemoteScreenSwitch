using RdpMonitorSwitcher.Core.Configuration;
using RdpMonitorSwitcher.Core.Models;
using RdpMonitorSwitcher.Core.Rdp;
using RdpMonitorSwitcher.Core.Services;
using RdpMonitorSwitcher.Core.ViewModels;

namespace RdpMonitorSwitcher.Tests;

/// <summary>Scriptable in-memory session: tests raise the server-side events explicitly.</summary>
internal sealed class FakeSession : IRdpSession
{
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

    /// <summary>Last local file offer and how the simulated remote PC answers it (null = never).</summary>
    public LocalFileOffer? Offer { get; private set; }
    public bool? AcceptOffer { get; set; } = true;
    public List<(ushort Flags, int X, int Y)> MouseEvents { get; } = [];

    // ---- simulated remote PC for file transfer ---------------------------------------
    /// <summary>Files "selected in the remote Explorer"; returned after Ctrl+C.</summary>
    public List<(RemoteFileEntry Entry, byte[] Content)> RemoteFiles { get; } = [];
    public bool RespondToCopy { get; set; } = true;
    public bool RespondToFileRequests { get; set; } = true;
    public HashSet<int> FailingFiles { get; } = [];
    public int ChunkRequests { get; private set; }

    /// <summary>When set, range requests are queued and answered by <see cref="AnswerPending"/>.</summary>
    public bool DeferFileResponses { get; set; }
    public List<(uint StreamId, int Index, long Offset, int Length)> PendingRanges { get; } = [];

    /// <summary>Answers all queued range requests, optionally in reverse order.</summary>
    public void AnswerPending(bool reverse = false)
    {
        var pending = reverse ? Enumerable.Reverse(PendingRanges).ToList() : PendingRanges.ToList();
        PendingRanges.Clear();
        foreach (var (streamId, index, offset, length) in pending)
            AnswerRange(streamId, index, offset, length);
    }
    private uint _clipboardSequence;

    public Func<CertificateInfo, CertificateDecision>? CertificateRequested { get; set; }
    public bool IsDisplayControlReady { get; set; }
    public RdpConnectParameters? ConnectedWith { get; private set; }
    public int DisconnectCalls { get; private set; }
    public bool Disposed { get; private set; }
    public List<MonitorLayout> LayoutRequests { get; } = [];
    public List<(bool Down, uint Scancode)> Keys { get; } = [];

    public void Connect(RdpConnectParameters parameters) => ConnectedWith = parameters;

    public void Disconnect() => DisconnectCalls++;

    public bool TrySetMonitorLayout(MonitorLayout layout)
    {
        if (!IsDisplayControlReady)
            return false;
        LayoutRequests.Add(layout);
        return true;
    }

    public void SendMouse(ushort flags, int x, int y) => MouseEvents.Add((flags, x, y));
    public void SendExtendedMouse(ushort flags, int x, int y) { }
    public void SendKey(bool down, uint scancode)
    {
        Keys.Add((down, scancode));
        // Ctrl+C: Ctrl (0x1D) is down when C (0x2E) goes down.
        if (down && scancode == 0x2E && RespondToCopy && Keys.Count >= 2 && Keys[^2] == (true, 0x1Du))
            RemoteClipboardChanged?.Invoke(this, new RemoteClipboardInfo(false, RemoteFiles.Count > 0, ++_clipboardSequence));
    }

    public bool SetClipboardFiles(LocalFileOffer? offer)
    {
        Offer = offer;
        if (AcceptOffer is { } accept)
            LocalClipboardAccepted?.Invoke(this, accept);
        return true;
    }

    public bool RequestRemoteFileList()
    {
        if (RemoteFiles.Count == 0)
            return false;
        if (RespondToFileRequests)
            RemoteFileListReceived?.Invoke(this, new RemoteFileList(true, RemoteFiles.Select(f => f.Entry).ToList()));
        return true;
    }

    public bool RequestFileRange(uint streamId, int listIndex, long offset, int length)
    {
        ChunkRequests++;
        if (!RespondToFileRequests)
            return true;
        if (DeferFileResponses)
        {
            PendingRanges.Add((streamId, listIndex, offset, length));
            return true;
        }
        AnswerRange(streamId, listIndex, offset, length);
        return true;
    }

    private void AnswerRange(uint streamId, int listIndex, long offset, int length)
    {
        if (FailingFiles.Contains(listIndex))
        {
            FileChunkReceived?.Invoke(this, new FileChunk(streamId, false, []));
            return;
        }
        var content = RemoteFiles[listIndex].Content;
        var count = (int)Math.Clamp(content.Length - offset, 0, length);
        FileChunkReceived?.Invoke(this, new FileChunk(streamId, true, content.AsSpan((int)offset, count).ToArray()));
    }
    public void SendFocusSync(uint toggleStates) { }
    public void SetClipboardText(string text) { }
    public bool ReadFrame(FrameReader reader) => false;
    public void Dispose() => Disposed = true;

    // ---- server side -------------------------------------------------------------
    public void RaiseState(ConnectionState state, DisconnectReason reason = DisconnectReason.None, string message = "") =>
        StateChanged?.Invoke(this, new SessionStateChange(state, reason, 0, message));

    public void RaiseConnected() => RaiseState(ConnectionState.Connected);

    public void RaiseDisconnected(DisconnectReason reason) => RaiseState(ConnectionState.Disconnected, reason);

    public void RaiseDisplayControlReady()
    {
        IsDisplayControlReady = true;
        DisplayControlReady?.Invoke(this, EventArgs.Empty);
    }

    public void RaiseDesktopResized(PixelSize size) => DesktopResized?.Invoke(this, size);

    public void RaiseFrame() => FrameAvailable?.Invoke(this, EventArgs.Empty);

    public void RaiseClipboard(string text) => ClipboardTextReceived?.Invoke(this, text);

    public void RaiseCursor(RemoteCursorChange change) => CursorChanged?.Invoke(this, change);

    public void RaiseFocusAgent(bool connected) => FocusAgentChanged?.Invoke(this, connected);

    public void RaiseRemoteForeground(int x, int y) => RemoteForeground?.Invoke(this, new PixelPoint(x, y));
}

internal sealed class FakeSessionFactory : IRdpSessionFactory
{
    public List<FakeSession> Created { get; } = [];
    public FakeSession Last => Created[^1];

    public IRdpSession Create()
    {
        var session = new FakeSession();
        Created.Add(session);
        return session;
    }
}

internal sealed class FakeCredentialStore : ICredentialStore
{
    public Dictionary<string, string> Passwords { get; } = [];
    public string? TryGetPassword(string key) => Passwords.GetValueOrDefault(key);
    public void SavePassword(string key, string userName, string password) => Passwords[key] = password;
    public void DeletePassword(string key) => Passwords.Remove(key);
}

internal sealed class FakeDialogService : IDialogService
{
    public ConnectRequest? NextConnect { get; set; }
    public CertificateDecision CertificateAnswer { get; set; } = CertificateDecision.Reject;
    public List<string> Errors { get; } = [];
    public int ConnectDialogs { get; private set; }

    public ConnectRequest? ShowConnectDialog(ConnectionProfile profile, string? storedPassword, IReadOnlyList<ConnectionProfile> recent)
    {
        ConnectDialogs++;
        return NextConnect;
    }

    public CertificateDecision ConfirmCertificate(CertificateInfo certificate) => CertificateAnswer;

    public void ShowError(string title, string message) => Errors.Add(message);
}

internal sealed class FakeWindowService : IWindowService
{
    public List<MonitorViewModel> Open { get; } = [];
    public int Activations { get; private set; }

    public void ShowMonitorWindow(MonitorViewModel view) => Open.Add(view);
    public void CloseMonitorWindow(MonitorViewModel view) => Open.Remove(view);
    public void ActivateMonitorWindow(MonitorViewModel view) => Activations++;
    public List<MonitorViewModel> Raised { get; } = [];
    public void RaiseMonitorWindow(MonitorViewModel view) => Raised.Add(view);
}

internal sealed class FakeEnvironment : ILocalEnvironmentProvider
{
    public LocalEnvironment GetEnvironment(RemoteScaling remoteScaling) => new(100, 100, 0x407);
}

internal sealed class FakeInputResetter : IInputResetter
{
    public int Calls { get; private set; }
    public void ReleaseAll() => Calls++;
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rdpms-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class TestProfiles
{
    public static ConnectionProfile Office(int monitors = 2) => new()
    {
        Host = "office-pc",
        Username = "alex",
        MonitorCount = monitors,
        SizeMode = MonitorSizeMode.FitToWindow,
    };
}
