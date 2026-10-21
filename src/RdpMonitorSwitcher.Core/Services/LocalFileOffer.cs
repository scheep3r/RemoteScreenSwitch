namespace RdpMonitorSwitcher.Core.Services;

/// <summary>One local file or folder offered to the remote PC.</summary>
/// <param name="RelativeName">Path as the remote PC sees it, e.g. "Fotos\\bild.jpg".</param>
public sealed record LocalFileEntry(string RelativeName, string FullPath, long Size, bool IsDirectory);

/// <summary>
/// Local files put on the RDP clipboard (Ctrl+C or drop). The remote PC reads the contents
/// lazily while it pastes; <see cref="Read"/> is called on a FreeRDP thread and only ever
/// serves the entries of this list.
/// </summary>
public sealed class LocalFileOffer : IDisposable
{
    public const int MaxEntries = 100_000;
    public const int MaxNameLength = 259; // FILEDESCRIPTORW.cFileName holds 260 WCHARs incl. terminator

    private readonly object _lock = new();
    private FileStream? _stream;
    private int _streamIndex = -1;
    private long _bytesServed;
    private bool _disposed;

    private LocalFileOffer(IReadOnlyList<LocalFileEntry> entries)
    {
        Entries = entries;
    }

    public IReadOnlyList<LocalFileEntry> Entries { get; }

    public int FileCount => Entries.Count(e => !e.IsDirectory);

    public long TotalBytes => Entries.Where(e => !e.IsDirectory).Sum(e => e.Size);

    public long BytesServed => Interlocked.Read(ref _bytesServed);

    /// <summary>The source paths as given (top-level items), e.g. to compare clipboard contents.</summary>
    public IReadOnlyList<string> SourcePaths { get; private init; } = [];

    /// <summary>
    /// Builds the list from files and folders (recursively). Folders come before their contents,
    /// symbolic links / junctions are not followed.
    /// </summary>
    public static LocalFileOffer Create(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var sources = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (sources.Count == 0)
            throw new FileTransferException("Es wurden keine Dateien übergeben.");

        var entries = new List<LocalFileEntry>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name))
                throw new FileTransferException($"„{source}“ kann nicht übertragen werden (Laufwerke werden nicht unterstützt).");
            if (!names.Add(name))
                throw new FileTransferException($"Zwei Elemente heißen „{name}“ – bitte nur eines davon auswählen.");

            if (Directory.Exists(source))
                AddDirectory(entries, source, name);
            else if (File.Exists(source))
                Add(entries, new LocalFileEntry(name, source, new FileInfo(source).Length, false));
            else
                throw new FileTransferException($"„{source}“ wurde nicht gefunden.");
        }
        return new LocalFileOffer(entries) { SourcePaths = sources };
    }

    private static void AddDirectory(List<LocalFileEntry> entries, string directory, string relative)
    {
        Add(entries, new LocalFileEntry(relative, directory, 0, true));
        var info = new DirectoryInfo(directory);
        foreach (var child in info.EnumerateFileSystemInfos().OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue; // do not follow links (could loop or leave the selection)
            var childRelative = relative + "\\" + child.Name;
            if (child is DirectoryInfo)
                AddDirectory(entries, child.FullName, childRelative);
            else if (child is FileInfo file)
                Add(entries, new LocalFileEntry(childRelative, file.FullName, file.Length, false));
        }
    }

    private static void Add(List<LocalFileEntry> entries, LocalFileEntry entry)
    {
        if (entry.RelativeName.Length > MaxNameLength)
            throw new FileTransferException($"Der Pfad „{entry.RelativeName}“ ist zu lang für die RDP-Übertragung (max. {MaxNameLength} Zeichen).");
        if (entries.Count >= MaxEntries)
            throw new FileTransferException($"Zu viele Dateien (mehr als {MaxEntries}).");
        entries.Add(entry);
    }

    /// <summary>Reads part of a file; thread-safe. Returns the number of bytes read (0 at end of file).</summary>
    public int Read(int index, long offset, Span<byte> buffer)
    {
        if (index < 0 || index >= Entries.Count || Entries[index].IsDirectory)
            throw new ArgumentOutOfRangeException(nameof(index));
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_streamIndex != index || _stream is null)
            {
                _stream?.Dispose();
                _stream = new FileStream(Entries[index].FullPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
                _streamIndex = index;
            }
            _stream.Position = offset;
            var total = 0;
            while (total < buffer.Length)
            {
                var read = _stream.Read(buffer[total..]);
                if (read == 0)
                    break;
                total += read;
            }
            Interlocked.Add(ref _bytesServed, total);
            return total;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _stream?.Dispose();
            _stream = null;
        }
    }
}
