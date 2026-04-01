using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DMEdit.App.Commands;
using Cmd = DMEdit.App.Commands.Commands;
using DMEdit.App.Controls;
using DMEdit.App.Services;
using DMEdit.App.Settings;
using DMEdit.Core.Buffers;
using DMEdit.Core.Documents;
using DMEdit.Core.IO;
using DMEdit.Core.Printing;

namespace DMEdit.App;

public partial class MainWindow : Window {

    private const int FLASH_RELOAD_DURATION_MS = 1000 / 3;

    private static readonly FilePickerFileType[] FileTypeFilters = [
        new("Text files") { Patterns = ["*.txt", "*.log", "*.md", "*.*"] },
        new("Markdown") { Patterns = ["*.md", "*.markdown"] },
        new("All files") { Patterns = ["*.*"] },
    ];

    private readonly List<TabState> _tabs = [];
    internal TabState? _activeTab;
    private TabState? _findBarTab;
    private CancellationTokenSource? _matchCountCts;
    private readonly RecentFilesStore _recentFiles = RecentFilesStore.Load();
    private readonly AppSettings _settings = AppSettings.Load();
    public AppSettings Settings => _settings;
    private readonly KeyBindingService _keyBindings;
    private int _staticMenuItemCount;
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private EditorTheme _theme = EditorTheme.Light;
    private bool _windowStateReady;
    private TabState? _settingsTab;
    private readonly FileWatcherService _watcher = new();
    private readonly List<(MenuItem item, Command cmd)> _menuCommandBindings = [];
    private TextBlock? _lineNumGlyph;
    private TextBlock? _statusBarGlyph;
    private TextBlock? _wrapLinesGlyph;
    private TextBlock? _whitespaceGlyph;

    // Chord gesture display: cached PART_InputGestureText references.
    private readonly Dictionary<MenuItem, Control> _menuGestureParts = [];
    private readonly HashSet<MenuItem> _gestureHooked = [];

    // Chord state: two-keystroke shortcut in progress.
    private KeyGesture? _chordFirst;
    private readonly DispatcherTimer _chordTimer;

    // Alt-menu activation: defer to KeyUp so Alt+drag / Alt+Shift+Arrow
    // don't accidentally activate the menu bar.
    private bool _altPressedClean;

    public MainWindow() {
        PieceTable.MaxPseudoLine = _settings.MaxPseudoLine;
        InitializeComponent();
        RegisterWindowCommands();
        Editor.RegisterCommands();
        _keyBindings = new KeyBindingService(_settings);
        _chordTimer = new DispatcherTimer {
            Interval = TimeSpan.FromMilliseconds(_settings.ChordTimeoutMs),
        };
        _chordTimer.Tick += (_, _) => CancelChord();

        // On Linux the WM ignores ExtendClientAreaToDecorationsHint and draws
        // its own title bar. Remove it so we can draw custom chrome buttons
        // in our tab bar, matching the Windows experience. BorderOnly removes
        // the title bar but also loses WM resize handles, so we add a
        // rounded, colored border (matching the tab bar) around the content
        // and handle resize ourselves. The window itself is transparent so
        // the rounded corners clip cleanly against the desktop.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
            SystemDecorations = SystemDecorations.BorderOnly;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            Background = Brushes.Transparent;
            WindowBorder.CornerRadius = new CornerRadius(8);
            WindowBorder.Padding = new Thickness(EdgeGrip);
            StatusBar.CornerRadius = new CornerRadius(0, 0, 6, 6);
            AddHandler(PointerPressedEvent, OnEdgePointerPressed,
                RoutingStrategies.Tunnel);
            AddHandler(PointerMovedEvent, OnEdgePointerMoved,
                RoutingStrategies.Tunnel);
        }

        RestoreWindowSize();

        // Wire each XAML MenuItem to its Command — sets up click handler,
        // IsEnabled tracking, gesture text, and advanced-menu visibility
        // in one call per item.
        // File:
        WireMenu(MenuNew, Cmd.FileNew);
        WireMenu(MenuOpen, Cmd.FileOpen);
        WireMenu(MenuSave, Cmd.FileSave);
        WireMenu(MenuSaveAs, Cmd.FileSaveAs);
        WireMenu(MenuSaveAll, Cmd.FileSaveAll);
        WireMenu(MenuRevertFile, Cmd.FileRevertFile);
        WireMenu(MenuReloadFile, Cmd.FileReloadFile);
        WireMenu(MenuToggleReadOnly, Cmd.FileToggleReadOnly);
        WireMenu(MenuPrint, Cmd.FilePrint);
        WireMenu(MenuSaveAsPdf, Cmd.FileSaveAsPdf);
        WireMenu(MenuClose, Cmd.FileClose);
        WireMenu(MenuCloseAll, Cmd.FileCloseAll);
        WireMenu(MenuExit, Cmd.FileExit);
        // Edit:
        WireMenu(MenuUndo, Cmd.EditUndo);
        WireMenu(MenuRedo, Cmd.EditRedo);
        WireMenu(MenuCut, Cmd.EditCut);
        WireMenu(MenuCopy, Cmd.EditCopy);
        WireMenu(MenuPaste, Cmd.EditPaste);
        WireMenu(MenuPasteMore, Cmd.EditPasteMore);
        WireMenu(MenuClipboardRing, Cmd.EditClipboardRing);
        WireMenu(MenuDelete, Cmd.EditDelete);
        WireMenu(MenuSelectAll, Cmd.EditSelectAll);
        WireMenu(MenuSelectWord, Cmd.EditSelectWord);
        WireMenu(MenuDeleteLine, Cmd.EditDeleteLine);
        WireMenu(MenuMoveLineUp, Cmd.EditMoveLineUp);
        WireMenu(MenuMoveLineDown, Cmd.EditMoveLineDown);
        WireMenu(MenuInsertLineBelow, Cmd.EditInsertLineBelow);
        WireMenu(MenuInsertLineAbove, Cmd.EditInsertLineAbove);
        WireMenu(MenuDuplicateLine, Cmd.EditDuplicateLine);
        WireMenu(MenuDeleteWordLeft, Cmd.EditDeleteWordLeft);
        WireMenu(MenuDeleteWordRight, Cmd.EditDeleteWordRight);
        WireMenu(MenuIndent, Cmd.EditSmartIndent);
        WireMenu(MenuCaseUpper, Cmd.EditUpperCase);
        WireMenu(MenuCaseLower, Cmd.EditLowerCase);
        WireMenu(MenuCaseProper, Cmd.EditProperCase);
        // Search:
        WireMenu(MenuFind, Cmd.SearchFind);
        WireMenu(MenuReplace, Cmd.SearchReplace);
        WireMenu(MenuFindNext, Cmd.SearchFindNext);
        WireMenu(MenuFindPrevious, Cmd.SearchFindPrevious);
        WireMenu(MenuFindNextSelection, Cmd.SearchFindNextSelection);
        WireMenu(MenuFindPreviousSelection, Cmd.SearchFindPreviousSelection);
        WireMenu(MenuIncrementalSearch, Cmd.SearchIncrementalSearch);
        WireMenu(MenuGoToLine, Cmd.SearchGoToLine);
        WireMenu(MenuCommandPalette, Cmd.SearchCommandPalette);
        // View:
        WireMenu(MenuLineNumbers, Cmd.ViewLineNumbers);
        WireMenu(MenuStatusBar, Cmd.ViewStatusBar);
        WireMenu(MenuWrapLines, Cmd.ViewWrapLines);
        WireMenu(MenuWhitespace, Cmd.ViewWhitespace);
        WireMenu(MenuZoomIn, Cmd.ViewZoomIn);
        WireMenu(MenuZoomOut, Cmd.ViewZoomOut);
        WireMenu(MenuZoomReset, Cmd.ViewZoomReset);
        WireMenu(MenuScrollLineUp, Cmd.ViewScrollLineUp);
        WireMenu(MenuScrollLineDown, Cmd.ViewScrollLineDown);

        // Print is only available on Windows when the WPF DLL is present.
        MenuPrint.IsVisible = WindowsPrintService.IsAvailable;
        MenuPrint.IsEnabled = WindowsPrintService.IsAvailable;

        // Help items don't have commands.
        MenuManual.Click += (_, _) => OpenHelpDocumentAsync("manual.md", "Manual");
        MenuAbout.Click += (_, _) => OpenHelpDocumentAsync("about.md", "About");

        // Dev-only diagnostic commands (visible only in DevMode).
        if (_settings.DevMode) {
            MenuDevSep.IsVisible = true;
            MenuDevThrowUI.IsVisible = true;
            MenuDevThrowBG.IsVisible = true;
        }
        MenuDevThrowUI.Click += (_, _) =>
            throw new InvalidOperationException("DevMode test exception on UI thread");
        MenuDevThrowBG.Click += (_, _) => {
            new Thread(static () =>
                throw new InvalidOperationException("DevMode test exception on background thread")) {
                IsBackground = true,
                Name = "DevThrowTest",
            }.Start();
        };

        MenuFile.SubmenuOpened += OnTopMenuOpened;
        MenuEdit.SubmenuOpened += OnTopMenuOpened;
        MenuSearch.SubmenuOpened += OnTopMenuOpened;
        MenuView.SubmenuOpened += OnTopMenuOpened;

        _staticMenuItemCount = MenuFile.Items.Count;

        PruneRecentFiles();
        RebuildRecentMenu();
        WireScrollBar();
        WireViewMenuState();
        InitializeToolbar();
        InitializeTabToolbar();
        ApplyAdvancedMenuVisibility();
        WireSettingsPanel();
        WireThemeSettings();
        WireTabBar();
        WireStatsBar();
        WireStatusBarButtons();
        WireFindBar();
        WireWindowState();
        WireFileWatcher();
        SyncMenuGestures();

        // Clicking on empty menu bar space should fully dismiss any open menu,
        // but must not intercept clicks on actual MenuItems in the bar.
        MenuBarBorder.PointerPressed += (_, e) => {
            for (var src = e.Source as Control; src != null && src != MenuBarBorder; src = src.Parent as Control) {
                if (src is MenuItem) return;
            }
            MenuBar.Close();
        };

        // Any mouse press while Alt is held means Alt is being used as a
        // modifier (e.g. column selection), not for menu activation.
        // Use tunnel routing so this fires before child controls handle the event.
        AddHandler(PointerPressedEvent, (_, _) => {
            if (_altPressedClean) _altPressedClean = false;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // File drag-and-drop: open dropped files in new tabs.
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        // When all menus close, return focus to the editor.
        MenuBar.Closed += (_, _) => {
            if (_activeTab is not { IsSettings: true }) {
                Editor.Focus();
            }
        };

        // Window activation tracking for tab bar (active/inactive styling).
        // No focus stealing — focus stays wherever the user left it.
        Activated += (_, _) => {
            TabBar.IsWindowActive = true;
            // Recheck the active tab's file on window activation in case
            // FSW missed events (e.g. network drives).
            if (_activeTab is not null) {
                _watcher.Recheck(_activeTab);
            }
        };
        Deactivated += (_, _) => {
            TabBar.IsWindowActive = false;
            CancelChord();
        };

        // Give the editor initial focus once at startup.
        Opened += (_, _) => {
            if (_activeTab is not { IsSettings: true }) {
                Editor.Focus();
            }
        };

        InitSessionAsync();
    }

    private async void InitSessionAsync() {
        if (!await TryRestoreSessionAsync()) {
            var tab = AddTab(TabState.CreateUntitled(_tabs));
            SwitchToTab(tab);
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
            Editor.Focus();

            // When a loading tab finishes, unblock the editor if it's
            // still the active tab — unless the tab is readonly or locked.
            if (tab.IsLoading) {
                tab.LoadCompleted += () => {
                    if (_activeTab == tab) {

                        Editor.Document = tab.Document;
                        Editor.RestoreScrollState(tab);
                        Editor.IsEditBlocked = tab.IsReadOnly || tab.IsLocked;
                        Editor.ResetCaretBlink();
                        Editor.InvalidateLayout();
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

    // -------------------------------------------------------------------------
    // Centralized keyboard dispatch (command system)
    // -------------------------------------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e) {
        // Bare Alt: prevent Avalonia's built-in menu activation by marking
        // handled BEFORE base runs. Menu activation is deferred to our
        // OnKeyUp handler (standard Windows behavior).
        if (e.Key is Key.LeftAlt or Key.RightAlt) {
            _altPressedClean = true;
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
        // Mark Alt as consumed when used as a modifier for another key.
        if (_altPressedClean && e.KeyModifiers.HasFlag(KeyModifiers.Alt)) {
            _altPressedClean = false;
        }
        // Ignore other bare modifier keys.
        if (e.Key is Key.LeftShift or Key.RightShift
            or Key.LeftCtrl or Key.RightCtrl
            or Key.LWin or Key.RWin) {
            return;
        }

        // Incremental search mode: intercept keys before normal dispatch.
        if (Editor.InIncrementalSearch) {
            if (e.Key == Key.Escape) {
                Editor.ExitIncrementalSearch();
                e.Handled = true;
                return;
            }
            if (HasCommandModifier(e) || IsNonTextKey(e.Key)) {
                // Exit isearch, then fall through to process the key normally.
                Editor.ExitIncrementalSearch();
            } else {
                // Plain character key — return without handling so that
                // OnTextInput fires and our interception there picks it up.
                return;
            }
        }

        // Menu bar focused: Escape returns focus to the editor.
        if (e.Key == Key.Escape && MenuBar.IsKeyboardFocusWithin) {
            MenuBar.Close();
            if (_activeTab is not { IsSettings: true }) {
                Editor.Focus();
            }
            e.Handled = true;
            return;
        }

        // Column selection mode: Escape exits back to normal editing.
        if (e.Key == Key.Escape && Editor.Document?.ColumnSel != null) {
            Editor.Document.ClearColumnSelection(Editor.IndentWidth);
            Editor.InvalidateVisual();
            Editor.ResetCaretBlink();
            e.Handled = true;
            return;
        }

        // Chord: waiting for the second key of a two-keystroke chord?
        if (_chordFirst != null) {
            _chordTimer.Stop();
            var chordCmd = _keyBindings.ResolveChord(_chordFirst, e.Key, e.KeyModifiers);
            _chordFirst = null;
            StatusLeft.Text = "";
            if (chordCmd != null) {
                if (DispatchCommand(chordCmd)) {
                    e.Handled = true;
                }
                return;
            }
            // Second key didn't complete a chord — fall through to process
            // it as a normal single-key gesture below.
        }

        // Check if this key is the first key of a chord.
        if (_keyBindings.IsChordPrefix(e.Key, e.KeyModifiers)) {
            _chordFirst = new KeyGesture(e.Key, e.KeyModifiers);
            StatusLeft.Text = $"{_chordFirst} was pressed. Waiting for second key of chord\u2026";
            _chordTimer.Stop();
            _chordTimer.Start();
            e.Handled = true;
            return;
        }

        // Normal single-key resolution.
        var commandId = _keyBindings.Resolve(e.Key, e.KeyModifiers);
        if (commandId == null) {
            // Alt+letter with no bound command → try menu access keys.
            if (e.KeyModifiers == KeyModifiers.Alt) {
                TryOpenMenuAccessKey(e);
            }
            return;
        }

        // Menu.* pseudo-commands represent menu access keys (Alt+F, etc.).
        // Open the corresponding menu instead of dispatching as a command.
        if (commandId.StartsWith("Menu.", StringComparison.Ordinal)) {
            TryOpenMenuAccessKey(e);
            return;
        }

        if (DispatchCommand(commandId)) {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Dispatches a command through the registry, applying editor guards for
    /// commands that require the editor to be active and focused.
    /// </summary>
    private bool DispatchCommand(string commandId) {
        var cmd = Cmd.TryGet(commandId);
        if (cmd == null) return false;
        if (!cmd.IsEnabled) return false;
        if (_activeTab is { IsSettings: true }) {
            // On the settings page, only File, Window, Menu, and
            // Nav.FocusEditor commands are allowed.
            if (cmd.Category is not ("File" or "Window" or "Menu")
                && cmd != Cmd.NavFocusEditor) {
                return false;
            }
        } else if (cmd.RequiresEditor) {
            if (FindBar.IsVisible && FindBar.IsKeyboardFocusWithin) return false;
        }
        return cmd.Run();
    }

    protected override void OnKeyUp(KeyEventArgs e) {
        // Alt release: handle BEFORE base to suppress Avalonia's built-in
        // menu activation. Only activate menu if Alt was pressed and released
        // alone (not used as a modifier for column editing or shortcuts).
        if (e.Key is Key.LeftAlt or Key.RightAlt) {
            var wantMenu = _altPressedClean && Editor.Document?.ColumnSel == null;
            _altPressedClean = false;
            e.Handled = true;
            if (wantMenu) {
                MenuBar.Focus();
            } else {
                // Alt was consumed as a modifier (e.g. Alt+Up).  Clear the
                // access-key underlines that Avalonia's AccessKeyHandler may
                // have turned on when it saw the initial Alt press.
                ((Avalonia.Input.IInputRoot)this).ShowAccessKeys = false;
                // The platform may still activate the menu bar via native
                // Alt handling (WM_SYSKEYUP on Windows). Post a deferred
                // focus restore to undo it.
                Dispatcher.UIThread.Post(() => {
                    if (MenuBar.IsKeyboardFocusWithin
                        && _activeTab is not { IsSettings: true }) {
                        MenuBar.Close();
                        Editor.Focus();
                    }
                }, DispatcherPriority.Input);
            }
            return;
        }

        base.OnKeyUp(e);
        // Releasing Ctrl confirms an active PasteMore clipboard-cycling session.
        if (e.Key is Key.LeftCtrl or Key.RightCtrl && Editor.IsClipboardCycling) {
            Editor.ConfirmClipboardCycle();
        }
    }

    /// <summary>
    /// Opens the top-level menu whose access key matches the pressed letter.
    /// Called when Alt+letter is pressed and no command binding matches.
    /// </summary>
    private void TryOpenMenuAccessKey(KeyEventArgs e) {
        // Menu bar is hidden while the settings tab is open.
        if (_activeTab is { IsSettings: true }) return;
        var letter = e.Key.ToString();
        if (letter.Length != 1) return;
        foreach (var item in MenuBar.Items.OfType<MenuItem>()) {
            if (item.Header is not string header) continue;
            var idx = header.IndexOf('_');
            if (idx < 0 || idx + 1 >= header.Length) continue;
            if (char.ToUpperInvariant(header[idx + 1]) == char.ToUpperInvariant(letter[0])) {
                item.Open();
                item.Focus();
                _altPressedClean = false;
                e.Handled = true;
                return;
            }
        }
    }

    private void CancelChord() {
        _chordTimer.Stop();
        _chordFirst = null;
        StatusLeft.Text = "";
    }

    private static bool HasCommandModifier(KeyEventArgs e) =>
        (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt)) != 0;

    private static bool IsNonTextKey(Key key) => key is
        Key.Left or Key.Right or Key.Up or Key.Down
        or Key.Home or Key.End or Key.PageUp or Key.PageDown
        or Key.Delete or Key.Back or Key.Insert or Key.Tab or Key.Enter or Key.Return
        or Key.F1 or Key.F2 or Key.F3 or Key.F4 or Key.F5 or Key.F6
        or Key.F7 or Key.F8 or Key.F9 or Key.F10 or Key.F11 or Key.F12
        or Key.CapsLock or Key.NumLock or Key.Scroll or Key.PrintScreen
        or Key.Pause or Key.Apps or Key.Sleep;

    private void UpdateIncrementalSearchStatus() {
        if (Editor.InIncrementalSearch) {
            StatusLeft.Text = Editor.IncrementalSearchFailed
                ? $"Incremental Search: {Editor.IncrementalSearchText} (not found)"
                : $"Incremental Search: {Editor.IncrementalSearchText}";
        } else {
            StatusLeft.Text = "";
        }
    }

    private void RegisterWindowCommands() {
        // -- File --
        Cmd.FileNew.Wire(() => OnNew(null, null!));
        Cmd.FileOpen.Wire(() => OnOpen(null, null!));
        Cmd.FileSave.Wire(() => OnSave(null, null!),
            canExecute: () => _activeTab is { IsSettings: false, IsReadOnly: false, IsDirty: true });
        Cmd.FileSaveAs.Wire(() => OnSaveAs(null, null!),
            canExecute: () => _activeTab is { IsSettings: false, IsLocked: false });
        Cmd.FileSaveAll.Wire(SaveAll);
        Cmd.FileClose.Wire(
            () => { if (_activeTab != null) _ = PromptAndCloseTabAsync(_activeTab); });
        Cmd.FileCloseAll.Wire(() => _ = CloseAllTabsAsync());
        Cmd.FilePrint.Wire(
            () => { if (WindowsPrintService.IsAvailable) _ = PrintAsync(); });
        Cmd.FileSaveAsPdf.Wire(() => _ = SaveAsPdfAsync());
        Cmd.FileExit.Wire(Close);
        Cmd.FileToggleReadOnly.Wire(ToggleActiveReadOnly,
            canExecute: () => _activeTab is { IsSettings: false, IsLocked: false });
        Cmd.FileRevertFile.Wire(() => _ = RevertFileAsync());
        Cmd.FileReloadFile.Wire(() => _ = ReloadFileAsync(_activeTab));
        Cmd.FileRecent.Wire(() => { }); // Dropdown-only — handled by tab toolbar
        Cmd.FileClearRecentFiles.Wire(() => {
            _recentFiles.Clear();
            _recentFiles.Save();
            RebuildRecentMenu();
        });

        // -- View --
        Cmd.ViewLineNumbers.Wire(ToggleLineNumbers);
        Cmd.ViewStatusBar.Wire(ToggleStatusBar);
        Cmd.ViewWrapLines.Wire(ToggleWrapLines);
        Cmd.ViewWhitespace.Wire(ToggleWhitespace);
        Cmd.ViewZoomIn.Wire(
            () => Editor.FontSize = Math.Min(Editor.FontSize + 1, 72));
        Cmd.ViewZoomOut.Wire(
            () => Editor.FontSize = Math.Max(Editor.FontSize - 1, 6));
        Cmd.ViewZoomReset.Wire(
            () => Editor.FontSize = _settings.EditorFontSize.ToPixels());

        // -- Window --
        Cmd.WindowNextTab.Wire(() => CycleTab(+1));
        Cmd.WindowPrevTab.Wire(() => CycleTab(-1));
        Cmd.WindowSettings.Wire(OpenSettings);
        Cmd.SearchCommandPalette.Wire(OpenCommandPalette);

        // -- Edit: Clipboard Ring popup (window-level because it opens a dialog) --
        Cmd.EditClipboardRing.Wire(() => _ = OpenClipboardRing(),
            canExecute: () => Editor._clipboardRing.Count > 1);

        // -- Search --
        Cmd.SearchFind.Wire(() => OpenFindBar(replaceMode: false));
        Cmd.SearchReplace.Wire(() => OpenFindBar(replaceMode: true));
        Cmd.SearchFindNext.Wire(() => Editor.FindNext());
        Cmd.SearchFindPrevious.Wire(() => Editor.FindPrevious());
        Cmd.SearchFindNextSelection.Wire(() => Editor.FindNextSelection());
        Cmd.SearchFindPreviousSelection.Wire(() => Editor.FindPreviousSelection());
        Cmd.SearchIncrementalSearch.Wire(() => Editor.StartIncrementalSearch());

        // -- Focus / Nav (window-level) --
        Cmd.NavFocusEditor.Wire(() => {
            if (_activeTab is not { IsSettings: true }) Editor.Focus();
        });
        Cmd.SearchGoToLine.Wire(OpenGoToLine);

        // Pseudo-commands for top-level menu access keys (Alt+letter).
        // These are never executed directly — they exist so that the key
        // binding UI shows conflicts when a user tries to bind Alt+F/E/S/V/H
        // to a real command.
        static void Noop() { }
        Cmd.PseudoMenuFile.Wire(Noop);
        Cmd.PseudoMenuEdit.Wire(Noop);
        Cmd.PseudoMenuSearch.Wire(Noop);
        Cmd.PseudoMenuView.Wire(Noop);
        Cmd.PseudoMenuHelp.Wire(Noop);

        // -- Dev (DevMode-only) --
        Cmd.DevThrowOnUIThread.Wire(
            static () => throw new InvalidOperationException("DevMode test exception on UI thread"),
            canExecute: () => _settings.DevMode);
        Cmd.DevThrowOnBackground.Wire(
            static () => new Thread(static () =>
                throw new InvalidOperationException("DevMode test exception on background thread")) {
                IsBackground = true, Name = "DevThrowTest",
            }.Start(),
            canExecute: () => _settings.DevMode);
    }

    private void CycleTab(int direction) {
        if (_tabs.Count <= 1) return;
        var idx = _activeTab != null ? _tabs.IndexOf(_activeTab) : 0;
        idx = (idx + direction + _tabs.Count) % _tabs.Count;
        SwitchToTab(_tabs[idx]);
    }

    private void ToggleLineNumbers() {
        var show = !_settings.ShowLineNumbers;
        _settings.ShowLineNumbers = show;
        _settings.ScheduleSave();
        Editor.ShowLineNumbers = show;
        _lineNumGlyph!.Opacity = show ? 1.0 : 0.0;
    }

    private void ToggleStatusBar() {
        var show = !_settings.ShowStatusBar;
        _settings.ShowStatusBar = show;
        _settings.ScheduleSave();
        _statusBarGlyph!.Opacity = show ? 1.0 : 0.0;
        UpdateStatusBarVisibility();
        TabBar.RefreshToolbar();
    }

    private void ToggleWrapLines() {
        var wrap = !_settings.WrapLines;
        _settings.WrapLines = wrap;
        _settings.ScheduleSave();
        Editor.WrapLines = wrap;
        _wrapLinesGlyph!.Opacity = wrap ? 1.0 : 0.0;
    }

    private void ToggleWhitespace() {
        var show = !_settings.ShowWhitespace;
        _settings.ShowWhitespace = show;
        _settings.ScheduleSave();
        Editor.ShowWhitespace = show;
        _whitespaceGlyph!.Opacity = show ? 1.0 : 0.0;
    }

    private async void SaveAll() {
        // Save All: save every tab that has a file path and is dirty.
        foreach (var tab in _tabs) {
            if (tab.FilePath != null && tab.IsDirty && !tab.IsReadOnly) {
                var sha1 = await SaveToAsync(tab.FilePath);
                if (sha1 is null) continue;
                tab.BaseSha1 = sha1;
                SnapshotFileStats(tab);
                tab.Document.MarkSavePoint();
                tab.IsDirty = false;
                PushRecentFile(tab.FilePath);
            }
        }
        UpdateTabBar();
    }

    /// <summary>
    /// Updates menu item InputGesture text to reflect current key bindings.
    /// Called after construction and after any binding change in settings.
    /// </summary>
    private void SyncMenuGestures() {
        foreach (var (item, cmd) in _menuCommandBindings) {
            SetMenuGesture(item, cmd.Id);
        }
    }

    private void SetMenuGesture(MenuItem item, string commandId) {
        var g = _keyBindings.GetGesture(commandId);
        var isChord = g is { IsChord: true };

        // Single gestures use native InputGesture display.
        // Chords need manual PART_InputGestureText override.
        item.InputGesture = isChord ? null : g?.First;

        if (!isChord) {
            // Restore template-driven display if we previously overrode it.
            if (_menuGestureParts.TryGetValue(item, out var part)) {
                part.ClearValue(IsVisibleProperty);
                if (part is TextBlock tb) tb.ClearValue(TextBlock.TextProperty);
                else if (part is ContentPresenter cp) cp.ClearValue(ContentPresenter.ContentProperty);
            }
            return;
        }

        var text = g!.ToString();

        if (_menuGestureParts.TryGetValue(item, out var cached)) {
            cached.IsVisible = true;
            if (cached is TextBlock tb) tb.Text = text;
            else if (cached is ContentPresenter cp) cp.Content = text;
        } else {
            item.Tag = text;
            if (_gestureHooked.Add(item)) {
                item.TemplateApplied += (_, e) => {
                    if (e.NameScope.Find("PART_InputGestureText") is not Control c) return;
                    _menuGestureParts[item] = c;
                    if (item.Tag is string t) {
                        c.IsVisible = true;
                        if (c is TextBlock tb2) tb2.Text = t;
                        else if (c is ContentPresenter cp2) cp2.Content = t;
                    }
                };
            }
        }
    }

    // -------------------------------------------------------------------------
    // Edit menu wiring
    // -------------------------------------------------------------------------

    // -------------------------------------------------------------------------
    // Menu-to-command wiring
    // -------------------------------------------------------------------------

    /// <summary>
    /// Wires a XAML MenuItem to a Command — click handler, IsEnabled tracking,
    /// gesture text display, and advanced-menu visibility. One call replaces
    /// the old separate BindMenuToCommand, Click handler, and SetMenuGesture.
    /// </summary>
    private void WireMenu(MenuItem item, Command cmd) {
        item.Click += (_, _) => cmd.Run();
        _menuCommandBindings.Add((item, cmd));
    }

    /// <summary>
    /// Refreshes <see cref="MenuItem.IsEnabled"/> for every bound menu item.
    /// Hooked to top-level menu <c>SubmenuOpened</c> so state is evaluated
    /// lazily, right before the user sees the menu.
    /// </summary>
    private void OnTopMenuOpened(object? sender, RoutedEventArgs e) {
        foreach (var (item, cmd) in _menuCommandBindings) {
            item.IsEnabled = cmd.IsEnabled;
        }
    }

    /// <summary>
    /// Hides or shows menu items marked as advanced, based on the current
    /// <see cref="AppSettings.HideAdvancedMenus"/> setting.  After toggling
    /// individual items, cleans up separators so there are no leading,
    /// trailing, or consecutive separators in any submenu.
    /// </summary>
    private void ApplyAdvancedMenuVisibility() {
        var hide = _settings.HideAdvancedMenus;
        foreach (var (item, cmd) in _menuCommandBindings) {
            // Explicit per-command overrides (true/false) take precedence.
            // null = use default behavior.
            if (_settings.MenuOverrides?.TryGetValue(cmd.Id, out var userVisible) == true
                && userVisible.HasValue) {
                item.IsVisible = userVisible.Value;
            } else if (cmd.IsAdvanced) {
                item.IsVisible = !hide;
            } else {
                item.IsVisible = true;
            }
        }

        // Hide the Transform Case submenu when all children are hidden.
        MenuTransformCase.IsVisible = MenuCaseUpper.IsVisible
            || MenuCaseLower.IsVisible || MenuCaseProper.IsVisible;

        // Clean up separators in each top-level menu.
        CleanupSeparators(MenuFile);
        CleanupSeparators(MenuEdit);
        CleanupSeparators(MenuSearch);
        CleanupSeparators(MenuView);
    }

    private static void CleanupSeparators(MenuItem parent) {
        // First, reset all separators to visible so toggling the setting
        // off restores them.  Then hide any that would be leading,
        // trailing, or consecutive (with no visible non-separator item
        // between them).
        foreach (var c in parent.Items.Cast<Control>())
            if (c is Separator) c.IsVisible = true;

        // Single pass: a separator is shown only when there is at least
        // one visible non-separator item before it AND at least one after.
        // We track the last separator we tentatively kept; if another
        // separator or the end of the list follows without an intervening
        // visible item, we hide it.
        var seenItem = false;          // any visible non-separator seen?
        Separator? pending = null;     // separator awaiting confirmation
        foreach (var child in parent.Items.Cast<Control>()) {
            if (child is Separator sep) {
                if (!seenItem) {
                    // Leading separator — hide.
                    sep.IsVisible = false;
                } else if (pending != null) {
                    // Consecutive separator — hide the earlier one.
                    pending.IsVisible = false;
                    pending = sep;
                } else {
                    pending = sep;
                }
            } else if (child.IsVisible) {
                seenItem = true;
                pending = null;        // confirmed the pending separator
            }
        }
        // Hide trailing separator.
        if (pending != null) pending.IsVisible = false;
    }

    // -------------------------------------------------------------------------
    // Theme
    // -------------------------------------------------------------------------

    private void WireThemeSettings() {

        // Listen for system theme changes so we update live when mode is System.
        ActualThemeVariantChanged += (_, _) => {
            if (_settings.ThemeMode == ThemeMode.System) {
                ApplyTheme(ResolveTheme());
            }
        };

        // Apply the initial theme.  Set RequestedThemeVariant first so
        // ActualThemeVariant is correct before ResolveTheme reads it.
        SyncRequestedThemeVariant();
        ApplyTheme(ResolveTheme());
    }

    private void SetThemeMode(ThemeMode mode) {
        _settings.ThemeMode = mode;
        _settings.ScheduleSave();
        SyncRequestedThemeVariant();
        ApplyTheme(ResolveTheme());
    }

    /// <summary>
    /// Sets Avalonia's RequestedThemeVariant from the current setting so
    /// Fluent-themed controls (menus, buttons) match, and so that
    /// ActualThemeVariant is up-to-date before <see cref="ResolveTheme"/>
    /// reads it.
    /// </summary>
    private void SyncRequestedThemeVariant() {
        RequestedThemeVariant = _settings.ThemeMode switch {
            ThemeMode.System => ThemeVariant.Default,
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    /// <summary>
    /// Resolves the current setting to a concrete Light or Dark theme,
    /// consulting the OS preference when the mode is System.
    /// </summary>
    private EditorTheme ResolveTheme() {
        var isDark = _settings.ThemeMode switch {
            ThemeMode.Light => false,
            ThemeMode.Dark => true,
            _ => ActualThemeVariant == ThemeVariant.Dark,
        };
        return isDark ? EditorTheme.Dark : EditorTheme.Light;
    }

    private void ApplySelectionBrushes() {
        Editor.SelectionBrush = _settings.BrightSelection
            ? _theme.BrightSelectionBrush : _theme.SelectionBrush;
    }

    /// <summary>
    /// Pushes theme colors to all custom-drawn controls and XAML elements.
    /// Call <see cref="SyncRequestedThemeVariant"/> before this so that
    /// Fluent-themed controls also match.
    /// </summary>
    private void ApplyTheme(EditorTheme theme) {
        _theme = theme;

        // Custom-drawn controls
        EditorPadding.Background = theme.EditorBackground;
        Editor.ApplyTheme(theme);
        ApplySelectionBrushes();
        ScrollBar.ApplyTheme(theme);
        ApplyHScrollBarTheme(theme);
        SettingsPanel.ApplyTheme(theme);
        FindBar.ApplyTheme(theme);
        Toolbar.ApplyTheme(theme);

        // Tab bar
        TabBar.ApplyTheme(theme);

        // On Linux the resize border is visible — color it to match the tab bar.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
            WindowBorder.Background = theme.TabBarBackground;
        }

        // Menu / toolbar bar — background on the outer border so the
        // entire bar (menus, toolbar buttons, gear icon) is uniform.
        MenuBarBorder.Background = theme.TabActiveBackground;
        MenuBarBorder.BorderBrush = theme.TabActiveBackground;
        GearGlyph.Foreground = theme.TabToolButtonForeground;

        // Status bar
        StatusBar.Background = theme.StatusBarBackground;
        StatusBar.BorderBrush = theme.StatusBarBorder;
        StatsBar.Foreground = theme.StatusBarForeground;
        StatsBarIO.Foreground = theme.StatusBarForeground;
        StatusLeft.Foreground = theme.StatusBarForeground;
        StatusLineCol.Foreground = theme.StatusBarForeground;
        StatusSep1.Background = theme.StatusBarForeground;
        StatusInsMode.Foreground = theme.StatusBarForeground;
        StatusSep1b.Background = theme.StatusBarForeground;
        StatusLineCount.Foreground = theme.StatusBarForeground;
        StatusSep2.Background = theme.StatusBarForeground;
        StatusEncoding.Foreground = theme.StatusBarForeground;
        StatusSep3.Background = theme.StatusBarForeground;
        StatusLineEnding.Foreground = theme.StatusBarForeground;
        StatusSep4.Background = theme.StatusBarForeground;
        StatusIndent.Foreground = theme.StatusBarForeground;
        StatusSep5.Background = theme.StatusBarForeground;
        // StatusTailGlyph foreground is set dynamically in UpdateStatusBar
        // based on active/inactive state.
    }

    // -------------------------------------------------------------------------
    // Scroll bar wiring
    // -------------------------------------------------------------------------

    private void WireScrollBar() {
        // Give the editor a reference so it can drive middle-drag visuals.
        Editor.ScrollBar = ScrollBar;

        // ScrollBar reads scroll state directly from the editor —
        // single source of truth, no sync needed.
        ScrollBar.ScrollSource = Editor;

        // Invalidate the scrollbar visual whenever the editor's scroll
        // state changes (value, extent, viewport).
        Editor.ScrollChanged += (_, _) => ScrollBar.InvalidateVisual();

        // Document metadata (line ending, encoding) changed without a content edit.
        Editor.MetadataChanged += () => {
            if (_activeTab is { } tab) {
                tab.IsDirty = true;
                UpdateTabBar();
            }
            UpdateStatusBar();
        };

        // Background paste → tab spinner
        Editor.BackgroundPasteChanged += loading => {
            if (_activeTab is { } tab) {
                tab.IsLoading = loading;
                UpdateTabBar();
            }
        };

        // Overwrite mode → status bar
        Editor.OverwriteModeChanged += (_, _) => UpdateStatusBar();

        // Incremental search → status bar
        Editor.IncrementalSearchChanged += (_, _) => UpdateIncrementalSearchStatus();

        // Clipboard ring cycling → status bar hint
        Editor.ClipboardCycleStatusChanged += (_, _) => {
            if (Editor.IsClipboardCycling) {
                StatusLeft.Text = $"Clipboard ring: {Editor.ClipboardCycleIndex + 1}/{Editor._clipboardRing.Count}";
            } else {
                StatusLeft.Text = "";
            }
        };

        // Copy/Cut too large for Avalonia fallback → status bar warning
        Editor.CopyTooLarge += len => {
            var mb = len / (1024 * 1024);
            var limitMb = Document.MaxCopyLength / (1024 * 1024);
            StatusLeft.Text = $"Selection too large to copy ({mb:N0} MB, limit {limitMb} MB). Native clipboard unavailable.";
        };

        // ScrollBar → Editor: update scroll offset when user drags/clicks scrollbar
        ScrollBar.ScrollRequested += newValue => {
            Editor.ScrollValue = newValue;
        };

        // Track-click page scrolling — handled by the editor in line-space
        // so wrapping is accounted for correctly.
        ScrollBar.PageRequested += direction => Editor.ScrollPage(direction);

        // Show caret immediately when any scrollbar interaction ends
        ScrollBar.InteractionEnded += () => Editor.ResetCaretBlink();

        // Horizontal scrollbar
        HScrollBar.Scroll += (_, e) => {
            Editor.HScrollValue = e.NewValue;
        };
        Editor.HScrollChanged += UpdateHScrollBar;
    }

    private void UpdateHScrollBar() {
        var max = Editor.HScrollMaximum;
        var show = max > 0;
        HScrollBar.IsVisible = show;
        if (show) {
            HScrollBar.Maximum = max;
            // Use ViewportWidth (set during MeasureOverride) rather than
            // Bounds.Width (set after ArrangeOverride) so the scrollbar is
            // correct on the very first layout pass.
            HScrollBar.ViewportSize = Editor.ViewportWidth - Editor.GutterWidth;
            HScrollBar.SmallChange = Editor.CharWidth;
            HScrollBar.LargeChange = HScrollBar.ViewportSize;
            HScrollBar.Value = Editor.HScrollValue;
        }
    }

    private void ApplyHScrollBarTheme(EditorTheme theme) {
        var r = HScrollBar.Resources;
        r["ScrollBarTrackFill"] = theme.ScrollTrack;
        r["ScrollBarTrackFillPointerOver"] = theme.ScrollTrack;
        r["ScrollBarBackground"] = theme.ScrollTrack;
        r["ScrollBarBackgroundPointerOver"] = theme.ScrollTrack;
        r["ScrollBarPanningThumbBackground"] = theme.ScrollInnerThumbNormal;
        r["ScrollBarThumbFillPointerOver"] = theme.ScrollInnerThumbHover;
        r["ScrollBarThumbFillPressed"] = theme.ScrollInnerThumbPress;
        r["ScrollBarButtonBackground"] = theme.ScrollArrowBg;
        r["ScrollBarButtonBackgroundPointerOver"] = theme.ScrollArrowBgHover;
        r["ScrollBarButtonBackgroundPressed"] = theme.ScrollArrowBgPress;
        r["ScrollBarButtonArrowForeground"] = theme.ScrollArrowGlyph;
        r["ScrollBarButtonArrowForegroundPointerOver"] = theme.ScrollArrowGlyph;
        r["ScrollBarButtonArrowForegroundPressed"] = theme.ScrollArrowGlyph;
        HScrollBar.InvalidateVisual();
    }

    // -------------------------------------------------------------------------
    // View menu wiring
    // -------------------------------------------------------------------------

    private static TextBlock CreateMenuCheckGlyph(bool isChecked) => new() {
        Text = IconGlyphs.CheckMark,
        FontFamily = IconGlyphs.Family,
        FontSize = 14,
        Opacity = isChecked ? 1.0 : 0.0,
        Margin = new Thickness(0, 2, 0, 0),
    };

    /// <summary>
    /// Initializes View menu toggle state (check glyphs, editor settings).
    /// Click handlers are wired by <see cref="WireMenu"/>.
    /// </summary>
    private void WireViewMenuState() {
        // Line Numbers
        Editor.ShowLineNumbers = _settings.ShowLineNumbers;
        _lineNumGlyph = CreateMenuCheckGlyph(_settings.ShowLineNumbers);
        MenuLineNumbers.Icon = _lineNumGlyph;

        // Status Bar
        _statusBarGlyph = CreateMenuCheckGlyph(_settings.ShowStatusBar);
        MenuStatusBar.Icon = _statusBarGlyph;

        // Editor font — always apply the resolved font so it matches the
        // settings preview exactly (avoids subtle differences from the
        // multi-font fallback chain in the EditorControl default).
        Editor.FontFamily = new FontFamily(
            SettingRowFactory.GetEffectiveFontFamily(_settings));
        Editor.FontSize = _settings.EditorFontSize.ToPixels();

        // Wrap Lines + column limit
        Editor.WrapLines = _settings.WrapLines;
        Editor.WrapLinesAt = _settings.WrapLinesAt;
        _wrapLinesGlyph = CreateMenuCheckGlyph(_settings.WrapLines);
        MenuWrapLines.Icon = _wrapLinesGlyph;

        // Show Whitespace
        Editor.ShowWhitespace = _settings.ShowWhitespace;
        _whitespaceGlyph = CreateMenuCheckGlyph(_settings.ShowWhitespace);
        MenuWhitespace.Icon = _whitespaceGlyph;

        // Undo coalesce idle timer (settings-only, no menu item)
        Editor.CoalesceTimerMs = _settings.CoalesceTimerMs;
        Editor._clipboardRing.MaxSize = Math.Max(1, _settings.ClipboardRingSize);
        Editor.ExpandSelectionMode = _settings.ExpandSelectionMode;
        Editor.IndentWidth = _settings.IndentWidth;
        Editor.CaretWidth = _settings.CaretWidth;
        Application.Current!.Resources["DMEditCaretWidth"] = _settings.CaretWidth;
        Editor.MaxRegexMatchLength = _settings.MaxRegexMatchLength;

        UpdateStatusBarVisibility();
    }

    private void InitializeToolbar() {
        // Unsubscribe before re-subscribing — this method is called both
        // at startup and when toolbar settings change.
        Toolbar.ButtonClicked -= OnToolbarButtonClicked;
        Toolbar.OverflowClicked -= ShowToolbarOverflowMenu;

        // Toolbar order = position in Commands.All (the master list).
        // Exclude commands assigned to the tab toolbar.
        var items = Cmd.All
            .Where(c => IsInToolbar(c) && c.ToolbarGlyph != null && !c.ToolbarFixed
                        && !Array.Exists(TabToolbarCommands, t => t == c))
            .Select(c => new ToolbarItem {
                CommandId = c.Id,
                Glyph = c.ToolbarGlyph!,
                Tooltip = c.ToolbarTooltip ?? c.DisplayName,
                IsToggle = c.IsToolbarToggle,
                IsChecked = GetToolbarToggleFunc(c.Id),
            })
            .ToArray();
        Toolbar.SetItems(items);
        Toolbar.ButtonClicked += OnToolbarButtonClicked;
        Toolbar.OverflowClicked += ShowToolbarOverflowMenu;
    }

    private void OnToolbarButtonClicked(string commandId) {
        Cmd.Execute(commandId);
        Toolbar.Refresh();
    }

    /// <summary>
    /// Returns whether the command should appear in the toolbar, checking
    /// user overrides first, then falling back to the manifest default.
    /// </summary>
    private bool IsInToolbar(Command cmd) {
        if (_settings.ToolbarOverrides?.TryGetValue(cmd.Id, out var v) == true) return v;
        return cmd.DefaultInToolbar;
    }

    /// <summary>
    /// Returns the IsChecked func for toolbar toggle buttons, or null for
    /// non-toggle commands.
    /// </summary>
    private Func<bool>? GetToolbarToggleFunc(string id) => id switch {
        "View.WrapLines" => () => _settings.WrapLines,
        "View.Whitespace" => () => _settings.ShowWhitespace,
        "View.LineNumbers" => () => _settings.ShowLineNumbers,
        "View.StatusBar" => () => _settings.ShowStatusBar,
        _ => null,
    };

    private void ShowToolbarOverflowMenu() {
        // If the menu is already open, close it (toggle behavior).
        if (Toolbar.ContextMenu is { IsOpen: true }) {
            Toolbar.ContextMenu.Close();
            return;
        }

        var overflow = Toolbar.GetOverflowItems();
        if (overflow.Count == 0) return;

        var menu = new ContextMenu {
            Placement = PlacementMode.Bottom,
            PlacementRect = Toolbar.OverflowButtonRect,
            FontSize = 12,
        };
        foreach (var item in overflow) {
            var captured = item;
            var mi = new MenuItem { Header = captured.Tooltip };
            if (captured.IsToggle && captured.IsChecked?.Invoke() == true) {
                mi.Icon = CreateMenuCheckGlyph(true);
            }
            var cmd = Cmd.TryGet(captured.CommandId);
            mi.IsEnabled = cmd?.IsEnabled ?? true;
            mi.Click += (_, _) => {
                Cmd.Execute(captured.CommandId);
                Toolbar.Refresh();
            };
            menu.Items.Add(mi);
        }
        // Assign as ContextMenu for proper light-dismiss behavior.
        Toolbar.ContextMenu = menu;
        menu.Open(Toolbar);
    }

    // -------------------------------------------------------------------------
    // Tab toolbar (toolbar items on the tab bar)
    // -------------------------------------------------------------------------

    /// <summary>Commands that belong in the tab toolbar, in display order.</summary>
    private static readonly Command[] TabToolbarCommands = [
        Cmd.FileNew, Cmd.FileOpen, Cmd.FileRecent,
        Cmd.FileSaveAll, Cmd.FileCloseAll, Cmd.ViewStatusBar,
    ];

    private void InitializeTabToolbar() {
        TabBar.ToolbarButtonClicked -= OnTabToolbarButtonClicked;
        TabBar.ToolbarDropdownRequested -= OnTabToolbarDropdownRequested;

        var items = new List<ToolbarItem>();
        foreach (var c in TabToolbarCommands) {
            if (c.ToolbarFixed || IsInToolbar(c)) {
                items.Add(new ToolbarItem {
                    CommandId = c.Id,
                    Glyph = c.ToolbarGlyph!,
                    Tooltip = c.ToolbarTooltip ?? c.DisplayName,
                    IsToggle = c.IsToolbarToggle,
                    IsChecked = GetToolbarToggleFunc(c.Id),
                    IsDropdown = c.IsToolbarDropdown,
                });
            }
        }
        TabBar.SetToolbarItems(items);
        TabBar.ToolbarButtonClicked += OnTabToolbarButtonClicked;
        TabBar.ToolbarDropdownRequested += OnTabToolbarDropdownRequested;
    }

    private void OnTabToolbarButtonClicked(string commandId) {
        Cmd.Execute(commandId);
        TabBar.RefreshToolbar();
    }

    private void OnTabToolbarDropdownRequested(string commandId, Rect buttonRect) {
        if (commandId == "File.Recent") {
            ShowRecentFilesDropdown(buttonRect);
        }
    }

    private void ShowRecentFilesDropdown(Rect buttonRect) {
        // If the menu is already open, close it (toggle behavior).
        if (TabBar.ContextMenu is { IsOpen: true }) {
            TabBar.ContextMenu.Close();
            return;
        }

        var recentPaths = _recentFiles.Paths;
        var visibleCount = Math.Min(recentPaths.Count, _settings.RecentFileCount);

        var menu = new ContextMenu {
            Placement = PlacementMode.Bottom,
            PlacementRect = buttonRect,
            FontSize = 12,
        };

        if (visibleCount == 0) {
            menu.Items.Add(new MenuItem { Header = "(No recent files)", IsEnabled = false });
        } else {
            for (var i = 0; i < visibleCount; i++) {
                var captured = recentPaths[i];
                var mi = new MenuItem { Header = Path.GetFileName(captured) };
                UiHelpers.SetPathToolTip(mi, captured);
                mi.Click += async (_, _) => await OpenFileInTabAsync(captured);
                menu.Items.Add(mi);
            }
        }
        TabBar.ContextMenu = menu;
        menu.Closed += (_, _) => { if (TabBar.ContextMenu == menu) TabBar.ContextMenu = null; };
        menu.Open(TabBar);
    }


    // -------------------------------------------------------------------------
    // Stats / status bar
    // -------------------------------------------------------------------------

    /// <summary>
    /// Shows or hides the status bar sections based on current settings.
    /// The whole <c>StatusBar</c> border is hidden when the settings panel
    /// is active, or when both the permanent bar and the stats bars are off.
    /// </summary>
    private void UpdateStatusBarVisibility() {
        if (_activeTab is { IsSettings: true }) {
            StatusBar.IsVisible = false;
            return;
        }
        PermanentStatusBar.IsVisible = _settings.ShowStatusBar;
        var showStats = _settings.DevMode && _settings.ShowStatistics;
        StatsBar.IsVisible = showStats;
        StatsBarIO.IsVisible = showStats;
        StatusBar.IsVisible = _settings.ShowStatusBar || showStats;
    }

    private void WireStatsBar() {

        // A single fire-once timer coalesces all status updates.  StatsUpdated
        // fires on every render frame, but the timer ensures we only touch the
        // TextBlocks at most twice per second.  If nothing changes the timer
        // never fires again — zero overhead at idle.
        _statsTimer.Tick += (_, _) => {
            _statsTimer.Stop();
            if (_settings.DevMode) {
                UpdateStatsBars();
            }
        };

        Editor.StatusUpdated += () => {
            if (_settings.DevMode && _settings.ShowStatistics && !_statsTimer.IsEnabled) {
                _statsTimer.Start();
            }
            if (_settings.ShowStatusBar) {
                Dispatcher.UIThread.Post(UpdateStatusBar);
            }
            // Toolbar enabled states depend on selection, undo history, etc.
            // Post to avoid InvalidateVisual during the active render pass.
            Dispatcher.UIThread.Post(Toolbar.Refresh);
        };
    }

    private void UpdateStatsBars() {
        var s = Editor.PerfStats;
        var editStat = s.Edit.Count > 0 ? $" | Edit: {s.Edit.Format()}" : "";
        var statsText =
            $"Layout: {s.Layout.Format()} | " +
            $"Render: {s.Render.Format()}{editStat} | " +
            $"{s.ViewportLines} lines ({s.ViewportRows} rows) | " +
            $"{s.ScrollPercent:F1}%" +
            (s.ScrollRetries > 0 ? $" | ScrRetry: {s.ScrollRetries}" : "") +
            $" | ScrCaret: {s.ScrollCaretCalls}";
        if (StatsBar.Text != statsText) StatsBar.Text = statsText;

        string load;
        if (s.FirstChunkTimeMs > 0) {
            load = $"{s.FirstChunkTimeMs:F1}ms + {s.LoadTimeMs:F1}ms";
        } else {
            load = s.LoadTimeMs > 0 ? $"{s.LoadTimeMs:F1}ms" : "\u2014";
        }
        var save = s.SaveTimeMs > 0 ? $"{s.SaveTimeMs:F1}ms" : "\u2014";
        var replaceAll = s.ReplaceAllTimeMs > 0 ? $" | ReplAll: {s.ReplaceAllTimeMs:F1}ms" : "";
        var ioText =
            $"Load: {load} | Save: {save}{replaceAll} | " +
            $"Mem: {s.MemoryMb:F0} MB (max {s.PeakMemoryMb:F0} MB)";
        if (StatsBarIO.Text != ioText) StatsBarIO.Text = ioText;
    }

    private void WireStatusBarButtons() {
        // Hover highlight (same pattern as GearButton).
        void WireHover(Border btn) {
            btn.PointerEntered += (_, _) => btn.Background = _theme.TabInactiveHoverBg;
            btn.PointerExited += (_, _) => btn.Background = Brushes.Transparent;
        }
        WireHover(BtnLineCol);
        WireHover(BtnEncoding);
        WireHover(BtnLineEnding);
        WireHover(BtnIndent);

        // -- Line/Col → GoTo Line --
        BtnLineCol.PointerPressed += (_, _) => OpenGoToLine();

        // -- Encoding → flyout --
        BtnEncoding.PointerPressed += (_, e) => {
            e.Handled = true;
            var doc = Editor.Document;
            if (doc == null) return;
            var flyout = new Avalonia.Controls.MenuFlyout();
            foreach (var (label, enc) in new[] {
                ("UTF-8", Core.Documents.FileEncoding.Utf8),
                ("UTF-8 with BOM", Core.Documents.FileEncoding.Utf8Bom),
                ("UTF-16 LE", Core.Documents.FileEncoding.Utf16Le),
                ("UTF-16 BE", Core.Documents.FileEncoding.Utf16Be),
                ("Windows-1252", Core.Documents.FileEncoding.Windows1252),
                ("ASCII", Core.Documents.FileEncoding.Ascii),
            }) {
                var item = new MenuItem { Header = label };
                var target = enc;
                item.Click += (_, _) => {
                    doc.EncodingInfo = new Core.Documents.EncodingInfo(target);
                    Editor.RaiseMetadataChanged();
                };
                flyout.Items.Add(item);
            }
            flyout.ShowAt(BtnEncoding);
        };

        // -- Line ending → flyout --
        BtnLineEnding.PointerPressed += (_, e) => {
            e.Handled = true;
            var doc = Editor.Document;
            if (doc == null) return;
            var flyout = new Avalonia.Controls.MenuFlyout();
            foreach (var (label, le) in new[] {
                ("LF", Core.Documents.LineEnding.LF),
                ("CRLF", Core.Documents.LineEnding.CRLF),
                ("CR", Core.Documents.LineEnding.CR) }) {
                var item = new MenuItem { Header = label };
                var target = le;
                item.Click += (_, _) => {
                    doc.ConvertLineEndings(target);
                    Editor.RaiseMetadataChanged();
                };
                flyout.Items.Add(item);
            }
            flyout.ShowAt(BtnLineEnding);
        };

        // -- Tail → toggle by moving caret --
        WireHover(BtnTail);
        BtnTail.PointerPressed += (_, e) => {
            if (e.GetCurrentPoint(BtnTail).Properties.IsLeftButtonPressed) {
                e.Handled = true;
                if (_activeTab is { } tab) {
                    if (IsCaretOnLastLine(tab)) {
                        // Disengage: move caret up one line.
                        Cmd.Execute("Nav.MoveUp");
                    } else {
                        // Engage: move caret to end of document.
                        Cmd.Execute("Nav.MoveDocEnd");
                    }
                }
            }
        };

        // -- Indent → flyout --
        BtnIndent.PointerPressed += (_, e) => {
            e.Handled = true;
            var doc = Editor.Document;
            if (doc == null) return;
            var ii = doc.IndentInfo;
            var flyout = new Avalonia.Controls.MenuFlyout();
            var spItem = new MenuItem { Header = "Convert Indentation to Spaces" };
            spItem.Click += (_, _) => Cmd.Execute("Edit.IndentToSpaces");
            var tabItem = new MenuItem { Header = "Convert Indentation to Tabs" };
            tabItem.Click += (_, _) => Cmd.Execute("Edit.IndentToTabs");
            flyout.Items.Add(spItem);
            flyout.Items.Add(tabItem);
            flyout.ShowAt(BtnIndent);
        };
    }

    private void UpdateStatusBar() {
        // -- Permanent status bar (always visible) --
        var doc = Editor.Document;
        if (doc == null) {
            SetText(StatusLineCol, "Ln 1 Ch 1");
            StatusSep1.IsVisible = false;
            SetText(StatusInsMode, "INS");
            StatusSep1b.IsVisible = false;
            SetText(StatusLineCount, "");
            StatusSep2.IsVisible = false;
            SetText(StatusEncoding, "");
            StatusSep3.IsVisible = false;
            SetText(StatusLineEnding, "");
            StatusSep4.IsVisible = false;
            SetText(StatusIndent, "");
            if (_chordFirst == null) SetText(StatusLeft, "");
        } else {
            var table = doc.Table;
            var stillLoading = table.Buffer is { LengthIsKnown: false };

            var lineCount = stillLoading && table.Buffer is { } buf
                ? buf.LineCount
                : table.LineCount;

            var lcText = lineCount >= 0 ? $"{lineCount:N0}" : "\u2014";
            var lcWidth = lcText.Length;

            var lineCol = "";
            // During loading, line-start lookups can fail (pages not in memory).
            if (!stillLoading) {
                var caret = Math.Min(doc.Selection.Caret, table.DocLength);
                var lineIdx = table.LineFromDocOfs(caret);
                var docLineStart = table.DocLineStartOfs(lineIdx);
                var contentLen = table.LineContentLength((int)lineIdx);
                var col = Math.Min(caret - docLineStart, contentLen) + 1;
                var line = lineIdx + 1;

                var lnText = $"{line:N0}".PadLeft(lcWidth);
                var maxLineLen = table.MaxLineLength;
                var chWidth = maxLineLen > 0 ? $"{maxLineLen:N0}".Length : lcWidth;
                var chText = $"{col:N0}".PadLeft(chWidth);
                lineCol = $"Ln {lnText} Ch {chText}";
            }

            SetText(StatusLineCol, lineCol);
            StatusSep1.IsVisible = true;
            SetText(StatusInsMode, Editor.OverwriteMode ? "OVR" : "INS");

            if (stillLoading) {
                StatusSep1b.IsVisible = true;
                SetText(StatusLineCount, $"{lcText} lines");
                StatusSep2.IsVisible = true;
                SetText(StatusEncoding, "loading\u2026");
                StatusSep3.IsVisible = false;
                SetText(StatusLineEnding, "");
                StatusSep4.IsVisible = false;
                SetText(StatusIndent, "");
            } else {
                StatusSep1b.IsVisible = true;
                SetText(StatusLineCount, $"{lcText} lines");
                StatusSep2.IsVisible = true;
                SetText(StatusEncoding, doc.EncodingInfo.Label);
                StatusSep3.IsVisible = true;

                var lei = doc.LineEndingInfo;
                SetText(StatusLineEnding, lei.Label);

                var leBrush = lei.IsMixed ? _theme.StatusBarWarning : _theme.StatusBarForeground;
                if (StatusLineEnding.Foreground != leBrush) StatusLineEnding.Foreground = leBrush;

                // Tooltip for mixed line endings showing counts.
                if (lei.IsMixed) {
                    var parts = new List<string>();
                    if (lei.LfCount > 0) parts.Add($"{lei.LfCount:N0} LF");
                    if (lei.CrlfCount > 0) parts.Add($"{lei.CrlfCount:N0} CRLF");
                    if (lei.CrCount > 0) parts.Add($"{lei.CrCount:N0} CR");
                    ToolTip.SetTip(BtnLineEnding, $"Mixed: {string.Join(", ", parts)}");
                } else {
                    ToolTip.SetTip(BtnLineEnding, null);
                }

                StatusSep4.IsVisible = true;
                SetText(StatusIndent, doc.IndentInfo.Label);

                var indBrush = doc.IndentInfo.IsMixed ? _theme.StatusBarWarning : _theme.StatusBarForeground;
                if (StatusIndent.Foreground != indBrush) StatusIndent.Foreground = indBrush;

                if (doc.IndentInfo.IsMixed) {
                    var parts = new List<string>();
                    if (doc.IndentInfo.SpaceCount > 0) parts.Add($"{doc.IndentInfo.SpaceCount:N0} Spaces");
                    if (doc.IndentInfo.TabCount > 0) parts.Add($"{doc.IndentInfo.TabCount:N0} Tabs");
                    ToolTip.SetTip(BtnIndent, $"Mixed: {string.Join(", ", parts)}");
                } else {
                    ToolTip.SetTip(BtnIndent, null);
                }
            }

        }

        UpdateTailButton();
    }

    /// <summary>
    /// Dimmed brush for the tail icon when the caret is not on the last
    /// line (tail is enabled but currently disengaged).
    /// </summary>
    private static readonly IBrush TailInactiveBrush =
        new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80));

    /// <summary>
    /// Updates the tail button visibility and active/inactive state.
    /// Visible only when the TailFile setting is enabled. Shows full
    /// opacity when tailing is engaged (caret at end, not paused),
    /// dimmed when paused or the caret is elsewhere.
    /// </summary>
    private void UpdateTailButton() {
        var show = _settings.TailFile;
        BtnTail.IsVisible = show;
        StatusSep5.IsVisible = show;

        if (!show) return;

        var tab = _activeTab;
        var active = tab is not null
            && !tab.IsDirty
            && IsCaretOnLastLine(tab)
            && Editor.IsScrolledToEnd;

        StatusTailGlyph.Foreground = active
            ? _theme.StatusBarForeground
            : TailInactiveBrush;

        ToolTip.SetTip(BtnTail, "Tail");
    }

    private static void SetText(TextBlock tb, string text) {
        if (tb.Text != text) tb.Text = text;
    }

    // -------------------------------------------------------------------------
    // File dialog helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// <c>true</c> when the platform provides a real file picker (Windows, macOS,
    /// or Linux with a working DBus portal). <c>false</c> when Avalonia fell back
    /// to its stub <c>FallbackStorageProvider</c> — in that case we use zenity.
    /// </summary>
    private bool UseNativePicker =>
        !RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
        || !StorageProvider.GetType().Name.Contains("Fallback", StringComparison.Ordinal);

    // -------------------------------------------------------------------------
    // File menu handlers
    // -------------------------------------------------------------------------

    private void OnNew(object? sender, RoutedEventArgs e) {
        var tab = AddTab(TabState.CreateUntitled(_tabs));
        SwitchToTab(tab);
    }

    /// <summary>
    /// Opens a bundled help document (e.g. manual.md, about.md) in a read-only tab.
    /// If already open, switches to the existing tab.
    /// </summary>
    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private async void OpenHelpDocumentAsync(string filename, string displayName) {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        var path = Path.Combine(dir, filename);

        // Switch to existing tab if already open.
        var existing = _tabs.FirstOrDefault(t =>
            string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) {
            SwitchToTab(existing);
            return;
        }

        if (!File.Exists(path)) {
            StatusLeft.Text = $"Help file not found: {filename}";
            return;
        }

        try {
            var result = await FileLoader.LoadAsync(path);
            var tab = new TabState(result.Document, path, displayName) {
                LoadResult = result,
                IsLoading = true,
                IsReadOnly = true,
                IsLocked = true,
            };
            AddTab(tab);
            SwitchToTab(tab);
            WireFileLoadCompletion(tab);
            TryCloseEmptyUntitled(tab);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            StatusLeft.Text = $"Open failed: {ex.Message}";
        }
    }

    private async void OnOpen(object? sender, RoutedEventArgs e) {
        string? path;
        if (UseNativePicker) {
            var startDir = await GetStartLocationAsync();
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
                Title = "Open File",
                AllowMultiple = false,
                FileTypeFilter = FileTypeFilters,
                SuggestedStartLocation = startDir,
            });
            if (files.Count == 0) {
                return;
            }
            path = files[0].TryGetLocalPath();
        } else {
            path = await LinuxFileDialog.OpenAsync("Open Markdown File", _settings.LastFileDialogDir);
        }
        if (path is null) {
            return;
        }

        UpdateLastFileDialogDir(path);
        await OpenFileInTabAsync(path);
    }

    private async void OnSave(object? sender, RoutedEventArgs e) {
        if (_activeTab == null || _activeTab.IsSettings || _activeTab.IsReadOnly) return;
        Editor.FlushCompound();
        if (_activeTab.FilePath is null) {
            await SaveAsAsync();
            return;
        }
        var sha1 = await SaveToAsync(_activeTab.FilePath);
        if (sha1 is null) return;
        _activeTab.BaseSha1 = sha1;
        SnapshotFileStats(_activeTab);
        _activeTab.Document.MarkSavePoint();
        _activeTab.IsDirty = false;
        PushRecentFile(_activeTab.FilePath);
        UpdateTabBar();
    }

    private async void OnSaveAs(object? sender, RoutedEventArgs e) => await SaveAsAsync();

    // -------------------------------------------------------------------------
    // Recent Files menu
    // -------------------------------------------------------------------------

    private void RebuildRecentMenu() {
        // Remove previous dynamic items (recent files + dev samples).
        while (MenuFile.Items.Count > _staticMenuItemCount) {
            MenuFile.Items.RemoveAt(MenuFile.Items.Count - 1);
        }

        // Only show recent files in the menu when the File.Recent command's
        // menu visibility (M column in Settings > Commands) is enabled.
        if (!IsCommandMenuVisible(Cmd.FileRecent)) return;

        var recentPaths = _recentFiles.Paths;
        var visibleCount = Math.Min(recentPaths.Count, _settings.RecentFileCount);

        if (visibleCount > 0) {
            MenuFile.Items.Add(new Separator());
            for (var i = 0; i < visibleCount; i++) {
                var captured = recentPaths[i];
                var item = new MenuItem { Header = Path.GetFileName(captured) };
                Controls.UiHelpers.SetPathToolTip(item, captured);
                item.Click += async (_, _) => {
                    MenuBar.Close();
                    await OpenFileInTabAsync(captured);
                };
                MenuFile.Items.Add(item);
            }
        }

    }

    /// <summary>
    /// Returns whether a command should be visible in its menu, respecting
    /// user overrides and the HideAdvancedMenus setting.
    /// </summary>
    private bool IsCommandMenuVisible(Command cmd) {
        if (_settings.MenuOverrides?.TryGetValue(cmd.Id, out var userVisible) == true
            && userVisible.HasValue) {
            return userVisible.Value;
        }
        if (cmd.IsAdvanced && _settings.HideAdvancedMenus) return false;
        return cmd.Menu != CommandMenu.None;
    }

    // -----------------------------------------------------------------
    // Linux edge resize (SystemDecorations.BorderOnly loses WM handles)
    // -----------------------------------------------------------------

    private const double EdgeGrip = 6;

    private void OnEdgePointerPressed(object? sender, PointerPressedEventArgs e) {
        if (WindowState != WindowState.Normal) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var pt = e.GetPosition(this);
        var edge = DetectEdge(pt, Bounds.Width, Bounds.Height);
        if (edge is null) return;
        BeginResizeDrag(edge.Value, e);
        e.Handled = true;
    }

    private void OnEdgePointerMoved(object? sender, PointerEventArgs e) {
        if (WindowState != WindowState.Normal) return;
        var pt = e.GetPosition(this);
        var edge = DetectEdge(pt, Bounds.Width, Bounds.Height);
        Cursor = edge switch {
            WindowEdge.North or WindowEdge.South => new Cursor(StandardCursorType.SizeNorthSouth),
            WindowEdge.West or WindowEdge.East => new Cursor(StandardCursorType.SizeWestEast),
            WindowEdge.NorthWest or WindowEdge.SouthEast => new Cursor(StandardCursorType.TopLeftCorner),
            WindowEdge.NorthEast or WindowEdge.SouthWest => new Cursor(StandardCursorType.TopRightCorner),
            _ => Cursor.Default,
        };
    }

    private static WindowEdge? DetectEdge(Point pt, double w, double h) {
        var left = pt.X < EdgeGrip;
        var right = pt.X >= w - EdgeGrip;
        var top = pt.Y < EdgeGrip;
        var bottom = pt.Y >= h - EdgeGrip;
        return (top, bottom, left, right) switch {
            (true, _, true, _) => WindowEdge.NorthWest,
            (true, _, _, true) => WindowEdge.NorthEast,
            (_, true, true, _) => WindowEdge.SouthWest,
            (_, true, _, true) => WindowEdge.SouthEast,
            (true, _, _, _) => WindowEdge.North,
            (_, true, _, _) => WindowEdge.South,
            (_, _, true, _) => WindowEdge.West,
            (_, _, _, true) => WindowEdge.East,
            _ => null,
        };
    }

    // -----------------------------------------------------------------
    // Drag-and-drop
    // -----------------------------------------------------------------

#pragma warning disable CS0618 // Data/DataFormats.Files deprecated but IDataTransfer replacement lacks GetFiles
    private void OnDragOver(object? sender, DragEventArgs e) {
        e.DragEffects = e.Data.Contains(DataFormats.Files)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e) {
        if (!e.Data.Contains(DataFormats.Files)) {
            return;
        }
        e.Handled = true;
        var items = e.Data.GetFiles();
        if (items is null) {
            return;
        }
#pragma warning restore CS0618
        foreach (var item in items) {
            if (item is IStorageFile file) {
                var path = file.Path.LocalPath;
                if (!string.IsNullOrEmpty(path)) {
                    await OpenFileInTabAsync(path);
                }
            }
        }
    }

    /// <summary>
    /// Opens a file in a new tab, or switches to an existing tab if the file
    /// is already open. Used by File > Open, recent files, drag-and-drop, and
    /// any other path that opens a file by path.
    /// </summary>
    private async Task OpenFileInTabAsync(string path) {
        if (!File.Exists(path)) {
            _recentFiles.Remove(path);
            _recentFiles.Save();
            RebuildRecentMenu();
            return;
        }

        // Check if the file is already open in another tab.
        var existing = _tabs.FirstOrDefault(t =>
            string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) {
            SwitchToTab(existing);
            return;
        }

        LoadResult result;
        try {
            result = await FileLoader.LoadAsync(path);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            StatusLeft.Text = $"Open failed: {ex.Message}";
            return;
        }

        var sw = Stopwatch.StartNew();
        bool ro = false;
        try {
            ro = (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
        } catch {
        }
        var tab = new TabState(result.Document, path, result.DisplayName) {
            LoadResult = result,
            IsLoading = true,
            IsReadOnly = ro
        };
        AddTab(tab);
        SwitchToTab(tab);
        WireStreamingProgress(sw, tab);
        WireFileLoadCompletion(tab);

        PushRecentFile(path);
        TryCloseEmptyUntitled(tab);
    }

    /// <summary>
    /// If the only other tab is an empty, unmodified untitled document,
    /// close it silently. Called after opening a file or help document
    /// so the startup placeholder tab doesn't linger.
    /// </summary>
    private void TryCloseEmptyUntitled(TabState exclude) {
        if (_tabs.Count != 2) return;
        var other = _tabs[0] == exclude ? _tabs[1] : _tabs[0];
        if (other.FilePath == null && !other.IsDirty && !other.IsSettings
            && other.Document.Table.Length == 0) {
            CloseTabDirect(other);
        }
    }

    /// <summary>
    /// Adds <paramref name="path"/> to the recent files list and rebuilds the menu.
    /// </summary>
    private void PushRecentFile(string path) {
        _recentFiles.Push(path);
        _recentFiles.Save();
        RebuildRecentMenu();
    }

    /// <summary>
    /// Prunes recent file entries for local files that no longer exist.
    /// Network paths are left alone (the server may just be offline).
    /// </summary>
    private void PruneRecentFiles() => _recentFiles.PruneMissing();

    // -------------------------------------------------------------------------
    // Save helpers
    // -------------------------------------------------------------------------

    private async Task SaveAsAsync() {
        if (_activeTab == null) return;
        Editor.FlushCompound();

        // For zip files, suggest the inner entry name (e.g., "model.xml" not "model.zip").
        string suggestedName;
        if (_activeTab.LoadResult is { WasZipped: true, InnerEntryName: { } innerName }) {
            suggestedName = innerName;
        } else if (_activeTab.FilePath is not null) {
            suggestedName = Path.GetFileName(_activeTab.FilePath);
        } else {
            suggestedName = _activeTab.DisplayName;
        }

        var title = $"Save \u2014 {_activeTab.DisplayName}";

        string? path;
        if (UseNativePicker) {
            var startDir = await GetStartLocationAsync();
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
                Title = title,
                SuggestedFileName = suggestedName,
                FileTypeChoices = FileTypeFilters,
                SuggestedStartLocation = startDir,
            });
            if (file is null) {
                return;
            }
            path = file.TryGetLocalPath();
        } else {
            path = await LinuxFileDialog.SaveAsync(title, suggestedName, _settings.LastFileDialogDir);
        }
        if (path is null) {
            return;
        }

        // Prevent overwriting a file that is already open in another tab.
        var normalizedPath = Path.GetFullPath(path);
        var conflict = _tabs.FirstOrDefault(t =>
            t != _activeTab && !t.IsSettings && t.FilePath is not null &&
            string.Equals(Path.GetFullPath(t.FilePath), normalizedPath,
                StringComparison.OrdinalIgnoreCase));
        if (conflict is not null) {
            var errorDialog = new ErrorDialog(
                "Cannot Save",
                $"{conflict.DisplayName} is already open in another tab. Close it first, or choose a different file name.",
                [ErrorDialogButton.OK],
                theme: _theme);
            await errorDialog.ShowDialog(this);
            return;
        }

        UpdateLastFileDialogDir(path);
        _watcher.Unwatch(_activeTab); // Old path no longer relevant.
        var sha1 = await SaveToAsync(path);
        if (sha1 is null) return;
        _activeTab.BaseSha1 = sha1;
        _activeTab.Document.MarkSavePoint();
        _activeTab.FilePath = path;
        _activeTab.DisplayName = Path.GetFileName(path);
        _activeTab.IsDirty = false;
        SnapshotFileStats(_activeTab);
        _watcher.Watch(_activeTab); // Watch the new path.
        PushRecentFile(path);
        UpdateTabBar();
    }

    /// <summary>
    /// Snapshots the file's last-write time and size on
    /// <paramref name="tab"/> so the file watcher can cheaply detect
    /// external modifications without recomputing SHA-1.
    /// </summary>
    private static void SnapshotFileStats(TabState tab) {
        if (tab.FilePath is null) return;
        try {
            var info = new FileInfo(tab.FilePath);
            tab.BaseLastWriteTimeUtc = info.LastWriteTimeUtc;
            tab.BaseFileSize = info.Length;
        } catch {
            // File may not exist yet (untitled) — leave defaults.
        }
    }

    private async Task<string?> SaveToAsync(string path) {
        if (Editor.Document is null) {
            return null;
        }

        // Show the loading spinner and block editing while saving.
        var tab = _activeTab;
        if (tab != null) {
            tab.IsLoading = true;
            Editor.IsEditBlocked = true;
            UpdateTabBar();
        }

        try {
            var sw = Stopwatch.StartNew();
            var sha1 = await FileSaver.SaveAsync(Editor.Document, path,
                _settings.BackupOnSave);
            sw.Stop();
            Editor.PerfStats.SaveTimeMs = sw.Elapsed.TotalMilliseconds;
            return sha1;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            var dialog = new ErrorDialog(
                "Could Not Save",
                $"File: {path}\n\n{ex.Message}",
                [ErrorDialogButton.SaveAs, ErrorDialogButton.OK],
                stackTrace: ex.ToString(),
                devMode: _settings.DevMode,
                theme: _theme);
            await dialog.ShowDialog(this);

            if (dialog.Result == ErrorDialogButton.SaveAs) {
                await SaveAsAsync();
                if (_activeTab?.FilePath is not null && !_activeTab.IsDirty) {
                    return _activeTab.BaseSha1;
                }
            }
            return null;
        } catch (Exception ex) {
            // Unexpected failure — write crash report and show error dialog.
            var reportPath = await CrashReport.WriteAsync(ex, "Save", path, Editor.Document);
            var dialog = new ErrorDialog(
                "Save Failed",
                $"File: {path}\n\n{ex.Message}",
                [ErrorDialogButton.SaveAs, ErrorDialogButton.CloseTab],
                crashReportPath: reportPath,
                stackTrace: ex.ToString(),
                devMode: _settings.DevMode,
                theme: _theme);
            await dialog.ShowDialog(this);

            if (dialog.Result == ErrorDialogButton.SaveAs) {
                await SaveAsAsync();
                if (_activeTab?.FilePath is not null && !_activeTab.IsDirty) {
                    return _activeTab.BaseSha1;
                }
            }

            // Save As failed or user chose Close Tab — close it.
            if (_activeTab is { } current) {
                CloseTabDirect(current);
            }
            return null;
        } finally {
            // Clear the loading spinner and unblock editing.
            if (tab != null) {
                tab.IsLoading = false;
                if (_activeTab == tab) {
                    Editor.IsEditBlocked = false;
                }
                UpdateTabBar();
            }
        }
    }

    /// <summary>
    /// Returns the last-used file dialog directory as an <see cref="IStorageFolder"/>,
    /// or null if none is set or the path is invalid.
    /// </summary>
    private async Task<IStorageFolder?> GetStartLocationAsync() {
        if (_settings.LastFileDialogDir is not { } dir) {
            return null;
        }
        try {
            return await StorageProvider.TryGetFolderFromPathAsync(dir);
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Updates <see cref="AppSettings.LastFileDialogDir"/> to the directory
    /// containing <paramref name="filePath"/> and schedules a settings save.
    /// </summary>
    private void UpdateLastFileDialogDir(string filePath) {
        var dir = Path.GetDirectoryName(filePath);
        if (dir is not null) {
            _settings.LastFileDialogDir = dir;
            _settings.ScheduleSave();
        }
    }

    private async Task RevertFileAsync() {
        if (_activeTab?.FilePath is not { } path) return;
        if (!File.Exists(path)) return;
        Editor.FlushCompound();
        var idx = _tabs.IndexOf(_activeTab);
        _tabs.Remove(_activeTab);
        await OpenFileInTabAsync(path);
    }

    private async Task PrintAsync() {
        if (_activeTab is null or { IsSettings: true }) return;
        var service = WindowsPrintService.Service;
        if (service is null) return;

        var doc = _activeTab.Document;
        var printers = service.GetPrinters();
        if (printers.Count == 0) return;

        // Build initial settings from AppSettings, falling back to defaults.
        var initial = BuildPrintSettings();

        // Determine which printer to fetch paper sizes for.
        var savedPrinter = _settings.PrinterName is { } sp
            ? printers.FirstOrDefault(p => p.Name == sp)
            : null;
        var startPrinter = savedPrinter
            ?? printers.FirstOrDefault(p => p.IsDefault)
            ?? printers[0];
        var paperSizes = service.GetPaperSizes(startPrinter.Name);

        var dlg = new PrintDialog(
            printers, paperSizes, initial,
            _settings.PrinterName, _theme, service);
        await dlg.ShowDialog(this);

        if (dlg.JobTicket is not { } ticket) return;

        // Persist the chosen settings on the document.
        doc.PrintSettings = ticket.Settings;

        // Save to AppSettings for next time.
        _settings.PrinterName = ticket.PrinterName;
        _settings.PaperSizeName = ticket.Settings.Paper.Name;
        _settings.PageOrientation = ticket.Settings.Orientation.ToString();
        _settings.MarginTopInches = ticket.Settings.Margins.Top / 72.0;
        _settings.MarginRightInches = ticket.Settings.Margins.Right / 72.0;
        _settings.MarginBottomInches = ticket.Settings.Margins.Bottom / 72.0;
        _settings.MarginLeftInches = ticket.Settings.Margins.Left / 72.0;
        _settings.ScheduleSave();

        await Task.Run(() => service.Print(doc, ticket));
    }

    private PrintSettings BuildPrintSettings() {
        var settings = new PrintSettings();

        // Restore orientation.
        if (_settings.PageOrientation is { } orient
            && Enum.TryParse<PageOrientation>(orient, out var po)) {
            settings.Orientation = po;
        }

        // Restore margins (stored in inches, convert to points).
        if (_settings.MarginTopInches is { } mt
            && _settings.MarginRightInches is { } mr
            && _settings.MarginBottomInches is { } mb
            && _settings.MarginLeftInches is { } ml) {
            settings.Margins = new PrintMargins(mt * 72, mr * 72, mb * 72, ml * 72);
        }

        // Restore paper size by name — the dialog matches this against the
        // printer's real sizes; if no match, index 0 is selected as fallback.
        if (_settings.PaperSizeName is { } paperName) {
            settings.Paper = new PaperSizeInfo {
                Name = paperName,
                Width = settings.Paper.Width,
                Height = settings.Paper.Height,
            };
        }

        return settings;
    }

    private async Task SaveAsPdfAsync() {
        if (_activeTab is null or { IsSettings: true }) return;

        var suggestedName = Path.GetFileNameWithoutExtension(
            _activeTab.FilePath ?? "Untitled") + ".pdf";

        string? path;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
            path = await LinuxFileDialog.SaveAsync("Save As PDF", suggestedName,
                _activeTab.FilePath is not null ? Path.GetDirectoryName(_activeTab.FilePath) : null);
        } else {
            var sp = StorageProvider;
            var result = await sp.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions {
                Title = "Save As PDF",
                SuggestedFileName = suggestedName,
                FileTypeChoices = [
                    new("PDF files") { Patterns = ["*.pdf"] },
                ],
            });
            path = result?.TryGetLocalPath();
        }

        if (path is null) return;

        try {
            PdfGenerator.RenderToPdf(_activeTab.Document, _activeTab.Document.PrintSettings, path);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            var dialog = new ErrorDialog(
                "Could Not Save PDF",
                $"File: {path}\n\n{ex.Message}",
                [ErrorDialogButton.OK],
                stackTrace: ex.ToString(),
                devMode: _settings.DevMode,
                theme: _theme);
            await dialog.ShowDialog(this);
        } catch (Exception ex) {
            var reportPath = await CrashReport.WriteAsync(ex, "Save As PDF", path);
            var dialog = new ErrorDialog(
                "PDF Export Failed",
                ex.Message,
                [ErrorDialogButton.OK],
                crashReportPath: reportPath,
                stackTrace: ex.ToString(),
                devMode: _settings.DevMode,
                theme: _theme);
            await dialog.ShowDialog(this);
        }
    }

    /// <summary>
    /// Reloads a tab's file from disk. If the tab has unsaved edits, shows
    /// the conflict dialog. Called by File.ReloadFile command, conflict
    /// resolution, and auto-reload on file change detection.
    /// </summary>
    private async Task ReloadFileAsync(TabState? tab) {
        if (tab?.FilePath is not { } path) return;
        if (!File.Exists(path)) return;

        // If the tab has unsaved edits, let the user decide.
        if (tab.IsDirty) {
            tab.Conflict = tab.Conflict ?? new Services.FileConflict {
                Kind = Services.FileConflictKind.Changed,
                FilePath = path,
                ExpectedSha1 = tab.BaseSha1,
            };
        } else {
            // Reload: replace the tab in-place.
            await ReloadFileInPlaceAsync(tab);
        }
    }

    /// <summary>
    /// Wraps a reload delegate with re-entrancy guard and cooldown
    /// tracking. Sets <see cref="TabState.ReloadInProgress"/> while
    /// the delegate runs and stamps <see cref="TabState.LastReloadFinishedUtc"/>
    /// when it completes, so that <see cref="OnFileChanged"/> can
    /// throttle rapid-fire external changes.
    /// </summary>
    private async Task ThrottledReloadAsync(
        TabState tab, Func<TabState, Task> reloadFunc) {
        tab.ReloadInProgress = true;
        try {
            await reloadFunc(tab);
        } finally {
            // The tab may have been replaced by the reload. Find the
            // current tab at the same index to stamp the cooldown.
            var current = _tabs.FirstOrDefault(t => t.FilePath == tab.FilePath)
                ?? tab;
            current.ReloadInProgress = false;
            current.LastReloadFinishedUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Reloads a tab's file from disk without removing/re-adding the tab
    /// in the tab bar, producing a smooth visual transition. The old tab
    /// is replaced at the same index with a new tab backed by the fresh
    /// file content.
    /// </summary>
    private async Task ReloadFileInPlaceAsync(TabState tab) {
        if (tab.FilePath is not { } path) return;
        if (!File.Exists(path)) return;

        _watcher.Unwatch(tab);

        // ---- Background: load file and wait for scan to finish ----
        var sw = Stopwatch.StartNew();
        LoadResult result;
        try {
            result = await FileLoader.LoadAsync(path);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            _watcher.Watch(tab);
            StatusLeft.Text = $"Reload failed: {ex.Message}";
            return;
        }
        try {
            if (result.Loaded is not null) await result.Loaded;
        } catch {
            // Scan failed — proceed with whatever content is available.
        }
        sw.Stop();

        // ---- UI thread: atomic swap ----
        // The user may have done anything during the load — scrolled,
        // moved the caret, started a column selection, or even begun
        // typing. We read ALL state right now and transfer it in one
        // tight block. No pre-await snapshots.

        var idx = _tabs.IndexOf(tab);
        if (idx < 0 || !_tabs.Contains(tab)) return;

        // User started editing → abort, don't discard their work.
        if (tab.IsDirty) {
            _watcher.Watch(tab);
            return;
        }

        // Build the replacement tab with fully-loaded content.
        var newTab = new TabState(result.Document, path, result.DisplayName) {
            LoadResult = result,
        };
        newTab.BaseSha1 = result.BaseSha1;
        newTab.Document.LineEndingInfo = result.Document.LineEndingInfo;
        newTab.Document.IndentInfo = result.Document.IndentInfo;
        newTab.Document.EncodingInfo = result.Document.EncodingInfo;
        if (result.Buffer is PagedFileBuffer paged) {
            var lengths = paged.TakeLineLengths();
            var docLengths = paged.TakeDocLineLengths();
            if (lengths is { Count: > 0 }) {
                if (docLengths is { Count: > 0 }) {
                    newTab.Document.Table.InstallLineTree(
                        CollectionsMarshal.AsSpan(lengths),
                        CollectionsMarshal.AsSpan(docLengths));
                } else {
                    newTab.Document.Table.InstallLineTree(
                        CollectionsMarshal.AsSpan(lengths));
                }
            }
        }

        // ---- Transfer live editor state → new document ----
        var newLen = newTab.Document.Table.DocLength;
        var isActive = tab == _activeTab;

        if (isActive) {
            Editor.SaveScrollState(tab);
        }

        var shouldTail = _settings.TailFile
            && isActive
            && Editor.IsScrolledToEnd
            && IsCaretOnLastLine(tab);

        if (shouldTail) {
            newTab.ScrollOffsetY = double.MaxValue;
            newTab.WinTopLine = int.MaxValue;
            newTab.WinScrollOffset = double.MaxValue;
            newTab.Document.Selection = Core.Documents.Selection.Collapsed(newLen);
        } else {
            newTab.ScrollOffsetY = tab.ScrollOffsetY;
            newTab.WinTopLine = tab.WinTopLine;
            newTab.WinScrollOffset = tab.WinScrollOffset;
            newTab.WinRenderOffsetY = tab.WinRenderOffsetY;
            newTab.WinFirstLineHeight = tab.WinFirstLineHeight;

            var oldSel = tab.Document.Selection;
            newTab.Document.Selection = new Core.Documents.Selection(
                Math.Min(oldSel.Anchor, newLen),
                Math.Min(oldSel.Active, newLen));

            if (tab.Document.ColumnSel is { } colSel) {
                var maxLine = (int)Math.Max(0, newTab.Document.Table.LineCount - 1);
                newTab.Document.ColumnSel = colSel with {
                    AnchorLine = Math.Min(colSel.AnchorLine, maxLine),
                    ActiveLine = Math.Min(colSel.ActiveLine, maxLine),
                };
            }
        }

        // ---- Swap ----
        _tabs[idx] = newTab;
        if (isActive) {
            _activeTab = newTab;
            Editor.ReplaceDocument(newTab.Document, newTab);
            Editor.PerfStats.FirstChunkTimeMs = 0;
            Editor.PerfStats.LoadTimeMs = sw.Elapsed.TotalMilliseconds;
        }
        newTab.Document.Changed += (_, _) => OnTabDocumentChanged(newTab);
        if (_findBarTab == tab) {
            _findBarTab = newTab;
        }

        SnapshotFileStats(newTab);
        _watcher.Watch(newTab);
        UpdateTabBar();
    }

    /// <summary>
    /// Returns true when the caret is on the last line of the document.
    /// Used by tail-file logic: the caret being at the end is a strong
    /// signal the user is actively watching new output, whereas being
    /// scrolled to the bottom alone is not sufficient (the user may have
    /// scrolled up and then back).
    /// </summary>
    private static bool IsCaretOnLastLine(TabState tab) {
        var table = tab.Document.Table;
        var lineCount = table.LineCount;
        if (lineCount <= 0) return true;
        var caretLine = table.LineFromDocOfs(tab.Document.Selection.Caret);
        return caretLine >= lineCount - 1;
    }

    // -------------------------------------------------------------------------
    // Streaming file load progress
    // -------------------------------------------------------------------------

    /// <summary>
    /// If the document is backed by a streaming/paged buffer, subscribes to its
    /// progress events so the editor re-layouts incrementally. Callbacks are
    /// guarded so that background loads for inactive tabs don't affect the UI.
    /// </summary>
    private void WireStreamingProgress(Stopwatch sw, TabState tab) {
        if (tab.Document.Table.Buffer is IProgressBuffer buf) {
            // Streaming load — capture time to first renderable chunk, then keep
            // the stopwatch running until fully loaded.
            var firstChunkCaptured = false;
            Editor.PerfStats.FirstChunkTimeMs = 0;
            Editor.PerfStats.LoadTimeMs = sw.Elapsed.TotalMilliseconds;

            // Track scan progress for perf stats.  Don't call
            // InvalidateLayout — the PieceTable can't be queried
            // consistently until the scan finishes and InstallLineTree
            // reconciles the initial piece.  The completion handler
            // does the first real layout.
            var layoutPending = 0;
            buf.ProgressChanged += () => {
                if (Interlocked.CompareExchange(ref layoutPending, 1, 0) == 0) {
                    Dispatcher.UIThread.Post(() => {
                        Interlocked.Exchange(ref layoutPending, 0);
                        if (_activeTab != tab) return;
                        if (!firstChunkCaptured) {
                            firstChunkCaptured = true;
                            Editor.PerfStats.FirstChunkTimeMs = sw.Elapsed.TotalMilliseconds;
                        }
                        Editor.PerfStats.LoadTimeMs = sw.Elapsed.TotalMilliseconds;
                    }, DispatcherPriority.Background);
                }
            };

            buf.LoadComplete += () => {
                Dispatcher.UIThread.Post(() => {
                    sw.Stop();
                    if (_activeTab != tab) return;
                    Editor.PerfStats.LoadTimeMs = sw.Elapsed.TotalMilliseconds;
                    Editor.InvalidateLayout();
                }, DispatcherPriority.Background);
            };

        } else {
            sw.Stop();
            Editor.PerfStats.FirstChunkTimeMs = 0;
            Editor.PerfStats.LoadTimeMs = sw.Elapsed.TotalMilliseconds;
        }
    }

    // -------------------------------------------------------------------------
    // Settings panel
    // -------------------------------------------------------------------------

    private void WireSettingsPanel() {
        SettingsPanel.Initialize(_settings, _keyBindings);
        SettingsPanel.KeyBindingChanged += () => SyncMenuGestures();
        SettingsPanel.MenuOrToolbarChanged += () => {
            ApplyAdvancedMenuVisibility();
            InitializeToolbar();
            InitializeTabToolbar();
            RebuildRecentMenu();
        };
        GearButton.Click += (_, _) => OpenSettings();

        SettingsPanel.SettingChanged += key => {
            // Push setting changes to the editor and menu state.
            switch (key) {
                case "ShowLineNumbers":
                    Editor.ShowLineNumbers = _settings.ShowLineNumbers;
                    _lineNumGlyph!.Opacity = _settings.ShowLineNumbers ? 1.0 : 0.0;
                    break;
                case "ShowStatusBar":
                    _statusBarGlyph!.Opacity = _settings.ShowStatusBar ? 1.0 : 0.0;
                    UpdateStatusBarVisibility();
                    break;
                case "ShowStatistics":
                    UpdateStatusBarVisibility();
                    break;
                case "WrapLines":
                    Editor.WrapLines = _settings.WrapLines;
                    _wrapLinesGlyph!.Opacity = _settings.WrapLines ? 1.0 : 0.0;
                    Toolbar.Refresh();
                    break;
                case "ShowWhitespace":
                    Editor.ShowWhitespace = _settings.ShowWhitespace;
                    _whitespaceGlyph!.Opacity = _settings.ShowWhitespace ? 1.0 : 0.0;
                    Toolbar.Refresh();
                    break;
                case "WrapLinesAt":
                    Editor.WrapLinesAt = _settings.WrapLinesAt;
                    break;
                case "BrightSelection":
                    ApplySelectionBrushes();
                    SettingsPanel.ApplyTheme(_theme);
                    break;
                case "ThemeMode":
                    SyncRequestedThemeVariant();
                    ApplyTheme(ResolveTheme());
                    break;
                case "CoalesceTimerMs":
                    Editor.CoalesceTimerMs = _settings.CoalesceTimerMs;
                    break;
                case "ExpandSelectionMode":
                    Editor.ExpandSelectionMode = _settings.ExpandSelectionMode;
                    break;
                case "OuterThumbScrollRateMultiplier":
                    ScrollBar.OuterScrollRateMultiplier = _settings.OuterThumbScrollRateMultiplier;
                    break;
                case "IndentWidth":
                    Editor.IndentWidth = _settings.IndentWidth;
                    break;
                case "EditorFontFamily":
                    Editor.FontFamily = new FontFamily(
                        SettingRowFactory.GetEffectiveFontFamily(_settings));
                    break;
                case "EditorFontSize":
                    Editor.FontSize = _settings.EditorFontSize.ToPixels();
                    break;
                case "CaretWidth":
                    Editor.CaretWidth = _settings.CaretWidth;
                    Application.Current!.Resources["DMEditCaretWidth"] = _settings.CaretWidth;
                    break;
                case "DevMode":
                    UpdateStatusBarVisibility();
                    MenuDevSep.IsVisible = _settings.DevMode;
                    MenuDevThrowUI.IsVisible = _settings.DevMode;
                    MenuDevThrowBG.IsVisible = _settings.DevMode;
                    break;
                case "TailFile":
                    UpdateTailButton();
                    break;
                case "HideAdvancedMenus":
                    ApplyAdvancedMenuVisibility();
                    break;
            }
        };
    }

    private void OpenSettings() {
        if (_settingsTab != null && _tabs.Contains(_settingsTab)) {
            SwitchToTab(_settingsTab);
            return;
        }
        _settingsTab = TabState.CreateSettings();
        _tabs.Insert(0, _settingsTab);
        _settingsTab.Document.Changed += (_, _) => OnTabDocumentChanged(_settingsTab);
        UpdateTabBar();
        SwitchToTab(_settingsTab);
    }

    private async void OpenCommandPalette() {
        var palette = new CommandPaletteWindow(_keyBindings, _settings, _theme);
        await palette.ShowDialog(this);

        if (palette.SelectedCommandId is { } cmdId) {
            DispatchCommand(cmdId);
        }
    }

    // -------------------------------------------------------------------------
    // Clipboard Ring popup
    // -------------------------------------------------------------------------

    private async Task OpenClipboardRing() {
        var ring = Editor._clipboardRing;
        if (ring.Count == 0) return;
        var dlg = new ClipboardRingWindow(ring, _theme);
        await dlg.ShowDialog(this);
        if (dlg.SelectedIndex >= 0) {
            Editor.PasteFromRing(dlg.SelectedIndex);
        }
    }

    // -------------------------------------------------------------------------
    // Go to Line
    // -------------------------------------------------------------------------

    private async void OpenGoToLine() {
        var doc = _activeTab is not { IsSettings: true } ? Editor.Document : null;
        long currentLine = 1;
        if (doc != null) {
            var table = doc.Table;
            var caret = Math.Min(doc.Selection.Caret, table.DocLength);
            currentLine = table.LineFromDocOfs(caret) + 1;
        }

        var dialog = new GoToLineWindow(_theme, currentLine);
        await dialog.ShowDialog(this);

        if (dialog.TargetLine is { } targetLine && doc != null) {
            var table = doc.Table;
            var lineIdx = Math.Clamp(targetLine - 1, 0, table.LineCount - 1);
            var docLineStart = table.DocLineStartOfs(lineIdx);
            var pos = docLineStart;
            if (dialog.TargetCol is { } targetCol) {
                // Clamp column to line content length.
                var contentLen = table.LineContentLength((int)lineIdx);
                pos = docLineStart + Math.Clamp(targetCol - 1, 0, contentLen);
            }
            Editor.GoToPosition(pos);
        }
    }

    // -------------------------------------------------------------------------
    // Find bar
    // -------------------------------------------------------------------------

    private void OpenFindBar(bool replaceMode) {
        _findBarTab = _activeTab;
        FindBar.IsReplaceMode = replaceMode;
        FindBar.IsVisible = true;
        FindBar.ResetState();

        // Initialize the search box with the current selection (single line only).
        var term = Editor.GetSelectionAsSearchTerm();
        if (term != null) {
            FindBar.SetSearchTerm(term);
        }

        // Focus must be deferred: the key event that triggered this command
        // is still being processed, and Avalonia will restore focus to the
        // previously-focused control after the event completes.  Posting at
        // Input priority ensures our focus request runs after that.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            FindBar.FocusSearchBox();
        }, Avalonia.Threading.DispatcherPriority.Input);
    }

    private void CloseFindBar() {
        _findBarTab = null;
        // Persist width for next session.
        if (FindBar.Width is > 0 and var w) {
            _settings.FindBarWidth = w;
            _settings.Save();
        }
        FindBar.IsVisible = false;
        if (_activeTab is not { IsSettings: true }) {
            Editor.Focus();
        }
    }

    private async void UpdateFindMatchInfo() {
        // Cancel any in-flight count.
        _matchCountCts?.Cancel();
        _matchCountCts?.Dispose();

        var term = FindBar.IsVisible ? FindBar.SearchTerm : (Editor.LastSearchTerm ?? "");
        if (term.Length == 0) {
            StatusLeft.Text = "";
            _matchCountCts = null;
            return;
        }
        if (FindBar.IsVisible) Editor.LastSearchTerm = term;

        var cts = new CancellationTokenSource();
        _matchCountCts = cts;
        try {
            var (current, total, capped) = await Editor.GetMatchInfoAsync(
                FindBar.MatchCase, FindBar.WholeWord, FindBar.SearchMode,
                cts.Token);
            // Only update UI if this request wasn't superseded.
            if (_matchCountCts == cts) {
                if (total == 0) {
                    StatusLeft.Text = "";
                } else if (capped) {
                    StatusLeft.Text = current > 0 ? $"Match {current} of {total}+" : $"{total}+ matches";
                } else {
                    StatusLeft.Text = $"Match {current} of {total}";
                }
            }
        } catch (OperationCanceledException) {
            // Superseded by a newer request — ignore.
        }
    }

    private void SaveFindBarToggles() {
        _settings.FindSearchMode = FindBar.SearchMode;
        _settings.FindMatchCase = FindBar.MatchCase;
        _settings.FindWholeWord = FindBar.WholeWord;
        _settings.ScheduleSave();
    }

    private void WireFindBar() {
        FindBar.CloseRequested += CloseFindBar;
        FindBar.Resized += w => {
            _settings.FindBarWidth = w;
            _settings.Save();
        };
        FindBar.ApplyTheme(_theme);

        // Provide history lists from settings.
        SyncFindBarHistory();

        // Restore persisted find bar toggle state.
        FindBar.SearchMode = _settings.FindSearchMode;
        FindBar.MatchCaseBtn.IsChecked = _settings.FindMatchCase;
        FindBar.WholeWordBtn.IsChecked = _settings.FindWholeWord;

        // Persist toggle state changes.
        FindBar.SearchTermChanged += () => {
            SaveFindBarToggles();
            UpdateFindMatchInfo();
        };
        FindBar.MatchCaseBtn.Click += (_, _) => { SaveFindBarToggles(); UpdateFindMatchInfo(); };
        FindBar.WholeWordBtn.Click += (_, _) => { SaveFindBarToggles(); UpdateFindMatchInfo(); };

        // Find requested: Enter / Shift+Enter or direction button.
        FindBar.FindRequested += forward => {
            var term = FindBar.SearchTerm;
            if (term.Length > 0) {
                _settings.PushRecentFindTerm(term);
                SyncFindBarHistory();
                _settings.ScheduleSave();
                Editor.LastSearchTerm = term;
                if (forward) {
                    Editor.FindNext(FindBar.MatchCase, FindBar.WholeWord, FindBar.SearchMode);
                } else {
                    Editor.FindPrevious(FindBar.MatchCase, FindBar.WholeWord, FindBar.SearchMode);
                }
                UpdateFindMatchInfo();
            }
        };

        // Replace current match.
        FindBar.ReplaceRequested += replaceTerm => {
            var searchTerm = FindBar.SearchTerm;
            if (searchTerm.Length == 0) return;
            _settings.PushRecentFindTerm(searchTerm);
            _settings.PushRecentReplaceTerm(replaceTerm);
            SyncFindBarHistory();
            _settings.ScheduleSave();
            Editor.LastSearchTerm = searchTerm;
            Editor.ReplaceCurrent(replaceTerm, FindBar.MatchCase, FindBar.WholeWord, FindBar.SearchMode);
            UpdateFindMatchInfo();
        };

        // Replace all matches.
        FindBar.ReplaceAllRequested += async () => {
            var searchTerm = FindBar.SearchTerm;
            var replaceTerm = FindBar.ReplaceTerm;
            if (searchTerm.Length == 0) return;
            _settings.PushRecentFindTerm(searchTerm);
            _settings.PushRecentReplaceTerm(replaceTerm);
            SyncFindBarHistory();
            _settings.ScheduleSave();
            Editor.LastSearchTerm = searchTerm;

            var sw = Stopwatch.StartNew();
            var dialog = new ProgressDialog("Replace All", "Searching\u2026", _theme);
            var progress = new Progress<(string Message, double Percent)>(
                p => dialog.Update(p.Message, p.Percent));

            // Show the dialog non-blocking so we can start the async operation.
            var dialogTask = dialog.ShowDialog(this);
            int count;
            try {
                count = await Editor.ReplaceAllAsync(
                    replaceTerm, FindBar.MatchCase, FindBar.WholeWord,
                    FindBar.SearchMode, progress, dialog.CancellationToken);
            } catch (OperationCanceledException) {
                count = 0;
            } finally {
                // Dialog may already be closed (e.g. user closed via taskbar).
                try { dialog.Close(); } catch (InvalidOperationException) { }
                await dialogTask;
            }

            sw.Stop();
            Editor.PerfStats.ReplaceAllTimeMs = sw.Elapsed.TotalMilliseconds;

            if (dialog.WasCancelled) {
                StatusLeft.Text = "Replace All cancelled";
            } else if (count == 0) {
                StatusLeft.Text = "No matches found";
            } else {
                StatusLeft.Text = $"Replaced {count:N0} occurrences in {sw.Elapsed.TotalMilliseconds:F0}ms";
            }
            UpdateFindMatchInfo();
        };

        // Restore persisted width.
        if (_settings.FindBarWidth is > 0 and var w) {
            FindBar.Width = w;
        }
    }

    private void SyncFindBarHistory() {
        FindBar.SearchBox.ItemsSource = _settings.RecentFindTerms;
        FindBar.ReplaceBox.ItemsSource = _settings.RecentReplaceTerms;
    }

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
                    var docLengths = paged.TakeDocLineLengths();
                    if (lengths is { Count: > 0 }) {
                        if (docLengths is { Count: > 0 }) {
                            tab.Document.Table.InstallLineTree(
                                CollectionsMarshal.AsSpan(lengths),
                                CollectionsMarshal.AsSpan(docLengths));
                        } else {
                            tab.Document.Table.InstallLineTree(
                                CollectionsMarshal.AsSpan(lengths));
                        }
                    }
                }

                // Conflict detection + edit replay (Loaded already completed).
                SessionStore.FinishLoad(tab, entry);

                SnapshotFileStats(tab);
                _watcher.Watch(tab);

                UpdateTabBar();
                if (_activeTab == tab) {
                    Editor.IsEditBlocked = false;
                    Editor.InvalidateLayout();
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
                    var docLengths = paged.TakeDocLineLengths();
                    if (lengths is { Count: > 0 }) {
                        if (docLengths is { Count: > 0 }) {
                            tab.Document.Table.InstallLineTree(
                                CollectionsMarshal.AsSpan(lengths),
                                CollectionsMarshal.AsSpan(docLengths));
                        } else {
                            tab.Document.Table.InstallLineTree(
                                CollectionsMarshal.AsSpan(lengths));
                        }
                    }
                }

                SnapshotFileStats(tab);
                _watcher.Watch(tab);

                tab.FinishLoading();
                UpdateTabBar();
                if (_activeTab == tab) {
                    Editor.IsEditBlocked = false;
                    Editor.InvalidateLayout();
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
