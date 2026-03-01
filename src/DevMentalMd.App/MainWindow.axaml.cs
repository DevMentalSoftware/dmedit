using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DevMentalMd.App.Services;
using DevMentalMd.Core.Buffers;
using DevMentalMd.Core.Documents;
using DevMentalMd.Core.IO;

namespace DevMentalMd.App;

public partial class MainWindow : Window {
    private string? _currentPath;
    private LoadResult? _lastLoadResult;
    private readonly RecentFilesStore _recentFiles = RecentFilesStore.Load();
    private readonly AppSettings _settings = AppSettings.Load();
    private int _staticMenuItemCount;

    public MainWindow() {
        InitializeComponent();

        MenuNew.Click += OnNew;
        MenuOpen.Click += OnOpen;
        MenuSave.Click += OnSave;
        MenuSaveAs.Click += OnSaveAs;
        MenuClose.Click += (_, _) => CloseDocument();
        MenuCloseAll.Click += (_, _) => CloseDocument();

        _staticMenuItemCount = MenuFile.Items.Count;

        RebuildRecentMenu();
        WireScrollBar();
        WireDevMenu();
        WireStatsBar();

        var sampleText =
            "# Welcome to DevMentalMD\n" +
            "\n" +
            "This is a plain-text editing surface — the first milestone.\n" +
            "\n" +
            "You can:\n" +
            "- Type and delete text\n" +
            "- Move the caret with arrow keys (Ctrl+Left/Right for word movement)\n" +
            "- Select text with Shift+arrow or click-drag\n" +
            "- Ctrl+A to select all\n" +
            "- Ctrl+Z / Ctrl+Shift+Z to undo and redo\n" +
            "- Home / End to jump to line start/end\n" +
            "- File → Open / Save to work with .md files\n" +
            "\n" +
            "Markdown rendering and formatting come next.\n";

        Editor.Document = new Document(sampleText);
    }

    // -------------------------------------------------------------------------
    // Scroll bar wiring
    // -------------------------------------------------------------------------

    private void WireScrollBar() {
        // Editor → ScrollBar: push scroll state whenever it changes
        Editor.ScrollChanged += (_, _) => SyncScrollBarFromEditor();

        // ScrollBar → Editor: update scroll offset when user drags/clicks scrollbar
        ScrollBar.ScrollRequested += newValue => {
            Editor.ScrollValue = newValue;
        };
    }

    private void SyncScrollBarFromEditor() {
        ScrollBar.Maximum = Editor.ScrollMaximum;
        ScrollBar.Value = Editor.ScrollValue;
        ScrollBar.ViewportSize = Editor.ScrollViewportHeight;
        ScrollBar.ExtentSize = Editor.ScrollExtentHeight;
        ScrollBar.RowHeight = Editor.RowHeightValue;
    }

    // -------------------------------------------------------------------------
    // Dev menu wiring
    // -------------------------------------------------------------------------

    private void WireDevMenu() {
        MenuDev.IsVisible = DevMode.IsEnabled;

        // Initialize slider from settings
        SliderOuterScrollRate.Value = _settings.OuterThumbScrollRateMultiplier;
        ScrollBar.OuterScrollRateMultiplier = _settings.OuterThumbScrollRateMultiplier;
        LabelOuterScrollRate.Text = _settings.OuterThumbScrollRateMultiplier.ToString("F1");

        SliderOuterScrollRate.PropertyChanged += (_, e) => {
            if (e.Property.Name != "Value") {
                return;
            }
            var val = SliderOuterScrollRate.Value;
            ScrollBar.OuterScrollRateMultiplier = val;
            LabelOuterScrollRate.Text = val.ToString("F1");
            _settings.OuterThumbScrollRateMultiplier = val;
            _settings.Save();
        };
    }

    // -------------------------------------------------------------------------
    // Stats bar wiring
    // -------------------------------------------------------------------------

    private int _statsThrottle;

    private void WireStatsBar() {
        StatusBar.IsVisible = DevMode.IsEnabled;
        Editor.StatsUpdated += () => {
            // Throttle: update every 5th frame to avoid layout thrash
            if (++_statsThrottle % 5 != 0) return;
            // Defer to after the render pass — updating Text during Render()
            // would invalidate a visual mid-pass and throw.
            Dispatcher.UIThread.Post(() => {
                var s = Editor.PerfStats;
                var editStat = s.Edit.Count > 0 ? $"  |  Edit: {s.Edit.Format()}" : "";
                StatsBar.Text =
                    $"Layout: {s.Layout.Format()}  |  " +
                    $"Render: {s.Render.Format()}{editStat}  |  " +
                    $"{s.LogicalLines:N0} lines, {s.ViewportLines} in view ({s.ViewportRows} rows)  |  " +
                    $"{s.ScrollPercent:F1}%";
                string load;
                if (s.FirstChunkTimeMs > 0) {
                    load = $"{s.FirstChunkTimeMs:F1}ms + {s.LoadTimeMs:F1}ms";
                } else {
                    load = s.LoadTimeMs > 0 ? $"{s.LoadTimeMs:F1}ms" : "—";
                }
                var save = s.SaveTimeMs > 0 ? $"{s.SaveTimeMs:F1}ms" : "—";
                StatsBarIO.Text =
                    $"Load: {load}  |  Save: {save}  |  " +
                    $"Mem: {s.MemoryMb:F0} MB (max {s.PeakMemoryMb:F0} MB)";
            }, DispatcherPriority.Background);
        };
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

    private void OnNew(object? sender, RoutedEventArgs e) => CloseDocument();

    private void CloseDocument() {
        Editor.Document = new Document();
        _currentPath = null;
        _lastLoadResult = null;
        Title = "DevMentalMD";
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

        var sw = Stopwatch.StartNew();
        var result = await FileLoader.LoadAsync(path, _settings.PagedBufferThresholdBytes);
        _lastLoadResult = result;
        Editor.Document = result.Document;
        WireStreamingProgress(sw);

        _currentPath = path;
        Title = $"DevMentalMD — {result.DisplayName}";

        _recentFiles.Push(path);
        _recentFiles.Save();
        RebuildRecentMenu();
    }

    private async void OnSave(object? sender, RoutedEventArgs e) {
        if (_currentPath is null) {
            await SaveAsAsync();
            return;
        }
        await SaveToAsync(_currentPath);
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
                    await OpenRecentFileAsync(captured);
                };
                MenuFile.Items.Add(item);
            }
        }

        if (DevMode.IsEnabled) {
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

    private async Task OpenRecentFileAsync(string path) {
        if (!File.Exists(path)) {
            _recentFiles.Remove(path);
            _recentFiles.Save();
            RebuildRecentMenu();
            return;
        }

        var sw = Stopwatch.StartNew();
        var result = await FileLoader.LoadAsync(path, _settings.PagedBufferThresholdBytes);
        _lastLoadResult = result;
        Editor.Document = result.Document;
        WireStreamingProgress(sw);

        _currentPath = path;
        Title = $"DevMentalMD — {result.DisplayName}";

        _recentFiles.Push(path);
        _recentFiles.Save();
        RebuildRecentMenu();
    }

    private void OpenDevSample(ProceduralSample sample) {
        var sw = Stopwatch.StartNew();
        Editor.Document = sample.CreateDocument();
        sw.Stop();
        Editor.PerfStats.LoadTimeMs = sw.Elapsed.TotalMilliseconds;

        _currentPath = null;
        _lastLoadResult = null;
        Title = $"DevMentalMD — {sample.DisplayName}";
    }

    // -------------------------------------------------------------------------
    // Save helpers
    // -------------------------------------------------------------------------

    private async Task SaveAsAsync() {
        // For zip files, suggest the inner entry name (e.g., "model.xml" not "model.zip").
        string suggestedName;
        if (_lastLoadResult is { WasZipped: true, InnerEntryName: { } innerName }) {
            suggestedName = innerName;
        } else if (_currentPath is not null) {
            suggestedName = Path.GetFileName(_currentPath);
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
        _currentPath = path;
        Title = $"DevMentalMD — {Path.GetFileName(path)}";
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
    /// If the current document is backed by a <see cref="StreamingFileBuffer"/>,
    /// subscribes to its progress events so the editor re-layouts incrementally
    /// and the stats bar shows the running load time. For small (non-streaming)
    /// files the stopwatch is stopped immediately.
    /// </summary>
    private void WireStreamingProgress(Stopwatch sw) {
        if (Editor.Document?.Table.OrigBuffer is StreamingFileBuffer streamBuf) {
            // Streaming load — capture time to first renderable chunk, then keep
            // the stopwatch running until fully loaded.
            var firstChunkCaptured = false;
            Editor.PerfStats.FirstChunkTimeMs = 0;
            Editor.PerfStats.LoadTimeMs = sw.Elapsed.TotalMilliseconds;

            streamBuf.ProgressChanged += () => {
                Dispatcher.UIThread.Post(() => {
                    if (!firstChunkCaptured) {
                        firstChunkCaptured = true;
                        Editor.PerfStats.FirstChunkTimeMs = sw.Elapsed.TotalMilliseconds;
                    }
                    Editor.PerfStats.LoadTimeMs = sw.Elapsed.TotalMilliseconds;
                    Editor.InvalidateLayout();
                }, DispatcherPriority.Background);
            };

            streamBuf.LoadComplete += () => {
                Dispatcher.UIThread.Post(() => {
                    sw.Stop();
                    Editor.PerfStats.LoadTimeMs = sw.Elapsed.TotalMilliseconds;
                    Editor.InvalidateLayout();
                }, DispatcherPriority.Background);
            };
        } else if (Editor.Document?.Table.OrigBuffer is PagedFileBuffer pagedBuf) {
            // Paged load — background scan builds page table + sampled line index.
            var firstChunkCaptured = false;
            Editor.PerfStats.FirstChunkTimeMs = 0;
            Editor.PerfStats.LoadTimeMs = sw.Elapsed.TotalMilliseconds;

            pagedBuf.ProgressChanged += () => {
                Dispatcher.UIThread.Post(() => {
                    if (!firstChunkCaptured) {
                        firstChunkCaptured = true;
                        Editor.PerfStats.FirstChunkTimeMs = sw.Elapsed.TotalMilliseconds;
                    }
                    Editor.PerfStats.LoadTimeMs = sw.Elapsed.TotalMilliseconds;
                    Editor.InvalidateLayout();
                }, DispatcherPriority.Background);
            };

            pagedBuf.LoadComplete += () => {
                Dispatcher.UIThread.Post(() => {
                    sw.Stop();
                    Editor.PerfStats.LoadTimeMs = sw.Elapsed.TotalMilliseconds;
                    Editor.InvalidateLayout();
                }, DispatcherPriority.Background);
            };
        } else {
            // Small file — loaded synchronously, stop the clock.
            // No streaming, so first-chunk time is the same as total load time.
            sw.Stop();
            Editor.PerfStats.FirstChunkTimeMs = 0;
            Editor.PerfStats.LoadTimeMs = sw.Elapsed.TotalMilliseconds;
        }
    }
}
