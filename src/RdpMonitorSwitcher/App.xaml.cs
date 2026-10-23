using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using RdpMonitorSwitcher.Agent;
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
    private FocusAgent? _helper;
    private Mutex? _helperMutex;
    private RegisteredWaitHandle? _helperStopWait;

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RdpMonitorSwitcher");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var isHelper = e.Args.Any(a => string.Equals(a, AgentSetup.Argument, StringComparison.OrdinalIgnoreCase));

        var logDirectory = Path.Combine(DataDirectory, "logs");
        Directory.CreateDirectory(logDirectory);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(logDirectory, isHelper ? "helper-.log" : "app-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7, shared: isHelper)
            .CreateLogger();
        _loggerFactory = LoggerFactory.Create(builder => builder.AddSerilog(dispose: true));
        var log = _loggerFactory.CreateLogger<App>();
        log.LogInformation("Starting RDP Monitor Switcher {Version}", typeof(App).Assembly.GetName().Version);

        if (isHelper)
        {
            StartHelper(log);
            return;
        }

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
        TryStartHelper(log);
        services.Updates = new UpdateService(main, _loggerFactory.CreateLogger<UpdateService>());

        var window = new MainWindow(services);
        MainWindow = window;
        window.Show();
        _ = services.Updates.CheckAsync(interactive: false);
    }

    /// <summary>"--agent": the helper on the controlled PC, without any window (see <see cref="FocusAgent"/>).</summary>
    private void StartHelper(Microsoft.Extensions.Logging.ILogger log)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += (_, args) =>
        {
            log.LogError(args.Exception, "Unhandled helper exception");
            args.Handled = true;
        };
        _helperMutex = new Mutex(true, AgentSetup.MutexName, out var created);
        if (!created)
        {
            log.LogInformation("Helper is already running in this session");
            _helperMutex.Dispose();
            _helperMutex = null;
            Shutdown();
            return;
        }
        var stop = new EventWaitHandle(false, EventResetMode.AutoReset, AgentSetup.StopEventName);
        _helperStopWait = ThreadPool.RegisterWaitForSingleObject(stop,
            (_, _) => Dispatcher.BeginInvoke(() => Shutdown()), null, Timeout.Infinite, executeOnlyOnce: true);
        _helper = new FocusAgent(log);
        _helper.Start();
    }

    /// <summary>Restarts the helper if it is set up on this PC but not running (e.g. after an update).</summary>
    private static void TryStartHelper(Microsoft.Extensions.Logging.ILogger log)
    {
        try
        {
            AgentSetup.EnsureRunning();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            log.LogWarning(ex, "Could not start the helper");
        }
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
        _helper?.Dispose();
        _helperStopWait?.Unregister(null);
        _helperMutex?.ReleaseMutex();
        _helperMutex?.Dispose();
        _loggerFactory?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
