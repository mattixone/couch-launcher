using System.Windows;
using Velopack;

namespace CouchLauncher;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Must run first: during install/update/uninstall Velopack launches the
        // app with special arguments, handles them here, and exits.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => LoginStartup.Remove())
            .Run();

        Paths.Ensure();
        Native.UseSystemDpiAwareness();

        // One launcher at a time. A second copy (say, the Start menu shortcut
        // clicked while it's already running) just pokes the first one back
        // to the home screen and quits.
        using var mutex = new Mutex(true, @"Local\CouchLauncher.Instance", out bool first);
        using var wake = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CouchLauncher.ShowHome");
        if (!first)
        {
            wake.Set();
            return;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        // A launcher that dies leaves you staring at the desktop from the
        // couch, so a bug in one screen gets logged rather than fatal.
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Write("unhandled (kept running): " + e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("fatal: " + e.ExceptionObject);

        var window = new Ui.MainWindow();
        new Thread(() =>
        {
            while (wake.WaitOne()) window.Dispatcher.InvokeAsync(window.ShowHome);
        }) { IsBackground = true }.Start();

        app.Run(window);
    }
}
