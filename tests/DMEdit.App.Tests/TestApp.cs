using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(DMEdit.App.Tests.TestApp))]

namespace DMEdit.App.Tests;

/// <summary>Minimal Avalonia application used by headless xUnit tests.</summary>
public sealed class TestApp : Application {
    public static AppBuilder BuildAvaloniaApp() {
        return AppBuilder.Configure<TestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}
