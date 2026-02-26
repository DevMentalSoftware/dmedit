using System;

namespace DevMentalMd.App.Services;

/// <summary>
/// Controls whether developer-mode features (e.g. procedural sample files in the
/// Recent menu) are visible. Active in DEBUG builds, or at runtime when the
/// environment variable <c>DEVMENTALMD_DEV=1</c> is set.
/// </summary>
public static class DevMode {
    public static bool IsEnabled { get; } = CheckEnabled();

    private static bool CheckEnabled() {
#if DEBUG
        return true;
#else
        return Environment.GetEnvironmentVariable("DEVMENTALMD_DEV") == "1";
#endif
    }
}
