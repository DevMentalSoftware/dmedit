using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using DevMentalMd.App.Services;
using System;
using System.Text;
using System.Diagnostics;
using System.Threading;
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

    /// <summary>
    /// Guards against re-entrant calls to <see cref="HandleFatalException"/>.
    /// A single exception can surface through multiple handlers (e.g.
    /// dispatcher + unobserved task); only the first one shows a dialog.
    /// </summary>
    private static int _handlingFatal;

    private static void HandleFatalException(Exception ex, string operation) {
        // Only the first caller gets to show the dialog. Subsequent
        // exceptions while the dialog is up are logged but ignored.
        if (Interlocked.Exchange(ref _handlingFatal, 1) != 0) {
            // Already handling a fatal error — log to stderr only.
            // Don't write additional crash report files for the same
            // cascading failure — the first report has the root cause.
            Console.Error.WriteLine($"FATAL (suppressed): {operation}");
            Console.Error.WriteLine(ex);
            return;
        }

        // Always write a crash report to disk — this succeeds even if the
        // UI is in a broken state.
        var reportPath = CrashReport.Write(ex, operation);

        try {
            // Try to show the error dialog on the UI thread.
            if (Application.Current?.ApplicationLifetime
                    is IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow }) {

                // Avalonia's ShowDialog is async — it needs the dispatcher
                // to pump messages. We must never block the UI thread with
                // .GetAwaiter().GetResult() or the dialog will deadlock.
                // All Avalonia control creation must happen on the UI thread.
                ErrorDialog CreateDialog() {
                    var devMode = (mainWindow as MainWindow)?.Settings.DevMode ?? false;
                    ErrorDialogButton[] buttons = Debugger.IsAttached
                        ? [ErrorDialogButton.Exit, ErrorDialogButton.Continue]
                        : [ErrorDialogButton.Exit];
                    return new ErrorDialog(
                        "Unexpected Error",
                        ex.Message,
                        buttons,
                        crashReportPath: reportPath,
                        stackTrace: ex.ToString(),
                        devMode: devMode);
                }

                if (Dispatcher.UIThread.CheckAccess()) {
                    // UI thread — ShowDialog works normally.
                    var dialog = CreateDialog();
                    _ = ShowDialogThenAsync(dialog, mainWindow, () => HandleDialogResult(dialog));
                } else {
                    // Background thread — Avalonia's ShowDialog modal
                    // loop doesn't receive input when opened from a
                    // Post callback (no Win32 user-interaction context).
                    // Work around with Show() + manual modality.
                    var done = new ManualResetEventSlim();
                    Dispatcher.UIThread.Post(() => {
                        var dialog = CreateDialog();
                        dialog.Topmost = true;
                        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
                        // Center on mainWindow after layout so
                        // SizeToContent height is finalized.
                        dialog.Opened += (_, _) => {
                            Dispatcher.UIThread.Post(() => {
                                var ownerPos = mainWindow.Position;
                                var ownerBounds = mainWindow.Bounds;
                                var x = ownerPos.X + (ownerBounds.Width - dialog.Bounds.Width) / 2;
                                var y = ownerPos.Y + (ownerBounds.Height - dialog.Bounds.Height) / 2;
                                dialog.Position = new PixelPoint((int)x, (int)y);
                            }, DispatcherPriority.Loaded);
                        };
                        mainWindow.IsEnabled = false;
                        dialog.Closed += (_, _) => {
                            mainWindow.IsEnabled = true;
                            HandleDialogResult(dialog);
                            done.Set();
                        };
                        dialog.Show();
                    });
                    done.Wait();
                }
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

    private static void HandleDialogResult(ErrorDialog dialog) {
        if (dialog.Result == ErrorDialogButton.Continue) {
            Interlocked.Exchange(ref _handlingFatal, 0);
            return;
        }
        Process.GetCurrentProcess().Kill();
    }

    private static async Task ShowDialogThenAsync(
        ErrorDialog dialog, Window owner, Action onClosed) {
        await dialog.ShowDialog(owner);
        onClosed();
    }
}
