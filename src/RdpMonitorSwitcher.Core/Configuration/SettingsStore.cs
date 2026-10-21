using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RdpMonitorSwitcher.Core.Configuration;

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON. Never contains passwords.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ILogger _log;

    public SettingsStore(string filePath, ILogger<SettingsStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
        _log = logger ?? (ILogger)NullLogger.Instance;
    }

    public string FilePath { get; }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RdpMonitorSwitcher", "settings.json");

    /// <summary>Returns the stored settings, or defaults if the file is missing or unreadable.</summary>
    public AppSettings Load()
    {
        if (!File.Exists(FilePath))
            return new AppSettings();
        try
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log.LogWarning(ex, "Settings file {Path} is unreadable; using defaults", FilePath);
            TryBackupCorruptFile();
            return new AppSettings();
        }
    }

    /// <summary>Writes atomically (temp file + replace) so a crash never leaves a half-written file.</summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        File.Move(temp, FilePath, overwrite: true);
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            File.Copy(FilePath, FilePath + ".corrupt", overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
