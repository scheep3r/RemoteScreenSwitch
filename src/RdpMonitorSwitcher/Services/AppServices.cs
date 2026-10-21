using Microsoft.Extensions.Logging;
using RdpMonitorSwitcher.Core.ViewModels;

namespace RdpMonitorSwitcher.Services;

/// <summary>The shared services of the running app (composition root output).</summary>
internal sealed class AppServices(
    MainViewModel main,
    FrameRenderer renderer,
    InputRouter input,
    RemoteCursorManager cursors,
    ClipboardSync clipboard,
    ILoggerFactory loggerFactory)
{
    public MainViewModel Main { get; } = main;
    public FrameRenderer Renderer { get; } = renderer;
    public InputRouter Input { get; } = input;
    public RemoteCursorManager Cursors { get; } = cursors;
    public ClipboardSync Clipboard { get; } = clipboard;
    public ILoggerFactory LoggerFactory { get; } = loggerFactory;
    public FileTransferService? FileTransfer { get; set; }
    public UpdateService? Updates { get; set; }
}
