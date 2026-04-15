using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using DMEdit.App.Commands;
using Cmd = DMEdit.App.Commands.Commands;
using DMEdit.App.Controls;
using DMEdit.App.Services;
using DMEdit.Core.Documents;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media;
using DMEdit.App.Settings;

namespace DMEdit.App;

// Dialogs + find bar partial of MainWindow.  Wires the settings
// panel tab, opens the command palette / clipboard ring / go-to-line
// dialogs, and owns the whole find bar surface (open/close, toggle
// persistence, match counting, recent-term history).
public partial class MainWindow {


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
                case "UseWrapColumn":
                    Editor.UseWrapColumn = _settings.UseWrapColumn;
                    break;
                case "WrapLinesAt":
                    Editor.WrapLinesAt = _settings.WrapLinesAt;
                    break;
                case "ShowWrapSymbol":
                    Editor.ShowWrapSymbol = _settings.ShowWrapSymbol;
                    break;
                case "HangingIndent":
                    Editor.HangingIndent = _settings.HangingIndent;
                    break;
                case "UseFastTextLayout":
                    Editor.UseFastTextLayout = _settings.UseFastTextLayout;
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
                // No case for ExpandSelectionMode, MaxRegexMatchLength, or
                // DistributeColumnPaste — these are passive settings, read
                // directly from Editor.Settings at the call site.  See the
                // EditorControl.Settings doc comment for the active vs passive
                // distinction.
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
                    break;
                case "TailFile":
                    UpdateTailButton();
                    break;
                case "HideAdvancedMenus":
                    ApplyAdvancedMenuVisibility();
                    break;
            }
        };

        // "Update to …" button in Advanced settings — downloads then restarts.
        SettingsPanel.CheckForUpdatesRequested += () => {
            if (_updateService.IsReadyToApply) {
                _updateService.ApplyAndRestart();
                return;
            }
            SettingsPanel.SetUpdateDownloading();
            _ = Task.Run(async () => {
                try {
                    await _updateService.DownloadAsync();
                    Dispatcher.UIThread.Post(() => _updateService.ApplyAndRestart());
                } catch {
                    Dispatcher.UIThread.Post(() => {
                        if (_updateService.AvailableVersion is { } v)
                            SettingsPanel.SetUpdateAvailable(v, downloaded: false);
                    });
                }
            });
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
            if (Editor.CharWrapMode && Editor.CharsPerRow > 0) {
                var caret = Math.Min(doc.Selection.Caret, doc.Table.Length);
                currentLine = caret / Editor.CharsPerRow + 1;
            } else {
                var table = doc.Table;
                var caret = Math.Min(doc.Selection.Caret, table.Length);
                currentLine = table.LineFromOfs(caret) + 1;
            }
        }

        var dialog = new GoToLineWindow(_theme, currentLine);
        await dialog.ShowDialog(this);

        if (dialog.TargetLine is { } targetLine && doc != null) {
            if (Editor.CharWrapMode && Editor.CharsPerRow > 0) {
                // In char-wrap mode, targetLine = row number, targetCol = column.
                var cpr = Editor.CharsPerRow;
                var rowStart = (targetLine - 1) * cpr;
                var pos = rowStart;
                if (dialog.TargetCol is { } targetCol) {
                    pos += Math.Clamp(targetCol - 1, 0, cpr - 1);
                }
                pos = Math.Clamp(pos, 0, doc.Table.Length);
                Editor.GoToPosition(pos);
            } else {
                var table = doc.Table;
                var lineIdx = Math.Clamp(targetLine - 1, 0, table.LineCount - 1);
                var lineStart = table.LineStartOfs(lineIdx);
                var pos = lineStart;
                if (dialog.TargetCol is { } targetCol) {
                    var contentLen = table.LineContentLength((int)lineIdx);
                    pos = lineStart + Math.Clamp(targetCol - 1, 0, contentLen);
                }
                Editor.GoToPosition(pos);
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
        _settings.FindPreserveCase = FindBar.PreserveCase;
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
        FindBar.PreserveCaseBtn.IsChecked = _settings.FindPreserveCase;

        // Persist toggle state changes.
        FindBar.SearchTermChanged += () => {
            SaveFindBarToggles();
            UpdateFindMatchInfo();
        };
        FindBar.MatchCaseBtn.Click += (_, _) => { SaveFindBarToggles(); UpdateFindMatchInfo(); };
        FindBar.WholeWordBtn.Click += (_, _) => { SaveFindBarToggles(); UpdateFindMatchInfo(); };
        FindBar.PreserveCaseBtn.Click += (_, _) => SaveFindBarToggles();

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
            Editor.ReplaceCurrent(replaceTerm, FindBar.MatchCase, FindBar.WholeWord, FindBar.SearchMode, FindBar.PreserveCase);
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
                    FindBar.SearchMode, FindBar.PreserveCase,
                    progress, dialog.CancellationToken);
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
}
