using System;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace DMEdit.App.Services;

/// <summary>
/// Manages automatic update checks via Velopack and GitHub Releases.
/// All methods are safe to call in dev/debug — they no-op when the app
/// is not installed through a Velopack installer.
/// </summary>
sealed class UpdateService {
    private const string RepoUrl = "https://github.com/DevMentalSoftware/dmedit";

    private readonly UpdateManager _mgr = new(new GithubSource(RepoUrl, null, false));
    private UpdateInfo? _pendingUpdate;
    private bool _downloaded;

    /// <summary>Version string of the available update, or null if none.</summary>
    public string? AvailableVersion => _pendingUpdate?.TargetFullRelease.Version.ToString();

    /// <summary>True when the update has been downloaded and is ready to apply.</summary>
    public bool IsReadyToApply => _downloaded;

    /// <summary>Raised when a new version is discovered (not yet downloaded).</summary>
    public event Action<string>? UpdateAvailable;

    /// <summary>Raised when the update has been downloaded and is ready to apply.</summary>
    public event Action? UpdateReady;

    /// <summary>
    /// Checks for a newer version. If <paramref name="autoDownload"/> is true,
    /// also downloads the update silently. Raises <see cref="UpdateAvailable"/>
    /// when a new version is found and <see cref="UpdateReady"/> after download.
    /// </summary>
    public async Task CheckAsync(bool autoDownload) {
        if (!_mgr.IsInstalled) return;

        var update = await _mgr.CheckForUpdatesAsync();
        if (update is null) return;

        _pendingUpdate = update;
        var version = update.TargetFullRelease.Version.ToString();
        UpdateAvailable?.Invoke(version);

        if (autoDownload) {
            await _mgr.DownloadUpdatesAsync(update);
            _downloaded = true;
            UpdateReady?.Invoke();
        }
    }

    /// <summary>
    /// Downloads the previously discovered update. Call after the user
    /// clicks the "Update to …" button when auto-download is off.
    /// </summary>
    public async Task DownloadAsync() {
        if (_pendingUpdate is null) return;
        await _mgr.DownloadUpdatesAsync(_pendingUpdate);
        _downloaded = true;
        UpdateReady?.Invoke();
    }

    /// <summary>
    /// Applies the previously downloaded update and restarts the application.
    /// </summary>
    public void ApplyAndRestart() {
        if (_pendingUpdate is null || !_downloaded) return;
        _mgr.ApplyUpdatesAndRestart(_pendingUpdate);
    }
}
