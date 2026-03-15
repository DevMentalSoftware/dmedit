using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Threading;

namespace DevMentalMd.App.Services;

/// <summary>
/// Describes why a watched file was flagged by the file watcher.
/// </summary>
public enum FileChangeKind {
    Modified,
    Deleted,
}

/// <summary>
/// Monitors open files for external modifications or deletions. Uses one
/// <see cref="FileSystemWatcher"/> per watched directory and verifies
/// changes via SHA-1 comparison to filter out metadata-only or duplicate
/// events.
/// </summary>
public sealed class FileWatcherService : IDisposable {
    private readonly record struct WatchedFile(TabState Tab, string FilePath);

    /// <summary>
    /// Fired on the UI thread when a watched file has genuinely changed
    /// (SHA-1 mismatch) or been deleted.
    /// </summary>
    public event Action<TabState, FileChangeKind>? FileChanged;

    // Directory → watcher. One watcher per directory, shared by all files
    // in that directory.
    // Case-insensitive on Windows/macOS, case-sensitive on Linux.
    private static readonly StringComparer PathComparer =
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;

    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(PathComparer);
    private readonly Dictionary<string, List<TabState>> _fileToTabs = new(PathComparer);
    private readonly ConcurrentDictionary<string, Timer> _debounceTimers = new(PathComparer);

    private const int DebounceMs = 500;
    private bool _disposed;

    /// <summary>
    /// Begin watching <paramref name="tab"/>'s file for external changes.
    /// </summary>
    public void Watch(TabState tab) {
        if (_disposed) return;
        if (tab.FilePath is not { } path) return;
        // Skip network paths — FSW is unreliable on UNC/mapped drives.
        // The window-activation recheck covers those files instead.
        if (!RecentFilesStore.IsLocalPath(path)) return;

        var dir = Path.GetDirectoryName(path);
        if (dir is null) return;

        // Track tab → file mapping.
        if (!_fileToTabs.TryGetValue(path, out var tabs)) {
            tabs = [];
            _fileToTabs[path] = tabs;
        }
        if (!tabs.Contains(tab)) {
            tabs.Add(tab);
        }

        // Ensure a watcher exists for the directory.
        if (!_watchers.ContainsKey(dir)) {
            try {
                var watcher = new FileSystemWatcher(dir) {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                    IncludeSubdirectories = false,
                };
                watcher.Changed += OnFswEvent;
                watcher.Deleted += OnFswEvent;
                watcher.Renamed += OnFswRenamed;
                _watchers[dir] = watcher;
            } catch (Exception) {
                // Directory may not exist or be inaccessible (e.g. network drive).
                // Silently degrade — no watching for this directory.
            }
        }
    }

    /// <summary>
    /// Stop watching <paramref name="tab"/>'s file.
    /// </summary>
    public void Unwatch(TabState tab) {
        if (tab.FilePath is not { } path) return;

        if (_fileToTabs.TryGetValue(path, out var tabs)) {
            tabs.Remove(tab);
            if (tabs.Count == 0) {
                _fileToTabs.Remove(path);
                CancelDebounce(path);

                // Clean up the directory watcher if no files remain in that dir.
                var dir = Path.GetDirectoryName(path);
                if (dir is not null && !HasFilesInDir(dir)) {
                    RemoveWatcher(dir);
                }
            }
        }
    }

    /// <summary>
    /// Stop all watching and release all resources.
    /// </summary>
    public void UnwatchAll() {
        foreach (var timer in _debounceTimers.Values) {
            timer.Dispose();
        }
        _debounceTimers.Clear();

        foreach (var watcher in _watchers.Values) {
            watcher.Dispose();
        }
        _watchers.Clear();
        _fileToTabs.Clear();
    }

    /// <summary>
    /// Manually trigger a recheck of <paramref name="tab"/>'s file.
    /// Useful on window activation to catch changes FSW may have missed
    /// (e.g. network drives where FSW is not installed).
    /// </summary>
    public void Recheck(TabState tab) {
        if (_disposed) return;
        if (tab.FilePath is not { } path) return;
        // Skip tabs that are still loading or haven't had their file
        // stats snapshotted yet — comparing against defaults would
        // produce a false conflict on every session restore.
        if (tab.IsLoading || tab.BaseLastWriteTimeUtc == default) return;

        ThreadPool.QueueUserWorkItem(_ => {
            if (_disposed) return;

            if (!File.Exists(path)) {
                Dispatcher.UIThread.Post(() => FileChanged?.Invoke(tab, FileChangeKind.Deleted));
                return;
            }

            try {
                var info = new FileInfo(path);
                var writeTime = info.LastWriteTimeUtc;
                var fileSize = info.Length;
                if (tab.BaseLastWriteTimeUtc != writeTime || tab.BaseFileSize != fileSize) {
                    Dispatcher.UIThread.Post(() => FileChanged?.Invoke(tab, FileChangeKind.Modified));
                }
            } catch {
                // File inaccessible — skip.
            }
        });
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        UnwatchAll();
    }

    // ---------------------------------------------------------------
    // FSW event handlers
    // ---------------------------------------------------------------

    private void OnFswEvent(object sender, FileSystemEventArgs e) {
        ScheduleVerification(e.FullPath);
    }

    private void OnFswRenamed(object sender, RenamedEventArgs e) {
        // If the old name was watched, the file has effectively been deleted.
        ScheduleVerification(e.OldFullPath);
    }

    /// <summary>
    /// Debounce: reset/start a timer for the given file path. When the
    /// timer fires, we'll compute the SHA-1 on a thread-pool thread and
    /// compare to the tab's <see cref="TabState.BaseSha1"/>.
    /// </summary>
    private void ScheduleVerification(string fullPath) {
        if (_disposed) return;
        if (!_fileToTabs.ContainsKey(fullPath)) return;

        _debounceTimers.AddOrUpdate(
            fullPath,
            _ => new Timer(OnDebounceElapsed, fullPath, DebounceMs, Timeout.Infinite),
            (_, existing) => {
                existing.Change(DebounceMs, Timeout.Infinite);
                return existing;
            });
    }

    private void OnDebounceElapsed(object? state) {
        if (state is string path) {
            VerifyFile(path);
        }
    }

    /// <summary>
    /// Checks whether the file has changed by comparing last-write time
    /// and size against the tab's stored baseline. This is much cheaper
    /// than computing SHA-1 on every FSW event. If either differs, we
    /// treat the file as modified.
    /// </summary>
    private void VerifyFile(string path) {
        if (_disposed) return;
        if (!_fileToTabs.TryGetValue(path, out var tabs) || tabs.Count == 0) return;

        if (!File.Exists(path)) {
            Dispatcher.UIThread.Post(() => {
                if (!_fileToTabs.TryGetValue(path, out var currentTabs)) return;
                foreach (var tab in currentTabs.ToArray()) {
                    FileChanged?.Invoke(tab, FileChangeKind.Deleted);
                }
            });
            return;
        }

        DateTime writeTime;
        long fileSize;
        try {
            var info = new FileInfo(path);
            writeTime = info.LastWriteTimeUtc;
            fileSize = info.Length;
        } catch {
            // File locked or inaccessible — skip this check.
            return;
        }

        Dispatcher.UIThread.Post(() => {
            if (!_fileToTabs.TryGetValue(path, out var currentTabs)) return;
            foreach (var tab in currentTabs.ToArray()) {
                // Skip tabs still loading — stats haven't been snapshotted yet.
                if (tab.IsLoading || tab.BaseLastWriteTimeUtc == default) continue;
                if (tab.BaseLastWriteTimeUtc != writeTime || tab.BaseFileSize != fileSize) {
                    FileChanged?.Invoke(tab, FileChangeKind.Modified);
                }
            }
        });
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private bool HasFilesInDir(string dir) {
        foreach (var path in _fileToTabs.Keys) {
            if (PathComparer.Equals(Path.GetDirectoryName(path), dir)) {
                return true;
            }
        }
        return false;
    }

    private void RemoveWatcher(string dir) {
        if (_watchers.TryGetValue(dir, out var watcher)) {
            _watchers.Remove(dir);
            watcher.Dispose();
        }
    }

    private void CancelDebounce(string path) {
        if (_debounceTimers.TryRemove(path, out var timer)) {
            timer.Dispose();
        }
    }
}
