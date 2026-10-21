using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using RdpMonitorSwitcher.Core.Models;
using RdpMonitorSwitcher.Core.Services;

namespace RdpMonitorSwitcher.Tests;

public class MacAddressTests
{
    [Theory]
    [InlineData("00-11-22-AA-BB-CC")]
    [InlineData("00:11:22:aa:bb:cc")]
    [InlineData("001122AABBCC")]
    [InlineData(" 00 11 22 AA BB CC ")]
    public void Parse_AcceptsCommonFormats(string text)
    {
        Assert.Equal("00:11:22:AA:BB:CC", MacAddress.Normalize(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("00-11-22-AA-BB")]
    [InlineData("00-11-22-AA-BB-ZZ")]
    [InlineData("00-11-22-AA-BB-CC-00")]
    public void Parse_RejectsInvalid(string text)
    {
        Assert.False(MacAddress.TryParse(text, out _));
        Assert.Throws<WakeOnLanException>(() => MacAddress.Normalize(text));
    }

    [Fact]
    public void MagicPacket_Is6xFF_Then16xMac()
    {
        var packet = MagicPacket.Build("00-11-22-AA-BB-CC");

        Assert.Equal(102, packet.Length);
        Assert.All(packet[..6], b => Assert.Equal(0xFF, b));
        for (var i = 0; i < 16; i++)
            Assert.Equal(new byte[] { 0x00, 0x11, 0x22, 0xAA, 0xBB, 0xCC }, packet[(6 + i * 6)..(12 + i * 6)]);
    }
}

public class FritzBoxClientTests
{
    [Theory]
    [InlineData("", "http://fritz.box:49000/")]
    [InlineData("fritz.box", "http://fritz.box:49000/")]
    [InlineData("192.168.178.1", "http://192.168.178.1:49000/")]
    [InlineData("10.0.0.1", "http://10.0.0.1:49000/")]
    [InlineData("abc123xyz.myfritz.net:44312", "https://abc123xyz.myfritz.net:44312/tr064/")]
    [InlineData("abc123xyz.myfritz.net", "https://abc123xyz.myfritz.net/tr064/")]
    [InlineData("https://box.example.org:8443/tr064/", "https://box.example.org:8443/tr064/")]
    public void ResolveEndpoint(string address, string expected)
    {
        Assert.Equal(new Uri(expected), FritzBoxClient.ResolveEndpoint(address));
    }

    [Theory]
    [InlineData("ftp://fritz.box")]
    [InlineData("fritz.box/path")]
    public void ResolveEndpoint_RejectsNonsense(string address)
    {
        Assert.Throws<WakeOnLanException>(() => FritzBoxClient.ResolveEndpoint(address));
    }

    [Fact]
    public async Task Wake_SendsTheDocumentedSoapAction_WithNormalizedMac()
    {
        var box = new FakeFritzBox();
        var client = new FritzBoxClient(box.CreateHandler);

        await client.WakeAsync("fritz.box", "alex", "pw", "00-11-22-aa-bb-cc", CancellationToken.None);

        var request = Assert.Single(box.Requests);
        Assert.Equal("http://fritz.box:49000/upnp/control/hosts", request.Uri.ToString());
        Assert.Equal("\"urn:dslforum-org:service:Hosts:1#X_AVM-DE_WakeOnLANByMACAddress\"", request.SoapAction);
        Assert.Contains("<NewMACAddress>00:11:22:AA:BB:CC</NewMACAddress>", request.Body);
        Assert.Contains("xmlns:u=\"urn:dslforum-org:service:Hosts:1\"", request.Body);
        Assert.Equal("alex", box.Credential!.UserName);
    }

    [Fact]
    public async Task Wake_Remote_UsesHttpsWithTr064Prefix()
    {
        var box = new FakeFritzBox();

        await new FritzBoxClient(box.CreateHandler).WakeAsync("abc.myfritz.net:44312", "u", "p", "001122AABBCC", CancellationToken.None);

        Assert.Equal("https://abc.myfritz.net:44312/tr064/upnp/control/hosts", box.Requests[0].Uri.ToString());
    }

    [Fact]
    public async Task WrongPassword_IsExplained()
    {
        var box = new FakeFritzBox { Status = HttpStatusCode.Unauthorized };

        var ex = await Assert.ThrowsAsync<WakeOnLanException>(() =>
            new FritzBoxClient(box.CreateHandler).WakeAsync("fritz.box", "u", "wrong", "001122AABBCC", CancellationToken.None));
        Assert.Contains("Anmeldung", ex.Message);
    }

    [Theory]
    [InlineData("714", "kennt diese MAC-Adresse nicht")]
    [InlineData("606", "Berechtigung")]
    public async Task SoapFaults_AreTranslated(string code, string expected)
    {
        var box = new FakeFritzBox { Status = HttpStatusCode.InternalServerError, Body = FakeFritzBox.Fault(code) };

        var ex = await Assert.ThrowsAsync<WakeOnLanException>(() =>
            new FritzBoxClient(box.CreateHandler).WakeAsync("fritz.box", "u", "p", "001122AABBCC", CancellationToken.None));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task Unreachable_IsExplained()
    {
        var box = new FakeFritzBox { Throw = true };

        var ex = await Assert.ThrowsAsync<WakeOnLanException>(() =>
            new FritzBoxClient(box.CreateHandler).TestAsync("fritz.box", "u", "p", CancellationToken.None));
        Assert.Contains("nicht erreichbar", ex.Message);
    }

    [Fact]
    public async Task ListHosts_ReturnsDevices_WiredFirst_SkipsEntriesWithoutMac()
    {
        var box = new FakeFritzBox
        {
            Respond = body => body.Contains("GetHostNumberOfEntries")
                ? FakeFritzBox.Response("GetHostNumberOfEntries", ("NewHostNumberOfEntries", "3"))
                : body.Contains("<NewIndex>0</NewIndex>")
                    ? FakeFritzBox.Response("GetGenericHostEntry", ("NewHostName", "Handy"), ("NewMACAddress", "AA:BB:CC:DD:EE:01"), ("NewIPAddress", "192.168.178.30"), ("NewInterfaceType", "802.11"), ("NewActive", "1"))
                    : body.Contains("<NewIndex>1</NewIndex>")
                        ? FakeFritzBox.Response("GetGenericHostEntry", ("NewHostName", "Office-PC"), ("NewMACAddress", "00:11:22:AA:BB:CC"), ("NewIPAddress", "192.168.178.20"), ("NewInterfaceType", "Ethernet"), ("NewActive", "1"))
                        : FakeFritzBox.Response("GetGenericHostEntry", ("NewHostName", "Gast"), ("NewMACAddress", ""), ("NewInterfaceType", "Ethernet")),
        };

        var hosts = await new FritzBoxClient(box.CreateHandler).ListHostsAsync("fritz.box", "u", "p", CancellationToken.None);

        Assert.Equal(["Office-PC", "Handy"], hosts.Select(h => h.Name));
        Assert.True(hosts[0].IsWired);
        Assert.Contains("Kabel", hosts[0].Display);
        Assert.Contains("WLAN", hosts[1].Display);
    }
}

public class PcWakerTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly FakeMagicPacketSender _udp = new();
    private readonly FakeFritzBox _box = new();
    private int _probes;
    private int _reachableAfterProbes = int.MaxValue;

    private PcWaker Waker() => new(_udp, new FritzBoxClient(_box.CreateHandler),
        (_, _, _) => Task.FromResult(++_probes > _reachableAfterProbes), _time);

    private static ConnectionProfile Profile(WakeMethod method) => new()
    {
        Host = "office-pc",
        Username = "alex",
        WakeMethod = method,
        MacAddress = "00-11-22-AA-BB-CC",
        FritzBoxAddress = "fritz.box",
        FritzBoxUser = "fb",
    };

    private async Task<bool> RunUntilDone(Task<bool> task)
    {
        while (!task.IsCompleted)
        {
            _time.Advance(PcWaker.ProbeInterval);
            await Task.Delay(1);
        }
        return await task;
    }

    [Fact]
    public async Task AlreadyRunningPc_IsNotWokenAgain()
    {
        _reachableAfterProbes = 0;

        Assert.True(await Waker().WakeAsync(Profile(WakeMethod.FritzBox), "pw", null, CancellationToken.None));
        Assert.Empty(_box.Requests);
    }

    [Fact]
    public async Task FritzBox_SendsWake_ThenWaitsUntilReachable()
    {
        _reachableAfterProbes = 4;
        var messages = new List<WakeProgress>();

        var ok = await RunUntilDone(Waker().WakeAsync(Profile(WakeMethod.FritzBox), "pw", new SyncProgress<WakeProgress>(messages), CancellationToken.None));

        Assert.True(ok);
        Assert.Single(_box.Requests);
        Assert.Equal(WakePhase.Ready, messages[^1].Phase);
        Assert.Contains(messages, m => m.Phase == WakePhase.WaitingForPc);
    }

    [Fact]
    public async Task MagicPacket_IsSentLocally()
    {
        _reachableAfterProbes = 2;

        Assert.True(await RunUntilDone(Waker().WakeAsync(Profile(WakeMethod.MagicPacket), null, null, CancellationToken.None)));
        Assert.Equal(MagicPacket.Build("00-11-22-AA-BB-CC"), Assert.Single(_udp.Sent));
        Assert.Empty(_box.Requests);
    }

    [Fact]
    public async Task GivesUpAfterTimeout()
    {
        var ok = await RunUntilDone(Waker().WakeAsync(Profile(WakeMethod.MagicPacket), null, null, CancellationToken.None, TimeSpan.FromSeconds(30)));

        Assert.False(ok);
    }

    [Fact]
    public async Task FritzBoxWithoutPassword_IsExplained()
    {
        var ex = await Assert.ThrowsAsync<WakeOnLanException>(() =>
            Waker().WakeAsync(Profile(WakeMethod.FritzBox), null, null, CancellationToken.None));
        Assert.Contains("Passwort", ex.Message);
    }

    [Fact]
    public void Profile_ValidatesWakeSettings()
    {
        var profile = Profile(WakeMethod.FritzBox);
        Assert.Empty(profile.Validate());
        Assert.True(profile.CanWake);

        profile.MacAddress = "nope";
        Assert.Contains(profile.Validate(), e => e.Contains("MAC"));
        Assert.False(profile.CanWake);

        profile.MacAddress = "00-11-22-AA-BB-CC";
        profile.FritzBoxUser = "";
        Assert.Contains(profile.Validate(), e => e.Contains("Benutzer"));
    }
}

internal sealed class FakeMagicPacketSender : IMagicPacketSender
{
    public List<byte[]> Sent { get; } = [];

    public Task SendAsync(byte[] packet, CancellationToken cancellationToken)
    {
        Sent.Add(packet);
        return Task.CompletedTask;
    }
}

internal sealed class SyncProgress<T>(List<T> list) : IProgress<T>
{
    public void Report(T value) => list.Add(value);
}

/// <summary>Answers like a FRITZ!Box TR-064 endpoint.</summary>
internal sealed class FakeFritzBox
{
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public string? Body { get; set; }
    public Func<string, string>? Respond { get; set; }
    public bool Throw { get; set; }
    public NetworkCredential? Credential { get; private set; }
    public List<(Uri Uri, string SoapAction, string Body)> Requests { get; } = [];

    public HttpMessageHandler CreateHandler(NetworkCredential credential)
    {
        Credential = credential;
        return new Handler(this);
    }

    public static string Response(string action, params (string Name, string Value)[] values) =>
        $"""<?xml version="1.0"?><s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><u:{action}Response xmlns:u="urn:dslforum-org:service:Hosts:1">{string.Concat(values.Select(v => $"<{v.Name}>{v.Value}</{v.Name}>"))}</u:{action}Response></s:Body></s:Envelope>""";

    public static string Fault(string code) =>
        $"""<?xml version="1.0"?><s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail><UPnPError xmlns="urn:schemas-upnp-org:control-1-0"><errorCode>{code}</errorCode><errorDescription>Error</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>""";

    private sealed class Handler(FakeFritzBox box) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (box.Throw)
                throw new HttpRequestException("No such host is known.");
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            box.Requests.Add((request.RequestUri!, string.Join("", request.Headers.GetValues("SOAPAction")), body));
            var text = box.Respond?.Invoke(body) ?? box.Body ?? Response("X_AVM-DE_WakeOnLANByMACAddress");
            return new HttpResponseMessage(box.Status) { Content = new StringContent(text, Encoding.UTF8, "text/xml") };
        }
    }
}
