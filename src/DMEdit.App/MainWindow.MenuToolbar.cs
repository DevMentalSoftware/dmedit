using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using DMEdit.App.Commands;
using Cmd = DMEdit.App.Commands.Commands;
using DMEdit.App.Controls;
using DMEdit.App.Services;
using DMEdit.App.Settings;
using DMEdit.Core.Documents;
using System.Runtime.InteropServices;
using Avalonia.Controls.Presenters;
using Avalonia.Styling;
using DMEdit.Core.Buffers;

namespace DMEdit.App;

// Menu + toolbar + command registration partial of MainWindow.
// Owns RegisterWindowCommands (the big Wire() block), menu gesture
// sync, advanced-menu visibility, toolbar / tab-toolbar init and
// overflow, and the small menu-state toggle helpers (ToggleLineNumbers,
// ToggleWrapLines, etc.) used by the View menu.
public partial class MainWindow {


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
        Cmd.FilePrint.Wire(() => {
            if (WindowsPrintService.IsAvailable) {
                _ = PrintAsync();
                return;
            }
            // Surface the discovery reason so a silent-no-op click never
            // happens again — if DMEdit.Windows.dll fails to load (stale
            // build, missing runtime pack, wrong OS) the user sees why.
            StatusLeft.Text = "Print unavailable: "
                + (WindowsPrintService.DiscoveryError ?? "reason unknown.");
        });
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
        Cmd.ViewZoomIn.Wire(() => ApplyZoom(_settings.ZoomPercent + 10));
        Cmd.ViewZoomOut.Wire(() => ApplyZoom(_settings.ZoomPercent - 10));
        Cmd.ViewZoomReset.Wire(() => ApplyZoom(100));

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

    private void ApplyZoom(int percent) {
        percent = Math.Clamp(percent, 10, 800);
        _settings.ZoomPercent = percent;
        _settings.ScheduleSave();
        Editor.ZoomPercent = percent;
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

    /// <summary>
    /// Checks whether a tab's document should use character-wrapping mode
    /// based on longest line and file size.
    /// </summary>
    private bool ShouldCharWrap(TabState tab) {
        if (tab.LoadResult?.Buffer is not PagedFileBuffer pb) return false;
        var fileSizeKb = pb.ByteLength / 1024.0;
        return fileSizeKb >= _settings.CharWrapFileSizeKB
            && pb.LongestLine >= PieceTable.MaxGetTextLength;
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
        StatusUpdateGlyph.Foreground = theme.StatusBarWarning;
        StatusUpdateText.Foreground = theme.StatusBarWarning;
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
        Editor.ZoomPercent = _settings.ZoomPercent;

        // Wrap Lines + column limit
        Editor.WrapLines = _settings.WrapLines;
        Editor.UseWrapColumn = _settings.UseWrapColumn;
        Editor.WrapLinesAt = _settings.WrapLinesAt;
        _wrapLinesGlyph = CreateMenuCheckGlyph(_settings.WrapLines);
        MenuWrapLines.Icon = _wrapLinesGlyph;

        // Show Whitespace + Wrap Symbol + Hanging Indent
        Editor.ShowWhitespace = _settings.ShowWhitespace;
        Editor.ShowWrapSymbol = _settings.ShowWrapSymbol;
        Editor.HangingIndent = _settings.HangingIndent;
        Editor.UseFastTextLayout = _settings.UseFastTextLayout;
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
}
