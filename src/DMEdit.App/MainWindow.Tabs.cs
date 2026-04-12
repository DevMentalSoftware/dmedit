using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DMEdit.App.Commands;
using Cmd = DMEdit.App.Commands.Commands;
using DMEdit.App.Services;
using DMEdit.Core.Documents;

namespace DMEdit.App;

// Tab management partial of MainWindow.  Owns InitSessionAsync,
// AddTab / SwitchToTab / CloseTabDirect / PromptAndCloseTabAsync,
// close-all / close-others, WireTabBar / ShowOverflowMenu /
// UpdateTabBar, context-menu actions (reveal, read-only toggle,
// close-to-right, close-other), and tab reorder.
public partial class MainWindow {


    private async void InitSessionAsync() {
        if (!await TryRestoreSessionAsync()) {
            var tab = AddTab(TabState.CreateUntitled(_tabs));
            SwitchToTab(tab);
        }

        // Open any files passed on the command line (e.g. Explorer context
        // menu) as additional tabs, after session restore.
        var files = _startupFiles;
        if (files is { Length: > 0 }) {
            foreach (var path in files) {
                if (File.Exists(path)) {
                    await OpenFileInTabAsync(Path.GetFullPath(path));
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    // Tab management
    // -------------------------------------------------------------------------

    private TabState AddTab(TabState tab) {
        _tabs.Add(tab);
        // New untitled documents use the default indent style from settings.
        if (tab.FilePath == null && !tab.IsSettings) {
            tab.Document.IndentInfo = new Core.Documents.IndentInfo(
                _settings.DefaultIndentStyle, false);
        }
        tab.Document.Changed += (_, _) => OnTabDocumentChanged(tab);
        UpdateTabBar();
        return tab;
    }

    private void SwitchToTab(TabState tab) {
        if (_activeTab == tab) return;
        CancelChord();
        Editor.ExitIncrementalSearch();
        // Flush any pending compound edit and save scroll state on the outgoing tab.
        if (_activeTab != null && !_activeTab.IsSettings) {
            Editor.FlushCompound();
            Editor.SaveScrollState(_activeTab);
        }
        _activeTab = tab;

        var isSettings = tab.IsSettings;
        EditorGrid.IsVisible = !isSettings;
        MenuBarBorder.IsVisible = !isSettings;
        SettingsPanel.IsVisible = isSettings;
        UpdateStatusBarVisibility();

        if (!isSettings) {
            // When a tab has pending session edits, don't show the base file
            // content — it would flash the pre-edit state. Assign the real
            // document only after edits are replayed in FinishLoad.
            if (!tab.HasPendingEdits) {
                Editor.Document = tab.Document;
                // Defer scroll restore until load completes so we don't
                // set _scrollOffset to a mid-document position during
                // streaming — that triggers expensive O(N) lookups.
                if (!tab.IsLoading) {
                    Editor.RestoreScrollState(tab);
                }
            } else {
                Editor.Document = null;
            }

            Editor.IsEditBlocked = tab.IsLoading || tab.IsReadOnly || tab.IsLocked;
            Editor.CharWrapMode = tab.CharWrapMode;
            Editor.Focus();

            // When a loading tab finishes, unblock the editor if it's
            // still the active tab — unless the tab is readonly or locked.
            if (tab.IsLoading) {
                tab.LoadCompleted += () => {
                    if (_activeTab == tab) {
                        Editor.CharWrapMode = tab.CharWrapMode;
                        Editor.Document = tab.Document;
                        Editor.RestoreScrollState(tab);
                        Editor.IsEditBlocked = tab.IsReadOnly || tab.IsLocked;
                        Editor.ResetCaretBlink();
                        Editor.InvalidateLayout();
                        if (tab.CharWrapMode) Dispatcher.UIThread.Post(() => {
                        Editor.InvalidateLayout();
                        Editor.ScrollCaretIntoView();
                    }, DispatcherPriority.Background);
                    }
                };
            }
        }
        // Show find bar only if this tab owns it.  Clear the search text
        // when switching away so the old term doesn't trigger an expensive
        // search against a new (potentially large) document.
        if (tab != _findBarTab) {
            FindBar.SetSearchTerm("");
        }
        FindBar.IsVisible = (tab == _findBarTab);
        UpdateTabBar();
        UpdateStatusBar();
        Toolbar.Refresh();

        // If the tab has a conflict but no unsaved edits, silently
        // reload the disk version now that the user is looking at it.
        if (tab.Conflict is { Kind: FileConflictKind.Changed } && !tab.IsDirty) {
            _ = ReloadFileInPlaceAsync(tab);
        }
    }

    /// <summary>
    /// Closes the tab without prompting. Use <see cref="PromptAndCloseTabAsync"/>
    /// for user-facing close operations on dirty tabs.
    /// </summary>
    private void CloseTabDirect(TabState tab) {
        _watcher.Unwatch(tab);
        SessionStore.DeleteTabFiles(tab.Id);
        if (tab.IsSettings) {
            _settingsTab = null;
            SettingsPanel.ResetState();
        }
        if (tab == _findBarTab) {
            _findBarTab = null;
        }
        // If this is the last document tab and it's an unedited untitled,
        // close the window instead of spawning a replacement.
        var isLastDoc = !_tabs.Any(t => t != tab && !t.IsSettings);
        if (isLastDoc && tab.FilePath == null && !tab.IsDirty) {
            _tabs.Remove(tab);
            Close();
            return;
        }

        var closedIdx = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        var hasDocumentTab = _tabs.Any(t => !t.IsSettings);
        if (!hasDocumentTab) {
            var newTab = AddTab(TabState.CreateUntitled(_tabs));
            SwitchToTab(newTab);
        } else if (_activeTab == tab) {
            // Switch to the tab now at the closed index (= old right neighbor),
            // or the last tab if the closed tab was rightmost.
            var newIdx = Math.Min(closedIdx, _tabs.Count - 1);
            SwitchToTab(_tabs[newIdx]);
        } else {
            UpdateTabBar();
        }
    }

    /// <summary>
    /// Prompts the user if <paramref name="tab"/> has unsaved changes,
    /// then closes it. Returns false if the user cancelled.
    /// </summary>
    private async Task<bool> PromptAndCloseTabAsync(TabState tab) {
        if (tab.IsSettings) {
            CloseTabDirect(tab);
            return true;
        }
        if (tab.IsDirty) {
            var dialog = new SaveChangesDialog(tab.DisplayName, _theme);
            await dialog.ShowDialog(this);
            switch (dialog.Result.Choice) {
                case SaveChoice.Save:
                    if (!await SaveTabAsync(tab)) {
                        return false; // Save was cancelled (e.g. user dismissed Save As)
                    }
                    break;
                case SaveChoice.DontSave:
                    break;
                case SaveChoice.Cancel:
                    return false;
            }
        }
        CloseTabDirect(tab);
        return true;
    }

    /// <summary>
    /// Prompts the user about multiple dirty tabs, then closes all tabs.
    /// Returns false if the user cancelled.
    /// </summary>
    private async Task<bool> PromptAndCloseMultipleTabsAsync(IReadOnlyList<TabState> tabsToClose) {
        var dirtyTabs = tabsToClose.Where(t => t.IsDirty && !t.IsSettings).ToList();

        if (dirtyTabs.Count == 0) {
            // No dirty tabs — close directly.
            foreach (var tab in tabsToClose.ToList()) {
                CloseTabDirect(tab);
            }
            return true;
        }

        if (dirtyTabs.Count == 1) {
            // Single dirty tab — use the simple dialog.
            return await PromptAndCloseTabAsync(dirtyTabs[0]);
        }

        // Multiple dirty tabs — use the multi-tab dialog.
        // Saves happen immediately when per-row [Save] is clicked.
        var dialog = new MultiSaveChangesDialog(dirtyTabs, SaveTabAsync, _theme);
        await dialog.ShowDialog(this);
        var result = dialog.Result;
        if (result.Cancelled) {
            return false;
        }

        // Close all tabs (including clean ones).
        foreach (var tab in tabsToClose.ToList()) {
            CloseTabDirect(tab);
        }
        return true;
    }

    /// <summary>
    /// Saves a tab. For untitled tabs, opens a Save As dialog.
    /// Returns false if the save was cancelled (user dismissed Save As).
    /// </summary>
    private async Task<bool> SaveTabAsync(TabState tab) {
        Editor.FlushCompound();
        if (tab.FilePath is null) {
            // Untitled — need Save As.
            // Temporarily switch to this tab so SaveToAsync works with Editor.Document.
            var previousTab = _activeTab;
            if (_activeTab != tab) {
                SwitchToTab(tab);
            }
            await SaveAsAsync();
            // If FilePath is still null after SaveAs, the user cancelled.
            if (tab.FilePath is null) {
                if (previousTab is not null && previousTab != tab && _tabs.Contains(previousTab)) {
                    SwitchToTab(previousTab);
                }
                return false;
            }
            return true;
        }
        // File-backed — save directly.
        var sha1 = await SaveToAsync(tab.FilePath);
        if (sha1 is null) return false;
        tab.BaseSha1 = sha1;
        SnapshotFileStats(tab);
        tab.Document.MarkSavePoint();
        tab.IsDirty = false;
        PushRecentFile(tab.FilePath);
        UpdateTabBar();
        return true;
    }

    private void CloseAllTabsDirect() {
        _tabs.Clear();
        _activeTab = null;
        var tab = AddTab(TabState.CreateUntitled(_tabs));
        SwitchToTab(tab);
    }

    private async Task CloseAllTabsAsync() {
        var allTabs = _tabs.ToList();
        var dirtyTabs = allTabs.Where(t => t.IsDirty && !t.IsSettings).ToList();
        if (dirtyTabs.Count == 0) {
            CloseAllTabsDirect();
            return;
        }
        if (!await PromptAndCloseMultipleTabsAsync(allTabs)) {
            return; // Cancelled
        }
        // Ensure we have at least one tab.
        if (_tabs.Count == 0) {
            var tab = AddTab(TabState.CreateUntitled(_tabs));
            SwitchToTab(tab);
        }
    }

    private void OnTabDocumentChanged(TabState tab) {
        var shouldBeDirty = !tab.Document.IsAtSavePoint;
        if (tab.IsDirty != shouldBeDirty) {
            tab.IsDirty = shouldBeDirty;
            Dispatcher.UIThread.Post(() => {
                UpdateTabBar();
                Toolbar.Refresh();
            });
        }
    }


    // -------------------------------------------------------------------------
    // Tab bar wiring
    // -------------------------------------------------------------------------

    private void WireTabBar() {
        TabBar.TabClicked += idx => {
            if (idx >= 0 && idx < _tabs.Count) SwitchToTab(_tabs[idx]);
        };
        TabBar.TabCloseClicked += async idx => {
            if (idx >= 0 && idx < _tabs.Count) await PromptAndCloseTabAsync(_tabs[idx]);
        };
        TabBar.OverflowClicked += ShowOverflowMenu;
        TabBar.CloseTabsToRightClicked += idx => _ = CloseTabsToRightAsync(idx);
        TabBar.CloseOtherTabsClicked += idx => _ = CloseOtherTabsAsync(idx);
        TabBar.CloseAllTabsClicked += () => _ = CloseAllTabsAsync();
        TabBar.TabReordered += OnTabReordered;
        TabBar.DragAreaPressed += () => {
            // BeginMoveDrag is called from within a PointerPressed handler,
            // so the pointer is already captured. This initiates OS-level
            // window drag.
            BeginMoveDrag(TabBar.LastPointerPressedArgs!);
        };
        TabBar.DragAreaDoubleClicked += () => {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        };

        // Custom chrome buttons (Linux only — Windows uses PreferSystemChrome).
        TabBar.MinimizeClicked += () => WindowState = WindowState.Minimized;
        TabBar.MaximizeClicked += () => {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        };
        TabBar.ChromeCloseClicked += () => Close();

        // Conflict resolution (session restore error icon)
        TabBar.ConflictResolutionClicked += HandleConflictResolution;

        TabBar.RevealInExplorerClicked += RevealInExplorer;
        TabBar.ToggleReadOnlyClicked += ToggleReadOnly;
    }

    private void ShowOverflowMenu() {
        var overflowTabs = TabBar.GetOverflowTabs();
        if (overflowTabs.Count == 0) return;

        // If the menu is already open, close it (toggle behavior).
        if (TabBar.ContextMenu is { IsOpen: true }) {
            TabBar.ContextMenu.Close();
            return;
        }

        var menu = new ContextMenu {
            PlacementTarget = TabBar,
            Placement = PlacementMode.Bottom,
            PlacementRect = TabBar.OverflowButtonRect,
            FontSize = 12,
        };
        foreach (var (index, label) in overflowTabs) {
            var captured = index;
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => {
                if (captured >= 0 && captured < _tabs.Count) {
                    SwitchToTab(_tabs[captured]);
                }
            };
            menu.Items.Add(item);
        }

        // Assign as ContextMenu for proper light-dismiss, clear on close
        // so stale menus don't auto-open on the next right-click.
        TabBar.ContextMenu = menu;
        menu.Closed += (_, _) => { if (TabBar.ContextMenu == menu) TabBar.ContextMenu = null; };
        menu.Open(TabBar);
    }

    private void UpdateTabBar() => TabBar.Update(_tabs, _activeTab);

    private void RevealInExplorer(int tabIndex) {
        if (tabIndex < 0 || tabIndex >= _tabs.Count) return;
        var path = _tabs[tabIndex].FilePath;
        if (string.IsNullOrEmpty(path)) return;
        try {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            } else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {
                Process.Start("open", $"-R \"{path}\"");
            } else {
                // Linux: open the containing folder
                var dir = Path.GetDirectoryName(path);
                if (dir != null) {
                    Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
                }
            }
        } catch {
            // Silently ignore — file manager may not be available
        }
    }

    private void ToggleReadOnly(int tabIndex) {
        if (tabIndex < 0 || tabIndex >= _tabs.Count) return;
        var tab = _tabs[tabIndex];
        if (tab.IsLocked || tab.IsSettings) return;
        tab.IsReadOnly = !tab.IsReadOnly;
        if (_activeTab == tab) {
            Editor.IsEditBlocked = tab.IsReadOnly;
        }
        UpdateTabBar();
    }

    /// <summary>
    /// Toggles read-only on the active tab. Bound as a command so it can
    /// appear in the command palette and accept a keyboard shortcut.
    /// </summary>
    private void ToggleActiveReadOnly() {
        if (_activeTab == null || _activeTab.IsLocked || _activeTab.IsSettings) return;
        _activeTab.IsReadOnly = !_activeTab.IsReadOnly;
        Editor.IsEditBlocked = _activeTab.IsReadOnly;
        UpdateTabBar();
    }

    private async Task CloseTabsToRightAsync(int tabIndex) {
        if (tabIndex < 0 || tabIndex >= _tabs.Count) return;
        var toClose = _tabs.Skip(tabIndex + 1).ToList();
        if (!await PromptAndCloseMultipleTabsAsync(toClose)) {
            return;
        }
        // If active tab was removed, switch to the rightmost remaining
        if (_activeTab != null && !_tabs.Contains(_activeTab)) {
            SwitchToTab(_tabs[^1]);
        } else {
            UpdateTabBar();
        }
    }

    private async Task CloseOtherTabsAsync(int tabIndex) {
        if (tabIndex < 0 || tabIndex >= _tabs.Count) return;
        var keep = _tabs[tabIndex];
        var toClose = _tabs.Where(t => t != keep).ToList();
        if (!await PromptAndCloseMultipleTabsAsync(toClose)) {
            return;
        }
        if (_activeTab != keep && _tabs.Contains(keep)) {
            SwitchToTab(keep);
        } else {
            UpdateTabBar();
        }
    }

    private void OnTabReordered(int fromIndex, int toIndex) {
        if (fromIndex < 0 || fromIndex >= _tabs.Count) return;
        if (toIndex < 0 || toIndex >= _tabs.Count) return;
        if (fromIndex == toIndex) return;
        // Settings tab is pinned to position 0 — don't move it or displace it.
        if (_tabs[fromIndex].IsSettings || _tabs[toIndex].IsSettings) return;
        var tab = _tabs[fromIndex];
        _tabs.RemoveAt(fromIndex);
        _tabs.Insert(toIndex, tab);
        UpdateTabBar();
    }
}
