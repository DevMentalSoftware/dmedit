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

namespace DevMentalMd.App;

public partial class MainWindow : Window {
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
    private readonly KeyBindingService _keyBindings;
    private int _staticMenuItemCount;
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private EditorTheme _theme = EditorTheme.Light;
    private bool _windowStateReady;
    private TabState? _settingsTab;
    private TextBlock? _lineNumGlyph;
    private TextBlock? _statusBarGlyph;
    private TextBlock? _wrapLinesGlyph;

    // Chord gesture display: cached PART_InputGestureText references.
    private readonly Dictionary<MenuItem, Control> _menuGestureParts = [];
    private readonly HashSet<MenuItem> _gestureHooked = [];

    // Chord state: two-keystroke shortcut in progress.
    private KeyGesture? _chordFirst;
    private readonly DispatcherTimer _chordTimer;

    public MainWindow() {
        InitializeComponent();
        _keyBindings = new KeyBindingService(_settings);
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
        WireFindBar();
        WireWindowState();
        SyncMenuGestures();

        // Clicking on empty menu bar space should fully dismiss any open menu,
        // but must not intercept clicks on actual MenuItems in the bar.
        MenuBarBorder.PointerPressed += (_, e) => {
            for (var src = e.Source as Control; src != null && src != MenuBarBorder; src = src.Parent as Control) {
                if (src is MenuItem) return;
            }
            MenuBar.Close();
        };

        // Window activation tracking for tab bar (active/inactive styling).
        // No focus stealing — focus stays wherever the user left it.
        Activated += (_, _) => TabBar.IsWindowActive = true;
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
            if (_settings.DevMode) {
                LoadManual();
            } else {
                var tab = AddTab(TabState.CreateUntitled(_tabs));
                SwitchToTab(tab);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Tab management
    // -------------------------------------------------------------------------

    private TabState AddTab(TabState tab) {
        _tabs.Add(tab);
        tab.Document.Changed += (_, _) => OnTabDocumentChanged(tab);
        UpdateTabBar();
        return tab;
    }

    private void SwitchToTab(TabState tab) {
        if (_activeTab == tab) return;
        CancelChord();
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
    }

    /// <summary>
    /// Closes the tab without prompting. Use <see cref="PromptAndCloseTabAsync"/>
    /// for user-facing close operations on dirty tabs.
    /// </summary>
    private void CloseTabDirect(TabState tab) {
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
            var dialog = new SaveChangesDialog(tab.DisplayName);
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
        var dialog = new MultiSaveChangesDialog(dirtyTabs, SaveTabAsync);
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
        TabBar.ConflictDiscardClicked += HandleConflictDiscard;
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
        base.OnKeyDown(e);

        // Ignore bare modifier keys.
        if (e.Key is Key.LeftShift or Key.RightShift
            or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin) {
            return;
        }

        // Chord: waiting for the second key of a two-keystroke chord?
        if (_chordFirst != null) {
            _chordTimer.Stop();
            var chordCmd = _keyBindings.ResolveChord(_chordFirst, e.Key, e.KeyModifiers);
            _chordFirst = null;
            StatusLeft.Text = "";
            if (chordCmd != null) {
                if (ExecuteWindowCommand(chordCmd)) {
                    e.Handled = true;
                } else if (_activeTab is not { IsSettings: true }
                           && Editor.ExecuteCommand(chordCmd)) {
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
        if (commandId == null) return;

        if (ExecuteWindowCommand(commandId)) {
            e.Handled = true;
        } else if (_activeTab is not { IsSettings: true }
                   && Editor.ExecuteCommand(commandId)) {
            e.Handled = true;
        }
    }

    private void CancelChord() {
        _chordTimer.Stop();
        _chordFirst = null;
        StatusLeft.Text = "";
    }

    private bool ExecuteWindowCommand(string commandId) {
        switch (commandId) {
            // -- File --
            case CommandIds.FileNew:
                OnNew(null, null!);
                return true;
            case CommandIds.FileOpen:
                OnOpen(null, null!);
                return true;
            case CommandIds.FileSave:
                OnSave(null, null!);
                return true;
            case CommandIds.FileSaveAs:
                OnSaveAs(null, null!);
                return true;
            case CommandIds.FileSaveAll:
                SaveAll();
                return true;
            case CommandIds.FileClose:
                if (_activeTab != null) _ = PromptAndCloseTabAsync(_activeTab);
                return true;
            case CommandIds.FileCloseAll:
                _ = CloseAllTabsAsync();
                return true;
            case CommandIds.FileExit:
                Close();
                return true;

            // -- View --
            case CommandIds.ViewLineNumbers:
                ToggleLineNumbers();
                return true;
            case CommandIds.ViewStatusBar:
                ToggleStatusBar();
                return true;
            case CommandIds.ViewWrapLines:
                ToggleWrapLines();
                return true;
            case CommandIds.ViewZoomIn:
                Editor.FontSize = Math.Min(Editor.FontSize + 1, 72);
                return true;
            case CommandIds.ViewZoomOut:
                Editor.FontSize = Math.Max(Editor.FontSize - 1, 6);
                return true;
            case CommandIds.ViewZoomReset:
                Editor.FontSize = 11.ToPixels();
                return true;

            // -- Window --
            case CommandIds.WindowNextTab:
                CycleTab(+1);
                return true;
            case CommandIds.WindowPrevTab:
                CycleTab(-1);
                return true;
            case CommandIds.WindowSettings:
                OpenSettings();
                return true;
            case CommandIds.WindowCommandPalette:
                OpenCommandPalette();
                return true;

            // -- File: Revert --
            case CommandIds.FileRevertFile:
                _ = RevertFileAsync();
                return true;

            // -- Find --
            case CommandIds.FindFind:
            case CommandIds.FindIncrementalSearch:
                OpenFindBar(replaceMode: false);
                return true;
            case CommandIds.FindReplace:
                OpenFindBar(replaceMode: true);
                return true;
            case CommandIds.FindFindNext:
                if (!FindBar.IsVisible) {
                    OpenFindBar(replaceMode: false);
                }
                return true;
            case CommandIds.FindFindPrevious:
                if (!FindBar.IsVisible) {
                    OpenFindBar(replaceMode: false);
                }
                return true;
            case CommandIds.FindFindWordOrSel:
                OpenFindBarWithSelection();
                return true;

            // -- Focus --
            case CommandIds.NavFocusEditor:
                if (_activeTab is not { IsSettings: true }) {
                    Editor.Focus();
                }
                return true;

            // -- Stubs (not yet implemented) --
            case CommandIds.NavGoToLine:
            case CommandIds.EditSelectAllOccurrences:
            case CommandIds.EditColumnSelect:
                return true;

            default:
                return false;
        }
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

    private async void SaveAll() {
        // Save All: save every tab that has a file path and is dirty.
        foreach (var tab in _tabs) {
            if (tab.FilePath != null && tab.IsDirty) {
                var sha1 = await SaveToAsync(tab.FilePath);
                if (sha1 is null) continue;
                tab.BaseSha1 = sha1;
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
        SetMenuGesture(MenuNew, CommandIds.FileNew);
        SetMenuGesture(MenuOpen, CommandIds.FileOpen);
        SetMenuGesture(MenuSave, CommandIds.FileSave);
        SetMenuGesture(MenuSaveAs, CommandIds.FileSaveAs);
        SetMenuGesture(MenuSaveAll, CommandIds.FileSaveAll);
        SetMenuGesture(MenuClose, CommandIds.FileClose);
        SetMenuGesture(MenuCloseAll, CommandIds.FileCloseAll);
        SetMenuGesture(MenuExit, CommandIds.FileExit);
        SetMenuGesture(MenuUndo, CommandIds.EditUndo);
        SetMenuGesture(MenuRedo, CommandIds.EditRedo);
        SetMenuGesture(MenuCut, CommandIds.EditCut);
        SetMenuGesture(MenuCopy, CommandIds.EditCopy);
        SetMenuGesture(MenuPaste, CommandIds.EditPaste);
        SetMenuGesture(MenuPasteMore, CommandIds.EditPaste); // PasteMore shares for now
        SetMenuGesture(MenuDelete, CommandIds.EditDelete);
        SetMenuGesture(MenuSelectAll, CommandIds.EditSelectAll);
        SetMenuGesture(MenuSelectWord, CommandIds.EditSelectWord);
        SetMenuGesture(MenuDeleteLine, CommandIds.EditDeleteLine);
        SetMenuGesture(MenuMoveLineUp, CommandIds.EditMoveLineUp);
        SetMenuGesture(MenuMoveLineDown, CommandIds.EditMoveLineDown);
        SetMenuGesture(MenuCaseUpper, CommandIds.EditUpperCase);
        SetMenuGesture(MenuCaseLower, CommandIds.EditLowerCase);
        SetMenuGesture(MenuCaseProper, CommandIds.EditProperCase);
        SetMenuGesture(MenuInsertLineBelow, CommandIds.EditInsertLineBelow);
        SetMenuGesture(MenuInsertLineAbove, CommandIds.EditInsertLineAbove);
        SetMenuGesture(MenuDuplicateLine, CommandIds.EditDuplicateLine);
        SetMenuGesture(MenuDeleteWordLeft, CommandIds.EditDeleteWordLeft);
        SetMenuGesture(MenuDeleteWordRight, CommandIds.EditDeleteWordRight);
        SetMenuGesture(MenuIndent, CommandIds.EditIndent);
        SetMenuGesture(MenuSelectAllOccurrences, CommandIds.EditSelectAllOccurrences);
        SetMenuGesture(MenuColumnSelect, CommandIds.EditColumnSelect);
        SetMenuGesture(MenuFind, CommandIds.FindFind);
        SetMenuGesture(MenuReplace, CommandIds.FindReplace);
        SetMenuGesture(MenuFindNext, CommandIds.FindFindNext);
        SetMenuGesture(MenuFindPrevious, CommandIds.FindFindPrevious);
        SetMenuGesture(MenuFindWordOrSel, CommandIds.FindFindWordOrSel);
        SetMenuGesture(MenuIncrementalSearch, CommandIds.FindIncrementalSearch);
        SetMenuGesture(MenuGoToLine, CommandIds.NavGoToLine);
        SetMenuGesture(MenuLineNumbers, CommandIds.ViewLineNumbers);
        SetMenuGesture(MenuStatusBar, CommandIds.ViewStatusBar);
        SetMenuGesture(MenuWrapLines, CommandIds.ViewWrapLines);
        SetMenuGesture(MenuZoomIn, CommandIds.ViewZoomIn);
        SetMenuGesture(MenuZoomOut, CommandIds.ViewZoomOut);
        SetMenuGesture(MenuZoomReset, CommandIds.ViewZoomReset);
        SetMenuGesture(MenuScrollLineUp, CommandIds.NavScrollLineUp);
        SetMenuGesture(MenuScrollLineDown, CommandIds.NavScrollLineDown);
        SetMenuGesture(MenuRevertFile, CommandIds.FileRevertFile);
        SetMenuGesture(MenuCommandPalette, CommandIds.WindowCommandPalette);
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
        MenuUndo.Click += (_, _) => Editor.PerformUndo();
        MenuRedo.Click += (_, _) => Editor.PerformRedo();
        MenuCut.Click += async (_, _) => await Editor.CutAsync();
        MenuCopy.Click += async (_, _) => await Editor.CopyAsync();
        MenuPaste.Click += async (_, _) => await Editor.PasteAsync();
        MenuDelete.Click += (_, _) => Editor.EditDelete();
        MenuSelectAll.Click += (_, _) => Editor.PerformSelectAll();
        MenuSelectWord.Click += (_, _) => Editor.PerformSelectWord();
        MenuDeleteLine.Click += (_, _) => Editor.PerformDeleteLine();
        MenuMoveLineUp.Click += (_, _) => Editor.PerformMoveLineUp();
        MenuMoveLineDown.Click += (_, _) => Editor.PerformMoveLineDown();
        MenuCaseUpper.Click += (_, _) => Editor.PerformTransformCase(CaseTransform.Upper);
        MenuCaseLower.Click += (_, _) => Editor.PerformTransformCase(CaseTransform.Lower);
        MenuCaseProper.Click += (_, _) => Editor.PerformTransformCase(CaseTransform.Proper);
        MenuInsertLineBelow.Click += (_, _) => Editor.ExecuteCommand(CommandIds.EditInsertLineBelow);
        MenuInsertLineAbove.Click += (_, _) => Editor.ExecuteCommand(CommandIds.EditInsertLineAbove);
        MenuDuplicateLine.Click += (_, _) => Editor.ExecuteCommand(CommandIds.EditDuplicateLine);
        MenuDeleteWordLeft.Click += (_, _) => Editor.ExecuteCommand(CommandIds.EditDeleteWordLeft);
        MenuDeleteWordRight.Click += (_, _) => Editor.ExecuteCommand(CommandIds.EditDeleteWordRight);
        MenuIndent.Click += (_, _) => Editor.ExecuteCommand(CommandIds.EditIndent);
        MenuSelectAllOccurrences.Click += (_, _) => ExecuteWindowCommand(CommandIds.EditSelectAllOccurrences);
        MenuColumnSelect.Click += (_, _) => ExecuteWindowCommand(CommandIds.EditColumnSelect);
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
        MenuBarBorder.BorderBrush = theme.TabBarBackground;
        MenuBar.Background = Brushes.Transparent;
        GearGlyph.Text = IconGlyphs.Settings;
        GearGlyph.FontFamily = IconGlyphs.Family;
        GearGlyph.Foreground = theme.TabPlusForeground;
        GearButton.Background = Brushes.Transparent;

        // Status bar
        StatusBar.Background = theme.StatusBarBackground;
        StatusBar.BorderBrush = theme.StatusBarBorder;
        StatsBar.Foreground = theme.StatusBarForeground;
        StatsBarIO.Foreground = theme.StatusBarForeground;
        StatusLeft.Foreground = theme.StatusBarForeground;
        StatusRight.Foreground = theme.StatusBarForeground;
        StatusLineEnding.Foreground = theme.StatusBarForeground;
        StatusRightSuffix.Foreground = theme.StatusBarForeground;
    }

    // -------------------------------------------------------------------------
    // Scroll bar wiring
    // -------------------------------------------------------------------------

    private void WireScrollBar() {
        // Give the editor a reference so it can drive middle-drag visuals.
        Editor.ScrollBar = ScrollBar;

        // Editor → ScrollBar: push scroll state whenever it changes
        Editor.ScrollChanged += (_, _) => SyncScrollBarFromEditor();

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

    private void SyncScrollBarFromEditor() {
        ScrollBar.Maximum = Editor.ScrollMaximum;
        ScrollBar.Value = Editor.ScrollValue;
        ScrollBar.ViewportSize = Editor.ScrollViewportHeight;
        ScrollBar.ExtentSize = Editor.ScrollExtentHeight;
        ScrollBar.RowHeight = Editor.RowHeightValue;
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

        // Wrap Lines + column limit
        Editor.WrapLines = _settings.WrapLines;
        Editor.WrapLinesAt = _settings.WrapLinesAt;
        _wrapLinesGlyph = CreateMenuCheckGlyph(_settings.WrapLines);
        MenuWrapLines.Icon = _wrapLinesGlyph;
        MenuWrapLines.Click += (_, _) => ToggleWrapLines();

        // Zoom
        MenuZoomIn.Click += (_, _) => ExecuteWindowCommand(CommandIds.ViewZoomIn);
        MenuZoomOut.Click += (_, _) => ExecuteWindowCommand(CommandIds.ViewZoomOut);
        MenuZoomReset.Click += (_, _) => ExecuteWindowCommand(CommandIds.ViewZoomReset);
        MenuScrollLineUp.Click += (_, _) => Editor.ExecuteCommand(CommandIds.NavScrollLineUp);
        MenuScrollLineDown.Click += (_, _) => Editor.ExecuteCommand(CommandIds.NavScrollLineDown);

        // Undo coalesce idle timer (settings-only, no menu item)
        Editor.CoalesceTimerMs = _settings.CoalesceTimerMs;
        Editor.ExpandSelectionMode = _settings.ExpandSelectionMode;

        UpdateStatusBarVisibility();
    }

    private void WireSearchMenu() {
        MenuFind.Click += (_, _) => ExecuteWindowCommand(CommandIds.FindFind);
        MenuReplace.Click += (_, _) => ExecuteWindowCommand(CommandIds.FindReplace);
        MenuFindNext.Click += (_, _) => ExecuteWindowCommand(CommandIds.FindFindNext);
        MenuFindPrevious.Click += (_, _) => ExecuteWindowCommand(CommandIds.FindFindPrevious);
        MenuFindWordOrSel.Click += (_, _) => ExecuteWindowCommand(CommandIds.FindFindWordOrSel);
        MenuIncrementalSearch.Click += (_, _) => ExecuteWindowCommand(CommandIds.FindIncrementalSearch);
        MenuGoToLine.Click += (_, _) => ExecuteWindowCommand(CommandIds.NavGoToLine);
        MenuCommandPalette.Click += (_, _) => OpenCommandPalette();
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

    private static readonly Avalonia.Media.IBrush MixedLineEndingBrush =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(0xE0, 0x40, 0x40));

    private void UpdateStatusBar() {
        // -- Permanent status bar (always visible) --
        var doc = Editor.Document;
        if (doc == null) {
            if (StatusRight.Text != "Ln 1 Ch 1") StatusRight.Text = "Ln 1 Ch 1";
            if (StatusLineEnding.Text != "") StatusLineEnding.Text = "";
            if (StatusRightSuffix.Text != "") StatusRightSuffix.Text = "";
            if (_chordFirst == null && StatusLeft.Text != "") StatusLeft.Text = "";
        } else {
            var table = doc.Table;
            var stillLoading = table.Buffer is { LengthIsKnown: false };

            var lineCount = table.LineCount;

            var lcText = lineCount >= 0 ? $"{lineCount:N0}" : "\u2014";
            var lcWidth = lcText.Length;

            var right = "";
            // During loading, line-start lookups can fail (pages not in memory).
            if (!stillLoading) {
                var caret = Math.Min(doc.Selection.Caret, table.Length);
                var lineIdx = table.LineFromOfs(caret);
                var lineStart = table.LineStartOfs(lineIdx);
                var col = caret - lineStart + 1;
                var line = lineIdx + 1;

                // Pad Ln to the width of the line-count string so the field
                // doesn't jitter as the caret moves.  Use the longest line in
                // the document to size the Ch field (grows during loading then
                // stabilises).  Falls back to lcWidth for huge documents where
                // the line index isn't built.
                var lnText = $"{line:N0}".PadLeft(lcWidth);
                var maxLineLen = table.MaxLineLength;
                var chWidth = maxLineLen > 0 ? $"{maxLineLen:N0}".Length : lcWidth;
                var chText = $"{col:N0}".PadLeft(chWidth);
                right = $"Ln {lnText} Ch {chText}";
            }

            if (stillLoading) {
                right += $" | {lcText} lines | loading\u2026";
                if (StatusRight.Text != right) StatusRight.Text = right;
                if (StatusLineEnding.Text != "") StatusLineEnding.Text = "";
                if (StatusRightSuffix.Text != "") StatusRightSuffix.Text = "";
            } else {
                right += $" | {lcText} lines | UTF-8 | ";
                if (StatusRight.Text != right) StatusRight.Text = right;

                var lei = doc.LineEndingInfo;
                var leLabel = lei.Label;
                if (StatusLineEnding.Text != leLabel) StatusLineEnding.Text = leLabel;

                var leBrush = lei.IsMixed ? MixedLineEndingBrush : _theme.StatusBarForeground;
                if (StatusLineEnding.Foreground != leBrush) StatusLineEnding.Foreground = leBrush;

                const string sfx = " | Spaces";
                if (StatusRightSuffix.Text != sfx) StatusRightSuffix.Text = sfx;
            }
        }
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

    private async void LoadManual() {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        var path = Path.Combine(dir, "manual.md");
        if (File.Exists(path)) {
            try {
                var result = await FileLoader.LoadAsync(path);
                var tab = new TabState(result.Document, path, "manual.md") {
                    LoadResult = result,
                    IsLoading = true,
                };
                AddTab(tab);
                SwitchToTab(tab);
                WireFileLoadCompletion(tab);
                return;
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // Fall through to untitled tab.
            }
        }
        var fallback = AddTab(TabState.CreateUntitled(_tabs));
        SwitchToTab(fallback);
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
        if (_activeTab == null || _activeTab.IsSettings) return;
        Editor.FlushCompound();
        if (_activeTab.FilePath is null) {
            await SaveAsAsync();
            return;
        }
        var sha1 = await SaveToAsync(_activeTab.FilePath);
        if (sha1 is null) return;
        _activeTab.BaseSha1 = sha1;
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
                    MenuFile.IsSubMenuOpen = false;
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
                    MenuFile.IsSubMenuOpen = false;
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
        var tab = new TabState(result.Document, path, result.DisplayName) {
            LoadResult = result,
            IsLoading = true,
        };
        AddTab(tab);
        SwitchToTab(tab);
        WireStreamingProgress(sw, tab);
        WireFileLoadCompletion(tab);

        PushRecentFile(path);
    }

    private void OpenDevSample(ProceduralSample sample) {
        var sw = Stopwatch.StartNew();
        var doc = sample.CreateDocument();
        sw.Stop();

        var tab = new TabState(doc, null, sample.DisplayName);
        AddTab(tab);
        SwitchToTab(tab);
        Editor.PerfStats.LoadTimeMs = sw.Elapsed.TotalMilliseconds;
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
        var sha1 = await SaveToAsync(path);
        if (sha1 is null) return;
        _activeTab.BaseSha1 = sha1;
        _activeTab.Document.MarkSavePoint();
        _activeTab.FilePath = path;
        _activeTab.DisplayName = Path.GetFileName(path);
        _activeTab.IsDirty = false;
        PushRecentFile(path);
        UpdateTabBar();
    }

    private async Task<string?> SaveToAsync(string path) {
        if (Editor.Document is null) {
            return null;
        }
        try {
            var sw = Stopwatch.StartNew();
            var sha1 = await FileSaver.SaveAsync(Editor.Document, path);
            sw.Stop();
            Editor.PerfStats.SaveTimeMs = sw.Elapsed.TotalMilliseconds;
            return sha1;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            StatusLeft.Text = $"Save failed: {ex.Message}";
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
        GearButton.PointerPressed += (_, _) => OpenSettings();
        GearButton.PointerEntered += (_, _) => GearButton.Background = _theme.TabInactiveHoverBg;
        GearButton.PointerExited += (_, _) => GearButton.Background = Brushes.Transparent;

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
                case "DevMode":
                    UpdateStatusBarVisibility();
                    RebuildRecentMenu();
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
        var palette = new CommandPaletteWindow(_keyBindings, _theme);
        await palette.ShowDialog(this);

        if (palette.SelectedCommandId is { } cmdId) {
            // Dispatch the chosen command through the normal path.
            if (!ExecuteWindowCommand(cmdId)) {
                if (_activeTab is not { IsSettings: true }) {
                    Editor.ExecuteCommand(cmdId);
                }
            }
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
        // When already open and re-invoked, focus the appropriate box.
        if (replaceMode) {
            FindBar.FocusReplaceBox();
        } else {
            FindBar.FocusSearchBox();
        }
    }

    private void OpenFindBarWithSelection() {
        var doc = _activeTab is not { IsSettings: true } ? Editor.Document : null;
        if (doc != null && !doc.Selection.IsEmpty) {
            FindBar.SetSearchTerm(doc.GetSelectedText());
        }
        OpenFindBar(replaceMode: false);
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

        // Provide history lists from settings (shared reference — mutations
        // by AppSettings.PushRecentTerm update the same list).
        SyncFindBarHistory();

        // Push search/replace terms into history when a find/replace is executed.
        FindBar.FindRequested += _ => {
            _settings.PushRecentFindTerm(FindBar.SearchTerm);
            SyncFindBarHistory();
            _settings.ScheduleSave();
        };
        FindBar.ReplaceRequested += _ => {
            _settings.PushRecentReplaceTerm(FindBar.ReplaceTerm);
            SyncFindBarHistory();
            _settings.ScheduleSave();
        };
        FindBar.ReplaceAllRequested += () => {
            _settings.PushRecentReplaceTerm(FindBar.ReplaceTerm);
            SyncFindBarHistory();
            _settings.ScheduleSave();
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
    // Conflict resolution (error icon on tab)
    // -----------------------------------------------------------------

    /// <summary>
    /// Handles the "Discard" action from the conflict context menu.
    /// For missing files: closes the tab. For changed files: clears the
    /// conflict flag and accepts the disk version.
    /// </summary>
    private void HandleConflictDiscard(int tabIndex) {
        if (tabIndex < 0 || tabIndex >= _tabs.Count) return;
        var tab = _tabs[tabIndex];
        if (tab.Conflict is null) return;

        if (tab.Conflict.Kind == SessionStore.FileConflictKind.Missing) {
            CloseTabDirect(tab);
        } else {
            // Changed — the tab already has the disk version loaded.
            tab.Conflict = null;
            tab.IsDirty = false;
            UpdateTabBar();
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
