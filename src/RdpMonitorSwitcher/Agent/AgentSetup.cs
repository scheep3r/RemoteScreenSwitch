using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Win32;

namespace RdpMonitorSwitcher.Agent;

/// <summary>
/// Sets up the helper on the PC that is being controlled: an autostart entry for the current user
/// (no admin rights needed) plus starting/stopping the running instance.
/// </summary>
internal static class AgentSetup
{
    public const string Argument = "--agent";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "RdpMonitorSwitcherHelper";

    /// <summary>Held by the running helper (one per Windows session).</summary>
    public const string MutexName = @"Local\RdpMonitorSwitcher.Helper";

    /// <summary>Set to ask the running helper to exit.</summary>
    public const string StopEventName = @"Local\RdpMonitorSwitcher.HelperStop";

    private static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "RdpMonitorSwitcher.exe");

    private static string Command => $"\"{ExePath}\" {Argument}";

    public static bool IsInstalled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
    }

    public static bool IsRunning
    {
        get
        {
            if (!Mutex.TryOpenExisting(MutexName, out var mutex))
                return false;
            mutex.Dispose();
            return true;
        }
    }

    /// <summary>Registers the autostart entry (for this app folder) and starts the helper.</summary>
    public static void Install()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            key.SetValue(RunValue, Command);
        Stop(); // an older instance may run from another folder or version
        Start();
    }

    public static void Uninstall()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            key?.DeleteValue(RunValue, throwOnMissingValue: false);
        Stop();
    }

    /// <summary>Starts the helper if it is set up but not running (e.g. after an update).</summary>
    public static void EnsureRunning()
    {
        if (!IsInstalled || IsRunning)
            return;
        // Keep the autostart entry pointing at this copy of the app.
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            key.SetValue(RunValue, Command);
        Start();
    }

    /// <summary>Asks a running helper to exit and waits briefly. Returns true if one was running.</summary>
    public static bool Stop()
    {
        if (!IsRunning)
            return false;
        if (EventWaitHandle.TryOpenExisting(StopEventName, out var stop))
        {
            using (stop)
                stop.Set();
        }
        for (var i = 0; i < 20 && IsRunning; i++)
            Thread.Sleep(100);
        return true;
    }

    private static void Start() =>
        Process.Start(new ProcessStartInfo(ExePath, Argument) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory })?.Dispose();
}
