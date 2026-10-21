using Microsoft.Extensions.Time.Testing;
using RdpMonitorSwitcher.Core.Rdp;
using RdpMonitorSwitcher.Core.Services;

namespace RdpMonitorSwitcher.Tests;

public sealed class RemoteFilePullerTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly FakeSession _session = new();
    private readonly FakeTimeProvider _time = new();

    public void Dispose() => _dir.Dispose();

    private string Target => Path.Combine(_dir.Path, "transfer");

    private static byte[] Bytes(int length) => Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();

    [Fact]
    public async Task Pull_PressesCtrlC_AndDownloadsFilesAndFolders()
    {
        var big = Bytes(RemoteFilePuller.ChunkSize * 2 + 123); // needs three chunks
        _session.RemoteFiles.Add((new RemoteFileEntry("report.pdf", 5, false), "hello"u8.ToArray()));
        _session.RemoteFiles.Add((new RemoteFileEntry("Fotos", null, true), []));
        _session.RemoteFiles.Add((new RemoteFileEntry("Fotos\\urlaub.jpg", big.Length, false), big));
        var progress = new List<TransferProgress>();

        var result = await new RemoteFilePuller(_session, _time).PullAsync(Target, new SyncProgress(progress), CancellationToken.None);

        Assert.Equal([(true, 0x1Du), (true, 0x2Eu), (false, 0x2Eu), (false, 0x1Du)], _session.Keys);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(Target, "report.pdf")));
        Assert.Equal(big, File.ReadAllBytes(Path.Combine(Target, "Fotos", "urlaub.jpg")));
        Assert.Equal(2, result.FileCount);
        Assert.Equal(5 + big.Length, result.TotalBytes);
        Assert.Equal([Path.Combine(Target, "report.pdf"), Path.Combine(Target, "Fotos")], result.TopLevelPaths);
        Assert.Equal(4, _session.ChunkRequests);
        Assert.Equal(1.0, progress[^1].Fraction);
    }

    [Fact]
    public async Task Pull_KeepsSeveralRequestsInFlight_AcrossFiles_AndToleratesReordering()
    {
        // 20 small files: one request at a time would cost 20 round trips.
        var contents = Enumerable.Range(0, 20).Select(i => Bytes(100 + i)).ToList();
        for (var i = 0; i < contents.Count; i++)
            _session.RemoteFiles.Add((new RemoteFileEntry($"file{i}.dll", contents[i].Length, false), contents[i]));
        _session.DeferFileResponses = true;

        var task = new RemoteFilePuller(_session, _time).PullAsync(Target, null, CancellationToken.None);

        Assert.Equal(RemoteFilePuller.PipelineDepth, _session.PendingRanges.Count); // all sent before any answer
        while (_session.PendingRanges.Count > 0)
        {
            _session.AnswerPending(reverse: true); // answers arrive out of order
            await Task.Yield();
            await Task.Delay(10);
        }
        var result = await task;

        Assert.Equal(20, result.FileCount);
        for (var i = 0; i < contents.Count; i++)
            Assert.Equal(contents[i], File.ReadAllBytes(Path.Combine(Target, $"file{i}.dll")));
    }

    [Fact]
    public async Task Pull_EmptyFile_IsCreated()
    {
        _session.RemoteFiles.Add((new RemoteFileEntry("empty.txt", 0, false), []));

        var result = await new RemoteFilePuller(_session, _time).PullAsync(Target, null, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(Target, "empty.txt")));
        Assert.Equal(0, _session.ChunkRequests);
        Assert.Equal(1, result.FileCount);
    }

    [Fact]
    public async Task Pull_FileWithoutSize_ReadsUntilShortChunk()
    {
        var content = Bytes(1000);
        _session.RemoteFiles.Add((new RemoteFileEntry("unknown.bin", null, false), content));

        await new RemoteFilePuller(_session, _time).PullAsync(Target, null, CancellationToken.None);

        Assert.Equal(content, File.ReadAllBytes(Path.Combine(Target, "unknown.bin")));
    }

    [Fact]
    public async Task Pull_NothingSelected_ExplainsWhatToDo()
    {
        var ex = await Assert.ThrowsAsync<FileTransferException>(() =>
            new RemoteFilePuller(_session, _time).PullAsync(Target, null, CancellationToken.None));

        Assert.Contains("keine Dateien markiert", ex.Message);
    }

    [Fact]
    public async Task Pull_RemoteDoesNotReactToCopy_TimesOut()
    {
        _session.RespondToCopy = false;
        _session.RemoteFiles.Add((new RemoteFileEntry("a.txt", 1, false), [1]));

        var task = new RemoteFilePuller(_session, _time).PullAsync(Target, null, CancellationToken.None);
        _time.Advance(RemoteFilePuller.CopyTimeout);

        var ex = await Assert.ThrowsAsync<FileTransferException>(() => task);
        Assert.Contains("Strg+C", ex.Message);
    }

    [Fact]
    public async Task Pull_ReadError_DeletesPartialDownload()
    {
        _session.RemoteFiles.Add((new RemoteFileEntry("ok.txt", 2, false), [1, 2]));
        _session.RemoteFiles.Add((new RemoteFileEntry("locked.db", 10, false), new byte[10]));
        _session.FailingFiles.Add(1);

        await Assert.ThrowsAsync<FileTransferException>(() =>
            new RemoteFilePuller(_session, _time).PullAsync(Target, null, CancellationToken.None));

        Assert.False(Directory.Exists(Target));
    }

    [Fact]
    public async Task Pull_Cancelled_DeletesPartialDownload()
    {
        _session.RemoteFiles.Add((new RemoteFileEntry("a.txt", 10, false), new byte[10]));
        _session.RespondToFileRequests = false;
        using var cts = new CancellationTokenSource();

        var task = new RemoteFilePuller(_session, _time).PullAsync(Target, null, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Theory]
    [InlineData("..\\evil.exe")]
    [InlineData("Fotos\\..\\..\\evil.exe")]
    [InlineData("\\Windows\\evil.exe")]
    [InlineData("C:\\evil.exe")]
    [InlineData("a\\b<c.txt")]
    [InlineData("")]
    public void RemoteNames_CannotEscapeTheTargetFolder(string name)
    {
        Assert.Throws<FileTransferException>(() => RemoteFilePuller.ToLocalRelativePath(name));
    }

    [Fact]
    public void RemoteNames_KeepFolderStructure()
    {
        Assert.Equal(Path.Combine("Fotos", "2024", "bild.jpg"), RemoteFilePuller.ToLocalRelativePath("Fotos\\2024\\bild.jpg"));
    }

    [Fact]
    public async Task Pull_MaliciousName_IsRejectedBeforeWriting()
    {
        _session.RemoteFiles.Add((new RemoteFileEntry("..\\evil.exe", 1, false), [1]));

        await Assert.ThrowsAsync<FileTransferException>(() =>
            new RemoteFilePuller(_session, _time).PullAsync(Target, null, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(_dir.Path, "evil.exe")));
        Assert.Equal(0, _session.ChunkRequests);
    }

    /// <summary>Progress&lt;T&gt; posts asynchronously; this records synchronously.</summary>
    private sealed class SyncProgress(List<TransferProgress> list) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => list.Add(value);
    }
}

public class DragOutTriggerTests
{
    private static DragOutTrigger Trigger(ScreenEdge edge = ScreenEdge.Right) =>
        new(new DragOutSettings { Edge = edge, Size = 20, ArmDelayMs = 300, ArmDistance = 10 });

    [Fact]
    public void FiresOnce_WhenADragReachesTheZone()
    {
        var t = Trigger();
        t.ButtonDown(0, 500, 300);

        Assert.False(t.Move(100, 600, 300, 1000, 600)); // moving, not armed yet
        Assert.False(t.Move(400, 900, 300, 1000, 600)); // armed, not in zone
        Assert.True(t.Move(450, 990, 300, 1000, 600));  // in zone
        Assert.False(t.Move(460, 995, 300, 1000, 600)); // only once
    }

    [Fact]
    public void DraggingPastTheWindowEdge_AlsoFires()
    {
        var t = Trigger();
        t.ButtonDown(0, 500, 300);
        t.Move(50, 700, 300, 1000, 600);

        Assert.True(t.Move(500, 1250, 300, 1000, 600)); // captured mouse outside the view
    }

    [Fact]
    public void ClickInsideTheZone_DoesNotFire()
    {
        var t = Trigger();
        t.ButtonDown(0, 995, 300); // pressed directly in the zone (e.g. scrollbar)

        Assert.False(t.Move(1000, 996, 301, 1000, 600)); // held long, but hardly moved
    }

    [Fact]
    public void QuickFlick_DoesNotFireBeforeTheDelay()
    {
        var t = Trigger();
        t.ButtonDown(0, 500, 300);

        Assert.False(t.Move(100, 995, 300, 1000, 600));
    }

    [Fact]
    public void Disabled_NeverFires()
    {
        var t = new DragOutTrigger(new DragOutSettings { Enabled = false });
        t.ButtonDown(0, 500, 300);

        Assert.False(t.Move(5000, 999, 300, 1000, 600));
    }

    [Fact]
    public void NewPress_RearmsTheTrigger()
    {
        var t = Trigger();
        t.ButtonDown(0, 500, 300);
        t.Move(50, 700, 300, 1000, 600);
        Assert.True(t.Move(500, 990, 300, 1000, 600));
        t.ButtonUp();

        t.ButtonDown(1000, 500, 300);
        t.Move(1050, 700, 300, 1000, 600);
        Assert.True(t.Move(1500, 990, 300, 1000, 600));
    }

    [Theory]
    [InlineData(ScreenEdge.Left, 5, 300, true)]
    [InlineData(ScreenEdge.Left, 50, 300, false)]
    [InlineData(ScreenEdge.Top, 500, 10, true)]
    [InlineData(ScreenEdge.Bottom, 500, 590, true)]
    [InlineData(ScreenEdge.Bottom, 500, 500, false)]
    public void Zones(ScreenEdge edge, double x, double y, bool inside)
    {
        Assert.Equal(inside, Trigger(edge).IsInZone(x, y, 1000, 600));
    }

    [Fact]
    public void Settings_AreClamped()
    {
        var s = new DragOutSettings { Size = 5000, ArmDelayMs = -5, Edge = (ScreenEdge)42 }.Normalized();

        Assert.Equal(200, s.Size);
        Assert.Equal(0, s.ArmDelayMs);
        Assert.Equal(ScreenEdge.Right, s.Edge);
    }
}

public sealed class LocalFileOfferTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Create(string relative, string content = "")
    {
        var path = Path.Combine(_dir.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void FilesAndFolders_AreListedRecursively_FoldersFirst()
    {
        var report = Create("report.pdf", "12345");
        Create("Fotos/a.jpg", "aa");
        Create("Fotos/2024/b.jpg", "bbb");

        using var offer = LocalFileOffer.Create([report, Path.Combine(_dir.Path, "Fotos")]);

        Assert.Equal(
            ["report.pdf", "Fotos", "Fotos\\2024", "Fotos\\2024\\b.jpg", "Fotos\\a.jpg"],
            offer.Entries.Select(e => e.RelativeName));
        Assert.True(offer.Entries[1].IsDirectory);
        Assert.Equal(3, offer.FileCount);
        Assert.Equal(10, offer.TotalBytes);
    }

    [Fact]
    public void Read_ServesRangesOfTheOfferedFiles()
    {
        var file = Create("data.txt", "Hello Remote PC");
        using var offer = LocalFileOffer.Create([file]);
        var buffer = new byte[6];

        var read = offer.Read(0, 6, buffer);
        Assert.Equal(6, read);
        Assert.Equal("Remote", System.Text.Encoding.ASCII.GetString(buffer));

        var tail = offer.Read(0, 13, buffer);
        Assert.Equal(2, tail);
        Assert.Equal("PC", System.Text.Encoding.ASCII.GetString(buffer, 0, tail));
        Assert.Equal(0, offer.Read(0, 100, buffer));
        Assert.Equal(8, offer.BytesServed);
    }

    [Fact]
    public void Read_OnlyServesFilesOfTheList()
    {
        Create("Fotos/a.jpg", "aa");
        using var offer = LocalFileOffer.Create([Path.Combine(_dir.Path, "Fotos")]);

        Assert.Throws<ArgumentOutOfRangeException>(() => offer.Read(0, 0, new byte[4])); // a folder
        Assert.Throws<ArgumentOutOfRangeException>(() => offer.Read(5, 0, new byte[4])); // not in the list
    }

    [Fact]
    public void Read_AfterDispose_Fails()
    {
        var offer = LocalFileOffer.Create([Create("x.txt", "x")]);
        offer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => offer.Read(0, 0, new byte[1]));
    }

    [Fact]
    public void MissingFile_IsReported()
    {
        var ex = Assert.Throws<FileTransferException>(() => LocalFileOffer.Create([Path.Combine(_dir.Path, "nope.txt")]));
        Assert.Contains("nicht gefunden", ex.Message);
    }

    [Fact]
    public void SameNameFromTwoFolders_IsRejected()
    {
        var a = Create("one/same.txt");
        var b = Create("two/same.txt");

        Assert.Throws<FileTransferException>(() => LocalFileOffer.Create([a, b]));
    }

    [Fact]
    public void TooLongRelativePath_IsRejected()
    {
        var deep = Path.Combine(_dir.Path, "top");
        var path = Path.Combine(deep, new string('a', 130), new string('b', 130), "file.txt"); // > 259 chars relative
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");

        var ex = Assert.Throws<FileTransferException>(() => LocalFileOffer.Create([deep]));
        Assert.Contains("zu lang", ex.Message);
    }
}

public class RemoteFileDropperTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly FakeSession _session = new();
    private readonly FakeTimeProvider _time = new();

    public void Dispose() => _dir.Dispose();

    private LocalFileOffer Offer()
    {
        var path = Path.Combine(_dir.Path, "report.pdf");
        File.WriteAllText(path, "pdf");
        return LocalFileOffer.Create([path]);
    }

    [Fact]
    public async Task Drop_OffersFiles_ClicksAtTheDropPoint_ThenPastes()
    {
        using var offer = Offer();

        var task = new RemoteFileDropper(_session, _time).DropAsync(offer, new Core.Models.PixelPoint(2500, 400), CancellationToken.None);
        Assert.Same(offer, _session.Offer);
        Assert.Empty(_session.Keys); // no paste before the click has settled

        _time.Advance(RemoteFileDropper.ClickToPasteDelay);
        await task;

        Assert.Equal(
            [(MouseFlags.Move, 2500, 400), ((ushort)(MouseFlags.Button1 | MouseFlags.Down), 2500, 400), (MouseFlags.Button1, 2500, 400)],
            _session.MouseEvents);
        Assert.Equal([(true, 0x1Du), (true, 0x2Fu), (false, 0x2Fu), (false, 0x1Du)], _session.Keys);
    }

    [Fact]
    public async Task RejectedOffer_DoesNotClickOrPaste()
    {
        _session.AcceptOffer = false;
        using var offer = Offer();

        await Assert.ThrowsAsync<FileTransferException>(() =>
            new RemoteFileDropper(_session, _time).DropAsync(offer, new Core.Models.PixelPoint(10, 10), CancellationToken.None));

        Assert.Empty(_session.MouseEvents);
        Assert.Empty(_session.Keys);
    }

    [Fact]
    public async Task UnansweredOffer_TimesOut()
    {
        _session.AcceptOffer = null;
        using var offer = Offer();

        var task = new RemoteFileDropper(_session, _time).DropAsync(offer, new Core.Models.PixelPoint(10, 10), CancellationToken.None);
        _time.Advance(RemoteFileDropper.AcceptTimeout);

        var ex = await Assert.ThrowsAsync<FileTransferException>(() => task);
        Assert.Contains("nicht bestätigt", ex.Message);
        Assert.Empty(_session.Keys);
    }
}
