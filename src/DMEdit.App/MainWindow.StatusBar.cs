using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DMEdit.App.Commands;
using Cmd = DMEdit.App.Commands.Commands;
using DMEdit.App.Controls;
using DMEdit.App.Services;
using DMEdit.Core.Documents;

namespace DMEdit.App;

// Status bar + stats bar + tail button + update service partial of
// MainWindow.  UpdateStatusBar renders the Ln/Col / encoding /
// line-ending / indent segments, UpdateStatsBars refreshes the
// dev-mode perf readout, UpdateTailButton manages the tail indicator,
// and OnUpdateAvailable / OnUpdateReady surface the auto-update state.
// Also holds UseNativePicker, the Linux storage-provider probe used
// by the file open/save dialogs.
public partial class MainWindow {


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

        Editor.LineTooLongDetected += ex => {
            // Update tab state to reflect the auto-switch.
            if (_activeTab != null) _activeTab.CharWrapMode = true;
            StatusLeft.Text = $"Switched to character-wrapping mode — " +
                $"file contains a {ex.LineLength:N0}-character line (limit: {ex.MaxLength:N0}). " +
                "Adjust in Settings > Advanced > Char Wrap Line Threshold.";
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
            $"Mem: {s.MemoryMb:F0} MB (max {s.PeakMemoryMb:F0} MB) | " +
            $"GC: {s.Gen0}/{s.Gen1}/{s.Gen2} | " +
            $"Inv/Rnd: {s.LayoutInvalidations}/{s.RenderCalls}";
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
                    if (Editor.CharWrapMode) return;
                    doc.ConvertLineEndings(target);
                    Editor.RaiseMetadataChanged();
                };
                flyout.Items.Add(item);
            }
            flyout.ShowAt(BtnLineEnding);
        };

        // -- Update → restart to apply --
        WireHover(BtnUpdate);
        BtnUpdate.PointerPressed += (_, e) => {
            if (e.GetCurrentPoint(BtnUpdate).Properties.IsLeftButtonPressed) {
                e.Handled = true;
                _updateService.ApplyAndRestart();
            }
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

            long displayCount;
            string countLabel;
            if (Editor.CharWrapMode && Editor.CharsPerRow > 0) {
                displayCount = (long)Math.Ceiling((double)table.Length / Editor.CharsPerRow);
                countLabel = "rows";
            } else {
                displayCount = stillLoading && table.Buffer is { } buf
                    ? buf.LineCount
                    : table.LineCount;
                countLabel = "lines";
            }

            var lcText = displayCount >= 0 ? $"{displayCount:N0}" : "\u2014";
            var lcWidth = lcText.Length;

            var lineCol = "";
            // During loading, line-start lookups can fail (pages not in memory).
            if (!stillLoading) {
                if (Editor.CharWrapMode && Editor.CharsPerRow > 0) {
                    // Character-wrapping mode: show Row / Col.
                    var caret = Math.Min(doc.Selection.Caret, table.Length);
                    var cpr = Editor.CharsPerRow;
                    var row = caret / cpr + 1;
                    var col = caret % cpr + 1;
                    var rowText = $"{row:N0}".PadLeft(lcWidth);
                    var colWidth = $"{cpr:N0}".Length;
                    var colText = $"{col:N0}".PadLeft(colWidth);
                    lineCol = $"Row {rowText} Col {colText}";
                } else {
                    var caret = Math.Min(doc.Selection.Caret, table.Length);
                    var lineIdx = table.LineFromOfs(caret);
                    var lineStart = table.LineStartOfs(lineIdx);
                    var contentLen = table.LineContentLength((int)lineIdx);
                    var col = Math.Min(caret - lineStart, contentLen) + 1;
                    var line = lineIdx + 1;

                    var lnText = $"{line:N0}".PadLeft(lcWidth);
                    var maxLineLen = table.MaxLineLength;
                    var chWidth = maxLineLen > 0 ? $"{maxLineLen:N0}".Length : lcWidth;
                    var chText = $"{col:N0}".PadLeft(chWidth);
                    lineCol = $"Ln {lnText} Ch {chText}";
                }
            }

            SetText(StatusLineCol, lineCol);
            StatusSep1.IsVisible = true;
            SetText(StatusInsMode, Editor.OverwriteMode ? "OVR" : "INS");

            if (stillLoading) {
                StatusSep1b.IsVisible = true;
                SetText(StatusLineCount, $"{lcText} {countLabel}");
                StatusSep2.IsVisible = true;
                SetText(StatusEncoding, "loading\u2026");
                StatusSep3.IsVisible = false;
                SetText(StatusLineEnding, "");
                StatusSep4.IsVisible = false;
                SetText(StatusIndent, "");
            } else if (Editor.CharWrapMode) {
                // Char-wrap mode: show row count and encoding, hide line-ending and indent.
                StatusSep1b.IsVisible = true;
                SetText(StatusLineCount, $"{lcText} {countLabel}");
                StatusSep2.IsVisible = true;
                SetText(StatusEncoding, doc.EncodingInfo.Label);
                StatusSep3.IsVisible = false;
                BtnLineEnding.IsVisible = false;
                StatusSep4.IsVisible = false;
                BtnIndent.IsVisible = false;
            } else {
                StatusSep1b.IsVisible = true;
                SetText(StatusLineCount, $"{lcText} {countLabel}");
                StatusSep2.IsVisible = true;
                SetText(StatusEncoding, doc.EncodingInfo.Label);
                StatusSep3.IsVisible = true;
                BtnLineEnding.IsVisible = true;

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
                BtnIndent.IsVisible = true;
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

    private void OnUpdateAvailable(string version) {
        Dispatcher.UIThread.Post(() => {
            SettingsPanel.SetUpdateAvailable(version, downloaded: false);
        });
    }

    private void OnUpdateReady() {
        Dispatcher.UIThread.Post(() => {
            if (_updateService.AvailableVersion is not { } v) return;
            BtnUpdate.IsVisible = true;
            StatusUpdateText.Text = v;
            SettingsPanel.SetUpdateAvailable(v, downloaded: true);
        });
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
}
