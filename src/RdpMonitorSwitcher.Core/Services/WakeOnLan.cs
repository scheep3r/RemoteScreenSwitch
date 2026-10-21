using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using RdpMonitorSwitcher.Core.Models;

namespace RdpMonitorSwitcher.Core.Services;

public enum WakeMethod
{
    None,

    /// <summary>Magic packet sent by this PC (only works inside the same network).</summary>
    MagicPacket,

    /// <summary>The FRITZ!Box in the remote network sends the magic packet (TR-064), also from outside.</summary>
    FritzBox,
}

public sealed class WakeOnLanException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>MAC address parsing ("00-11-22-AA-BB-CC", "00:11:22:aa:bb:cc", "001122AABBCC").</summary>
public static class MacAddress
{
    public static bool TryParse(string? text, out byte[] bytes)
    {
        bytes = [];
        var hex = new string((text ?? "").Where(c => !char.IsWhiteSpace(c) && c is not (':' or '-' or '.')).ToArray());
        if (hex.Length != 12 || !hex.All(char.IsAsciiHexDigit))
            return false;
        bytes = Convert.FromHexString(hex);
        return true;
    }

    /// <summary>Canonical form "00:11:22:AA:BB:CC" (as the FRITZ!Box expects it).</summary>
    public static string Normalize(string text) =>
        TryParse(text, out var bytes)
            ? string.Join(':', bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)))
            : throw new WakeOnLanException($"„{text}“ ist keine gültige MAC-Adresse (Format: 00-11-22-AA-BB-CC).");
}

public static class MagicPacket
{
    /// <summary>6 × 0xFF followed by the MAC address 16 times (102 bytes).</summary>
    public static byte[] Build(string mac)
    {
        if (!MacAddress.TryParse(mac, out var bytes))
            throw new WakeOnLanException($"„{mac}“ ist keine gültige MAC-Adresse.");
        var packet = new byte[6 + 16 * 6];
        Array.Fill(packet, (byte)0xFF, 0, 6);
        for (var i = 0; i < 16; i++)
            bytes.CopyTo(packet, 6 + i * 6);
        return packet;
    }
}

/// <summary>Sends a magic packet into the local network(s).</summary>
public interface IMagicPacketSender
{
    Task SendAsync(byte[] packet, CancellationToken cancellationToken);
}

/// <summary>
/// Minimal TR-064 client for the FRITZ!Box "Hosts" service (verified against FRITZ!OS 7.57:
/// urn:dslforum-org:service:Hosts:1, /upnp/control/hosts, X_AVM-DE_WakeOnLANByMACAddress(NewMACAddress)).
/// Local access: http://fritz.box:49000. Remote access (MyFRITZ / internet access over HTTPS):
/// https://&lt;address&gt;:&lt;port&gt;/tr064/... Authentication is HTTP Digest with a FRITZ!Box user.
/// </summary>
public sealed class FritzBoxClient
{
    public const string HostsService = "urn:dslforum-org:service:Hosts:1";
    public const string HostsControlPath = "upnp/control/hosts";
    public const int LocalPort = 49000;

    private readonly Func<NetworkCredential, HttpMessageHandler> _handlerFactory;

    /// <param name="handlerFactory">Creates the HTTP handler for the given credentials (tests inject a fake).</param>
    public FritzBoxClient(Func<NetworkCredential, HttpMessageHandler>? handlerFactory = null)
    {
        _handlerFactory = handlerFactory ?? (credential => new HttpClientHandler
        {
            Credentials = credential,
            PreAuthenticate = false,
        });
    }

    /// <summary>
    /// "fritz.box" or a local IP → http://…:49000/ ; anything else (e.g. "abc.myfritz.net:44312")
    /// → https://…/tr064/ for the remote access port. A full URL is used as given.
    /// </summary>
    public static Uri ResolveEndpoint(string? address)
    {
        var text = string.IsNullOrWhiteSpace(address) ? "fritz.box" : address.Trim();
        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(text.EndsWith('/') ? text : text + "/", UriKind.Absolute, out var full) ||
                full.Scheme is not ("http" or "https"))
                throw new WakeOnLanException($"„{text}“ ist keine gültige FRITZ!Box-Adresse.");
            return full;
        }

        if (!Uri.TryCreate("http://" + text, UriKind.Absolute, out var parsed) || parsed.AbsolutePath != "/")
            throw new WakeOnLanException($"„{text}“ ist keine gültige FRITZ!Box-Adresse.");
        var host = parsed.Host;
        var explicitPort = text.Contains(':') && !IPAddress.TryParse(text, out _) ? parsed.Port : (int?)null;
        if (IsLocal(host))
            return new Uri($"http://{parsed.Authority.Split(':')[0]}:{explicitPort ?? LocalPort}/");
        return new UriBuilder("https", host, explicitPort ?? 443, "tr064/").Uri;
    }

    private static bool IsLocal(string host)
    {
        if (host.Equals("fritz.box", StringComparison.OrdinalIgnoreCase) || !host.Contains('.'))
            return true;
        if (!IPAddress.TryParse(host, out var ip))
            return false;
        var b = ip.GetAddressBytes();
        return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
               (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168));
    }

    /// <summary>Asks the FRITZ!Box to wake the device with this MAC address.</summary>
    public Task WakeAsync(string address, string user, string password, string mac, CancellationToken cancellationToken) =>
        CallHostsAsync(address, user, password, "X_AVM-DE_WakeOnLANByMACAddress",
            [("NewMACAddress", MacAddress.Normalize(mac))], cancellationToken);

    /// <summary>Checks address and login; returns the number of known network devices.</summary>
    public async Task<int> TestAsync(string address, string user, string password, CancellationToken cancellationToken)
    {
        var response = await CallHostsAsync(address, user, password, "GetHostNumberOfEntries", [], cancellationToken);
        var value = response.Descendants().FirstOrDefault(e => e.Name.LocalName == "NewHostNumberOfEntries")?.Value;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : 0;
    }

    /// <summary>
    /// All network devices the FRITZ!Box knows (name, MAC, IP), so the user can pick the PC
    /// instead of typing its MAC address. Wired devices first.
    /// </summary>
    public async Task<IReadOnlyList<FritzBoxHost>> ListHostsAsync(string address, string user, string password,
        CancellationToken cancellationToken)
    {
        var count = await TestAsync(address, user, password, cancellationToken);
        var hosts = new List<FritzBoxHost>();
        for (var i = 0; i < Math.Min(count, 500); i++)
        {
            var doc = await CallHostsAsync(address, user, password, "GetGenericHostEntry",
                [("NewIndex", i.ToString(CultureInfo.InvariantCulture))], cancellationToken);
            string Get(string name) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? "";
            var mac = Get("NewMACAddress");
            if (!MacAddress.TryParse(mac, out _))
                continue;
            hosts.Add(new FritzBoxHost(Get("NewHostName"), MacAddress.Normalize(mac), Get("NewIPAddress"),
                Get("NewInterfaceType"), Get("NewActive") == "1"));
        }
        return hosts
            .OrderByDescending(h => h.InterfaceType.Equals("Ethernet", StringComparison.OrdinalIgnoreCase))
            .ThenBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private async Task<XDocument> CallHostsAsync(string address, string user, string password, string action,
        IReadOnlyList<(string Name, string Value)> arguments, CancellationToken cancellationToken)
    {
        var endpoint = new Uri(ResolveEndpoint(address), HostsControlPath);
        var body = BuildEnvelope(action, arguments);
        using var handler = _handlerFactory(new NetworkCredential(user ?? "", password ?? ""));
        using var http = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(15) };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/xml"),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/xml") { CharSet = "utf-8" };
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{HostsService}#{action}\"");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new WakeOnLanException($"Die FRITZ!Box unter {endpoint.Authority} ist nicht erreichbar ({ex.Message}).", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WakeOnLanException($"Die FRITZ!Box unter {endpoint.Authority} antwortet nicht.", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new WakeOnLanException("Anmeldung an der FRITZ!Box fehlgeschlagen. Benutzername und Passwort prüfen.");
            if (!response.IsSuccessStatusCode)
                throw new WakeOnLanException(DescribeFault(text, response.StatusCode));
            try
            {
                return XDocument.Parse(text);
            }
            catch (System.Xml.XmlException ex)
            {
                throw new WakeOnLanException("Die FRITZ!Box hat eine unerwartete Antwort geschickt.", ex);
            }
        }
    }

    public static string BuildEnvelope(string action, IReadOnlyList<(string Name, string Value)> arguments)
    {
        XNamespace s = "http://schemas.xmlsoap.org/soap/envelope/";
        XNamespace u = HostsService;
        var envelope = new XElement(s + "Envelope",
            new XAttribute(XNamespace.Xmlns + "s", s),
            new XAttribute(s + "encodingStyle", "http://schemas.xmlsoap.org/soap/encoding/"),
            new XElement(s + "Body",
                new XElement(u + action,
                    new XAttribute(XNamespace.Xmlns + "u", u),
                    arguments.Select(a => new XElement(a.Name, a.Value)))));
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + envelope.ToString(SaveOptions.DisableFormatting);
    }

    private static string DescribeFault(string body, HttpStatusCode status)
    {
        string? code = null, description = null;
        try
        {
            var doc = XDocument.Parse(body);
            code = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "errorCode")?.Value;
            description = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "errorDescription")?.Value;
        }
        catch (System.Xml.XmlException)
        {
        }
        return code switch
        {
            "606" => "Der FRITZ!Box-Benutzer hat keine Berechtigung dafür (Recht „FRITZ!Box Einstellungen“ nötig, für den Fernzugriff zusätzlich „Zugang auch aus dem Internet“).",
            "714" => "Die FRITZ!Box kennt diese MAC-Adresse nicht. Der PC muss mindestens einmal im Heimnetz gewesen sein; MAC-Adresse prüfen.",
            "401" or "402" => "Die FRITZ!Box hat die Anfrage abgelehnt (ungültige Argumente – MAC-Adresse prüfen).",
            _ => $"Die FRITZ!Box meldet einen Fehler ({(int)status}{(code is null ? "" : $", Code {code}")}{(description is null ? "" : $": {description}")}).",
        };
    }
}

/// <summary>A device from the FRITZ!Box host list.</summary>
public sealed record FritzBoxHost(string Name, string Mac, string IpAddress, string InterfaceType, bool Active)
{
    public bool IsWired => InterfaceType.Equals("Ethernet", StringComparison.OrdinalIgnoreCase);

    public string Display =>
        $"{(string.IsNullOrEmpty(Name) ? "(ohne Namen)" : Name)} – {Mac}" +
        (string.IsNullOrEmpty(IpAddress) ? "" : $" – {IpAddress}") +
        (IsWired ? " – Kabel" : InterfaceType.Length > 0 ? $" – {InterfaceType} (WLAN weckt meist nicht)" : "");
}

public enum WakePhase
{
    Sending,
    WaitingForPc,
    Ready,
}

public readonly record struct WakeProgress(WakePhase Phase, TimeSpan Elapsed, string Message);

/// <summary>Sends the wake signal and waits until the remote PC accepts RDP connections.</summary>
public sealed class PcWaker
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(4);
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(3);

    private readonly IMagicPacketSender _magicPacket;
    private readonly FritzBoxClient _fritzBox;
    private readonly Func<string, int, CancellationToken, Task<bool>> _isReachable;
    private readonly TimeProvider _time;

    public PcWaker(IMagicPacketSender magicPacket, FritzBoxClient fritzBox,
        Func<string, int, CancellationToken, Task<bool>> isReachable, TimeProvider? time = null)
    {
        _magicPacket = magicPacket;
        _fritzBox = fritzBox;
        _isReachable = isReachable;
        _time = time ?? TimeProvider.System;
    }

    /// <returns>True when the PC is reachable, false after the timeout.</returns>
    public async Task<bool> WakeAsync(ConnectionProfile profile, string? fritzBoxPassword, IProgress<WakeProgress>? progress,
        CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var start = _time.GetTimestamp();
        TimeSpan Elapsed() => _time.GetElapsedTime(start);

        if (await _isReachable(profile.Host, profile.Port, cancellationToken))
        {
            progress?.Report(new WakeProgress(WakePhase.Ready, Elapsed(), "Der PC ist bereits eingeschaltet."));
            return true;
        }

        progress?.Report(new WakeProgress(WakePhase.Sending, Elapsed(), "Sende Weck-Signal …"));
        switch (profile.WakeMethod)
        {
            case WakeMethod.MagicPacket:
                await _magicPacket.SendAsync(MagicPacket.Build(profile.MacAddress), cancellationToken);
                break;
            case WakeMethod.FritzBox:
                if (string.IsNullOrEmpty(fritzBoxPassword))
                    throw new WakeOnLanException("Für die FRITZ!Box ist kein Passwort gespeichert. Bitte in den Verbindungseinstellungen eintragen.");
                await _fritzBox.WakeAsync(profile.FritzBoxAddress, profile.FritzBoxUser, fritzBoxPassword, profile.MacAddress, cancellationToken);
                break;
            default:
                throw new WakeOnLanException("Für diese Verbindung ist kein Wake-on-LAN eingerichtet.");
        }

        var limit = timeout ?? DefaultTimeout;
        while (Elapsed() < limit)
        {
            progress?.Report(new WakeProgress(WakePhase.WaitingForPc, Elapsed(),
                $"Warte, bis der PC hochgefahren ist … ({Elapsed().TotalSeconds:0} s)"));
            await Task.Delay(ProbeInterval, _time, cancellationToken);
            if (await _isReachable(profile.Host, profile.Port, cancellationToken))
            {
                progress?.Report(new WakeProgress(WakePhase.Ready, Elapsed(), "Der PC ist erreichbar."));
                return true;
            }
        }
        return false;
    }
}
