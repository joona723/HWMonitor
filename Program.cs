namespace HWMonitor;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        Application.ThreadException += (_, e) => ShowUnhandled(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ShowUnhandled(e.ExceptionObject as Exception);

        Application.Run(new TrayApplicationContext());
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
