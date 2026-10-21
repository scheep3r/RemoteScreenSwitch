using System.Net;
using System.Security.Cryptography;
using RdpMonitorSwitcher.Core.Services;

namespace RdpMonitorSwitcher.Tests;

public sealed class UpdateCheckerTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private static readonly byte[] Package = "PK fake zip content"u8.ToArray();

    public void Dispose() => _dir.Dispose();

    private static string ReleaseJson(string tag = "v1.2.0", bool withChecksum = true, string scheme = "https") => $$"""
        {
          "tag_name": "{{tag}}",
          "body": "Neue Funktionen",
          "assets": [
            { "name": "RdpMonitorSwitcher-1.2.0-win-x64.zip", "size": {{Package.Length}}, "browser_download_url": "{{scheme}}://github.com/o/r/releases/download/{{tag}}/RdpMonitorSwitcher-1.2.0-win-x64.zip" }
            {{(withChecksum ? $$""", { "name": "RdpMonitorSwitcher-1.2.0-win-x64.zip.sha256", "size": 64, "browser_download_url": "https://github.com/o/r/releases/download/{{tag}}/RdpMonitorSwitcher-1.2.0-win-x64.zip.sha256" }""" : "")}}
          ]
        }
        """;

    private static HttpClient Http(string checksum, byte[]? package = null, HttpStatusCode latest = HttpStatusCode.OK) =>
        new(new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/releases/latest") => (latest, ReleaseJson()),
            var p when p.EndsWith(".sha256") => (HttpStatusCode.OK, checksum + "  RdpMonitorSwitcher-1.2.0-win-x64.zip"),
            _ => (HttpStatusCode.OK, null),
        }, package ?? Package));

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    [Fact]
    public void Parse_FindsPackageAndChecksum()
    {
        var release = UpdateChecker.Parse(ReleaseJson());

        Assert.Equal(new Version(1, 2, 0, 0), release.Version);
        Assert.EndsWith("-win-x64.zip", release.PackageUrl.AbsolutePath);
        Assert.EndsWith(".sha256", release.ChecksumUrl.AbsolutePath);
        Assert.Equal("Neue Funktionen", release.Notes);
    }

    [Fact]
    public void Parse_RejectsReleasesWithoutChecksum()
    {
        Assert.Throws<UpdateException>(() => UpdateChecker.Parse(ReleaseJson(withChecksum: false)));
    }

    [Fact]
    public void Parse_RejectsPlainHttp()
    {
        Assert.Throws<UpdateException>(() => UpdateChecker.Parse(ReleaseJson(scheme: "http")));
    }

    [Theory]
    [InlineData("1.1.0", "v1.2.0", true)]
    [InlineData("1.2.0", "v1.2.0", false)]
    [InlineData("1.2.0.0", "1.2.0", false)]
    [InlineData("1.3.0", "v1.2.0", false)]
    [InlineData("1.2.0", "v1.10.0", true)]
    public void IsNewer(string current, string tag, bool expected)
    {
        Assert.Equal(expected, UpdateChecker.IsNewer(Version.Parse(current), UpdateChecker.Parse(ReleaseJson(tag))));
    }

    [Fact]
    public async Task NoReleaseYet_ReturnsNull()
    {
        var checker = new UpdateChecker(Http(Sha(Package), latest: HttpStatusCode.NotFound), "o/r");

        Assert.Null(await checker.GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Download_VerifiesChecksum()
    {
        var checker = new UpdateChecker(Http(Sha(Package)), "o/r");
        var release = (await checker.GetLatestAsync(CancellationToken.None))!;
        var file = _dir.File("update/package.zip");

        await checker.DownloadAsync(release, file, null, CancellationToken.None);

        Assert.Equal(Package, File.ReadAllBytes(file));
    }

    [Fact]
    public async Task Download_WithWrongChecksum_IsDiscarded()
    {
        var checker = new UpdateChecker(Http(Sha("other"u8.ToArray())), "o/r");
        var release = (await checker.GetLatestAsync(CancellationToken.None))!;
        var file = _dir.File("update/package.zip");

        var ex = await Assert.ThrowsAsync<UpdateException>(() => checker.DownloadAsync(release, file, null, CancellationToken.None));

        Assert.Contains("Prüfsumme", ex.Message);
        Assert.False(File.Exists(file));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zz" + "0000000000000000000000000000000000000000000000000000000000000000")]
    public void ParseChecksum_RejectsInvalid(string text)
    {
        Assert.Throws<UpdateException>(() => UpdateChecker.ParseChecksum(text));
    }

    [Fact]
    public void MissingRepository_IsRejected()
    {
        Assert.Throws<UpdateException>(() => new UpdateChecker(new HttpClient(), ""));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, (HttpStatusCode Status, string? Text)> route, byte[] package) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, text) = route(request);
            var response = new HttpResponseMessage(status)
            {
                Content = text is null ? new ByteArrayContent(package) : new StringContent(text),
            };
            return Task.FromResult(response);
        }
    }
}
