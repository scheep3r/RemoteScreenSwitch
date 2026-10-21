using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace RdpMonitorSwitcher.Core.Services;

public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A published release with its downloadable app package.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string Notes, Uri PackageUrl, Uri ChecksumUrl, long Size);

/// <summary>
/// Looks for new versions in the public GitHub releases of the project and downloads them.
/// A release must contain "RdpMonitorSwitcher-&lt;version&gt;-win-x64.zip" and a matching
/// ".zip.sha256" file; packages without a valid checksum are never installed.
/// </summary>
public sealed class UpdateChecker
{
    public const string PackageSuffix = "-win-x64.zip";
    public const string ChecksumSuffix = ".zip.sha256";

    private readonly HttpClient _http;
    private readonly string _repository;

    /// <param name="repository">"owner/name" of the GitHub repository.</param>
    public UpdateChecker(HttpClient http, string repository)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (string.IsNullOrWhiteSpace(repository) || repository.Count(c => c == '/') != 1)
            throw new UpdateException("Keine Update-Quelle konfiguriert.");
        _repository = repository.Trim();
    }

    public static bool IsNewer(Version current, ReleaseInfo release) => release.Version > Normalize(current);

    /// <summary>Latest published release, or null if there is none yet.</summary>
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{_repository}/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("RdpMonitorSwitcher", "1"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException("GitHub ist nicht erreichbar. Besteht eine Internetverbindung?", ex);
        }
        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null; // no release published yet
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"GitHub hat mit Fehler {(int)response.StatusCode} geantwortet.");
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return Parse(json);
        }
    }

    public static ReleaseInfo Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
                throw new UpdateException($"Die Versionsnummer „{tag}“ ist ungültig.");
            var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";

            (Uri Url, long Size)? package = null;
            Uri? checksum = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                var url = new Uri(asset.GetProperty("browser_download_url").GetString() ?? "");
                if (name.EndsWith(PackageSuffix, StringComparison.OrdinalIgnoreCase))
                    package = (url, asset.GetProperty("size").GetInt64());
                else if (name.EndsWith(ChecksumSuffix, StringComparison.OrdinalIgnoreCase))
                    checksum = url;
            }
            if (package is null || checksum is null)
                throw new UpdateException($"Version {tag} enthält kein vollständiges Installationspaket.");
            if (package.Value.Url.Scheme != Uri.UriSchemeHttps || checksum.Scheme != Uri.UriSchemeHttps)
                throw new UpdateException("Das Update wird nur über HTTPS geladen.");
            return new ReleaseInfo(Normalize(version), tag, notes, package.Value.Url, checksum, package.Value.Size);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or UriFormatException)
        {
            throw new UpdateException("Die Antwort von GitHub konnte nicht gelesen werden.", ex);
        }
    }

    /// <summary>Downloads the package to <paramref name="targetFile"/> and verifies its SHA-256 checksum.</summary>
    public async Task DownloadAsync(ReleaseInfo release, string targetFile, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var expected = ParseChecksum(await GetStringAsync(release.ChecksumUrl, cancellationToken));
        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
        try
        {
            using var response = await _http.GetAsync(release.PackageUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"Download fehlgeschlagen (Fehler {(int)response.StatusCode}).");
            var total = response.Content.Headers.ContentLength ?? release.Size;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                sha.AppendData(buffer, 0, read);
                done += read;
                if (total > 0)
                    progress?.Report(Math.Clamp((double)done / total, 0, 1));
            }
            var actual = Convert.ToHexString(sha.GetHashAndReset());
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("Die Prüfsumme des Updates stimmt nicht – das Paket wurde verworfen.");
        }
        catch (HttpRequestException ex)
        {
            TryDelete(targetFile);
            throw new UpdateException("Der Download wurde unterbrochen.", ex);
        }
        catch
        {
            TryDelete(targetFile);
            throw;
        }
    }

    /// <summary>Accepts "HEX" or "HEX  filename" (sha256sum / Get-FileHash style).</summary>
    public static string ParseChecksum(string text)
    {
        var token = (text ?? "").Trim().Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (token.Length != 64 || !token.All(char.IsAsciiHexDigit))
            throw new UpdateException("Die Prüfsummen-Datei des Updates ist ungültig.");
        return token.ToUpperInvariant();
    }

    private async Task<string> GetStringAsync(Uri url, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.GetStringAsync(url, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException("Die Prüfsumme des Updates konnte nicht geladen werden.", ex);
        }
    }

    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static string FormatVersion(Version v) =>
        v.Revision > 0 ? v.ToString(4) : v.ToString(3);

    public static string Describe(ReleaseInfo release) =>
        string.Create(CultureInfo.InvariantCulture, $"Version {FormatVersion(release.Version)} ({release.Size / 1024.0 / 1024.0:0.#} MB)");
}
