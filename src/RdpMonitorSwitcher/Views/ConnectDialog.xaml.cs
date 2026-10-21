using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using RdpMonitorSwitcher.Core.Configuration;
using RdpMonitorSwitcher.Core.Models;
using RdpMonitorSwitcher.Core.Services;
using RdpMonitorSwitcher.Core.ViewModels;

namespace RdpMonitorSwitcher.Views;

/// <summary>Connection settings. Edits a copy of the profile; the password never leaves this dialog except in the result.</summary>
public partial class ConnectDialog : Window
{
    private static readonly WakeMethod[] WakeMethods = [WakeMethod.None, WakeMethod.FritzBox, WakeMethod.MagicPacket];

    private static readonly (RemoteScaling Value, string Label)[] ScalingOptions =
    [
        (RemoteScaling.MatchLocal, "Wie lokal (automatisch)"),
        (RemoteScaling.Percent100, "100 %"),
        (RemoteScaling.Percent125, "125 %"),
        (RemoteScaling.Percent150, "150 %"),
        (RemoteScaling.Percent175, "175 %"),
        (RemoteScaling.Percent200, "200 %"),
    ];

    private readonly IReadOnlyList<ConnectionProfile> _recent;
    private readonly ICredentialStore _credentials;
    private bool _loading;

    internal ConnectDialog(ConnectionProfile profile, string? storedPassword, IReadOnlyList<ConnectionProfile> recent,
        ICredentialStore credentials)
    {
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height - 20; // never taller than the screen; content scrolls
        _recent = recent;
        _credentials = credentials;
        for (var i = 1; i <= 8; i++)
            CountBox.Items.Add(i);
        foreach (var (_, label) in ScalingOptions)
            RemoteScalingBox.Items.Add(label);
        foreach (var p in recent)
            HostBox.Items.Add(p.DisplayName);

        Load(profile, storedPassword);
        Loaded += (_, _) =>
        {
            if (string.IsNullOrEmpty(HostBox.Text))
                HostBox.Focus();
            else if (PasswordBox.Password.Length == 0)
                PasswordBox.Focus();
        };
    }

    public ConnectRequest? Result { get; private set; }

    private void Load(ConnectionProfile p, string? password)
    {
        _loading = true;
        HostBox.Text = p.Host;
        PortBox.Text = p.Port.ToString(CultureInfo.InvariantCulture);
        UserBox.Text = string.IsNullOrEmpty(p.Domain) ? p.Username : $"{p.Domain}\\{p.Username}";
        PasswordBox.Password = password ?? "";
        SavePasswordBox.IsChecked = p.SavePassword;
        FitModeBox.IsChecked = p.SizeMode == MonitorSizeMode.FitToWindow;
        FixedModeBox.IsChecked = p.SizeMode == MonitorSizeMode.Fixed;
        CountBox.SelectedItem = Math.Clamp(p.MonitorCount, 1, 8);
        ResolutionsBox.Text = ResolutionList.Format(p.FixedMonitors);
        ScalingBox.SelectedIndex = p.Scaling == ScalingMode.Original ? 1 : 0;
        RemoteScalingBox.SelectedIndex = Math.Max(0, Array.FindIndex(ScalingOptions, o => o.Value == p.RemoteScaling));
        ClipboardBox.IsChecked = p.RedirectClipboard;
        AudioBox.IsChecked = p.PlayAudio;
        H264Box.IsChecked = p.UseH264;
        ReconnectBox.IsChecked = p.AutoReconnect;
        WakeMethodBox.SelectedIndex = Math.Max(0, Array.IndexOf(WakeMethods, p.WakeMethod));
        FritzAddressBox.Text = string.IsNullOrWhiteSpace(p.FritzBoxAddress) ? "fritz.box" : p.FritzBoxAddress;
        FritzUserBox.Text = p.FritzBoxUser;
        FritzPasswordBox.Password = "";
        if (p.WakeMethod == WakeMethod.FritzBox && !string.IsNullOrEmpty(p.FritzBoxUser))
        {
            try
            {
                FritzPasswordBox.Password = _credentials.TryGetPassword(p.FritzBoxCredentialKey) ?? "";
            }
            catch (Exception)
            {
                // Credential store unavailable: the user can type it again.
            }
        }
        MacBox.Text = p.MacAddress;
        DevicesBox.ItemsSource = null;
        WakeStatusText.Text = "";
        WakeExpander.IsExpanded = p.WakeMethod != WakeMethod.None;
        UpdateWakeControls();
        UpdateModeControls();
        _loading = false;
    }

    private void OnRecentSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || HostBox.SelectedIndex < 0 || HostBox.SelectedIndex >= _recent.Count)
            return;
        var profile = _recent[HostBox.SelectedIndex];
        string? password = null;
        try
        {
            password = _credentials.TryGetPassword(profile.Key);
        }
        catch (Exception)
        {
            // Credential store unavailable: ask for the password.
        }
        Dispatcher.BeginInvoke(() => Load(profile.Clone(), password));
    }

    private void OnModeChanged(object sender, RoutedEventArgs e) => UpdateModeControls();

    private void OnWakeMethodChanged(object sender, SelectionChangedEventArgs e) => UpdateWakeControls();

    private void UpdateWakeControls()
    {
        if (MacBox is null)
            return;
        var method = WakeMethods[Math.Max(0, WakeMethodBox.SelectedIndex)];
        var fritz = method == WakeMethod.FritzBox ? Visibility.Visible : Visibility.Collapsed;
        foreach (var element in new UIElement[] { FritzAddressLabel, FritzAddressBox, FritzAddressHint, FritzUserLabel, FritzUserBox,
                     FritzPasswordLabel, FritzPasswordRow, DevicesLabel, DevicesBox })
            element.Visibility = fritz;
        MacBox.IsEnabled = method != WakeMethod.None;
        WakeStatusText.Text = method switch
        {
            WakeMethod.FritzBox => "Die FRITZ!Box weckt den PC – auch wenn du nicht zu Hause bist.",
            WakeMethod.MagicPacket => "Funktioniert nur, wenn dieser PC im selben Netzwerk ist wie der Remote-PC.",
            _ => "",
        };
        WakeStatusText.Foreground = System.Windows.Media.Brushes.Gray;
    }

    private async void OnLoadDevices(object sender, RoutedEventArgs e)
    {
        LoadDevicesButton.IsEnabled = false;
        WakeStatusText.Foreground = System.Windows.Media.Brushes.Gray;
        WakeStatusText.Text = "Verbinde mit der FRITZ!Box …";
        try
        {
            var hosts = await new FritzBoxClient().ListHostsAsync(FritzAddressBox.Text, FritzUserBox.Text.Trim(),
                FritzPasswordBox.Password, CancellationToken.None);
            DevicesBox.ItemsSource = hosts;
            var current = MacAddress.TryParse(MacBox.Text, out _) ? MacAddress.Normalize(MacBox.Text) : null;
            var host = HostBox.Text.Trim().Split('.')[0];
            DevicesBox.SelectedItem =
                hosts.FirstOrDefault(h => h.Mac == current) ??
                hosts.FirstOrDefault(h => h.Name.Equals(host, StringComparison.OrdinalIgnoreCase) && h.IsWired) ??
                hosts.FirstOrDefault(h => h.IpAddress == HostBox.Text.Trim());
            WakeStatusText.Foreground = System.Windows.Media.Brushes.ForestGreen;
            WakeStatusText.Text = $"Verbindung zur FRITZ!Box OK – {hosts.Count} Geräte gefunden. Wähle den Remote-PC (am besten per Kabel verbunden).";
        }
        catch (WakeOnLanException ex)
        {
            WakeStatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            WakeStatusText.Text = ex.Message;
        }
        finally
        {
            LoadDevicesButton.IsEnabled = true;
        }
    }

    private void OnDeviceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (DevicesBox.SelectedItem is FritzBoxHost host)
            MacBox.Text = host.Mac;
    }

    private void UpdateModeControls()
    {
        CountBox.IsEnabled = FitModeBox.IsChecked == true;
        ResolutionsBox.IsEnabled = FixedModeBox.IsChecked == true;
    }

    private void OnConnect(object sender, RoutedEventArgs e)
    {
        var errors = new List<string>();
        var profile = new ConnectionProfile
        {
            Host = HostBox.Text.Trim(),
            Username = UserBox.Text.Trim(),
            SavePassword = SavePasswordBox.IsChecked == true,
            SizeMode = FixedModeBox.IsChecked == true ? MonitorSizeMode.Fixed : MonitorSizeMode.FitToWindow,
            MonitorCount = CountBox.SelectedItem is int count ? count : 2,
            Scaling = ScalingBox.SelectedIndex == 1 ? ScalingMode.Original : ScalingMode.Fit,
            RemoteScaling = ScalingOptions[Math.Max(0, RemoteScalingBox.SelectedIndex)].Value,
            RedirectClipboard = ClipboardBox.IsChecked == true,
            PlayAudio = AudioBox.IsChecked == true,
            UseH264 = H264Box.IsChecked == true,
            AutoReconnect = ReconnectBox.IsChecked == true,
            WakeMethod = WakeMethods[Math.Max(0, WakeMethodBox.SelectedIndex)],
            MacAddress = MacBox.Text.Trim(),
            FritzBoxAddress = FritzAddressBox.Text.Trim(),
            FritzBoxUser = FritzUserBox.Text.Trim(),
        };
        if (profile.WakeMethod != WakeMethod.None && MacAddress.TryParse(profile.MacAddress, out _))
            profile.MacAddress = MacAddress.Normalize(profile.MacAddress);

        // "DOMAIN\user" is kept split so the profile key does not depend on the notation.
        var (user, domain) = profile.SplitUser();
        profile.Username = user;
        profile.Domain = domain;

        if (int.TryParse(PortBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            profile.Port = port;
        else
            errors.Add("Der Port muss eine Zahl sein.");

        if (ResolutionList.TryParse(ResolutionsBox.Text, out var sizes, out var resolutionError))
            profile.FixedMonitors = sizes;
        else if (profile.SizeMode == MonitorSizeMode.Fixed)
            errors.Add(resolutionError!);

        errors.AddRange(profile.Validate());
        if (PasswordBox.Password.Length == 0)
            errors.Add("Bitte das Passwort eingeben.");
        if (profile.WakeMethod == WakeMethod.FritzBox && FritzPasswordBox.Password.Length == 0)
            errors.Add("Wake-on-LAN: Bitte das FRITZ!Box-Passwort eingeben.");

        if (errors.Count > 0)
        {
            ErrorText.Text = string.Join(Environment.NewLine, errors.Distinct());
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        Result = new ConnectRequest(profile, PasswordBox.Password,
            profile.WakeMethod == WakeMethod.FritzBox ? FritzPasswordBox.Password : null);
        DialogResult = true;
    }
}
