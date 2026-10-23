using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using Microsoft.Extensions.Logging;
using RdpMonitorSwitcher.Core.Services;
using RdpMonitorSwitcher.Core.ViewModels;

namespace RdpMonitorSwitcher.Services;

/// <summary>
/// "Check for updates": downloads the newest GitHub release (checksum-verified) and replaces the
/// app files. Running files cannot be overwritten on Windows, but they can be renamed: the old
/// files become *.old, the new ones are copied in, the app restarts and deletes the *.old files.
/// </summary>
internal sealed class UpdateService
{
    private const string ExeName = "RdpMonitorSwitcher.exe";

    private readonly MainViewModel _main;
    private readonly ILogger _log;
    private readonly string? _repository;
    private bool _busy;

    public UpdateService(MainViewModel main, ILogger<UpdateService> logger)
    {
        _main = main;
        _log = logger;
        _repository = typeof(UpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateRepository")?.Value;
    }

    public static Version CurrentVersion => typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_repository);

    private static string AppDirectory => AppContext.BaseDirectory;

    /// <summary>Removes *.old files left by the previous update (they were still in use then).</summary>
    public static void CleanupAfterUpdate()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(AppDirectory, "*.old", SearchOption.AllDirectories))
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
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <param name="interactive">False for the silent check at startup (only a notice if newer).</param>
    public async Task CheckAsync(bool interactive)
    {
        if (_busy)
            return;
        if (!IsConfigured)
        {
            if (interactive)
                _main.Notice = "Für diese Version ist keine Update-Quelle hinterlegt.";
            return;
        }

        _busy = true;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            var checker = new UpdateChecker(http, _repository!);
            var release = await checker.GetLatestAsync(CancellationToken.None);
            var current = CurrentVersion;
            if (release is null || !UpdateChecker.IsNewer(current, release))
            {
                if (interactive)
                    _main.Notice = $"Du hast die neueste Version ({UpdateChecker.FormatVersion(current)}).";
                return;
            }
            if (!interactive)
            {
                _main.Notice = $"Update verfügbar: {UpdateChecker.Describe(release)} – Klick auf ⟳ in der Toolbar zum Installieren.";
                return;
            }

            var notes = string.IsNullOrWhiteSpace(release.Notes) ? "" : "\n\nNeu:\n" + Truncate(release.Notes, 1200);
            var answer = MessageBox.Show(Application.Current.MainWindow!,
                $"{UpdateChecker.Describe(release)} ist verfügbar (installiert: {UpdateChecker.FormatVersion(current)}).{notes}\n\n" +
                "Jetzt installieren? Eine bestehende Verbindung wird dafür getrennt; die App startet neu.",
                "Update verfügbar", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer != MessageBoxResult.Yes)
                return;

            await InstallAsync(checker, release);
        }
        catch (UpdateException ex)
        {
            if (interactive)
                _main.Notice = ex.Message;
            _log.LogWarning(ex, "Update check failed");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _log.LogError(ex, "Update failed");
            _main.Notice = $"Das Update konnte nicht installiert werden: {ex.Message}";
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task InstallAsync(UpdateChecker checker, ReleaseInfo release)
    {
        EnsureWritable();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RdpMonitorSwitcher", "update");
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        var package = Path.Combine(root, "package.zip");
        var staging = Path.Combine(root, "files");

        var progress = new Progress<double>(p => _main.Notice = $"Lade Update {UpdateChecker.FormatVersion(release.Version)} … {p:P0}");
        await checker.DownloadAsync(release, package, progress, CancellationToken.None);
        _main.Notice = "Installiere Update …";
        await Task.Run(() => ZipFile.ExtractToDirectory(package, staging, overwriteFiles: true));

        var source = FindPackageRoot(staging);
        _log.LogInformation("Installing update {Version} from {Source}", release.Version, source);
        _main.Controller.Disconnect();
        await Task.Run(() =>
        {
            Agent.AgentSetup.Stop(); // started again by the new version
            ReplaceFiles(source, AppDirectory);
        });

        Process.Start(new ProcessStartInfo(Path.Combine(AppDirectory, ExeName)) { UseShellExecute = false, WorkingDirectory = AppDirectory });
        Application.Current.Shutdown();
    }

    /// <summary>The folder inside the ZIP that contains the app (root or a single sub folder).</summary>
    private static string FindPackageRoot(string staging)
    {
        if (File.Exists(Path.Combine(staging, ExeName)))
            return Validate(staging);
        var dirs = Directory.GetDirectories(staging);
        if (dirs.Length == 1 && File.Exists(Path.Combine(dirs[0], ExeName)))
            return Validate(dirs[0]);
        throw new UpdateException("Das Update-Paket enthält die App nicht.");
    }

    private static string Validate(string dir)
    {
        if (!File.Exists(Path.Combine(dir, "RdpBridge.dll")))
            throw new UpdateException("Das Update-Paket ist unvollständig (RdpBridge.dll fehlt).");
        return dir;
    }

    private static void EnsureWritable()
    {
        var probe = Path.Combine(AppDirectory, $".write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UpdateException($"In den App-Ordner ({AppDirectory}) kann nicht geschrieben werden. Verschiebe die App z. B. nach Dokumente oder D:\\, dann klappt das Update.", ex);
        }
    }

    /// <summary>Renames existing files to *.old and copies the new ones; rolls back on failure.</summary>
    private static void ReplaceFiles(string source, string target)
    {
        var moved = new List<(string Original, string Backup)>();
        var created = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                var destination = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination))
                {
                    var backup = destination + ".old";
                    if (File.Exists(backup))
                        WithRetry(() => File.Delete(backup));
                    WithRetry(() => File.Move(destination, backup));
                    moved.Add((destination, backup));
                }
                WithRetry(() => File.Copy(file, destination));
                created.Add(destination);
            }
        }
        catch
        {
            foreach (var file in created)
            {
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            foreach (var (original, backup) in moved)
            {
                try { File.Move(backup, original, overwrite: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    /// <summary>
    /// Virus scanners (e.g. Windows Defender) briefly lock freshly written or touched DLLs;
    /// retry a few times before giving up.
    /// </summary>
    private static void WithRetry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(300 * attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < 10)
            {
                Thread.Sleep(300 * attempt);
            }
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + " …";
}
