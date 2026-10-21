using RdpMonitorSwitcher.Core.Configuration;
using RdpMonitorSwitcher.Core.Models;
using RdpMonitorSwitcher.Core.Services;

namespace RdpMonitorSwitcher.Tests;

public class SettingsStoreTests
{
    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        using var dir = new TempDirectory();

        var settings = new SettingsStore(dir.File("settings.json")).Load();

        Assert.Empty(settings.Profiles);
        Assert.True(settings.ToolbarVisible);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsProfiles()
    {
        using var dir = new TempDirectory();
        var store = new SettingsStore(dir.File("sub/settings.json"));
        var settings = new AppSettings();
        var profile = TestProfiles.Office(3);
        profile.SizeMode = MonitorSizeMode.Fixed;
        profile.FixedMonitors = [new(2560, 1440), new(1920, 1080)];
        settings.Remember(profile);
        settings.MonitorWindows[1] = new WindowPlacement { Left = 10, Top = 20, Width = 800, Height = 600 };

        store.Save(settings);
        var loaded = store.Load();

        var p = Assert.Single(loaded.Profiles);
        Assert.Equal("office-pc", p.Host);
        Assert.Equal(MonitorSizeMode.Fixed, p.SizeMode);
        Assert.Equal([new PixelSize(2560, 1440), new PixelSize(1920, 1080)], p.FixedMonitors);
        Assert.Equal(p.Key, loaded.LastProfileKey);
        Assert.Equal(800, loaded.MonitorWindows[1].Width);
    }

    [Fact]
    public void SettingsFile_HasNoPasswordProperty()
    {
        using var dir = new TempDirectory();
        var store = new SettingsStore(dir.File("settings.json"));
        var settings = new AppSettings();
        settings.Remember(TestProfiles.Office());

        store.Save(settings);

        var json = File.ReadAllText(store.FilePath);
        Assert.DoesNotContain("\"Password\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"SavePassword\"", json); // only the user's choice is stored
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaultsAndKeepsBackup()
    {
        using var dir = new TempDirectory();
        var path = dir.File("settings.json");
        File.WriteAllText(path, "{ not json");

        var settings = new SettingsStore(path).Load();

        Assert.Empty(settings.Profiles);
        Assert.True(File.Exists(path + ".corrupt"));
    }

    [Fact]
    public void Remember_ReplacesSameHostAndUser_AndMovesToFront()
    {
        var settings = new AppSettings();
        settings.Remember(new ConnectionProfile { Host = "a", Username = "u" });
        settings.Remember(new ConnectionProfile { Host = "b", Username = "u" });
        settings.Remember(new ConnectionProfile { Host = "A", Username = "U", MonitorCount = 3 });

        Assert.Equal(2, settings.Profiles.Count);
        Assert.Equal(3, settings.Profiles[0].MonitorCount);
        Assert.Equal("b", settings.Profiles[1].Host);
    }
}

public class ConnectionProfileTests
{
    [Fact]
    public void Validate_AcceptsTypicalProfile()
    {
        Assert.Empty(TestProfiles.Office().Validate());
    }

    [Theory]
    [InlineData("", "user", 3389)]
    [InlineData("my host", "user", 3389)]
    [InlineData("host", "", 3389)]
    [InlineData("host", "user", 0)]
    [InlineData("host", "user", 70000)]
    public void Validate_RejectsInvalidInput(string host, string user, int port)
    {
        var profile = new ConnectionProfile { Host = host, Username = user, Port = port };

        Assert.NotEmpty(profile.Validate());
    }

    [Fact]
    public void Validate_RejectsInvalidFixedResolution()
    {
        var profile = TestProfiles.Office();
        profile.SizeMode = MonitorSizeMode.Fixed;
        profile.FixedMonitors = [new(100, 1080)];

        Assert.NotEmpty(profile.Validate());
    }

    [Theory]
    [InlineData("alex", "", "alex", "")]
    [InlineData("OFFICE\\alex", "", "alex", "OFFICE")]
    [InlineData("alex@example.com", "", "alex@example.com", "")]
    [InlineData("alex", "WORKGROUP", "alex", "WORKGROUP")]
    public void SplitUser(string input, string domain, string expectedUser, string expectedDomain)
    {
        var profile = new ConnectionProfile { Username = input, Domain = domain };

        Assert.Equal((expectedUser, expectedDomain), profile.SplitUser());
    }

    [Fact]
    public void Key_IsCaseInsensitive()
    {
        Assert.Equal(
            new ConnectionProfile { Host = "Office-PC", Username = "Alex" }.Key,
            new ConnectionProfile { Host = "office-pc", Username = "alex" }.Key);
    }

    [Fact]
    public void Clone_CopiesMonitorList()
    {
        var profile = TestProfiles.Office();
        var clone = profile.Clone();
        clone.FixedMonitors.Add(new PixelSize(800, 600));

        Assert.NotEqual(profile.FixedMonitors.Count, clone.FixedMonitors.Count);
    }
}

public class ResolutionListTests
{
    [Fact]
    public void Parse_AcceptsCommonFormats()
    {
        Assert.True(ResolutionList.TryParse("1920x1080, 2560X1440;1280×1024", out var sizes, out _));
        Assert.Equal([new PixelSize(1920, 1080), new PixelSize(2560, 1440), new PixelSize(1280, 1024)], sizes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1920")]
    [InlineData("1920x")]
    [InlineData("abcxdef")]
    [InlineData("100x100")]
    [InlineData("-1920x1080")]
    public void Parse_RejectsInvalid(string text)
    {
        Assert.False(ResolutionList.TryParse(text, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Format_RoundTrips()
    {
        var text = ResolutionList.Format([new(1920, 1080), new(2560, 1440)]);

        Assert.Equal("1920x1080, 2560x1440", text);
    }
}

public class ReconnectPolicyTests
{
    [Fact]
    public void NetworkLoss_IsRetriedWithBackoff()
    {
        var policy = new ReconnectPolicy();

        Assert.Equal(TimeSpan.FromSeconds(1), policy.GetDelay(DisconnectReason.Network, 1));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.GetDelay(DisconnectReason.Network, 2));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.GetDelay(DisconnectReason.Network, 3));
        Assert.Equal(TimeSpan.FromSeconds(10), policy.GetDelay(DisconnectReason.Network, 9));
    }

    [Fact]
    public void GivesUpAfterMaxAttempts()
    {
        var policy = new ReconnectPolicy(maxAttempts: 3);

        Assert.NotNull(policy.GetDelay(DisconnectReason.Network, 3));
        Assert.Null(policy.GetDelay(DisconnectReason.Network, 4));
    }

    [Theory]
    [InlineData(DisconnectReason.User)]
    [InlineData(DisconnectReason.Authentication)]
    [InlineData(DisconnectReason.Certificate)]
    [InlineData(DisconnectReason.ServerEnded)]
    [InlineData(DisconnectReason.Replaced)]
    [InlineData(DisconnectReason.Other)]
    public void FinalReasons_AreNotRetried(DisconnectReason reason)
    {
        Assert.Null(new ReconnectPolicy().GetDelay(reason, 1));
    }
}
