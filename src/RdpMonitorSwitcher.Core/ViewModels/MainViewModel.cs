using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RdpMonitorSwitcher.Core.Configuration;
using RdpMonitorSwitcher.Core.Models;
using RdpMonitorSwitcher.Core.Rdp;
using RdpMonitorSwitcher.Core.Services;

namespace RdpMonitorSwitcher.Core.ViewModels;

/// <summary>
/// Main window logic: connection handling, the monitor toolbar of the main view and the
/// optional separate windows (one per remote monitor) that all share the same session.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    public static readonly PixelSize FallbackMonitorSize = new(1920, 1080);

    private readonly RemoteDesktopController _controller;
    private readonly SettingsStore _settingsStore;
    private readonly ICredentialStore _credentials;
    private readonly IDialogService _dialogs;
    private readonly IWindowService _windows;
    private readonly ILocalEnvironmentProvider _environment;
    private readonly ILogger _log;
    private readonly AppSettings _settings;
    private readonly PcWaker? _waker;
    private CancellationTokenSource? _wakeCts;

    public MainViewModel(RemoteDesktopController controller, SettingsStore settingsStore, ICredentialStore credentials,
        IDialogService dialogs, IWindowService windows, ILocalEnvironmentProvider environment,
        ILogger<MainViewModel>? logger = null, PcWaker? waker = null)
    {
        _waker = waker;
        _controller = controller;
        _settingsStore = settingsStore;
        _credentials = credentials;
        _dialogs = dialogs;
        _windows = windows;
        _environment = environment;
        _log = logger ?? (ILogger)NullLogger.Instance;
        _settings = settingsStore.Load();

        MainView = new MonitorViewModel(isMain: true) { IsToolbarVisible = _settings.ToolbarVisible };
        MainView.GeometryChanged += (_, _) => OnViewGeometryChanged();
        MainView.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MonitorViewModel.IsToolbarVisible))
                _settings.ToolbarVisible = MainView.IsToolbarVisible;
        };

        _controller.StateChanged += (_, _) => OnControllerStateChanged();
        _controller.LayoutChanged += (_, _) => OnLayoutChanged();
        _controller.Notice += (_, message) => Notice = message;
        _controller.FocusAgentChanged += (_, _) => OnPropertyChanged(nameof(IsFocusAgentConnected));
        _controller.RemoteForeground += (_, point) => FollowRemoteForeground(point);
        _controller.CertificateHandler = info => _dialogs.ConfirmCertificate(info);
    }

    public RemoteDesktopController Controller => _controller;

    public AppSettings Settings => _settings;

    public MonitorViewModel MainView { get; }

    /// <summary>Separate monitor windows that are currently open.</summary>
    public ObservableCollection<MonitorViewModel> ExtraViews { get; } = [];

    public IEnumerable<MonitorViewModel> AllViews => ExtraViews.Prepend(MainView);

    public IInputResetter? InputResetter { get; set; }

    /// <summary>The user wants the selected remote files (shortcut or toolbar); argument = view with focus.</summary>
    public event EventHandler<MonitorViewModel>? PullFilesRequested;

    /// <summary>File drag-out zone settings changed (settings dialog).</summary>
    public event EventHandler? DragOutSettingsChanged;

    public bool CanPullFiles => IsConnected && _controller.Profile?.RedirectClipboard == true;

    public ConnectionState State => _controller.State;

    public bool IsConnected => State == ConnectionState.Connected;

    public bool IsBusy => State is ConnectionState.Connecting or ConnectionState.Reconnecting or ConnectionState.Disconnecting;

    public bool IsDisconnected => State == ConnectionState.Disconnected;

    public string StatusText => State switch
    {
        ConnectionState.Connected => "Verbunden",
        ConnectionState.Connecting => "Verbinde …",
        ConnectionState.Reconnecting => "Neuverbindung …",
        ConnectionState.Disconnecting => "Trenne …",
        _ => "Getrennt",
    };

    public string StatusDetail => _controller.StatusMessage;

    public string ConnectButtonText => State == ConnectionState.Disconnected ? "Verbinden" : "Trennen";

    public string WindowTitle => _controller.Profile is { } p && State != ConnectionState.Disconnected
        ? $"{p.DisplayName} – RDP Monitor Switcher"
        : "RDP Monitor Switcher";

    /// <summary>Overlay text on the remote view while not connected (null = no overlay).</summary>
    public string? OverlayText => IsWaking ? WakeStatus : State switch
    {
        ConnectionState.Connected => null,
        ConnectionState.Disconnected when _controller.LastReason is DisconnectReason.None or DisconnectReason.User =>
            "Nicht verbunden. Klicke auf „Verbinden“.",
        _ => _controller.StatusMessage,
    };

    public bool ShowOverlay => IsWaking || OverlayText is not null;

    /// <summary>The "how do I reach my PC" guide below the overlay buttons.</summary>
    public bool ShowSetupGuide => IsDisconnected && !IsWaking;

    /// <summary>No connection saved yet: the guide starts expanded.</summary>
    public bool IsFirstRun => _settings.Profiles.Count == 0;

    public bool CanCancelReconnect => State == ConnectionState.Reconnecting;

    /// <summary>The last used connection can wake its PC (Wake-on-LAN configured).</summary>
    public bool CanWake => _waker is not null && IsDisconnected && !IsWaking &&
                           _settings.FindProfile(_settings.LastProfileKey)?.CanWake == true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanWake), nameof(OverlayText), nameof(ShowOverlay), nameof(ShowSetupGuide))]
    private bool _isWaking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverlayText))]
    private string? _wakeStatus;

    /// <summary>Wakes the PC of the last used connection, waits until it is reachable, then connects.</summary>
    [RelayCommand]
    private async Task WakeAndConnectAsync()
    {
        if (_waker is null || IsWaking || !IsDisconnected)
            return;
        var profile = _settings.FindProfile(_settings.LastProfileKey)?.Clone();
        if (profile is not { CanWake: true })
            return;

        using var cts = new CancellationTokenSource();
        _wakeCts = cts;
        IsWaking = true;
        WakeStatus = "Sende Weck-Signal …";
        try
        {
            string? fritzPassword = null;
            if (profile.WakeMethod == WakeMethod.FritzBox)
                fritzPassword = _credentials.TryGetPassword(profile.FritzBoxCredentialKey);
            var progress = new Progress<WakeProgress>(p => WakeStatus = p.Message);
            if (!await _waker.WakeAsync(profile, fritzPassword, progress, cts.Token))
            {
                Notice = $"{profile.DisplayName} ist nach {PcWaker.DefaultTimeout.TotalMinutes:0} Minuten noch nicht erreichbar. " +
                         "Wake-on-LAN im BIOS, „Wake on Magic Packet“ der Netzwerkkarte und den deaktivierten Schnellstart prüfen.";
                return;
            }

            IsWaking = false;
            WakeStatus = null;
            var password = profile.SavePassword ? _credentials.TryGetPassword(profile.Key) : null;
            if (!string.IsNullOrEmpty(password))
                ConnectWith(new ConnectRequest(profile, password));
            else
                Connect();
        }
        catch (OperationCanceledException)
        {
        }
        catch (WakeOnLanException ex)
        {
            Notice = ex.Message;
        }
        finally
        {
            IsWaking = false;
            WakeStatus = null;
            _wakeCts = null;
        }
    }

    [RelayCommand]
    private void CancelWake() => _wakeCts?.Cancel();

    public bool CanChangeLayoutLive => _controller.CanChangeLayoutLive;

    [ObservableProperty]
    private string? _notice;

    [ObservableProperty]
    private bool _isMultiWindow;

    [RelayCommand]
    private void ConnectOrDisconnect()
    {
        if (State == ConnectionState.Disconnected)
            Connect();
        else
            _controller.Disconnect();
    }

    [RelayCommand]
    private void Connect()
    {
        var profile = _settings.FindProfile(_settings.LastProfileKey)?.Clone() ?? new ConnectionProfile();
        var stored = string.IsNullOrEmpty(profile.Host) ? null : _credentials.TryGetPassword(profile.Key);
        var request = _dialogs.ShowConnectDialog(profile, stored, _settings.Profiles);
        if (request is null)
            return;
        ConnectWith(request);
    }

    /// <summary>Connects with an already confirmed profile and password.</summary>
    public void ConnectWith(ConnectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profile = request.Profile;
        var errors = profile.Validate();
        if (errors.Count > 0)
        {
            _dialogs.ShowError("Ungültige Verbindungseinstellungen", string.Join(Environment.NewLine, errors));
            return;
        }

        _settings.Remember(profile);
        try
        {
            if (profile.WakeMethod == WakeMethod.FritzBox && !string.IsNullOrEmpty(request.FritzBoxPassword))
                _credentials.SavePassword(profile.FritzBoxCredentialKey, profile.FritzBoxUser, request.FritzBoxPassword);
            if (profile.SavePassword && !string.IsNullOrEmpty(request.Password))
                _credentials.SavePassword(profile.Key, profile.Username, request.Password);
            else if (!profile.SavePassword)
                _credentials.DeletePassword(profile.Key);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not update the credential store");
        }
        SaveSettings();

        foreach (var view in AllViews)
            view.Scaling = profile.Scaling;

        var layout = BuildLayout(profile);
        _controller.Connect(profile, request.Password, layout, _environment.GetEnvironment(profile.RemoteScaling));
    }

    [RelayCommand]
    private void CancelReconnect() => _controller.Disconnect();

    [RelayCommand]
    private void PullRemoteFiles() => RequestPullFiles(MainView);

    public void RequestPullFiles(MonitorViewModel source)
    {
        if (!IsConnected)
            return;
        if (_controller.Profile?.RedirectClipboard != true)
        {
            Notice = "Dateien holen benötigt die geteilte Zwischenablage. Aktiviere „Zwischenablage teilen“ in den Verbindungseinstellungen.";
            return;
        }
        PullFilesRequested?.Invoke(this, source);
    }

    /// <summary>Applies new drag-out settings and saves them.</summary>
    /// <summary>The helper on the remote PC is running and reports the foreground window.</summary>
    public bool IsFocusAgentConnected => _controller.IsFocusAgentConnected;

    public void UpdateFollowRemoteForeground(bool enabled)
    {
        _settings.FollowRemoteForeground = enabled;
        SaveSettings();
    }

    /// <summary>
    /// A window came to the front on the remote PC: show its monitor in the main window,
    /// unless the main window or a separate monitor window already shows it.
    /// </summary>
    private void FollowRemoteForeground(PixelPoint point)
    {
        if (!_settings.FollowRemoteForeground || !IsConnected || _controller.Layout is not { } layout)
            return;
        var monitor = layout.FindMonitorAt(point);
        if (AllViews.Any(view => view.SelectedIndex == monitor.Index))
            return;
        _log.LogDebug("Following the remote foreground window to monitor {Monitor}", monitor.Number);
        MainView.SelectMonitorCommand.Execute(monitor.Index);
    }

    public void UpdateDragOutSettings(DragOutSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings.DragOut = settings.Normalized();
        SaveSettings();
        DragOutSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void DismissNotice() => Notice = null;

    [RelayCommand]
    private void AddMonitor() => ChangeMonitorCount(+1);

    [RelayCommand]
    private void RemoveMonitor() => ChangeMonitorCount(-1);

    /// <summary>Changes the number of remote monitors (FitToWindow and Fixed mode).</summary>
    public void ChangeMonitorCount(int delta)
    {
        var profile = _controller.Profile;
        if (profile is null)
            return;
        var current = _controller.DesiredLayout?.Count ?? profile.EffectiveMonitorCount;
        var count = Math.Clamp(current + delta, 1, MonitorLayout.MaxMonitors);
        if (count == current)
            return;

        profile.MonitorCount = count;
        if (profile.SizeMode == MonitorSizeMode.Fixed)
        {
            while (profile.FixedMonitors.Count < count)
                profile.FixedMonitors.Add(profile.FixedMonitors.LastOrDefault(FallbackMonitorSize));
            while (profile.FixedMonitors.Count > count)
                profile.FixedMonitors.RemoveAt(profile.FixedMonitors.Count - 1);
        }
        _settings.Remember(profile);
        SaveSettings();
        _controller.RequestLayout(BuildLayout(profile));
    }

    /// <summary>Opens one window for every remote monitor not shown in the main window.</summary>
    [RelayCommand]
    private void OpenAllMonitorWindows()
    {
        if (_controller.Layout is not { } layout)
            return;
        foreach (var monitor in layout.Monitors)
        {
            if (monitor.Index != MainView.SelectedIndex)
                OpenMonitorWindow(monitor.Index);
        }
    }

    /// <summary>Opens (or activates) a separate window for one remote monitor.</summary>
    [RelayCommand]
    private void OpenMonitorWindow(int index)
    {
        if (_controller.Layout is not { } layout)
            return;
        if (index < 0 || index >= layout.Count)
        {
            MainView.ErrorMessage = $"Monitor {index + 1} ist nicht verfügbar.";
            return;
        }

        var existing = ExtraViews.FirstOrDefault(v => v.SelectedIndex == index);
        if (existing is not null)
        {
            _windows.ActivateMonitorWindow(existing);
            return;
        }

        var view = new MonitorViewModel(isMain: false, initialMonitorIndex: index) { Scaling = MainView.Scaling };
        view.SetMonitors(layout.Monitors);
        view.GeometryChanged += (_, _) => OnViewGeometryChanged();
        view.CloseRequested += (_, _) => CloseMonitorWindow(view);
        ExtraViews.Add(view);
        IsMultiWindow = true;
        _windows.ShowMonitorWindow(view);
    }

    /// <summary>Called by the window service when a monitor window was closed by the user.</summary>
    public void OnMonitorWindowClosed(MonitorViewModel view)
    {
        if (!ExtraViews.Remove(view))
            return;
        IsMultiWindow = ExtraViews.Count > 0;
        OnViewGeometryChanged();
    }

    [RelayCommand]
    private void CloseAllMonitorWindows()
    {
        foreach (var view in ExtraViews.ToList())
            CloseMonitorWindow(view);
    }

    private void CloseMonitorWindow(MonitorViewModel view)
    {
        _windows.CloseMonitorWindow(view);
        OnMonitorWindowClosed(view);
    }

    /// <summary>Executes a keyboard shortcut that was pressed in <paramref name="source"/>.</summary>
    public void Execute(AppCommand command, MonitorViewModel source)
    {
        ArgumentNullException.ThrowIfNull(source);
        switch (command.Kind)
        {
            case AppCommandKind.PreviousMonitor:
                ReleaseInput();
                source.PreviousMonitorCommand.Execute(null);
                break;
            case AppCommandKind.NextMonitor:
                ReleaseInput();
                source.NextMonitorCommand.Execute(null);
                break;
            case AppCommandKind.SelectMonitor:
                ReleaseInput();
                source.SelectMonitorCommand.Execute(command.MonitorIndex);
                break;
            case AppCommandKind.ToggleFullscreen:
                source.ToggleFullscreenCommand.Execute(null);
                break;
            case AppCommandKind.ToggleToolbar:
                source.ToggleToolbarCommand.Execute(null);
                break;
            case AppCommandKind.OpenAllMonitorWindows:
                OpenAllMonitorWindows();
                break;
            case AppCommandKind.SendCtrlAltDel:
                SendCtrlAltDel();
                break;
            case AppCommandKind.PullRemoteFiles:
                RequestPullFiles(source);
                break;
        }
    }

    [RelayCommand]
    private void SendCtrlAltDel()
    {
        if (_controller.Session is not { } session || !IsConnected)
            return;
        ReleaseInput();
        const uint ctrl = 0x1D, alt = 0x38, del = 0x53 | 0x100;
        session.SendKey(true, ctrl);
        session.SendKey(true, alt);
        session.SendKey(true, del);
        session.SendKey(false, del);
        session.SendKey(false, alt);
        session.SendKey(false, ctrl);
    }

    public void SaveSettings()
    {
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not save settings");
        }
    }

    /// <summary>The layout to request for the current views and profile.</summary>
    public MonitorLayout BuildLayout(ConnectionProfile profile)
    {
        if (profile.SizeMode == MonitorSizeMode.Fixed)
            return MonitorLayout.CreateRow(profile.FixedMonitors);

        var count = Math.Clamp(profile.MonitorCount, 1, MonitorLayout.MaxMonitors);
        var mainSize = MainView.ViewPixelSize.IsEmpty ? FallbackMonitorSize : MainView.ViewPixelSize;
        var current = _controller.Layout;
        var sizes = new PixelSize[count];
        for (var i = 0; i < count; i++)
        {
            var extra = ExtraViews.LastOrDefault(v => v.SelectedIndex == i && !v.ViewPixelSize.IsEmpty);
            if (extra is not null)
                sizes[i] = extra.ViewPixelSize;
            else if (ExtraViews.Count == 0 || MainView.SelectedIndex == i)
                sizes[i] = mainSize;
            else if (current is not null && i < current.Count)
                sizes[i] = current.Monitors[i].Bounds.Size; // not visible anywhere: keep its size
            else
                sizes[i] = mainSize;
        }
        return MonitorLayout.CreateRow(sizes);
    }

    private void OnViewGeometryChanged()
    {
        if (_controller.Profile is { SizeMode: MonitorSizeMode.FitToWindow } profile && State != ConnectionState.Disconnected)
            _controller.RequestLayout(BuildLayout(profile));
    }

    private void OnLayoutChanged()
    {
        var monitors = _controller.Layout?.Monitors ?? [];

        // Close windows whose monitor no longer exists (before the selection gets clamped).
        foreach (var view in ExtraViews.Where(v => v.SelectedIndex >= monitors.Count).ToList())
            CloseMonitorWindow(view);

        foreach (var view in AllViews)
            view.SetMonitors(monitors);
        OnPropertyChanged(nameof(CanChangeLayoutLive));
    }

    private void OnControllerStateChanged()
    {
        if (State == ConnectionState.Disconnected)
        {
            CloseAllMonitorWindows();
            foreach (var view in AllViews)
                view.SetMonitors([]);
        }
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsDisconnected));
        OnPropertyChanged(nameof(ShowSetupGuide));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(ConnectButtonText));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(OverlayText));
        OnPropertyChanged(nameof(ShowOverlay));
        OnPropertyChanged(nameof(CanCancelReconnect));
        OnPropertyChanged(nameof(CanChangeLayoutLive));
        OnPropertyChanged(nameof(CanPullFiles));
        OnPropertyChanged(nameof(CanWake));
    }

    private void ReleaseInput() => InputResetter?.ReleaseAll();

    public void Dispose()
    {
        SaveSettings();
        _controller.Dispose();
    }
}
