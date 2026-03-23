using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using DevMentalMd.App.Services;
using System;
using System.Text;
using System.Threading.Tasks;

namespace DevMentalMd.App;

class Program {
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) {
        // Register Windows-1252 and other legacy code page encodings.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        } catch (Exception ex) {
            HandleFatalException(ex, "Application startup");
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .AfterSetup(_ => {
                // Install Avalonia dispatcher exception handler once the
                // framework is initialized.
                Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
            });

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e) {
        if (e.ExceptionObject is Exception ex) {
            HandleFatalException(ex, "Unhandled exception");
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) {
        e.SetObserved();
        HandleFatalException(e.Exception, "Unobserved task exception");
    }

    private static void OnDispatcherUnhandledException(
        object? sender, DispatcherUnhandledExceptionEventArgs e) {
        e.Handled = true;
        HandleFatalException(e.Exception, "UI thread exception");
    }

    private static void HandleFatalException(Exception ex, string operation) {
        // Always write a crash report to disk — this succeeds even if the
        // UI is in a broken state.
        var reportPath = CrashReport.Write(ex, operation);

        try {
            // Try to show the error dialog on the UI thread.
            if (Application.Current?.ApplicationLifetime
                    is IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow }) {
                Dispatcher.UIThread.Invoke(() => {
                    var devMode = false;
                    if (mainWindow is MainWindow mw) {
                        // Flush session before showing the dialog so edits aren't lost
                        // if the user closes the app after dismissing.
                        try { mw.SaveSession(); } catch { }
                        devMode = mw.Settings.DevMode;
                    }

                    var dialog = new ErrorDialog(
                        "Unexpected Error",
                        "An unexpected error occurred.",
                        ex.Message,
                        [ErrorDialogButton.OK],
                        crashReportPath: reportPath,
                        stackTrace: ex.ToString(),
                        devMode: devMode);
                    dialog.ShowDialog(mainWindow).GetAwaiter().GetResult();
                });
                return;
            }
        } catch {
            // UI is dead — fall through to console output.
        }

        // Last resort: write to stderr so there's some trace.
        Console.Error.WriteLine($"FATAL: {operation}");
        Console.Error.WriteLine(ex);
        if (reportPath is not null) {
            Console.Error.WriteLine($"Crash report: {reportPath}");
        }
    }
}
