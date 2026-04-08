using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using DMEdit.App.Controls;
using DMEdit.App.Services;
using DMEdit.Core.Buffers;
using DMEdit.Core.Documents;
using DMEdit.Core.IO;

namespace DMEdit.App;

// Window state + session restore + file watcher + OnClosing partial
// of MainWindow.  Applies the saved window size on startup, tracks
// geometry changes, runs the session restore load, wires the file
// watcher with change-kind dispatch and conflict resolution, saves
// the session on close, and owns OnClosing.
public partial class MainWindow {

    // -------------------------------------------------------------------------
    // Window state persistence
    // -------------------------------------------------------------------------

    /// <summary>
    /// Applies saved window size before layout. Called early in the
    /// constructor so the first measure pass uses the restored dimensions.
    /// </summary>
    private void RestoreWindowSize() {
        if (_settings.WindowWidth.HasValue && _settings.WindowHeight.HasValue) {
            Width = _settings.WindowWidth.Value;
            Height = _settings.WindowHeight.Value;
        }
    }

    /// <summary>
    /// Wires position, size, and state tracking. Position and maximized
    /// state are applied in the <see cref="Window.Opened"/> handler so
    /// the window is already on-screen.
    /// </summary>
    private void WireWindowState() {
        Opened += (_, _) => {
            if (_settings.WindowLeft.HasValue && _settings.WindowTop.HasValue) {
                Position = new PixelPoint(
                    _settings.WindowLeft.Value,
                    _settings.WindowTop.Value);
            }
            if (_settings.WindowMaximized) {
                WindowState = WindowState.Maximized;
            }
            _windowStateReady = true;
        };

        PositionChanged += (_, _) => TrackWindowState();
        PropertyChanged += (_, e) => {
            if (e.Property == ClientSizeProperty || e.Property == WindowStateProperty) {
                TrackWindowState();
            }
            if (e.Property == WindowStateProperty) {
                var maximized = WindowState == WindowState.Maximized;
                TabBar.IsMaximized = maximized;
                // Square corners and collapse resize border when maximized (Linux).
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
                    WindowBorder.CornerRadius = new CornerRadius(maximized ? 0 : 8);
                    WindowBorder.Padding = new Thickness(maximized ? 0 : EdgeGrip);
                    StatusBar.CornerRadius = new CornerRadius(0, 0,
                        maximized ? 0 : 6, maximized ? 0 : 6);
                }
            }
        };
    }

    // -------------------------------------------------------------------------
    // Session persistence
    // -------------------------------------------------------------------------

    /// <summary>
    /// Attempts to restore the previous session. Returns true if at least one
    /// tab was restored; false if no session exists.
    /// </summary>
    private async Task<bool> TryRestoreSessionAsync() {
        var session = await SessionStore.LoadManifestAsync();
        if (session is null || session.Value.Entries.Count == 0) {
            return false;
        }

        var (entries, activeIdx) = session.Value;
        var sessionSw = Stopwatch.StartNew();
        var loadingRemaining = new int[] { 0 };

        // Phase 1: create all tabs instantly from the manifest.
        // File-backed tabs start background scans but don't wait.
        var loadingPairs = new List<(TabState tab, SessionStore.TabEntry entry)>();
        foreach (var entry in entries) {
            var tab = SessionStore.CreateTabFromEntry(entry);
            AddTab(tab);
            if (tab.IsLoading) {
                loadingPairs.Add((tab, entry));
                loadingRemaining[0]++;
            }
        }

        // Activate the saved tab (clamped to valid range).
        var idx = Math.Clamp(activeIdx, 0, _tabs.Count - 1);
        SwitchToTab(_tabs[idx]);

        // Phase 2: finish loading each file asynchronously.
        // All scans run concurrently; smaller files naturally complete first.
        foreach (var (tab, entry) in loadingPairs) {
            WireLoadCompletion(tab, entry, sessionSw, loadingRemaining);
        }

        if (loadingRemaining[0] == 0) {
            // All tabs were non-file (untitled) — record immediately.
            sessionSw.Stop();
            Editor.PerfStats.LoadTimeMs = sessionSw.Elapsed.TotalMilliseconds;
        }

        return true;
    }

    /// <summary>
    /// Wires streaming progress for a session-restored tab and launches
    /// the async completion (conflict detection + edit replay) as
    /// fire-and-forget. The tab's spinner clears when loading finishes.
    /// When <paramref name="sessionSw"/> is provided, the Load stat is
    /// updated as the session progresses and stamped when the last tab finishes.
    /// </summary>
    private void WireLoadCompletion(TabState tab, SessionStore.TabEntry entry,
        Stopwatch? sessionSw = null, int[]? loadingRemaining = null) {
        // Wire streaming progress to update the tab bar (spinner) but
        // don't call InvalidateLayout — the PieceTable can't be queried
        // consistently until the scan finishes and InstallLineTree
        // reconciles the initial piece.  The completion handler below
        // does the first real layout.
        if (tab.Document.Table.Buffer is IProgressBuffer buf) {
            var layoutPending = 0;
            buf.ProgressChanged += () => {
                if (Interlocked.CompareExchange(ref layoutPending, 1, 0) == 0) {
                    Dispatcher.UIThread.Post(() => {
                        Interlocked.Exchange(ref layoutPending, 0);
                        UpdateTabBar();
                    }, DispatcherPriority.Background);
                }
            };
        }

        // Fire-and-forget: await the scan, then finish on the UI thread.
        _ = Task.Run(async () => {
            try {
                if (tab.LoadResult is not null) {
                    await tab.LoadResult.Loaded;
                }
            } catch {
                // Scan failed — handled below on UI thread.
            }

            await Dispatcher.UIThread.InvokeAsync(() => {
                // Tab may have been closed while loading.
                if (!_tabs.Contains(tab)) return;

                // Install the line tree from the paged buffer scan before
                // replaying edits — edit replay needs the tree for correct
                // line-tree maintenance during Insert/Delete/CaptureLineInfo.
                if (tab.LoadResult?.Buffer is PagedFileBuffer paged) {
                    var lengths = paged.TakeLineLengths();
                    if (lengths is { Count: > 0 }) {
                        tab.Document.Table.InstallLineTree(
                            CollectionsMarshal.AsSpan(lengths));
                    }
                }

                // Set char-wrap mode BEFORE edit replay so that the
                // LoadCompleted callback sees the correct mode.
                tab.CharWrapMode = ShouldCharWrap(tab);

                // Conflict detection + edit replay (Loaded already completed).
                SessionStore.FinishLoad(tab, entry);

                SnapshotFileStats(tab);
                _watcher.Watch(tab);

                UpdateTabBar();
                if (_activeTab == tab) {
                    Editor.CharWrapMode = tab.CharWrapMode;
                    Editor.IsEditBlocked = false;
                    Editor.InvalidateLayout();
                    if (tab.CharWrapMode) Dispatcher.UIThread.Post(() => {
                        Editor.InvalidateLayout();
                        Editor.ScrollCaretIntoView();
                    }, DispatcherPriority.Background);
                }

                // Session load timing: update as each tab finishes so the
                // stat shows progress, and stamp the final time when the
                // last tab completes.
                if (sessionSw != null) {
                    Editor.PerfStats.LoadTimeMs = sessionSw.Elapsed.TotalMilliseconds;
                    if (loadingRemaining != null
                        && Interlocked.Decrement(ref loadingRemaining[0]) == 0) {
                        sessionSw.Stop();
                    }
                }
            });
        });
    }

    /// <summary>
    /// Wires load completion for a file opened via File > Open, recent
    /// files, drag-drop, or the manual. Awaits the background scan, then
    /// populates BaseSha1 / LineEndingInfo and finishes loading on the
    /// UI thread.
    /// </summary>
    private void WireFileLoadCompletion(TabState tab) {
        _ = Task.Run(async () => {
            try {
                if (tab.LoadResult is not null) {
                    await tab.LoadResult.Loaded;
                }
            } catch {
                // Scan failed — tab stays in base state.
            }

            await Dispatcher.UIThread.InvokeAsync(() => {
                // Tab may have been closed while loading.
                if (!_tabs.Contains(tab)) return;

                if (tab.LoadResult is not null) {
                    tab.BaseSha1 = tab.LoadResult.BaseSha1;
                    tab.Document.LineEndingInfo = tab.LoadResult.Document.LineEndingInfo;
                }

                // Install the exact line tree built during the buffer scan.
                // This replaces the old PreBuildLineIndex post-scan rescan.
                if (tab.LoadResult?.Buffer is PagedFileBuffer paged) {
                    var lengths = paged.TakeLineLengths();
                    if (lengths is { Count: > 0 }) {
                        tab.Document.Table.InstallLineTree(
                            CollectionsMarshal.AsSpan(lengths));
                    }
                }

                SnapshotFileStats(tab);
                _watcher.Watch(tab);

                tab.CharWrapMode = ShouldCharWrap(tab);

                tab.FinishLoading();
                UpdateTabBar();
                if (_activeTab == tab) {
                    Editor.CharWrapMode = tab.CharWrapMode;
                    Editor.IsEditBlocked = false;
                    Editor.InvalidateLayout();
                    if (tab.CharWrapMode) Dispatcher.UIThread.Post(() => {
                        Editor.InvalidateLayout();
                        Editor.ScrollCaretIntoView();
                    }, DispatcherPriority.Background);
                }
            });
        });
    }

    // -----------------------------------------------------------------
    // File watching
    // -----------------------------------------------------------------

    private void WireFileWatcher() {
        _watcher.FileChanged += OnFileChanged;
    }

    /// <summary>
    /// Called on the UI thread when the file watcher detects an external
    /// change. For non-active tabs, just sets a conflict flag — the
    /// actual reload is deferred until the tab becomes active (see
    /// <see cref="SwitchToTab"/>). For the active tab with no unsaved
    /// edits, reloads immediately when AutoReload is enabled.
    /// </summary>
    private void OnFileChanged(TabState tab, FileChangeKind kind) {
        if (!_tabs.Contains(tab)) return;
        // Dirty tabs: skip if already flagged (conflict icon is already visible).
        // Clean tabs: allow re-entry so we can flash the spinner on each new change.
        if (tab.Conflict is not null && tab.IsDirty) return;

        // Active, clean, modified tab with auto-reload.
        if (tab == _activeTab
            && kind == FileChangeKind.Modified
            && !tab.IsDirty
            && _settings.AutoReloadExternalChanges) {
            // Throttle: skip if a reload is already in progress or the
            // cooldown since the last reload hasn't elapsed. Update
            // baseline stats so the watcher doesn't re-fire for this
            // change — the next external change after cooldown will
            // trigger a proper reload.
            if (tab.ReloadInProgress) {
                SnapshotFileStats(tab);
                return;
            }
            var elapsed = (DateTime.UtcNow - tab.LastReloadFinishedUtc).TotalMilliseconds;
            if (elapsed < _settings.TailReloadCooldownMs) {
                SnapshotFileStats(tab);
                return;
            }

            _ = ThrottledReloadAsync(tab, ReloadFileInPlaceAsync);
            return;
        }

        tab.Conflict = new FileConflict {
            Kind = kind == FileChangeKind.Deleted
                ? FileConflictKind.Missing
                : FileConflictKind.Changed,
            FilePath = tab.FilePath!,
            ExpectedSha1 = tab.BaseSha1,
        };

        // Clean tab with a modified file: update baseline stats so the
        // watcher can detect the next change.  Flash the spinner only when
        // auto-reload is enabled — otherwise no work happens until the user
        // switches to the tab, so the animation would be misleading.
        if (!tab.IsDirty && kind == FileChangeKind.Modified) {
            SnapshotFileStats(tab);

            if (_settings.AutoReloadExternalChanges) {
                tab.FlashReloadUntil = DateTime.UtcNow.AddMilliseconds(FLASH_RELOAD_DURATION_MS);

                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FLASH_RELOAD_DURATION_MS) };
                timer.Tick += (_, _) => {
                    timer.Stop();
                    UpdateTabBar();
                };
                timer.Start();
            }
        }

        UpdateTabBar();
    }

    // -----------------------------------------------------------------
    // Conflict resolution (error icon on tab)
    // -----------------------------------------------------------------

    /// <summary>
    /// Handles a conflict resolution choice from the tab bar context menu.
    /// </summary>
    private void HandleConflictResolution(int tabIndex, FileConflictChoice choice) {
        if (tabIndex < 0 || tabIndex >= _tabs.Count) return;
        var tab = _tabs[tabIndex];
        if (tab.Conflict is null) return;

        switch (choice) {
            case FileConflictChoice.LoadDiskVersion:
                // User already confirmed — clear dirty so the reload
                // doesn't abort at the "user started editing" guard.
                tab.IsDirty = false;
                tab.Conflict = null;
                _ = ReloadFileInPlaceAsync(tab);
                break;
            case FileConflictChoice.KeepMyVersion:
                tab.Conflict = null;
                UpdateTabBar();
                break;
            case FileConflictChoice.Discard:
                CloseTabDirect(tab);
                break;
        }
    }

    /// <summary>
    /// Saves the current session (open tabs, edit history, scroll/caret state)
    /// so it can be restored on next launch.
    /// </summary>
    public void SaveSession() {
        // Save scroll state for the active tab before serializing.
        if (_activeTab is { IsSettings: false }) {
            Editor.SaveScrollState(_activeTab);
        }

        var activeIdx = _activeTab is not null ? _tabs.IndexOf(_activeTab) : 0;
        SessionStore.Save(_tabs, Math.Max(0, activeIdx));
    }

    protected override void OnClosing(WindowClosingEventArgs e) {
        // Flush pending compound edits on ALL tabs so undo stacks are
        // complete before the final session write.
        Editor.FlushCompound();
        foreach (var tab in _tabs) {
            if (!tab.IsSettings) {
                tab.Document.EndCompound();
            }
        }

        _watcher.Dispose();
        SaveSession();
        base.OnClosing(e);
    }

    /// <summary>
    /// Records the current window geometry. Only the unmaximized size and
    /// position are saved; maximized/minimized bounds are ignored so the
    /// normal position is preserved for later restore.
    /// </summary>
    private void TrackWindowState() {
        if (!_windowStateReady) return;

        _settings.WindowMaximized = WindowState == WindowState.Maximized;

        if (WindowState == WindowState.Normal) {
            _settings.WindowWidth = ClientSize.Width;
            _settings.WindowHeight = ClientSize.Height;
            _settings.WindowLeft = Position.X;
            _settings.WindowTop = Position.Y;
        }

        _settings.ScheduleSave();
    }

}
