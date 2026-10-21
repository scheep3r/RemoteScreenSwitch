using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using RdpMonitorSwitcher.Core.Configuration;
using RdpMonitorSwitcher.Core.Services;
using RdpMonitorSwitcher.Core.ViewModels;
using RdpMonitorSwitcher.Infrastructure;
using RdpMonitorSwitcher.Rdp;
using RdpMonitorSwitcher.Services;
using RdpMonitorSwitcher.Views;
using Serilog;

namespace RdpMonitorSwitcher;

public partial class App : Application
{
    private ILoggerFactory? _loggerFactory;
    private AppServices? _services;

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RdpMonitorSwitcher");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var logDirectory = Path.Combine(DataDirectory, "logs");
        Directory.CreateDirectory(logDirectory);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(logDirectory, "app-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();
        _loggerFactory = LoggerFactory.Create(builder => builder.AddSerilog(dispose: true));
        var log = _loggerFactory.CreateLogger<App>();
        log.LogInformation("Starting RDP Monitor Switcher {Version}", typeof(App).Assembly.GetName().Version);

        DispatcherUnhandledException += (_, args) =>
        {
            log.LogError(args.Exception, "Unhandled UI exception");
            MessageBox.Show(args.Exception.Message, "Unerwarteter Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        if (!CheckNativeBridge(logDirectory, log))
        {
            Shutdown(1);
            return;
        }

        var dispatcher = Dispatcher;
        var credentials = new CredentialManagerStore();
        var controller = new RemoteDesktopController(
            new FreeRdpSessionFactory(dispatcher, _loggerFactory),
            new ReconnectPolicy(),
            TimeProvider.System,
            new DispatcherSynchronizationContext(dispatcher),
            _loggerFactory.CreateLogger<RemoteDesktopController>());

        AppServices? services = null;
        var windowService = new WindowService(() => services!);
        var main = new MainViewModel(
            controller,
            new SettingsStore(SettingsStore.DefaultPath, _loggerFactory.CreateLogger<SettingsStore>()),
            credentials,
            new DialogService(dispatcher, credentials, _loggerFactory.CreateLogger<DialogService>()),
            windowService,
            new LocalEnvironmentProvider(),
            _loggerFactory.CreateLogger<MainViewModel>(),
            new PcWaker(new UdpMagicPacketSender(), new FritzBoxClient(), TcpProbe.IsReachableAsync));

        var input = new InputRouter(main, dispatcher, _loggerFactory.CreateLogger<InputRouter>());
        main.InputResetter = input;
        services = new AppServices(
            main,
            new FrameRenderer(controller, dispatcher),
            input,
            new RemoteCursorManager(controller),
            new ClipboardSync(controller, _loggerFactory.CreateLogger<ClipboardSync>()),
            _loggerFactory);
        _services = services;
        services.FileTransfer = new FileTransferService(main, services.Renderer, input, services.Clipboard,
            _loggerFactory.CreateLogger<FileTransferService>());
        FileTransferService.CleanupOldTransfers(TimeSpan.FromDays(1));
        UpdateService.CleanupAfterUpdate();
        services.Updates = new UpdateService(main, _loggerFactory.CreateLogger<UpdateService>());

        var window = new MainWindow(services);
        MainWindow = window;
        window.Show();
        _ = services.Updates.CheckAsync(interactive: false);
    }

    private static bool CheckNativeBridge(string logDirectory, Microsoft.Extensions.Logging.ILogger log)
    {
        try
        {
            var version = RdpBridgeNative.GetApiVersion();
            if (version != RdpBridgeNative.ApiVersion)
                throw new InvalidOperationException($"RdpBridge.dll API version {version}, expected {RdpBridgeNative.ApiVersion}.");
            RdpBridgeNative.ConfigureLogging(logDirectory, "INFO");
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            log.LogCritical(ex, "RdpBridge.dll could not be loaded");
            MessageBox.Show(
                "Die RDP-Komponente (RdpBridge.dll) fehlt oder passt nicht zu dieser Version.\n\n" + ex.Message,
                "RDP Monitor Switcher", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_services is not null)
        {
            _services.Main.Dispose();
            _services.Input.Dispose();
            _services.Clipboard.Dispose();
            _services.Cursors.Dispose();
            FreeRdpSession.WaitForPendingShutdowns(TimeSpan.FromSeconds(3));
        }
        _loggerFactory?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
