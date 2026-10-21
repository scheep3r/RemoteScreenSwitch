using RdpMonitorSwitcher.Core.Services;

namespace RdpMonitorSwitcher.Core.Models;

/// <summary>How the size of the remote virtual monitors is chosen.</summary>
public enum MonitorSizeMode
{
    /// <summary>Every remote monitor gets the size of the local view that shows it (no black bars).</summary>
    FitToWindow,

    /// <summary>Fixed resolutions, e.g. matching the physical monitors of the remote PC.</summary>
    Fixed,
}

/// <summary>How a remote monitor is drawn inside its local view.</summary>
public enum ScalingMode
{
    /// <summary>Scale to fit the view, keeping the aspect ratio.</summary>
    Fit,

    /// <summary>1:1 pixels, scroll if the view is smaller.</summary>
    Original,
}

/// <summary>Remote DPI scaling (Windows "Scale" setting inside the session).</summary>
public enum RemoteScaling
{
    MatchLocal = 0,
    Percent100 = 100,
    Percent125 = 125,
    Percent150 = 150,
    Percent175 = 175,
    Percent200 = 200,
}

/// <summary>Non-sensitive connection settings. The password lives in the Windows Credential Manager.</summary>
public sealed class ConnectionProfile
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 3389;
    public string Username { get; set; } = "";
    public string Domain { get; set; } = "";
    public bool SavePassword { get; set; } = true;

    public MonitorSizeMode SizeMode { get; set; } = MonitorSizeMode.FitToWindow;

    /// <summary>Number of virtual monitors in <see cref="MonitorSizeMode.FitToWindow"/> mode.</summary>
    public int MonitorCount { get; set; } = 2;

    /// <summary>Monitor resolutions in <see cref="MonitorSizeMode.Fixed"/> mode, left to right; the first is primary.</summary>
    public List<PixelSize> FixedMonitors { get; set; } = [new(1920, 1080), new(1920, 1080)];

    public ScalingMode Scaling { get; set; } = ScalingMode.Fit;
    public RemoteScaling RemoteScaling { get; set; } = RemoteScaling.MatchLocal;

    public bool RedirectClipboard { get; set; } = true;
    public bool UseH264 { get; set; } = true;
    public bool PlayAudio { get; set; } = true;
    public bool AutoReconnect { get; set; } = true;

    // ---- Wake-on-LAN ----------------------------------------------------------------
    public WakeMethod WakeMethod { get; set; } = WakeMethod.None;

    /// <summary>MAC address of the remote PC's network adapter.</summary>
    public string MacAddress { get; set; } = "";

    /// <summary>"fritz.box" (local) or the remote access address, e.g. "abc123.myfritz.net:44312".</summary>
    public string FritzBoxAddress { get; set; } = "fritz.box";

    public string FritzBoxUser { get; set; } = "";

    public bool CanWake => WakeMethod != WakeMethod.None && Services.MacAddress.TryParse(MacAddress, out _);

    /// <summary>Credential store key of the FRITZ!Box password.</summary>
    public string FritzBoxCredentialKey =>
        $"fritzbox|{FritzBoxAddress.Trim().ToLowerInvariant()}|{FritzBoxUser.Trim().ToLowerInvariant()}";

    public string DisplayName => Port == 3389 ? Host : $"{Host}:{Port}";

    /// <summary>Key used for the credential store and for matching saved profiles.</summary>
    public string Key => $"{Host.Trim().ToLowerInvariant()}:{Port}|{Username.Trim().ToLowerInvariant()}";

    public int EffectiveMonitorCount =>
        SizeMode == MonitorSizeMode.Fixed ? FixedMonitors.Count : MonitorCount;

    public ConnectionProfile Clone()
    {
        var clone = (ConnectionProfile)MemberwiseClone();
        clone.FixedMonitors = [.. FixedMonitors];
        return clone;
    }

    /// <summary>Returns user-facing validation errors; empty if the profile can be used.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Host))
            errors.Add("Bitte einen Hostnamen oder eine IP-Adresse angeben.");
        else if (Host.Any(char.IsWhiteSpace))
            errors.Add("Der Hostname darf keine Leerzeichen enthalten.");
        if (Port is < 1 or > 65535)
            errors.Add("Der Port muss zwischen 1 und 65535 liegen.");
        if (string.IsNullOrWhiteSpace(Username))
            errors.Add("Bitte einen Benutzernamen angeben.");

        if (SizeMode == MonitorSizeMode.FitToWindow)
        {
            if (MonitorCount is < 1 or > MonitorLayout.MaxMonitors)
                errors.Add($"Die Monitoranzahl muss zwischen 1 und {MonitorLayout.MaxMonitors} liegen.");
        }
        else
        {
            if (FixedMonitors.Count is < 1 or > MonitorLayout.MaxMonitors)
                errors.Add($"Es müssen zwischen 1 und {MonitorLayout.MaxMonitors} Monitore angegeben werden.");
            foreach (var size in FixedMonitors)
            {
                if (size.Width is < MonitorLayout.MinMonitorWidth or > MonitorLayout.MaxMonitorWidth ||
                    size.Height is < MonitorLayout.MinMonitorHeight or > MonitorLayout.MaxMonitorHeight)
                {
                    errors.Add($"Ungültige Auflösung {size}: erlaubt sind 200–8192 Pixel je Richtung.");
                }
            }
        }
        if (WakeMethod != WakeMethod.None)
        {
            if (!Services.MacAddress.TryParse(MacAddress, out _))
                errors.Add("Wake-on-LAN: Bitte eine gültige MAC-Adresse angeben (z. B. 00-11-22-AA-BB-CC).");
            if (WakeMethod == WakeMethod.FritzBox)
            {
                if (string.IsNullOrWhiteSpace(FritzBoxUser))
                    errors.Add("Wake-on-LAN: Bitte den FRITZ!Box-Benutzernamen angeben.");
                try
                {
                    FritzBoxClient.ResolveEndpoint(FritzBoxAddress);
                }
                catch (WakeOnLanException ex)
                {
                    errors.Add("Wake-on-LAN: " + ex.Message);
                }
            }
        }
        return errors;
    }

    /// <summary>Splits "user", "DOMAIN\user" or "user@domain" into user name and domain.</summary>
    public (string User, string Domain) SplitUser()
    {
        var user = Username.Trim();
        var slash = user.IndexOf('\\');
        if (slash > 0)
            return (user[(slash + 1)..], user[..slash]);
        return (user, Domain.Trim());
    }
}
