using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using DevMentalMd.App.Services;
using DevMentalMd.Core.Buffers;
using DevMentalMd.Core.Documents;
using DevMentalMd.Core.IO;

namespace DevMentalMd.App;

public partial class MainWindow : Window {
    private readonly List<TabState> _tabs = [];
    private TabState? _activeTab;
    private readonly RecentFilesStore _recentFiles = RecentFilesStore.Load();
    private readonly AppSettings _settings = AppSettings.Load();
    private int _staticMenuItemCount;
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private EditorTheme _theme = EditorTheme.Light;
    private bool _windowStateReady;

    public MainWindow() {
        InitializeComponent();
        RestoreWindowSize();

        MenuNew.Click += OnNew;
        MenuOpen.Click += OnOpen;
        MenuSave.Click += OnSave;
        MenuSaveAs.Click += OnSaveAs;
        MenuClose.Click += (_, _) => { if (_activeTab != null) CloseTab(_activeTab); };
        MenuCloseAll.Click += (_, _) => CloseAllTabs();

        _staticMenuItemCount = MenuFile.Items.Count;

        RebuildRecentMenu();
        WireScrollBar();
        WireViewMenu();
        WireThemeMenu();
        WireTabBar();
        WireStatsBar();
        WireWindowState();

        // The editor is the only focusable control.  Grab focus on
        // activation and reclaim it whenever anything else receives focus.
        Activated += (_, _) => Editor.Focus();
        GotFocus += (_, e) => {
            if (e.Source != Editor) {
                Editor.Focus();
            }
        };

        if (_settings.DevMode) {
            LoadManual();
        } else {
            var tab = AddTab(TabState.CreateUntitled(_tabs));
            SwitchToTab(tab);
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
        // Save current tab's scroll state.
        if (_activeTab != null) {
            Editor.SaveScrollState(_activeTab);
        }
        _activeTab = tab;
        Editor.Document = tab.Document;
        Editor.RestoreScrollState(tab);
        UpdateTabBar();
    }

    private void CloseTab(TabState tab) {
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

    private void CloseAllTabs() {
        _tabs.Clear();
        _activeTab = null;
        var tab = AddTab(TabState.CreateUntitled(_tabs));
        SwitchToTab(tab);
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
        TabBar.TabCloseClicked += idx => {
            if (idx >= 0 && idx < _tabs.Count) CloseTab(_tabs[idx]);
        };
        TabBar.PlusClicked += () => {
            var t = AddTab(TabState.CreateUntitled(_tabs));
            SwitchToTab(t);
        };
        TabBar.OverflowClicked += ShowOverflowMenu;
        TabBar.CloseTabsToRightClicked += CloseTabsToRight;
        TabBar.CloseOtherTabsClicked += CloseOtherTabs;
        TabBar.TabReordered += OnTabReordered;
        TabBar.DragAreaPressed += () => {
            // BeginMoveDrag is called from within a PointerPressed handler,
            // so the pointer is already captured. This initiates OS-level
            // window drag.
            BeginMoveDrag(TabBar.LastPointerPressedArgs!);
        };
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

    private void CloseTabsToRight(int tabIndex) {
        if (tabIndex < 0 || tabIndex >= _tabs.Count) return;
        // Close all tabs with index > tabIndex
        for (var i = _tabs.Count - 1; i > tabIndex; i--) {
            _tabs.RemoveAt(i);
        }
        // If active tab was removed, switch to the rightmost remaining
        if (_activeTab != null && !_tabs.Contains(_activeTab)) {
            SwitchToTab(_tabs[^1]);
        } else {
            UpdateTabBar();
        }
    }

    private void CloseOtherTabs(int tabIndex) {
        if (tabIndex < 0 || tabIndex >= _tabs.Count) return;
        var keep = _tabs[tabIndex];
        _tabs.Clear();
        _tabs.Add(keep);
        if (_activeTab == keep) {
            UpdateTabBar();
        } else {
            SwitchToTab(keep);
        }
    }

    private void OnTabReordered(int fromIndex, int toIndex) {
        if (fromIndex < 0 || fromIndex >= _tabs.Count) return;
        if (toIndex < 0 || toIndex >= _tabs.Count) return;
        if (fromIndex == toIndex) return;
        var tab = _tabs[fromIndex];
        _tabs.RemoveAt(fromIndex);
        _tabs.Insert(toIndex, tab);
        UpdateTabBar();
    }

    // -------------------------------------------------------------------------
    // Keyboard shortcuts (tab switching)
    // -------------------------------------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e) {
        base.OnKeyDown(e);
        if (e.Key == Key.F4 && e.KeyModifiers.HasFlag(KeyModifiers.Control)) {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) {
                CloseAllTabs();
            } else if (_activeTab != null) {
                CloseTab(_activeTab);
            }
            e.Handled = true;
        } else if (e.Key == Key.Tab && e.KeyModifiers.HasFlag(KeyModifiers.Control)) {
            if (_tabs.Count <= 1) {
                e.Handled = true;
                return;
            }
            var idx = _activeTab != null ? _tabs.IndexOf(_activeTab) : 0;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) {
                idx = (idx - 1 + _tabs.Count) % _tabs.Count;
            } else {
                idx = (idx + 1) % _tabs.Count;
            }
            SwitchToTab(_tabs[idx]);
            e.Handled = true;
        }
    }

    // -------------------------------------------------------------------------
    // Theme
    // -------------------------------------------------------------------------

    private void WireThemeMenu() {
        MenuThemeSystem.ToggleType = MenuItemToggleType.CheckBox;
        MenuThemeLight.ToggleType = MenuItemToggleType.CheckBox;
        MenuThemeDark.ToggleType = MenuItemToggleType.CheckBox;

        UpdateThemeMenuChecks();

        MenuThemeSystem.Click += (_, _) => SetThemeMode(ThemeMode.System);
        MenuThemeLight.Click += (_, _) => SetThemeMode(ThemeMode.Light);
        MenuThemeDark.Click += (_, _) => SetThemeMode(ThemeMode.Dark);

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
        UpdateThemeMenuChecks();
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

    private void UpdateThemeMenuChecks() {
        MenuThemeSystem.IsChecked = _settings.ThemeMode == ThemeMode.System;
        MenuThemeLight.IsChecked = _settings.ThemeMode == ThemeMode.Light;
        MenuThemeDark.IsChecked = _settings.ThemeMode == ThemeMode.Dark;
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

        // Tab bar
        TabBar.ApplyTheme(theme);

        // Menu bar — match the active tab background so it blends
        MenuBar.Background = theme.TabActiveBackground;
        MenuBarBorder.BorderBrush = theme.TabBarBackground;

        // Status bar
        StatusBar.Background = theme.StatusBarBackground;
        StatusBar.BorderBrush = theme.StatusBarBorder;
        StatsBar.Foreground = theme.StatusBarForeground;
        StatsBarIO.Foreground = theme.StatusBarForeground;
        StatusLeft.Foreground = theme.StatusBarForeground;
        StatusRight.Foreground = theme.StatusBarForeground;
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

    private void WireViewMenu() {
        // Line Numbers
        Editor.ShowLineNumbers = _settings.ShowLineNumbers;
        MenuLineNumbers.ToggleType = MenuItemToggleType.CheckBox;
        MenuLineNumbers.IsChecked = _settings.ShowLineNumbers;
        MenuLineNumbers.Click += (_, _) => {
            var show = !_settings.ShowLineNumbers;
            _settings.ShowLineNumbers = show;
            _settings.ScheduleSave();
            Editor.ShowLineNumbers = show;
            MenuLineNumbers.IsChecked = show;
        };

        // Status Bar
        MenuStatusBar.ToggleType = MenuItemToggleType.CheckBox;
        MenuStatusBar.IsChecked = _settings.ShowStatusBar;
        MenuStatusBar.Click += (_, _) => {
            var show = !_settings.ShowStatusBar;
            _settings.ShowStatusBar = show;
            _settings.ScheduleSave();
            MenuStatusBar.IsChecked = show;
            UpdateStatusBarVisibility();
        };

        // Statistics
        MenuStatistics.ToggleType = MenuItemToggleType.CheckBox;
        MenuStatistics.IsChecked = _settings.ShowStatistics;
        MenuStatistics.Click += (_, _) => {
            var show = !_settings.ShowStatistics;
            _settings.ShowStatistics = show;
            _settings.ScheduleSave();
            MenuStatistics.IsChecked = show;
            UpdateStatusBarVisibility();
        };

        // Wrap Lines + column limit
        Editor.WrapLines = _settings.WrapLines;
        Editor.WrapLinesAt = _settings.WrapLinesAt;
        MenuWrapLines.ToggleType = MenuItemToggleType.CheckBox;
        MenuWrapLines.IsChecked = _settings.WrapLines;
        MenuWrapLines.Click += (_, _) => {
            var wrap = !_settings.WrapLines;
            _settings.WrapLines = wrap;
            _settings.ScheduleSave();
            Editor.WrapLines = wrap;
            MenuWrapLines.IsChecked = wrap;
        };

        UpdateStatusBarVisibility();
    }

    // -------------------------------------------------------------------------
    // Stats / status bar
    // -------------------------------------------------------------------------

    /// <summary>
    /// Shows or hides the status bar sections based on current settings.
    /// The whole <c>StatusBar</c> border is hidden only when both the
    /// permanent bar and the stats bars are turned off.
    /// </summary>
    private void UpdateStatusBarVisibility() {
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

    private void UpdateStatusBar() {
        // -- Permanent status bar (always visible) --
        var doc = Editor.Document;
        if (doc == null) {
            if (StatusRight.Text != "Ln 1 Ch 1") StatusRight.Text = "Ln 1 Ch 1";
            if (StatusLeft.Text != "") StatusLeft.Text = "";
        } else {
            var table = doc.Table;
            var stillLoading = table.Buffer is { LengthIsKnown: false };

            var lineCount = table.LineCount;

            var lcText = lineCount >= 0 ? $"{lineCount:N0}" : "\u2014";
            var lcWidth = lcText.Length;

            var right = "";
            // During loading, line-start lookups can fail (pages not in memory).
            if (!stillLoading) {
                var caret = doc.Selection.Caret;
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

            var suffix = stillLoading ? " | loading\u2026" : " | UTF-8 | LF | Spaces";
            right += $" | {lcText} lines{suffix}";
            if (StatusRight.Text != right) StatusRight.Text = right;
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

    private void LoadManual() {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        var path = Path.Combine(dir, "manual.md");
        if (File.Exists(path)) {
            var result = FileLoader.Load(path);
            var tab = new TabState(result.Document, path, "manual.md") {
                LoadResult = result,
            };
            AddTab(tab);
            SwitchToTab(tab);
        } else {
            var tab = AddTab(TabState.CreateUntitled(_tabs));
            SwitchToTab(tab);
        }
    }

    private async void OnOpen(object? sender, RoutedEventArgs e) {
        string? path;
        if (UseNativePicker) {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
                Title = "Open File",
                AllowMultiple = false,
                FileTypeFilter = [
                    new FilePickerFileType("All files") { Patterns = ["*.*"] },
                    new FilePickerFileType("Markdown") { Patterns = ["*.md", "*.markdown"] }
                ]
            });
            if (files.Count == 0) {
                return;
            }
            path = files[0].TryGetLocalPath();
        } else {
            path = await LinuxFileDialog.OpenAsync("Open Markdown File");
        }
        if (path is null) {
            return;
        }

        await OpenFileInTabAsync(path);
    }

    private async void OnSave(object? sender, RoutedEventArgs e) {
        if (_activeTab == null) return;
        if (_activeTab.FilePath is null) {
            await SaveAsAsync();
            return;
        }
        await SaveToAsync(_activeTab.FilePath);
        _activeTab.Document.MarkSavePoint();
        _activeTab.IsDirty = false;
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
                ToolTip.SetTip(item, captured);
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

        var sw = Stopwatch.StartNew();
        var result = await FileLoader.LoadAsync(path, _settings.PagedBufferThresholdBytes);

        var tab = new TabState(result.Document, path, result.DisplayName) {
            LoadResult = result,
        };
        AddTab(tab);
        SwitchToTab(tab);
        WireStreamingProgress(sw, tab);

        _recentFiles.Push(path);
        _recentFiles.Save();
        RebuildRecentMenu();
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

    // -------------------------------------------------------------------------
    // Save helpers
    // -------------------------------------------------------------------------

    private async Task SaveAsAsync() {
        if (_activeTab == null) return;

        // For zip files, suggest the inner entry name (e.g., "model.xml" not "model.zip").
        string suggestedName;
        if (_activeTab.LoadResult is { WasZipped: true, InnerEntryName: { } innerName }) {
            suggestedName = innerName;
        } else if (_activeTab.FilePath is not null) {
            suggestedName = Path.GetFileName(_activeTab.FilePath);
        } else {
            suggestedName = "untitled.txt";
        }

        string? path;
        if (UseNativePicker) {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
                Title = "Save File",
                SuggestedFileName = suggestedName,
                FileTypeChoices = [
                    new FilePickerFileType("All files") { Patterns = ["*.*"] },
                    new FilePickerFileType("Markdown") { Patterns = ["*.md"] }
                ]
            });
            if (file is null) {
                return;
            }
            path = file.TryGetLocalPath();
        } else {
            path = await LinuxFileDialog.SaveAsync("Save Markdown File", suggestedName);
        }
        if (path is null) {
            return;
        }

        await SaveToAsync(path);
        _activeTab.Document.MarkSavePoint();
        _activeTab.FilePath = path;
        _activeTab.DisplayName = Path.GetFileName(path);
        _activeTab.IsDirty = false;
        UpdateTabBar();
    }

    private async Task SaveToAsync(string path) {
        if (Editor.Document is null) {
            return;
        }
        var sw = Stopwatch.StartNew();
        await FileSaver.SaveAsync(Editor.Document, path);
        sw.Stop();
        Editor.PerfStats.SaveTimeMs = sw.Elapsed.TotalMilliseconds;
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

            buf.ProgressChanged += () => {
                Dispatcher.UIThread.Post(() => {
                    if (_activeTab != tab) return;
                    if (!firstChunkCaptured) {
                        firstChunkCaptured = true;
                        Editor.PerfStats.FirstChunkTimeMs = sw.Elapsed.TotalMilliseconds;
                    }
                    Editor.PerfStats.LoadTimeMs = sw.Elapsed.TotalMilliseconds;
                    Editor.InvalidateLayout();
                }, DispatcherPriority.Background);
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
        };
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
