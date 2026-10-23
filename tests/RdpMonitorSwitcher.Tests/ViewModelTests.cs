using Microsoft.Extensions.Time.Testing;
using RdpMonitorSwitcher.Core.Configuration;
using RdpMonitorSwitcher.Core.Models;
using RdpMonitorSwitcher.Core.Services;
using RdpMonitorSwitcher.Core.ViewModels;

namespace RdpMonitorSwitcher.Tests;

public class MonitorViewModelTests
{
    private static MonitorViewModel WithMonitors(int count)
    {
        var view = new MonitorViewModel(isMain: true);
        view.SetMonitors(MonitorLayout.CreateUniform(count, new PixelSize(1920, 1080)).Monitors);
        return view;
    }

    [Fact]
    public void ButtonsFollowTheMonitorCount_AndMarkTheActiveOne()
    {
        var view = WithMonitors(3);

        Assert.Equal(3, view.MonitorButtons.Count);
        Assert.True(view.MonitorButtons[0].IsSelected);
        Assert.Equal("Monitor 2", view.MonitorButtons[1].Label);

        view.SelectMonitorCommand.Execute(2);

        Assert.Equal(2, view.SelectedIndex);
        Assert.False(view.MonitorButtons[0].IsSelected);
        Assert.True(view.MonitorButtons[2].IsSelected);
    }

    [Fact]
    public void NextAndPrevious_WrapAround()
    {
        var view = WithMonitors(3);

        view.PreviousMonitorCommand.Execute(null);
        Assert.Equal(2, view.SelectedIndex);

        view.NextMonitorCommand.Execute(null);
        Assert.Equal(0, view.SelectedIndex);
    }

    [Fact]
    public void SelectingMissingMonitor_ShowsErrorAndKeepsSelection()
    {
        var view = WithMonitors(2);

        view.SelectMonitorCommand.Execute(4);

        Assert.Equal(0, view.SelectedIndex);
        Assert.Contains("Monitor 5 ist nicht verfügbar", view.ErrorMessage);
        Assert.Contains("2 Monitore", view.ErrorMessage);
    }

    [Fact]
    public void ShrinkingLayout_ClampsSelection()
    {
        var view = WithMonitors(3);
        view.SelectMonitorCommand.Execute(2);

        view.SetMonitors(MonitorLayout.CreateUniform(2, new PixelSize(1920, 1080)).Monitors);

        Assert.Equal(1, view.SelectedIndex);
        Assert.Equal(2, view.MonitorButtons.Count);
    }

    [Fact]
    public void GeometryChanged_FiresOnSelectionAndSizeChanges()
    {
        var view = WithMonitors(2);
        var count = 0;
        view.GeometryChanged += (_, _) => count++;

        view.NextMonitorCommand.Execute(null);
        view.ReportViewSize(new PixelSize(1000, 700));
        view.ReportViewSize(new PixelSize(1000, 700)); // unchanged

        Assert.Equal(2, count);
    }

    [Fact]
    public void Toggles()
    {
        var view = WithMonitors(1);

        view.ToggleFullscreenCommand.Execute(null);
        view.ToggleToolbarCommand.Execute(null);
        view.ToggleScalingCommand.Execute(null);

        Assert.True(view.IsFullscreen);
        Assert.False(view.IsToolbarVisible);
        Assert.Equal(ScalingMode.Original, view.Scaling);
    }
}

public sealed class MainViewModelTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly FakeSessionFactory _factory = new();
    private readonly FakeTimeProvider _time = new();
    private readonly FakeCredentialStore _credentials = new();
    private readonly FakeDialogService _dialogs = new();
    private readonly FakeWindowService _windows = new();
    private readonly FakeInputResetter _input = new();
    private readonly MainViewModel _vm;

    public MainViewModelTests()
    {
        var controller = new RemoteDesktopController(_factory, new ReconnectPolicy(), _time);
        _vm = new MainViewModel(controller, new SettingsStore(_dir.File("settings.json")), _credentials,
            _dialogs, _windows, new FakeEnvironment()) { InputResetter = _input };
        _vm.MainView.ReportViewSize(new PixelSize(1600, 900));
    }

    public void Dispose()
    {
        _vm.Dispose();
        _dir.Dispose();
    }

    private FakeSession Connect(int monitors = 3)
    {
        _vm.ConnectWith(new ConnectRequest(TestProfiles.Office(monitors), "secret"));
        var session = _factory.Last;
        session.RaiseConnected();
        session.RaiseDisplayControlReady();
        return session;
    }

    [Fact]
    public void Connect_UsesDialogResult_StoresPasswordSecurely_AndRemembersProfile()
    {
        _dialogs.NextConnect = new ConnectRequest(TestProfiles.Office(), "secret");

        _vm.ConnectCommand.Execute(null);

        Assert.Equal(1, _dialogs.ConnectDialogs);
        Assert.Equal("secret", _credentials.Passwords[TestProfiles.Office().Key]);
        Assert.Equal("office-pc", _vm.Settings.Profiles[0].Host);
        Assert.Equal(ConnectionState.Connecting, _vm.State);
    }

    [Fact]
    public void Password_NeverEndsUpInTheSettingsFile()
    {
        _vm.ConnectWith(new ConnectRequest(TestProfiles.Office(), "Sup3r-Geheim!"));
        _vm.SaveSettings();

        var json = File.ReadAllText(_dir.File("settings.json"));

        Assert.DoesNotContain("Sup3r-Geheim!", json);
        Assert.Equal("Sup3r-Geheim!", _credentials.Passwords[TestProfiles.Office().Key]);
    }

    [Fact]
    public void Connect_Cancelled_DoesNothing()
    {
        _vm.ConnectCommand.Execute(null);

        Assert.Empty(_factory.Created);
    }

    [Fact]
    public void Connect_WithoutSavePassword_RemovesStoredPassword()
    {
        _credentials.Passwords[TestProfiles.Office().Key] = "old";
        var profile = TestProfiles.Office();
        profile.SavePassword = false;

        _vm.ConnectWith(new ConnectRequest(profile, "secret"));

        Assert.Empty(_credentials.Passwords);
    }

    [Fact]
    public void FitMode_AllMonitorsGetTheMainViewSize()
    {
        Connect(3);

        var layout = _factory.Last.ConnectedWith!.Layout;

        Assert.Equal(3, layout.Count);
        Assert.All(layout.Monitors, m => Assert.Equal(new PixelSize(1600, 900), m.Bounds.Size));
    }

    [Fact]
    public void FixedMode_UsesProfileResolutions()
    {
        var profile = TestProfiles.Office();
        profile.SizeMode = MonitorSizeMode.Fixed;
        profile.FixedMonitors = [new(2560, 1440), new(1920, 1080)];

        _vm.ConnectWith(new ConnectRequest(profile, "secret"));

        Assert.Equal(profile.FixedMonitors, _factory.Last.ConnectedWith!.Layout.Sizes);
    }

    [Fact]
    public void MonitorButtons_AppearAfterConnect_AndDisappearAfterDisconnect()
    {
        var session = Connect(3);
        Assert.Equal(3, _vm.MainView.MonitorButtons.Count);
        Assert.True(_vm.IsConnected);

        _vm.ConnectOrDisconnectCommand.Execute(null);
        session.RaiseDisconnected(Core.Models.DisconnectReason.User);

        Assert.Empty(_vm.MainView.MonitorButtons);
        Assert.True(_vm.IsDisconnected);
        Assert.Equal("Verbinden", _vm.ConnectButtonText);
    }

    [Fact]
    public void Shortcut_SwitchesMonitorOfTheFocusedView_AndReleasesInput()
    {
        Connect(3);

        _vm.Execute(new AppCommand(AppCommandKind.SelectMonitor, 2), _vm.MainView);
        Assert.Equal(2, _vm.MainView.SelectedIndex);

        _vm.Execute(new AppCommand(AppCommandKind.NextMonitor), _vm.MainView);
        Assert.Equal(0, _vm.MainView.SelectedIndex);
        Assert.Equal(2, _input.Calls);
    }

    [Fact]
    public void MonitorSwitch_InSingleWindowMode_KeepsSessionAndLayout()
    {
        var session = Connect(3);

        _vm.MainView.SelectMonitorCommand.Execute(1);
        _time.Advance(TimeSpan.FromSeconds(10));

        Assert.Single(_factory.Created);
        Assert.Empty(session.LayoutRequests);
        Assert.Equal(0, session.DisconnectCalls);
    }

    [Fact]
    public void OpenAllMonitorWindows_OpensOneWindowPerOtherMonitor_SharingTheSession()
    {
        Connect(3);

        _vm.OpenAllMonitorWindowsCommand.Execute(null);

        Assert.Equal(2, _windows.Open.Count);
        Assert.Equal([1, 2], _windows.Open.Select(v => v.SelectedIndex));
        Assert.True(_vm.IsMultiWindow);
        Assert.Single(_factory.Created); // still one RDP connection
    }

    [Fact]
    public void OpenMonitorWindow_Twice_ActivatesExistingWindow()
    {
        Connect(3);

        _vm.OpenMonitorWindowCommand.Execute(1);
        _vm.OpenMonitorWindowCommand.Execute(1);

        Assert.Single(_windows.Open);
        Assert.Equal(1, _windows.Activations);
    }

    [Fact]
    public void MultiWindow_EachMonitorFollowsItsWindowSize()
    {
        var session = Connect(3);
        _vm.OpenMonitorWindowCommand.Execute(1);
        var window = _windows.Open[0];

        window.ReportViewSize(new PixelSize(1024, 768));
        _time.Advance(RemoteDesktopController.LayoutDebounce);

        var requested = session.LayoutRequests[^1];
        Assert.Equal(new PixelSize(1600, 900), requested.Monitors[0].Bounds.Size); // main window
        Assert.Equal(new PixelSize(1024, 768), requested.Monitors[1].Bounds.Size); // separate window
        Assert.Equal(new PixelSize(1600, 900), requested.Monitors[2].Bounds.Size); // not shown: unchanged
    }

    [Fact]
    public void ClosingAMonitorWindow_RemovesItsView()
    {
        Connect(3);
        _vm.OpenAllMonitorWindowsCommand.Execute(null);
        var view = _vm.ExtraViews[0];

        view.CloseCommand.Execute(null);

        Assert.Single(_vm.ExtraViews);
        Assert.Single(_windows.Open);
    }

    [Fact]
    public void Disconnect_ClosesAllMonitorWindows()
    {
        var session = Connect(3);
        _vm.OpenAllMonitorWindowsCommand.Execute(null);

        _vm.ConnectOrDisconnectCommand.Execute(null);
        session.RaiseDisconnected(DisconnectReason.User);

        Assert.Empty(_windows.Open);
        Assert.False(_vm.IsMultiWindow);
    }

    [Fact]
    public void RemovingMonitors_ClosesWindowsOfMissingMonitors()
    {
        var session = Connect(3);
        _vm.OpenMonitorWindowCommand.Execute(2);

        _vm.RemoveMonitorCommand.Execute(null);
        _time.Advance(RemoteDesktopController.LayoutDebounce);
        var requested = session.LayoutRequests[^1];
        session.RaiseDesktopResized(requested.DesktopSize);

        Assert.Equal(2, _vm.Controller.Layout!.Count);
        Assert.Empty(_windows.Open);
    }

    [Fact]
    public void AddMonitor_RequestsLiveLayoutWithOneMoreMonitor()
    {
        var session = Connect(2);

        _vm.AddMonitorCommand.Execute(null);
        _time.Advance(RemoteDesktopController.LayoutDebounce);

        Assert.Equal(3, session.LayoutRequests[^1].Count);
        Assert.Equal(0, session.DisconnectCalls);
    }

    [Fact]
    public void SendCtrlAltDel_SendsBalancedKeySequence()
    {
        var session = Connect(1);

        _vm.SendCtrlAltDelCommand.Execute(null);

        Assert.Equal(6, session.Keys.Count);
        Assert.Equal((true, 0x1Du), session.Keys[0]);
        Assert.Equal((true, 0x153u), session.Keys[2]);
        Assert.All(session.Keys.GroupBy(k => k.Scancode), g => Assert.Equal(2, g.Count()));
    }

    [Fact]
    public void PullFiles_FromShortcut_ReachesTheFileTransferWithTheFocusedView()
    {
        Connect(2);
        _vm.OpenMonitorWindowCommand.Execute(1);
        MonitorViewModel? requested = null;
        _vm.PullFilesRequested += (_, view) => requested = view;

        _vm.Execute(new AppCommand(AppCommandKind.PullRemoteFiles), _vm.ExtraViews[0]);

        Assert.Same(_vm.ExtraViews[0], requested);
        Assert.True(_vm.CanPullFiles);
    }

    [Fact]
    public void PullFiles_WithClipboardDisabled_ExplainsWhy()
    {
        var profile = TestProfiles.Office();
        profile.RedirectClipboard = false;
        _vm.ConnectWith(new ConnectRequest(profile, "secret"));
        _factory.Last.RaiseConnected();
        var raised = false;
        _vm.PullFilesRequested += (_, _) => raised = true;

        _vm.PullRemoteFilesCommand.Execute(null);

        Assert.False(raised);
        Assert.False(_vm.CanPullFiles);
        Assert.Contains("Zwischenablage", _vm.Notice);
    }

    [Fact]
    public void DragOutSettings_AreSavedAndAnnounced()
    {
        var changed = 0;
        _vm.DragOutSettingsChanged += (_, _) => changed++;

        _vm.UpdateDragOutSettings(new DragOutSettings { Edge = ScreenEdge.Left, Size = 40 });
        var reloaded = new SettingsStore(_dir.File("settings.json")).Load();

        Assert.Equal(1, changed);
        Assert.Equal(ScreenEdge.Left, reloaded.DragOut.Edge);
        Assert.Equal(40, reloaded.DragOut.Size);
    }

    [Fact]
    public async Task WakeAndConnect_WakesThePc_ThenConnectsWithTheSavedPassword()
    {
        var udp = new FakeMagicPacketSender();
        var probes = 0;
        var waker = new PcWaker(udp, new FritzBoxClient(new FakeFritzBox().CreateHandler),
            (_, _, _) => Task.FromResult(++probes > 2), _time);
        using var vm = new MainViewModel(_vm.Controller, new SettingsStore(_dir.File("wake.json")), _credentials,
            _dialogs, _windows, new FakeEnvironment(), waker: waker);
        var profile = TestProfiles.Office();
        profile.WakeMethod = WakeMethod.MagicPacket;
        profile.MacAddress = "00-11-22-AA-BB-CC";
        vm.Settings.Remember(profile);
        _credentials.Passwords[profile.Key] = "secret";
        Assert.True(vm.CanWake);

        var task = vm.WakeAndConnectCommand.ExecuteAsync(null);
        Assert.True(vm.IsWaking);
        while (!task.IsCompleted)
        {
            _time.Advance(PcWaker.ProbeInterval);
            await Task.Delay(1);
        }
        await task;

        Assert.Single(udp.Sent);
        Assert.False(vm.IsWaking);
        Assert.Equal("secret", _factory.Last.ConnectedWith!.Password);
        Assert.Equal(ConnectionState.Connecting, vm.State);
    }

    [Fact]
    public void FritzBoxPassword_IsStoredSeparately_InTheCredentialStore()
    {
        var profile = TestProfiles.Office();
        profile.WakeMethod = WakeMethod.FritzBox;
        profile.MacAddress = "00-11-22-AA-BB-CC";
        profile.FritzBoxUser = "fb";

        _vm.ConnectWith(new ConnectRequest(profile, "secret", "fritz-pw"));
        _vm.SaveSettings();

        Assert.Equal("fritz-pw", _credentials.Passwords[profile.FritzBoxCredentialKey]);
        Assert.DoesNotContain("fritz-pw", File.ReadAllText(_dir.File("settings.json")));
    }

    [Fact]
    public void Overlay_ShowsErrorAfterFailedConnect()
    {
        _vm.ConnectWith(new ConnectRequest(TestProfiles.Office(), "wrong"));

        _factory.Last.RaiseDisconnected(DisconnectReason.Authentication);

        Assert.True(_vm.ShowOverlay);
        Assert.Contains("Anmeldung fehlgeschlagen", _vm.OverlayText);
    }

    [Fact]
    public void Settings_ArePersistedOnDispose()
    {
        Connect(2);
        _vm.MainView.ToggleToolbarCommand.Execute(null);

        _vm.SaveSettings();
        var reloaded = new SettingsStore(_dir.File("settings.json")).Load();

        Assert.False(reloaded.ToolbarVisible);
        Assert.Equal("office-pc", reloaded.Profiles[0].Host);
    }

    [Fact]
    public void RemoteForeground_SwitchesMainViewToThatMonitor()
    {
        var session = Connect(3);

        session.RaiseRemoteForeground(1920 * 2 + 960, 540); // centre of monitor 3

        Assert.Equal(2, _vm.MainView.SelectedIndex);
    }

    [Fact]
    public void RemoteForeground_OnMonitorShownInSeparateWindow_RaisesThatWindowAndKeepsMainView()
    {
        var session = Connect(3);
        _vm.OpenMonitorWindowCommand.Execute(1);

        session.RaiseRemoteForeground(1920 + 960, 540);

        Assert.Equal(0, _vm.MainView.SelectedIndex);
        Assert.Equal(1, Assert.Single(_windows.Raised).SelectedIndex);
        Assert.Equal(0, _windows.Activations); // raised, not activated: the keyboard focus stays where it is
    }

    [Fact]
    public void RemoteForeground_OnMonitorShownInMainWindow_RaisesNothing()
    {
        var session = Connect(3);
        _vm.OpenMonitorWindowCommand.Execute(1);

        session.RaiseRemoteForeground(960, 540);

        Assert.Empty(_windows.Raised);
    }

    [Fact]
    public void RemoteForeground_IsIgnoredWhenDisabled()
    {
        var session = Connect(3);
        _vm.UpdateFollowRemoteForeground(false);

        session.RaiseRemoteForeground(1920 + 960, 540);

        Assert.Equal(0, _vm.MainView.SelectedIndex);
        Assert.False(new SettingsStore(_dir.File("settings.json")).Load().FollowRemoteForeground);
    }

    [Fact]
    public void FocusAgentState_IsExposedAndResetOnDisconnect()
    {
        var session = Connect(2);
        var changes = new List<string?>();
        _vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        session.RaiseFocusAgent(true);
        Assert.True(_vm.IsFocusAgentConnected);
        Assert.Contains(nameof(MainViewModel.IsFocusAgentConnected), changes);

        session.RaiseDisconnected(DisconnectReason.Network);
        Assert.False(_vm.IsFocusAgentConnected);
    }
}
