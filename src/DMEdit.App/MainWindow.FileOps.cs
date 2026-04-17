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
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DMEdit.App.Commands;
using Cmd = DMEdit.App.Commands.Commands;
using DMEdit.App.Services;
using DMEdit.Core.Buffers;
using DMEdit.Core.Documents;
using DMEdit.Core.IO;
using DMEdit.Core.Printing;
using DMEdit.App.Settings;

namespace DMEdit.App;

// File operations partial of MainWindow.  Owns New/Open/Save/SaveAs
// menu handlers, SaveAsAsync / SaveToAsync, PrintAsync / SaveAsPdfAsync,
// RevertFileAsync, ReloadFileAsync / ReloadFileInPlaceAsync / the tail
// throttler, SnapshotFileStats, drag-drop file opening, the IPC
// open-file handler, recent-file management, WireStreamingProgress, and
// a couple of small helpers (TryCloseEmptyUntitled, PushRecentFile).
public partial class MainWindow {


    private void OnNew(object? sender, RoutedEventArgs e) {
        var tab = AddTab(TabState.CreateUntitled(_tabs));
        SwitchToTab(tab);
    }

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
            path = await LinuxFileDialog.OpenAsync(FilePicker, "Open Markdown File",
                _settings.LastFileDialogDir, isDark: _theme == EditorTheme.Dark);
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
        Toolbar.Refresh();
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

        var pinnedPaths = _recentFiles.PinnedPaths;
        var unpinnedPaths = _recentFiles.UnpinnedPaths;
        var unpinnedLimit = Math.Max(0, _settings.RecentFileCount - pinnedPaths.Count);
        var unpinnedCount = Math.Min(unpinnedPaths.Count, unpinnedLimit);

        if (pinnedPaths.Count == 0 && unpinnedCount == 0) return;

        MenuFile.Items.Add(new Separator());

        // Pinned items first, with pin icon.
        for (var i = 0; i < pinnedPaths.Count; i++) {
            var captured = pinnedPaths[i];
            var icon = new TextBlock {
                Text = IconGlyphs.Pin,
                FontFamily = IconGlyphs.Family,
                FontSize = 14,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            var label = new TextBlock {
                Text = Path.GetFileName(captured),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            var panel = new StackPanel {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
            };
            panel.Children.Add(icon);
            panel.Children.Add(label);
            var item = new MenuItem { Header = panel };
            Controls.UiHelpers.SetPathToolTip(item, captured);
            item.Click += async (_, _) => {
                MenuBar.Close();
                await OpenFileInTabAsync(captured);
            };
            MenuFile.Items.Add(item);
        }

        // Separator between pinned and unpinned.
        if (pinnedPaths.Count > 0 && unpinnedCount > 0) {
            MenuFile.Items.Add(new Separator());
        }

        // Unpinned items.
        for (var i = 0; i < unpinnedCount; i++) {
            var captured = unpinnedPaths[i];
            var item = new MenuItem { Header = Path.GetFileName(captured) };
            Controls.UiHelpers.SetPathToolTip(item, captured);
            item.Click += async (_, _) => {
                MenuBar.Close();
                await OpenFileInTabAsync(captured);
            };
            MenuFile.Items.Add(item);
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

    private void OnDragOver(object? sender, DragEventArgs e) {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e) {
        if (!e.DataTransfer.Contains(DataFormat.File)) {
            return;
        }
        e.Handled = true;
        var items = e.DataTransfer.TryGetFiles();
        if (items is null) {
            return;
        }
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
    /// Called from the single-instance IPC listener when another DMEdit
    /// process sends a file path (e.g. Explorer context menu with multiple
    /// files selected). Must be called on the UI thread.
    /// </summary>
    internal async void OpenFileFromIpc(string path) {
        await OpenFileInTabAsync(Path.GetFullPath(path));
        Activate();
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
        UpdateJumpList();
    }

    /// <summary>
    /// Prunes recent file entries for local files that no longer exist.
    /// Network paths are left alone (the server may just be offline).
    /// </summary>
    private void PruneRecentFiles() {
        _recentFiles.PruneMissing();
        UpdateJumpList();
    }

    /// <summary>
    /// Rebuilds the Windows taskbar jump list from the current recent files.
    /// No-op on non-Windows or if DMEdit.Windows.dll is not present.
    /// </summary>
    private void UpdateJumpList() {
        var svc = JumpListDiscovery.Service;
        if (svc == null) return;
        var exePath = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (exePath == null) return;
        svc.UpdateRecentFiles(_recentFiles.Paths, exePath);
    }

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
            path = await LinuxFileDialog.SaveAsync(FilePicker, title, suggestedName,
                _settings.LastFileDialogDir, isDark: _theme == EditorTheme.Dark);
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
        Toolbar.Refresh();
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

            // Reload the active tab from the just-saved file so the piece
            // table is compacted to a single piece backed by a fresh
            // PagedFileBuffer.  Without this, pieces still reference the
            // old (now-replaced) memory-mapped buffer and crash on access.
            // The reload is invisible to the user — IsLoading is still
            // true from above.  Only reload the active tab — non-active
            // tabs aren't rendered and will reload when switched to.
            if (tab != null && tab == _activeTab) {
                await ReloadDocumentAfterSaveAsync(tab, path, sha1);
            }

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
            // _activeTab may have been replaced by ReloadDocumentAfterSaveAsync,
            // so unblock unconditionally if we blocked on entry.
            if (tab != null) {
                tab.IsLoading = false;
                _activeTab!.IsLoading = false;
                Editor.IsEditBlocked = _activeTab.IsReadOnly;
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
        if (_printTask is { IsCompleted: false }) {
            StatusLeft.Text = "A print job is still in progress.";
            return;
        }
        if (_activeTab is null or { IsSettings: true }) return;
        if (Editor.CharWrapMode) {
            StatusLeft.Text = "Print is not available in character-wrapping mode.";
            return;
        }
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

        // Apply the hidden diagnostic toggle for GlyphRun printing.  Users
        // with no settings.json entry get the default (true).
        ticket.UseGlyphRun = _settings.UseGlyphRunPrinting;

        // Make the printout match the editor's font and size.  Without this
        // the print path falls back to its hardcoded defaults and the
        // visible point size is wrong (e.g. WPF's emSize is in DIPs, so a
        // hardcoded "11" comes out as 8.25pt).
        ticket.FontFamily = SettingRowFactory.GetEffectiveFontFamily(_settings);
        ticket.FontSizePoints = _settings.EditorFontSize;
        ticket.IndentWidth = _settings.IndentWidth;

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

        var dialog = new ProgressDialog("Printing", "Preparing\u2026", _theme);
        var progress = new Progress<(string Message, double Percent)>(p =>
            dialog.Update(p.Message, p.Percent));

        // Close the dialog when the user clicks Cancel — don't wait for
        // the print thread to finish (WPF spooler cleanup is slow).
        dialog.CancellationToken.Register(() =>
            Dispatcher.UIThread.Post(() => {
                try { dialog.Close(); } catch (InvalidOperationException) { }
            }));

        var dialogTask = dialog.ShowDialog(this);
        _printTask = Task.Run(() => service.Print(doc, ticket, progress,
            dialog.CancellationToken));

        await Task.WhenAny(_printTask, dialogTask);
        var wasCancelled = dialog.WasCancelled;
        try { dialog.Close(); } catch (InvalidOperationException) { }
        await dialogTask;

        if (wasCancelled) {
            StatusLeft.Text = "Printing cancelled";
            return;
        }

        var result = await _printTask;
        if (result.Success) {
            StatusLeft.Text = "Printing complete";
            return;
        }
        if (result.Cancelled) {
            StatusLeft.Text = "Printing cancelled";
            return;
        }

        StatusLeft.Text = "Print failed.";
        const string FriendlyDetail =
            "An error occurred while printing and the job was not sent to the printer.\n\n" +
            "If this keeps happening, check that the printer is connected and powered on, " +
            "and that no jobs are stuck or paused in the Windows print queue.";
        var err = new ErrorDialog(
            title: "Print failed",
            detail: FriendlyDetail,
            buttons: [ErrorDialogButton.OK],
            stackTrace: result.ErrorDetails,
            devMode: _settings.DevMode,
            theme: _theme);
        await err.ShowDialog(this);
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
        if (Editor.CharWrapMode) {
            StatusLeft.Text = "Save as PDF is not available in character-wrapping mode.";
            return;
        }

        var suggestedName = Path.GetFileNameWithoutExtension(
            _activeTab.FilePath ?? "Untitled") + ".pdf";

        string? path;
        if (UseNativePicker) {
            var sp = StorageProvider;
            var result = await sp.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions {
                Title = "Save As PDF",
                SuggestedFileName = suggestedName,
                FileTypeChoices = [
                    new("PDF files") { Patterns = ["*.pdf"] },
                ],
            });
            path = result?.TryGetLocalPath();
        } else {
            path = await LinuxFileDialog.SaveAsync(FilePicker, "Save As PDF", suggestedName,
                _activeTab.FilePath is not null ? Path.GetDirectoryName(_activeTab.FilePath) : null,
                isDark: _theme == EditorTheme.Dark);
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
            newTab.CharWrapMode = ShouldCharWrap(newTab);
            Editor.CharWrapMode = newTab.CharWrapMode;

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
        Toolbar.Refresh();
    }

    /// <summary>
    /// After a successful Save, reload the document from the just-written
    /// file so the piece table is compacted to a single piece backed by a
    /// fresh <see cref="PagedFileBuffer"/>.  Preserves caret, selection,
    /// and scroll state.  Runs within the caller's IsLoading window so the
    /// user sees a single save operation.
    /// </summary>
    private async Task ReloadDocumentAfterSaveAsync(TabState tab, string path, string sha1) {
        LoadResult result;
        try {
            result = await FileLoader.LoadAsync(path);
        } catch {
            return; // reload failed — leave the old document in place
        }
        try {
            if (result.Loaded is not null) {
                await result.Loaded;
            }
        } catch {
            // Scan failed — proceed with whatever is available.
        }

        var idx = _tabs.IndexOf(tab);
        if (idx < 0) {
            return;
        }

        var newTab = new TabState(result.Document, path, result.DisplayName) {
            LoadResult = result,
        };
        newTab.BaseSha1 = sha1;
        newTab.Document.LineEndingInfo = result.Document.LineEndingInfo;
        newTab.Document.IndentInfo = result.Document.IndentInfo;
        newTab.Document.EncodingInfo = result.Document.EncodingInfo;
        newTab.Document.MarkSavePoint();
        if (result.Buffer is PagedFileBuffer paged) {
            var lengths = paged.TakeLineLengths();
            if (lengths is { Count: > 0 }) {
                newTab.Document.Table.InstallLineTree(
                    CollectionsMarshal.AsSpan(lengths));
            }
        }

        // Transfer live editor state.
        var newLen = newTab.Document.Table.Length;
        var isActive = tab == _activeTab;

        if (isActive) {
            Editor.SaveScrollState(tab);
        }

        newTab.ScrollOffsetY = tab.ScrollOffsetY;
        newTab.WinTopLine = tab.WinTopLine;
        newTab.WinScrollOffset = tab.WinScrollOffset;
        newTab.WinRenderOffsetY = tab.WinRenderOffsetY;
        newTab.WinFirstLineHeight = tab.WinFirstLineHeight;

        var oldSel = tab.Document.Selection;
        newTab.Document.Selection = new Selection(
            Math.Min(oldSel.Anchor, newLen),
            Math.Min(oldSel.Active, newLen));

        if (tab.Document.ColumnSel is { } colSel) {
            var maxLine = (int)Math.Max(0, newTab.Document.Table.LineCount - 1);
            newTab.Document.ColumnSel = colSel with {
                AnchorLine = Math.Min(colSel.AnchorLine, maxLine),
                ActiveLine = Math.Min(colSel.ActiveLine, maxLine),
            };
        }

        // Swap — callers handle watcher and file stats.
        _tabs[idx] = newTab;
        if (isActive) {
            newTab.CharWrapMode = ShouldCharWrap(newTab);
            Editor.CharWrapMode = newTab.CharWrapMode;
            _activeTab = newTab;
            Editor.ReplaceDocument(newTab.Document, newTab);
        }
        newTab.Document.Changed += (_, _) => OnTabDocumentChanged(newTab);
        if (_findBarTab == tab) {
            _findBarTab = newTab;
        }
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
}
