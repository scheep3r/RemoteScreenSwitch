using RdpMonitorSwitcher.Core.Rdp;

namespace RdpMonitorSwitcher.Core.Services;

public sealed class FileTransferException(string message) : Exception(message);

public readonly record struct TransferProgress(int FileIndex, int FileCount, string CurrentFile, long BytesDone, long? BytesTotal)
{
    public double? Fraction => BytesTotal is > 0 ? Math.Clamp((double)BytesDone / BytesTotal.Value, 0, 1) : null;
}

/// <summary>Files fetched from the remote PC.</summary>
/// <param name="TopLevelPaths">Local paths of the selected items (files and folders), for drag &amp; drop / clipboard.</param>
public sealed record PullResult(string Directory, IReadOnlyList<string> TopLevelPaths, int FileCount, long TotalBytes);

/// <summary>
/// Copies the current selection on the remote PC (Ctrl+C there) and downloads the files through
/// the RDP clipboard channel into a local directory. RDP has no drag &amp; drop channel; the
/// clipboard file streams (MS-RDPECLIP FileContents) are the only way to get files out.
/// Must be used from the thread on which the session raises its events.
/// </summary>
public sealed class RemoteFilePuller
{
    public const int ChunkSize = 512 * 1024;

    /// <summary>Chunk requests kept in flight at once (hides the round-trip time).</summary>
    public const int PipelineDepth = 8;
    public static readonly TimeSpan CopyTimeout = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ChunkTimeout = TimeSpan.FromSeconds(30);

    private const uint ScanControl = 0x1D;
    private const uint ScanC = 0x2E;

    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    private readonly IRdpSession _session;
    private readonly TimeProvider _time;
    private uint _nextStreamId = 1;

    public RemoteFilePuller(IRdpSession session, TimeProvider? time = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _time = time ?? TimeProvider.System;
    }

    public async Task<PullResult> PullAsync(string targetDirectory, IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        var clipboard = await CopyRemoteSelectionAsync(cancellationToken);
        if (!clipboard.HasFiles)
            throw new FileTransferException("Auf dem Remote-PC sind keine Dateien markiert. Markiere Dateien im Explorer und versuche es erneut.");

        var list = await RequestAsync<RemoteFileList>(
            h => _session.RemoteFileListReceived += h, h => _session.RemoteFileListReceived -= h,
            _ => true, () => _session.RequestRemoteFileList(), ListTimeout, cancellationToken,
            "Die Dateiliste des Remote-PCs konnte nicht abgerufen werden.");
        if (!list.Success || list.Files.Count == 0)
            throw new FileTransferException("Die Dateiliste des Remote-PCs ist leer oder konnte nicht gelesen werden.");

        var entries = list.Files.Select((f, i) => (Entry: f, Index: i, Path: ToLocalRelativePath(f.Name))).ToList();
        var files = entries.Where(e => !e.Entry.IsDirectory).ToList();
        long? total = files.All(f => f.Entry.Size.HasValue) ? files.Sum(f => f.Entry.Size!.Value) : null;

        Directory.CreateDirectory(targetDirectory);
        var state = new DownloadState(files.Count, total, progress);
        using var pipelineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var inFlight = new Queue<(Chunk Chunk, Task<byte[]> Data)>();
        try
        {
            // Folders and empty files first, so the structure exists even without data.
            var fileNumber = 0;
            var numbered = new List<(RemoteFileEntry Entry, int Index, string Path, int Number)>();
            foreach (var (entry, index, relative) in entries)
            {
                var localPath = Path.Combine(targetDirectory, relative);
                if (entry.IsDirectory)
                {
                    Directory.CreateDirectory(localPath);
                    continue;
                }
                fileNumber++;
                Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                if (entry.Size == 0)
                {
                    await File.WriteAllBytesAsync(localPath, [], cancellationToken);
                    state.Report(fileNumber, relative, 0);
                }
                else
                {
                    numbered.Add((entry, index, localPath, fileNumber));
                }
            }

            // Files with known size: up to PipelineDepth chunk requests in flight, across file
            // boundaries, so the connection never idles waiting for a round trip.
            var plan = numbered.Where(f => f.Entry.Size.HasValue).SelectMany(f => PlanChunks(f.Entry.Size!.Value)
                .Select(c => new Chunk(f.Index, f.Path, entries[f.Index].Path, f.Number, c.Offset, c.Length, c.Last)));
            using var planned = plan.GetEnumerator();
            var hasMore = planned.MoveNext();
            FileStream? stream = null;
            try
            {
                while (hasMore || inFlight.Count > 0)
                {
                    while (hasMore && inFlight.Count < PipelineDepth)
                    {
                        var c = planned.Current;
                        inFlight.Enqueue((c, ReadChunkAsync(c.ListIndex, c.Offset, c.Length, pipelineCts.Token)));
                        hasMore = planned.MoveNext();
                    }

                    var (chunk, data) = inFlight.Dequeue();
                    var bytes = await data;
                    if (bytes.Length != chunk.Length)
                        throw new FileTransferException($"„{chunk.RelativePath}“ wurde vom Remote-PC unvollständig geliefert.");
                    if (chunk.Offset == 0)
                    {
                        if (stream is not null)
                            await stream.DisposeAsync();
                        stream = new FileStream(chunk.LocalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                    }
                    await stream!.WriteAsync(bytes, cancellationToken);
                    if (chunk.Last)
                    {
                        await stream.DisposeAsync();
                        stream = null;
                    }
                    state.Report(chunk.FileNumber, chunk.RelativePath, bytes.Length);
                }
            }
            finally
            {
                if (stream is not null)
                    await stream.DisposeAsync();
            }

            // Files without announced size (rare): read sequentially until a short chunk.
            foreach (var (entry, index, localPath, number) in numbered.Where(f => !f.Entry.Size.HasValue))
            {
                var relative = entries[index].Path;
                await using var unsized = new FileStream(localPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                long offset = 0;
                while (true)
                {
                    var bytes = await ReadChunkAsync(index, offset, ChunkSize, cancellationToken);
                    await unsized.WriteAsync(bytes, cancellationToken);
                    offset += bytes.Length;
                    state.Report(number, relative, bytes.Length);
                    if (bytes.Length < ChunkSize)
                        break;
                }
            }
        }
        catch
        {
            pipelineCts.Cancel();
            foreach (var (_, pending) in inFlight)
            {
                try
                {
                    await pending;
                }
                catch (Exception)
                {
                    // Already failing; only make sure nothing is left running.
                }
            }
            TryDelete(targetDirectory);
            throw;
        }

        var topLevel = entries
            .Where(e => !e.Path.Contains(Path.DirectorySeparatorChar))
            .Select(e => Path.Combine(targetDirectory, e.Path))
            .ToList();
        return new PullResult(targetDirectory, topLevel, files.Count, state.Done);
    }

    private sealed record Chunk(int ListIndex, string LocalPath, string RelativePath, int FileNumber, long Offset, int Length, bool Last);

    private static IEnumerable<(long Offset, int Length, bool Last)> PlanChunks(long size)
    {
        for (long offset = 0; offset < size; offset += ChunkSize)
        {
            var length = (int)Math.Min(ChunkSize, size - offset);
            yield return (offset, length, offset + length >= size);
        }
    }

    private sealed class DownloadState(int fileCount, long? total, IProgress<TransferProgress>? progress)
    {
        public long Done { get; private set; }

        public void Report(int fileNumber, string relativePath, int bytes)
        {
            Done += bytes;
            progress?.Report(new TransferProgress(fileNumber, fileCount, relativePath, Done, total));
        }
    }

    /// <summary>Presses Ctrl+C on the remote PC and waits for the resulting clipboard change.</summary>
    private Task<RemoteClipboardInfo> CopyRemoteSelectionAsync(CancellationToken cancellationToken) =>
        RequestAsync<RemoteClipboardInfo>(
            h => _session.RemoteClipboardChanged += h, h => _session.RemoteClipboardChanged -= h,
            _ => true,
            () =>
            {
                _session.SendKey(true, ScanControl);
                _session.SendKey(true, ScanC);
                _session.SendKey(false, ScanC);
                _session.SendKey(false, ScanControl);
                return true;
            },
            CopyTimeout, cancellationToken,
            "Der Remote-PC hat nicht auf Strg+C reagiert. Ist im Explorer etwas markiert und die Zwischenablage aktiviert?");

    private async Task<byte[]> ReadChunkAsync(int listIndex, long offset, int length, CancellationToken cancellationToken)
    {
        var streamId = _nextStreamId++;
        var chunk = await RequestAsync<FileChunk>(
            h => _session.FileChunkReceived += h, h => _session.FileChunkReceived -= h,
            c => c.StreamId == streamId,
            () => _session.RequestFileRange(streamId, listIndex, offset, length),
            ChunkTimeout, cancellationToken,
            "Der Remote-PC hat die Dateiübertragung nicht beantwortet.");
        if (!chunk.Success)
            throw new FileTransferException("Der Remote-PC hat das Lesen einer Datei abgelehnt (gesperrt oder keine Berechtigung?).");
        return chunk.Data;
    }

    /// <summary>Subscribes, sends a request and waits for the first matching event (with timeout).</summary>
    private async Task<T> RequestAsync<T>(Action<EventHandler<T>> subscribe, Action<EventHandler<T>> unsubscribe,
        Func<T, bool> match, Func<bool> send, TimeSpan timeout, CancellationToken cancellationToken, string timeoutMessage)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<T> handler = (_, value) =>
        {
            if (match(value))
                tcs.TrySetResult(value);
        };
        subscribe(handler);
        try
        {
            if (!send())
                throw new FileTransferException(timeoutMessage);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(timeout, _time, timeoutCts.Token);
            var finished = await Task.WhenAny(tcs.Task, delay);
            timeoutCts.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            if (finished != tcs.Task)
                throw new FileTransferException(timeoutMessage);
            return await tcs.Task;
        }
        finally
        {
            unsubscribe(handler);
        }
    }

    /// <summary>
    /// Converts a remote relative name ("folder\file.txt") into a safe local relative path.
    /// Rejects absolute paths, ".." and invalid characters so a remote PC cannot write outside the target folder.
    /// </summary>
    public static string ToLocalRelativePath(string remoteName)
    {
        var parts = (remoteName ?? "").Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || remoteName!.StartsWith('\\') || remoteName.StartsWith('/') || remoteName.Contains(':'))
            throw new FileTransferException($"Ungültiger Dateiname vom Remote-PC: „{remoteName}“");
        foreach (var part in parts)
        {
            if (part is "." or ".." || part.IndexOfAny(InvalidNameChars) >= 0 || part.EndsWith(' ') || part.EndsWith('.'))
                throw new FileTransferException($"Ungültiger Dateiname vom Remote-PC: „{remoteName}“");
        }
        return Path.Combine(parts);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
