using System.Windows;
using System.Windows.Threading;

namespace ScanKiosk;

public partial class App : Application
{
    Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args.Select(a => a.ToLowerInvariant()).ToList();
        DispatcherUnhandledException += OnUnhandled;

        if (args.Contains("--install") || args.Contains("--uninstall"))
        {
            // Start (or stop starting) the kiosk at logon for the current user.
            using var run = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (args.Contains("--install")) run.SetValue("ScanKiosk", $"\"{Environment.ProcessPath}\"");
            else run.DeleteValue("ScanKiosk", throwOnMissingValue: false);
            MessageBox.Show(args.Contains("--install")
                ? "Scan Kiosk will start automatically when you sign in."
                : "Scan Kiosk will no longer start automatically.", "Scan Kiosk");
            Shutdown();
            return;
        }

        int snap = args.IndexOf("--snapshot");
        if (snap >= 0)
        {
            // Renders each screen to PNG for layout checks: --snapshot <outdir> [sample.pdf]
            string dir = e.Args.ElementAtOrDefault(snap + 1) ?? ".";
            string? pdf = e.Args.ElementAtOrDefault(snap + 2);
            var w = new MainWindow(KioskConfig.Load(), windowed: true, buttonEnabled: false);
            _ = w.SnapshotAsync(dir, pdf).ContinueWith(_ => Dispatcher.Invoke(Shutdown));
            return;
        }

        _single = new Mutex(true, @"Local\ScanKiosk", out bool first);
        if (!first)
        {
            Log.Write("Another ScanKiosk instance is already running; exiting.");
            Shutdown();
            return;
        }
        var cfg = KioskConfig.Load();
        bool windowed = args.Contains("--windowed") || !cfg.FullScreen;
        bool button = cfg.ScanButtonEnabled && !args.Contains("--no-button");
        Log.Write($"Started (windowed={windowed}, scan button={button})");
        var main = new MainWindow(cfg, windowed, button);
        MainWindow = main;
        main.Show();
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write("Unhandled: " + e.Exception);
        e.Handled = true;  // a kiosk should keep running
        (MainWindow as MainWindow)?.ShowError("Something went wrong", e.Exception.Message);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Write("Exiting.");
        _single?.Dispose();
        base.OnExit(e);
    }
}
