using Microsoft.Extensions.Time.Testing;
using RdpMonitorSwitcher.Core.Models;
using RdpMonitorSwitcher.Core.Rdp;
using RdpMonitorSwitcher.Core.Services;

namespace RdpMonitorSwitcher.Tests;

public class RemoteDesktopControllerTests
{
    private static readonly PixelSize Hd = new(1920, 1080);

    private readonly FakeSessionFactory _factory = new();
    private readonly FakeTimeProvider _time = new();
    private readonly RemoteDesktopController _controller;

    public RemoteDesktopControllerTests()
    {
        _controller = new RemoteDesktopController(_factory, new ReconnectPolicy(maxAttempts: 3), _time);
    }

    private FakeSession ConnectAndEstablish(int monitors = 2)
    {
        _controller.Connect(TestProfiles.Office(monitors), "secret", MonitorLayout.CreateUniform(monitors, Hd), LocalEnvironment.Default);
        var session = _factory.Last;
        session.RaiseState(ConnectionState.Connecting);
        session.RaiseConnected();
        return session;
    }

    [Fact]
    public void Connect_PassesLayoutAndCredentialsToSession()
    {
        _controller.Connect(TestProfiles.Office(3), "secret", MonitorLayout.CreateUniform(3, Hd), LocalEnvironment.Default);

        var p = _factory.Last.ConnectedWith!;
        Assert.Equal("office-pc", p.Host);
        Assert.Equal("alex", p.Username);
        Assert.Equal("secret", p.Password);
        Assert.Equal(3, p.Layout.Count);
        Assert.Equal(ConnectionState.Connecting, _controller.State);
        Assert.DoesNotContain("secret", p.ToString());
    }

    [Fact]
    public void Connect_RejectsInvalidProfile()
    {
        Assert.Throws<ArgumentException>(() =>
            _controller.Connect(new ConnectionProfile(), "x", MonitorLayout.CreateUniform(1, Hd), LocalEnvironment.Default));
    }

    [Fact]
    public void Connected_UpdatesStateAndLayout()
    {
        var layoutChanges = 0;
        _controller.LayoutChanged += (_, _) => layoutChanges++;

        ConnectAndEstablish(2);

        Assert.Equal(ConnectionState.Connected, _controller.State);
        Assert.Equal(2, _controller.Layout!.Count);
        Assert.True(layoutChanges >= 1);
    }

    [Fact]
    public void UserDisconnect_DoesNotReconnect()
    {
        var session = ConnectAndEstablish();

        _controller.Disconnect();
        Assert.Equal(ConnectionState.Disconnecting, _controller.State);
        Assert.Equal(1, session.DisconnectCalls);

        session.RaiseDisconnected(DisconnectReason.User);
        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(ConnectionState.Disconnected, _controller.State);
        Assert.Single(_factory.Created);
        Assert.True(session.Disposed);
    }

    [Fact]
    public void NetworkLoss_ReconnectsAfterBackoff_AndKeepsLayout()
    {
        var first = ConnectAndEstablish(3);

        first.RaiseDisconnected(DisconnectReason.Network);

        Assert.Equal(ConnectionState.Reconnecting, _controller.State);
        Assert.Equal(1, _controller.ReconnectAttempt);
        Assert.Single(_factory.Created);

        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(2, _factory.Created.Count);
        var second = _factory.Last;
        Assert.Equal(3, second.ConnectedWith!.Layout.Count);
        Assert.Equal("secret", second.ConnectedWith.Password);

        second.RaiseConnected();
        Assert.Equal(ConnectionState.Connected, _controller.State);
        Assert.Equal(0, _controller.ReconnectAttempt);
    }

    [Fact]
    public void Reconnect_GivesUpAfterMaxAttempts()
    {
        ConnectAndEstablish().RaiseDisconnected(DisconnectReason.Network);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            _time.Advance(TimeSpan.FromSeconds(30));
            _factory.Last.RaiseDisconnected(DisconnectReason.Network);
        }

        Assert.Equal(ConnectionState.Disconnected, _controller.State);
        Assert.Equal(DisconnectReason.Network, _controller.LastReason);
        Assert.Equal(4, _factory.Created.Count);
    }

    [Fact]
    public void FailedInitialConnect_IsNotRetried()
    {
        _controller.Connect(TestProfiles.Office(), "wrong", MonitorLayout.CreateUniform(2, Hd), LocalEnvironment.Default);

        _factory.Last.RaiseDisconnected(DisconnectReason.Authentication);
        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(ConnectionState.Disconnected, _controller.State);
        Assert.Equal(DisconnectReason.Authentication, _controller.LastReason);
        Assert.Contains("Anmeldung", _controller.StatusMessage);
        Assert.Single(_factory.Created);
    }

    [Theory]
    [InlineData(DisconnectReason.ServerEnded)]
    [InlineData(DisconnectReason.Replaced)]
    public void ServerDecisions_AreNotRetried(DisconnectReason reason)
    {
        ConnectAndEstablish().RaiseDisconnected(reason);
        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(ConnectionState.Disconnected, _controller.State);
        Assert.Single(_factory.Created);
    }

    [Fact]
    public void Reconnecting_CanBeCancelled()
    {
        ConnectAndEstablish().RaiseDisconnected(DisconnectReason.Network);

        _controller.Disconnect();
        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(ConnectionState.Disconnected, _controller.State);
        Assert.Single(_factory.Created);
    }

    [Fact]
    public void LayoutChange_IsAppliedLiveAfterDebounce_WithoutNewConnection()
    {
        var session = ConnectAndEstablish(2);
        session.RaiseDisplayControlReady();
        var desired = MonitorLayout.CreateUniform(3, Hd);

        _controller.RequestLayout(desired);
        Assert.Empty(session.LayoutRequests); // debounced

        _time.Advance(RemoteDesktopController.LayoutDebounce);
        Assert.Equal(desired, Assert.Single(session.LayoutRequests));

        session.RaiseDesktopResized(desired.DesktopSize);

        Assert.Equal(desired, _controller.Layout);
        Assert.Single(_factory.Created);
        Assert.Equal(0, session.DisconnectCalls);
    }

    [Fact]
    public void RapidResizes_AreCoalesced()
    {
        var session = ConnectAndEstablish(1);
        session.RaiseDisplayControlReady();

        for (var w = 1000; w < 1100; w += 10)
        {
            _controller.RequestLayout(MonitorLayout.CreateUniform(1, new PixelSize(w, 700)));
            _time.Advance(TimeSpan.FromMilliseconds(50));
        }
        _time.Advance(RemoteDesktopController.LayoutDebounce);

        var request = Assert.Single(session.LayoutRequests);
        Assert.Equal(1090, request.Monitors[0].Bounds.Width);
    }

    [Fact]
    public void MonitorCountChange_WithoutDisplayControl_ReconnectsWithNewLayout()
    {
        string? notice = null;
        _controller.Notice += (_, n) => notice = n;
        var session = ConnectAndEstablish(2);
        var desired = MonitorLayout.CreateUniform(3, Hd);

        _controller.RequestLayout(desired);
        _time.Advance(RemoteDesktopController.LayoutDebounce);

        Assert.Equal(1, session.DisconnectCalls);
        Assert.NotNull(notice);

        session.RaiseDisconnected(DisconnectReason.User); // our own disconnect
        Assert.Equal(2, _factory.Created.Count);
        Assert.Equal(desired, _factory.Last.ConnectedWith!.Layout);
        Assert.Equal(ConnectionState.Reconnecting, _controller.State);
    }

    [Fact]
    public void SizeChange_WithoutDisplayControl_KeepsSessionAndScales()
    {
        var session = ConnectAndEstablish(2);

        _controller.RequestLayout(MonitorLayout.CreateUniform(2, new PixelSize(1600, 900)));
        _time.Advance(RemoteDesktopController.LayoutDebounce);

        Assert.Equal(0, session.DisconnectCalls);
        Assert.Equal(MonitorLayout.CreateUniform(2, Hd), _controller.Layout);
    }

    [Fact]
    public void UnconfirmedCountChange_FallsBackToReconnect()
    {
        var session = ConnectAndEstablish(2);
        session.RaiseDisplayControlReady();

        _controller.RequestLayout(MonitorLayout.CreateUniform(3, Hd));
        _time.Advance(RemoteDesktopController.LayoutDebounce);
        Assert.Single(session.LayoutRequests);

        _time.Advance(RemoteDesktopController.LayoutConfirmTimeout);

        Assert.Equal(1, session.DisconnectCalls);
    }

    [Fact]
    public void LayoutRequestedWhileConnecting_IsAppliedOnceDisplayControlIsReady()
    {
        _controller.Connect(TestProfiles.Office(2), "secret", MonitorLayout.CreateUniform(2, Hd), LocalEnvironment.Default);
        var session = _factory.Last;
        var desired = MonitorLayout.CreateUniform(2, new PixelSize(1600, 900));

        _controller.RequestLayout(desired);
        session.RaiseConnected();
        Assert.Empty(session.LayoutRequests);

        session.RaiseDisplayControlReady();

        Assert.Equal(desired, Assert.Single(session.LayoutRequests));
    }

    [Fact]
    public void CertificateRequests_AreForwardedToHandler()
    {
        _controller.CertificateHandler = _ => CertificateDecision.AcceptOnce;
        _controller.Connect(TestProfiles.Office(), "secret", MonitorLayout.CreateUniform(2, Hd), LocalEnvironment.Default);

        var decision = _factory.Last.CertificateRequested!(new CertificateInfo("h", 3389, "cn", "s", "i", "fp", null));

        Assert.Equal(CertificateDecision.AcceptOnce, decision);
    }

    [Fact]
    public void EventsOfAnOldSession_AreIgnored()
    {
        var first = ConnectAndEstablish();
        first.RaiseDisconnected(DisconnectReason.Network);
        _time.Advance(TimeSpan.FromSeconds(1));

        first.RaiseConnected(); // stale event from the disposed session

        Assert.Equal(ConnectionState.Reconnecting, _controller.State);
    }

    [Fact]
    public void FocusAgentEvents_AreForwarded_AndIgnoredFromOldSessions()
    {
        var points = new List<PixelPoint>();
        _controller.RemoteForeground += (_, p) => points.Add(p);
        var first = ConnectAndEstablish();

        first.RaiseFocusAgent(true);
        first.RaiseRemoteForeground(100, 200);
        Assert.True(_controller.IsFocusAgentConnected);
        Assert.Equal([new PixelPoint(100, 200)], points);

        first.RaiseDisconnected(DisconnectReason.Network);
        Assert.False(_controller.IsFocusAgentConnected);

        _time.Advance(TimeSpan.FromSeconds(1));
        first.RaiseFocusAgent(true); // stale
        first.RaiseRemoteForeground(1, 1);
        Assert.False(_controller.IsFocusAgentConnected);
        Assert.Single(points);
    }

    [Fact]
    public void ResizeWithDisplayControl_StillAppliesLive()
    {
        var session = ConnectAndEstablish(2);
        session.RaiseDisplayControlReady();

        _controller.RequestLayout(MonitorLayout.CreateRow([new(1600, 900), new(1920, 1080)]));
        _time.Advance(RemoteDesktopController.LayoutDebounce);

        Assert.Single(session.LayoutRequests);
        Assert.Equal(0, session.DisconnectCalls);
    }

    [Fact]
    public void ArrangementOnlyChange_ReconnectsInsteadOfUnconfirmableLiveChange()
    {
        var session = ConnectAndEstablish(2);
        session.RaiseDisplayControlReady();
        var left = MonitorLayout.CreateUniform(2, Hd, MonitorArrangement.Left);

        _controller.RequestLayout(left);
        _time.Advance(RemoteDesktopController.LayoutDebounce);

        Assert.Empty(session.LayoutRequests);
        Assert.Equal(1, session.DisconnectCalls);
        session.RaiseDisconnected(DisconnectReason.User);
        Assert.Equal(left, _factory.Last.ConnectedWith!.Layout);
    }
}
