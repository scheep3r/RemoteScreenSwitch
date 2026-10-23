using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RdpMonitorSwitcher.Core.Models;
using RdpMonitorSwitcher.Core.Rdp;

namespace RdpMonitorSwitcher.Core.Services;

/// <summary>Local environment values that are sent to the server on connect.</summary>
public sealed record LocalEnvironment(int DesktopScaleFactor, int DeviceScaleFactor, uint KeyboardLayout)
{
    public static LocalEnvironment Default { get; } = new(100, 100, 0);

    /// <summary>Maps a local DPI scale (1.0 = 100 %) to the RDP desktop/device scale factors.</summary>
    public static (int Desktop, int Device) ScaleFactorsFor(double dpiScale, RemoteScaling setting)
    {
        var desktop = setting == RemoteScaling.MatchLocal
            ? (int)Math.Round(dpiScale * 100)
            : (int)setting;
        desktop = Math.Clamp(desktop, 100, 500);
        var device = desktop switch
        {
            < 140 => 100,
            < 180 => 140,
            _ => 180,
        };
        return (desktop, device);
    }
}

/// <summary>
/// Owns one logical remote desktop connection: connects, reconnects after network loss,
/// and keeps the remote monitor layout in sync with what the UI wants.
///
/// Layout changes are applied inside the running session via the Display Control channel
/// whenever the server supports it. Only if the server cannot change the number of monitors
/// at runtime is the connection re-established with the new layout (and the UI is told so).
/// Must be used from a single (UI) thread.
/// </summary>
public sealed class RemoteDesktopController : IDisposable
{
    public static readonly TimeSpan LayoutDebounce = TimeSpan.FromMilliseconds(400);
    public static readonly TimeSpan LayoutConfirmTimeout = TimeSpan.FromSeconds(6);

    private readonly IRdpSessionFactory _factory;
    private readonly ReconnectPolicy _policy;
    private readonly TimeProvider _time;
    private readonly SynchronizationContext? _sync;
    private readonly ILogger _log;

    private IRdpSession? _session;
    private ConnectionProfile? _profile;
    private string? _password;
    private LocalEnvironment _environment = LocalEnvironment.Default;

    private MonitorLayout? _desired;
    private MonitorLayout? _pending;
    private ITimer? _debounceTimer;
    private ITimer? _confirmTimer;
    private ITimer? _reconnectTimer;
    private int _reconnectAttempt;
    private bool _relayoutReconnect;
    private int _generation;

    public RemoteDesktopController(IRdpSessionFactory factory, ReconnectPolicy? policy = null,
        TimeProvider? timeProvider = null, SynchronizationContext? synchronizationContext = null,
        ILogger<RemoteDesktopController>? logger = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _policy = policy ?? new ReconnectPolicy();
        _time = timeProvider ?? TimeProvider.System;
        _sync = synchronizationContext;
        _log = logger ?? (ILogger)NullLogger.Instance;
    }

    public event EventHandler? StateChanged;

    /// <summary>The committed monitor layout changed (new monitors or sizes).</summary>
    public event EventHandler? LayoutChanged;

    public event EventHandler? FrameAvailable;

    public event EventHandler<string>? ClipboardTextReceived;

    public event EventHandler<RemoteCursorChange>? CursorChanged;

    /// <summary>The helper on the remote PC connected or went away (see <see cref="IsFocusAgentConnected"/>).</summary>
    public event EventHandler? FocusAgentChanged;

    /// <summary>A window came to the front on the remote PC (point on its monitor, frame buffer coordinates).</summary>
    public event EventHandler<PixelPoint>? RemoteForeground;

    /// <summary>Informational message for the user (e.g. "reconnecting to apply 3 monitors").</summary>
    public event EventHandler<string>? Notice;

    public Func<CertificateInfo, CertificateDecision>? CertificateHandler { get; set; }

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public DisconnectReason LastReason { get; private set; }

    public string StatusMessage { get; private set; } = "Nicht verbunden";

    public ConnectionProfile? Profile => _profile;

    /// <summary>The helper on the remote PC reports the foreground window.</summary>
    public bool IsFocusAgentConnected { get; private set; }

    /// <summary>Layout of the running session (what the frame buffer contains).</summary>
    public MonitorLayout? Layout { get; private set; }

    public int ReconnectAttempt => _reconnectAttempt;

    public TimeSpan? NextReconnectDelay { get; private set; }

    public IRdpSession? Session => _session;

    public bool CanChangeLayoutLive => _session?.IsDisplayControlReady == true;

    public void Connect(ConnectionProfile profile, string? password, MonitorLayout layout, LocalEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(layout);
        var errors = profile.Validate();
        if (errors.Count > 0)
            throw new ArgumentException(string.Join(Environment.NewLine, errors), nameof(profile));

        TearDown();
        _profile = profile.Clone();
        _password = password;
        _environment = environment ?? LocalEnvironment.Default;
        _desired = layout;
        _reconnectAttempt = 0;
        StartSession(layout, ConnectionState.Connecting);
    }

    /// <summary>User-initiated disconnect; never followed by an automatic reconnect.</summary>
    public void Disconnect()
    {
        CancelTimers();
        _password = null;
        if (_session is null)
        {
            if (State != ConnectionState.Disconnected)
                SetState(ConnectionState.Disconnected, DisconnectReason.User, "Getrennt");
            return;
        }
        SetState(ConnectionState.Disconnecting, DisconnectReason.None, "Trenne …");
        _session.Disconnect();
    }

    /// <summary>
    /// Asks for a new monitor layout. Changes are debounced; when connected they are applied
    /// live if possible, otherwise on the next connect.
    /// </summary>
    public void RequestLayout(MonitorLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.Equals(_desired))
            return;
        _desired = layout;
        if (State != ConnectionState.Connected)
            return;

        _debounceTimer?.Dispose();
        _debounceTimer = _time.CreateTimer(_ => Post(ApplyDesiredLayout), null, LayoutDebounce, Timeout.InfiniteTimeSpan);
    }

    public MonitorLayout? DesiredLayout => _desired;

    private void StartSession(MonitorLayout layout, ConnectionState state)
    {
        var profile = _profile!;
        var (user, domain) = profile.SplitUser();
        var parameters = new RdpConnectParameters(
            profile.Host.Trim(), profile.Port, user, domain, _password, layout,
            _environment.DesktopScaleFactor, _environment.DeviceScaleFactor, _environment.KeyboardLayout,
            profile.RedirectClipboard, profile.UseH264, profile.PlayAudio);

        SetFocusAgent(false);
        var session = _factory.Create();
        var generation = ++_generation;
        session.StateChanged += (_, e) => { if (generation == _generation) OnSessionState(e); };
        session.DesktopResized += (_, size) => { if (generation == _generation) OnDesktopResized(size); };
        session.DisplayControlReady += (_, _) => { if (generation == _generation) OnDisplayControlReady(); };
        session.FrameAvailable += (_, _) => { if (generation == _generation) FrameAvailable?.Invoke(this, EventArgs.Empty); };
        session.ClipboardTextReceived += (_, t) => { if (generation == _generation) ClipboardTextReceived?.Invoke(this, t); };
        session.CursorChanged += (_, c) => { if (generation == _generation) CursorChanged?.Invoke(this, c); };
        session.FocusAgentChanged += (_, connected) => { if (generation == _generation) SetFocusAgent(connected); };
        session.RemoteForeground += (_, p) => { if (generation == _generation) RemoteForeground?.Invoke(this, p); };
        session.CertificateRequested = info => CertificateHandler?.Invoke(info) ?? CertificateDecision.Reject;
        _session = session;

        Layout = layout;
        _pending = null;
        LayoutChanged?.Invoke(this, EventArgs.Empty);

        _log.LogInformation("Connecting: {Parameters}", parameters);
        SetState(state, DisconnectReason.None,
            state == ConnectionState.Reconnecting ? $"Verbinde erneut (Versuch {_reconnectAttempt}) …" : $"Verbinde mit {profile.Label} …");
        session.Connect(parameters);
    }

    private void SetFocusAgent(bool connected)
    {
        if (IsFocusAgentConnected == connected)
            return;
        IsFocusAgentConnected = connected;
        _log.LogInformation("Remote helper {State}", connected ? "connected" : "disconnected");
        FocusAgentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSessionState(SessionStateChange change)
    {
        switch (change.State)
        {
            case ConnectionState.Connecting:
                break;

            case ConnectionState.Connected:
                _reconnectAttempt = 0;
                NextReconnectDelay = null;
                SetState(ConnectionState.Connected, DisconnectReason.None, $"Verbunden mit {_profile!.Label}");
                if (_desired is not null && !_desired.Equals(Layout))
                    ApplyDesiredLayout();
                break;

            case ConnectionState.Disconnecting:
                break;

            case ConnectionState.Disconnected:
                OnSessionEnded(change);
                break;
        }
    }

    private void OnSessionEnded(SessionStateChange change)
    {
        _log.LogInformation("Session ended: {Reason} 0x{Code:X8} {Message}", change.Reason, change.ErrorCode, change.Message);
        DisposeSession();
        _debounceTimer?.Dispose();
        _confirmTimer?.Dispose();
        _pending = null;
        SetFocusAgent(false);

        if (_relayoutReconnect && change.Reason == DisconnectReason.User)
        {
            // We closed the session ourselves to apply a new monitor count.
            _relayoutReconnect = false;
            StartSession(_desired ?? Layout!, ConnectionState.Reconnecting);
            return;
        }
        _relayoutReconnect = false;

        if (State == ConnectionState.Disconnecting || change.Reason == DisconnectReason.User)
        {
            SetState(ConnectionState.Disconnected, DisconnectReason.User, "Getrennt");
            _password = null;
            return;
        }

        var wasConnected = State is ConnectionState.Connected or ConnectionState.Reconnecting;
        var attempt = _reconnectAttempt + 1;
        var delay = _profile!.AutoReconnect && wasConnected ? _policy.GetDelay(change.Reason, attempt) : null;
        if (delay is null)
        {
            SetState(ConnectionState.Disconnected, change.Reason, DisconnectReasonText.Describe(change.Reason, change.Message));
            return;
        }

        _reconnectAttempt = attempt;
        NextReconnectDelay = delay;
        SetState(ConnectionState.Reconnecting, change.Reason,
            $"Verbindung unterbrochen – neuer Versuch in {delay.Value.TotalSeconds:0} s (Versuch {attempt}/{_policy.MaxAttempts})");
        _reconnectTimer?.Dispose();
        _reconnectTimer = _time.CreateTimer(_ => Post(() =>
        {
            if (State == ConnectionState.Reconnecting && _session is null)
                StartSession(_desired ?? Layout!, ConnectionState.Reconnecting);
        }), null, delay.Value, Timeout.InfiniteTimeSpan);
    }

    private void OnDisplayControlReady()
    {
        _log.LogInformation("Display control ready");
        if (State == ConnectionState.Connected && _desired is not null && !_desired.Equals(Layout))
            ApplyDesiredLayout();
    }

    private void ApplyDesiredLayout()
    {
        if (State != ConnectionState.Connected || _session is null || _desired is null || Layout is null)
            return;
        if (_desired.Equals(Layout) || _desired.Equals(_pending))
            return;

        // Same monitor sizes, other positions (monitors moved to the other side of monitor 1): the
        // desktop size stays the same, so the server would not confirm it with a resize. Reconnect.
        if (_desired.Sizes.SequenceEqual(Layout.Sizes))
        {
            ReconnectForLayout("Die Monitoranordnung wird übernommen");
            return;
        }

        if (_session.TrySetMonitorLayout(_desired))
        {
            _log.LogInformation("Requested live layout change: {Layout}", _desired);
            _pending = _desired;
            _confirmTimer?.Dispose();
            _confirmTimer = _time.CreateTimer(_ => Post(OnLayoutConfirmTimeout), null, LayoutConfirmTimeout, Timeout.InfiniteTimeSpan);
            return;
        }

        if (_desired.Count != Layout.Count)
            ReconnectForLayout("Der Remote-PC unterstützt keine Monitoränderung während der Sitzung");
        // Same monitor count: the view simply scales the existing monitors.
    }

    private void OnLayoutConfirmTimeout()
    {
        if (_pending is null || Layout is null)
            return;
        var pending = _pending;
        _pending = null;
        _log.LogWarning("Server did not apply layout {Layout} in time", pending);
        if (pending.Count != Layout.Count)
            ReconnectForLayout("Der Remote-PC hat die neue Monitoranzahl nicht übernommen");
    }

    private void ReconnectForLayout(string why)
    {
        if (_session is null)
            return;
        Notice?.Invoke(this, $"{why} – die Verbindung wird kurz neu aufgebaut ({_desired!.Count} Monitor(e)). Die Sitzung bleibt erhalten.");
        _relayoutReconnect = true;
        _session.Disconnect();
    }

    private void OnDesktopResized(PixelSize size)
    {
        if (_pending is not null && size == _pending.DesktopSize)
        {
            _confirmTimer?.Dispose();
            Layout = _pending;
            _pending = null;
            _log.LogInformation("Layout applied: {Layout}", Layout);
            LayoutChanged?.Invoke(this, EventArgs.Empty);
            if (_desired is not null && !_desired.Equals(Layout))
                ApplyDesiredLayout();
            return;
        }

        if (Layout is not null && size != Layout.DesktopSize)
            _log.LogWarning("Remote desktop size {Size} does not match layout {Layout}", size, Layout);
    }

    private void TearDown()
    {
        CancelTimers();
        DisposeSession(); // disposing a session also disconnects it
    }

    private void DisposeSession()
    {
        if (_session is null)
            return;
        _generation++;
        var session = _session;
        _session = null;
        session.Dispose();
    }

    private void CancelTimers()
    {
        _debounceTimer?.Dispose();
        _confirmTimer?.Dispose();
        _reconnectTimer?.Dispose();
        _debounceTimer = _confirmTimer = _reconnectTimer = null;
        _relayoutReconnect = false;
        NextReconnectDelay = null;
    }

    private void SetState(ConnectionState state, DisconnectReason reason, string message)
    {
        State = state;
        LastReason = reason;
        StatusMessage = message;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Post(Action action)
    {
        if (_sync is null)
            action();
        else
            _sync.Post(_ => action(), null);
    }

    public void Dispose()
    {
        TearDown();
        _password = null;
    }
}
