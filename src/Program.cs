using System.Diagnostics;
using System.Windows.Forms;

namespace DeviceBattery;

internal static class Program
{
    private const string MutexName = @"Local\DeviceBatteryTray_7C1F";
    private const string QuitEventName = @"Local\DeviceBatteryQuit_7C1F";

    [STAThread]
    private static void Main(string[] args)
    {
        // "DeviceBattery.exe --quit": ask the running copy to exit and wait for it (used by the uninstaller / upgrades).
        if (args.Any(a => a.Equals("--quit", StringComparison.OrdinalIgnoreCase)))
        {
            QuitRunningInstance();
            return;
        }

        // Single instance per user session.
        using var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
            return;

        Application.ThreadException += (_, e) => Log.Error("UI thread", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Unhandled: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("Task", e.Exception); e.SetObserved(); };

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var context = new TrayApplicationContext();

        using var quitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, QuitEventName);
        quitEvent.Reset();
        var wait = ThreadPool.RegisterWaitForSingleObject(quitEvent, (_, _) => context.PostQuit(), null, Timeout.Infinite, executeOnlyOnce: true);

        Application.Run(context);
        wait.Unregister(null);
    }

    private static void QuitRunningInstance()
    {
        if (!EventWaitHandle.TryOpenExisting(QuitEventName, out var quitEvent))
            return; // nothing running
        using (quitEvent)
            quitEvent.Set();

        // Give it a moment to disappear so files can be replaced / deleted right after we return.
        int self = Environment.ProcessId;
        for (int i = 0; i < 50; i++)
        {
            bool others = Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName).Any(p => p.Id != self);
            if (!others) return;
            Thread.Sleep(100);
        }
    }
}
