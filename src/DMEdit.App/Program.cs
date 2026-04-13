using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using DMEdit.App.Services;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace DMEdit.App;

class Program {
    /// <summary>
    /// The single-instance service for the owning process. Accessed by
    /// <see cref="App"/> to start listening and wire up file-open events.
    /// </summary>
    internal static SingleInstanceService? SingleInstance { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) {
        // Velopack bootstrap — must be first, before any UI or framework init.
        var vpk = VelopackApp.Build()
            .OnFirstRun(v => RegisterShellIntegration());
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
            vpk.OnAfterInstallFastCallback(v => RegisterShellIntegration())
               .OnBeforeUninstallFastCallback(v => UnregisterShellIntegration());
        }
        vpk.Run();

        // Register Windows-1252 and other legacy code page encodings.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // Single-instance: if another DMEdit is already running, hand off
        // our file argument and exit so all files open in one window.
        using var singleInstance = new SingleInstanceService();
        if (!singleInstance.IsOwner) {
            if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])) {
                SingleInstanceService.SendToOwner(Path.GetFullPath(args[0]));
            }
            return;
        }
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        SingleInstance = singleInstance;

        // Register the WPF assembly resolver before anything touches WPF
        // types (printing, jump lists).  WPF is not bundled — it comes
        // from the installed Windows Desktop runtime.
        WpfResolver.Register();

        // Force jump list discovery early so the AppUserModelID is set
        // before Avalonia creates the main window.
        _ = JumpListDiscovery.Service;

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

    // -----------------------------------------------------------------
    // Shell integration (context menu on Windows, .desktop on Linux)
    // -----------------------------------------------------------------

    private static void RegisterShellIntegration() {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            RegisterWindowsContextMenu();
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            RegisterLinuxDesktopEntry();
    }

    private static void UnregisterShellIntegration() {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            UnregisterWindowsContextMenu();
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            UnregisterLinuxDesktopEntry();
    }

    // -- Windows: Explorer context menu --

    private const string ShellRegKey = @"Software\Classes\*\shell\DMEdit";

    [SupportedOSPlatform("windows")]
    private static void RegisterWindowsContextMenu() {
        try {
            var exe = Environment.ProcessPath;
            if (exe is null) return;

            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(ShellRegKey);
            key.SetValue(null, "DMEdit");
            key.SetValue("Icon", $"\"{exe}\",0");

            using var cmd = key.CreateSubKey("command");
            cmd.SetValue(null, $"\"{exe}\" \"%1\"");
        } catch {
            // Best-effort — non-fatal.
        }
    }

    [SupportedOSPlatform("windows")]
    private static void UnregisterWindowsContextMenu() {
        try {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(ShellRegKey, throwOnMissingSubKey: false);
        } catch {
            // Best-effort — non-fatal.
        }
    }

    // -- Linux: .desktop file for app launchers and "Open With" --

    private static readonly string DesktopFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "applications", "dmedit.desktop");

    private static void RegisterLinuxDesktopEntry() {
        try {
            var exe = Environment.ProcessPath;
            if (exe is null) return;

            var dir = Path.GetDirectoryName(DesktopFilePath)!;
            Directory.CreateDirectory(dir);

            File.WriteAllText(DesktopFilePath,
                $"""
                [Desktop Entry]
                Type=Application
                Name=DMEdit
                Comment=Text editor for simple and large files
                Exec="{exe}" %F
                Icon=dmedit
                Terminal=false
                Categories=Utility;TextEditor;
                MimeType=text/plain;text/markdown;
                """);
        } catch {
            // Best-effort — non-fatal.
        }
    }

    private static void UnregisterLinuxDesktopEntry() {
        try {
            if (File.Exists(DesktopFilePath))
                File.Delete(DesktopFilePath);
        } catch {
            // Best-effort — non-fatal.
        }
    }
}
