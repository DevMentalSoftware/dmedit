using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(DevMentalMd.Rendering.Tests.TestApp))]

namespace DevMentalMd.Rendering.Tests;

/// <summary>Minimal Avalonia application used by headless xUnit tests.</summary>
public sealed class TestApp : Application {
    public static AppBuilder BuildAvaloniaApp() {
        return AppBuilder.Configure<TestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}
