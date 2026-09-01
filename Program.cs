using System.Threading;

namespace HWMonitor;

static class Program
{
    private const string SingleInstanceMutexName = "HWMonitor-SingleInstance-9F3D2C11-4E7B-4C9E-9C3A-6E9B7E2C1234";
    private const string ShowWindowEventName = "HWMonitor-ShowWindow-9F3D2C11-4E7B-4C9E-9C3A-6E9B7E2C1234";

    [STAThread]
    static void Main()
    {
        using var singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            // Already running: ask the existing instance to pop its window forward, then exit
            // instead of starting a second process (which would fight over admin-only sensors,
            // tray icons, and the same ETW/network-monitoring session).
            try
            {
                using var showEvent = EventWaitHandle.OpenExisting(ShowWindowEventName);
                showEvent.Set();
            }
            catch
            {
                // The other instance hasn't finished starting up yet; nothing more we can do.
            }
            return;
        }

        ApplicationConfiguration.Initialize();

        Application.ThreadException += (_, e) => ShowUnhandled(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ShowUnhandled(e.ExceptionObject as Exception);

        var trayContext = new TrayApplicationContext();

        using var showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        var watcherThread = new Thread(() =>
        {
            while (showWindowEvent.WaitOne())
            {
                trayContext.RequestShowMainWindow();
            }
        })
        {
            IsBackground = true,
            Name = "HWMonitor-SingleInstanceWatcher",
        };
        watcherThread.Start();

        Application.Run(trayContext);
    }

    private static void ShowUnhandled(Exception? ex)
    {
        string text = ex?.ToString() ?? "Unknown error";
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HWMonitor");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"), $"[{DateTime.Now}]{Environment.NewLine}{text}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Best-effort logging; still show the dialog below regardless.
        }

        MessageBox.Show(text, "HWMonitor - Unhandled Exception", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
