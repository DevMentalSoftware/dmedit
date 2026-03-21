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
using DevMentalMd.App.Commands;
using DevMentalMd.App.Controls;
using DevMentalMd.App.Services;
using DevMentalMd.App.Settings;
using DevMentalMd.Core.Buffers;
using DevMentalMd.Core.Documents;
using DevMentalMd.Core.IO;
using DevMentalMd.Core.Printing;

namespace DevMentalMd.App;

public partial class MainWindow : Window {

    private const int FLASH_RELOAD_DURATION_MS = 1000 / 3;

    private static readonly FilePickerFileType[] FileTypeFilters = [
        new("Text files") { Patterns = ["*.txt", "*.log", "*.md", "*.*"] },
        new("Markdown") { Patterns = ["*.md", "*.markdown"] },
        new("All files") { Patterns = ["*.*"] },
    ];

    private readonly List<TabState> _tabs = [];
    private TabState? _activeTab;
    private TabState? _findBarTab;
    private readonly RecentFilesStore _recentFiles = RecentFilesStore.Load();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly CommandRegistry _commands = new();
    private readonly KeyBindingService _keyBindings;
    private int _staticMenuItemCount;
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private EditorTheme _theme = EditorTheme.Light;
    private bool _windowStateReady;
    private TabState? _settingsTab;
    private readonly FileWatcherService _watcher = new();
    private readonly List<(MenuItem item, string commandId)> _menuCommandBindings = [];
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
        InitializeComponent();
        RegisterWindowCommands();
        Editor.RegisterCommands(_commands);
        _keyBindings = new KeyBindingService(_settings, _commands);
        _chordTimer = new DispatcherTimer {
            Interval = TimeSpan.FromMilliseconds(_settings.ChordTimeoutMs),
        };
        _chordTimer.Tick += (_, _) => CancelChord();

        // On Linux the WM ignores ExtendClientAreaToDecorationsHint and draws
        // its own title bar. Remove it so we can draw custom chrome buttons
        // in our tab bar, matching the Windows experience. Transparency lets
        // the rounded corner clip show through.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
            SystemDecorations = SystemDecorations.BorderOnly;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            Background = Brushes.Transparent;
            WindowBorder.CornerRadius = new CornerRadius(8);
        }

        RestoreWindowSize();

        MenuNew.Click += OnNew;
        MenuOpen.Click += OnOpen;
        MenuSave.Click += OnSave;
        MenuSaveAs.Click += OnSaveAs;
        MenuSaveAll.Click += (_, _) => SaveAll();
        MenuClose.Click += async (_, _) => { if (_activeTab != null) await PromptAndCloseTabAsync(_activeTab); };
        MenuCloseAll.Click += async (_, _) => await CloseAllTabsAsync();
        MenuExit.Click += (_, _) => Close();
        MenuRevertFile.Click += (_, _) => _ = RevertFileAsync();
        MenuPrint.Click += (_, _) => _ = PrintAsync();
        MenuSaveAsPdf.Click += (_, _) => _ = SaveAsPdfAsync();

        // Print is only available on Windows when the WPF DLL is present.
        // Hide and disable so the keyboard shortcut is also inert.
        MenuPrint.IsVisible = WindowsPrintService.IsAvailable;
        MenuPrint.IsEnabled = WindowsPrintService.IsAvailable;

        MenuManual.Click += (_, _) => OpenHelpDocumentAsync("manual.md", "Manual");
        MenuAbout.Click += (_, _) => OpenHelpDocumentAsync("about.md", "About");

        // Bind menu items to commands so IsEnabled tracks CanExecute.
        // State is refreshed lazily when the parent submenu opens.
        BindMenuToCommand(MenuSave, "File.Save");
        BindMenuToCommand(MenuSaveAs, "File.SaveAs");
        BindMenuToCommand(MenuUndo, "Edit.Undo");
        BindMenuToCommand(MenuRedo, "Edit.Redo");
        BindMenuToCommand(MenuCut, "Edit.Cut");
        BindMenuToCommand(MenuCopy, "Edit.Copy");
        BindMenuToCommand(MenuPaste, "Edit.Paste");
        BindMenuToCommand(MenuPasteMore, "Edit.PasteMore");
        BindMenuToCommand(MenuClipboardRing, "Edit.ClipboardRing");
        BindMenuToCommand(MenuDelete, "Edit.Delete");
        BindMenuToCommand(MenuCaseUpper, "Edit.UpperCase");
        BindMenuToCommand(MenuCaseLower, "Edit.LowerCase");
        BindMenuToCommand(MenuCaseProper, "Edit.ProperCase");
        MenuFile.SubmenuOpened += OnTopMenuOpened;
        MenuEdit.SubmenuOpened += OnTopMenuOpened;

        _staticMenuItemCount = MenuFile.Items.Count;

        PruneRecentFiles();
        RebuildRecentMenu();
        WireScrollBar();
        WireEditMenu();
        WireSearchMenu();
        WireViewMenu();
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
            Editor.Document = tab.Document;
            Editor.IsInputBlocked = tab.IsLoading;
            Editor.RestoreScrollState(tab);
            Editor.Focus();

            // When a loading tab finishes, unblock the editor if it's
            // still the active tab. One-shot — fires once per load.
            if (tab.IsLoading) {
                tab.LoadCompleted += () => {
                    if (_activeTab == tab) {
                        Editor.IsInputBlocked = false;
                        Editor.ResetCaretBlink();
                        Editor.InvalidateLayout();
                    }
                };
            }
        }
        // Show find bar only if this tab owns it.
        FindBar.IsVisible = (tab == _findBarTab);
        UpdateTabBar();

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
        if (tab.IsSettings) {
            _settingsTab = null;
            SettingsPanel.ResetState();
        }
        if (tab == _findBarTab) {
            _findBarTab = null;
        }
        var closedIdx = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        if (_tabs.Count == 0) {
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
            Dispatcher.UIThread.Post(UpdateTabBar);
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
        TabBar.PlusClicked += () => {
            var t = AddTab(TabState.CreateUntitled(_tabs));
            SwitchToTab(t);
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
    }

    private void ShowOverflowMenu() {
        var overflowTabs = TabBar.GetOverflowTabs();
        if (overflowTabs.Count == 0) return;

        var menu = new ContextMenu {
            PlacementTarget = TabBar,
            Placement = PlacementMode.Bottom,
            PlacementRect = TabBar.OverflowButtonRect,
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

        if (DispatchCommand(commandId)) {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Dispatches a command through the registry, applying editor guards for
    /// commands that require the editor to be active and focused.
    /// </summary>
    private bool DispatchCommand(string commandId) {
        var cmd = _commands.TryGet(commandId);
        if (cmd == null) return false;
        if (!cmd.IsEnabled) return false;
        if (cmd.RequiresEditor) {
            if (_activeTab is { IsSettings: true }) return false;
            if (FindBar.IsVisible && FindBar.IsKeyboardFocusWithin) return false;
        }
        cmd.Execute();
        return true;
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
                // Avalonia / the platform may still activate the menu bar via
                // native Alt handling (WM_SYSKEYUP on Windows). Post a
                // deferred focus restore to undo it.
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
        _commands.Register("File.New", "New", () => OnNew(null, null!));
        _commands.Register("File.Open", "Open", () => OnOpen(null, null!));
        _commands.Register("File.Save", "Save", () => OnSave(null, null!),
            canExecute: () => _activeTab is { IsSettings: false, IsReadOnly: false });
        _commands.Register("File.SaveAs", "Save As", () => OnSaveAs(null, null!),
            canExecute: () => _activeTab is { IsSettings: false });
        _commands.Register("File.SaveAll", "Save All", SaveAll);
        _commands.Register("File.Close", "Close",
            () => { if (_activeTab != null) _ = PromptAndCloseTabAsync(_activeTab); });
        _commands.Register("File.CloseAll", "Close All", () => _ = CloseAllTabsAsync());
        _commands.Register("File.Print", "Print",
            () => { if (WindowsPrintService.IsAvailable) _ = PrintAsync(); });
        _commands.Register("File.SaveAsPdf", "Save As PDF", () => _ = SaveAsPdfAsync());
        _commands.Register("File.Exit", "Exit", Close);
        _commands.Register("File.RevertFile", "Revert File", () => _ = RevertFileAsync());
        _commands.Register("File.ReloadFile", "Reload File", () => _ = ReloadFileAsync(_activeTab));
        _commands.Register("File.ClearRecentFiles", "Clear Recent Files", () => {
            _recentFiles.Clear();
            _recentFiles.Save();
        });

        // -- View --
        _commands.Register("View.LineNumbers", "Line Numbers", ToggleLineNumbers);
        _commands.Register("View.StatusBar", "Status Bar", ToggleStatusBar);
        _commands.Register("View.WrapLines", "Wrap Lines", ToggleWrapLines);
        _commands.Register("View.Whitespace", "Show Whitespace", ToggleWhitespace);
        _commands.Register("View.ZoomIn", "Zoom In",
            () => Editor.FontSize = Math.Min(Editor.FontSize + 1, 72));
        _commands.Register("View.ZoomOut", "Zoom Out",
            () => Editor.FontSize = Math.Max(Editor.FontSize - 1, 6));
        _commands.Register("View.ZoomReset", "Zoom Reset",
            () => Editor.FontSize = _settings.EditorFontSize.ToPixels());

        // -- Window --
        _commands.Register("Window.NextTab", "Next Tab", () => CycleTab(+1));
        _commands.Register("Window.PrevTab", "Previous Tab", () => CycleTab(-1));
        _commands.Register("Window.Settings", "Settings", OpenSettings);
        _commands.Register("Window.CommandPalette", "Command Palette", OpenCommandPalette);

        // -- Edit: Clipboard Ring popup (window-level because it opens a dialog) --
        _commands.Register("Edit.ClipboardRing", "Clipboard Ring", () => _ = OpenClipboardRing(),
            canExecute: () => Editor._clipboardRing.Count > 1);

        // -- Find --
        _commands.Register("Find.Find", "Find", () => OpenFindBar(replaceMode: false));
        _commands.Register("Find.Replace", "Replace", () => OpenFindBar(replaceMode: true));
        _commands.Register("Find.FindNext", "Find Next", () => Editor.FindNext());
        _commands.Register("Find.FindPrevious", "Find Previous", () => Editor.FindPrevious());
        _commands.Register("Find.FindNextSelection", "Find Next Selection", () => Editor.FindNextSelection());
        _commands.Register("Find.FindPreviousSelection", "Find Previous Selection", () => Editor.FindPreviousSelection());
        _commands.Register("Find.IncrementalSearch", "Incremental Search", () => Editor.StartIncrementalSearch());

        // -- Focus / Nav (window-level) --
        _commands.Register("Nav.FocusEditor", "Focus Editor", () => {
            if (_activeTab is not { IsSettings: true }) Editor.Focus();
        });
        _commands.Register("Nav.GoToLine", "Go to Line", OpenGoToLine);
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
        SetMenuGesture(MenuNew, "File.New");
        SetMenuGesture(MenuOpen, "File.Open");
        SetMenuGesture(MenuSave, "File.Save");
        SetMenuGesture(MenuSaveAs, "File.SaveAs");
        SetMenuGesture(MenuSaveAll, "File.SaveAll");
        SetMenuGesture(MenuClose, "File.Close");
        SetMenuGesture(MenuCloseAll, "File.CloseAll");
        SetMenuGesture(MenuExit, "File.Exit");
        SetMenuGesture(MenuUndo, "Edit.Undo");
        SetMenuGesture(MenuRedo, "Edit.Redo");
        SetMenuGesture(MenuCut, "Edit.Cut");
        SetMenuGesture(MenuCopy, "Edit.Copy");
        SetMenuGesture(MenuPaste, "Edit.Paste");
        SetMenuGesture(MenuPasteMore, "Edit.PasteMore");
        SetMenuGesture(MenuClipboardRing, "Edit.ClipboardRing");
        SetMenuGesture(MenuDelete, "Edit.Delete");
        SetMenuGesture(MenuSelectAll, "Edit.SelectAll");
        SetMenuGesture(MenuSelectWord, "Edit.SelectWord");
        SetMenuGesture(MenuDeleteLine, "Edit.DeleteLine");
        SetMenuGesture(MenuMoveLineUp, "Edit.MoveLineUp");
        SetMenuGesture(MenuMoveLineDown, "Edit.MoveLineDown");
        SetMenuGesture(MenuCaseUpper, "Edit.UpperCase");
        SetMenuGesture(MenuCaseLower, "Edit.LowerCase");
        SetMenuGesture(MenuCaseProper, "Edit.ProperCase");
        SetMenuGesture(MenuInsertLineBelow, "Edit.InsertLineBelow");
        SetMenuGesture(MenuInsertLineAbove, "Edit.InsertLineAbove");
        SetMenuGesture(MenuDuplicateLine, "Edit.DuplicateLine");
        SetMenuGesture(MenuDeleteWordLeft, "Edit.DeleteWordLeft");
        SetMenuGesture(MenuDeleteWordRight, "Edit.DeleteWordRight");
        SetMenuGesture(MenuIndent, "Edit.SmartIndent");
        SetMenuGesture(MenuFind, "Find.Find");
        SetMenuGesture(MenuReplace, "Find.Replace");
        SetMenuGesture(MenuFindNext, "Find.FindNext");
        SetMenuGesture(MenuFindPrevious, "Find.FindPrevious");
        SetMenuGesture(MenuIncrementalSearch, "Find.IncrementalSearch");
        SetMenuGesture(MenuGoToLine, "Nav.GoToLine");
        SetMenuGesture(MenuLineNumbers, "View.LineNumbers");
        SetMenuGesture(MenuStatusBar, "View.StatusBar");
        SetMenuGesture(MenuWrapLines, "View.WrapLines");
        SetMenuGesture(MenuWhitespace, "View.Whitespace");
        SetMenuGesture(MenuZoomIn, "View.ZoomIn");
        SetMenuGesture(MenuZoomOut, "View.ZoomOut");
        SetMenuGesture(MenuZoomReset, "View.ZoomReset");
        SetMenuGesture(MenuScrollLineUp, "Nav.ScrollLineUp");
        SetMenuGesture(MenuScrollLineDown, "Nav.ScrollLineDown");
        SetMenuGesture(MenuRevertFile, "File.RevertFile");
        SetMenuGesture(MenuCommandPalette, "Window.CommandPalette");
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

    private void WireEditMenu() {
        MenuUndo.Click += (_, _) => _commands.Execute("Edit.Undo");
        MenuRedo.Click += (_, _) => _commands.Execute("Edit.Redo");
        MenuCut.Click += (_, _) => _commands.Execute("Edit.Cut");
        MenuCopy.Click += (_, _) => _commands.Execute("Edit.Copy");
        MenuPaste.Click += (_, _) => _commands.Execute("Edit.Paste");
        MenuPasteMore.Click += (_, _) => _commands.Execute("Edit.PasteMore");
        MenuClipboardRing.Click += (_, _) => _commands.Execute("Edit.ClipboardRing");
        MenuDelete.Click += (_, _) => _commands.Execute("Edit.Delete");
        MenuSelectAll.Click += (_, _) => _commands.Execute("Edit.SelectAll");
        MenuSelectWord.Click += (_, _) => _commands.Execute("Edit.SelectWord");
        MenuDeleteLine.Click += (_, _) => _commands.Execute("Edit.DeleteLine");
        MenuMoveLineUp.Click += (_, _) => _commands.Execute("Edit.MoveLineUp");
        MenuMoveLineDown.Click += (_, _) => _commands.Execute("Edit.MoveLineDown");
        MenuCaseUpper.Click += (_, _) => _commands.Execute("Edit.UpperCase");
        MenuCaseLower.Click += (_, _) => _commands.Execute("Edit.LowerCase");
        MenuCaseProper.Click += (_, _) => _commands.Execute("Edit.ProperCase");
        MenuInsertLineBelow.Click += (_, _) => _commands.Execute("Edit.InsertLineBelow");
        MenuInsertLineAbove.Click += (_, _) => _commands.Execute("Edit.InsertLineAbove");
        MenuDuplicateLine.Click += (_, _) => _commands.Execute("Edit.DuplicateLine");
        MenuDeleteWordLeft.Click += (_, _) => _commands.Execute("Edit.DeleteWordLeft");
        MenuDeleteWordRight.Click += (_, _) => _commands.Execute("Edit.DeleteWordRight");
        MenuIndent.Click += (_, _) => _commands.Execute("Edit.SmartIndent");
    }

    // -------------------------------------------------------------------------
    // Menu-to-command enable/disable tracking
    // -------------------------------------------------------------------------

    private void BindMenuToCommand(MenuItem item, string commandId) =>
        _menuCommandBindings.Add((item, commandId));

    /// <summary>
    /// Refreshes <see cref="MenuItem.IsEnabled"/> for every bound menu item.
    /// Hooked to top-level menu <c>SubmenuOpened</c> so state is evaluated
    /// lazily, right before the user sees the menu.
    /// </summary>
    private void OnTopMenuOpened(object? sender, RoutedEventArgs e) {
        foreach (var (item, id) in _menuCommandBindings) {
            var cmd = _commands.TryGet(id);
            item.IsEnabled = cmd?.IsEnabled ?? true;
        }
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

    /// <summary>
    /// Pushes theme colors to all custom-drawn controls and XAML elements.
    /// Call <see cref="SyncRequestedThemeVariant"/> before this so that
    /// Fluent-themed controls also match.
    /// </summary>
    private void ApplyTheme(EditorTheme theme) {
        _theme = theme;

        // Custom-drawn controls
        Editor.ApplyTheme(theme);
        ScrollBar.ApplyTheme(theme);
        SettingsPanel.ApplyTheme(theme);
        FindBar.ApplyTheme(theme);

        // Tab bar
        TabBar.ApplyTheme(theme);

        // Menu / toolbar bar — background on the outer border so the
        // entire bar (menus, toolbar buttons, gear icon) is uniform.
        MenuBarBorder.Background = theme.TabActiveBackground;
        MenuBarBorder.BorderBrush = theme.TabActiveBackground;
        GearGlyph.Foreground = theme.TabPlusForeground;

        // Status bar
        StatusBar.Background = theme.StatusBarBackground;
        StatusBar.BorderBrush = theme.StatusBarBorder;
        StatsBar.Foreground = theme.StatusBarForeground;
        StatsBarIO.Foreground = theme.StatusBarForeground;
        StatusLeft.Foreground = theme.StatusBarForeground;
        StatusLineCol.Foreground = theme.StatusBarForeground;
        StatusSep1.Foreground = theme.StatusBarForeground;
        StatusInsMode.Foreground = theme.StatusBarForeground;
        StatusSep1b.Foreground = theme.StatusBarForeground;
        StatusLineCount.Foreground = theme.StatusBarForeground;
        StatusSep2.Foreground = theme.StatusBarForeground;
        StatusEncoding.Foreground = theme.StatusBarForeground;
        StatusSep3.Foreground = theme.StatusBarForeground;
        StatusLineEnding.Foreground = theme.StatusBarForeground;
        StatusSep4.Foreground = theme.StatusBarForeground;
        StatusIndent.Foreground = theme.StatusBarForeground;
        StatusSep5.Foreground = theme.StatusBarForeground;
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

        // ScrollBar → Editor: update scroll offset when user drags/clicks scrollbar
        ScrollBar.ScrollRequested += newValue => {
            Editor.ScrollValue = newValue;
        };

        // Track-click page scrolling — handled by the editor in line-space
        // so wrapping is accounted for correctly.
        ScrollBar.PageRequested += direction => Editor.ScrollPage(direction);

        // Show caret immediately when any scrollbar interaction ends
        ScrollBar.InteractionEnded += () => Editor.ResetCaretBlink();
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

    private void WireViewMenu() {
        // Line Numbers
        Editor.ShowLineNumbers = _settings.ShowLineNumbers;
        _lineNumGlyph = CreateMenuCheckGlyph(_settings.ShowLineNumbers);
        MenuLineNumbers.Icon = _lineNumGlyph;
        MenuLineNumbers.Click += (_, _) => ToggleLineNumbers();

        // Status Bar
        _statusBarGlyph = CreateMenuCheckGlyph(_settings.ShowStatusBar);
        MenuStatusBar.Icon = _statusBarGlyph;
        MenuStatusBar.Click += (_, _) => ToggleStatusBar();

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
        MenuWrapLines.Click += (_, _) => ToggleWrapLines();

        // Show Whitespace
        Editor.ShowWhitespace = _settings.ShowWhitespace;
        _whitespaceGlyph = CreateMenuCheckGlyph(_settings.ShowWhitespace);
        MenuWhitespace.Icon = _whitespaceGlyph;
        MenuWhitespace.Click += (_, _) => ToggleWhitespace();

        // Zoom
        MenuZoomIn.Click += (_, _) => _commands.Execute("View.ZoomIn");
        MenuZoomOut.Click += (_, _) => _commands.Execute("View.ZoomOut");
        MenuZoomReset.Click += (_, _) => _commands.Execute("View.ZoomReset");
        MenuScrollLineUp.Click += (_, _) => _commands.Execute("Nav.ScrollLineUp");
        MenuScrollLineDown.Click += (_, _) => _commands.Execute("Nav.ScrollLineDown");

        // Undo coalesce idle timer (settings-only, no menu item)
        Editor.CoalesceTimerMs = _settings.CoalesceTimerMs;
        Editor._clipboardRing.MaxSize = Math.Max(1, _settings.ClipboardRingSize);
        Editor.ExpandSelectionMode = _settings.ExpandSelectionMode;
        Editor.IndentWidth = _settings.IndentWidth;

        UpdateStatusBarVisibility();
    }

    private void WireSearchMenu() {
        MenuFind.Click += (_, _) => _commands.Execute("Find.Find");
        MenuReplace.Click += (_, _) => _commands.Execute("Find.Replace");
        MenuFindNext.Click += (_, _) => _commands.Execute("Find.FindNext");
        MenuFindPrevious.Click += (_, _) => _commands.Execute("Find.FindPrevious");
        MenuIncrementalSearch.Click += (_, _) => _commands.Execute("Find.IncrementalSearch");
        MenuGoToLine.Click += (_, _) => _commands.Execute("Nav.GoToLine");
        MenuCommandPalette.Click += (_, _) => _commands.Execute("Window.CommandPalette");
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
        };
    }

    private void UpdateStatsBars() {
        var s = Editor.PerfStats;
        var editStat = s.Edit.Count > 0 ? $" | Edit: {s.Edit.Format()}" : "";
        var statsText =
            $"Layout: {s.Layout.Format()} | " +
            $"Render: {s.Render.Format()}{editStat} | " +
            $"{s.ViewportLines} lines ({s.ViewportRows} rows) | " +
            $"{s.ScrollPercent:F1}%";
        if (StatsBar.Text != statsText) StatsBar.Text = statsText;

        string load;
        if (s.FirstChunkTimeMs > 0) {
            load = $"{s.FirstChunkTimeMs:F1}ms + {s.LoadTimeMs:F1}ms";
        } else {
            load = s.LoadTimeMs > 0 ? $"{s.LoadTimeMs:F1}ms" : "\u2014";
        }
        var save = s.SaveTimeMs > 0 ? $"{s.SaveTimeMs:F1}ms" : "\u2014";
        var ioText =
            $"Load: {load} | Save: {save} | " +
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
            foreach (var (label, cmdId) in new[] {
                ("UTF-8", "Edit.EncodingUtf8"),
                ("UTF-8 with BOM", "Edit.EncodingUtf8Bom"),
                ("UTF-16 LE", "Edit.EncodingUtf16Le"),
                ("UTF-16 BE", "Edit.EncodingUtf16Be"),
                ("Windows-1252", "Edit.EncodingWin1252"),
                ("ASCII", "Edit.EncodingAscii"),
            }) {
                var item = new MenuItem { Header = label };
                var cmd = cmdId;
                item.Click += (_, _) => _commands.Execute(cmd);
                flyout.Items.Add(item);
            }
            flyout.ShowAt(BtnEncoding);
        };

        // -- Line ending → flyout --
        BtnLineEnding.PointerPressed += (_, e) => {
            e.Handled = true;
            var doc = Editor.Document;
            if (doc == null) return;
            var lei = doc.LineEndingInfo;
            var flyout = new Avalonia.Controls.MenuFlyout();
            foreach (var (label, le) in new[] {
                ("LF", Core.Documents.LineEnding.LF),
                ("CRLF", Core.Documents.LineEnding.CRLF),
                ("CR", Core.Documents.LineEnding.CR) }) {
                var item = new MenuItem { Header = label };
                var target = le;
                item.Click += (_, _) => {
                    _commands.Execute(target switch {
                        Core.Documents.LineEnding.LF => "Edit.LineEndingLF",
                        Core.Documents.LineEnding.CRLF => "Edit.LineEndingCRLF",
                        _ => "Edit.LineEndingCR",
                    });
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
                        _commands.Execute("Nav.MoveUp");
                    } else {
                        // Engage: move caret to end of document.
                        _commands.Execute("Nav.MoveDocEnd");
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
            spItem.Click += (_, _) => _commands.Execute("Edit.IndentToSpaces");
            var tabItem = new MenuItem { Header = "Convert Indentation to Tabs" };
            tabItem.Click += (_, _) => _commands.Execute("Edit.IndentToTabs");
            flyout.Items.Add(spItem);
            flyout.Items.Add(tabItem);
            flyout.ShowAt(BtnIndent);
        };
    }

    private static readonly Avalonia.Media.IBrush MixedLineEndingBrush =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0xE0, 0x40, 0x40));

    private void UpdateStatusBar() {
        // -- Permanent status bar (always visible) --
        var doc = Editor.Document;
        if (doc == null) {
            SetText(StatusLineCol, "Ln 1 Ch 1");
            SetText(StatusSep1, "");
            SetText(StatusInsMode, "INS");
            SetText(StatusSep1b, "");
            SetText(StatusLineCount, "");
            SetText(StatusSep2, "");
            SetText(StatusEncoding, "");
            SetText(StatusSep3, "");
            SetText(StatusLineEnding, "");
            SetText(StatusSep4, "");
            SetText(StatusIndent, "");
            if (_chordFirst == null) SetText(StatusLeft, "");
        } else {
            var table = doc.Table;
            var stillLoading = table.Buffer is { LengthIsKnown: false };

            var lineCount = table.LineCount;

            var lcText = lineCount >= 0 ? $"{lineCount:N0}" : "\u2014";
            var lcWidth = lcText.Length;

            var lineCol = "";
            // During loading, line-start lookups can fail (pages not in memory).
            if (!stillLoading) {
                var caret = Math.Min(doc.Selection.Caret, table.Length);
                var lineIdx = table.LineFromOfs(caret);
                var lineStart = table.LineStartOfs(lineIdx);
                var col = caret - lineStart + 1;
                var line = lineIdx + 1;

                var lnText = $"{line:N0}".PadLeft(lcWidth);
                var maxLineLen = table.MaxLineLength;
                var chWidth = maxLineLen > 0 ? $"{maxLineLen:N0}".Length : lcWidth;
                var chText = $"{col:N0}".PadLeft(chWidth);
                lineCol = $"Ln {lnText} Ch {chText}";
            }

            SetText(StatusLineCol, lineCol);
            SetText(StatusSep1, "|");
            SetText(StatusInsMode, Editor.OverwriteMode ? "OVR" : "INS");

            if (stillLoading) {
                SetText(StatusSep1b, "|");
                SetText(StatusLineCount, $"{lcText} lines");
                SetText(StatusSep2, "|");
                SetText(StatusEncoding, "loading\u2026");
                SetText(StatusSep3, "");
                SetText(StatusLineEnding, "");
                SetText(StatusSep4, "");
                SetText(StatusIndent, "");
            } else {
                SetText(StatusSep1b, "|");
                SetText(StatusLineCount, $"{lcText} lines");
                SetText(StatusSep2, "|");
                SetText(StatusEncoding, doc.EncodingInfo.Label);
                SetText(StatusSep3, "|");

                var lei = doc.LineEndingInfo;
                SetText(StatusLineEnding, lei.Label);

                var leBrush = lei.IsMixed ? MixedLineEndingBrush : _theme.StatusBarForeground;
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

                SetText(StatusSep4, "|");
                SetText(StatusIndent, doc.IndentInfo.Label);
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
        StatusSep5.Text = show ? "|" : "";

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

        if (_settings.DevMode) {
            MenuFile.Items.Add(new Separator());
            foreach (var sample in DevSamples.All) {
                var captured = sample;
                var item = new MenuItem { Header = sample.DisplayName };
                item.Click += (_, _) => {
                    MenuBar.Close();
                    OpenDevSample(captured);
                };
                MenuFile.Items.Add(item);
            }
        }
    }

    /// <summary>
    /// Opens a file in a new tab, or switches to an existing tab if the file
    /// is already open. Used by File > Open, recent files, and any other path
    /// that opens a file by path.
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

    private void OpenDevSample(ProceduralSample sample) {
        var sw = Stopwatch.StartNew();
        var doc = sample.CreateDocument();
        sw.Stop();

        var tab = new TabState(doc, null, sample.DisplayName);
        AddTab(tab);
        SwitchToTab(tab);
        Editor.PerfStats.LoadTimeMs = sw.Elapsed.TotalMilliseconds;
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
        try {
            var sw = Stopwatch.StartNew();
            var sha1 = await FileSaver.SaveAsync(Editor.Document, path,
                _settings.BackupOnSave);
            sw.Stop();
            Editor.PerfStats.SaveTimeMs = sw.Elapsed.TotalMilliseconds;
            return sha1;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            var dialog = new SaveErrorDialog(path, ex.Message, _theme);
            await dialog.ShowDialog(this);

            if (dialog.Result == SaveErrorChoice.SaveAs) {
                await SaveAsAsync();
                if (_activeTab?.FilePath is not null && !_activeTab.IsDirty) {
                    return _activeTab.BaseSha1;
                }
            }
            return null;
        } catch (Exception ex) {
            // Unexpected failure — write crash report and show error dialog.
            var reportPath = await CrashReport.WriteAsync(ex, path, Editor.Document);
            var dialog = new SaveFailedDialog(path, ex.Message, reportPath, _theme);
            await dialog.ShowDialog(this);

            if (dialog.Result == SaveFailedChoice.SaveAs) {
                await SaveAsAsync();
                if (_activeTab?.FilePath is not null && !_activeTab.IsDirty) {
                    return _activeTab.BaseSha1;
                }
            }

            // Save As failed or user chose Close Tab — close it.
            if (_activeTab is { } tab) {
                CloseTabDirect(tab);
            }
            return null;
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

        PdfGenerator.RenderToPdf(_activeTab.Document, _activeTab.Document.PrintSettings, path);
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
            if (lengths is { Count: > 0 }) {
                newTab.Document.Table.InstallLineTree(
                    CollectionsMarshal.AsSpan(lengths));
            }
        }

        // ---- Transfer live editor state → new document ----
        var newLen = newTab.Document.Table.Length;
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
        var caretLine = table.LineFromOfs(tab.Document.Selection.Caret);
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

            // Throttle: only one post queued at a time so we don't
            // saturate the dispatcher and starve the spinner timer.
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
                        Editor.InvalidateLayout();
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
                    break;
                case "ShowWhitespace":
                    Editor.ShowWhitespace = _settings.ShowWhitespace;
                    _whitespaceGlyph!.Opacity = _settings.ShowWhitespace ? 1.0 : 0.0;
                    break;
                case "WrapLinesAt":
                    Editor.WrapLinesAt = _settings.WrapLinesAt;
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
                case "DevMode":
                    UpdateStatusBarVisibility();
                    RebuildRecentMenu();
                    break;
                case "TailFile":
                    UpdateTailButton();
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
        var palette = new CommandPaletteWindow(_commands, _keyBindings, _settings, _theme);
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
            var caret = Math.Min(doc.Selection.Caret, table.Length);
            currentLine = table.LineFromOfs(caret) + 1;
        }

        var dialog = new GoToLineWindow(_theme, currentLine);
        await dialog.ShowDialog(this);

        if (dialog.TargetLine is { } targetLine && doc != null) {
            var table = doc.Table;
            var lineIdx = Math.Clamp(targetLine - 1, 0, table.LineCount - 1);
            var lineStart = table.LineStartOfs(lineIdx);
            var pos = lineStart;
            if (dialog.TargetCol is { } targetCol) {
                // Clamp column to line length.
                var nextLineStart = lineIdx + 1 < table.LineCount
                    ? table.LineStartOfs(lineIdx + 1)
                    : table.Length;
                var lineLen = nextLineStart - lineStart;
                pos = lineStart + Math.Clamp(targetCol - 1, 0, lineLen);
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

        // Initialize the search box with the current selection if any.
        var doc = _activeTab is not { IsSettings: true } ? Editor.Document : null;
        if (doc != null && !doc.Selection.IsEmpty) {
            FindBar.SetSearchTerm(doc.GetSelectedText());
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

    private void WireFindBar() {
        FindBar.CloseRequested += CloseFindBar;
        FindBar.Resized += w => {
            _settings.FindBarWidth = w;
            _settings.Save();
        };
        FindBar.ApplyTheme(_theme);

        // Provide history lists from settings.
        SyncFindBarHistory();

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
        };

        // Replace all matches.
        FindBar.ReplaceAllRequested += () => {
            var searchTerm = FindBar.SearchTerm;
            var replaceTerm = FindBar.ReplaceTerm;
            if (searchTerm.Length == 0) return;
            _settings.PushRecentFindTerm(searchTerm);
            _settings.PushRecentReplaceTerm(replaceTerm);
            SyncFindBarHistory();
            _settings.ScheduleSave();
            Editor.LastSearchTerm = searchTerm;
            var count = Editor.ReplaceAll(replaceTerm, FindBar.MatchCase, FindBar.WholeWord, FindBar.SearchMode);
            FindBar.SetMatchInfo(0, count);
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
                // Square corners when maximized, rounded when normal (Linux).
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) {
                    WindowBorder.CornerRadius = new CornerRadius(maximized ? 0 : 8);
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

        // Phase 1: create all tabs instantly from the manifest.
        // File-backed tabs start background scans but don't wait.
        var loadingPairs = new List<(TabState tab, SessionStore.TabEntry entry)>();
        foreach (var entry in entries) {
            var tab = SessionStore.CreateTabFromEntry(entry);
            AddTab(tab);
            if (tab.IsLoading) {
                loadingPairs.Add((tab, entry));
            }
        }

        // Activate the saved tab (clamped to valid range).
        var idx = Math.Clamp(activeIdx, 0, _tabs.Count - 1);
        SwitchToTab(_tabs[idx]);

        // Phase 2: finish loading each file asynchronously.
        // All scans run concurrently; smaller files naturally complete first.
        foreach (var (tab, entry) in loadingPairs) {
            WireLoadCompletion(tab, entry);
        }

        return true;
    }

    /// <summary>
    /// Wires streaming progress for a session-restored tab and launches
    /// the async completion (conflict detection + edit replay) as
    /// fire-and-forget. The tab's spinner clears when loading finishes.
    /// </summary>
    private void WireLoadCompletion(TabState tab, SessionStore.TabEntry entry) {
        // Wire streaming progress so the active tab re-layouts incrementally.
        // Throttle: only one post is queued at a time so we don't saturate
        // the dispatcher and starve the spinner timer.
        if (tab.Document.Table.Buffer is IProgressBuffer buf) {
            var layoutPending = 0;
            buf.ProgressChanged += () => {
                if (Interlocked.CompareExchange(ref layoutPending, 1, 0) == 0) {
                    Dispatcher.UIThread.Post(() => {
                        Interlocked.Exchange(ref layoutPending, 0);
                        if (_activeTab == tab) {
                            Editor.InvalidateLayout();
                        }
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

                // Conflict detection + edit replay (Loaded already completed).
                SessionStore.FinishLoad(tab, entry);

                SnapshotFileStats(tab);
                _watcher.Watch(tab);

                UpdateTabBar();
                if (_activeTab == tab) {
                    Editor.IsInputBlocked = false;
                    Editor.InvalidateLayout();
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

                tab.FinishLoading();
                UpdateTabBar();
                if (_activeTab == tab) {
                    Editor.IsInputBlocked = false;
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
    private void SaveSession() {
        // Save scroll state for the active tab before serializing.
        if (_activeTab is { IsSettings: false }) {
            Editor.SaveScrollState(_activeTab);
        }

        // Flush pending compound edits on ALL tabs so undo stacks are complete.
        // Editor.FlushCompound() only flushes the active document; inactive tabs
        // may still have uncommitted compounds from earlier editing.
        Editor.FlushCompound();
        foreach (var tab in _tabs) {
            if (!tab.IsSettings) {
                tab.Document.EndCompound();
            }
        }

        var activeIdx = _activeTab is not null ? _tabs.IndexOf(_activeTab) : 0;
        SessionStore.Save(_tabs, Math.Max(0, activeIdx));
    }

    protected override void OnClosing(WindowClosingEventArgs e) {
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
