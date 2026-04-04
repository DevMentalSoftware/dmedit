using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace DMEdit.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow { StartupFiles = desktop.Args };
            desktop.MainWindow = mainWindow;

            // Start listening for file-open requests from secondary instances
            // (e.g. Explorer context menu with multiple files selected).
            var svc = Program.SingleInstance;
            if (svc is not null) {
                svc.FileRequested += path =>
                    Dispatcher.UIThread.Post(() => mainWindow.OpenFileFromIpc(path));
                svc.StartListening();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
